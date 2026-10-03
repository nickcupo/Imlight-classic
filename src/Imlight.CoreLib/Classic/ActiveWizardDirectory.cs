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
 * ACTIVE WIZARD DIRECTORY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: each session's active wizard and its game object, pushed by the
 * session's WizardService when they change, so that zone actors, duels and
 * NPCs read them without asking the session (MSG_QUERYACTIVEWIZARD). The
 * Ask cost a temporary actor, a task and, for most callers, a blocked
 * dispatcher thread until the session got to it: NPCs asked about every
 * player in range once a second (about 1,600 asks a second with 15 players
 * in the Commons and Unicorn Way), triggers per event, duels per join.
 * The values are the same object references the Ask returned, read at the
 * time of the question, so the answer is unchanged; a session not in the
 * directory (not yet in the world, or gone) is still asked.
 *
 * USAGE EXAMPLE:
 * ActiveWizardDirectory.Set(SessionActor.ActorRef, wizard, gameObject);  // WizardService
 * if (ActiveWizardDirectory.TryGet(playerActor, out var wizard, out _)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The active wizard of each live session, keyed by the session actor (what zones know as the player actor).
/// </summary>
internal static class ActiveWizardDirectory {

    private sealed record Entry(Wizard Wizard, CoreObject GameObject);

    private static readonly ConcurrentDictionary<IActorRef, Entry> s_entries = new();
    private static readonly ConcurrentDictionary<ulong, Wizard> s_byCharId = new(); // CLASSIC: ambient wizards read friends' facts

    /// <summary>How many sessions are listed (tests and the PERF log).</summary>
    internal static int Count => s_entries.Count;

    /// <summary>The session's wizard changed (WizardService MSG_SETACTIVEWIZARD).</summary>
    internal static void SetWizard(IActorRef session, Wizard wizard) {
        if (session is null) {
            return;
        }

        s_entries.AddOrUpdate(session, _ => new Entry(wizard, null), (_, old) => old with { Wizard = wizard });
        if (wizard is not null) {
            s_byCharId[wizard.CharId] = wizard;
        }
    }

    /// <summary>The session's wizard game object changed (WizardService MSG_ADDPLAYERRSP).</summary>
    internal static void SetGameObject(IActorRef session, CoreObject gameObject) {
        if (session is null) {
            return;
        }

        s_entries.AddOrUpdate(session, _ => new Entry(null, gameObject), (_, old) => old with { GameObject = gameObject });
    }

    /// <summary>The session is going away.</summary>
    internal static void Remove(IActorRef session) {
        if (session is not null && s_entries.TryRemove(session, out var entry) && entry.Wizard is { } wizard) {
            s_byCharId.TryRemove(new KeyValuePair<ulong, Wizard>(wizard.CharId, wizard));
        }
    }

    /// <summary>The wizard of a live session by character id (the last session that set it).</summary>
    internal static bool TryGetByCharId(ulong charId, out Wizard wizard) => s_byCharId.TryGetValue(charId, out wizard);

    /// <summary>
    /// The session's wizard and game object as its WizardService holds them, when the session is listed and has a wizard.
    /// </summary>
    internal static bool TryGet(IActorRef session, out Wizard wizard, out CoreObject gameObject) {
        if (session is not null && s_entries.TryGetValue(session, out var entry) && entry.Wizard is not null
            && session is not IInternalActorRef { IsTerminated: true }) {
            (wizard, gameObject) = (entry.Wizard, entry.GameObject);

            return true;
        }

        (wizard, gameObject) = (null, null);

        return false;
    }

}
