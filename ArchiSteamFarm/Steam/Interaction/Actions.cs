// ----------------------------------------------------------------------------------------------
//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// ----------------------------------------------------------------------------------------------
// |
// Copyright 2015-2025 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam.Data;
using ArchiSteamFarm.Steam.Exchange;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Steam.Storage;
using ArchiSteamFarm.Storage;
using ArchiSteamFarm.Web;
using JetBrains.Annotations;
using SteamKit2;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;

namespace ArchiSteamFarm.Steam.Interaction;

public sealed class Actions : IAsyncDisposable, IDisposable {
	private static readonly SemaphoreSlim GiftCardsSemaphore = new(1, 1);

	private readonly Bot Bot;
	private readonly ConcurrentHashSet<ulong> HandledGifts = [];
	private readonly SemaphoreSlim TradingSemaphore = new(1, 1);

	private Timer? CardsFarmerResumeTimer;
	private bool ProcessingGiftsScheduled;
	private bool TradingScheduled;

	internal Actions(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Bot = bot;
	}

	public void Dispose() {
		// Those are objects that are always being created if constructor doesn't throw exception
		TradingSemaphore.Dispose();

		// Those are objects that might be null and the check should be in-place
		CardsFarmerResumeTimer?.Dispose();
	}

	public async ValueTask DisposeAsync() {
		// Those are objects that are always being created if constructor doesn't throw exception
		TradingSemaphore.Dispose();

		// Those are objects that might be null and the check should be in-place
		if (CardsFarmerResumeTimer != null) {
			await CardsFarmerResumeTimer.DisposeAsync().ConfigureAwait(false);
		}
	}

	[PublicAPI]
	public async Task<(EResult Result, IReadOnlyCollection<uint>? GrantedApps, IReadOnlyCollection<uint>? GrantedPackages)> AddFreeLicenseApp(uint appID) {
		ArgumentOutOfRangeException.ThrowIfZero(appID);

		SteamApps.FreeLicenseCallback callback;

		try {
			callback = await Bot.SteamApps.RequestFreeLicense(appID).ToLongRunningTask().ConfigureAwait(false);
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericWarningException(e);

			return (EResult.Timeout, null, null);
		}

		return (callback.Result, callback.GrantedApps, callback.GrantedPackages);
	}

	[PublicAPI]
	public async Task<(EResult Result, EPurchaseResultDetail PurchaseResultDetail)> AddFreeLicensePackage(uint subID) {
		ArgumentOutOfRangeException.ThrowIfZero(subID);

		return await Bot.ArchiWebHandler.AddFreeLicense(subID).ConfigureAwait(false);
	}

	[PublicAPI]
	public static string? Encrypt(ArchiCryptoHelper.ECryptoMethod cryptoMethod, string stringToEncrypt) {
		if (!Enum.IsDefined(cryptoMethod)) {
			throw new InvalidEnumArgumentException(nameof(cryptoMethod), (int) cryptoMethod, typeof(ArchiCryptoHelper.ECryptoMethod));
		}

		ArgumentException.ThrowIfNullOrEmpty(stringToEncrypt);

		return ArchiCryptoHelper.Encrypt(cryptoMethod, stringToEncrypt);
	}

	[PublicAPI]
	public static (bool Success, string Message) Exit() {
		// Schedule the task after some time so user can receive response
		Utilities.InBackground(static async () => {
				await Task.Delay(1000).ConfigureAwait(false);
				await Program.Exit().ConfigureAwait(false);
			}
		);

		return (true, Strings.Done);
	}

	[PublicAPI]
	public async Task<(bool Success, string? Token, string Message)> GenerateTwoFactorAuthenticationToken() {
		if (Bot.BotDatabase.MobileAuthenticator == null) {
			return (false, null, Strings.BotNoASFAuthenticator);
		}

		string? token = await Bot.BotDatabase.MobileAuthenticator.GenerateToken().ConfigureAwait(false);

		bool success = !string.IsNullOrEmpty(token);

		return (success, token, success ? Strings.Success : Strings.WarningFailed);
	}

