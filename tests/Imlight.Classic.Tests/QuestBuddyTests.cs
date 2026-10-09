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
 * QUEST BUDDY AND SHARED INSTANCE GOAL TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (group questing, 2026-10-09): the quest buddy relation (same quest,
 * same chain ahead/behind, different), its chain graph built from quest
 * prerequisites, when a notice goes out, that only friends hear, and which
 * goals are shared inside an instance.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection("QuestBuddyNotices")]
public sealed class QuestBuddyTests {

    // A -> B -> C (story), C -> D (story); S1 -> S2 a side chain; B -> SIDE (side quest after a story quest).
    private static readonly Dictionary<string, string[]> s_graph = new(StringComparer.OrdinalIgnoreCase) {
        ["WC-MAIN-C01-001"] = ["WC-MAIN-C01-002"],
        ["WC-MAIN-C01-002"] = ["WC-MAIN-C01-003"],
        ["WC-MAIN-C01-003"] = ["KT-MAIN-C01-001"],
        ["WC-SIDE-C01-001"] = ["WC-SIDE-C01-002"],
    };

    private static IEnumerable<string> Next(string quest) => s_graph.TryGetValue(quest, out var next) ? next : [];

    private static bool Main(string quest) => quest.Contains("MAIN", StringComparison.Ordinal);

    [Theory]
    [InlineData("WC-UNICORN-MAIN-007", "WC-UNICORN-MAIN")]
    [InlineData("WC-COMMONS-MAIN-002-FIRE", "WC-COMMONS-MAIN")]
    [InlineData("WC-OLDE-MAIN-003A", "WC-OLDE-MAIN")]
    [InlineData("WC-ST01-C01-005", "WC-ST01-C01")]
    [InlineData("NoNumberHere", null)]
    public void ChainKeyDropsTheStepNumberAndVariant(string name, string? chain)
        => Assert.Equal(chain, QuestBuddies.ChainKey(name));

    [Fact]
    public void SameQuestWinsAndPrefersAStoryQuest() {
        var relation = QuestBuddies.Compare(["WC-SIDE-C01-001", "WC-MAIN-C01-002"], ["WC-MAIN-C01-002", "WC-SIDE-C01-001"], Next, Main);
        Assert.Equal(new QuestRelation(QuestRelationKind.SameQuest, "WC-MAIN-C01-002", 0), relation);
    }

    [Fact]
    public void AFriendFurtherAlongTheChainIsAheadByTheStepCount() {
        var relation = QuestBuddies.Compare(["WC-MAIN-C01-001"], ["WC-MAIN-C01-003"], Next, Main);
        Assert.Equal(new QuestRelation(QuestRelationKind.SameChain, "WC-MAIN-C01-003", 2), relation);
    }

    [Fact]
    public void AFriendEarlierInTheChainIsBehind() {
        var relation = QuestBuddies.Compare(["KT-MAIN-C01-001"], ["WC-MAIN-C01-002"], Next, Main);
        Assert.Equal(new QuestRelation(QuestRelationKind.SameChain, "WC-MAIN-C01-002", -2), relation);
    }

    [Fact]
    public void UnrelatedQuestsAreDifferentAboutTheFriendsStoryQuest() {
        var relation = QuestBuddies.Compare(["WC-SIDE-C01-002"], ["WC-SIDE-C01-009", "WC-MAIN-C01-002"], Next, Main);
        Assert.Equal(new QuestRelation(QuestRelationKind.Different, "WC-MAIN-C01-002", 0), relation);
        Assert.Equal(new QuestRelation(QuestRelationKind.Different, null, 0), QuestBuddies.Compare(["WC-MAIN-C01-001"], [], Next, Main));
    }

    [Fact]
    public void TheChainSearchStopsAtMaxSteps() {
        var chain = Enumerable.Range(1, QuestBuddies.MaxSteps + 5).Select(i => $"X-MAIN-{i:000}").ToList();
        IEnumerable<string> next(string q) => chain.IndexOf(q) is var i and >= 0 && i + 1 < chain.Count ? [chain[i + 1]] : [];
        Assert.Equal(QuestRelationKind.SameChain, QuestBuddies.Compare([chain[0]], [chain[QuestBuddies.MaxSteps]], next, Main).Kind);
        Assert.Equal(QuestRelationKind.Different, QuestBuddies.Compare([chain[0]], [chain[QuestBuddies.MaxSteps + 1]], next, Main).Kind);
    }

