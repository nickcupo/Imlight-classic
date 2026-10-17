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
 * CLIENT JOURNAL (hidden quests, finished tutorial tips)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: keeps two strings the r806919 client owns and formats itself, per character, and gives them back at
 * login. The server never parses them.
 *  - Hidden quests: the client's HiddenQuestsBehavior serializes its list into m_hiddenQuestsData and sends
 *    MSG_UPDATEUSERHIDDENQUESTS {Data} when it changes (FUN_140c12dd0). The player object carries the behavior,
 *    so the saved string goes back in m_hiddenQuestsData (WizardObjectLoader).
 *  - Finished tutorial tips: the client keeps them in its CharacterRegistry (a local file per character,
 *    Data/GameData/CharacterRegistry/<char>.xml) and tells the server MSG_READTUTORIALTIP {TutorialTipID}
 *    (CharacterRegistry::AddFinishedTip, FUN_14212b7b0). Its TutorialLogBehavior answers a server
 *    MSG_REQUESTTUTORIALTIPLOG with MSG_UPDATETUTORIALTIPLOG {TutorialTipData}, its whole log (FUN_1413045f0), and at
 *    login asks the server for the log (FUN_1421d5730) and merges the answer (FUN_1413048d0). So a finished tip asks
 *    the client for its log, the log is stored as it is, and it is the answer at the next login: a second Mac or a
 *    fresh install no longer shows tips again.
 * The active quest-helper quest (MSG_SWITCHACTIVEQUEST) also lives in that local registry (m_activeQuestId,
 * m_activeGoalId) and no server message carries it back, so it is not kept here.
 *
 * NOTE:
 * One small document per character (ClientJournals/<char id>), outside the Wizard document: no player-data
 * schema change. Loaded once at attach into Wizard.Journal (never persisted with the wizard). A string longer than
 * MaxLength is refused, not cut.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/17/2026
 */

using System;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Databases;
using Raven.Client;

namespace Imlight.CoreLib.Classic;

/// <summary>The client-formatted journal strings of one character.</summary>
internal sealed class ClientJournal {

    public ulong CharId { get; set; }
    public string HiddenQuests { get; set; }
    public string TutorialTips { get; set; }
    public DateTime UpdatedUtc { get; set; }

}

/// <summary>Loads and saves <see cref="ClientJournal"/> documents.</summary>
internal static class ClientJournalStore {

    internal const string CollectionName = "ClientJournals";
    internal const int MaxLength = 64 * 1024;

    internal static string IdFor(ulong charId) => $"{CollectionName}/{charId}";

    /// <summary>The string to keep, or null when it is not one (too long, or carries a NUL).</summary>
    internal static string Accept(string value)
        => value is null || value.Length > MaxLength || value.Contains('\0') ? null : value;

    /// <summary>The character's journal; an empty one when none is saved or the database cannot be read.</summary>
    internal static ClientJournal Load(ulong charId) {
        try {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            return session.Load<ClientJournal>(IdFor(charId)) ?? new ClientJournal { CharId = charId };
        }
        catch (Exception ex) {
            Logger.Warning("Client journal of {0} not loaded: {1}", Logger.Args(charId, ex.Message));
            return new ClientJournal { CharId = charId };
        }
    }

    /// <summary>Saves one field of the character's journal; returns whether it was written.</summary>
    internal static bool Save(ulong charId, Action<ClientJournal> change) {
        try {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            var journal = session.Load<ClientJournal>(IdFor(charId));
            if (journal is null) {
                journal = new ClientJournal { CharId = charId };
                session.Store(journal, IdFor(charId));
                session.Advanced.GetMetadataFor(journal)[Constants.Documents.Metadata.Collection] = CollectionName;
            }

            change(journal);
            journal.UpdatedUtc = DateTime.UtcNow;
            session.SaveChanges();
            return true;
        }
        catch (Exception ex) {
            Logger.Warning("Client journal of {0} not saved: {1}", Logger.Args(charId, ex.Message));
            return false;
        }
    }

}
