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
 * GROUP INSTANCES (2009 DUNGEON RULES)
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 rules for entering a dungeon instance together:
 *   - a dungeon sigil starts a countdown; everyone who uses the same sigil
 *     before it reaches zero (up to four) enters the SAME new instance;
 *   - an instance holds up to four wizards; a friend may teleport in until
 *     it is full ("Your friend is in a full instance");
 *   - doors and triggers inside an instance lead to the same run's other
 *     zones, so a group stays together through a multi-zone dungeon;
 *   - an instance nobody is in is kept 30 minutes, then reset.
 *
 * EVIDENCE (Wizard101 wiki, wizard101.fandom.com, revisions before 2010-05-26):
 *   - Quests oldid 4887 (2009-01-23), "Instances": "Up to four people at a time
 *     can be in an instance"; "You can also enter instanced areas with up to four
 *     players. To do so, you all have to interact with the circles before the
 *     countdown reaches zero"; "Leaving any other way gives you 30 minutes to
 *     return before it resets." Unchanged through oldid 62609 (2010-02-16).
 *   - Quests oldid 21718 (2009-06-20), "Gauntlets": in a gauntlet "anyone who tries
 *     to teleport to you will get the message: That player is busy right now";
 *     ordinary instances let friends teleport in.
 *   - Take It by Storm oldid 41949 (2009-09-13): friends teleporting to a wizard
 *     alone in a gauntlet "will receive the, 'Your friend is in a full instance,'
 *     message".
 *   - Emperor's Retreat oldid 56080 (2010-01-08): "Only four at a time can be in a
 *     Dungeon".
 *   - Duels oldid 66643 (2010-04-13): "If you join a duel in progress, you will have
 *     to wait for the round to end".
 *
 * NOTE:
 * A sigil run's instance is keyed by a run id (a random 64-bit number, like a
 * character id), not by the wizard who started it, so the starter going back
 * through the sigil starts a fresh run without pulling the old one out from
 * under friends still inside.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Imlight.CoreLib.Classic;

/// <summary>A wizard's place in a sigil's group: the run's instance, the face slot and the time left.</summary>
public sealed record SigilTicket(ulong RunId, int Slot, double SecondsLeft, bool Started);

/// <summary>
/// CLASSIC: the wizards gathering on one dungeon sigil during its countdown. The first starts the countdown; the
/// others (up to <see cref="GroupInstances.MaxGroupSize"/> in all) join it and enter the same instance when it ends.
/// </summary>
public sealed class SigilGroup {

    private readonly List<ulong> _members = [];

    public SigilGroup(ulong runId, DateTime startedUtc, double countdownSeconds) {
        RunId = runId;
        StartedUtc = startedUtc;
        CountdownSeconds = countdownSeconds;
    }

    public ulong RunId { get; }
    public DateTime StartedUtc { get; }
    public double CountdownSeconds { get; }
    public DateTime EndsUtc => StartedUtc.AddSeconds(CountdownSeconds);
    public IReadOnlyList<ulong> Members => _members;

    /// <summary>True while wizards may still join: the countdown has not reached zero.</summary>
    public bool IsOpen(DateTime nowUtc) => nowUtc < EndsUtc;

    /// <summary>
    /// Takes <paramref name="charId"/> into the group, or gives back its slot if it is already in; null when the
    /// countdown is over or every slot is taken.
    /// </summary>
    public SigilTicket? Join(ulong charId, DateTime nowUtc) {
        if (!IsOpen(nowUtc)) {
            return null;
        }

        var slot = _members.IndexOf(charId);
        if (slot < 0) {
            if (_members.Count >= GroupInstances.MaxGroupSize) {
                return null;
            }

            _members.Add(charId);
            slot = _members.Count - 1;
        }

        return new SigilTicket(RunId, slot, Math.Max(0.5, (EndsUtc - nowUtc).TotalSeconds), slot == 0);
    }

}

/// <summary>
/// CLASSIC: the 2009 group-instance rules (see the file header for the evidence).
/// </summary>
public static class GroupInstances {

    /// <summary>"Up to four people at a time can be in an instance."</summary>
    public const int MaxGroupSize = 4;

    /// <summary>The countdown a dungeon sigil runs before it takes its group in.</summary>
    public const double SigilCountdownSeconds = 10.0;

