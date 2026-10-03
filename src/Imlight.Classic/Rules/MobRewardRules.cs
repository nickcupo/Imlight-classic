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
 * (or the mob's own documented range), and equipment, Treasure Card and
 * reagent drops from the mob's documented loot lists.
 *
 * USAGE EXAMPLE:
 * var rules = MobRewardRulesLoader.Load(path);
 * var pips = rules.CombatXp.PipsForCast(rank: 0, isXPip: false, xPipsSpent: 0);   // 1
 * var loot = rules.Roll(new MobInfo(35785, Rank: 1, MobKind.Normal), random);      // gold 1-2, maybe items, cards, reagents
 *
 * NOTE:
 * A mob's rank is its template's NPC level (the 2009 wiki's "Rank"). A
 * documented mob's gold range and loot list win over the rank table; an
 * undocumented mob gets the rank table's gold and no items. Every list entry
 * rolls on its own: a tallied entry at its tallied chance, an untallied one at
 * the kind's expected drops per fight divided by the list length, capped per
 * mob. Treasure Cards and reagents fall back to the client's loot-table names
 * when the mob's page lists none. Pure logic, no server types, so the tests
 * can drive it.
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
/// <param name="ListedItems">How many equipment, pet and housing items the page lists, with a client template or not.</param>
/// <param name="Items">The listed items that exist in the client.</param>
/// <param name="TreasureCards">The listed Treasure Cards (spell templates).</param>
/// <param name="Reagents">The listed reagents.</param>
/// <param name="Tally">Fights behind the page's drop percentages, or null when it has none.</param>
public sealed record DocumentedMob(string Name, ImmutableArray<ulong> Templates, int Rank, MobKind Kind, GoldRange? Gold,
                                   int ListedItems, ImmutableArray<DropEntry> Items, ImmutableArray<DropEntry> TreasureCards,
                                   ImmutableArray<DropEntry> Reagents, int? Tally = null) {

    /// <summary>
    /// The item templates of the documented loot.
    /// </summary>
    public ImmutableArray<ulong> Drops => [.. Items.Select(entry => entry.Template)];

}

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
/// One entry of a mob's documented loot list.
/// </summary>
/// <param name="Template">The client template (an item, a Treasure Card spell or a reagent).</param>
/// <param name="Chance">The entry's own chance per fight from a drop tally (0: listed but not seen in the tally), or null to
/// derive it from the kind's rate.</param>
public sealed record DropEntry(ulong Template, double? Chance);

/// <summary>
/// One kind of loot (equipment, Treasure Cards or reagents) and how often a mob drops it.
/// </summary>
/// <param name="Expected">Expected drops of this kind per defeated mob and player, by mob kind.</param>
/// <param name="MaxPerMob">The most drops of this kind one mob gives one player in one fight.</param>
/// <param name="UnseenChance">The chance of a listed entry that a mob's drop tally never saw.</param>
/// <param name="Quantity">How many of the template one drop gives.</param>
public sealed record DropRule(ImmutableDictionary<MobKind, double> Expected, int MaxPerMob, double UnseenChance, GoldRange Quantity) {

    /// <summary>
    /// No drops of this kind.
    /// </summary>
    public static DropRule None { get; } = new(ImmutableDictionary<MobKind, double>.Empty, 0, 0, new GoldRange(1, 1));

    /// <summary>
    /// The chance per fight of one list entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="kind">The mob's kind.</param>
    /// <param name="listed">How many entries the mob's list has, templates or not.</param>
    /// <param name="tallied">True when the page gave entries of this list their own percentage.</param>
    public double ChanceOf(DropEntry entry, MobKind kind, int listed, bool tallied) {
        if (entry.Chance is { } own) {
            return own > 0 ? own : UnseenChance;   // 0: on the tally's list but never seen in its fights
        }

        if (tallied) {
            return UnseenChance;
        }

        return listed <= 0 ? 0 : Math.Min(1.0, Expected.GetValueOrDefault(kind) / listed);
    }

    /// <summary>
    /// Rolls every entry of a list on its own, in random order, and keeps at most <see cref="MaxPerMob"/> hits.
    /// </summary>
    /// <param name="multiplier">CLASSIC: [Classic] DropRateMultiplier; each chance is multiplied, at most 100%.</param>
    public ImmutableArray<ulong> Roll(ImmutableArray<DropEntry> entries, MobKind kind, int listed, bool tallied, Random random,
                                      double multiplier = 1.0) {
        if (entries.IsDefaultOrEmpty || MaxPerMob <= 0) {
            return [];
        }

        var order = entries.ToArray();
        random.Shuffle(order);
        var hits = ImmutableArray.CreateBuilder<ulong>();
        foreach (var entry in order) {
            if (hits.Count >= MaxPerMob) {
                break;
            }

            if (random.NextDouble() < Math.Min(1.0, ChanceOf(entry, kind, listed, tallied) * Math.Max(0, multiplier))) {
                hits.Add(entry.Template);
            }
        }

        return hits.ToImmutable();
    }

}

/// <summary>
/// A reagent drop.
/// </summary>
/// <param name="Template">The reagent's client template.</param>
/// <param name="Quantity">How many.</param>
public readonly record struct ReagentDrop(ulong Template, int Quantity);

/// <summary>
/// The loot one defeated mob gives one player.
/// </summary>
/// <param name="Gold">Gold.</param>
/// <param name="Items">Item templates (equipment, pets, housing).</param>
/// <param name="TreasureCards">Treasure Card spell templates.</param>
/// <param name="Reagents">Reagents.</param>
public sealed record MobLoot(int Gold, ImmutableArray<ulong> Items, ImmutableArray<ulong> TreasureCards, ImmutableArray<ReagentDrop> Reagents) {

    /// <summary>
    /// The first item, if any.
    /// </summary>
    public ulong? Item => Items.IsDefaultOrEmpty ? null : Items[0];

}

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
    /// Equipment, pet and housing drops.
    /// </summary>
    public required DropRule ItemDrops { get; init; }

    /// <summary>
    /// Treasure Card drops.
    /// </summary>
    public DropRule TreasureCardDrops { get; init; } = DropRule.None;

    /// <summary>
    /// Reagent drops.
    /// </summary>
    public DropRule ReagentDrops { get; init; } = DropRule.None;

    /// <summary>
    /// Treasure Cards for mob templates whose page lists none, from the client's loot-table names.
    /// </summary>
    public ImmutableDictionary<ulong, ImmutableArray<DropEntry>> TreasureCardFallback { get; init; } =
        ImmutableDictionary<ulong, ImmutableArray<DropEntry>>.Empty;

    /// <summary>
    /// Reagents for mob templates whose page lists none, from the client's loot-table names.
    /// </summary>
    public ImmutableDictionary<ulong, ImmutableArray<DropEntry>> ReagentFallback { get; init; } =
        ImmutableDictionary<ulong, ImmutableArray<DropEntry>>.Empty;

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
    /// Rolls one player's loot from one defeated mob: gold always; items, Treasure Cards and reagents each rolled
    /// entry by entry from the mob's lists (or, for cards and reagents, the client loot-table fallback).
    /// </summary>
    /// <param name="mob">The defeated mob.</param>
    /// <param name="random">The random source.</param>
    /// <param name="dropMultiplier">CLASSIC: [Classic] DropRateMultiplier (2009: 1).</param>
    public MobLoot Roll(MobInfo mob, Random random, double dropMultiplier = 1.0) {
        var gold = GoldFor(mob).Roll(random);
        var documented = Find(mob.TemplateId);

        // A list counts as tallied when the page gave any of its entries a percentage.
        static bool Tallied(ImmutableArray<DropEntry> entries) => entries.Any(entry => entry.Chance is not null);

        var items = documented is null
            ? []
            : ItemDrops.Roll(documented.Items, mob.Kind, documented.ListedItems, Tallied(documented.Items), random, dropMultiplier);

        var cards = documented is { TreasureCards.IsEmpty: false }
            ? TreasureCardDrops.Roll(documented.TreasureCards, mob.Kind, documented.TreasureCards.Length, Tallied(documented.TreasureCards), random, dropMultiplier)
            : TreasureCardFallback.TryGetValue(mob.TemplateId, out var cardFallback)
                ? TreasureCardDrops.Roll(cardFallback, mob.Kind, cardFallback.Length, tallied: false, random, dropMultiplier)
                : [];

        var reagentTemplates = documented is { Reagents.IsEmpty: false }
            ? ReagentDrops.Roll(documented.Reagents, mob.Kind, documented.Reagents.Length, Tallied(documented.Reagents), random, dropMultiplier)
            : ReagentFallback.TryGetValue(mob.TemplateId, out var reagentFallback)
                ? ReagentDrops.Roll(reagentFallback, mob.Kind, reagentFallback.Length, tallied: false, random, dropMultiplier)
                : [];
        var reagents = reagentTemplates.Select(template => new ReagentDrop(template, ReagentDrops.Quantity.Roll(random)))
            .ToImmutableArray();

        return new MobLoot(gold, items, cards, reagents);
    }

}

/// <summary>
/// Loads and validates mob reward rules.
/// </summary>
public static class MobRewardRulesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "combat_xp", "gold", "drops", "treasure_cards", "reagents", "mobs", "later_changes", "license_tag", "notes");
    private static readonly string[] s_rootRequired = ["id", "profiles", "combat_xp", "gold", "drops", "mobs", "license_tag"];
    internal static readonly FrozenSet<string> s_combatKeys = FrozenSet.Create(StringComparer.Ordinal,
        "xp_per_pip", "zero_pip_counts_as", "x_pip_counts_as", "fizzle_counts", "provenance", "notes");
    internal static readonly FrozenSet<string> s_goldKeys = FrozenSet.Create(StringComparer.Ordinal,
        "by_rank", "provenance", "notes");
    internal static readonly FrozenSet<string> s_rankKeys = FrozenSet.Create(StringComparer.Ordinal,
        "rank", "normal", "elite", "boss", "sample", "confidence", "notes");
    internal static readonly FrozenSet<string> s_dropKeys = FrozenSet.Create(StringComparer.Ordinal,
        "expected", "max_per_mob", "unseen_chance", "tallies", "provenance", "confidence", "notes");
    internal static readonly FrozenSet<string> s_extraDropKeys = FrozenSet.Create(StringComparer.Ordinal,
        "expected", "max_per_mob", "unseen_chance", "quantity", "client_tables", "provenance", "confidence", "notes");
    internal static readonly FrozenSet<string> s_clientTableKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "template", "mobs");
    internal static readonly FrozenSet<string> s_tallyKeys = FrozenSet.Create(StringComparer.Ordinal,
        "page", "oldid", "date", "first_oldid", "first_date", "kind", "rank", "battles", "items", "treasure_cards", "reagents", "minions", "items_alone",
        "treasure_cards_alone", "notes");
    internal static readonly FrozenSet<string> s_mobKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "templates", "rank", "kind", "gold", "drops", "treasure_cards", "reagents", "tally", "source", "notes");
    internal static readonly FrozenSet<string> s_itemKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "template", "chance");
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
        var drops = ReadDropRule(map, "drops", s_dropKeys, diagnostics, out _);
        var cards = ReadDropRule(map, "treasure_cards", s_extraDropKeys, diagnostics, out var cardFallback);
        var reagents = ReadDropRule(map, "reagents", s_extraDropKeys, diagnostics, out var reagentFallback);
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
            ItemDrops = drops,
            TreasureCardDrops = cards,
            ReagentDrops = reagents,
            TreasureCardFallback = cardFallback ?? ImmutableDictionary<ulong, ImmutableArray<DropEntry>>.Empty,
            ReagentFallback = reagentFallback ?? ImmutableDictionary<ulong, ImmutableArray<DropEntry>>.Empty,
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

    private static double? ReadNumber(YNode node, string keyPath, double max, YamlDiagnostics diagnostics) {
        if (node is YScalar { IsPlain: true } scalar
            && double.TryParse(scalar.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && value >= 0 && value <= max) {
            return value;
        }

        diagnostics.At(node, keyPath, $"expected a number from 0 to {max.ToString(CultureInfo.InvariantCulture)}, got {node.Describe()}");

        return null;
    }

    private static DropRule ReadDropRule(YMap map, string key, FrozenSet<string> keys, YamlDiagnostics diagnostics,
                                         out ImmutableDictionary<ulong, ImmutableArray<DropEntry>>? fallback) {
        fallback = null;
        if (map.Find(key) is not { } entry || diagnostics.ReadMap(entry.Value, key) is not { } rule) {
            return DropRule.None;
        }

        diagnostics.CheckKeys(rule, key, keys, ["expected", "max_per_mob", "confidence"]);
        RequireProvenance(rule, key, diagnostics);
        if (rule.Find("confidence") is { } c) {
            _ = diagnostics.ReadEnum(c.Value, YamlTree.Join(key, "confidence"), s_confidences);
        }

        if (rule.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, YamlTree.Join(key, "notes"));
        }

        var expected = ImmutableDictionary.CreateBuilder<MobKind, double>();
        if (rule.Find("expected") is { } expectedEntry
            && diagnostics.ReadMap(expectedEntry.Value, YamlTree.Join(key, "expected")) is { } perKind) {
            var path = YamlTree.Join(key, "expected");
            diagnostics.CheckKeys(perKind, path, s_kinds.ToFrozenSet(StringComparer.Ordinal), s_kinds);
            foreach (var kind in s_kinds) {
                if (perKind.Find(kind) is { } value && ReadNumber(value.Value, YamlTree.Join(path, kind), 20, diagnostics) is { } number) {
                    expected[ParseKind(kind)] = number;
                }
            }
        }

        var max = rule.Find("max_per_mob") is { } m ? diagnostics.ReadInt(m.Value, YamlTree.Join(key, "max_per_mob"), 0, 50) : null;
        double unseen = 0;
        if (rule.Find("unseen_chance") is { } u && diagnostics.ReadFraction(u.Value, YamlTree.Join(key, "unseen_chance")) is { } fraction) {
            unseen = fraction;
        }

        var quantity = new GoldRange(1, 1);
        if (rule.Find("quantity") is { } q && ReadRange(q.Value, YamlTree.Join(key, "quantity"), diagnostics) is { } range) {
            if (range.Min < 1) {
                diagnostics.At(q.Value, YamlTree.Join(key, "quantity"), "a drop gives at least one");
            }

            quantity = range;
        }

        if (rule.Find("tallies") is { } tallies) {
            ReadTallies(tallies.Value, YamlTree.Join(key, "tallies"), diagnostics);
        }

        if (rule.Find("client_tables") is { } tables) {
            fallback = ReadClientTables(tables.Value, YamlTree.Join(key, "client_tables"), diagnostics);
        }

        return new DropRule(expected.ToImmutable(), max ?? 0, unseen, quantity);
    }

    private static void ReadTallies(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return;
        }

        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(keyPath, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } tally) {
                continue;
            }

            diagnostics.CheckKeys(tally, path, s_tallyKeys, ["page", "oldid", "date", "kind", "battles", "items"]);
            if (tally.Find("kind") is { } kind) {
                _ = diagnostics.ReadEnum(kind.Value, YamlTree.Join(path, "kind"), s_kinds);
            }

            if (tally.Find("battles") is { } battles) {
                _ = diagnostics.ReadInt(battles.Value, YamlTree.Join(path, "battles"), 1);
            }

            if (tally.Find("minions") is { } minions) {
                diagnostics.ReadStringList(minions.Value, YamlTree.Join(path, "minions"));
            }

            foreach (var number in (string[]) ["items", "treasure_cards", "reagents", "items_alone", "treasure_cards_alone"]) {
                if (tally.Find(number) is { } value) {
                    _ = ReadNumber(value.Value, YamlTree.Join(path, number), 20, diagnostics);
                }
            }
        }
    }

    private static ImmutableDictionary<ulong, ImmutableArray<DropEntry>> ReadClientTables(YNode node, string keyPath,
                                                                                           YamlDiagnostics diagnostics) {
        var byMob = new Dictionary<ulong, List<DropEntry>>();
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return ImmutableDictionary<ulong, ImmutableArray<DropEntry>>.Empty;
        }

        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(keyPath, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } table) {
                continue;
            }

            diagnostics.CheckKeys(table, path, s_clientTableKeys, ["name", "template", "mobs"]);
            if (table.Find("name") is { } name) {
                _ = diagnostics.ReadString(name.Value, YamlTree.Join(path, "name"));
            }

            if (table.Find("template") is not { } templateEntry || ReadId(templateEntry.Value, YamlTree.Join(path, "template"), diagnostics) is not { } template) {
                continue;
            }

            foreach (var mob in ReadIds(table, "mobs", path, diagnostics, minOne: true)) {
                if (!byMob.TryGetValue(mob, out var entries)) {
                    byMob[mob] = entries = [];
                }

                if (entries.All(entry => entry.Template != template)) {
                    entries.Add(new DropEntry(template, null));
                }
            }
        }

        return byMob.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray());
    }

    private static ImmutableArray<DropEntry> ReadEntries(YMap mob, string key, string keyPath, YamlDiagnostics diagnostics,
                                                         out int listed) {
        listed = 0;
        var result = ImmutableArray.CreateBuilder<DropEntry>();
        if (mob.Find(key) is not { } d || diagnostics.ReadList(d.Value, YamlTree.Join(keyPath, key)) is not { } items) {
            return result.ToImmutable();
        }

        for (var j = 0; j < items.Items.Length; j++) {
            var itemPath = YamlTree.Index(YamlTree.Join(keyPath, key), j);
            if (diagnostics.ReadMap(items.Items[j], itemPath) is not { } item) {
                continue;
            }

            listed++;
            diagnostics.CheckKeys(item, itemPath, s_itemKeys, key == "drops" ? ["name"] : ["name", "template"]);
            if (item.Find("name") is { } itemName) {
                _ = diagnostics.ReadString(itemName.Value, YamlTree.Join(itemPath, "name"));
            }

            double? chance = null;
            if (item.Find("chance") is { } c) {
                chance = diagnostics.ReadFraction(c.Value, YamlTree.Join(itemPath, "chance"));
            }

            if (item.Find("template") is { } template && ReadId(template.Value, YamlTree.Join(itemPath, "template"), diagnostics) is { } id) {
                result.Add(new DropEntry(id, chance));
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

            var items = ReadEntries(mob, "drops", keyPath, diagnostics, out var listedItems);
            var cards = ReadEntries(mob, "treasure_cards", keyPath, diagnostics, out _);
            var reagents = ReadEntries(mob, "reagents", keyPath, diagnostics, out _);
            var tally = mob.Find("tally") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(keyPath, "tally"), 1) : null;
            if (tally is null && items.Concat(cards).Concat(reagents).Any(entry => entry.Chance is not null)) {
                diagnostics.At(mob, keyPath, "a drop chance needs the mob's tally (the fights behind it)");
            }

            if (mob.Find("source") is { } source) {
                _ = diagnostics.ReadMap(source.Value, YamlTree.Join(keyPath, "source"));
            }

            if (name is null || rank is null || kind is null) {
                continue;
            }

            result.Add(new DocumentedMob(name, templates, rank.Value, ParseKind(kind), gold, listedItems, items, cards, reagents, tally));
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