	[PublicAPI]
	public async Task<(bool Success, IReadOnlyCollection<Confirmation>? Confirmations, string Message)> GetConfirmations() {
		if (Bot.BotDatabase.MobileAuthenticator == null) {
			return (false, null, Strings.BotNoASFAuthenticator);
		}

		if (!Bot.IsConnectedAndLoggedOn) {
			return (false, null, Strings.BotNotConnected);
		}

		ImmutableHashSet<Confirmation>? confirmations = await Bot.BotDatabase.MobileAuthenticator.GetConfirmations().ConfigureAwait(false);

		bool success = confirmations != null;

		return (success, confirmations, success ? Strings.Success : Strings.WarningFailed);
	}

	[PublicAPI]
	public ulong GetFirstSteamMasterID() {
		ulong steamMasterID = Bot.BotConfig.SteamUserPermissions.Where(kv => (kv.Key > 0) && (kv.Key != Bot.SteamID) && new SteamID(kv.Key).IsIndividualAccount && (kv.Value == BotConfig.EAccess.Master)).Select(static kv => kv.Key).OrderBy(static steamID => steamID).FirstOrDefault();

		if (steamMasterID > 0) {
			return steamMasterID;
		}

		ulong steamOwnerID = ASF.GlobalConfig?.SteamOwnerID ?? GlobalConfig.DefaultSteamOwnerID;

		return (steamOwnerID > 0) && new SteamID(steamOwnerID).IsIndividualAccount ? steamOwnerID : 0;
	}

	/// <remarks>This action should be used if you require full inventory exclusively, otherwise consider calling <see cref="ArchiHandler.GetMyInventoryAsync" /> instead.</remarks>
	[PublicAPI]
	public async Task<(HashSet<Asset>? Result, string Message)> GetInventory(uint appID = Asset.SteamAppID, ulong contextID = Asset.SteamCommunityContextID, Func<Asset, bool>? filterFunction = null, string? language = null) {
		ArgumentOutOfRangeException.ThrowIfZero(appID);
		ArgumentOutOfRangeException.ThrowIfZero(contextID);

		if (!Bot.IsConnectedAndLoggedOn) {
			return (null, Strings.BotNotConnected);
		}

		filterFunction ??= static _ => true;

		using (await GetTradingLock().ConfigureAwait(false)) {
			try {
				return (await Bot.ArchiHandler.GetMyInventoryAsync(appID, contextID, language: language).Where(item => filterFunction(item)).ToHashSetAsync().ConfigureAwait(false), Strings.Success);
			} catch (TimeoutException e) {
				Bot.ArchiLogger.LogGenericWarningException(e);

				return (null, Strings.FormatWarningFailedWithError(e.Message));
			} catch (Exception e) {
				Bot.ArchiLogger.LogGenericException(e);

				return (null, Strings.FormatWarningFailedWithError(e.Message));
			}
		}
	}

	[PublicAPI]
	public async Task<Dictionary<uint, LoyaltyRewardDefinition>?> GetRewardItems(IReadOnlyCollection<uint> definitionIDs) {
		if ((definitionIDs == null) || (definitionIDs.Count == 0)) {
			throw new ArgumentNullException(nameof(definitionIDs));
		}

		return await Bot.ArchiHandler.GetRewardItems(definitionIDs).ConfigureAwait(false);
	}

	[MustDisposeResource]
	[PublicAPI]
	public async Task<IDisposable> GetTradingLock() {
		await TradingSemaphore.WaitAsync().ConfigureAwait(false);

		return new SemaphoreLock(TradingSemaphore);
	}

