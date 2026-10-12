// CLASSIC: the Marleybone playthrough's server findings (2026-10-10): a goal notice the client never got, and a held
// duel seat whose wizard logged back in before their client was in the zone.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class QuestJournalDiffTests {

    private static JournalQuest Quest(ulong id, string name, params JournalGoal[] goals) => new(id, name, goals);

    [Fact]
    public void AFinishedGoalAndItsSuccessorAreSentInTheNativeOrder() {
        var shown = new[] { Quest(1, "Q", new JournalGoal(10, "Kill", 4), new JournalGoal(11, "Talk", -1)) };
        var saved = new[] { Quest(1, "Q", new JournalGoal(10, "Kill", int.MaxValue), new JournalGoal(11, "Talk", 0)) };

        var updates = QuestJournalDiff.Compare(shown, saved, _ => false);

        Assert.Equal([JournalUpdateKind.GoalCompleted, JournalUpdateKind.GoalStarted], updates.Select(u => u.Kind));
        Assert.Equal(10UL, updates[0].GoalId);
        Assert.Equal(11UL, updates[1].GoalId);
    }

    [Fact]
    public void AFinishedQuestIsCompletedAndADroppedOneOnlyRemoved() {
        var shown = new[] { Quest(1, "Done", new JournalGoal(10, "G", 0)), Quest(2, "Dropped", new JournalGoal(20, "G", 0)) };

        var updates = QuestJournalDiff.Compare(shown, [], name => name == "Done");

        Assert.Equal([(JournalUpdateKind.QuestCompleted, 1UL), (JournalUpdateKind.QuestRemoved, 2UL)],
            updates.Select(u => (u.Kind, u.QuestId)));
    }

    [Fact]
    public void ANewQuestAndAMovedTallyAreSent() {
        var shown = new[] { Quest(1, "Q", new JournalGoal(10, "Kill", 2)) };
        var saved = new[] { Quest(1, "Q", new JournalGoal(10, "Kill", 3)), Quest(2, "New", new JournalGoal(20, "G", 0)) };

        var updates = QuestJournalDiff.Compare(shown, saved, _ => false);

        Assert.Equal([(JournalUpdateKind.QuestAdded, 2UL, 0UL), (JournalUpdateKind.GoalProgressed, 1UL, 10UL)],
            updates.Select(u => (u.Kind, u.QuestId, u.GoalId)).OrderBy(u => u.Kind == JournalUpdateKind.QuestAdded ? 0 : 1));
    }

    [Fact]
    public void ACurrentJournalAndAGoalTheClientNeverSawBeginNeedNothing() {
        var shown = new[] { Quest(1, "Q", new JournalGoal(10, "A", int.MaxValue), new JournalGoal(11, "B", -1)) };
        var saved = new[] { Quest(1, "Q", new JournalGoal(10, "A", int.MaxValue), new JournalGoal(11, "B", int.MaxValue)) };

        Assert.Empty(QuestJournalDiff.Compare(shown, shown, _ => false));
        Assert.Empty(QuestJournalDiff.Compare(shown, saved, _ => false));
    }

}

public sealed class RejoinTimingTests {

    private static readonly DateTime Arrived = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AZonedClientGetsItsDuelAfterTheArenaLoadDelayNotBefore() {
        Assert.False(RejoinTiming.Due(Arrived, zoned: true, Arrived.AddSeconds(0.5)));
        Assert.True(RejoinTiming.Due(Arrived, zoned: true, Arrived + RejoinTiming.ClientLoadDelay));
    }

    [Fact]
    public void AClientThatNeverSaysItZonedGetsItAfterTheWait() {
        Assert.False(RejoinTiming.Due(Arrived, zoned: false, Arrived + RejoinTiming.ClientLoadDelay));
        Assert.False(RejoinTiming.Due(Arrived, zoned: false, Arrived.AddSeconds(9)));
        Assert.True(RejoinTiming.Due(Arrived, zoned: false, Arrived + RejoinTiming.ZonedWait));
    }

