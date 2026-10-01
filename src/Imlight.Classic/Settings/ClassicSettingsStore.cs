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
 * CLASSIC SERVER SWITCHES
 * ========================================================================
 *
 * PURPOSE:
 * The owner's server switches in [Classic]: quality-of-life multipliers, the
 * open PvP circles, the stocked Bazaar, holiday events, combat rejoin and
 * safe restarts. Each has a 2009 default; the admin dashboard can change the
 * ones marked live while the server runs, and those changes persist in a
 * small JSON file next to the server's working directory (the deployed
 * Imlight.ini is read-only to the server).
 *
 * USAGE EXAMPLE:
 * var store = new ClassicSettingsStore(key => ini[key], "classic-settings.json");
 * var xp = store.Double(ClassicSettingKeys.XpMultiplier);
 * store.TrySet(ClassicSettingKeys.XpMultiplier, "2", out var error);
 *
 * NOTE:
 * Order of precedence: a dashboard override, then Imlight.ini, then the
 * built-in 2009 default. A value that does not parse or is out of range is
 * ignored with the reason kept for the dashboard.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Imlight.Classic.Settings;

/// <summary>The kind of value a switch holds.</summary>
public enum ClassicSettingKind {

    Bool,
    Int,
    Double

}

/// <summary>
/// One switch: its ini key under [Classic], kind, 2009 default, allowed range and whether it can change while the
/// server runs.
/// </summary>
public sealed record ClassicSettingDefinition(
    string Key,
    ClassicSettingKind Kind,
    string Default,
    string Group,
    string Description,
    double Min = double.MinValue,
    double Max = double.MaxValue,
    bool Live = true);

/// <summary>The keys of the switches, as written in Imlight.ini's [Classic] section.</summary>
public static class ClassicSettingKeys {

    public const string XpMultiplier = "XpMultiplier";
    public const string GoldMultiplier = "GoldMultiplier";
    public const string DropRateMultiplier = "DropRateMultiplier";
    public const string BackpackSize = "BackpackSize";
    public const string BankSize = "BankSize";
    public const string SpellAnimationSpeed = "SpellAnimationSpeed";
    public const string TeleportToFriendAnywhere = "TeleportToFriendAnywhere";
    public const string CombatRejoinSeconds = "CombatRejoinSeconds";
    public const string RestartMaxWaitMinutes = "RestartMaxWaitMinutes";
    public const string HolidayEvents = "HolidayEvents";
    public const string HolidayDateOverride = "HolidayDateOverride";
    public const string BazaarStocked = "BazaarStocked";
    public const string BazaarRestockMinutes = "BazaarRestockMinutes";
    public const string BazaarStockPerRestock = "BazaarStockPerRestock";
    public const string OpenPvp = "OpenPvp";
    public const string PvpCountdownSeconds = "PvpCountdownSeconds";

}

/// <summary>
/// The switches' values: dashboard overrides over Imlight.ini over the 2009 defaults.
/// </summary>
public sealed class ClassicSettingsStore {