	[PublicAPI]
	public async Task<(bool Success, IReadOnlyCollection<Confirmation>? HandledConfirmations, string Message)> HandleTwoFactorAuthenticationConfirmations(bool accept, Confirmation.EConfirmationType? acceptedType = null, IReadOnlyCollection<ulong>? acceptedCreatorIDs = null, bool waitIfNeeded = false) {
		if (Bot.BotDatabase.MobileAuthenticator == null) {
			return (false, null, Strings.BotNoASFAuthenticator);
		}

		if (!Bot.IsConnectedAndLoggedOn) {
			return (false, null, Strings.BotNotConnected);
		}

		Dictionary<ulong, Confirmation>? handledConfirmations = null;

		for (byte i = 0; (i == 0) || ((i < WebBrowser.MaxTries) && waitIfNeeded); i++) {
			if (i > 0) {
				await Task.Delay(1000).ConfigureAwait(false);
			}

			ImmutableHashSet<Confirmation>? confirmations = await Bot.BotDatabase.MobileAuthenticator.GetConfirmations().ConfigureAwait(false);

			if ((confirmations == null) || (confirmations.Count == 0)) {
				continue;
			}

			HashSet<Confirmation> remainingConfirmations = confirmations.ToHashSet();

			if (acceptedType.HasValue) {
				if (remainingConfirmations.RemoveWhere(confirmation => confirmation.ConfirmationType != acceptedType.Value) > 0) {
					if (remainingConfirmations.Count == 0) {
						continue;
					}
				}
			}

			if (acceptedCreatorIDs?.Count > 0) {
				if (remainingConfirmations.RemoveWhere(confirmation => !acceptedCreatorIDs.Contains(confirmation.CreatorID)) > 0) {
					if (remainingConfirmations.Count == 0) {
						continue;
					}
				}
			}

			if (!await Bot.BotDatabase.MobileAuthenticator.HandleConfirmations(remainingConfirmations, accept).ConfigureAwait(false)) {
				return (false, handledConfirmations?.Values, Strings.WarningFailed);
			}

			handledConfirmations ??= new Dictionary<ulong, Confirmation>();

			foreach (Confirmation confirmation in remainingConfirmations) {
				handledConfirmations[confirmation.CreatorID] = confirmation;
			}

			// We've accepted *something*, if caller didn't specify the IDs, that's enough for us
			if ((acceptedCreatorIDs == null) || (acceptedCreatorIDs.Count == 0)) {
				return (true, handledConfirmations.Values, Strings.FormatBotHandledConfirmations(handledConfirmations.Count));
			}

			// If they did, check if we've already found everything we were supposed to
			if ((handledConfirmations.Count >= acceptedCreatorIDs.Count) && acceptedCreatorIDs.All(handledConfirmations.ContainsKey)) {
				return (true, handledConfirmations.Values, Strings.FormatBotHandledConfirmations(handledConfirmations.Count));
			}
		}

		// If we've reached this point, then it's a failure for waitIfNeeded, and success otherwise
		return (!waitIfNeeded, handledConfirmations?.Values, !waitIfNeeded ? Strings.FormatBotHandledConfirmations(handledConfirmations?.Count ?? 0) : Strings.FormatErrorRequestFailedTooManyTimes(WebBrowser.MaxTries));
	}

	[PublicAPI]
	public static string Hash(ArchiCryptoHelper.EHashingMethod hashingMethod, string stringToHash) {
		if (!Enum.IsDefined(hashingMethod)) {
			throw new InvalidEnumArgumentException(nameof(hashingMethod), (int) hashingMethod, typeof(ArchiCryptoHelper.EHashingMethod));
		}

		ArgumentException.ThrowIfNullOrEmpty(stringToHash);

		return ArchiCryptoHelper.Hash(hashingMethod, stringToHash);
	}

	[PublicAPI]
	public async Task<(bool Success, string Message)> Pause(bool permanent, ushort resumeInSeconds = 0) {
		if (Bot.CardsFarmer.Paused) {
			return (false, Strings.BotAutomaticIdlingPausedAlready);
		}

		await Bot.CardsFarmer.Pause(permanent).ConfigureAwait(false);

		if (!permanent && (Bot.BotConfig.GamesPlayedWhileIdle.Count > 0)) {
			// We want to let family sharing users access our library, and in this case we must also stop GamesPlayedWhileIdle
			// We add extra delay because OnFarmingStopped() also executes PlayGames()
			// Despite of proper order on our end, Steam network might not respect it
			await Task.Delay(Bot.CallbackSleep).ConfigureAwait(false);

			await Bot.ArchiHandler.PlayGames([], Bot.BotConfig.CustomGamePlayedWhileIdle).ConfigureAwait(false);
		}

		if (resumeInSeconds > 0) {
			if (CardsFarmerResumeTimer == null) {
				CardsFarmerResumeTimer = new Timer(
					_ => Resume(),
					null,
					TimeSpan.FromSeconds(resumeInSeconds), // Delay
					Timeout.InfiniteTimeSpan // Period
				);
			} else {
				CardsFarmerResumeTimer.Change(TimeSpan.FromSeconds(resumeInSeconds), Timeout.InfiniteTimeSpan);
			}
		}

		return (true, Strings.BotAutomaticIdlingNowPaused);
	}

