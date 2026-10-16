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
 * ASTRAL SCHOOLS RULES (CELESTIA, OCTOBER 2010)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: reads classic-data/rules/astral-*.yaml (a profile's rules.astral):
 * the Celestia trainers that teach the Sun, Moon and Star cards (owner ruling
 * D5), the creature items the dated 2010 polymorph form leaves out, and the
 * owner's Moon rework (an owner extra, switched by [Classic] MoonRework).
 * Also the pure numbers the polymorph uses (health conversion, the rework's
 * health link and level bonus), so they are tested without a duel.
 *
 * USAGE EXAMPLE:
 * var astral = AstralRulesLoader.LoadForProfile(profile, classicDataRoot);
 * var health = PolymorphMath.ConvertHealth(current, oldMax, newMax);
 *
 * NOTE:
 * The card numbers are the spell records'; this file only says what a record
 * cannot. Schema: classic-data/schema/astral.schema.json (checked by CI).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>CLASSIC: one Celestia trainer and the Astral spell records it teaches (record ids).</summary>
public sealed record AstralTrainer(ulong Npc, string Name, string Zone, ImmutableArray<string> Spells);

/// <summary>CLASSIC: one rework form entry (owner extra), keyed by its Moon spell record id.</summary>
public sealed record MoonReworkForm(string Spell, string Role, int HealthLinkPercent, int? StunResistPercent, string? MatchesOfficial);

/// <summary>CLASSIC: the Moon rework's PvP guard (owner extra).</summary>
public sealed record MoonReworkPvp(bool OncePerMatch, int MinLevel, int MaxHealthLinkPercent);

/// <summary>
/// CLASSIC OWNER EXTRA (not 2010): the Moon rework. Moon's fantasy is transformation: the form keeps a link to the
/// caster (health, gear, critical and block, level), plays a real deck built from its own cards with part of the
/// wizard's own deck still reachable, can be left with a revert card, lasts longer, and is guarded in PvP.
/// </summary>
public sealed record MoonRework(int Rounds, string RevertCard, int OwnDeckSlots, int LevelDamagePercentPerLevel,
                                int LevelDamagePercentMax, bool KeepCasterCriticalAndBlock, bool KeepCasterUniversalGear,
                                int NaturalAttackCopies, int UtilityCopies, MoonReworkPvp Pvp, ImmutableArray<MoonReworkForm> Forms) {

    /// <summary>The form entry of a Moon spell record, or null when the rework leaves that form as dated.</summary>
    public MoonReworkForm? FormFor(string? recordId)
        => recordId is null ? null : Forms.FirstOrDefault(form => string.Equals(form.Spell, recordId, StringComparison.Ordinal));
}

/// <summary>CLASSIC: the Astral schools file of the active profile.</summary>
public sealed record AstralRules(string Id, ImmutableArray<string> Profiles, ImmutableArray<AstralTrainer> Trainers,
                                 FrozenSet<uint> FormItemExclusions, MoonRework? MoonRework, string SourceFile) {

    /// <summary>No Astral schools (every profile without rules.astral).</summary>
    public static AstralRules Empty { get; } = new("", [], [], FrozenSet<uint>.Empty, null, "");

    /// <summary>True when the file lists <paramref name="profileId"/> (or a profile it inherits membership from).</summary>
    public bool AppliesTo(string? profileId) => Id.Length > 0 && ClassicProfiles.Lists(Profiles, profileId);
}

/// <summary>CLASSIC: strict reader of rules/astral-*.yaml.</summary>
public static class AstralRulesLoader {

    private static readonly FrozenSet<string> s_rootKeys = Keys("id", "profiles", "license_tag", "provenance", "trainers", "polymorph", "moon_rework");
    private static readonly FrozenSet<string> s_trainerKeys = Keys("npc", "name", "zone", "source_id", "spells");
    private static readonly FrozenSet<string> s_polymorphKeys = Keys("form_item_exclusions");
    private static readonly FrozenSet<string> s_exclusionKeys = Keys("item", "reason", "source_id");
    private static readonly FrozenSet<string> s_reworkKeys = Keys("owner_extra", "setting", "rounds", "revert_card", "own_deck_slots",
        "level_damage_percent_per_level", "level_damage_percent_max", "keep_caster_critical_and_block", "keep_caster_universal_gear",
        "natural_attack_copies", "utility_copies", "pvp", "forms");
    private static readonly FrozenSet<string> s_pvpKeys = Keys("once_per_match", "min_level", "max_health_link_percent");
    private static readonly FrozenSet<string> s_formKeys = Keys("spell", "role", "health_link_percent", "stun_resist_percent", "matches_official");
    private static readonly FrozenSet<string> s_formRequired = Keys("spell", "role", "health_link_percent");
    private static readonly string[] s_roles = ["tank", "healer", "support", "striker", "trickster"];

    /// <summary>The file the profile names in rules.astral, or <see cref="AstralRules.Empty"/> when it names none.</summary>
    public static AstralRules LoadForProfile(ClassicProfile profile, string classicDataRoot)
        => profile.Rules.Astral is { } relative ? Load(Path.Combine(classicDataRoot, relative)) : AstralRules.Empty;

    /// <summary>Reads and checks one Astral schools file.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid.</exception>
    public static AstralRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the configured Astral rules do not exist"));
        }

        var d = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, d);
        if (root is not YMap map) {
            if (root is not null) d.At(root, "", "the root must be a mapping");
            throw d.ToException();
        }

        d.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "license_tag", "provenance", "trainers", "polymorph"]);
        var id = Text(map, "id", "", d) ?? "";
        if (id != Path.GetFileNameWithoutExtension(fullPath) || !id.StartsWith("astral-", StringComparison.Ordinal)) {
            d.At(map, "id", "id must be astral-<name> and match the file name");
        }
        if (Text(map, "license_tag", "", d) != "own") d.At(map, "license_tag", "must be own");
        var profiles = Strings(map, "profiles", "", d);

        var trainers = ImmutableArray.CreateBuilder<AstralTrainer>();
        foreach (var (row, key) in Rows(map, "trainers", d)) {
            d.CheckKeys(row, key, s_trainerKeys, s_trainerKeys);
            var npc = row.Find("npc") is { } n ? d.ReadInt(n.Value, key + ".npc", 1) : null;
            var name = Text(row, "name", key, d);
            var zone = Text(row, "zone", key, d);
            var spells = Strings(row, "spells", key, d);
            foreach (var spell in spells.Where(spell => !IsAstralRecordId(spell))) {
                d.At(row, key + ".spells", $"'{spell}' is not a Sun, Moon or Star spell record id");
            }
            if (npc is { } tid && name is not null && zone is not null) {
                if (trainers.Any(trainer => trainer.Npc == (ulong) tid)) d.At(row, key + ".npc", "repeats another trainer");
                trainers.Add(new AstralTrainer((ulong) tid, name, zone, spells));
            }
        }

        var exclusions = new System.Collections.Generic.HashSet<uint>();
        if (map.Find("polymorph") is { } poly && d.ReadMap(poly.Value, "polymorph") is { } polyMap) {
            d.CheckKeys(polyMap, "polymorph", s_polymorphKeys, s_polymorphKeys);
            foreach (var (row, key) in Rows(polyMap, "form_item_exclusions", d, "polymorph.form_item_exclusions", allowEmpty: true)) {
                d.CheckKeys(row, key, s_exclusionKeys, s_exclusionKeys);
                if (row.Find("item") is { } item && d.ReadInt(item.Value, key + ".item", 1) is { } itemId) exclusions.Add((uint) itemId);
                _ = Text(row, "reason", key, d);
            }
        }

        MoonRework? rework = null;
        if (map.Find("moon_rework") is { } reworkEntry && d.ReadMap(reworkEntry.Value, "moon_rework") is { } r) {
            rework = ReadRework(r, d);
        }

        if (d.HasErrors) throw d.ToException();
        return new AstralRules(id, profiles, trainers.ToImmutable(), exclusions.ToFrozenSet(), rework, display);
    }

    private static MoonRework? ReadRework(YMap r, YamlDiagnostics d) {
        const string at = "moon_rework";
        d.CheckKeys(r, at, s_reworkKeys, s_reworkKeys);
        if (r.Find("owner_extra") is { } extra && d.ReadBool(extra.Value, at + ".owner_extra") != true) {
            d.At(r, at + ".owner_extra", "the Moon rework is an owner extra and must say owner_extra: true");
        }
        if (Text(r, "setting", at, d) != "Classic.MoonRework") d.At(r, at + ".setting", "must be Classic.MoonRework");
        int Int(string key, int min, int max) => r.Find(key) is { } e ? d.ReadInt(e.Value, at + "." + key, min, max) ?? min : min;
        bool Bool(string key) => r.Find(key) is { } e && d.ReadBool(e.Value, at + "." + key) == true;
        var revert = Text(r, "revert_card", at, d) ?? "";
        if (!revert.StartsWith("Spells/", StringComparison.Ordinal) || !revert.EndsWith(".xml", StringComparison.Ordinal)) {
            d.At(r, at + ".revert_card", "must be a client spell template path (Spells/....xml)");
        }

        var pvp = new MoonReworkPvp(true, 50, 100);
        if (r.Find("pvp") is { } pvpEntry && d.ReadMap(pvpEntry.Value, at + ".pvp") is { } p) {
            d.CheckKeys(p, at + ".pvp", s_pvpKeys, s_pvpKeys);
            pvp = new MoonReworkPvp(
                p.Find("once_per_match") is { } once && d.ReadBool(once.Value, at + ".pvp.once_per_match") == true,
                p.Find("min_level") is { } ml ? d.ReadInt(ml.Value, at + ".pvp.min_level", 1) ?? 50 : 50,
                p.Find("max_health_link_percent") is { } mh ? d.ReadInt(mh.Value, at + ".pvp.max_health_link_percent", 0, 300) ?? 100 : 100);
        }

        var forms = ImmutableArray.CreateBuilder<MoonReworkForm>();
        foreach (var (row, key) in Rows(r, "forms", d, at + ".forms")) {
            d.CheckKeys(row, key, s_formKeys, s_formRequired);
            var spell = Text(row, "spell", key, d);
            var role = row.Find("role") is { } re ? d.ReadEnum(re.Value, key + ".role", s_roles) : null;
            var link = row.Find("health_link_percent") is { } hl ? d.ReadInt(hl.Value, key + ".health_link_percent", 0, 300) : null;
            var stun = row.Find("stun_resist_percent") is { } sr ? d.ReadInt(sr.Value, key + ".stun_resist_percent", 0, 100) : null;
            var official = row.Find("matches_official") is { } mo ? d.ReadDate(mo.Value, key + ".matches_official", false)?.ToString("yyyy-MM-dd") : null;
            if (spell is not null && !spell.StartsWith("spell.moon.", StringComparison.Ordinal)) d.At(row, key + ".spell", "must be a Moon spell record id");
            if (spell is not null && forms.Any(form => form.Spell == spell)) d.At(row, key + ".spell", "repeats another form");
            if (spell is not null && role is not null && link is not null) forms.Add(new MoonReworkForm(spell, role, link.Value, stun, official));
        }

        return new MoonRework(Int("rounds", 1, 20), revert, Int("own_deck_slots", 0, 3), Int("level_damage_percent_per_level", 0, 5),
            Int("level_damage_percent_max", 0, 50), Bool("keep_caster_critical_and_block"), Bool("keep_caster_universal_gear"),
            Int("natural_attack_copies", 1, 10), Int("utility_copies", 1, 10), pvp, forms.ToImmutable());
    }

    internal static bool IsAstralRecordId(string id)
        => id.StartsWith("spell.sun.", StringComparison.Ordinal) || id.StartsWith("spell.moon.", StringComparison.Ordinal)
            || id.StartsWith("spell.star.", StringComparison.Ordinal);

    private static FrozenSet<string> Keys(params string[] keys) => keys.ToFrozenSet(StringComparer.Ordinal);

    private static string? Text(YMap map, string name, string key, YamlDiagnostics d) {
        if (map.Find(name) is not { } entry) return null;
        var value = d.ReadString(entry.Value, YamlTree.Join(key, name));
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value != value.Trim())) {
            d.At(entry.Value, YamlTree.Join(key, name), "must be a nonempty string without surrounding whitespace");
        }
        return value;
    }

    private static ImmutableArray<(YMap Row, string Key)> Rows(YMap map, string name, YamlDiagnostics d, string? path = null,
                                                              bool allowEmpty = false) {
        path ??= name;
        if (map.Find(name) is not { } entry || d.ReadList(entry.Value, path) is not { } list) return [];
        if (list.Items.IsEmpty && !allowEmpty) d.At(list, path, "needs at least one entry");
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
            if (string.IsNullOrWhiteSpace(value) || values.Contains(value)) {
                d.At(list.Items[i], YamlTree.Index(path, i), "must be a unique nonempty string");
            }
            values.Add(value);
        }
        return values.ToImmutable();
    }
}