    /// <summary>Every switch the server knows, in dashboard order.</summary>
    public static ImmutableArray<ClassicSettingDefinition> Definitions { get; } = [
        new(ClassicSettingKeys.XpMultiplier, ClassicSettingKind.Double, "1", "Quality of life",
            "Multiplies the XP from duels and quest rewards. 2009: 1.", 0, 100),
        new(ClassicSettingKeys.GoldMultiplier, ClassicSettingKind.Double, "1", "Quality of life",
            "Multiplies the gold from mobs, quest rewards and chests. 2009: 1.", 0, 100),
        new(ClassicSettingKeys.DropRateMultiplier, ClassicSettingKind.Double, "1", "Quality of life",
            "Multiplies each item, Treasure Card and reagent drop chance (each chance is capped at 100%, and a mob still "
            + "drops at most its 2009 number of items). 2009: 1.", 0, 100),
        new(ClassicSettingKeys.BackpackSize, ClassicSettingKind.Int, "0", "Quality of life",
            "Backpack slots in all. 0 uses Character.MaxInventoryItems. (The 2009 backpack held eight of each kind of "
            + "equipment, wiki \"Vendors and Banking\" oldid 4921; this server counts one total.) Applies at the next login.",
            0, 1000),
        new(ClassicSettingKeys.BankSize, ClassicSettingKind.Int, "100", "Quality of life",
            "Bank slots (2009: 100, wiki oldid 4921). This server has no bank service yet, so it has no effect.",
            0, 1000),
        new(ClassicSettingKeys.SpellAnimationSpeed, ClassicSettingKind.Double, "1", "Quality of life",
            "How fast the server moves on after spell animations (2 waits half as long). The client must also play "
            + "them faster, or the next round starts before its animation ends. 2009: 1.", 0.25, 4),
        new(ClassicSettingKeys.TeleportToFriendAnywhere, ClassicSettingKind.Bool, "true", "Quality of life",
            "Teleport to a friend in any open zone, whatever worlds you have unlocked (owner ruling 2026-10-01)."),
        new(ClassicSettingKeys.CombatRejoinSeconds, ClassicSettingKind.Int, "120", "Reliability",
            "How long a wizard who drops mid-fight keeps their seat. They pass each round, and if every wizard in the "
            + "fight has dropped, it waits. 0 removes them at once, as a flee.", 0, 1800),
        new(ClassicSettingKeys.RestartMaxWaitMinutes, ClassicSettingKind.Int, "10", "Reliability",
            "A safe restart waits until no one is in a fight, but never longer than this after the warning ends.", 0, 120),
        new(ClassicSettingKeys.HolidayEvents, ClassicSettingKind.Bool, "true", "Events",
            "Run the 2009 holiday events (classic-data/holidays) on their real calendar dates."),
        new(ClassicSettingKeys.HolidayDateOverride, ClassicSettingKind.Int, "0", "Events",
            "Pretend today is this date (yyyyMMdd) for holiday events. 0: the real date.", 0, 99991231),
        new(ClassicSettingKeys.BazaarStocked, ClassicSettingKind.Bool, "true", "Bazaar",
            "Keep the Bazaar stocked with 2009 items (owner request). Players' own sales still list and sell normally."),
        new(ClassicSettingKeys.BazaarRestockMinutes, ClassicSettingKind.Int, "60", "Bazaar",
            "Minutes between Bazaar restocks.", 1, 10080),
        new(ClassicSettingKeys.BazaarStockPerRestock, ClassicSettingKind.Int, "120", "Bazaar",
            "How many server lots each restock aims to keep listed.", 0, 2000),
        new(ClassicSettingKeys.OpenPvp, ClassicSettingKind.Bool, "true", "PvP",
            "Open duel circles in the Wizard City Arena: walk in, pick a side, 1v1 up to 4v4, no ranks or penalty."),
        new(ClassicSettingKeys.PvpCountdownSeconds, ClassicSettingKind.Int, "20", "PvP",
            "Seconds an arena circle waits for more wizards once both sides have one, unless all are ready sooner.",
            3, 300),
    ];

    private static readonly IReadOnlyDictionary<string, ClassicSettingDefinition> s_byKey =
        Definitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, string?> _ini;
    private readonly string? _overridePath;
    private readonly ConcurrentDictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();

    /// <param name="ini">Reads a [Classic] key from Imlight.ini (null or empty when it is not set).</param>
    /// <param name="overridePath">The JSON file of dashboard overrides, or null to keep them in memory only.</param>
    public ClassicSettingsStore(Func<string, string?> ini, string? overridePath) {
        _ini = ini;
        _overridePath = overridePath;
        LoadOverrides();
    }

    /// <summary>Problems met reading the override file or the ini, for the dashboard and the log.</summary>
    public List<string> Warnings { get; } = [];

    public static ClassicSettingDefinition? Find(string key) => s_byKey.GetValueOrDefault(key);

