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
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ArchiSteamFarm.Steam.Cards;

public sealed class Game : IEquatable<Game> {
	[JsonInclude]
	public uint AppID { get; }

	[JsonInclude]
	public string GameName { get; }

	internal readonly byte BadgeLevel;

	[JsonInclude]
	[JsonRequired]
	[Required]
	public ushort CardsRemaining { get; internal set; }

	[JsonInclude]
	[JsonRequired]
	[Required]
	public float HoursPlayed { get; internal set; }

	internal uint PlayableAppID { get; set; }

	internal Game(uint appID, string gameName, float hoursPlayed, ushort cardsRemaining, byte badgeLevel) {
		ArgumentOutOfRangeException.ThrowIfZero(appID);
		ArgumentException.ThrowIfNullOrEmpty(gameName);
		ArgumentOutOfRangeException.ThrowIfNegative(hoursPlayed);

		AppID = appID;
		GameName = gameName;
		HoursPlayed = hoursPlayed;
		CardsRemaining = cardsRemaining;
		BadgeLevel = badgeLevel;

		PlayableAppID = appID;
	}

	public bool Equals(Game? other) => (other != null) && (ReferenceEquals(other, this) || ((AppID == other.AppID) && (BadgeLevel == other.BadgeLevel) && (GameName == other.GameName)));
	public override bool Equals(object? obj) => (obj != null) && ((obj == this) || (obj is Game game && Equals(game)));
	public override int GetHashCode() => HashCode.Combine(AppID, BadgeLevel, GameName);
}
