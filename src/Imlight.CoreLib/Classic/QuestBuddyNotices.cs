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
 * QUEST BUDDY NOTICES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner request 2026-10-09; not a 2010 feature, [Classic]
 * QuestBuddyNotices, default on): an automatic chat line tells a wizard
 * whether an online friend is on the same quest, further along or behind in
 * the same chain, or on a different quest. Nobody types a command. It comes:
 *   - when the wizard logs in, about each online friend, and to each online
 *     friend about the wizard (Login);
 *   - when a friend arrives in the same zone and instance (Arrived), unless
 *     the same news was given in the last hour;
 *   - when either wizard takes or finishes a quest and the news changed
 *     (QuestChanged, settled 15 s later so "finished A, took B" is one line).
 * Friends only (a live, unblocked friendship), never strangers; ambient
 * wizards neither get nor give notices.
 *
 * HOW IT SHOWS:
 * A whisper (MSG_DIRECTEDCHAT) from the friend, tagged "[Quest]", e.g.
 * "[Quest] Same quest chain, 2 quests ahead of you: Away to Unicorn Way! (in
 * Olde Town)". A whisper with the sender's packed name is a plain chat line in
 * r806919 (the ambient wizards' Text path); a non-modal MSG_SERVERMESSAGE
 * would be a "!" alert, and a chat line with a made-up speaker name froze the
 * client (Classic/ClassicChat.cs).
 *
 * FOR OTHER FEATURES:
 * Describe(viewer, friend) gives the relation and text for any two online
 * wizards (for example ambient-group helpers), with no friendship check.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

/// <summary>Why a quest buddy notice is considered.</summary>
internal enum QuestBuddyReason {

    /// <summary>A wizard logged in.</summary>
    Login,

    /// <summary>A friend arrived in the same zone and instance.</summary>
    Arrived,

    /// <summary>One of the two took or finished a quest.</summary>
    QuestChanged,

}

/// <summary>CLASSIC: the quest buddy notices (see the file header).</summary>
internal static class QuestBuddyNotices {

    /// <summary>A login notice is not repeated within this time (the login and the first attach both ask).</summary>
    internal static readonly TimeSpan LoginRepeat = TimeSpan.FromMinutes(2);

    /// <summary>The same news on arrival nearby is not repeated within this time.</summary>
    internal static readonly TimeSpan ArrivalRepeat = TimeSpan.FromHours(1);

    /// <summary>How long after a quest change the news is worked out (a finish and the next quest settle first).</summary>
    internal static readonly TimeSpan QuestChangeSettle = TimeSpan.FromSeconds(15);

    private sealed record Sent(string Signature, DateTime AtUtc);

    private static readonly ConcurrentDictionary<(ulong Viewer, ulong Friend), Sent> s_sent = new();
    private static readonly ConcurrentDictionary<ulong, byte> s_pendingChange = new();

    /// <summary>The [Classic] QuestBuddyNotices switch (default on).</summary>
    internal static bool Enabled {
        get {
            try {
                return ConfigurationManager.Settings["Classic.QuestBuddyNotices"].AsString("true")
                    .ToLowerInvariant() is "true" or "yes" or "1" or "on" or "enabled";
            }
            catch (Exception) {
                return true;
            }
        }
    }

    /// <summary>Test seam: quest titles without the quest database (tests must not start SpiralDB).</summary>
    internal static Func<string, string?>? TitlesForTests { get; set; }

    /// <summary>Test seam: where notices go instead of a session (viewer, friend, text).</summary>
    internal static Action<ulong, ulong, string>? SinkForTests { get; set; }

    /// <summary>A wizard logged in: notices both ways with every online friend.</summary>
    internal static void Login(ActorSystem? system, ulong charId) => Around(system, charId, QuestBuddyReason.Login, nearbyOnly: false);

    /// <summary>A wizard arrived in a zone: notices both ways with every online friend in the same place.</summary>
    internal static void Arrived(ActorSystem? system, ulong charId) => Around(system, charId, QuestBuddyReason.Arrived, nearbyOnly: true);

    /// <summary>A wizard took or finished a quest: after it settles, notices both ways where the news changed.</summary>
    internal static void QuestChanged(ActorSystem? system, ulong charId) {
        if (!Enabled || charId == 0 || !s_pendingChange.TryAdd(charId, 0)) {
            return;
        }

        _ = Task.Delay(QuestChangeSettle).ContinueWith(settled => {
            s_pendingChange.TryRemove(charId, out _);
            Around(system, charId, QuestBuddyReason.QuestChanged, nearbyOnly: false);
        }, TaskScheduler.Default);
    }

    private static void Around(ActorSystem? system, ulong charId, QuestBuddyReason reason, bool nearbyOnly) {
        try {
            if (!Enabled || charId == 0 || AmbientWizards.IsAmbientChar(charId)
                || !ActiveWizardDirectory.TryGetByCharId(charId, out var wizard) || wizard is null) {
                return;
            }

            var here = OnlinePlayerCollection.GetOnlinePlayer(charId);
            foreach (var friendId in FriendIds(wizard)) {
                if (AmbientWizards.IsAmbientChar(friendId) || OnlinePlayerCollection.GetOnlinePlayer(friendId) is not { } there
                    || !ActiveWizardDirectory.TryGetByCharId(friendId, out var friend) || friend is null) {
                    continue;
                }

                if (nearbyOnly && !SamePlace(here, there)) {
                    continue;
                }

                Consider(system, wizard, here, friend, there, reason);
                Consider(system, friend, there, wizard, here, reason);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Quest buddy notices for {0} failed: {1}", Logger.Args(charId, ex.Message));
        }
    }

    // Tells `viewer` about `friend` when the reason and what was last said allow it.
    private static void Consider(ActorSystem? system, Wizard viewer, OnlinePlayer? viewerOnline, Wizard friend,
        OnlinePlayer? friendOnline, QuestBuddyReason reason) {
        if (!IsFriend(viewer, friend.CharId) || viewerOnline is null) {
            return;
        }

        var (relation, text) = Describe(viewer, friend, SameZone(viewerOnline, friendOnline));
        var signature = QuestBuddies.Signature(relation);
        var key = (viewer.CharId, friend.CharId);
        var now = DateTime.UtcNow;
        if (!ShouldSend(s_sent.TryGetValue(key, out var last) ? last : null, signature, reason, now)) {
            return;
        }

        s_sent[key] = new Sent(signature, now);
        Deliver(system, viewerOnline, friend, text);
    }

    // CLASSIC: whether news `signature` goes out for `reason`, given what was last said to this viewer about this friend.
    private static bool ShouldSend(Sent? last, string signature, QuestBuddyReason reason, DateTime nowUtc) => reason switch {
        _ when last is null => true,
        QuestBuddyReason.Login => last.Signature != signature || nowUtc - last.AtUtc >= LoginRepeat,
        QuestBuddyReason.Arrived => last.Signature != signature || nowUtc - last.AtUtc >= ArrivalRepeat,
        _ => last.Signature != signature,
    };

    /// <summary>Test seam for <see cref="ShouldSend"/>: the last signature and when it went out, or null.</summary>
    internal static bool ShouldSendForTests(string? lastSignature, DateTime lastUtc, string signature, QuestBuddyReason reason,
        DateTime nowUtc)
        => ShouldSend(lastSignature is null ? null : new Sent(lastSignature, lastUtc), signature, reason, nowUtc);

    /// <summary>
    /// The relation of <paramref name="friend"/>'s quests to <paramref name="viewer"/>'s and the line the viewer reads
    /// (<paramref name="nearby"/>: the friend is in the same place, so the line does not say where).
    /// </summary>
    internal static (QuestRelation Relation, string Text) Describe(Wizard viewer, Wizard friend, bool nearby = false) {
        var relation = QuestBuddies.Compare(ActiveQuests(viewer), ActiveQuests(friend), QuestChains.Next, QuestChains.IsMainline);
        var title = relation.Quest is { } quest ? QuestTitle(quest) : null;
        var zone = nearby ? null : ZoneName(friend);

        return (relation, QuestBuddies.Text(relation, title, zone));
    }

    /// <summary>A quest as players see it: its title, else its internal name.</summary>
    internal static string QuestTitle(string questName) {
        if (TitlesForTests is { } titles) {
            return titles(questName) ?? questName;
        }

        try {
            var title = QuestTemplateCollection.GetQuestByName(questName)?.m_questTitle;
            var english = string.IsNullOrEmpty(title) ? "" : Locale.GetEnglishName(title);

            return string.IsNullOrWhiteSpace(english) ? questName : english.Trim();
        }
        catch (Exception) {
            return questName;
        }
    }

    private static string? ZoneName(Wizard wizard) {
        try {
            var english = string.IsNullOrEmpty(wizard.ZoneDisplayName) ? "" : Locale.GetEnglishName(wizard.ZoneDisplayName);

            return string.IsNullOrWhiteSpace(english) ? AmbientKnowledge.ZoneName(wizard.Zone ?? "") : english.Trim();
        }
        catch (Exception) {
            return null;
        }
    }

    private static List<string> ActiveQuests(Wizard wizard) {
        for (var attempt = 0; attempt < 3; attempt++) {
            try {
                return wizard.QuestBehavior?.CurrentQuestInstances?.Select(q => q.QuestName).Where(n => !string.IsNullOrEmpty(n))
                    .ToList() ?? [];
            }
            catch (InvalidOperationException) {
                // the wizard's own session changed the list meanwhile; read it again
            }
        }

        return [];
    }

    private static IEnumerable<ulong> FriendIds(Wizard wizard) {
        Relationship[] relationships;
        try {
            relationships = wizard.FriendsBehavior?.Relationships?.ToArray() ?? [];
        }
        catch (Exception) {
            relationships = [];
        }

        return relationships.Where(FriendRules.IsFriend)
            .Select(r => r.FirstPlayerId == wizard.CharId ? r.SecondPlayerId : r.FirstPlayerId)
            .Where(id => id != 0 && id != wizard.CharId).Distinct().ToList();
    }

    private static bool IsFriend(Wizard viewer, ulong friendId) {
        try {
            return viewer.FriendsBehavior.TryGetRelationship(friendId, out var relationship) && FriendRules.IsFriend(relationship);
        }
        catch (Exception) {
            return false;
        }
    }

    // The line leaves out where the friend is when they are in the same zone (any copy of it).
    private static bool SameZone(OnlinePlayer? a, OnlinePlayer? b)
        => a is not null && b is not null && string.Equals(a.CurrentZone, b.CurrentZone, StringComparison.OrdinalIgnoreCase);

    private static bool SamePlace(OnlinePlayer? a, OnlinePlayer? b)
        => a is not null && b is not null && string.Equals(a.CurrentZone, b.CurrentZone, StringComparison.OrdinalIgnoreCase)
           && a.InstanceOwnerId == b.InstanceOwnerId && a.HousingDeedId == b.HousingDeedId;

    private static void Deliver(ActorSystem? system, OnlinePlayer viewer, Wizard friend, string text) {
        if (SinkForTests is { } sink) {
            sink(viewer.CharacterId, friend.CharId, text);
            return;
        }

        if (system is null || string.IsNullOrEmpty(viewer.ActorPath)) {
            return;
        }

        system.ActorSelection(viewer.ActorPath).Tell(new GAME_5_PROTOCOL.MSG_DIRECTEDCHAT {
            SourceName = DataManipulation.SpacedHexStringToBytes(friend.PlayerNameBehavior.GetWizardNameAsByteHexString()),
            SourceID = friend.CharId,
            Message = text,
            Filter = 0,
        });
        Logger.Debug("Quest buddy notice to {0} about {1}: {2}", Logger.Args(viewer.CharacterId, friend.CharId, text));
    }

    internal static void ClearForTests() {
        s_sent.Clear();
        s_pendingChange.Clear();
    }

}

/// <summary>
/// CLASSIC: the quest chain graph of the served quests, for the quest buddy notices: quest B follows quest A when B
/// asks for A's "Complete" quest entry (a ReqHasEntry not negated) and both are story quests or share a chain name.
/// Built once on first use.
/// </summary>
internal static class QuestChains {

    private static readonly object s_lock = new();
    private static IReadOnlyDictionary<string, string[]>? s_next;
    private static IReadOnlySet<string>? s_mainline;

    /// <summary>The quests that directly follow <paramref name="questName"/> in its chain.</summary>
    internal static IEnumerable<string> Next(string questName) {
        Ensure();

        return s_next!.TryGetValue(questName, out var next) ? next : [];
    }

    /// <summary>True for a story (mainline) quest.</summary>
    internal static bool IsMainline(string questName) {
        Ensure();

        return s_mainline!.Contains(questName);
    }

    /// <summary>Builds the graph from <paramref name="quests"/> (tests; the server builds it from the served quests).</summary>
    internal static void Use(IEnumerable<QuestTemplate>? quests) {
        lock (s_lock) {
            (s_next, s_mainline) = Build(quests ?? []);
        }
    }

    private static void Ensure() {
        if (s_next is not null) {
            return;
        }

        lock (s_lock) {
            if (s_next is not null) {
                return;
            }

            List<QuestTemplate> quests;
            try {
                quests = QuestTemplateCollection.GetAllQuests() ?? [];
            }
            catch (Exception) {
                quests = [];
            }

            (s_next, s_mainline) = Build(quests);
        }
    }

    internal static (IReadOnlyDictionary<string, string[]> Next, IReadOnlySet<string> Mainline) Build(IEnumerable<QuestTemplate> quests) {
        var list = quests.Where(q => !string.IsNullOrEmpty(q?.m_questName)).ToList();
        var mainline = new HashSet<string>(list.Where(q => q.m_mainline).Select(q => q.m_questName), StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in list) {
            foreach (var prereq in CompletedPrereqs(quest.m_requirements)) {
                if (string.Equals(prereq, quest.m_questName, StringComparison.OrdinalIgnoreCase)
                    || !QuestBuddies.SameChain(prereq, mainline.Contains(prereq), quest.m_questName, quest.m_mainline)) {
                    continue;
                }

                if (!next.TryGetValue(prereq, out var after)) {
                    next[prereq] = after = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                after.Add(quest.m_questName);
            }
        }

        return (next.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.OrdinalIgnoreCase), mainline);
    }

    // The quests whose "Complete" entry a requirement list asks for (nested lists included, negated ones skipped).
    private static IEnumerable<string> CompletedPrereqs(RequirementList? requirements) {
        foreach (var requirement in requirements?.m_requirements ?? []) {
            if (requirement is null || requirement.m_applyNOT) {
                continue;
            }

            if (requirement is RequirementList nested) {
                foreach (var name in CompletedPrereqs(nested)) {
                    yield return name;
                }

                continue;
            }

            if (requirement is ReqHasEntry { m_isQuestRegistry: true } entry
                && string.Equals(entry.m_entryName, "Complete", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entry.m_questName)) {
                yield return entry.m_questName;
            }
        }
    }

    internal static void ClearForTests() {
        lock (s_lock) {
            s_next = null;
            s_mainline = null;
        }
    }

}
