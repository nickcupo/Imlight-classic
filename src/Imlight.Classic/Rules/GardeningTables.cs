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
 * GARDENING TABLES (DECEMBER 2010 - MAY 2011)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: reads classic-data/rules/gardening-*.yaml (a profile's rules.gardening): Energy per level, the cost of
 * planting a seed, the gardening ranks, the gardener vendors' stock and trainable gardening spells, the tutorial's
 * starter spells, the harvest reward tables and the gardening items of the Crown Shop. The plant and spell numbers
 * themselves are the client's templates (owner ruling D7); this file says what a template cannot.
 *
 * USAGE EXAMPLE:
 * var tables = GardeningTablesLoader.LoadForProfile(profile, classicDataRoot);
 * var max = tables.Energy?.MaxAt(level);
 *
 * NOTE:
 * Schema: classic-data/schema/gardening.schema.json (checked by CI).
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

/// <summary>CLASSIC: Energy (shared by pets and gardening, December 2010): <c>start</c> at level 1, +<c>per_even_level</c>
/// at each even level.</summary>
public sealed record GardenEnergyRule(int Start, int PerEvenLevel) {
    /// <summary>The maximum Energy at <paramref name="level"/> (level 1 and below: the start).</summary>
    public int MaxAt(int level) => Start + PerEvenLevel * (Math.Max(level, 1) / 2);
}

/// <summary>CLASSIC: one gardening rank: the XP that leaves it (null at the last rank).</summary>
public sealed record GardenRankRow(int Rank, int? XpToNext);

/// <summary>CLASSIC: an item a gardener sells (its price is the client template's).</summary>
public sealed record GardenVendorItem(uint Template, string Name);

/// <summary>CLASSIC: a gardening spell a gardener teaches for gold from a gardening rank.</summary>
public sealed record GardenVendorSpell(uint Template, string Name, int Rank, int Gold);

/// <summary>CLASSIC: one gardener: an NPC template, its shop window layout, stock and trainable spells.</summary>
public sealed record GardenVendor(uint Npc, string Name, string Zone, int ShopLayout,
                                  ImmutableArray<GardenVendorItem> Stock, ImmutableArray<GardenVendorSpell> Spells);

/// <summary>CLASSIC: the kinds of harvest reward.</summary>
public enum GardenRewardKind { Item, Reagent, Card, Snack, Seed, Gold }

/// <summary>CLASSIC: one harvest reward line: rolled on its own with <see cref="Chance"/>; a count from Min to Max.</summary>
public sealed record GardenReward(GardenRewardKind Kind, uint Template, string Name, double Chance, int Min, int Max);

/// <summary>CLASSIC: the harvest table of one plant: the plant stage templates it applies to and what a mature and an
/// elder harvest give.</summary>
public sealed record GardenHarvestTable(string Plant, ImmutableArray<uint> Mature, ImmutableArray<uint> Elder,
                                        ImmutableArray<GardenReward> MatureRewards, ImmutableArray<GardenReward> ElderRewards) {
    /// <summary>pick: one: a harvest gives its gold lines and ONE of its other lines, chosen evenly (the dated pages list
    /// what a plant can give, not how much or how often).</summary>
    public bool PickOne { get; init; }
}

/// <summary>CLASSIC: how near objects change a plant's growth (client: within 1000 units, modifier clamped 0.1 to 5).</summary>
public sealed record GardenLikesRule(float Radius, float MinModifier, float MaxModifier);

/// <summary>CLASSIC: how pests arrive (client PestEntry m_percentPerHour, rolled every m_pestUpdateRate).</summary>
public sealed record GardenPestRule(bool Enabled, double ChanceScale);

/// <summary>CLASSIC: the gardening tables of the active profile.</summary>
public sealed record GardeningTables(string Id, ImmutableArray<string> Profiles, GardenEnergyRule? Energy, int PlantSeedEnergy,
                                     int MaxRank, ImmutableArray<GardenRankRow> Ranks, ImmutableArray<string> TutorialQuests,
                                     ImmutableArray<GardenVendorSpell> StarterSpells, ImmutableArray<GardenVendor> Vendors,
                                     GardenLikesRule Likes, GardenPestRule Pests, ImmutableArray<GardenHarvestTable> Harvests,
                                     string SourceFile) {

    /// <summary>No gardening tables (every profile without rules.gardening).</summary>
    public static GardeningTables Empty { get; } = new("", [], null, 0, 0, [], [], [], [], new(1000f, 0.1f, 5f), new(false, 1), [], "");

    /// <summary>True when the file lists <paramref name="profileId"/> (or a profile it inherits membership from).</summary>
    public bool AppliesTo(string? profileId) => Id.Length > 0 && ClassicProfiles.Lists(Profiles, profileId);

    /// <summary>The gardener with NPC template <paramref name="npc"/>, if any.</summary>
    public GardenVendor? Vendor(ulong npc) => Vendors.FirstOrDefault(v => v.Npc == npc);

    /// <summary>The gardening rank for a gardening XP total (1 to <see cref="MaxRank"/>; 1 when there is no table).</summary>
    public int RankFor(int xp) {
        var rank = 1;
        foreach (var row in Ranks.OrderBy(r => r.Rank)) {
            if (row.Rank >= MaxRank || row.XpToNext is not { } leave || xp < leave) break;
            rank = row.Rank + 1;
        }
        return Math.Clamp(rank, 1, Math.Max(MaxRank, 1));
    }

    /// <summary>The harvest table for a plant stage template, and whether that template is an elder.</summary>
    public (GardenHarvestTable Table, bool Elder)? HarvestFor(uint plantTemplate) {
        foreach (var table in Harvests) {
            if (table.Elder.Contains(plantTemplate)) return (table, true);
            if (table.Mature.Contains(plantTemplate)) return (table, false);
        }
        return null;
    }
}

/// <summary>CLASSIC: strict reader of rules/gardening-*.yaml.</summary>
public static class GardeningTablesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = Keys("id", "title", "profiles", "license_tag", "provenance", "notes",
        "energy", "plant_seed_energy", "ranks", "tutorial", "vendors", "likes", "pests", "harvests");
    private static readonly FrozenSet<string> s_energyKeys = Keys("start", "per_even_level", "confidence", "source");
    private static readonly FrozenSet<string> s_ranksKeys = Keys("max_rank", "confidence", "source", "levels");
    private static readonly FrozenSet<string> s_rankKeys = Keys("rank", "name_key", "xp_to_next");
    private static readonly FrozenSet<string> s_tutorialKeys = Keys("quests", "starter_spells", "confidence", "source");
    private static readonly FrozenSet<string> s_vendorKeys = Keys("npc", "name", "zone", "shop_layout", "stock", "spells", "confidence", "source", "notes");
    private static readonly FrozenSet<string> s_itemKeys = Keys("template", "name", "confidence", "notes");
    private static readonly FrozenSet<string> s_spellKeys = Keys("template", "name", "rank", "gold", "confidence", "notes");
    private static readonly FrozenSet<string> s_likesKeys = Keys("radius", "min_modifier", "max_modifier", "confidence", "source");
    private static readonly FrozenSet<string> s_pestKeys = Keys("enabled", "chance_scale", "confidence", "source");
    private static readonly FrozenSet<string> s_harvestKeys = Keys("plant", "pick", "mature", "elder", "mature_rewards", "elder_rewards", "confidence", "source", "notes");
    private static readonly FrozenSet<string> s_rewardKeys = Keys("kind", "template", "name", "chance", "min", "max", "notes");
    private static readonly string[] s_kinds = ["item", "reagent", "card", "snack", "seed", "gold"];

    /// <summary>The file the profile names in rules.gardening, or <see cref="GardeningTables.Empty"/> when it names none.</summary>
    public static GardeningTables LoadForProfile(ClassicProfile profile, string classicDataRoot)
        => profile.Rules.Gardening is { } relative ? Load(Path.Combine(classicDataRoot, relative)) : GardeningTables.Empty;

    /// <summary>Reads and checks one gardening tables file.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid.</exception>
    public static GardeningTables Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the configured gardening tables do not exist"));
        }

        var d = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, d);
        if (root is not YMap map) {
            if (root is not null) d.At(root, "", "the root must be a mapping");
            throw d.ToException();
        }

        d.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "license_tag", "provenance", "energy", "ranks", "vendors"]);
        var id = Text(map, "id", "", d) ?? "";
        if (id != Path.GetFileNameWithoutExtension(fullPath) || !id.StartsWith("gardening-", StringComparison.Ordinal)) {
            d.At(map, "id", "id must be gardening-<name> and match the file name");
        }
        if (Text(map, "license_tag", "", d) != "own") d.At(map, "license_tag", "must be own");
        var profiles = ClassicRuleFiles.ReadProfiles(map, d);

        GardenEnergyRule? energy = null;
        if (Section(map, "energy", s_energyKeys, ["start", "per_even_level"], d) is { } e) {
            energy = new GardenEnergyRule(Int(e, "start", "energy", 1, 1000, d), Int(e, "per_even_level", "energy", 0, 100, d));
        }
        var plantSeed = map.Find("plant_seed_energy") is { } ps ? d.ReadInt(ps.Value, "plant_seed_energy", 0, 1000) ?? 0 : 0;

        var maxRank = 0;
        var ranks = ImmutableArray.CreateBuilder<GardenRankRow>();
        if (Section(map, "ranks", s_ranksKeys, ["max_rank", "levels"], d) is { } r) {
            maxRank = Int(r, "max_rank", "ranks", 1, 100, d);
            foreach (var (row, key) in Rows(r, "levels", d, "ranks.levels")) {
                d.CheckKeys(row, key, s_rankKeys, ["rank"]);
                var rank = Int(row, "rank", key, 1, 100, d);
                int? xp = row.Find("xp_to_next") is { } x && x.Value is not YNull ? d.ReadInt(x.Value, key + ".xp_to_next", 1) : null;
                if (row.Find("name_key") is { } nk) _ = d.ReadString(nk.Value, key + ".name_key");
                if (ranks.Count > 0 && rank != ranks[^1].Rank + 1) d.At(row, key + ".rank", "ranks run from 1 without gaps");
                if (ranks.Count > 0 && xp is { } now && ranks[^1].XpToNext is { } before && now <= before) {
                    d.At(row, key + ".xp_to_next", "must grow from rank to rank");
                }
                ranks.Add(new GardenRankRow(rank, xp));
            }
            if (ranks.Count != maxRank) d.At(r, "ranks.levels", $"lists {ranks.Count} ranks; max_rank is {maxRank}");
        }

        var quests = ImmutableArray<string>.Empty;
        var starter = ImmutableArray.CreateBuilder<GardenVendorSpell>();
        if (Section(map, "tutorial", s_tutorialKeys, ["quests"], d) is { } t) {
            quests = Strings(t, "quests", "tutorial", d);
            foreach (var (row, key) in Rows(t, "starter_spells", d, "tutorial.starter_spells", allowEmpty: true)) {
                d.CheckKeys(row, key, s_spellKeys, ["template", "name"]);
                starter.Add(new GardenVendorSpell((uint) Int(row, "template", key, 1, int.MaxValue, d), Text(row, "name", key, d) ?? "",
                    row.Find("rank") is { } rk ? d.ReadInt(rk.Value, key + ".rank", 1, 100) ?? 1 : 1, 0));
            }
        }

        var vendors = ImmutableArray.CreateBuilder<GardenVendor>();
        foreach (var (row, key) in Rows(map, "vendors", d)) {
            d.CheckKeys(row, key, s_vendorKeys, ["npc", "name", "zone", "stock"]);
            var npc = (uint) Int(row, "npc", key, 1, int.MaxValue, d);
            var layout = row.Find("shop_layout") is { } sl ? d.ReadInt(sl.Value, key + ".shop_layout", 0, 16) ?? 0 : 0;
            var stock = ImmutableArray.CreateBuilder<GardenVendorItem>();
            foreach (var (item, ik) in Rows(row, "stock", d, key + ".stock", allowEmpty: true)) {
                d.CheckKeys(item, ik, s_itemKeys, ["template", "name"]);
                stock.Add(new GardenVendorItem((uint) Int(item, "template", ik, 1, int.MaxValue, d), Text(item, "name", ik, d) ?? ""));
            }
            var spells = ImmutableArray.CreateBuilder<GardenVendorSpell>();
            foreach (var (spell, sk) in Rows(row, "spells", d, key + ".spells", allowEmpty: true)) {
                d.CheckKeys(spell, sk, s_spellKeys, ["template", "name", "rank", "gold"]);
                spells.Add(new GardenVendorSpell((uint) Int(spell, "template", sk, 1, int.MaxValue, d), Text(spell, "name", sk, d) ?? "",
                    Int(spell, "rank", sk, 1, 100, d), Int(spell, "gold", sk, 0, 10_000_000, d)));
            }
            if (vendors.Any(v => v.Npc == npc)) d.At(row, key + ".npc", "repeats another vendor");
            if (stock.Select(s => s.Template).Distinct().Count() != stock.Count) d.At(row, key + ".stock", "lists an item twice");
            if (spells.Select(s => s.Template).Distinct().Count() != spells.Count) d.At(row, key + ".spells", "lists a spell twice");
            vendors.Add(new GardenVendor(npc, Text(row, "name", key, d) ?? "", Text(row, "zone", key, d) ?? "", layout,
                stock.ToImmutable(), spells.ToImmutable()));
        }

        var likes = GardeningTables.Empty.Likes;
        if (Section(map, "likes", s_likesKeys, ["radius", "min_modifier", "max_modifier"], d) is { } l) {
            likes = new GardenLikesRule(Float(l, "radius", "likes", 0, 100_000, d), Float(l, "min_modifier", "likes", 0.01, 100, d),
                Float(l, "max_modifier", "likes", 0.01, 100, d));
            if (likes.MinModifier > likes.MaxModifier) d.At(l, "likes", "min_modifier is above max_modifier");
        }

        var pests = GardeningTables.Empty.Pests;
        if (Section(map, "pests", s_pestKeys, ["enabled"], d) is { } p) {
            pests = new GardenPestRule(p.Find("enabled") is { } en && d.ReadBool(en.Value, "pests.enabled") == true,
                p.Find("chance_scale") is not null ? Float(p, "chance_scale", "pests", 0, 100, d) : 1);
        }

        var harvests = ImmutableArray.CreateBuilder<GardenHarvestTable>();
        foreach (var (row, key) in Rows(map, "harvests", d, allowEmpty: true)) {
            d.CheckKeys(row, key, s_harvestKeys, ["plant", "mature", "elder"]);
            var mature = Ids(row, "mature", key, d);
            var elder = Ids(row, "elder", key, d);
            var table = new GardenHarvestTable(Text(row, "plant", key, d) ?? "", mature, elder,
                Rewards(row, "mature_rewards", key, d), Rewards(row, "elder_rewards", key, d)) {
                PickOne = row.Find("pick") is { } pick && d.ReadEnum(pick.Value, key + ".pick", ["one", "each"]) == "one",
            };
            if (harvests.Any(h => h.Mature.Concat(h.Elder).Intersect(mature.Concat(elder)).Any())) {
                d.At(row, key, "a plant template is in two harvest tables");
            }
            harvests.Add(table);
        }

        if (d.HasErrors) throw d.ToException();
        return new GardeningTables(id, profiles, energy, plantSeed, maxRank, ranks.ToImmutable(), quests, starter.ToImmutable(),
            vendors.ToImmutable(), likes, pests, harvests.ToImmutable(), display);
    }

    private static ImmutableArray<GardenReward> Rewards(YMap row, string name, string key, YamlDiagnostics d) {
        var list = ImmutableArray.CreateBuilder<GardenReward>();
        foreach (var (r, rk) in Rows(row, name, d, YamlTree.Join(key, name), allowEmpty: true)) {
            d.CheckKeys(r, rk, s_rewardKeys, ["kind", "chance"]);
            var kind = r.Find("kind") is { } k ? d.ReadEnum(k.Value, rk + ".kind", s_kinds) : null;
            var template = r.Find("template") is { } tp ? (uint) (d.ReadInt(tp.Value, rk + ".template", 1) ?? 0) : 0u;
            var chance = r.Find("chance") is { } c ? d.ReadFraction(c.Value, rk + ".chance") ?? 0 : 0;
            var min = r.Find("min") is { } mn ? d.ReadInt(mn.Value, rk + ".min", 0, 1_000_000) ?? 1 : 1;
            var max = r.Find("max") is { } mx ? d.ReadInt(mx.Value, rk + ".max", 0, 1_000_000) ?? min : min;
            if (max < min) d.At(r, rk + ".max", "is below min");
            if (kind is not null && kind != "gold" && template == 0) d.At(r, rk, "needs a template");
            if (kind is not null) {
                list.Add(new GardenReward(Enum.Parse<GardenRewardKind>(kind, ignoreCase: true), template,
                    r.Find("name") is { } nm ? d.ReadString(nm.Value, rk + ".name") ?? "" : "", chance, min, max));
            }
        }
        return list.ToImmutable();
    }

    private static YMap? Section(YMap map, string name, FrozenSet<string> keys, string[] required, YamlDiagnostics d) {
        if (map.Find(name) is not { } entry || d.ReadMap(entry.Value, name) is not { } section) return null;
        d.CheckKeys(section, name, keys, required);
        return section;
    }

    private static int Int(YMap map, string name, string key, int min, int max, YamlDiagnostics d)
        => map.Find(name) is { } e ? d.ReadInt(e.Value, YamlTree.Join(key, name), min, max) ?? min : min;

    private static float Float(YMap map, string name, string key, double min, double max, YamlDiagnostics d) {
        var path = YamlTree.Join(key, name);
        if (map.Find(name) is not { } e) return (float) min;
        if (e.Value is YScalar { IsPlain: true } s && double.TryParse(s.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) {
            return (float) v;
        }
        d.At(e.Value, path, $"expected a number from {min} to {max}");
        return (float) min;
    }

    private static ImmutableArray<uint> Ids(YMap map, string name, string key, YamlDiagnostics d) {
        var path = YamlTree.Join(key, name);
        if (map.Find(name) is not { } entry || d.ReadList(entry.Value, path) is not { } list) return [];
        var ids = ImmutableArray.CreateBuilder<uint>();
        for (var i = 0; i < list.Items.Length; i++) {
            if (d.ReadInt(list.Items[i], YamlTree.Index(path, i), 1) is { } v) ids.Add((uint) v);
        }
        return ids.ToImmutable();
    }

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