	[PublicAPI]
	public async Task<(bool Success, string Message)> Play(IReadOnlyCollection<uint> gameIDs, string? gameName = null) {
		ArgumentNullException.ThrowIfNull(gameIDs);

		if (!Bot.IsConnectedAndLoggedOn) {
			return (false, Strings.BotNotConnected);
		}

		if (!Bot.CardsFarmer.Paused) {
			await Bot.CardsFarmer.Pause(true).ConfigureAwait(false);
		}

		await Bot.ArchiHandler.PlayGames(gameIDs, gameName).ConfigureAwait(false);

		return (true, gameIDs.Count > 0 ? Strings.FormatBotIdlingSelectedGames(nameof(gameIDs), string.Join(", ", gameIDs)) : Strings.Done);
	}

	[PublicAPI]
	public async Task<CStore_RegisterCDKey_Response?> RedeemKey(string key) {
		await LimitGiftsRequestsAsync().ConfigureAwait(false);

		return await Bot.ArchiHandler.RedeemKey(key).ConfigureAwait(false);
	}

	[PublicAPI]
	public async Task<EResult> RedeemPoints(uint definitionID, bool forced = false) {
		ArgumentOutOfRangeException.ThrowIfZero(definitionID);

		if (!forced) {
			Dictionary<uint, LoyaltyRewardDefinition>? definitions = await Bot.Actions.GetRewardItems(new HashSet<uint>(1) { definitionID }).ConfigureAwait(false);

			if (definitions == null) {
				return EResult.Timeout;
			}

			if (!definitions.TryGetValue(definitionID, out LoyaltyRewardDefinition? definition)) {
				return EResult.InvalidParam;
			}

			if (definition.point_cost > 0) {
				return EResult.InvalidState;
			}
		}

		return await Bot.ArchiHandler.RedeemPoints(definitionID).ConfigureAwait(false);
	}

	[PublicAPI]
	public async Task<EResult> RemoveLicenseApp(uint appID) {
		ArgumentOutOfRangeException.ThrowIfZero(appID);

		return await Bot.ArchiHandler.RemoveLicenseForApp(appID).ConfigureAwait(false);
	}

	[PublicAPI]
	public async Task<EResult> RemoveLicensePackage(uint subID) {
		ArgumentOutOfRangeException.ThrowIfZero(subID);

		return await Bot.ArchiWebHandler.RemoveLicense(subID).ConfigureAwait(false);
	}

	[PublicAPI]
	public static (bool Success, string Message) Restart() {
		if (!Program.RestartAllowed) {
			return (false, $"!{nameof(Program.RestartAllowed)}");
		}

		// Schedule the task after some time so user can receive response
		Utilities.InBackground(static async () => {
				await Task.Delay(1000).ConfigureAwait(false);
				await Program.Restart().ConfigureAwait(false);
			}
		);

		return (true, Strings.Done);
	}

	[PublicAPI]
	public (bool Success, string Message) Resume() {
		if (!Bot.CardsFarmer.Paused) {
			return (false, Strings.BotAutomaticIdlingResumedAlready);
		}

		Utilities.InBackground(() => Bot.CardsFarmer.Resume(true));

		return (true, Strings.BotAutomaticIdlingNowResumed);
	}

