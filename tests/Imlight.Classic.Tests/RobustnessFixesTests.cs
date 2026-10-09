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
 * ROBUSTNESS FIXES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The runtime robustness fixes from the production audit of 2026-10-04
 * (playbot-reports/prod-robustness.md): a duel whose participant left
 * mid-round, the tutorial hand grant, entities that load late, disposed
 * sessions, the Crown Shop rows the client rejects, unhandled-message
 * logging, one-node paths, quests without Underway text, disconnects.
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~RobustnessFixesTests
 *
 * NOTE:
 * Actor-owned state is supplied directly; no world server or database is started.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class RobustnessFixesTests {

    public RobustnessFixesTests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-robustness-tests.log")}\n");
            Imlight.Common.ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    // --- 2. A participant who left mid-round no longer hangs the duel ---------------------------------------------

    [Fact]
    public void AQueuedActionOfAParticipantWhoLeftIsSkippedAndTheRoundResolves() {
        var duel = CombatRegressionTests.MakeDuel();
        var duelData = new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } };
        CombatRegressionTests.SetProperty(duel, "Duel", duelData);
        var enemy = Occupy(duel, 0, player: false);
        var stays = Occupy(duel, 4, player: true);
        var leaves = Occupy(duel, 5, player: true);
        var resolver = new CombatResolver(duelData, duel.SubCircles);
        resolver.Reset();
        resolver.AddCombatMove(CombatMoveType.Pass, enemy, null, null);
        resolver.AddCombatMove(CombatMoveType.Pass, stays, null, null);
        resolver.AddCombatMove(CombatMoveType.Pass, leaves, null, null);

        // The rig-final duel: slot 5's seat was released after its pass was queued (rejoin timeout).
        leaves.RemoveParticipant();
        Assert.Null(leaves._hangingEffects);

        resolver.ApplyQueuedCombatActions(out var actions);

        Assert.Equal(new[] { 0, 4 }, actions.m_actionList.Select(action => action.m_spellCaster).OrderBy(slot => slot).ToArray());
    }

    [Fact]
    public void OverTimeEffectsOfACircleWithNoParticipantDoNothing() {
        var duel = CombatRegressionTests.MakeDuel();
        var duelData = new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } };
        CombatRegressionTests.SetProperty(duel, "Duel", duelData);
        var resolver = new CombatResolver(duelData, duel.SubCircles);
        var empty = duel.SubCircles[3];

        var time = (float) typeof(CombatResolver).GetMethod("InvokeOverTimeEffects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(resolver, [empty])!;

        Assert.Equal(0f, time);
    }

    // --- 3. Tutorial hand grants ----------------------------------------------------------------------------------

    [Fact]
    public void TutorialPlayerGrantsDuringExecutionAccumulateFromNone() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelPhase = kDuelPhase.kPhase_Execution });
        Occupy(duel, 4, player: true);
        var director = new TutorialDuelDirector(duel, "WizardCity/Tutorial/Tutorial_Duel");

        // rig-final 2026-10-04: the first grant of the phase spread a null pending list (NullReferenceException).
        director.ReceiveRebuildDuelHand(ActorRefs.Nobody, new TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND {
            RecipientTemplateId = 1, SpellIdsToGrant = [11],
        });
        director.ReceiveRebuildDuelHand(ActorRefs.Nobody, new TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND {
            RecipientTemplateId = 1, SpellIdsToGrant = [12, 13],
        });

        var pending = (uint[]) typeof(TutorialDuelDirector).GetField("_pendingHandGrant", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(director)!;
        Assert.Equal(new uint[] { 11, 12, 13 }, pending);
    }

    [Fact]
    public void TutorialGrantSkipsEmptySigilSlots() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelPhase = kDuelPhase.kPhase_Execution });
        Occupy(duel, 4, player: true);
        var circles = duel.SubCircles.ToArray();
        circles[2] = null!;
        CombatRegressionTests.SetProperty(duel, "SubCircles", circles);
        var director = new TutorialDuelDirector(duel, "Tutorial");

        director.ReceiveRebuildDuelHand(ActorRefs.Nobody, new TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND {
            RecipientTemplateId = 1, SpellIdsToGrant = [7],
        });
    }

    // --- 4. Entities that answer their load late are kept ---------------------------------------------------------

    [Fact]
    public async Task AnEntityThatMissesTheLoadBudgetIsKeptAndCountedWhenItAnswers() {
        var system = ActorSystem.Create("late-load-" + Guid.NewGuid().ToString("N"));
        try {
            var zone = (Zone) RuntimeHelpers.GetUninitializedObject(typeof(Zone));
            CombatRegressionTests.SetProperty(zone, "ZonePath", "Test/LateLoad");
            var reports = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parent = system.ActorOf(Props.Create(() => new SupervisorParent(zone, reports)));
            var supervisor = await parent.Ask<IActorRef>("child", TimeSpan.FromSeconds(3));
            var boss = system.ActorOf(Props.Create(() => new SlowEntity()));
            Watch(system, boss, out var bossStopped);

            supervisor.Tell(new TestSupervisor.Begin(boss));
            supervisor.Tell(new ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT());

            // The zone goes on without it...
            Assert.True(await reports.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            var late = await supervisor.Ask<TestSupervisor.State>(new TestSupervisor.Query(boss), TimeSpan.FromSeconds(3));
            Assert.Equal((1, true), (late.Late, late.Known));
            Assert.False(bossStopped.Task.IsCompleted);

            // ... and it joins when its answer comes.
            boss.Tell("go");
            TestSupervisor.State joined;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            do {
                joined = await supervisor.Ask<TestSupervisor.State>(new TestSupervisor.Query(boss), TimeSpan.FromSeconds(3));
            } while (joined.Late != 0 && DateTime.UtcNow < deadline);
            Assert.Equal((0, true), (joined.Late, joined.Known));
            Assert.False(bossStopped.Task.IsCompleted);
        }
        finally {
            await system.Terminate();
        }
    }

    private static void Watch(ActorSystem system, IActorRef actor, out TaskCompletionSource<bool> stopped) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.ActorOf(Props.Create(() => new Watcher(actor, tcs)));
        stopped = tcs;
    }

    // --- 5. Disposed sessions --------------------------------------------------------------------------------------

    [Fact]
    public async Task AGoneSessionIsNotAskedForItsWizard() {
        var system = ActorSystem.Create("gone-" + Guid.NewGuid().ToString("N"));
        try {
            var silent = system.ActorOf(Props.Create(() => new SlowEntity()));
            PlayerQuery.MarkGone(silent);

            var clock = Stopwatch.StartNew();
            var answered = PlayerQuery.TryActiveWizard(silent, TimeSpan.FromSeconds(5), out var wizard, out var error);

            Assert.False(answered);
            Assert.Null(wizard);
            Assert.Null(error); // no one to ask: no warning
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
            Assert.True(PlayerQuery.IsGone(silent));
        }
        finally {
            await system.Terminate();
        }
    }

    [Fact]
    public void ADisposedSessionLeavesTheOnlineListAndTheListIsTheMemoryCopy() {
        const ulong account = 0x7E57_0000_0000_0001, character = 0x7E57_0000_0000_0002;
        const string path = "akka://test/user/SessionActor.4242";
        OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            AccountId = account, CharacterId = character, SessionId = 4242, ActorPath = path, CurrentZone = "Test/Zone",
        });
        try {
            Assert.Equal(path, OnlinePlayerCollection.GetOnlinePlayer(character)?.ActorPath);
            Assert.Contains(OnlinePlayerCollection.GetPlayersInZone("Test/Zone"), player => player.CharacterId == character);

            Assert.Equal(1, OnlinePlayerCollection.RemoveSessionByActorPath(path));

            // No database read-back: a stale database row cannot bring the player back.
            Assert.Null(OnlinePlayerCollection.GetOnlinePlayer(character));
            Assert.DoesNotContain(OnlinePlayerCollection.GetOnlinePlayers(), player => player.CharacterId == character);
            Assert.Equal(0, OnlinePlayerCollection.RemoveSessionByActorPath(path));
        }
        finally {
            OnlinePlayerCollection.RemoveVirtualOnlinePlayer(account);
        }
    }

    [Fact]
    public void TeleportToAFriendWhoDoesNotAnswerSaysSo()
        => Assert.Equal("Your friend is not available.", FriendsService.FriendNotAvailableMessage);

    // --- 6. Crown Shop rows the r806919 client can show ------------------------------------------------------------

    [Fact]
    public void TheClientCatalogDropsHenchmanCreatureTemplatesAndPricesGoldOnlyRentalsInCrowns() { // CLASSIC: items are kept (HenchmenTests)
        CrownShopEntry Entry(string name, ulong template, string category, int crowns, int gold, int? days = null)
            => new(name, template, category, crowns, gold, days, 1, category == CrownShopCategories.Henchmen, null);
        var offered = new[] {
            Entry("Chestnut Pony", 191237, CrownShopCategories.PermanentMounts, 5000, 50000),
            Entry("Chestnut Pony (1 Day)", 191222, CrownShopCategories.RentalMounts, 0, 2000, 1),
            Entry("Enchanted Broom (1 Day)", 191154, CrownShopCategories.RentalMounts, 0, 1429, 1),
            Entry("Enchanted Broom (7 Day)", 191226, CrownShopCategories.RentalMounts, 1000, 10000, 7),
            Entry("Renn Swiftweaver", 191196, CrownShopCategories.Henchmen, 100, 0),
        };

        var shown = CrownShopService.ForClient(offered).ToDictionary(item => item.Template);

        Assert.False(shown.ContainsKey(191196));
        Assert.Equal((5000, 50000), (shown[191237].Crowns, shown[191237].Gold));
        Assert.Equal((200, 2000), (shown[191222].Crowns, shown[191222].Gold));
        Assert.Equal((143, 1429), (shown[191154].Crowns, shown[191154].Gold));
        Assert.Equal((1000, 10000), (shown[191226].Crowns, shown[191226].Gold));
        Assert.All(shown.Values, item => Assert.True(item.Crowns > 0));
    }

    // --- 7. Unhandled client messages are warned about once per type ----------------------------------------------

    [Fact]
    public void AnUnhandledMessageTypeIsReportedOnce() {
        var type = typeof(RobustnessFixesTests); // any type nothing else reports
        Assert.True(UnhandledMessageLog.FirstTime(type));
        Assert.False(UnhandledMessageLog.FirstTime(type));
        Assert.False(UnhandledMessageLog.FirstTime(null!));
    }

    // --- Smaller ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PathType.PT_CHAIN, 0, 1, 1, 0, 0)]    // one-node chain: stepped to -1 before
    [InlineData(PathType.PT_CHAIN, 0, 1, -1, 0, 0)]
    [InlineData(PathType.PT_CHAIN, -1, 3, -1, 0, 0)]  // off the path (approach node): start at the first node
    [InlineData(PathType.PT_CHAIN, 2, 3, 1, 0, 1)]    // end of a chain: back
    [InlineData(PathType.PT_CHAIN, 0, 3, -1, 0, 1)]   // start of a chain going back: forward
    [InlineData(PathType.PT_CHAIN, 1, 3, 1, 0, 2)]
    [InlineData(PathType.PT_LOOP, 2, 3, 0, 0, 0)]
    [InlineData(PathType.PT_LOOP, 0, 3, 0, 1, 2)]
    [InlineData(PathType.PT_LOOP, -1, 3, 0, 1, 0)]
    [InlineData(PathType.PT_LOOP, 0, 0, 0, 0, 0)]
    public void PathNodeIndexesStayOnThePath(PathType type, int current, int count, int chainDirection, int pathDirection, int expected) {
        var direction = chainDirection;
        var next = PathMovementComponent.NextNodeIndex(type, current, count, ref direction, pathDirection);

        Assert.Equal(expected, next);
        Assert.InRange(next, 0, Math.Max(0, count - 1));
    }

    [Fact]
    public void AQuestWithNoUnderwayTextShowsItsOfferText() {
        var prep = new ActorDialog { m_dialogTag = "Prep" };
        var list = new ActorDialogList { m_dialogs = [prep] };

        Assert.Same(prep, InteractQuestUnderwayComponent.UnderwayDialog(list, out var fellBack));
        Assert.True(fellBack);

        var underway = new ActorDialog { m_dialogTag = "Underway" };
        list.m_dialogs.Add(underway);
        Assert.Same(underway, InteractQuestUnderwayComponent.UnderwayDialog(list, out fellBack));
        Assert.False(fellBack);
        Assert.Null(InteractQuestUnderwayComponent.UnderwayDialog(null!, out _));
    }

    [Fact]
    public void ResReInteractAndTheTwoLaterResultsHaveHandlers() {
        foreach (var result in new Result[] { new ResReInteract(), new ResDownloadPackage(), new ResMarkZoneNoWarn() }) {
            var handler = typeof(Imlight.CoreLib.Game.Results.ResultDispatcher)
                .GetMethod("FindHandlerForResult", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [result.GetType(), new OneResultContext(result)]);
            Assert.NotNull(handler);
        }
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.Shutdown, true)]
    [InlineData(SocketError.ConnectionAborted, true)]
    [InlineData(SocketError.AccessDenied, false)]
    public void ADisconnectIsNotAnError(SocketError code, bool normal) {
        Assert.Equal(normal, SocketListener.IsNormalDisconnect(new SocketException((int) code)));
        Assert.Equal(normal, SocketListener.IsNormalDisconnect(new AggregateException(new SocketException((int) code))));
    }

    [Fact]
    public void CancelledAndDisposedSocketOperationsAreDisconnects() {
        Assert.True(SocketListener.IsNormalDisconnect(new OperationCanceledException()));
        Assert.True(SocketListener.IsNormalDisconnect(new ObjectDisposedException("socket")));
        Assert.False(SocketListener.IsNormalDisconnect(new InvalidOperationException()));
    }

    // --- helpers -----------------------------------------------------------------------------------------------------

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool player) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = 1000;
        stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        circle.AddedToDuel = true;
        return circle;
    }

    private sealed class OneResultContext(Result result) : Imlight.CoreLib.Game.Results.IResultContext {
        public System.Collections.Generic.IEnumerable<Result> GetResults() => [result];
        public IActorRef GetZoneActor() => ActorRefs.Nobody;
        public IActorRef GetPlayerRef() => ActorRefs.Nobody;
        public CoreObject GetPlayerObj() => null!;
        public IActorRef GetReplyTo() => ActorRefs.Nobody;
    }

    private sealed class SlowEntity : ReceiveActor {
        private IActorRef? _loader;

        public SlowEntity() {
            Receive<ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN>(_ => _loader = Sender);
            Receive<string>(text => text == "go", _ => _loader?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS(), Self));
        }
    }

    private sealed class Watcher : ReceiveActor {
        public Watcher(IActorRef target, TaskCompletionSource<bool> stopped) {
            Context.Watch(target);
            Receive<Terminated>(_ => stopped.TrySetResult(true));
        }
    }

    private sealed class SupervisorParent : ReceiveActor {
        public SupervisorParent(Zone zone, TaskCompletionSource<bool> reports) {
            var child = Context.ActorOf(Props.Create(() => new TestSupervisor(zone)));
            Receive<string>(text => text == "child", _ => Sender.Tell(child));
            Receive<ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS>(_ => reports.TrySetResult(true));
            ReceiveAny(_ => { });
        }
    }

    internal sealed class TestSupervisor(Zone zone) : ZoneEntitySupervisor(zone) {
        internal sealed record Begin(IActorRef Entity);
        internal sealed record Query(IActorRef Entity);
        internal sealed record State(int Late, bool Known);

        public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) { }

        [MessageHandler(typeof(Begin))]
        private void ReceiveBegin(Begin message) {
            BeginEntityLoad(message.Entity, "GH-Boar-4-GHBoss-R4");
            ReportLoadedWhenEntitiesLoad();
        }

        [MessageHandler(typeof(Query))]
        private void ReceiveQuery(Query message) => Sender.Tell(new State(LateEntityCount, EntityActors.Contains(message.Entity)));
    }

}
