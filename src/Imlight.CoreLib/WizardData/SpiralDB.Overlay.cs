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
*/

// CLASSIC: SpiralDB overlays. Data we author (quests SpiralDB lacks, fixes to its records) lives in
// separate folders with SpiralDB's layout and is layered over the SpiralDB cache at load time, so the
// cache stays a pristine git checkout that auto-fetch can reset.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.World;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData;

public static partial class SpiralDB {

    // CLASSIC: Database.SpiralDBOverlayPaths, comma-separated, applied in order (a later root wins).
    private static readonly string[] s_overlayPaths
        = ConfigurationManager.Settings["Database.SpiralDBOverlayPaths"].AsList();

    // CLASSIC: <overlay root>/QuestTemplates/_disabled.txt lists quests (one name per line, '#' starts a
    // comment) to remove from everything loaded before that root.
    private const string OverlayTombstoneFile = "_disabled.txt";

    private const int OverlayMaxKeysLogged = 50;

    // CLASSIC: ZoneTransfer merge records applied onto a record loaded from the same folder in the last LoadZoneData
    // call; such a file shares its key with another file on purpose (SpiralDB.LoadZoneData).
    private static int s_zoneMergesOntoSameRootRecords;

    private sealed class OverlayCategoryStats(string name, Func<int> count) {
        public string Name { get; } = name;
        public Func<int> Count { get; } = count;
        public int Base { get; } = count();
        public bool Touched { get; set; }
        public int Files { get; set; }
        public int Loaded { get; set; }
        public int Added { get; set; }
        public int Overrides { get; set; }
        public int Failed { get; set; }
        public int Tombstoned { get; set; }
        public int TombstonesUnmatched { get; set; }
    }

    // CLASSIC: full paths of the configured overlay roots, in order, without duplicates. Missing roots are
    // kept so the sync guard still treats them as off-limits; LoadOverlays skips them.
    private static List<string> ResolveOverlayRoots() {
        var roots = new List<string>();
        foreach (var configured in s_overlayPaths) {
            string full;
            try {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            }
            catch (Exception ex) {
                Logger.Error("SpiralDB overlay path {0} is invalid: {1}", Logger.Args(configured, ex.Message));
                continue;
            }

            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) {
                roots.Add(full);
            }
        }

        if (roots.Count > 0) {
            Logger.Information("SpiralDB overlays (applied in order): {0}", Logger.Args(string.Join(", ", roots)));
        }