	[PublicAPI]
	public async Task<(bool Success, string Message)> SendInventory(IReadOnlyCollection<Asset> items, ulong targetSteamID = 0, string? tradeToken = null, string? customMessage = null, ushort itemsPerTrade = Trading.MaxItemsPerTrade) {
		if ((items == null) || (items.Count == 0)) {
			throw new ArgumentNullException(nameof(items));
		}

		ArgumentOutOfRangeException.ThrowIfZero(itemsPerTrade);

		if (!Bot.IsConnectedAndLoggedOn) {
			return (false, Strings.BotNotConnected);
		}

		if (targetSteamID == 0) {
			targetSteamID = GetFirstSteamMasterID();

			if (targetSteamID == 0) {
				return (false, Strings.BotLootingMasterNotDefined);
			}

			if (string.IsNullOrEmpty(tradeToken) && !string.IsNullOrEmpty(Bot.BotConfig.SteamTradeToken)) {
				tradeToken = Bot.BotConfig.SteamTradeToken;
			}
		} else if (!new SteamID(targetSteamID).IsIndividualAccount) {
			throw new ArgumentOutOfRangeException(nameof(targetSteamID));
		}

		if (targetSteamID == Bot.SteamID) {
			return (false, Strings.BotSendingTradeToYourself);
		}

		if (string.IsNullOrEmpty(tradeToken) && (Bot.SteamFriends.GetFriendRelationship(targetSteamID) != EFriendRelationship.Friend)) {
			Bot? targetBot = Bot.Bots?.Values.FirstOrDefault(bot => bot.SteamID == targetSteamID);

			if (targetBot?.IsConnectedAndLoggedOn == true) {
				tradeToken = await targetBot.ArchiHandler.GetTradeToken().ConfigureAwait(false);
			}
		}

		// Marking sent trades is crucial in regards to refreshing current state on Steam side
		// Steam might not always realize e.g. "items no longer available" trades without it, and prevent us from sending further ones
		// A simple visit to sent trade offers page will suffice
		if (!await Bot.ArchiWebHandler.MarkSentTrades().ConfigureAwait(false)) {
			return (false, Strings.BotLootingFailed);
		}

		// In similar way we might need to accept popup on Steam side, we limit it only to cases that we're aware of, as sending this request otherwise is additional overhead for no reason
		if (!Bot.BotDatabase.TradeRestrictionsAcknowledged && items.Any(static item => item.AppID != Asset.SteamAppID)) {
			// We should normally fail the process in case of a failure here, but since the popup could be marked already in the past, we'll allow it in hope it wasn't needed after all
			await Bot.Trading.AcknowledgeTradeRestrictions().ConfigureAwait(false);
		}

		(bool success, _, HashSet<ulong>? mobileTradeOfferIDs) = await Bot.ArchiWebHandler.SendTradeOffer(targetSteamID, items, token: tradeToken, customMessage: customMessage, itemsPerTrade: itemsPerTrade).ConfigureAwait(false);

		if ((mobileTradeOfferIDs?.Count > 0) && Bot.HasMobileAuthenticator) {
			(bool twoFactorSuccess, _, _) = await HandleTwoFactorAuthenticationConfirmations(true, Confirmation.EConfirmationType.Trade, mobileTradeOfferIDs, true).ConfigureAwait(false);

			if (!twoFactorSuccess) {
				return (false, Strings.BotLootingFailed);
			}
		}

		return success ? (true, Strings.BotLootingSuccess) : (false, Strings.BotLootingFailed);
	}

	[PublicAPI]
	public async Task<(bool Success, string Message)> SendInventory(uint appID = Asset.SteamAppID, ulong contextID = Asset.SteamCommunityContextID, ulong targetSteamID = 0, string? tradeToken = null, string? customMessage = null, Func<Asset, bool>? filterFunction = null, ushort itemsPerTrade = Trading.MaxItemsPerTrade) {
		ArgumentOutOfRangeException.ThrowIfZero(appID);
		ArgumentOutOfRangeException.ThrowIfZero(contextID);

		if (!Bot.IsConnectedAndLoggedOn) {
			return (false, Strings.BotNotConnected);
		}

		filterFunction ??= static _ => true;

		HashSet<Asset> inventory;

		// ReSharper disable once SuspiciousLockOverSynchronizationPrimitive - this is not a mistake, we need extra synchronization, and we can re-use the semaphore object for that
		lock (TradingSemaphore) {
			if (TradingScheduled) {
				return (false, Strings.ErrorAborted);
			}

			TradingScheduled = true;
		}

		using (await GetTradingLock().ConfigureAwait(false)) {
			// ReSharper disable once SuspiciousLockOverSynchronizationPrimitive - this is not a mistake, we need extra synchronization, and we can re-use the semaphore object for that
			lock (TradingSemaphore) {
				TradingScheduled = false;
			}

			try {
				inventory = await Bot.ArchiHandler.GetMyInventoryAsync(appID, contextID, true).Where(item => filterFunction(item)).ToHashSetAsync().ConfigureAwait(false);
			} catch (TimeoutException e) {
				Bot.ArchiLogger.LogGenericWarningException(e);

				return (false, Strings.FormatWarningFailedWithError(e.Message));
			} catch (Exception e) {
				Bot.ArchiLogger.LogGenericException(e);

				return (false, Strings.FormatWarningFailedWithError(e.Message));
			}
		}

		if (inventory.Count == 0) {
			return (false, Strings.FormatErrorIsEmpty(nameof(inventory)));
		}

		return await SendInventory(inventory, targetSteamID, tradeToken, customMessage, itemsPerTrade).ConfigureAwait(false);
	}