    [Fact]
    public void TheLinesSayWhatThePlayerNeeds() {
        Assert.Equal("[Quest] Same quest as you: Unicorn Way.",
            QuestBuddies.Text(new QuestRelation(QuestRelationKind.SameQuest, "q", 0), "Unicorn Way", null));
        Assert.Equal("[Quest] Same quest chain, 2 quests ahead of you: Away to Unicorn Way! (in Olde Town).",
            QuestBuddies.Text(new QuestRelation(QuestRelationKind.SameChain, "q", 2), "Away to Unicorn Way!", "Olde Town"));
        Assert.Equal("[Quest] Same quest chain, 1 quest behind you: Rattlebones.",
            QuestBuddies.Text(new QuestRelation(QuestRelationKind.SameChain, "q", -1), "Rattlebones", null));
        Assert.Equal("[Quest] Different quest: Lost Lieutenant (in Colossus Boulevard).",
            QuestBuddies.Text(new QuestRelation(QuestRelationKind.Different, "q", 0), "Lost Lieutenant", "Colossus Boulevard"));
        Assert.Equal("[Quest] No quest right now.", QuestBuddies.Text(new QuestRelation(QuestRelationKind.Different, null, 0), null, null));
        Assert.Equal("[Quest] Same quest as you: Who Are You?",
            QuestBuddies.Text(new QuestRelation(QuestRelationKind.SameQuest, "q", 0), "Who Are You?", null));
    }

    [Fact]
    public void TheSignatureIgnoresStepCountsButNotDirectionOrQuest() {
        string sig(QuestRelationKind kind, string? quest, int steps) => QuestBuddies.Signature(new QuestRelation(kind, quest, steps));
        Assert.Equal(sig(QuestRelationKind.SameChain, "a", 2), sig(QuestRelationKind.SameChain, "b", 3));
        Assert.NotEqual(sig(QuestRelationKind.SameChain, "a", 2), sig(QuestRelationKind.SameChain, "a", -2));
        Assert.NotEqual(sig(QuestRelationKind.SameQuest, "a", 0), sig(QuestRelationKind.SameQuest, "b", 0));
    }

    [Fact]
    public void TheChainGraphFollowsCompletePrereqsOfTheSameChainOnly() {
        var a = Quest("WC-UNICORN-MAIN-001", mainline: true);
        var b = Quest("WC-UNICORN-MAIN-002", mainline: true, Complete("WC-UNICORN-MAIN-001"));
        var side = Quest("WC-CLASSIC-SIDE-001", mainline: false, Complete("WC-UNICORN-MAIN-001"));
        var sideNext = Quest("WC-CLASSIC-SIDE-002", mainline: false, Complete("WC-CLASSIC-SIDE-001"));
        var notAfter = Quest("WC-UNICORN-MAIN-009", mainline: true, Complete("WC-UNICORN-MAIN-002", applyNot: true));
        var nested = Quest("KT-MAIN-C01-001", mainline: true);
        nested.m_requirements = new RequirementList {
            m_requirements = [new RequirementList { m_requirements = [Complete("WC-UNICORN-MAIN-002")] }],
        };

        var (next, mainline) = QuestChains.Build([a, b, side, sideNext, notAfter, nested]);

        Assert.Equal(["WC-UNICORN-MAIN-002"], next["WC-UNICORN-MAIN-001"]); // the side quest is not the story's next step
        Assert.Equal(["KT-MAIN-C01-001"], next["WC-UNICORN-MAIN-002"]); // a negated prereq is no step
        Assert.Equal(["WC-CLASSIC-SIDE-002"], next["WC-CLASSIC-SIDE-001"]);
        Assert.Contains("WC-UNICORN-MAIN-009", mainline);
        Assert.DoesNotContain("WC-CLASSIC-SIDE-001", mainline);
    }

