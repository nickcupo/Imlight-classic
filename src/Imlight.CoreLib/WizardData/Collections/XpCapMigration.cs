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
 * XP CAP MIGRATION (no player data schema change)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: when the server first starts under a profile whose level cap is
 * higher than the cap the database's XP was settled under, a wizard exactly
 * at the old cap goes back to that level's XP total (Imlight.Classic.Admin.
 * XpCapSettlement; October 2010 notes, owner ruling 2026-10-16). Runs at every
 * start-up after the schema check; the record Meta/XpCapSettlement (the cap,
 * a number) makes it run once per raise. Under october-2010-arc1 (cap 50, as
 * every earlier profile) it only writes the record "50" and touches no wizard.
 * [Classic] XpCapMigration = apply (default) | dry-run (count and log, write
 * nothing) | off.
 *
 * NOTE:
 * No PlayerDataSchema bump: the record is a new document an older build never
 * loads, and a wizard changes by one existing number (patched in place, only
 * if it still holds the level and XP that were read). An older build after a
 * rollback keeps the reset XP (a valid value); getting the banked XP back
 * needs the pre-deploy backup.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic;
using Imlight.Classic.Admin;
using Imlight.Common;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>CLASSIC: the record of the cap the database's XP was settled under.</summary>
public sealed class XpCapSettlementMarker {
    public int Cap { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>The wizards the last raise reset.</summary>
    public int LastReset { get; set; }
    /// <summary>The build that last wrote it (its informational version).</summary>
    public string WrittenBy { get; set; } = "";
}

/// <summary>CLASSIC: what an XP cap migration found (and, unless a dry run, changed).</summary>
public sealed record XpCapMigrationResult(bool DryRun, int? RecordedCap, int? CurrentCap, int? SweptFromCap, int Wizards, long XpRemoved);

/// <summary>CLASSIC: resets XP banked at an old level cap, once per raise.</summary>
public static class XpCapMigration {

    /// <summary>The setting that picks the mode: apply, dry-run or off.</summary>
    public const string Setting = "Classic.XpCapMigration";

    private const string PatchScript =
        "if (this.MagicSchoolBehavior && this.MagicSchoolBehavior.Level === args.level"
        + " && this.MagicSchoolBehavior.ExperiencePoints === args.from) { this.MagicSchoolBehavior.ExperiencePoints = args.to; }";

    private sealed class XpRow {
        public string Id { get; set; } = "";
        public ulong CharId { get; set; }
        public int Level { get; set; }
        public int Xp { get; set; }
    }

    /// <summary>Runs in the configured mode under the active profile's cap. Null when off.</summary>
    public static XpCapMigrationResult? RunConfigured(IDocumentStore store) {
        var mode = (ConfigurationManager.Settings[Setting].AsString() ?? "").Trim().ToLowerInvariant();
        if (mode == "off") {
            Logger.Warning("XP cap migration: off ([Classic] XpCapMigration = off); XP banked at an old cap stays.");

            return null;
        }

        var cap = ClassicRuntime.IsInitialized && ClassicRuntime.IsActive ? ClassicRuntime.Rules.Profile.LevelCap : null;

        return Run(store, cap, Imlight.CoreLib.Shared.Character.MagicLevelsConfig.GetExperiencePointsAtLevel, dryRun: mode is "dry-run" or "dryrun");
    }

    /// <summary>
    /// Settles the database's XP under <paramref name="currentCap"/>. <paramref name="xpToReach"/> gives the XP total on
    /// reaching a level (the profile's table); a level it does not know (0) is left alone and the record not written.
    /// </summary>
    public static XpCapMigrationResult Run(IDocumentStore store, int? currentCap, Func<int, int> xpToReach, bool dryRun) {
        int? recorded;
        using (var session = store.OpenSession()) {
            recorded = session.Load<XpCapSettlementMarker>(XpCapSettlement.DocumentId)?.Cap;
        }

        var plan = XpCapSettlement.Plan(recorded, currentCap);
        if (plan.NothingToDo) {
            Logger.Information("XP cap migration: XP settled under level cap {0}; nothing to do.",
                Logger.Args(recorded?.ToString() ?? $"{XpCapSettlement.UnrecordedCap} (no record)"));

            return new XpCapMigrationResult(dryRun, recorded, currentCap, null, 0, 0);
        }

        var verb = dryRun ? "would reset" : "reset";
        var wizards = 0;
        long removed = 0;
        if (plan.SweepFromCap is int oldCap) {
            var total = xpToReach(oldCap);
            if (total <= 0) {
                Logger.Error("XP cap migration: the XP total of level {0} is unknown; nothing reset and the record left at {1}.",
                    Logger.Args(oldCap, recorded?.ToString() ?? "none"));

                return new XpCapMigrationResult(dryRun, recorded, currentCap, null, 0, 0);
            }

            List<XpRow> rows;
            using (var session = store.OpenSession()) {
                rows = session.Advanced
                    .RawQuery<XpRow>($"from '{WizardCollection.CollectionName}' where MagicSchoolBehavior.Level = $level"
                                     + " select id() as Id, CharId, MagicSchoolBehavior.Level as Level, MagicSchoolBehavior.ExperiencePoints as Xp")
                    .AddParameter("level", oldCap)
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(30))
                    .ToList();
            }

            foreach (var row in rows) {
                if (XpCapSettlement.Settled(row.Level, row.Xp, oldCap, total) is not int settled) {
                    continue;
                }

                Logger.Information("XP cap migration: {0} character {1} (level {2}): XP {3} -> {4} (banked at the level {5} cap, not paid "
                                   + "when the cap rises to {6}).",
                    Logger.Args(verb, row.CharId, row.Level, row.Xp, settled, oldCap, currentCap));
                if (!dryRun) {
                    store.Operations.Send(new PatchOperation(row.Id, null, new PatchRequest {
                        Script = PatchScript,
                        Values = { ["level"] = row.Level, ["from"] = row.Xp, ["to"] = settled },
                    }));
                }

                wizards++;
                removed += row.Xp - settled;
            }
        }

        if (!dryRun && plan.CapToRecord is int record) {
            using var session = store.OpenSession();
            var marker = session.Load<XpCapSettlementMarker>(XpCapSettlement.DocumentId) ?? new XpCapSettlementMarker();
            marker.Cap = record;
            marker.UpdatedUtc = DateTime.UtcNow;
            marker.LastReset = plan.SweepFromCap is null ? marker.LastReset : wizards;
            marker.WrittenBy = typeof(XpCapMigration).Assembly.GetName().Version?.ToString() ?? "";
            session.Store(marker, XpCapSettlement.DocumentId);
            session.SaveChanges();
        }

        Logger.Information("XP cap migration{0}: level cap {1} (XP settled under {2}); {3} character(s) {4}, {5} XP; record {6}.",
            Logger.Args(dryRun ? " (dry run)" : "", currentCap, recorded?.ToString() ?? $"{XpCapSettlement.UnrecordedCap}, no record",
                wizards, verb, removed, dryRun ? "not written" : $"now {plan.CapToRecord}"));

        return new XpCapMigrationResult(dryRun, recorded, currentCap, plan.SweepFromCap, wizards, removed);
    }

}
