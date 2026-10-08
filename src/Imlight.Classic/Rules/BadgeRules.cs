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
 * CLASSIC BADGES
 * ========================================================================
 *
 * PURPOSE:
 * The badges of a profile's cutoff (classic-data/badges/badges-*.yaml) and
 * the rules that award them: completing a quest, completing a set of
 * quests, defeating mobs with an adjective, or entering a zone.
 *
 * USAGE EXAMPLE:
 * var rules = BadgeRulesLoader.Load(Path.Combine(classicDataRoot, profile.Rules.Badges));
 * foreach (var badge in rules.ForQuest("WC-ST01-C01-006")) { if (rules.IsEarned(badge, progress)) { ... } }
 *
 * NOTE:
 * No 2009 quest data carries ResAddBadge (it has no fields in the server
 * registry), so every award comes from this file. The wizard's badges,
 * kill counters and zone visits live in its quest registry under the keys
 * this class names, which the registry already persists.
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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Spells;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// How a badge is earned.
/// </summary>
public abstract record BadgeAward;

/// <summary>Completing any one of the quests.</summary>
public sealed record QuestBadgeAward(ImmutableArray<string> AnyOf) : BadgeAward;

/// <summary>Having completed every one of the quests.</summary>
public sealed record AllQuestsBadgeAward(ImmutableArray<string> Quests) : BadgeAward;

/// <summary>Defeating <see cref="Count"/> mobs whose template has <see cref="Adjective"/>.</summary>
public sealed record KillBadgeAward(string Adjective, int Count) : BadgeAward;

/// <summary>Entering <see cref="Zone"/> <see cref="Count"/> times.</summary>
public sealed record ZoneVisitBadgeAward(string Zone, int Count) : BadgeAward;

/// <summary>
/// Having completed every one of <see cref="Quests"/> and, for a wizard whose primary school has an entry in
/// <see cref="BySchool"/>, every one of that school's quests (quests only that school's students are offered).
/// </summary>
public sealed record AreaQuestsBadgeAward(ImmutableArray<string> Quests,
    ImmutableDictionary<string, ImmutableArray<string>> BySchool) : BadgeAward;

/// <summary>Reaching the Ranked PvP rank <see cref="Rank"/> (the arena's rank names and thresholds).</summary>
public sealed record PvpRankBadgeAward(string Rank) : BadgeAward;

/// <summary>
/// A wizard of <see cref="School"/> knowing every one of <see cref="Spells"/> (classic spell record ids) that the
/// active profile contains.
/// </summary>
public sealed record SchoolSpellsBadgeAward(string School, ImmutableArray<string> Spells) : BadgeAward;

/// <summary>Not awarded by the server; <see cref="Reason"/> says why.</summary>
public sealed record NotGrantedBadgeAward(string Reason) : BadgeAward;

/// <summary>
/// One badge.
/// </summary>
/// <param name="Id">The data id, such as <c>hero-of-unicorn-way</c>.</param>
/// <param name="Name">The name as shown in game.</param>
/// <param name="NameKey">The client locale key of the name.</param>
/// <param name="DescriptionKey">The client locale key of the description, if any.</param>
/// <param name="TitleKey">What the wizard's title shows when the badge is selected.</param>
/// <param name="FilterKey">The badge page filter key.</param>
/// <param name="World">The world id, or <c>general</c>.</param>
/// <param name="Award">How it is earned.</param>
public sealed record Badge(string Id, string Name, string NameKey, string? DescriptionKey, string TitleKey,
                           string FilterKey, string World, BadgeAward Award) {

    /// <summary>
    /// True when the server awards this badge.
    /// </summary>
    public bool IsGranted => Award is not NotGrantedBadgeAward;

}

/// <summary>
/// What a wizard has done, as the badge rules read it.
/// </summary>
public interface IBadgeProgress {

    bool HasCompletedQuest(string quest);

    int KillCount(string adjective);

    int ZoneVisits(string zone);

    /// <summary>True when the wizard's Ranked rating is at or above <paramref name="rank"/>'s threshold.</summary>
    bool ReachedPvpRank(string rank) => false;

    /// <summary>The wizard's primary school in lower case (<c>fire</c>), or null.</summary>
    string? PrimarySchool => null;

    /// <summary>True when the active profile contains the spell record <paramref name="spellId"/>.</summary>
    bool IsSpellAvailable(string spellId) => false;

