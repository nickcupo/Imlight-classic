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
 * PLAYER DATA SCHEMA VERSION
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the version of the player database's document shapes this build
 * writes. The database keeps the highest version that ever ran on it
 * (Meta/PlayerDataSchema); a build older than that refuses to start,
 * because the RavenDB client re-serializes whole documents and an older
 * build silently drops the fields it does not know (a wizard's dorm bank,
 * Wizard.StorageBehavior, on its first save).
 *
 * NOTE:
 * Bump Current only when an older build would lose or corrupt data written
 * by the new one (a new persisted field on a document the old build saves).
 * A new document type the old build never loads needs no bump. History:
 *   1  everything before the bank (implied by a database with no marker)
 *   2  2026-10-04 bank: Wizard.StorageBehavior, SharedBanks/{AccountId},
 *      shared items with owner id 0
 * Rolling back past a bump needs the pre-deploy backup restored
 * (deploy/linux/deploy-from-mac.sh --rollback --restore-backup).
 * The marker is the same for every rules profile.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

namespace Imlight.Classic.Admin;

/// <summary>What a build does with a player database, given the database's schema version.</summary>
public enum SchemaVerdict {
    /// <summary>No marker: a new database, or one from before markers (written by version 1 or 2). Write ours.</summary>
    Unmarked,
    /// <summary>The database is at this build's version.</summary>
    Same,
    /// <summary>The database is older: this build upgrades it (writes its version).</summary>
    Upgrade,
    /// <summary>The database was written by a newer build: refuse to start.</summary>
    TooNew,
}

/// <summary>CLASSIC: the player database schema version and the start-up check.</summary>
public static class PlayerDataSchema {

    /// <summary>The version this build writes.</summary>
    public const int Current = 2;

    /// <summary>The marker document's id.</summary>
    public const string DocumentId = "Meta/PlayerDataSchema";

    /// <summary>The exit code of a refused start (EX_CONFIG); the systemd unit does not restart on it.</summary>
    public const int RefusedExitCode = 78;

    /// <summary>Compares the database's version (null without a marker) with this build's.</summary>
    public static SchemaVerdict Check(int? stored, int build = Current) => stored switch {
        null => SchemaVerdict.Unmarked,
        var version when version == build => SchemaVerdict.Same,
        var version when version < build => SchemaVerdict.Upgrade,
        _ => SchemaVerdict.TooNew,
    };

    /// <summary>The version to store after the check, or null to leave the marker as it is.</summary>
    public static int? ToWrite(SchemaVerdict verdict, int build = Current)
        => verdict is SchemaVerdict.Unmarked or SchemaVerdict.Upgrade ? build : null;

    /// <summary>The log line for the verdict.</summary>
    public static string Describe(SchemaVerdict verdict, int? stored, int build = Current) => verdict switch {
        SchemaVerdict.Unmarked => $"Player data schema: no marker; marking the database as version {build}.",
        SchemaVerdict.Same => $"Player data schema: version {build}.",
        SchemaVerdict.Upgrade => $"Player data schema: upgrading the marker from version {stored} to {build}.",
        _ => $"Player data schema: the database is at version {stored}, newer than this build's {build}. "
             + "An older build would drop the newer fields on save, so the server will not start. Deploy the newer "
             + "build again, or restore the backup taken before it was deployed "
             + "(deploy-from-mac.sh --rollback --restore-backup).",
    };

}