/// <summary>
/// CLASSIC: the numbers of a polymorph, pure so they are tested without a duel. The 2010 rule (archived October 2010
/// notes): "Max Health (Current Health is proportionally converted)". The rest is the owner's Moon rework.
/// </summary>
public static class PolymorphMath {

    /// <summary>
    /// The current health after max health changes from <paramref name="oldMax"/> to <paramref name="newMax"/>, keeping the
    /// same share (rounded to nearest, at least 1 while alive, never above the new max).
    /// </summary>
    public static int ConvertHealth(int current, int oldMax, int newMax) {
        if (current <= 0 || newMax <= 0) return 0;
        if (oldMax <= 0) return newMax;
        var converted = (long) Math.Round((double) current * newMax / oldMax, MidpointRounding.AwayFromZero);
        return (int) Math.Clamp(converted, 1L, newMax);
    }

    /// <summary>
    /// OWNER EXTRA: the rework form's max health: the larger of the form's own health and <paramref name="linkPercent"/> of
    /// the caster's max health (capped by <paramref name="capPercent"/> in PvP when given).
    /// </summary>
    public static int ReworkFormMaxHealth(int formHealth, int casterMaxHealth, int linkPercent, int? capPercent = null) {
        var link = capPercent is { } cap ? Math.Min(linkPercent, cap) : linkPercent;
        var linked = (long) Math.Round((double) Math.Max(casterMaxHealth, 0) * Math.Max(link, 0) / 100, MidpointRounding.AwayFromZero);
        return (int) Math.Min(int.MaxValue, Math.Max(formHealth, linked));
    }

    /// <summary>OWNER EXTRA: the rework's damage bonus (a fraction) for a caster level: per level above 50, up to the max.</summary>
    public static float ReworkLevelDamageBonus(int level, int percentPerLevel, int percentMax)
        => Math.Clamp((level - 50) * percentPerLevel, 0, Math.Max(percentMax, 0)) / 100f;
}