    /// <summary>True when the wizard has learned the spell of record <paramref name="spellId"/>.</summary>
    bool KnowsSpell(string spellId) => false;

}

/// <summary>
/// A profile's badges.
/// </summary>
public sealed class BadgeRules {

    /// <summary>The registry key that marks a badge as earned.</summary>
    public static string BadgeKey(string badgeId) => "ClassicBadge_" + badgeId;

    /// <summary>The registry key that counts defeated mobs with an adjective.</summary>
    public static string KillCounterKey(string adjective) => "ClassicKills_" + adjective;

    /// <summary>The registry key that counts entries into a zone.</summary>
    public static string ZoneVisitKey(string zone) => "ClassicZoneVisits_" + zone;

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required ImmutableArray<Badge> Badges { get; init; }
    public required string SourceFile { get; init; }

    private FrozenDictionary<string, ImmutableArray<Badge>>? _byQuest;
    private FrozenDictionary<string, ImmutableArray<Badge>>? _byAdjective;
    private FrozenDictionary<string, ImmutableArray<Badge>>? _byZone;
    private FrozenDictionary<string, Badge>? _byId;

    /// <summary>The badges awarded at all.</summary>
    public IEnumerable<Badge> Granted => Badges.Where(badge => badge.IsGranted);

    /// <summary>A badge by id.</summary>
    public Badge? Find(string id) => (_byId ??= Badges.ToFrozenDictionary(b => b.Id, StringComparer.Ordinal)).GetValueOrDefault(id);

    /// <summary>The badges completing <paramref name="quest"/> may award.</summary>
    public ImmutableArray<Badge> ForQuest(string quest)
        => (_byQuest ??= Index(badge => badge.Award switch {
            QuestBadgeAward q => q.AnyOf,
            AllQuestsBadgeAward a => a.Quests,
            AreaQuestsBadgeAward area => [.. area.Quests, .. area.BySchool.Values.SelectMany(q => q)],
            _ => [],
        })).GetValueOrDefault(quest, []);

    /// <summary>The kill badges that count a mob with <paramref name="adjective"/>.</summary>
    public ImmutableArray<Badge> ForAdjective(string adjective)
        => (_byAdjective ??= Index(badge => badge.Award is KillBadgeAward k ? [k.Adjective] : [])).GetValueOrDefault(adjective, []);

    /// <summary>The adjectives some kill badge counts.</summary>
    public IEnumerable<string> CountedAdjectives
        => (_byAdjective ??= Index(badge => badge.Award is KillBadgeAward k ? [k.Adjective] : [])).Keys;

    /// <summary>The Ranked PvP rank badges.</summary>
    public ImmutableArray<Badge> PvpRankBadges => [.. Badges.Where(badge => badge.Award is PvpRankBadgeAward)];

    /// <summary>The learn-every-spell-of-your-school badges.</summary>
    public ImmutableArray<Badge> SchoolSpellBadges => [.. Badges.Where(badge => badge.Award is SchoolSpellsBadgeAward)];

    /// <summary>The zone-visit badges of <paramref name="zone"/>.</summary>
    public ImmutableArray<Badge> ForZone(string zone)
        => (_byZone ??= Index(badge => badge.Award is ZoneVisitBadgeAward z ? [z.Zone] : [])).GetValueOrDefault(zone, []);

    /// <summary>
    /// Whether the wizard's progress meets the badge's award.
    /// </summary>
    public static bool IsEarned(Badge badge, IBadgeProgress progress) => badge.Award switch {
        QuestBadgeAward q => q.AnyOf.Any(progress.HasCompletedQuest),
        AllQuestsBadgeAward a => a.Quests.All(progress.HasCompletedQuest),
        KillBadgeAward k => progress.KillCount(k.Adjective) >= k.Count,
        ZoneVisitBadgeAward z => progress.ZoneVisits(z.Zone) >= z.Count,
        PvpRankBadgeAward r => progress.ReachedPvpRank(r.Rank),
        AreaQuestsBadgeAward area => area.Quests.All(progress.HasCompletedQuest)
            && (area.BySchool.IsEmpty
                || progress.PrimarySchool is { } school
                    && (!area.BySchool.TryGetValue(school, out var own) || own.All(progress.HasCompletedQuest))),
        SchoolSpellsBadgeAward s => string.Equals(progress.PrimarySchool, s.School, StringComparison.Ordinal)
            && s.Spells.Where(progress.IsSpellAvailable).ToArray() is { Length: > 0 } available
            && available.All(progress.KnowsSpell),
        _ => false,
    };

