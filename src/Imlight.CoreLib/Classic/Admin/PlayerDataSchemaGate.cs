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
 * PLAYER DATA SCHEMA GATE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: reads and writes the player database's schema marker
 * (Meta/PlayerDataSchema, see Imlight.Classic.Admin.PlayerDataSchema) at
 * start-up, before any server listens, and refuses a database written by a
 * newer build.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using Imlight.Classic.Admin;
using Imlight.Common;
using Raven.Client.Documents;

namespace Imlight.CoreLib.Classic.Admin;

/// <summary>The marker document.</summary>
public sealed class PlayerDataSchemaMarker {
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>The build that last wrote it (its informational version).</summary>
    public string WrittenBy { get; set; } = "";
}

/// <summary>CLASSIC: the start-up schema check.</summary>
public static class PlayerDataSchemaGate {

    /// <summary>
    /// Checks the database's schema marker and brings it up to this build's version. False when the database was
    /// written by a newer build (the caller must not start). A store that cannot be read is logged and allowed: the
    /// server's own database start-up reports it.
    /// </summary>
    public static bool Check(IDocumentStore? store) {
        if (store is null) {
            Logger.Warning("Player data schema: no database store, so the schema version was not checked.");

            return true;
        }

        try {
            using var session = store.OpenSession();
            var marker = session.Load<PlayerDataSchemaMarker>(PlayerDataSchema.DocumentId);
            var stored = marker?.Version;
            var verdict = PlayerDataSchema.Check(stored);
            var line = PlayerDataSchema.Describe(verdict, stored);
            if (verdict == SchemaVerdict.TooNew) {
                Logger.Fatal(line);

                return false;
            }

            Logger.Information(line);
            if (PlayerDataSchema.ToWrite(verdict) is { } version) {
                marker ??= new PlayerDataSchemaMarker();
                marker.Version = version;
                marker.UpdatedUtc = DateTime.UtcNow;
                marker.WrittenBy = typeof(PlayerDataSchemaGate).Assembly.GetName().Version?.ToString() ?? "";
                session.Store(marker, PlayerDataSchema.DocumentId);
                session.SaveChanges();
            }

            return true;
        }
        catch (Exception ex) {
            Logger.Error("Player data schema: could not read or write the marker: {0}", Logger.Args(ex.Message));

            return true;
        }
    }

}
