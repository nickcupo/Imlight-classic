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
 * ARENA LADDER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: each wizard's Ranked standing (rating, wins, losses) in the 2009
 * arena, one document per wizard (ArenaLadders/{charId}). The arena
 * matchmaker writes it whether the wizard is online or not, so a wizard who
 * dropped out of a ranked match still takes the loss.
 *
 * NOTE:
 * A new document type: an older build never loads it, so it needs no
 * PlayerDataSchema bump (see Imlight.Classic.Admin.PlayerDataSchema). The
 * Arena Tickets stay in the wizard's game stats (m_currentArenaPoints).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>A wizard's Ranked standing.</summary>
public sealed class ArenaLadderEntry {

    public string? Id { get; set; }
    public ulong CharId { get; set; }
    public int Rating { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public DateTime LastMatchUtc { get; set; }

}

/// <summary>Where the ladder lives (RavenDB, or memory in tests).</summary>
internal interface IArenaLadderStore {

    ArenaLadderEntry? Load(ulong charId);

    void Save(ArenaLadderEntry entry);

}

/// <summary>CLASSIC: the Ranked ladder documents.</summary>
internal static class ArenaLadderCollection {

    public const string CollectionName = "ArenaLadders";

    public static string DocumentId(ulong charId) => $"{CollectionName}/{charId}";

    /// <summary>The RavenDB store.</summary>
    public sealed class Raven : IArenaLadderStore {

        public ArenaLadderEntry? Load(ulong charId) {
            using var session = PlayerDatabase.Instance.Store.OpenSession();

            return session.Load<ArenaLadderEntry>(DocumentId(charId));
        }

        public void Save(ArenaLadderEntry entry) {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            var id = DocumentId(entry.CharId);
            var doc = session.Load<ArenaLadderEntry>(id);
            if (doc is null) {
                doc = new ArenaLadderEntry { Id = id, CharId = entry.CharId };
                session.Store(doc, id);
                session.Advanced.GetMetadataFor(doc)[global::Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
            }

            doc.Rating = entry.Rating;
            doc.Wins = entry.Wins;
            doc.Losses = entry.Losses;
            doc.LastMatchUtc = entry.LastMatchUtc;
            session.SaveChanges();
        }

    }

    /// <summary>A store in memory (tests and rigs without a database).</summary>
    public sealed class Memory : IArenaLadderStore {

        private readonly ConcurrentDictionary<ulong, ArenaLadderEntry> _entries = new();

        public ArenaLadderEntry? Load(ulong charId) => _entries.TryGetValue(charId, out var e)
            ? new ArenaLadderEntry { Id = e.Id, CharId = e.CharId, Rating = e.Rating, Wins = e.Wins, Losses = e.Losses, LastMatchUtc = e.LastMatchUtc }
            : null;

        public void Save(ArenaLadderEntry entry) => _entries[entry.CharId] = entry;

    }

}