        return roots;
    }

    // CLASSIC: auto-fetch runs "git reset --hard" in the cache, so git must never run where it could reach
    // an overlay: an overlay equal to, inside, or containing the cache, or a cache folder that is not its
    // own git checkout (git would walk up and fetch/reset whatever repository encloses it).
    private static bool CanSyncWithoutTouchingOverlays(string basePath, List<string> overlayRoots) {
        var cache = CanonicalPath(basePath);
        foreach (var root in overlayRoots) {
            var overlay = CanonicalPath(root);
            if (IsSameOrInside(overlay, cache) || IsSameOrInside(cache, overlay)) {
                Logger.Error("SpiralDB overlay {0} overlaps the SpiralDB cache {1}. Skipping the SpiralDB sync so " +
                             "git cannot touch the overlay; move one of them.",
                    Logger.Args(root, basePath));

                return false;
            }
        }

        if (s_autoFetch && Directory.Exists(basePath)
                && !Directory.Exists(Path.Combine(basePath, ".git"))
                && !File.Exists(Path.Combine(basePath, ".git"))) {
            Logger.Error("SpiralDB cache {0} is not a git checkout. Skipping the SpiralDB sync so git cannot act " +
                         "on a repository that encloses it.",
                Logger.Args(basePath));

            return false;
        }

        return true;
    }

    private static bool IsSameOrInside(string path, string dir) {
        if (path.Equals(dir, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // CLASSIC: the full path with every symlink along it resolved, so a link cannot hide an overlap.
    private static string CanonicalPath(string path, int depth = 0) {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (depth > 16) {
            return full;
        }

        try {
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var current = root;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts) {
                current = Path.Combine(current, part);
                var target = new FileInfo(current).LinkTarget is null
                    ? null
                    : File.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target is not null) {
                    current = CanonicalPath(target.FullName, depth + 1);
                }
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception) {
            return full;
        }
    }

    // CLASSIC: runs the base loaders over each overlay root in order, so a record there replaces the
    // record with the same key loaded before it, then applies that root's quest tombstones. Parse failures
    // are logged per file by the loaders and never abort the load. Returns the number of files loaded.
    private static int LoadOverlays(List<string> overlayRoots,
                                    ConcurrentDictionary<string, CreatureSpellbook> spellbooks,
                                    ConcurrentDictionary<string, DropTable> dropTables,
                                    GlobalRegistryModel globalRegistry,
                                    ConcurrentDictionary<ulong, NPCInventory> npcInventories,
                                    ConcurrentDictionary<ulong, NPCSpellInventory> npcSpellInventories,
                                    ConcurrentDictionary<ulong, NpcDropTable> npcDropTables,
                                    ConcurrentDictionary<ulong, NpcTreasureCardInventory> treasureCardInventories,
                                    List<QuestTemplate> questTemplates,
                                    ConcurrentDictionary<string, QuestTemplate> questTemplatesByName,
                                    ConcurrentDictionary<string, WizardZoneData> zoneData) {
        if (overlayRoots.Count == 0) {
            return 0;
        }

        // Folder names match the ones the base loaders read.
        var spellbookStats = new OverlayCategoryStats("CreatureSpellbook", () => spellbooks.Count);
        var dropTableStats = new OverlayCategoryStats("DropTables", () => dropTables.Count);
        var registryStats = new OverlayCategoryStats("GlobalRegistry", () => globalRegistry.GlobalRegistryValues.Count);
        var inventoryStats = new OverlayCategoryStats("NpcInventory", () => npcInventories.Count);
        var spellInventoryStats = new OverlayCategoryStats("NpcSpellInventory", () => npcSpellInventories.Count);
        var npcDropTableStats = new OverlayCategoryStats("NpcDropTable", () => npcDropTables.Count);
        var treasureCardStats = new OverlayCategoryStats("TreasureCardInventory", () => treasureCardInventories.Count);
        var questStats = new OverlayCategoryStats("QuestTemplates", () => questTemplatesByName.Count);
        var zoneStats = new OverlayCategoryStats("ZoneTransfer", () => zoneData.Count);

        var filesLoaded = 0;
        foreach (var root in overlayRoots) {
            if (!Directory.Exists(root)) {
                Logger.Warning("SpiralDB overlay {0} does not exist; skipping it.", Logger.Args(root));
                continue;
            }

            Logger.Information("Applying SpiralDB overlay {0}...", Logger.Args(root));
            try {
                filesLoaded += LayerOverlay(root, spellbookStats, spellbooks, LoadCreatureSpellbooks, out _);
                filesLoaded += LayerOverlay(root, dropTableStats, dropTables, LoadDropTables, out _);
                filesLoaded += LayerOverlayGlobalRegistry(root, registryStats, globalRegistry);
                filesLoaded += LayerOverlay(root, inventoryStats, npcInventories, LoadNpcInventories, out _);
                filesLoaded += LayerOverlay(root, spellInventoryStats, npcSpellInventories, LoadNpcSpellInventories, out _);
                filesLoaded += LayerOverlay(root, npcDropTableStats, npcDropTables, LoadNpcDropTables, out _);
                filesLoaded += LayerOverlay(root, treasureCardStats, treasureCardInventories,
                    LoadTreasureCardInventories, out _);
                filesLoaded += LayerOverlay(root, questStats, questTemplatesByName,
                    (path, target) => LoadQuestTemplates(path, questTemplates, target), out var questsHere);
                ApplyQuestTombstones(root, questStats, questTemplatesByName, questsHere);
                filesLoaded += LayerOverlay(root, zoneStats, zoneData, LoadZoneData, out _);
            }
            catch (Exception ex) {
                Logger.Error("SpiralDB overlay {0} stopped part-way and may be partly applied: {1}",
                    Logger.Args(root, ex.Message));
            }
        }

        OverlayCategoryStats[] all = [
            spellbookStats, dropTableStats, registryStats, inventoryStats, spellInventoryStats,
            npcDropTableStats, treasureCardStats, questStats, zoneStats
        ];
        foreach (var stats in all.Where(s => s.Touched)) {
            Logger.Information(
                "SpiralDB overlay totals, {0}: base {1}, overlay {2} of {3} files loaded ({4} new, {5} overrides, " +
                "{6} failed), final {7}.",
                Logger.Args(stats.Name, stats.Base, stats.Loaded, stats.Files, stats.Added, stats.Overrides,
                    stats.Failed, stats.Count()));
        }

        if (questStats.Tombstoned > 0 || questStats.TombstonesUnmatched > 0) {
            Logger.Information("SpiralDB overlay totals, quest tombstones: {0} quests disabled, {1} names not found.",
                Logger.Args(questStats.Tombstoned, questStats.TombstonesUnmatched));
        }

        return filesLoaded;
    }

    // CLASSIC: one category of one overlay root. Returns the files the loader read; changedKeys gets
    // every key the root added or replaced.
    private static int LayerOverlay<TKey, TValue>(string root,
                                                  OverlayCategoryStats stats,
                                                  ConcurrentDictionary<TKey, TValue> target,
                                                  Func<string, ConcurrentDictionary<TKey, TValue>, int> loader,
                                                  out List<TKey> changedKeys) where TValue : class {
        changedKeys = [];
        var dir = Path.Combine(root, stats.Name);
        if (!Directory.Exists(dir)) {
            return 0;
        }

        stats.Touched = true;
        var files = Directory.EnumerateFiles(dir, "*.json").ToList();
        foreach (var file in files) {
            ReportEmptyOverlayFile(file);
        }

        var before = new Dictionary<TKey, TValue>(target, target.Comparer);
        var loaded = loader(root, target);

        var added = 0;
        var overridden = new List<TKey>();
        var keyless = new List<TKey>();
        foreach (var (key, value) in target) {
            if (!before.TryGetValue(key, out var previous)) {
                if (IsMissingKey(key)) {
                    keyless.Add(key);
                    continue;
                }

                added++;
                changedKeys.Add(key);
            }
            else if (!ReferenceEquals(previous, value)) {
                overridden.Add(key);
                changedKeys.Add(key);
            }
        }

        foreach (var key in keyless) {
            target.TryRemove(key, out _);
            Logger.Error("SpiralDB overlay {0}: a record has no key (empty name or template ID 0) and was dropped.",
                Logger.Args(dir));
        }

        var failed = files.Count - loaded;
        stats.Files += files.Count;
        stats.Loaded += loaded;
        stats.Added += added;
        stats.Overrides += overridden.Count;
        stats.Failed += failed;

        Logger.Information("SpiralDB overlay {0}: {1}: loaded {2} of {3} files ({4} new, {5} overrides).",
            Logger.Args(root, stats.Name, loaded, files.Count, added, overridden.Count));
        if (overridden.Count > 0) {
            Logger.Information("SpiralDB overlay {0}: {1} overrides: {2}",
                Logger.Args(root, stats.Name, JoinKeys(overridden)));
        }

        if (failed > 0) {
            Logger.Error("SpiralDB overlay {0}: {1} of {2} files did not load; the warnings above name each file " +
                         "and its error.",
                Logger.Args(dir, failed, files.Count));
        }

        var duplicates = loaded - added - overridden.Count - keyless.Count
            - (stats.Name == "ZoneTransfer" ? s_zoneMergesOntoSameRootRecords : 0);
        if (duplicates > 0) {
            Logger.Warning("SpiralDB overlay {0}: {1} files repeat a key used by another file in this folder; " +
                           "which one wins is not defined.",
                Logger.Args(dir, duplicates));
        }

        return loaded;
    }

    // CLASSIC: GlobalRegistry merges values from every file instead of keeping one record per file.
    private static int LayerOverlayGlobalRegistry(string root, OverlayCategoryStats stats, GlobalRegistryModel target) {
        var dir = Path.Combine(root, stats.Name);
        if (!Directory.Exists(dir)) {
            return 0;
        }

        stats.Touched = true;
        var files = Directory.EnumerateFiles(dir, "*.json").ToList();
        foreach (var file in files) {
            ReportEmptyOverlayFile(file);
        }

        var before = new Dictionary<string, float>(target.GlobalRegistryValues, target.GlobalRegistryValues.Comparer);
        var loaded = LoadGlobalRegistry(root, target);

        var added = 0;
        var overridden = new List<string>();
        foreach (var (key, value) in target.GlobalRegistryValues) {
            if (!before.TryGetValue(key, out var previous)) {
                added++;
            }
            else if (!previous.Equals(value)) {
                overridden.Add(key);
            }
        }

        var failed = files.Count - loaded;
        stats.Files += files.Count;
        stats.Loaded += loaded;
        stats.Added += added;
        stats.Overrides += overridden.Count;
        stats.Failed += failed;

        Logger.Information("SpiralDB overlay {0}: {1}: loaded {2} of {3} files ({4} new values, {5} changed).",
            Logger.Args(root, stats.Name, loaded, files.Count, added, overridden.Count));
        if (overridden.Count > 0) {
            Logger.Information("SpiralDB overlay {0}: {1} changed: {2}",
                Logger.Args(root, stats.Name, JoinKeys(overridden)));
        }

        if (failed > 0) {
            Logger.Error("SpiralDB overlay {0}: {1} of {2} files did not load; the warnings above name each file " +
                         "and its error.",
                Logger.Args(dir, failed, files.Count));
        }

        return loaded;
    }

    private static void ApplyQuestTombstones(string root,
                                             OverlayCategoryStats stats,
                                             ConcurrentDictionary<string, QuestTemplate> questsByName,
                                             List<string> questsInThisRoot) {
        var file = Path.Combine(root, stats.Name, OverlayTombstoneFile);
        if (!File.Exists(file)) {
            return;
        }

        stats.Touched = true;
        var definedHere = new HashSet<string>(questsInThisRoot, StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(file)) {
            var hash = line.IndexOf('#');
            var name = (hash >= 0 ? line[..hash] : line).Trim();
            if (name.Length == 0) {
                continue;
            }

            if (!questsByName.TryRemove(name, out _)) {
                stats.TombstonesUnmatched++;
                Logger.Warning("SpiralDB overlay {0}: {1} lists {2}, which is not loaded.",
                    Logger.Args(root, OverlayTombstoneFile, name));
                continue;
            }

            stats.Tombstoned++;
            if (definedHere.Contains(name)) {
                Logger.Warning("SpiralDB overlay {0}: quest {1} is both defined and disabled here; it stays disabled.",
                    Logger.Args(root, name));
            }
            else {
                Logger.Information("SpiralDB overlay {0}: disabled quest {1}.", Logger.Args(root, name));
            }
        }
    }

    // CLASSIC: removes the active profile's disabled_quests (content of a later update, such as Briskbreeze Tower's
    // Lost Lieutenant in arc1-2009h1) after every overlay has loaded, as a tombstone in the last root would.
    private static void ApplyProfileDisabledQuests(ConcurrentDictionary<string, QuestTemplate> questsByName) {
        if (!Imlight.Classic.ClassicRuntime.IsInitialized) {
            return;
        }

        var profile = Imlight.Classic.ClassicRuntime.Rules.Profile;
        var removed = RemoveDisabledQuests(questsByName, profile.DisabledQuests, out var unmatched);
        foreach (var name in removed) {
            Logger.Information("Profile {0} disables quest {1}.", Logger.Args(profile.Id, name));
        }

        foreach (var name in unmatched) {
            Logger.Warning("Profile {0} lists {1} under disabled_quests, but no such quest is loaded.",
                Logger.Args(profile.Id, name));
        }
    }

    /// <summary>
    /// CLASSIC: removes <paramref name="disabled"/> from <paramref name="questsByName"/>.
    /// </summary>
    /// <returns>The names removed; <paramref name="unmatched"/> gets the names that were not loaded.</returns>
    internal static List<string> RemoveDisabledQuests(ConcurrentDictionary<string, QuestTemplate> questsByName,
                                                      IEnumerable<string> disabled, out List<string> unmatched) {
        var removed = new List<string>();
        unmatched = [];
        foreach (var name in disabled) {
            if (questsByName.TryRemove(name, out _)) {
                removed.Add(name);
            }
            else {
                unmatched.Add(name);
            }
        }

        return removed;
    }

    // CLASSIC: the quest list in first-load order, one entry per name, each the winning (last loaded)
    // version; quests no longer in the by-name map (tombstoned, or without a name) are left out.
    private static List<QuestTemplate> RebuildQuestList(List<QuestTemplate> loadOrder,
                                                        ConcurrentDictionary<string, QuestTemplate> byName) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rebuilt = new List<QuestTemplate>(byName.Count);
        foreach (var quest in loadOrder) {
            // A file without m_questName is appended by the loader before its map insert throws.
            string name = quest.m_questName;
            if (name is not null && seen.Add(name) && byName.TryGetValue(name, out var current)) {
                rebuilt.Add(current);
            }
        }

        return rebuilt;
    }

    // The loaders skip a file that deserializes to null (empty, or the literal "null") without logging.
    private static void ReportEmptyOverlayFile(string file) {
        try {
            if (new FileInfo(file).Length > 16) {
                return;
            }

            var text = File.ReadAllText(file).Trim();
            if (text.Length == 0 || text == "null") {
                Logger.Error("SpiralDB overlay file {0} is empty and was skipped.", Logger.Args(file));
            }
        }
        catch (Exception ex) {
            Logger.Error("SpiralDB overlay file {0} could not be read: {1}", Logger.Args(file, ex.Message));
        }
    }

    private static bool IsMissingKey<TKey>(TKey key)
        => key switch {
            null => true,
            string s => s.Length == 0,
            ulong id => id == 0,
            _ => false
        };

    private static string JoinKeys<TKey>(List<TKey> keys)
        => keys.Count <= OverlayMaxKeysLogged
            ? string.Join(", ", keys)
            : string.Join(", ", keys.Take(OverlayMaxKeysLogged)) + $" and {keys.Count - OverlayMaxKeysLogged} more";

}
