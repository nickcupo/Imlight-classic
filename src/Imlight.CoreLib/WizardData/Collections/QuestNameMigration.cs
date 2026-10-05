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
 * QUEST NAME MIGRATION (player data schema 4)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: renames the old (made-up) quest names of
 * classic-data/quests/quest-name-aliases.yaml in saved characters: the
 * QuestName of active quest instances (QuestInstances) and the registry
 * keys that carry a quest name (Wizards' QuestBehavior.Registry:
 * "<quest>_Complete", other "<quest>_<entry>" quest-registry keys and
 * "QT-<quest>"). Goal names are unchanged, so a quest in progress goes on
 * from the goal it was on. Runs at every start-up after the schema check;
 * idempotent (a second run finds nothing). [Classic] QuestNameMigration =
 * apply (default) | dry-run (count and log, write nothing) | off.
 *
 * NOTE:
 * The Wizards to change are found with a plain field projection; only those
 * are loaded and saved (as every character save does). A key already present
 * under the new name keeps the larger value (QuestNameAliases.MigrateRegistry,
 * unit-tested on a schema-3 snapshot).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>CLASSIC: what a quest name migration found (and, unless a dry run, changed).</summary>
public sealed record QuestNameMigrationResult(bool DryRun, int QuestInstances, int Wizards, int RegistryKeys) {

    /// <summary>True when nothing carried an old name.</summary>
    public bool NothingToDo => QuestInstances == 0 && RegistryKeys == 0;

}

/// <summary>CLASSIC (player data schema 4): renames old quest names in saved characters.</summary>
public static class QuestNameMigration {

    /// <summary>The setting that picks the mode: apply, dry-run or off.</summary>
    public const string Setting = "Classic.QuestNameMigration";

    // The registry projection: the document id and the quest registry, nothing else of the Wizard.
    private sealed class RegistryRow {
        public string Id { get; set; } = "";
        public ulong CharId { get; set; }
        public Dictionary<string, ulong>? Registry { get; set; }
    }

    /// <summary>
    /// Runs the migration in the mode [Classic] QuestNameMigration names (apply when unset). Returns null when off
    /// or when no aliases are loaded.
    /// </summary>
    public static QuestNameMigrationResult? RunConfigured(IDocumentStore store) {
        var mode = (ConfigurationManager.Settings[Setting].AsString() ?? "").Trim().ToLowerInvariant();
        if (mode == "off") {
            Logger.Warning("Quest name migration: off ([Classic] QuestNameMigration = off); old quest names stay in saved characters.");

            return null;
        }

        return Run(store, QuestNameAliases.Current, dryRun: mode is "dry-run" or "dryrun");
    }

    /// <summary>Renames old quest names in saved characters (or only counts them, for a dry run).</summary>
    public static QuestNameMigrationResult? Run(IDocumentStore store, QuestNameAliases aliases, bool dryRun) {
        if (aliases.Count == 0) {
            Logger.Information("Quest name migration: no quest name aliases loaded; nothing to do.");

            return null;
        }

        var verb = dryRun ? "would rename" : "renamed";
        var olds = aliases.Aliases.Select(alias => alias.Old).ToList();

        // 1. Active quest instances under an old name.
        var instances = 0;
        using (var session = store.OpenSession()) {
            session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
            var rows = session.Query<QuestInstance>(collectionName: QuestInstanceCollection.CollectionName)
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(q => q.QuestName.In(olds))
                .ToList();
            foreach (var row in rows) {
                var renamed = aliases.Canonical(row.QuestName);
                Logger.Information("Quest name migration: {0} quest instance {1} of character {2}: {3} -> {4} (goals {5}).",
                    Logger.Args(verb, row.ID, row.OwnerCharId, row.QuestName, renamed,
                        string.Join(", ", row.GoalProgress.Select(g => $"{g.GoalName}={g.CurrentProgress}"))));
                row.QuestName = renamed;
                instances++;
            }

            if (!dryRun && instances > 0) {
                session.SaveChanges();
            }
        }

        // 2. Registry keys with an old name in them. Find them with a plain field projection (no JavaScript, so the
        // 64-bit ids and values keep their precision), then change only those Wizards, as every save does.
        var wizards = 0;
        var keys = 0;
        var toChange = new List<string>();
        using (var session = store.OpenSession()) {
            var rows = session.Advanced
                .RawQuery<RegistryRow>($"from '{WizardCollection.CollectionName}' select id() as Id, CharId, QuestBehavior.Registry as Registry")
                .WaitForNonStaleResults(TimeSpan.FromSeconds(30))
                .ToList();
            foreach (var row in rows) {
                if (row.Registry is not { Count: > 0 } registry) {
                    continue;
                }

                var renames = aliases.RegistryRenames(registry.Keys);
                if (renames.Count == 0) {
                    continue;
                }

                wizards++;
                keys += renames.Count;
                toChange.Add(row.Id);
                Logger.Information("Quest name migration: {0} {1} registry key(s) of character {2}: {3}.",
                    Logger.Args(verb, renames.Count, row.CharId, string.Join(", ", renames.Select(r => $"{r.From} -> {r.To}"))));
            }
        }

        if (!dryRun && toChange.Count > 0) {
            using var session = store.OpenSession();
            session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
            foreach (var (id, wizard) in session.Load<Wizard>(toChange)) {
                if (wizard?.QuestBehavior?.Registry is { } registry) {
                    aliases.MigrateRegistry(registry);
                }
                else {
                    Logger.Warning("Quest name migration: {0} has no quest registry on reload; skipped.", Logger.Args(id));
                }
            }

            session.SaveChanges();
        }

        var result = new QuestNameMigrationResult(dryRun, instances, wizards, keys);
        Logger.Information("Quest name migration{0}: {1} quest instance(s) and {2} registry key(s) on {3} character(s) {4} " +
                           "({5} quest name aliases).",
            Logger.Args(dryRun ? " (dry run)" : "", instances, keys, wizards, dryRun ? "to rename" : "renamed", aliases.Count));

        return result;
    }

}