    /// <summary>"Leaving any other way gives you 30 minutes to return before it resets."</summary>
    public static readonly TimeSpan EmptyInstanceLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a transfer's instance is remembered for the attach that follows it.</summary>
    public static readonly TimeSpan PendingEntryLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The message a friend gets when the instance is full (Take It by Storm oldid 41949).</summary>
    public const string FullInstanceMessage = "Your friend is in a full instance.";

    /// <summary>The error a zone answers a transfer into a full instance with.</summary>
    public const string FullInstanceError = "ERROR_InstanceFull";

    private static readonly ConcurrentDictionary<ulong, DateTime> s_runs = new();
    private static readonly ConcurrentDictionary<ulong, PendingEntry> s_pending = new();

    private sealed record PendingEntry(string Zone, ulong OwnerId, DateTime ExpiresUtc);

    /// <summary>A new sigil run id; never 0 and never a run already in use.</summary>
    public static ulong NewRunId(DateTime nowUtc) {
        while (true) {
            var id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            if (id != 0 && s_runs.TryAdd(id, nowUtc)) {
                return id;
            }
        }
    }

    /// <summary>True when <paramref name="ownerId"/> keys a sigil run's instance rather than a wizard's own.</summary>
    public static bool IsRun(ulong ownerId) => ownerId != 0 && s_runs.ContainsKey(ownerId);

    /// <summary>Forgets a run whose instance has been dropped.</summary>
    public static void EndRun(ulong runId) => s_runs.TryRemove(runId, out _);

    /// <summary>
    /// How many wizards an instance holds: four, or fewer when the zone's own hard limit is lower (a one-wizard
    /// gauntlet room is "a full instance" to everyone else).
    /// </summary>
    public static int Capacity(int zoneHardLimit)
        => zoneHardLimit is > 0 and < MaxGroupSize ? zoneHardLimit : MaxGroupSize;

    /// <summary>True when an instance with <paramref name="players"/> wizards in it has no room for one more.</summary>
    public static bool IsFull(int players, int zoneHardLimit) => players >= Capacity(zoneHardLimit);

    /// <summary>
    /// The instance a zone transfer should use. A door or trigger inside an instance (<paramref name="keepInstance"/>)
    /// leads to the same instance's zones; anything else keeps the owner the transfer named.
    /// </summary>
    public static ulong OwnerForTransfer(ulong requestedOwner, bool keepInstance, ulong currentInstanceOwner)
        => keepInstance && currentInstanceOwner != 0 ? currentInstanceOwner : requestedOwner;

    /// <summary>
    /// Remembers that <paramref name="charId"/> is on the way into <paramref name="zone"/> of instance
    /// <paramref name="ownerId"/>, for the attach that follows the client's zone change.
    /// </summary>
    public static void QueueEntry(ulong charId, string zone, ulong ownerId, DateTime nowUtc) {
        if (ownerId == 0 || ownerId == charId) {
            s_pending.TryRemove(charId, out _);

            return;
        }

        s_pending[charId] = new PendingEntry(zone, ownerId, nowUtc + PendingEntryLifetime);
    }

    /// <summary>
    /// The instance owner the attach of <paramref name="charId"/> into <paramref name="zone"/> should use: a queued
    /// entry (taken once), else a held duel seat, else the wizard.
    /// </summary>
    public static ulong OwnerForAttach(ulong charId, string zone, DateTime nowUtc) {
        if (s_pending.TryRemove(charId, out var entry) && entry.ExpiresUtc > nowUtc
                && string.Equals(entry.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            return entry.OwnerId;
        }

        return ActiveDuels.InstanceOwnerForLogin(charId, zone, nowUtc);
    }

    /// <summary>The wizards of <paramref name="players"/> in instance <paramref name="ownerId"/> of <paramref name="zone"/>.</summary>
    public static int CountIn<T>(IEnumerable<T> players, Func<T, string?> zoneOf, Func<T, ulong> ownerOf, string zone, ulong ownerId)
        => players.Count(p => ownerOf(p) == ownerId && string.Equals(zoneOf(p), zone, StringComparison.OrdinalIgnoreCase));

    internal static void ClearForTests() {
        s_runs.Clear();
        s_pending.Clear();
    }

}
