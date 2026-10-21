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
 * CLASSIC OWNER EXTRA: SCHOOL WEAVING RULES
 * ========================================================================
 *
 * PURPOSE:
 * The School Weaving table (classic-data/rules/weaving-*.yaml, named by a
 * profile's rules.weaving): one Weave card per school, its professors, the
 * school's rider and the curated input -> face pairs. Pure data; the combat
 * side is Imlight.CoreLib.Classic.ClassicWeaving.
 *
 * NOTE:
 * Not 2010. Inert unless features.experiments.weaving is true. Design:
 * ~/w101c-private/build/playbot-reports/design-weaving.md.
 *
 * Created by: Nick with Claude Code (claude-fable-5-1)
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>CLASSIC OWNER EXTRA: one school's rider (what the woven card does on top of the converted hit).</summary>
public sealed record WeaveRider(string School, string Name, int HitPercent, int FirstHitPercent, int BurnPercent, int BurnRounds,
                                int AbsorbPercent, int AccuracyPenalty, int HealPercent, int HealRounds, int DrainPercent, int TrapPercent);

/// <summary>CLASSIC OWNER EXTRA: one curated weave: a trainable off-school input card and the client face shown for the woven card.</summary>
public sealed record WeaveInput(string Input, string Face);

/// <summary>CLASSIC OWNER EXTRA: one school's Weave card, who teaches it, and what it can weave.</summary>
public sealed record SchoolWeave(string School, string Card, ImmutableArray<ulong> Trainers, string TrainerNames, ImmutableArray<WeaveInput> Inputs);

/// <summary>CLASSIC OWNER EXTRA: the School Weaving table of the active profile.</summary>
public sealed record WeavingRules(string Id, ImmutableArray<string> Profiles, int Level, int DeckCopies, bool Pvp,
                                  ImmutableArray<WeaveRider> Riders, ImmutableArray<SchoolWeave> Weaves, string SourceFile) {

    /// <summary>No weaving (every profile without rules.weaving).</summary>
    public static WeavingRules Empty { get; } = new("", [], 20, 2, true, [], [], "");

    /// <summary>The seven school names in schema order.</summary>
    public static ImmutableArray<string> Schools { get; } = ["fire", "ice", "storm", "myth", "life", "death", "balance"];

    /// <summary>True when the file lists <paramref name="profileId"/> (or a profile it inherits membership from).</summary>
    public bool AppliesTo(string? profileId) => Id.Length > 0 && ClassicProfiles.Lists(Profiles, profileId);

    /// <summary>The rider of a school (lower case), or null.</summary>
    public WeaveRider? RiderFor(string? school)
        => school is null ? null : Riders.FirstOrDefault(rider => string.Equals(rider.School, school, StringComparison.OrdinalIgnoreCase));

    /// <summary>The weave whose Weave card is the client template at <paramref name="cardPath"/>, or null.</summary>
    public SchoolWeave? WeaveByCard(string? cardPath)
        => cardPath is null ? null : Weaves.FirstOrDefault(weave => string.Equals(weave.Card, cardPath, StringComparison.Ordinal));
}

/// <summary>CLASSIC OWNER EXTRA: strict reader of rules/weaving-*.yaml.</summary>
public static class WeavingRulesLoader {

    private static readonly FrozenSet<string> s_rootKeys = Keys("id", "title", "owner_extra", "setting", "profiles", "license_tag", "provenance",
        "level", "deck_copies", "pvp", "riders", "weaves");
    private static readonly FrozenSet<string> s_riderKeys = Keys("name", "hit_percent", "first_hit_percent", "burn_percent", "burn_rounds",
        "absorb_percent", "accuracy_penalty", "heal_percent", "heal_rounds", "drain_percent", "trap_percent");
    private static readonly FrozenSet<string> s_weaveKeys = Keys("school", "card", "trainers", "trainer_names", "inputs");
    private static readonly FrozenSet<string> s_inputKeys = Keys("input", "face");

    private static FrozenSet<string> Keys(params string[] keys) => keys.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The file the profile names in rules.weaving, or <see cref="WeavingRules.Empty"/> when it names none.</summary>
    public static WeavingRules LoadForProfile(ClassicProfile profile, string classicDataRoot)
        => profile.Rules.Weaving is { } relative ? Load(Path.Combine(classicDataRoot, relative)) : WeavingRules.Empty;

    /// <summary>Reads and checks one weaving file.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid.</exception>
    public static WeavingRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the configured weaving rules do not exist"));
        }

        var d = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, d);
        if (root is not YMap map) {
            if (root is not null) d.At(root, "", "the root must be a mapping");
            throw d.ToException();
        }

        d.CheckKeys(map, "", s_rootKeys, s_rootKeys);
        var id = Text(map, "id", "", d) ?? "";
        if (id != Path.GetFileNameWithoutExtension(fullPath) || !id.StartsWith("weaving-", StringComparison.Ordinal)) {
            d.At(map, "id", "id must be weaving-<name> and match the file name");
        }
        if (map.Find("owner_extra") is { } extra && d.ReadBool(extra.Value, "owner_extra") != true) d.At(map, "owner_extra", "must be true");
        if (Text(map, "setting", "", d) != ClassicFeatures.ExperimentsWeaving) d.At(map, "setting", $"must be {ClassicFeatures.ExperimentsWeaving}");
        if (Text(map, "license_tag", "", d) != "own") d.At(map, "license_tag", "must be own");
        var profiles = Strings(map, "profiles", "", d);
        var level = Int(map, "level", "", d, 1, 150) ?? 20;
        var copies = Int(map, "deck_copies", "", d, 1, 7) ?? 2;
        var pvp = map.Find("pvp") is { } pvpEntry && d.ReadBool(pvpEntry.Value, "pvp") == true;

        var riders = ImmutableArray.CreateBuilder<WeaveRider>();
        if (map.Find("riders") is { } ridersEntry && d.ReadMap(ridersEntry.Value, "riders") is { } ridersMap) {
            d.CheckKeys(ridersMap, "riders", WeavingRules.Schools.ToFrozenSet(StringComparer.Ordinal), WeavingRules.Schools.ToFrozenSet(StringComparer.Ordinal));
            foreach (var school in WeavingRules.Schools) {
                if (ridersMap.Find(school) is not { } entry || d.ReadMap(entry.Value, "riders." + school) is not { } r) continue;
                var at = "riders." + school;
                d.CheckKeys(r, at, s_riderKeys, Keys("name", "hit_percent"));
                int Pct(string key, int max = 300) => Int(r, key, at, d, 0, max) ?? 0;
                var rider = new WeaveRider(school, Text(r, "name", at, d) ?? school, Pct("hit_percent"), Pct("first_hit_percent"), Pct("burn_percent"),
                    Pct("burn_rounds", 10), Pct("absorb_percent"), Pct("accuracy_penalty", 100), Pct("heal_percent"), Pct("heal_rounds", 10),
                    Pct("drain_percent", 100), Pct("trap_percent", 100));
                var missing = school switch {
                    "fire" => rider.BurnPercent == 0 || rider.BurnRounds == 0,
                    "ice" => rider.AbsorbPercent == 0,
                    "myth" => rider.FirstHitPercent == 0,
                    "life" => rider.HealPercent == 0 || rider.HealRounds == 0,
                    "death" => rider.DrainPercent == 0,
                    "balance" => rider.TrapPercent == 0,
                    _ => false,
                };
                if (missing) d.At(r, at, "the rider lacks its school's signature value");
                riders.Add(rider);
            }
        } else {
            d.At(map, "riders", "needs a rider per school");
        }

        var weaves = ImmutableArray.CreateBuilder<SchoolWeave>();
        foreach (var (row, key) in Rows(map, "weaves", d)) {
            d.CheckKeys(row, key, s_weaveKeys, s_weaveKeys);
            var school = row.Find("school") is { } se ? d.ReadEnum(se.Value, key + ".school", WeavingRules.Schools.ToArray()) : null;
            var card = TemplatePath(row, "card", key, d);
            var trainers = ImmutableArray.CreateBuilder<ulong>();
            if (row.Find("trainers") is { } te && d.ReadList(te.Value, key + ".trainers") is { } list) {
                for (var i = 0; i < list.Items.Length; i++) {
                    if (d.ReadInt(list.Items[i], YamlTree.Index(key + ".trainers", i), 1) is { } tid) trainers.Add((ulong) tid);
                }
                if (trainers.Count == 0) d.At(list, key + ".trainers", "needs at least one professor");
            }
            var names = Text(row, "trainer_names", key, d) ?? "";
            var inputs = ImmutableArray.CreateBuilder<WeaveInput>();
            foreach (var (pair, pairKey) in Rows(row, "inputs", d, key + ".inputs")) {
                d.CheckKeys(pair, pairKey, s_inputKeys, s_inputKeys);
                var input = TemplatePath(pair, "input", pairKey, d);
                var face = TemplatePath(pair, "face", pairKey, d);
                if (input is not null && inputs.Any(existing => existing.Input == input)) d.At(pair, pairKey + ".input", "repeats another input of this school");
                if (input is not null && face is not null) inputs.Add(new WeaveInput(input, face));
            }
            if (school is not null && weaves.Any(weave => weave.School == school)) d.At(row, key + ".school", "repeats another school");
            if (card is not null && weaves.Any(weave => weave.Card == card)) d.At(row, key + ".card", "is already another school's Weave card");
            if (school is not null && card is not null) weaves.Add(new SchoolWeave(school, card, trainers.ToImmutable(), names, inputs.ToImmutable()));
        }
        if (weaves.Count != WeavingRules.Schools.Length) d.At(map, "weaves", "needs exactly one weave per school");

        if (d.HasErrors) throw d.ToException();
        return new WeavingRules(id, profiles, level, copies, pvp, riders.ToImmutable(), weaves.ToImmutable(), display);
    }

    private static string? TemplatePath(YMap map, string name, string key, YamlDiagnostics d) {
        var value = Text(map, name, key, d);
        if (value is not null && (!value.StartsWith("Spells/", StringComparison.Ordinal) || !value.EndsWith(".xml", StringComparison.Ordinal))) {
            d.At(map, YamlTree.Join(key, name), "must be a client spell template path (Spells/....xml)");
        }
        return value;
    }

    private static int? Int(YMap map, string name, string key, YamlDiagnostics d, int min, int max)
        => map.Find(name) is { } entry ? d.ReadInt(entry.Value, YamlTree.Join(key, name), min, max) : null;

    private static string? Text(YMap map, string name, string key, YamlDiagnostics d) {
        if (map.Find(name) is not { } entry) return null;
        var value = d.ReadString(entry.Value, YamlTree.Join(key, name));
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value != value.Trim())) {
            d.At(entry.Value, YamlTree.Join(key, name), "must be a nonempty string without surrounding whitespace");
        }
        return value;
    }

    private static ImmutableArray<(YMap Row, string Key)> Rows(YMap map, string name, YamlDiagnostics d, string? path = null) {
        path ??= name;
        if (map.Find(name) is not { } entry || d.ReadList(entry.Value, path) is not { } list) return [];
        if (list.Items.IsEmpty) d.At(list, path, "needs at least one entry");
        var rows = ImmutableArray.CreateBuilder<(YMap, string)>();
        for (var i = 0; i < list.Items.Length; i++) {
            var key = YamlTree.Index(path, i);
            if (d.ReadMap(list.Items[i], key) is { } row) rows.Add((row, key));
        }
        return rows.ToImmutable();
    }

    private static ImmutableArray<string> Strings(YMap map, string name, string key, YamlDiagnostics d) {
        var path = YamlTree.Join(key, name);
        if (map.Find(name) is not { } entry || d.ReadList(entry.Value, path) is not { } list) return [];
        if (list.Items.IsEmpty) d.At(list, path, "needs at least one entry");
        var values = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            var value = d.ReadString(list.Items[i], YamlTree.Index(path, i));
            if (value is null) continue;
            if (string.IsNullOrWhiteSpace(value) || values.Contains(value)) d.At(list.Items[i], YamlTree.Index(path, i), "must be a unique nonempty string");
            values.Add(value);
        }
        return values.ToImmutable();
    }
}