    private FrozenDictionary<string, ImmutableArray<Badge>> Index(Func<Badge, IEnumerable<string>> keys)
        => Badges.SelectMany(badge => keys(badge).Select(key => (key, badge)))
            .GroupBy(pair => pair.key, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.badge).Distinct().ToImmutableArray(),
                StringComparer.Ordinal);

}

/// <summary>
/// Loads and validates badge rules.
/// </summary>
public static class BadgeRulesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "badges");
    internal static readonly FrozenSet<string> s_badgeKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "name", "name_key", "description_key", "title_key", "filter_key", "world", "award", "confidence", "sources", "notes");
    internal static readonly FrozenSet<string> s_awardKeys = FrozenSet.Create(StringComparer.Ordinal,
        "quest", "all_quests", "area_quests", "kills", "zone_visits", "pvp_rank", "school_spells", "not_granted");
    private static readonly FrozenSet<string> s_areaKeys = FrozenSet.Create(StringComparer.Ordinal, "quests", "by_school");
    private static readonly FrozenSet<string> s_schoolSpellKeys = FrozenSet.Create(StringComparer.Ordinal, "school", "spells");
    private static readonly string[] s_schools = ["fire", "ice", "storm", "myth", "life", "death", "balance"];
    private static readonly FrozenSet<string> s_killKeys = FrozenSet.Create(StringComparer.Ordinal, "adjective", "count");
    private static readonly FrozenSet<string> s_zoneKeys = FrozenSet.Create(StringComparer.Ordinal, "zone", "count");
    private static readonly string[] s_worlds = ["wizard_city", "krokotopia", "marleybone", "mooshu", "dragonspyre", "grizzleheim", "general"];
    private static readonly Regex s_id = new(@"^badges-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_badgeId = new(@"^[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_langKey = new(@"^[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9]+\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the badges at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static BadgeRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the badge rules do not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "badges"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be badges-<name> and match the file name '{expectedId}'");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        var badges = ImmutableArray.CreateBuilder<Badge>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (map.Find("badges") is { } badgesEntry && diagnostics.ReadList(badgesEntry.Value, "badges") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("badges", i);
                if (ReadBadge(list.Items[i], keyPath, diagnostics) is not { } badge) {
                    continue;
                }

                if (!ids.Add(badge.Id)) {
                    diagnostics.At(list.Items[i], keyPath, $"badge id '{badge.Id}' is repeated");
                    continue;
                }

                badges.Add(badge);
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new BadgeRules {
            Id = id!,
            Profiles = profiles,
            Badges = badges.ToImmutable(),
            SourceFile = display,
        };
    }

    private static Badge? ReadBadge(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, keyPath) is not { } map) {
            return null;
        }

        diagnostics.CheckKeys(map, keyPath, s_badgeKeys,
            ["id", "name", "name_key", "title_key", "filter_key", "world", "award", "confidence", "sources"]);
        string? Str(string key) => map.Find(key) is { } e ? diagnostics.ReadString(e.Value, YamlTree.Join(keyPath, key)) : null;
        string? Key(string key) {
            var value = Str(key);
            if (value is not null && !s_langKey.IsMatch(value)) {
                diagnostics.At(map.Find(key)!.Value, YamlTree.Join(keyPath, key), $"'{value}' is not a locale key <table>_<key>");

                return null;
            }

            return value;
        }

        var id = Str("id");
        if (id is not null && !s_badgeId.IsMatch(id)) {
            diagnostics.At(map.Find("id")!.Value, YamlTree.Join(keyPath, "id"), $"'{id}' is not a lower-case badge id");
            id = null;
        }

        var name = Str("name");
        var nameKey = Key("name_key");
        var descriptionKey = Key("description_key");
        var titleKey = Key("title_key");
        var filterKey = Key("filter_key");
        var world = map.Find("world") is { } w ? diagnostics.ReadEnum(w.Value, YamlTree.Join(keyPath, "world"), s_worlds) : null;
        var award = map.Find("award") is { } a ? ReadAward(a.Value, YamlTree.Join(keyPath, "award"), diagnostics) : null;

        return id is null || name is null || nameKey is null || titleKey is null || filterKey is null || world is null || award is null
            ? null
            : new Badge(id, name, nameKey, descriptionKey, titleKey, filterKey, world, award);
    }

    private static BadgeAward? ReadAward(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, keyPath) is not { } map) {
            return null;
        }

        diagnostics.CheckKeys(map, keyPath, s_awardKeys, []);
        if (map.Entries.Length != 1) {
            diagnostics.At(map, keyPath, "an award has exactly one of quest, all_quests, area_quests, kills, zone_visits, pvp_rank, school_spells or not_granted");

            return null;
        }

        var entry = map.Entries[0];
        var path = YamlTree.Join(keyPath, entry.Key);
        switch (entry.Key) {
            case "quest" or "all_quests": {
                var quests = ReadQuests(entry.Value, path, diagnostics);
                if (quests.IsEmpty) {
                    return null;
                }

                return entry.Key == "quest" ? new QuestBadgeAward(quests) : new AllQuestsBadgeAward(quests);
            }
            case "kills": {
                if (diagnostics.ReadMap(entry.Value, path) is not { } kills) {
                    return null;
                }

                diagnostics.CheckKeys(kills, path, s_killKeys, ["adjective", "count"]);
                var adjective = kills.Find("adjective") is { } adj ? diagnostics.ReadString(adj.Value, YamlTree.Join(path, "adjective")) : null;
                var count = kills.Find("count") is { } c ? diagnostics.ReadInt(c.Value, YamlTree.Join(path, "count"), 1, 100_000) : null;

                return adjective is null || count is null ? null : new KillBadgeAward(adjective, count.Value);
            }
            case "zone_visits": {
                if (diagnostics.ReadMap(entry.Value, path) is not { } visits) {
                    return null;
                }

                diagnostics.CheckKeys(visits, path, s_zoneKeys, ["zone", "count"]);
                var zone = visits.Find("zone") is { } z ? diagnostics.ReadString(z.Value, YamlTree.Join(path, "zone")) : null;
                var count = visits.Find("count") is { } c ? diagnostics.ReadInt(c.Value, YamlTree.Join(path, "count"), 1, 1000) : null;

                return zone is null || count is null ? null : new ZoneVisitBadgeAward(zone, count.Value);
            }
            case "area_quests": {
                if (diagnostics.ReadMap(entry.Value, path) is not { } area) {
                    return null;
                }

                diagnostics.CheckKeys(area, path, s_areaKeys, ["quests"]);
                var quests = area.Find("quests") is { } q ? ReadQuests(q.Value, YamlTree.Join(path, "quests"), diagnostics) : [];
                var bySchool = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
                if (area.Find("by_school") is { } bs && diagnostics.ReadMap(bs.Value, YamlTree.Join(path, "by_school")) is { } schools) {
                    foreach (var school in schools.Entries) {
                        var at = YamlTree.Join(YamlTree.Join(path, "by_school"), school.Key);
                        if (!s_schools.Contains(school.Key)) {
                            diagnostics.At(school.Value, at, $"'{school.Key}' is not a school");
                            continue;
                        }
                        bySchool[school.Key] = ReadQuests(school.Value, at, diagnostics);
                    }
                }

                return quests.IsEmpty ? null : new AreaQuestsBadgeAward(quests, bySchool.ToImmutable());
            }
            case "pvp_rank":
                return diagnostics.ReadString(entry.Value, path) is { Length: > 0 } rank ? new PvpRankBadgeAward(rank) : null;
            case "school_spells": {
                if (diagnostics.ReadMap(entry.Value, path) is not { } spells) {
                    return null;
                }

                diagnostics.CheckKeys(spells, path, s_schoolSpellKeys, ["school", "spells"]);
                var school = spells.Find("school") is { } sc ? diagnostics.ReadEnum(sc.Value, YamlTree.Join(path, "school"), s_schools) : null;
                var ids = spells.Find("spells") is { } sp ? ReadQuests(sp.Value, YamlTree.Join(path, "spells"), diagnostics) : [];

                return school is null || ids.IsEmpty ? null : new SchoolSpellsBadgeAward(school, ids);
            }
            case "not_granted":
                return diagnostics.ReadString(entry.Value, path) is { } reason ? new NotGrantedBadgeAward(reason) : null;
            default:
                return null;
        }
    }

    private static ImmutableArray<string> ReadQuests(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, keyPath, "needs at least one quest");
        }

        var quests = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            if (diagnostics.ReadString(list.Items[i], YamlTree.Index(keyPath, i)) is { } quest) {
                quests.Add(quest);
            }
        }

        return quests.ToImmutable();
    }

}
