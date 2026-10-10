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
 * CLASSIC DATA LOADING
 * ========================================================================
 * 
 * PURPOSE:
 * Loads a rules profile from classic-data/profiles: validates each file of
 * its extends chain against the profile schema, merges the chain and builds
 * the immutable ClassicProfile.
 * 
 * USAGE EXAMPLE:
 * var profile = ClassicProfileLoader.Load("/opt/w101c/classic-data/profiles", "late-2009");
 * 
 * NOTE:
 * Only cutoff, zone_content_cutoff, level_cap, worlds, features, rules, world_unlocks and
 * disabled_quests are inherited. Maps
 * merge recursively; scalars, lists and explicit nulls replace. Metadata
 * (id, title, description, status, notes, extends) is never inherited.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imlight.Classic.Travel;
using Imlight.Classic.Yaml;

namespace Imlight.Classic;

/// <summary>
/// Loads and validates classic rules profiles.
/// </summary>
public static class ClassicProfileLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "description", "status", "extends", "cutoff", "zone_content_cutoff", "level_cap", "worlds", "features", "rules", "notes",
        "world_unlocks", "disabled_quests");
    private static readonly string[] s_requiredKeys = ["id", "title", "cutoff", "status"];
    private static readonly string[] s_inheritedKeys = ["cutoff", "zone_content_cutoff", "level_cap", "worlds", "features", "rules", "world_unlocks", "disabled_quests"];
    internal static readonly FrozenSet<string> s_worldUnlockKeys = FrozenSet.Create(StringComparer.Ordinal, "any_of", "source", "notes");
    private static readonly FrozenSet<string> s_ruleKeys = FrozenSet.Create(StringComparer.Ordinal,
        "accuracy_table", "xp_table", "player_health", "mob_rewards", "badges", "quest_cards", "treasure_prices", "mob_stats", "crown_shop", "later_objects", "creature_decks", "power_pips_from_rank", "dragonspyre_difficulty", "tutorial",
        "teleport_stones", "potions", "second_chance", "boss_cheats", "instance_resets", "puzzle_helpers");

    /// <summary>
    /// Loads the profile <paramref name="id"/> from <paramref name="profilesDir"/>, following its extends chain.
    /// </summary>
    /// <param name="profilesDir">The profiles directory.</param>
    /// <param name="id">The profile id; the file is <c>&lt;id&gt;.yaml</c>.</param>
    /// <returns>The merged profile.</returns>
    /// <exception cref="ClassicDataException">The id is invalid, a file is missing, invalid or the chain cycles.</exception>
    public static ClassicProfile Load(string profilesDir, string id) {
        if (!ClassicSchema.IsValidId(id)) {
            throw new ClassicDataException(new ClassicDataError(ClassicDataLocator.DisplayPath(profilesDir), "", null,
                $"'{id}' is not a valid profile id (expected {ClassicSchema.IdPattern})"));
        }

        var profilesFolder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesDir)));
        var chain = new List<(string Path, YMap Map)>();
        var visited = new List<string>();
        var currentId = id;
        (string File, YEntry Entry)? childExtends = null;

        while (true) {
            var path = Path.GetFullPath(Path.Combine(profilesDir, currentId + ".yaml"));
            if (!File.Exists(path)) {
                throw new ClassicDataException(childExtends is { } link
                    ? new ClassicDataError(link.File, "extends", link.Entry.Line,
                        $"extends names '{currentId}', but {profilesFolder}/{currentId}.yaml does not exist")
                    : new ClassicDataError(ClassicDataLocator.DisplayPath(path), "", null,
                        $"profile '{currentId}' does not exist"));
            }

            var map = ParseAndValidate(path, currentId);
            visited.Add(currentId);
            chain.Add((path, map));

            var extends = map.Find("extends");
            if (extends?.Value is not YScalar { Value: var parentId }) {
                break;
            }

            if (visited.Contains(parentId, StringComparer.Ordinal)) {
                throw new ClassicDataException(new ClassicDataError(map.File, "extends", extends.Line,
                    $"extends cycle: {string.Join(" -> ", visited.Append(parentId))}"));
            }

            childExtends = (map.File, extends);
            currentId = parentId;
        }

        return Build(chain);
    }

    /// <summary>
    /// Loads every profile in <paramref name="profilesDir"/>, reporting the errors of all files together.
    /// </summary>
    /// <param name="profilesDir">The profiles directory.</param>
    /// <returns>The profiles, ordered by file name.</returns>
    /// <exception cref="ClassicDataException">Any profile fails to load.</exception>
    public static ImmutableArray<ClassicProfile> LoadAll(string profilesDir) {
        if (!Directory.Exists(profilesDir)) {
            throw new DirectoryNotFoundException($"The profiles directory {profilesDir} does not exist.");
        }

        var profiles = ImmutableArray.CreateBuilder<ClassicProfile>();
        var errors = new List<ClassicDataError>();
        foreach (var file in Directory.GetFiles(profilesDir, "*.yaml").Order(StringComparer.Ordinal)) {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ClassicSchema.IsValidId(id)) {
                errors.Add(new ClassicDataError(ClassicDataLocator.DisplayPath(file), "", null,
                    $"the file name is not a valid profile id (expected {ClassicSchema.IdPattern})"));
                continue;
            }

            try {
                profiles.Add(Load(profilesDir, id));
            }
            catch (ClassicDataException ex) {
                // A child re-validates its parents, so the same error can arrive more than once.
                errors.AddRange(ex.Errors.Where(error => !errors.Contains(error)));
            }
        }

        if (errors.Count > 0) {
            throw new ClassicDataException(errors);
        }

        return profiles.ToImmutable();
    }

    private static YMap ParseAndValidate(string path, string expectedId) {
        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(path, ClassicDataLocator.DisplayPath(path), diagnostics);
        if (root is null) {
            throw diagnostics.ToException();
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, s_requiredKeys);
        foreach (var entry in map.Entries) {
            var value = entry.Value;
            switch (entry.Key) {
                case "id":
                    if (diagnostics.ReadString(value, "id") is { } id) {
                        if (!ClassicSchema.IsValidId(id)) {
                            diagnostics.At(value, "id", $"'{id}' is not a valid profile id (expected {ClassicSchema.IdPattern})");
                        }
                        else if (!string.Equals(id, expectedId, StringComparison.Ordinal)) {
                            diagnostics.At(value, "id", $"id '{id}' does not match the file name '{expectedId}'");
                        }
                    }
                    break;
                case "title" or "description":
                    _ = diagnostics.ReadString(value, entry.Key);
                    break;
                case "status":
                    _ = diagnostics.ReadEnum(value, "status", ClassicSchema.ProfileStatuses);
                    break;
                case "extends":
                    if (diagnostics.ReadString(value, "extends") is { } parent && !ClassicSchema.IsValidId(parent)) {
                        diagnostics.At(value, "extends", $"'{parent}' is not a valid profile id (expected {ClassicSchema.IdPattern})");
                    }
                    break;
                case "cutoff" or "zone_content_cutoff":
                    _ = diagnostics.ReadDate(value, entry.Key, allowNull: true);
                    break;
                case "level_cap":
                    _ = diagnostics.ReadPositiveInt(value, "level_cap", allowNull: true);
                    break;
                case "worlds":
                    ValidateWorlds(value, diagnostics);
                    break;
                case "features":
                    ValidateFeatures(value, diagnostics);
                    break;
                case "rules":
                    ValidateRules(value, diagnostics);
                    break;
                case "notes":
                    diagnostics.ReadStringList(value, "notes");
                    break;
                case "world_unlocks":
                    ValidateWorldUnlocks(value, diagnostics);
                    break;
                case "disabled_quests": // CLASSIC
                    ValidateDisabledQuests(value, diagnostics);
                    break;
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return map;
    }

    private static void ValidateWorlds(YNode value, YamlDiagnostics diagnostics) {
        if (value is YNull || diagnostics.ReadList(value, "worlds") is not { } list) {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index("worlds", i);
            if (diagnostics.ReadString(list.Items[i], path) is not { } world) {
                continue;
            }

            if (!ClassicSchema.IsWorldId(world)) {
                diagnostics.At(list.Items[i], path, $"unknown world id '{world}'");
            }
            else if (!seen.Add(world)) {
                diagnostics.At(list.Items[i], path, $"world '{world}' is listed twice");
            }
        }
    }

    // CLASSIC: quest names, each listed once.
    private static void ValidateDisabledQuests(YNode value, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(value, "disabled_quests") is not { } list) {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index("disabled_quests", i);
            if (diagnostics.ReadString(list.Items[i], path) is not { } quest) {
                continue;
            }

            if (quest.Length == 0 || !char.IsAsciiLetterOrDigit(quest[0])
                    || !quest.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) {
                diagnostics.At(list.Items[i], path, $"'{quest}' is not a quest name");
            }
            else if (!seen.Add(quest)) {
                diagnostics.At(list.Items[i], path, $"quest '{quest}' is listed twice");
            }
        }
    }

    private static void ValidateWorldUnlocks(YNode value, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(value, "world_unlocks") is not { } unlocks) {
            return;
        }

        foreach (var entry in unlocks.Entries) {
            var path = YamlTree.Join("world_unlocks", entry.Key);
            if (!ClassicSchema.IsWorldId(entry.Key)) {
                diagnostics.AtKey(unlocks, entry, path, $"unknown world id '{entry.Key}'");
                continue;
            }

            if (diagnostics.ReadMap(entry.Value, path) is not { } rule) {
                continue;
            }

            diagnostics.CheckKeys(rule, path, s_worldUnlockKeys, ["any_of"]);
            foreach (var field in rule.Entries) {
                var fieldPath = YamlTree.Join(path, field.Key);
                switch (field.Key) {
                    case "source":
                        _ = diagnostics.ReadString(field.Value, fieldPath);
                        break;
                    case "notes":
                        diagnostics.ReadStringList(field.Value, fieldPath);
                        break;
                    case "any_of":
                        if (diagnostics.ReadList(field.Value, fieldPath) is not { } checks) {
                            break;
                        }

                        if (checks.Items.Length == 0) {
                            diagnostics.At(field.Value, fieldPath, "any_of needs at least one check");
                        }

                        for (var i = 0; i < checks.Items.Length; i++) {
                            _ = ReadUnlockCheck(checks.Items[i], YamlTree.Index(fieldPath, i), diagnostics);
                        }
                        break;
                }
            }
        }
    }

    private static UnlockCheck? ReadUnlockCheck(YNode node, string path, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, path) is not { } check) {
            return null;
        }

        if (check.Entries.Length != 1 || !UnlockCheck.Keys.Contains(check.Entries[0].Key)) {
            diagnostics.At(node, path, $"a check has exactly one of {string.Join(", ", UnlockCheck.Keys)}");

            return null;
        }

        var (key, value) = (check.Entries[0].Key, check.Entries[0].Value);
        var kind = (UnlockCheckKind) UnlockCheck.Keys.IndexOf(key);
        var valuePath = YamlTree.Join(path, key);
        if (kind == UnlockCheckKind.Level) {
            return diagnostics.ReadPositiveInt(value, valuePath, allowNull: false) is { } level
                ? new UnlockCheck(kind, null, level)
                : null;
        }

        if (diagnostics.ReadString(value, valuePath) is not { Length: > 0 } name) {
            return null;
        }

        return new UnlockCheck(kind, name, 0);
    }

    private static ImmutableDictionary<string, WorldUnlock> BuildWorldUnlocks(YMap? unlocks) {
        var builder = ImmutableDictionary.CreateBuilder<string, WorldUnlock>(StringComparer.Ordinal);
        if (unlocks is null) {
            return builder.ToImmutable();
        }

        var diagnostics = new YamlDiagnostics();
        foreach (var entry in unlocks.Entries) {
            if (entry.Value is not YMap rule || rule.Find("any_of")?.Value is not YSeq checks) {
                continue;
            }

            var read = checks.Items.Select(item => ReadUnlockCheck(item, "", diagnostics)).OfType<UnlockCheck>().ToImmutableArray();
            builder[entry.Key] = new WorldUnlock(entry.Key, read, ScalarOf(rule, "source"));
        }

        return builder.ToImmutable();
    }

    private static void ValidateFeatures(YNode value, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(value, "features") is not { } features) {
            return;
        }

        foreach (var entry in features.Entries) {
            var path = YamlTree.Join("features", entry.Key);
            if (entry.Key == ClassicFeatures.PetsGroup) {
                if (diagnostics.ReadMap(entry.Value, path) is not { } pets) {
                    continue;
                }

                foreach (var pet in pets.Entries) {
                    var feature = $"{ClassicFeatures.PetsGroup}.{pet.Key}";
                    var petPath = YamlTree.Join(path, pet.Key);
                    if (!ClassicFeatures.IsKnown(feature)) {
                        diagnostics.AtKey(pets, pet, petPath, $"unknown feature '{feature}'");
                        continue;
                    }

                    _ = diagnostics.ReadBool(pet.Value, petPath);
                }

                continue;
            }

            if (entry.Key.Contains('.') || !ClassicFeatures.IsKnown(entry.Key)) {
                diagnostics.AtKey(features, entry, path, $"unknown feature '{entry.Key}'");
                continue;
            }

            _ = diagnostics.ReadBool(entry.Value, path);
        }
    }

    private static void ValidateRules(YNode value, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(value, "rules") is not { } rules) {
            return;
        }

        diagnostics.CheckKeys(rules, "rules", s_ruleKeys, []);
        foreach (var entry in rules.Entries) {
            var path = YamlTree.Join("rules", entry.Key);
            switch (entry.Key) {
                case "accuracy_table" or "xp_table" or "player_health" or "mob_rewards" or "badges" or "quest_cards" or "treasure_prices" or "mob_stats" or "crown_shop" or "later_objects" or "creature_decks":
                    _ = diagnostics.ReadString(entry.Value, path);
                    break;
                case "potions": // CLASSIC
                    _ = diagnostics.ReadString(entry.Value, path);
                    break;
                case "second_chance" or "boss_cheats" or "instance_resets" or "puzzle_helpers": // CLASSIC: null switches off an inherited file (arc1-2009h1, before Oct 2009)
                    if (entry.Value is not YNull) {
                        _ = diagnostics.ReadString(entry.Value, path);
                    }
                    break;
                case "power_pips_from_rank":
                    if (entry.Value is not YNull) {
                        _ = diagnostics.ReadEnum(entry.Value, path, ClassicSchema.PowerPipRanks);
                    }
                    break;
                case "dragonspyre_difficulty":
                    _ = diagnostics.ReadEnum(entry.Value, path, ClassicSchema.DragonspyreDifficulties);
                    break;
                case "tutorial":
                    _ = diagnostics.ReadEnum(entry.Value, path, ClassicSchema.Tutorials);
                    break;
                case "teleport_stones":
                    _ = diagnostics.ReadEnum(entry.Value, path, ClassicSchema.TeleportStoneRules);
                    break;
            }
        }
    }

    private static ClassicProfile Build(List<(string Path, YMap Map)> chain) {
        // The chain is child first; merge the inherited keys from the root ancestor down.
        YMap? merged = null;
        for (var i = chain.Count - 1; i >= 0; i--) {
            var inherited = OnlyKeys(chain[i].Map, s_inheritedKeys);
            merged = merged is null ? inherited : YamlTree.Merge(merged, inherited);
        }

        var child = chain[0].Map;
        var diagnostics = new YamlDiagnostics();
        var rules = merged!.Find("rules")?.Value as YMap;

        return new ClassicProfile {
            Id = ScalarOf(child, "id")!,
            Title = ScalarOf(child, "title")!,
            Description = ScalarOf(child, "description"),
            Status = Enum.Parse<ProfileStatus>(ScalarOf(child, "status")!, ignoreCase: true),
            Extends = ScalarOf(child, "extends"),
            Cutoff = merged.Find("cutoff")?.Value is { } cutoff ? diagnostics.ReadDate(cutoff, "cutoff", allowNull: true) : null,
            ZoneContentCutoff = merged.Find("zone_content_cutoff")?.Value is { } zoneCutoff
                ? diagnostics.ReadDate(zoneCutoff, "zone_content_cutoff", allowNull: true) : null,
            LevelCap = merged.Find("level_cap")?.Value is { } cap ? diagnostics.ReadPositiveInt(cap, "level_cap", allowNull: true) : null,
            Worlds = merged.Find("worlds")?.Value is YSeq worlds
                ? worlds.Items.Cast<YScalar>().Select(world => world.Value).ToImmutableArray()
                : null,
            Features = new FeatureSwitches(FlattenFeatures(merged.Find("features")?.Value as YMap)),
            Rules = rules is null ? ProfileRules.None : new ProfileRules {
                AccuracyTable = ScalarOf(rules, "accuracy_table"),
                XpTable = ScalarOf(rules, "xp_table"),
                PlayerHealth = ScalarOf(rules, "player_health"), // CLASSIC
                MobRewards = ScalarOf(rules, "mob_rewards"),
                Badges = ScalarOf(rules, "badges"),
                QuestCards = ScalarOf(rules, "quest_cards"),
                TreasurePrices = ScalarOf(rules, "treasure_prices"),
                MobStats = ScalarOf(rules, "mob_stats"),
                CrownShop = ScalarOf(rules, "crown_shop"),
                LaterObjects = ScalarOf(rules, "later_objects"),
                CreatureDecks = ScalarOf(rules, "creature_decks"), // CLASSIC
                Potions = ScalarOf(rules, "potions"), // CLASSIC
                SecondChance = ScalarOf(rules, "second_chance"), // CLASSIC
                BossCheats = ScalarOf(rules, "boss_cheats"), // CLASSIC
                InstanceResets = ScalarOf(rules, "instance_resets"), // CLASSIC
                PuzzleHelpers = ScalarOf(rules, "puzzle_helpers"), // CLASSIC
                PowerPipsFromRank = ScalarOf(rules, "power_pips_from_rank"),
                DragonspyreDifficulty = ScalarOf(rules, "dragonspyre_difficulty"),
                Tutorial = ScalarOf(rules, "tutorial"),
                TeleportStones = ScalarOf(rules, "teleport_stones"),
            },
            WorldUnlocks = BuildWorldUnlocks(merged.Find("world_unlocks")?.Value as YMap),
            DisabledQuests = merged.Find("disabled_quests")?.Value is YSeq disabled // CLASSIC
                ? [.. disabled.Items.Cast<YScalar>().Select(quest => quest.Value)]
                : [],
            Notes = child.Find("notes")?.Value is YSeq notes
                ? [.. notes.Items.Cast<YScalar>().Select(note => note.Value)]
                : [],
            SourceFiles = [.. chain.Select(link => link.Path)],
        };
    }

    private static YMap OnlyKeys(YMap map, string[] keys)
        => new(map.File, map.Line, [.. map.Entries.Where(entry => keys.Contains(entry.Key, StringComparer.Ordinal))]);

    private static string? ScalarOf(YMap map, string key)
        => map.Find(key)?.Value is YScalar scalar ? scalar.Value : null;

    private static Dictionary<string, bool> FlattenFeatures(YMap? features) {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (features is null) {
            return values;
        }

        foreach (var entry in features.Entries) {
            if (entry.Value is YMap group) {
                foreach (var member in group.Entries) {
                    values[$"{entry.Key}.{member.Key}"] = IsTrue(member.Value);
                }
            }
            else {
                values[entry.Key] = IsTrue(entry.Value);
            }
        }

        return values;
    }

    private static bool IsTrue(YNode node)
        => node is YScalar scalar && string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase);

}