	[PublicAPI]
	public (bool Success, string Message) Start() {
		if (Bot.KeepRunning) {
			return (false, Strings.BotAlreadyRunning);
		}

		Utilities.InBackground(Bot.Start);

		return (true, Strings.Done);
	}

	[PublicAPI]
	public async Task<(bool Success, string Message)> Stop() {
		if (!Bot.KeepRunning) {
			return (false, Strings.BotAlreadyStopped);
		}

		await Bot.Stop().ConfigureAwait(false);

		return (true, Strings.Done);
	}

	[PublicAPI]
	public async Task<bool> UnpackBoosterPacks() {
		if (!Bot.IsConnectedAndLoggedOn) {
			return false;
		}

		// It'd make sense here to actually check return code of ArchiWebHandler.UnpackBooster(), but it lies most of the time | https://github.com/JustArchi/ArchiSteamFarm/issues/704
		bool result = true;

		// It'd also make sense to run all of this in parallel, but it seems that Steam has a lot of problems with inventory-related parallel requests | https://steamcommunity.com/groups/archiasf/discussions/1/3559414588264550284/
		try {
			await foreach (Asset item in Bot.ArchiHandler.GetMyInventoryAsync().Where(static item => item.Type == EAssetType.BoosterPack).ConfigureAwait(false)) {
				if (!await Bot.ArchiWebHandler.UnpackBooster(item.RealAppID, item.AssetID).ConfigureAwait(false)) {
					result = false;
				}
			}
		} catch (TimeoutException e) {
			Bot.ArchiLogger.LogGenericWarningException(e);

			return false;
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericException(e);

			return false;
		}

		return result;
	}

	[PublicAPI]
	public static async Task<(bool Success, string? Message, Version? Version)> Update(GlobalConfig.EUpdateChannel? channel = null, bool forced = false) {
		if (channel.HasValue && !Enum.IsDefined(channel.Value)) {
			throw new InvalidEnumArgumentException(nameof(channel), (int) channel, typeof(GlobalConfig.EUpdateChannel));
		}

		(bool updated, Version? newVersion) = await ASF.Update(channel, true, forced).ConfigureAwait(false);

		if (updated) {
			Utilities.InBackground(ASF.RestartOrExit);
		}

		return updated ? (true, null, newVersion) : SharedInfo.Version >= newVersion ? (false, $"V{SharedInfo.Version} ≥ V{newVersion}", newVersion) : (false, null, newVersion);
	}

	[PublicAPI]
	public static async Task<(bool Success, string? Message)> UpdatePlugins(GlobalConfig.EUpdateChannel? channel = null, IReadOnlyCollection<string>? plugins = null, bool forced = false) {
		if (channel.HasValue && !Enum.IsDefined(channel.Value)) {
			throw new InvalidEnumArgumentException(nameof(channel), (int) channel, typeof(GlobalConfig.EUpdateChannel));
		}

		bool updated;

		if (plugins is { Count: > 0 }) {
			HashSet<string> pluginAssemblyNames = plugins.ToHashSet(StringComparer.OrdinalIgnoreCase);

			HashSet<IPluginUpdates> pluginsForUpdate = PluginsCore.GetPluginsForUpdate(pluginAssemblyNames);

			if (pluginsForUpdate.Count == 0) {
				return (false, Strings.NothingFound);
			}

			updated = await PluginsCore.UpdatePlugins(SharedInfo.Version, false, pluginsForUpdate, channel, true, forced).ConfigureAwait(false);
		} else {
			updated = await PluginsCore.UpdatePlugins(SharedInfo.Version, false, channel, true, forced).ConfigureAwait(false);
		}

		if (updated) {
			Utilities.InBackground(ASF.RestartOrExit);
		}

		string message = updated ? Strings.UpdateFinished : Strings.NothingFound;

		return (true, message);
	}

