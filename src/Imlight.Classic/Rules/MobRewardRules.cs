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
 * CLASSIC MOB REWARDS
 * ========================================================================
 *
 * PURPOSE:
 * A profile's combat reward rules (progression/mob-rewards-2009.yaml):
 * XP per pip used in a won duel, gold per defeated mob by rank and kind
 * (or the mob's own documented range), and item drops from the mob's
 * documented loot list.
 *
 * USAGE EXAMPLE:
 * var rules = MobRewardRulesLoader.Load(path);
 * var pips = rules.CombatXp.PipsForCast(rank: 0, isXPip: false, xPipsSpent: 0);   // 1
 * var loot = rules.Roll(new MobInfo(35785, Rank: 1, MobKind.Normal), random);      // gold 1-2, maybe an item
 *
 * NOTE:
 * A mob's rank is its template's NPC level (the 2009 wiki's "Rank"). A
 * documented mob's gold range and loot list win over the rank table; an
 * undocumented mob gets the rank table's gold and no items. Pure logic, no
 * server types, so the tests can drive it.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Spells;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// How a mob is classed for rewards.
/// </summary>
public enum MobKind {
    Normal,
    Elite,
    Boss,
}

/// <summary>
/// A defeated mob, as the reward rules see it.
/// </summary>
/// <param name="TemplateId">The mob's template id.</param>
/// <param name="Rank">The template's NPC level (the 2009 rank).</param>
/// <param name="Kind">Normal, elite or boss.</param>
public sealed record MobInfo(ulong TemplateId, int Rank, MobKind Kind);

/// <summary>
/// An inclusive gold range.
/// </summary>
public readonly record struct GoldRange(int Min, int Max) {

    public int Roll(Random random) => Max <= Min ? Min : random.Next(Min, Max + 1);

}

/// <summary>
/// Gold ranges for one rank.
/// </summary>
public sealed record RankGold(int Rank, GoldRange Normal, GoldRange Elite, GoldRange Boss) {

    public GoldRange For(MobKind kind) => kind switch {
        MobKind.Boss => Boss,
        MobKind.Elite => Elite,
        _ => Normal,
    };

}

/// <summary>
/// A documented mob: its 2009 wiki page and the client templates it covers.
/// </summary>
/// <param name="Name">The wiki name.</param>
/// <param name="Templates">Client template ids with this name.</param>
/// <param name="Rank">The 2009 rank.</param>
/// <param name="Kind">Normal, elite or boss.</param>
/// <param name="Gold">The documented gold range, if the page gave one.</param>
/// <param name="Drops">Client item template ids of the documented loot that exist in the client.</param>
public sealed record DocumentedMob(string Name, ImmutableArray<ulong> Templates, int Rank, MobKind Kind, GoldRange? Gold,
                                   ImmutableArray<ulong> Drops);

/// <summary>
/// XP from a won duel: <see cref="XpPerPip"/> for every pip of every card played.
/// </summary>
/// <param name="XpPerPip">XP per counted pip.</param>
/// <param name="ZeroPipCountsAs">Pips a 0-pip card counts as.</param>
/// <param name="XPipCountsAs">Pips an X-pip card counts as; null to count the pips it spent.</param>
/// <param name="FizzleCountsCard">True: a fizzled card counts like a cast one; false: it counts as one pip.</param>
public sealed record CombatXpRule(int XpPerPip, int ZeroPipCountsAs, int? XPipCountsAs, bool FizzleCountsCard) {

    /// <summary>
    /// Imlight's own counting: 0-pip cards 1, X-pip cards what they spent, a fizzle 1.
    /// </summary>
    public static CombatXpRule Stock { get; } = new(3, 1, null, false);

    /// <summary>
    /// The pips a cast card counts for XP.
    /// </summary>
    /// <param name="rank">The card's pip cost (its rank).</param>
    /// <param name="isXPip">True for an X-pip card.</param>
    /// <param name="xPipsSpent">The pips an X-pip card spent.</param>
    public int PipsForCast(int rank, bool isXPip, int xPipsSpent) {
        if (isXPip) {
            return XPipCountsAs ?? xPipsSpent;
        }

        return rank <= 0 ? ZeroPipCountsAs : rank;
    }

    /// <summary>
    /// The pips a fizzled card counts for XP.
    /// </summary>
    /// <param name="rank">The card's pip cost (its rank).</param>
    /// <param name="isXPip">True for an X-pip card.</param>
    public int PipsForFizzle(int rank, bool isXPip) {
        if (!FizzleCountsCard) {
            return 1;
        }

        return isXPip ? XPipCountsAs ?? 1 : rank <= 0 ? ZeroPipCountsAs : rank;
    }

    /// <summary>
    /// XP for the counted pips.
    /// </summary>
    public int Xp(int pips) => Math.Max(0, pips) * XpPerPip;

}

/// <summary>
/// The loot one defeated mob gives one player.
/// </summary>
/// <param name="Gold">Gold.</param>
/// <param name="Item">An item template id, or null for no item.</param>
public sealed record MobLoot(int Gold, ulong? Item);

/// <summary>
/// A profile's combat XP, mob gold and drop rules.
/// </summary>
public sealed class MobRewardRules {

    public required string Id { get; init; }
    public string? Title { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required CombatXpRule CombatXp { get; init; }

    /// <summary>
    /// Gold per rank; ranks past the last entry use the last entry.
    /// </summary>
    public required ImmutableSortedDictionary<int, RankGold> GoldByRank { get; init; }

    /// <summary>
    /// Chance a documented mob drops one item from its loot list, per kind.
    /// </summary>
    public required ImmutableDictionary<MobKind, double> DropChance { get; init; }

    public required ImmutableArray<DocumentedMob> Mobs { get; init; }
    public required string SourceFile { get; init; }

    private FrozenDictionary<ulong, DocumentedMob>? _byTemplate;

    private FrozenDictionary<ulong, DocumentedMob> ByTemplate
        => _byTemplate ??= Mobs.SelectMany(mob => mob.Templates.Select(tid => (tid, mob)))
            .GroupBy(pair => pair.tid).ToFrozenDictionary(group => group.Key, group => group.First().mob);

    /// <summary>
    /// The number of templates the documented mobs cover.
    /// </summary>
    public int MobCount => ByTemplate.Count;

    /// <summary>
    /// The documented mob a template belongs to, if any.
    /// </summary>
    public DocumentedMob? Find(ulong templateId) => ByTemplate.GetValueOrDefault(templateId);

    /// <summary>
    /// The gold range for a mob: its documented range, else the rank table's.
    /// </summary>
    public GoldRange GoldFor(MobInfo mob) {
        if (Find(mob.TemplateId) is { Gold: { } documented }) {
            return documented;
        }

        if (GoldByRank.Count == 0) {
            return new GoldRange(0, 0);
        }

        var rank = Math.Max(1, mob.Rank);
        RankGold? row = null;
        foreach (var (key, value) in GoldByRank) {
            if (key > rank) {
                break;
            }

            row = value;
        }

        return (row ?? GoldByRank.Values.First()).For(mob.Kind);
    }

    /// <summary>
    /// Rolls one player's loot from one defeated mob.
    /// </summary>
    /// <param name="mob">The defeated mob.</param>
    /// <param name="random">The random source.</param>
    public MobLoot Roll(MobInfo mob, Random random) {
        var gold = GoldFor(mob).Roll(random);
        ulong? item = null;
        if (Find(mob.TemplateId) is { Drops.IsEmpty: false } documented
            && random.NextDouble() < DropChance.GetValueOrDefault(mob.Kind)) {
            item = documented.Drops[random.Next(documented.Drops.Length)];
        }

        return new MobLoot(gold, item);
    }

}

/// <summary>
/// Loads and validates mob reward rules.
/// </summary>
public static class MobRewardRulesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "combat_xp", "gold", "drops", "mobs", "later_changes", "license_tag", "notes");
    private static readonly string[] s_rootRequired = ["id", "profiles", "combat_xp", "gold", "drops", "mobs", "license_tag"];
    internal static readonly FrozenSet<string> s_combatKeys = FrozenSet.Create(StringComparer.Ordinal,
        "xp_per_pip", "zero_pip_counts_as", "x_pip_counts_as", "fizzle_counts", "provenance", "notes");
    internal static readonly FrozenSet<string> s_goldKeys = FrozenSet.Create(StringComparer.Ordinal,
        "by_rank", "provenance", "notes");
    internal static readonly FrozenSet<string> s_rankKeys = FrozenSet.Create(StringComparer.Ordinal,
        "rank", "normal", "elite", "boss", "sample", "confidence", "notes");
    internal static readonly FrozenSet<string> s_dropKeys = FrozenSet.Create(StringComparer.Ordinal,
        "chance", "provenance", "confidence", "notes");
    internal static readonly FrozenSet<string> s_mobKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "templates", "rank", "kind", "gold", "drops", "source", "notes");
    internal static readonly FrozenSet<string> s_itemKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "template");
    private static readonly string[] s_fizzle = ["card", "one"];
    private static readonly string[] s_kinds = ["normal", "elite", "boss"];
    private static readonly string[] s_confidences = ["verified", "corroborated", "inferred", "placeholder"];

    private static readonly Regex s_id = new(@"^mob-rewards-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the rules at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The file, such as classic-data/progression/mob-rewards-2009.yaml.</param>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static MobRewardRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the mob reward rules do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is null) {
            throw diagnostics.ToException();
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, s_rootRequired);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be mob-rewards-<name> and match the file name '{expectedId}'");
        }

        var title = map.Find("title") is { } titleEntry ? diagnostics.ReadString(titleEntry.Value, "title") : null;
        var profiles = ReadProfiles(map, diagnostics);
        var combat = ReadCombatXp(map, diagnostics);
        var gold = ReadGold(map, diagnostics);
        var drops = ReadDrops(map, diagnostics);
        var mobs = ReadMobs(map, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        if (map.Find("later_changes") is { } changes) {
            _ = diagnostics.ReadList(changes.Value, "later_changes");
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new MobRewardRules {
            Id = id!,
            Title = title,
            Profiles = profiles,
            CombatXp = combat!,
            GoldByRank = gold,
            DropChance = drops,
            Mobs = mobs,
            SourceFile = display,
        };
    }

    private static ImmutableArray<string> ReadProfiles(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("profiles") is not { } entry || diagnostics.ReadList(entry.Value, "profiles") is not { } list) {
            return [];
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, "profiles", "needs at least one profile");
        }

        var profiles = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("profiles", i);
            if (diagnostics.ReadString(list.Items[i], keyPath) is not { } profile) {
                continue;
            }

            if (!ClassicSchema.IsValidId(profile) || profiles.Contains(profile)) {
                diagnostics.At(list.Items[i], keyPath, $"'{profile}' is not a valid, unrepeated profile id");
                continue;
            }

            profiles.Add(profile);
        }

        return profiles.ToImmutable();
    }

    private static void RequireProvenance(YMap map, string keyPath, YamlDiagnostics diagnostics) {
        if (map.Find("provenance") is { } provenance
            && diagnostics.ReadList(provenance.Value, YamlTree.Join(keyPath, "provenance")) is { Items.IsEmpty: true } empty) {
            diagnostics.At(empty, YamlTree.Join(keyPath, "provenance"), "needs at least one source");
        }
    }

    private static CombatXpRule? ReadCombatXp(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("combat_xp") is not { } entry || diagnostics.ReadMap(entry.Value, "combat_xp") is not { } combat) {
            return null;
        }

        diagnostics.CheckKeys(combat, "combat_xp", s_combatKeys, ["xp_per_pip", "zero_pip_counts_as", "x_pip_counts_as", "fizzle_counts", "provenance"]);
        RequireProvenance(combat, "combat_xp", diagnostics);
        var perPip = combat.Find("xp_per_pip") is { } p ? diagnostics.ReadInt(p.Value, "combat_xp.xp_per_pip", 0, 1000) : null;
        var zero = combat.Find("zero_pip_counts_as") is { } z ? diagnostics.ReadInt(z.Value, "combat_xp.zero_pip_counts_as", 0, 14) : null;
        int? xPip = null;
        if (combat.Find("x_pip_counts_as") is { } x && x.Value is not YNull) {
            xPip = diagnostics.ReadInt(x.Value, "combat_xp.x_pip_counts_as", 0, 14);
        }

        var fizzle = combat.Find("fizzle_counts") is { } f ? diagnostics.ReadEnum(f.Value, "combat_xp.fizzle_counts", s_fizzle) : null;
        if (combat.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "combat_xp.notes");
        }

        return perPip is null || zero is null || fizzle is null
            ? null
            : new CombatXpRule(perPip.Value, zero.Value, xPip, fizzle == "card");
    }

    private static GoldRange? ReadRange(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return null;
        }

        if (list.Items.Length != 2) {
            diagnostics.At(list, keyPath, "a gold range is [min, max]");

            return null;
        }

        var min = diagnostics.ReadInt(list.Items[0], YamlTree.Index(keyPath, 0), 0, 1_000_000);
        var max = diagnostics.ReadInt(list.Items[1], YamlTree.Index(keyPath, 1), 0, 1_000_000);
        if (min is null || max is null) {
            return null;
        }

        if (min > max) {
            diagnostics.At(list, keyPath, $"min {min} is above max {max}");

            return null;
        }

        return new GoldRange(min.Value, max.Value);
    }

    private static ImmutableSortedDictionary<int, RankGold> ReadGold(YMap map, YamlDiagnostics diagnostics) {
        var result = ImmutableSortedDictionary.CreateBuilder<int, RankGold>();
        if (map.Find("gold") is not { } entry || diagnostics.ReadMap(entry.Value, "gold") is not { } gold) {
            return result.ToImmutable();
        }

        diagnostics.CheckKeys(gold, "gold", s_goldKeys, ["by_rank", "provenance"]);
        RequireProvenance(gold, "gold", diagnostics);
        if (gold.Find("by_rank") is not { } byRankEntry || diagnostics.ReadList(byRankEntry.Value, "gold.by_rank") is not { } rows) {
            return result.ToImmutable();
        }

        if (rows.Items.IsEmpty) {
            diagnostics.At(rows, "gold.by_rank", "needs at least one rank");
        }

        for (var i = 0; i < rows.Items.Length; i++) {
            var keyPath = YamlTree.Index("gold.by_rank", i);
            if (diagnostics.ReadMap(rows.Items[i], keyPath) is not { } row) {
                continue;
            }

            diagnostics.CheckKeys(row, keyPath, s_rankKeys, ["rank", "normal", "elite", "boss", "confidence"]);
            var rank = row.Find("rank") is { } r ? diagnostics.ReadInt(r.Value, YamlTree.Join(keyPath, "rank"), 1, 100) : null;
            var normal = row.Find("normal") is { } n ? ReadRange(n.Value, YamlTree.Join(keyPath, "normal"), diagnostics) : null;
            var elite = row.Find("elite") is { } e ? ReadRange(e.Value, YamlTree.Join(keyPath, "elite"), diagnostics) : null;
            var boss = row.Find("boss") is { } b ? ReadRange(b.Value, YamlTree.Join(keyPath, "boss"), diagnostics) : null;
            if (row.Find("sample") is { } s) {
                _ = diagnostics.ReadInt(s.Value, YamlTree.Join(keyPath, "sample"), 0);
            }

            if (row.Find("confidence") is { } c) {
                _ = diagnostics.ReadEnum(c.Value, YamlTree.Join(keyPath, "confidence"), s_confidences);
            }

            if (rank is null || normal is null || elite is null || boss is null) {
                continue;
            }

            if (result.ContainsKey(rank.Value)) {
                diagnostics.At(row, keyPath, $"rank {rank} repeats");
                continue;
            }

            result[rank.Value] = new RankGold(rank.Value, normal.Value, elite.Value, boss.Value);
        }

        return result.ToImmutable();
    }

    private static ImmutableDictionary<MobKind, double> ReadDrops(YMap map, YamlDiagnostics diagnostics) {
        var result = ImmutableDictionary.CreateBuilder<MobKind, double>();
        if (map.Find("drops") is not { } entry || diagnostics.ReadMap(entry.Value, "drops") is not { } drops) {
            return result.ToImmutable();
        }

        diagnostics.CheckKeys(drops, "drops", s_dropKeys, ["chance", "confidence"]);
        RequireProvenance(drops, "drops", diagnostics);
        if (drops.Find("confidence") is { } c) {
            _ = diagnostics.ReadEnum(c.Value, "drops.confidence", s_confidences);
        }

        if (drops.Find("chance") is { } chanceEntry && diagnostics.ReadMap(chanceEntry.Value, "drops.chance") is { } chance) {
            diagnostics.CheckKeys(chance, "drops.chance", s_kinds.ToFrozenSet(StringComparer.Ordinal), s_kinds);
            foreach (var kind in s_kinds) {
                if (chance.Find(kind) is { } value
                    && diagnostics.ReadFraction(value.Value, YamlTree.Join("drops.chance", kind)) is { } fraction) {
                    result[ParseKind(kind)] = fraction;
                }
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<DocumentedMob> ReadMobs(YMap map, YamlDiagnostics diagnostics) {
        var result = ImmutableArray.CreateBuilder<DocumentedMob>();
        if (map.Find("mobs") is not { } entry || diagnostics.ReadList(entry.Value, "mobs") is not { } list) {
            return result.ToImmutable();
        }

        var seen = new HashSet<ulong>();
        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("mobs", i);
            if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } mob) {
                continue;
            }

            diagnostics.CheckKeys(mob, keyPath, s_mobKeys, ["name", "templates", "rank", "kind", "source"]);
            var name = mob.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "name")) : null;
            var rank = mob.Find("rank") is { } r ? diagnostics.ReadInt(r.Value, YamlTree.Join(keyPath, "rank"), 1, 100) : null;
            var kind = mob.Find("kind") is { } k ? diagnostics.ReadEnum(k.Value, YamlTree.Join(keyPath, "kind"), s_kinds) : null;
            var gold = mob.Find("gold") is { } g ? ReadRange(g.Value, YamlTree.Join(keyPath, "gold"), diagnostics) : null;
            var templates = ReadIds(mob, "templates", keyPath, diagnostics, minOne: true);
            foreach (var tid in templates) {
                if (!seen.Add(tid)) {
                    diagnostics.At(mob, keyPath, $"template {tid} belongs to two mobs");
                }
            }

            var drops = ImmutableArray.CreateBuilder<ulong>();
            if (mob.Find("drops") is { } d && diagnostics.ReadList(d.Value, YamlTree.Join(keyPath, "drops")) is { } items) {
                for (var j = 0; j < items.Items.Length; j++) {
                    var itemPath = YamlTree.Index(YamlTree.Join(keyPath, "drops"), j);
                    if (diagnostics.ReadMap(items.Items[j], itemPath) is not { } item) {
                        continue;
                    }

                    diagnostics.CheckKeys(item, itemPath, s_itemKeys, ["name"]);
                    if (item.Find("name") is { } itemName) {
                        _ = diagnostics.ReadString(itemName.Value, YamlTree.Join(itemPath, "name"));
                    }

                    if (item.Find("template") is { } template && ReadId(template.Value, YamlTree.Join(itemPath, "template"), diagnostics) is { } id) {
                        drops.Add(id);
                    }
                }
            }

            if (mob.Find("source") is { } source) {
                _ = diagnostics.ReadMap(source.Value, YamlTree.Join(keyPath, "source"));
            }

            if (name is null || rank is null || kind is null) {
                continue;
            }

            result.Add(new DocumentedMob(name, templates, rank.Value, ParseKind(kind), gold, drops.ToImmutable()));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<ulong> ReadIds(YMap map, string key, string parentPath, YamlDiagnostics diagnostics, bool minOne) {
        var keyPath = YamlTree.Join(parentPath, key);
        var ids = ImmutableArray.CreateBuilder<ulong>();
        if (map.Find(key) is not { } entry || diagnostics.ReadList(entry.Value, keyPath) is not { } list) {
            return ids.ToImmutable();
        }

        if (minOne && list.Items.IsEmpty) {
            diagnostics.At(list, keyPath, "needs at least one template id");
        }

        for (var i = 0; i < list.Items.Length; i++) {
            if (ReadId(list.Items[i], YamlTree.Index(keyPath, i), diagnostics) is { } id) {
                ids.Add(id);
            }
        }

        return ids.ToImmutable();
    }

    private static ulong? ReadId(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (node is YScalar { IsPlain: true } scalar
            && ulong.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) {
            return id;
        }

        diagnostics.At(node, keyPath, $"expected a template id, got {node.Describe()}");

        return null;
    }

    internal static MobKind ParseKind(string kind) => kind switch {
        "boss" => MobKind.Boss,
        "elite" => MobKind.Elite,
        _ => MobKind.Normal,
    };

}