    /// <summary>The value in effect and where it comes from ("dashboard", "ini" or "default").</summary>
    public (string Value, string Source) Resolve(string key) {
        var definition = s_byKey.GetValueOrDefault(key) ?? throw new ArgumentException($"unknown switch {key}", nameof(key));
        if (_overrides.TryGetValue(definition.Key, out var overridden) && Validate(definition, overridden, out _)) {
            return (Normalize(definition, overridden), "dashboard");
        }

        var fromIni = _ini(definition.Key);
        if (!string.IsNullOrWhiteSpace(fromIni) && Validate(definition, fromIni.Trim(), out _)) {
            return (Normalize(definition, fromIni.Trim()), "ini");
        }

        return (definition.Default, "default");
    }

    public bool Bool(string key) => bool.Parse(Resolve(key).Value);

    public int Int(string key) => int.Parse(Resolve(key).Value, CultureInfo.InvariantCulture);

    public double Double(string key) => double.Parse(Resolve(key).Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Sets a dashboard override (or clears it when <paramref name="value"/> is null or empty) and saves the file.
    /// </summary>
    public bool TrySet(string key, string? value, out string error) {
        if (s_byKey.GetValueOrDefault(key) is not { } definition) {
            error = $"unknown switch {key}";

            return false;
        }

        if (!definition.Live) {
            error = $"{definition.Key} only changes in Imlight.ini";

            return false;
        }

        if (string.IsNullOrWhiteSpace(value)) {
            _overrides.TryRemove(definition.Key, out _);
        }
        else {
            var trimmed = value.Trim();
            if (!Validate(definition, trimmed, out error)) {
                return false;
            }

            _overrides[definition.Key] = Normalize(definition, trimmed);
        }

        error = "";
        try {
            SaveOverrides();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            error = $"saved for this run only: {ex.Message}";
        }

        return true;
    }

    /// <summary>Every switch with its value in effect and source.</summary>
    public IEnumerable<(ClassicSettingDefinition Definition, string Value, string Source)> Snapshot()
        => Definitions.Select(definition => {
            var (value, source) = Resolve(definition.Key);

            return (definition, value, source);
        });

    /// <summary>Checks a value against a switch's kind and range.</summary>
    public static bool Validate(ClassicSettingDefinition definition, string value, out string error) {
        error = "";
        switch (definition.Kind) {
            case ClassicSettingKind.Bool:
                if (bool.TryParse(value, out _)) {
                    return true;
                }

                error = $"{definition.Key} must be true or false";

                return false;
            case ClassicSettingKind.Int:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) {
                    error = $"{definition.Key} must be a whole number";

                    return false;
                }

                return InRange(definition, whole, out error);
            default:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        || double.IsNaN(number) || double.IsInfinity(number)) {
                    error = $"{definition.Key} must be a number";

                    return false;
                }

                return InRange(definition, number, out error);
        }
    }

    private static bool InRange(ClassicSettingDefinition definition, double value, out string error) {
        if (value < definition.Min || value > definition.Max) {
            error = $"{definition.Key} must be between {definition.Min.ToString(CultureInfo.InvariantCulture)} "
                + $"and {definition.Max.ToString(CultureInfo.InvariantCulture)}";

            return false;
        }

        error = "";

        return true;
    }

    private static string Normalize(ClassicSettingDefinition definition, string value) => definition.Kind switch {
        ClassicSettingKind.Bool => bool.Parse(value) ? "true" : "false",
        ClassicSettingKind.Int => int.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => double.Parse(value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture),
    };

    private void LoadOverrides() {
        if (_overridePath is null || !File.Exists(_overridePath)) {
            return;
        }

        try {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_overridePath)) ?? [];
            foreach (var (key, value) in values) {
                if (s_byKey.GetValueOrDefault(key) is not { } definition) {
                    Warnings.Add($"{_overridePath}: unknown switch {key} ignored");
                    continue;
                }

                if (!Validate(definition, value, out var error)) {
                    Warnings.Add($"{_overridePath}: {error}; ignored");
                    continue;
                }

                _overrides[definition.Key] = Normalize(definition, value);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Warnings.Add($"{_overridePath}: {ex.Message}");
        }
    }

    private void SaveOverrides() {
        if (_overridePath is null) {
            return;
        }

        lock (_writeLock) {
            var snapshot = _overrides.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var temporary = _overridePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _overridePath, overwrite: true);
        }
    }

}
