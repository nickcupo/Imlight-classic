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
 * CLASSIC SERVER SWITCHES (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * The server's one copy of the [Classic] switches (ClassicSettingsStore),
 * read from Imlight.ini with the dashboard's overrides on top.
 *
 * USAGE EXAMPLE:
 * var gold = (int) Math.Round(baseGold * ClassicSettings.GoldMultiplier);
 *
 * NOTE:
 * Overrides live in [Classic] SettingsOverridePath (default
 * classic-settings.json in the working directory, /var/lib/w101c on the LXC).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.IO;
using Imlight.Classic.Settings;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The [Classic] switches in effect.
/// </summary>
public static class ClassicSettings {

    private static readonly Lazy<ClassicSettingsStore> s_store = new(Create);

    public static ClassicSettingsStore Store => s_store.Value;

    public static double XpMultiplier => Store.Double(ClassicSettingKeys.XpMultiplier);
    public static double GoldMultiplier => Store.Double(ClassicSettingKeys.GoldMultiplier);
    public static double DropRateMultiplier => Store.Double(ClassicSettingKeys.DropRateMultiplier);
    public static int BackpackSize => Store.Int(ClassicSettingKeys.BackpackSize);
    public static double SpellAnimationSpeed => Store.Double(ClassicSettingKeys.SpellAnimationSpeed);
    public static bool TeleportToFriendAnywhere => Store.Bool(ClassicSettingKeys.TeleportToFriendAnywhere);
    public static int CombatRejoinSeconds => Store.Int(ClassicSettingKeys.CombatRejoinSeconds);
    public static int RestartMaxWaitMinutes => Store.Int(ClassicSettingKeys.RestartMaxWaitMinutes);
    public static int ZoneTransferDelayMs => Store.Int(ClassicSettingKeys.ZoneTransferDelayMs);
    public static bool HolidayEvents => Store.Bool(ClassicSettingKeys.HolidayEvents);
    public static int HolidayDateOverride => Store.Int(ClassicSettingKeys.HolidayDateOverride);
    public static bool BazaarStocked => Store.Bool(ClassicSettingKeys.BazaarStocked);
    public static int BazaarRestockMinutes => Store.Int(ClassicSettingKeys.BazaarRestockMinutes);
    public static int BazaarStockPerRestock => Store.Int(ClassicSettingKeys.BazaarStockPerRestock);
    public static bool OpenPvp => Store.Bool(ClassicSettingKeys.OpenPvp);
    public static int PvpCountdownSeconds => Store.Int(ClassicSettingKeys.PvpCountdownSeconds);
    public static bool PetPavilion => Store.Bool(ClassicSettingKeys.PetPavilion);

    /// <summary>Scales a whole reward amount, rounding to the nearest unit (never below 0).</summary>
    public static int Scale(int amount, double multiplier)
        => amount <= 0 || multiplier == 1.0 ? amount : (int) Math.Clamp(Math.Round(amount * multiplier), 0, int.MaxValue);

    private static ClassicSettingsStore Create() {
        var path = Setting("Classic.SettingsOverridePath") ?? "classic-settings.json";
        try {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            Logger.Warning("Classic.SettingsOverridePath {Path} is not a path; dashboard changes are kept in memory only.",
                Logger.Args(path));
            path = null;
        }

        var store = new ClassicSettingsStore(key => Setting($"Classic.{key}"), path);
        foreach (var warning in store.Warnings) {
            Logger.Warning("Classic switches: {Warning}", Logger.Args(warning));
        }

        return store;
    }

    private static string? Setting(string key) {
        try {
            return ConfigurationManager.GetSetting(key) is { Length: > 0 } text ? text : null;
        }
        catch (Exception) {
            return null; // configuration not initialized (tests)
        }
    }

}