    [Fact]
    public void ATimerFiringAHairEarlyStillCounts()
        => Assert.True(RejoinTiming.Due(Arrived, zoned: true, Arrived + RejoinTiming.ClientLoadDelay - TimeSpan.FromMilliseconds(30)));

}

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HeldSeatRejoinTests : IDisposable {

    private readonly ActorSystem _system = ActorSystem.Create("rejoin-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");

    public HeldSeatRejoinTests() {
        ClassicRuntime.ResetForTests();
        ClientZoneSignals.ResetForTests();
    }

    public void Dispose() {
        ClientZoneSignals.ResetForTests();
        _system.Terminate().Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ZonedSignalsCountOnlyForTheSameSessionAndReachTheWaitingDuel() {
        var inbox = Inbox.Create(_system);
        var oldSession = _system.ActorOf(Props.Empty, "old");
        var session = _system.ActorOf(Props.Empty, "new");

        ClientZoneSignals.Zoned(9, oldSession);
        Assert.False(ClientZoneSignals.Await(9, session, inbox.Receiver)); // an older session's report does not count

        ClientZoneSignals.Zoned(9, session);
        var ready = Assert.IsType<CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY>(inbox.Receive(TimeSpan.FromSeconds(3)));
        Assert.Equal(9UL, ready.CharacterId);
        Assert.True(ready.Zoned);

        Assert.True(ClientZoneSignals.Await(9, session, inbox.Receiver)); // already zoned: ready at once
    }

    [Fact]
    public void AForgottenWaitIsNotTold() {
        var inbox = Inbox.Create(_system);
        var session = _system.ActorOf(Props.Empty, "s");
        Assert.False(ClientZoneSignals.Await(5, session, inbox.Receiver));
        ClientZoneSignals.Forget(5, session);
        ClientZoneSignals.Zoned(5, session);
        Assert.Throws<TimeoutException>(() => inbox.Receive(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void AWizardBackInTheZoneKeepsTheHeldSeatUntilTheirClientIsIn() {
        var timers = TimerRecorder.Create();
        var duel = HeldDuel(timers, charId: 9, out var seat);
        var session = _system.ActorOf(Props.Empty, "player");

        Assert.True(Queue(duel, session, Wizard(9)));

        // Nothing went to the client and the seat is still held (it passes; the duel waits if everyone is away).
        Assert.True(seat.Disconnected);
        Assert.Equal(9UL, seat.HeldCharacterId);
        Assert.Contains(("StartSingleTimer", RejoinTiming.ClientLoadDelay), timers.Started);
        Assert.Contains(("StartSingleTimer", RejoinTiming.ZonedWait), timers.Started);

        // The client says it is in the zone at once: still too early (the arena's load delay).
        Ready(duel, 9, zoned: true);
        Assert.True(seat.Disconnected);
        Assert.Single(Pending(duel));
    }

    [Fact]
    public void AnExpiredHoldOrALeavingWizardDropsTheWaitWithoutTouchingTheSeat() {
        var timers = TimerRecorder.Create();
        var duel = HeldDuel(timers, charId: 9, out var seat);
        var session = _system.ActorOf(Props.Empty, "player");
        Assert.True(Queue(duel, session, Wizard(9)));

        duel.OnPlayerLeave(session, 0);
        Assert.Empty(Pending(duel));
        Assert.True(seat.Disconnected); // left again before taking it: the hold goes on
        Assert.Contains(timers.Cancelled, key => key.StartsWith("RejoinWait_", StringComparison.Ordinal));

        Assert.True(Queue(duel, session, Wizard(9)));
        Age(duel, 9, TimeSpan.FromSeconds(30));
        seat.RemoveParticipant(); // the hold ran out meanwhile
        Ready(duel, 9, zoned: false);
        Assert.Empty(Pending(duel));
        Assert.False(seat.Occupied);
    }

    [Fact]
    public void NoHeldSeatMeansNoWait() {
        var timers = TimerRecorder.Create();
        var duel = HeldDuel(timers, charId: 9, out _);
        Assert.False(Queue(duel, _system.ActorOf(Props.Empty, "other"), Wizard(10)));
        Assert.Empty(Pending(duel));
        Assert.Empty(timers.Started);
    }

    private static CombatDuelComponent HeldDuel(TimerRecorder timers, ulong charId, out CombatDuelSubCircle seat) {
        var duel = CombatRegressionTests.MakeDuel();
        duel.Timers = (ITimerScheduler)(object)timers;
        typeof(CombatDuelComponent).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, true);
        seat = duel.SubCircles[4];
        CombatRegressionTests.SetProperty(seat, "ParticipantObject", new CoreObject { m_templateID = 1 });
        CombatRegressionTests.SetProperty(seat, "ParticipantActor", ActorRefs.Nobody);
        var wizard = Wizard(charId);
        CombatRegressionTests.SetProperty(seat, "ParticipantGameStats", wizard.GameStats);
        CombatRegressionTests.SetProperty(seat, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        seat._wizard = wizard;
        seat.AddedToDuel = true;
        seat.HoldSeat(DateTime.UtcNow);
        return duel;
    }

    private static bool Queue(CombatDuelComponent duel, IActorRef session, Wizard wizard)
        => (bool)CombatRegressionTests.Invoke(duel, "QueueRejoin", new CoreObject { m_templateID = 1 }, session, wizard)!;

    private static void Ready(CombatDuelComponent duel, ulong charId, bool zoned)
        => CombatRegressionTests.Invoke(duel, "ReceiveRejoinClientReady",
            new CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY { CharacterId = charId, Zoned = zoned });

    private static System.Collections.IDictionary Pending(CombatDuelComponent duel)
        => (System.Collections.IDictionary)typeof(CombatDuelComponent)
            .GetField("_pendingRejoins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(duel)!;

    private static void Age(CombatDuelComponent duel, ulong charId, TimeSpan by) {
        var pending = Pending(duel)[charId]!;
        var property = pending.GetType().GetProperty("ArrivedUtc")!;
        property.SetValue(pending, (DateTime)property.GetValue(pending)! - by);
    }

    private static Wizard Wizard(ulong charId) {
        var wizard = (Wizard)RuntimeHelpers.GetUninitializedObject(typeof(Wizard));
        typeof(Wizard).GetField("<CharId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wizard, charId);
        var stats = (ServerWizGameStats)RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = 1000;
        stats.m_currentHitpoints = 500;
        wizard.GameStats = stats;
        return wizard;
    }

    /// <summary>Records timer calls; never fires.</summary>
    public class TimerRecorder : DispatchProxy {
        internal readonly List<(string Method, TimeSpan Delay)> Started = [];
        internal readonly List<string> Cancelled = [];

        internal static TimerRecorder Create() => (TimerRecorder)(object)Create<ITimerScheduler, TimerRecorder>();

        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method?.Name) {
                case "StartSingleTimer":
                    Started.Add(("StartSingleTimer", args!.OfType<TimeSpan>().First()));
                    break;
                case "Cancel":
                    Cancelled.Add(args![0]?.ToString() ?? "");
                    break;
            }

            return method?.ReturnType == typeof(bool) ? false : null;
        }
    }

}
