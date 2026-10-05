/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * SECURITY SETTINGS
 * ========================================================================
 *
 * PURPOSE:
 * The login, attach and connection limits, read once from Imlight.ini with
 * defaults for keys an older ini does not have (ConfigValue.AsInt(default)
 * returns 0, not the default, for a missing key).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

#nullable enable

using System;
using System.Globalization;
using Imlight.Classic.Net;
using Imlight.Common;

namespace Imlight.CoreLib.Auth;

internal static class SecuritySettings {

    internal static int Int(string key, int fallback) {
        string? text;
        try {
            text = ConfigurationManager.GetSetting(key);
        }
        catch (Exception) {
            return fallback; // not initialised (tests)
        }

        return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    internal static bool Bool(string key, bool fallback) {
        string? text;
        try {
            text = ConfigurationManager.GetSetting(key);
        }
        catch (Exception) {
            return fallback;
        }

        return text?.Trim().ToLowerInvariant() switch {
            "true" or "yes" or "1" or "on" or "enabled" => true,
            "false" or "no" or "0" or "off" or "disabled" => false,
            _ => fallback,
        };
    }

    internal static string? Text(string key) {
        try {
            return ConfigurationManager.GetSetting(key);
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// The process's game-server attach keys. [Game Server] SessionKeyValidityTime (seconds, clamped to 30..900,
    /// default 300) and SessionKeyBindIp (default true).
    /// </summary>
    internal static readonly Lazy<GameSessionKeys> GameKeys = new(() => new GameSessionKeys(
        TimeSpan.FromSeconds(Int("Game Server.SessionKeyValidityTime", 300)),
        Bool("Game Server.SessionKeyBindIp", true)));

    /// <summary>Failed logins: per account and per address, with doubling lockouts.</summary>
    internal static readonly Lazy<LoginThrottle> Logins = new(() => new LoginThrottle(new LoginThrottleOptions {
        AccountFailures = Int("Login Server.AccountFailuresBeforeLockout", 5),
        AddressFailures = Int("Login Server.AddressFailuresBeforeLockout", 20),
        Window = TimeSpan.FromMinutes(Int("Login Server.FailureWindowMinutes", 15)),
        FirstLockout = TimeSpan.FromSeconds(Int("Login Server.FirstLockoutSeconds", 60)),
        MaxLockout = TimeSpan.FromMinutes(Int("Login Server.MaxLockoutMinutes", 30)),
    }));

}