	internal async Task AcceptDigitalGiftCards() {
		if (!Bot.IsConnectedAndLoggedOn) {
			return;
		}

		// ReSharper disable once SuspiciousLockOverSynchronizationPrimitive - this is not a mistake, we need extra synchronization, and we can re-use the semaphore object for that
		lock (GiftCardsSemaphore) {
			if (ProcessingGiftsScheduled) {
				return;
			}

			ProcessingGiftsScheduled = true;
		}

		await GiftCardsSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			// ReSharper disable once SuspiciousLockOverSynchronizationPrimitive - this is not a mistake, we need extra synchronization, and we can re-use the semaphore object for that
			lock (GiftCardsSemaphore) {
				ProcessingGiftsScheduled = false;
			}

			if (!Bot.IsConnectedAndLoggedOn) {
				return;
			}

			HashSet<ulong>? giftCardIDs = await Bot.ArchiWebHandler.GetDigitalGiftCards().ConfigureAwait(false);

			if ((giftCardIDs == null) || (giftCardIDs.Count == 0)) {
				return;
			}

			foreach (ulong giftCardID in giftCardIDs.Where(gid => !HandledGifts.Contains(gid))) {
				HandledGifts.Add(giftCardID);

				Bot.ArchiLogger.LogGenericInfo(Strings.FormatBotAcceptingGift(giftCardID));
				await LimitGiftsRequestsAsync().ConfigureAwait(false);

				bool result = await Bot.ArchiWebHandler.AcceptDigitalGiftCard(giftCardID).ConfigureAwait(false);

				if (result) {
					Bot.ArchiLogger.LogGenericInfo(Strings.Success);
				} else {
					Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
				}
			}
		} finally {
			GiftCardsSemaphore.Release();
		}
	}

	internal async Task AcceptGuestPasses(IReadOnlyCollection<ulong> guestPassIDs) {
		if ((guestPassIDs == null) || (guestPassIDs.Count == 0)) {
			throw new ArgumentNullException(nameof(guestPassIDs));
		}

		if (!Bot.IsConnectedAndLoggedOn) {
			return;
		}

		foreach (ulong guestPassID in guestPassIDs.Where(guestPassID => !HandledGifts.Contains(guestPassID))) {
			HandledGifts.Add(guestPassID);

			Bot.ArchiLogger.LogGenericInfo(Strings.FormatBotAcceptingGift(guestPassID));
			await LimitGiftsRequestsAsync().ConfigureAwait(false);

			SteamApps.RedeemGuestPassResponseCallback? response = await Bot.ArchiHandler.RedeemGuestPass(guestPassID).ConfigureAwait(false);

			if (response != null) {
				if (response.Result == EResult.OK) {
					Bot.ArchiLogger.LogGenericInfo(Strings.Success);
				} else {
					Bot.ArchiLogger.LogGenericWarning(Strings.FormatWarningFailedWithError(response.Result));
				}
			} else {
				Bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);
			}
		}
	}

	internal void OnDisconnected() => HandledGifts.Clear();

	private static async Task LimitGiftsRequestsAsync() {
		if (ASF.GiftsSemaphore == null) {
			throw new InvalidOperationException(nameof(ASF.GiftsSemaphore));
		}

		byte giftsLimiterDelay = ASF.GlobalConfig?.GiftsLimiterDelay ?? GlobalConfig.DefaultGiftsLimiterDelay;

		if (giftsLimiterDelay == 0) {
			return;
		}

		await ASF.GiftsSemaphore.WaitAsync().ConfigureAwait(false);

		Utilities.InBackground(async () => {
				await Task.Delay(giftsLimiterDelay * 1000).ConfigureAwait(false);
				ASF.GiftsSemaphore.Release();
			}
		);
	}
}