    [Fact]
    public void NoticesRepeatOnlyWithNewsOrAfterTheirQuietTime() {
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(QuestBuddyNotices.ShouldSendForTests(null, t0, "same:a", QuestBuddyReason.Arrived, t0));
        Assert.False(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "same:a", QuestBuddyReason.Arrived, t0.AddMinutes(30)));
        Assert.True(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "same:a", QuestBuddyReason.Arrived, t0.AddMinutes(61)));
        Assert.True(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "chain:ahead", QuestBuddyReason.Arrived, t0.AddMinutes(1)));
        Assert.False(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "same:a", QuestBuddyReason.QuestChanged, t0.AddDays(1)));
        Assert.True(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "same:b", QuestBuddyReason.QuestChanged, t0.AddSeconds(1)));
        Assert.False(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "same:a", QuestBuddyReason.Login, t0.AddMinutes(1)));
        Assert.True(QuestBuddyNotices.ShouldSendForTests("same:a", t0, "same:a", QuestBuddyReason.Login, t0.AddMinutes(3)));
    }

    [Fact]
    public void FriendsOnlineHearBothWaysAndStrangersAndAmbientNever() {
        using var system = ActorSystem.Create("quest-buddies", "akka.actor.provider=local");
        var sent = new List<(ulong Viewer, ulong About, string Text)>();
        QuestBuddyNotices.ClearForTests();
        QuestChains.Use([Quest("WC-MAIN-C01-001", true), Quest("WC-MAIN-C01-002", true, Complete("WC-MAIN-C01-001"))]);
        QuestBuddyNotices.SinkForTests = (viewer, about, text) => { lock (sent) sent.Add((viewer, about, text)); };
        QuestBuddyNotices.TitlesForTests = quest => quest == "WC-MAIN-C01-002" ? "Second Quest" : null;
        const ulong alice = 9_880_001, bob = 9_880_002, stranger = 9_880_003;
        var refs = new List<IActorRef>();
        try {
            var a = Online(system, refs, alice, "WC-MAIN-C01-002", "WizardCity/WC_Hub");
            var b = Online(system, refs, bob, "WC-MAIN-C01-001", "WizardCity/WC_Hub");
            Online(system, refs, stranger, "WC-MAIN-C01-002", "WizardCity/WC_Hub");
            Befriend(a, b);

            QuestBuddyNotices.Login(system, alice);

            Assert.Equal(2, sent.Count);
            Assert.Contains(sent, s => s.Viewer == bob && s.About == alice && s.Text == "[Quest] Same quest chain, 1 quest ahead of you: Second Quest.");
            Assert.Contains(sent, s => s.Viewer == alice && s.About == bob && s.Text.StartsWith("[Quest] Same quest chain, 1 quest behind you"));
            Assert.DoesNotContain(sent, s => s.Viewer == stranger || s.About == stranger);

            // Arriving nearby right after: no news, so nothing is repeated.
            sent.Clear();
            QuestBuddyNotices.Arrived(system, bob);
            Assert.Empty(sent);

            // A blocked friendship is no friendship.
            a.FriendsBehavior.Relationships.Single().Blocked = true;
            b.FriendsBehavior.Relationships.Single().Blocked = true;
            QuestBuddyNotices.ClearForTests();
            QuestBuddyNotices.Login(system, alice);
            Assert.Empty(sent);
        }
        finally {
            QuestBuddyNotices.SinkForTests = null;
            QuestBuddyNotices.TitlesForTests = null;
            QuestBuddyNotices.ClearForTests();
            QuestChains.ClearForTests();
            foreach (var r in refs) ActiveWizardDirectory.Remove(r);
            foreach (var id in new[] { alice, bob, stranger }) OnlinePlayerCollection.RemoveVirtualOnlinePlayer(id);
        }
    }

    [Fact]
    public void OnlyTalkAndOneUseGoalsAreSharedAndOnlyInsideTheSameInstance() {
        Assert.True(InstanceGoalSharing.SharesGoal(isTalk: true, isUse: false, tally: 1));
        Assert.True(InstanceGoalSharing.SharesGoal(isTalk: false, isUse: true, tally: 1));
        Assert.False(InstanceGoalSharing.SharesGoal(isTalk: false, isUse: true, tally: 5));
        Assert.False(InstanceGoalSharing.SharesGoal(isTalk: false, isUse: false, tally: 1));

        const string tower = "WizardCity/WC_Streets/Interiors/WC_Unicorn_T1";
        Assert.True(InstanceGoalSharing.SamePlace(tower, 77, 0, tower, 77, 0));
        Assert.False(InstanceGoalSharing.SamePlace(tower, 77, 0, tower, 78, 0)); // another copy
        Assert.False(InstanceGoalSharing.SamePlace(tower, 77, 0, "WizardCity/WC_Hub", 77, 0)); // another zone of it
        Assert.False(InstanceGoalSharing.SamePlace("WizardCity/WC_Hub", 0, 0, "WizardCity/WC_Hub", 0, 0)); // the open world
        Assert.False(InstanceGoalSharing.SamePlace("Housing/Dorm", 77, 5, "Housing/Dorm", 77, 5)); // a house
    }

    private static Wizard Online(ActorSystem system, List<IActorRef> refs, ulong charId, string quest, string zone) {
        var wizard = new Wizard {
            CharId = charId,
            Zone = zone,
            QuestBehavior = new ServerQuestBehavior(),
            FriendsBehavior = new ServerFriendBehavior(),
        };
        Assert.True(wizard.QuestBehavior.AddQuest(new QuestInstance(Quest(quest, true), charId)));
        var actor = system.ActorOf(Props.Empty);
        refs.Add(actor);
        ActiveWizardDirectory.SetWizard(actor, wizard);
        OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            AccountId = charId, CharacterId = charId, CurrentZone = zone, ActorPath = actor.Path.ToString(),
        });

        return wizard;
    }

    private static void Befriend(Wizard a, Wizard b) {
        var relationship = new Relationship { FirstPlayerId = a.CharId, SecondPlayerId = b.CharId };
        a.FriendsBehavior.Relationships.Add(relationship);
        b.FriendsBehavior.Relationships.Add(new Relationship { FirstPlayerId = a.CharId, SecondPlayerId = b.CharId });
    }

    private static QuestTemplate Quest(string name, bool mainline, params Requirement[] requirements) => new() {
        m_questName = name,
        m_mainline = mainline,
        m_goals = [],
        m_startGoals = [],
        m_requirements = new RequirementList { m_requirements = [.. requirements] },
    };

    private static ReqHasEntry Complete(string quest, bool applyNot = false) => new() {
        m_isQuestRegistry = true, m_questName = quest, m_entryName = "Complete", m_applyNOT = applyNot,
    };

}
