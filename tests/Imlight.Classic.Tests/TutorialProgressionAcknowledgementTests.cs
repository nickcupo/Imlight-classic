// CLASSIC: exercise the actual sealed tutorial actor's native handlers against authored quests and ACK faults.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class TutorialProgressionAcknowledgementTests {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Intro = "Tutorial_Intro", Goal = "OnlyGoal", ResultEvent = "QA-Tutorial-Goal-Result", LaterEvent = "QA-Tutorial-Later-Field";

    [Fact]
    public async Task NativeControlGoalCanCompleteUnbegunAndItsRepeatNeverReplaysGoalResults() {
        using var scope = new Scope(); var f = scope.F; var held = f.Expected.GoalProgress[0];
        using var actor = await TutorialFixture.Create(f);
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actor.Events); Assert.Empty(actor.Packets); Assert.Equal(-1, held.CurrentProgress); };
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: Intro, goal: Goal, stage: 3));
        Assert.Equal(3, reply.Stage); await actor.Event(ResultEvent);
        Assert.Equal(1, f.Saves); Assert.Equal(int.MaxValue, held.CurrentProgress); Assert.Same(held, f.Expected.GoalProgress[0]);
        Assert.Equal(1, actor.Events.Count(name => name == ResultEvent));
        f.OnSave = null;
        await actor.Call("ReceiveServerTutorialCommand", Command(goal: Goal)); await actor.Drain();
        Assert.Equal(1, f.Saves); Assert.Equal(1, actor.Events.Count(name => name == ResultEvent)); Assert.False(actor.Session.IsDisposed);
    }

    [Theory]
    [InlineData("before")] [InlineData("lost")] [InlineData("publication")]
    public async Task NativeGoalUnknownOutcomeClosesWithoutRunningLaterCommandFieldsOrResults(string failure) {
        using var scope = new Scope(); var f = scope.F; Fault(scope, failure);
        using var actor = await TutorialFixture.Create(f);
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); };
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: Intro, goal: Goal, stage: 8));
        await actor.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, reply.Stage); Assert.Empty(actor.Events); Assert.Empty(await actor.Drain());
        Assert.Equal(1, f.Saves); Assert.Equal(-1, f.Expected.GoalProgress[0].CurrentProgress);
        Assert.Equal(failure == "before" ? -1 : int.MaxValue, f.Quests[0].GoalProgress[0].CurrentProgress);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public async Task KnownMissingQuestRefusesTheRestOfTheNativeCommandWithoutClosing() {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: "QA-No-Such-Tutorial-Quest", goal: Goal, stage: 8));
        Assert.Equal(0, reply.Stage); Assert.Equal(0, f.Saves); Assert.Empty(actor.Events); Assert.Empty(await actor.Drain());
        Assert.Equal(-1, f.Expected.GoalProgress[0].CurrentProgress); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.False(actor.Session.IsDisposed);
    }

    [Fact]
    public async Task AlreadyRetiredIntroReturnsSuccessWithoutReAddingOrReplayingItsResults() {
        using var scope = new Scope(); var f = scope.F; RetireIntro(f);
        using var actor = await TutorialFixture.Create(f);
        Assert.True((await actor.Call("CompleteTutorialIntro", f.Live, f.Live.GameObject)).Value);
        Assert.True((await actor.Call("CompleteTutorialIntro", f.Live, f.Live.GameObject)).Value);
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves); Assert.Empty(f.Quests); Assert.Empty(f.Live.QuestBehavior.CurrentQuestInstances);
        Assert.Empty(actor.Events); Assert.Empty(await actor.Drain()); Assert.False(actor.Session.IsDisposed);
    }

    [Fact]
    public async Task OwnedIntroCompletesItsExactHeldGoalOnceAndARepeatedFinaleKeepsItsAlias() {
        using var scope = new Scope(); var f = scope.F; var held = f.Expected.GoalProgress[0];
        using var actor = await TutorialFixture.Create(f);
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(-1, held.CurrentProgress); Assert.Empty(actor.Events); };
        Assert.True((await actor.Call("CompleteTutorialIntro", f.Live, f.Live.GameObject)).Value); await actor.Event(ResultEvent);
        Assert.Equal(1, f.Saves); Assert.Equal(int.MaxValue, held.CurrentProgress); Assert.Same(held, f.Expected.GoalProgress[0]);
        f.OnSave = null; Assert.True((await actor.Call("CompleteTutorialIntro", f.Live, f.Live.GameObject)).Value); await actor.Drain();
        Assert.Equal(1, f.Saves); Assert.Equal(1, actor.Events.Count(name => name == ResultEvent));
    }

    [Fact]
    public async Task MissingIntroIsAddedBeforeItsGoalCompletionAndResultsFollowBothAcks() {
        using var scope = new Scope(); var f = scope.F; EmptyJournal(f);
        using var actor = await TutorialFixture.Create(f);
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actor.Events);
            if (f.Saves == 1) Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs);
            else Assert.Equal(0, Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances).GoalProgress[0].CurrentProgress);
        };
        Assert.True((await actor.Call("CompleteTutorialIntro", f.Live, f.Live.GameObject)).Value); await actor.Event(ResultEvent);
        Assert.Equal(2, f.Saves); Assert.Equal(int.MaxValue, Assert.Single(f.Quests).GoalProgress[0].CurrentProgress);
        f.OnSave = null; Assert.True((await actor.Call("CompleteTutorialIntro", f.Live, f.Live.GameObject)).Value); await actor.Drain();
        Assert.Equal(2, f.Saves); Assert.Equal(1, actor.Events.Count(name => name == ResultEvent));
    }

    [Theory]
    [InlineData("before")] [InlineData("lost")] [InlineData("publication")]
    public async Task NativeSkipStopsAfterUnknownIntroAddWithoutLaterEventActionEquipmentOrTeleport(string failure) {
        using var scope = new Scope(); var f = scope.F; EmptyJournal(f); Fault(scope, failure);
        using var actor = await TutorialFixture.Create(f);
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(goal: "SkipTutorialGoal", stage: 8));
        await actor.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(99, reply.Stage); // The existing skip stage is set before attempting its saved finale.
        Assert.Empty(actor.Events); Assert.Empty(await actor.Drain()); Assert.Equal(1, f.Saves);
        Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Equal(failure == "before" ? 0 : 1, f.Quests.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public async Task NativeRemovalSendsTheExactOriginalIdOnlyAfterAckAndThenProcessesLaterFields() {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actor.Packets); Assert.Single(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Empty(actor.Events); };
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(remove: Intro, stage: 6));
        Assert.Equal(6, reply.Stage);
        var removed = Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST>(Assert.Single(await actor.Drain()));
        Assert.Equal(TerminalClaimFixture.QuestId, removed.QuestID); Assert.Equal(1, f.Saves); Assert.Empty(f.Quests);
        f.OnSave = null; await actor.Call("ReceiveServerTutorialCommand", Command(remove: Intro));
        Assert.Empty(await actor.Drain()); Assert.Equal(1, f.Saves); Assert.False(actor.Session.IsDisposed);
    }

    [Fact]
    public async Task OutsideAttachKeepsRetiredIntroPendingWhenAuthoredFinalizationHasNoSchoolSpell() {
        using var scope = new Scope(); var f = scope.F; RetireIntro(f); f.Live.Zone = "QA/Outside-Tutorial";
        f.Live.QuestBehavior.Registry[ClassicStart.StarterKitGivenEntry] = 1;
        f.Live.PlayerNameBehavior = new() { NameOverride = "Tutorial fixture" };
        using var actor = await TutorialFixture.Create(f);
        // The authored intro has no school-spell result. A retried deck finalization must stay pending,
        // rather than treating intro retirement as a completed starter kit or granting enrollment.
        await actor.Call("ReceiveAttachComplete", new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE());
        Assert.False(f.Live.HasRegistryValue(ClassicStart.CompletedEntry)); Assert.False(f.Live.HasRegistryValue(ClassicStart.EnrollmentEntry));
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves); Assert.Empty(f.Quests); Assert.Empty(actor.Events);
        Assert.Empty(await actor.Drain()); Assert.False(actor.Session.IsDisposed);
    }

    [Theory]
    [InlineData("event")] [InlineData("remove")] [InlineData("action")] [InlineData("stage")]
    [InlineData("unknown-goal")] [InlineData("wrong-pair")] [InlineData("case")]
    public async Task InvalidPackedFieldRefusesBeforeTheEarlierOwnedGoalCanSave(string field) {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        var command = Command(add: Intro, goal: Goal, stage: 3);
        switch (field) {
            case "event": command.EventToPost = LaterEvent; break;
            case "remove": command.QuestToRemove = "QA-Ordinary-Quest"; break;
            case "action": command.Action = "QA-Action"; break;
            case "stage": command.Value = 7; break;
            case "unknown-goal": command.GoalToComplete = "QA-Ordinary-Goal"; break;
            case "wrong-pair": command.GoalToComplete = "SkipTutorialGoal"; break;
            case "case": command.QuestToAdd = "tutorial_intro"; break;
        }
        var reply = await actor.Call("ReceiveServerTutorialCommand", command);
        Assert.Equal(0, reply.Stage); Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves);
        Assert.Equal(-1, f.Expected.GoalProgress[0].CurrentProgress);
        Assert.Empty(actor.Events); Assert.Empty(await actor.Drain()); Assert.False(actor.Session.IsDisposed);
    }

    [Theory]
    [InlineData("WizardCity/QA/Tutorial_Interior")] [InlineData("WizardCity/Tutorial_Exterior/Other")]
    [InlineData("QA/Tutorial_Interior")]
    public async Task TutorialSubstringOutsideTheTwoNativeZonesCannotAdmitCommands(string zone) {
        using var scope = new Scope(); var f = scope.F; f.Live.Zone = zone;
        using var actor = await TutorialFixture.Create(f);
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: Intro, goal: Goal, stage: 3));
        Assert.Equal(0, reply.Stage); Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves);
        Assert.Empty(actor.Events); Assert.Empty(await actor.Drain()); Assert.False(actor.Session.IsDisposed);
    }

    [Theory]
    [InlineData("missing-template")] [InlineData("missing-goal")] [InlineData("ambiguous-template")]
    public async Task NativeGoalResultsMustResolveUniquelyBeforeCompletionCanSave(string defect) {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        if (defect == "missing-template") actor.SetTemplates();
        if (defect == "missing-goal") { f.Template.m_goals = []; f.Template.m_startGoals = []; }
        if (defect == "ambiguous-template") actor.SetTemplates(f.Template, new QuestTemplate {
            m_questName = Intro, m_goals = [f.Goal], m_startGoals = [Goal],
        });
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: Intro, goal: Goal, stage: 3));
        Assert.Equal(0, reply.Stage); Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves);
        Assert.Equal(-1, f.Expected.GoalProgress[0].CurrentProgress);
        Assert.Empty(actor.Events); Assert.Empty(await actor.Drain()); Assert.False(actor.Session.IsDisposed);
    }

    [Fact]
    public async Task NativePackedIntroAddPublishesItsInstanceBeforeCompletingTheSelectedGoal() {
        using var scope = new Scope(); var f = scope.F; EmptyJournal(f);
        using var actor = await TutorialFixture.Create(f);
        GoalInstance? publishedGoal = null;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actor.Events);
            if (f.Saves == 1) Assert.Empty(f.Live.QuestBehavior.CurrentQuestInstances);
            else { publishedGoal = Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances).GoalProgress[0]; Assert.Equal(0, publishedGoal.CurrentProgress); }
        };
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: Intro, goal: Goal, stage: 8));
        await actor.Event(ResultEvent);
        Assert.Equal(8, reply.Stage); Assert.Equal(2, f.Saves);
        Assert.Equal(int.MaxValue, Assert.Single(f.Quests).GoalProgress[0].CurrentProgress);
        Assert.Same(publishedGoal, Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances).GoalProgress[0]);
        Assert.False(actor.Session.IsDisposed);
    }

    [Theory]
    [InlineData("WC-TUT-C09-014")] [InlineData("WC-TUT-C09-016")]
    public async Task InvalidLaterEventPreventsEarlierNativeRefillAddAndResourceChanges(string quest) {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        actor.SetTemplates(f.Template, new QuestTemplate { m_questName = quest, m_goals = [], m_startGoals = [] });
        var health = f.Live.GameStats.m_currentHitpoints; var mana = f.Live.GameStats.m_currentMana;
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: quest, eventName: LaterEvent, stage: 6));
        Assert.Equal(0, reply.Stage); Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves);
        Assert.Equal(health, f.Live.GameStats.m_currentHitpoints); Assert.Equal(mana, f.Live.GameStats.m_currentMana);
        Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances); Assert.Empty(await actor.Drain());
    }

    [Theory]
    [InlineData("WC-TUT-C09-014")] [InlineData("WC-TUT-C09-016")]
    public async Task NativeRefillAddAndRemoveKeepTheirExistingResourceAndJournalBehavior(string quest) {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        actor.SetTemplates(f.Template, new QuestTemplate { m_questName = quest, m_goals = [], m_startGoals = [] });
        await actor.Call("ReceiveServerTutorialCommand", Command(add: quest));
        Assert.Equal(2, f.Saves); Assert.Equal(2, f.Live.QuestBehavior.CurrentQuestInstances.Count); // Quest ACK + existing resource save.
        var packets = await actor.Drain();
        if (quest == "WC-TUT-C09-014") {
            Assert.Equal(f.Live.GameStats.m_baseHitpoints, f.Live.GameStats.m_currentHitpoints);
            Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH>());
        } else {
            Assert.Equal(f.Live.GameStats.m_baseMana, f.Live.GameStats.m_currentMana);
            Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>());
        }
        await actor.Call("ReceiveServerTutorialCommand", Command(remove: quest));
        Assert.Equal(3, f.Saves); Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances);
        Assert.Single((await actor.Drain()).OfType<QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST>());
        Assert.False(actor.Session.IsDisposed);
    }

    [Fact]
    public async Task ProvenNativeCinematicPairPreservesItsServerEventAndUnknownBeatCannotPostOne() {
        using var scope = new Scope("WC-TUT-C05-001", "Walk Ambrose"); var f = scope.F;
        using var actor = await TutorialFixture.Create(f);
        var reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: "WC-TUT-C05-001", goal: "Trigger Wand Effect", stage: 6));
        await actor.Event("WandFX"); Assert.Equal(6, reply.Stage); Assert.Equal(0, f.Saves);
        reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: "WC-TUT-C05-001", goal: "StopRain", stage: 3));
        Assert.Equal(3, reply.Stage);
        reply = await actor.Call("ReceiveServerTutorialCommand", Command(add: "WC-TUT-C05-001", goal: "ConfigurePlayer", stage: 8));
        Assert.Equal(3, reply.Stage);
        await actor.Drain(); Assert.Single(actor.Events); Assert.Equal(0, f.Saves); Assert.False(actor.Session.IsDisposed);
    }

    private static GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND Command(string add = "", string goal = "", string remove = "", string eventName = "", int? stage = null)
        => new() { QuestToAdd = add, GoalToComplete = goal, QuestToRemove = remove, EventToPost = eventName, Action = stage is null ? "" : "Stage", Value = stage ?? 0 };
    private static void EmptyJournal(TerminalClaimFixture f) { f.Quests.Clear(); f.Saved.QuestBehavior.CurrentQuestIDs = []; f.Live.QuestBehavior.CurrentQuestIDs = []; f.Live.QuestBehavior.CurrentQuestInstances = []; }
    private static void RetireIntro(TerminalClaimFixture f) { EmptyJournal(f); f.Saved.QuestBehavior.Registry[Intro + "_Complete"] = 1; f.Live.QuestBehavior.Registry[Intro + "_Complete"] = 1; }
    private static void Fault(Scope scope, string failure) {
        scope.F.FailSave = failure != "publication"; scope.F.Durable = failure == "lost";
        if (failure == "publication") scope.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored tutorial publication failure");
    }
    private sealed class Scope : IDisposable {
        internal readonly TerminalClaimFixture F = new();
        internal readonly QuestMutationDependencies Dependencies = new();
        private readonly QuestMutationDependencies? _old = WizardQuestTransactions.TestScope.Value;
        private static readonly FieldInfo LevelConfig = typeof(MagicLevelsConfig).GetField("s_playerLevelConfig", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _oldLevelConfig = LevelConfig.GetValue(null);
        internal Scope(string quest = Intro, string goal = Goal) {
            LevelConfig.SetValue(null, new Dictionary<string, List<MagicLevelInfo>> {
                [F.Live.GameStats.MagicSchool.ToString()] = Enumerable.Range(0, 5).Select(TerminalClaimFixture.Table).ToList(),
            });
            WizardQuestTransactions.TestScope.Value = Dependencies;
            F.Template.m_questName = quest; F.Goal.m_goalName = goal; F.Template.m_startGoals = [goal];
            F.Goal.m_completeResults = new() { m_results = [new ResPostEvent { m_eventName = ResultEvent }] };
            F.Quests[0].QuestName = quest; F.Expected.QuestName = quest;
            F.Quests[0].GoalProgress[0].GoalName = goal; F.Expected.GoalProgress[0].GoalName = goal;
            typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(F.Quests[0].GoalProgress[0], -1);
            typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(F.Expected.GoalProgress[0], -1);
            F.Live.Zone = "WizardCity/Tutorial_Interior";
        }
        public void Dispose() { LevelConfig.SetValue(null, _oldLevelConfig); WizardQuestTransactions.TestScope.Value = _old; F.Dispose(); }
    }

    private sealed record Ready;
    private sealed record CallStep(string Method, object[] Arguments);
    private sealed record StepReply(bool Value, int Stage);
    private sealed class TutorialFixture : IDisposable {
        internal readonly ActorSystem System;
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        internal readonly ConcurrentQueue<string> Events = new();
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _events = new();
        internal SessionActor Session = null!;
        private IActorRef _session = null!, _tutorial = null!, _socket = null!, _zone = null!;
        private readonly object? _questList, _questMap;
        private static readonly FieldInfo QuestList = typeof(SpiralDB).GetField("s_questTemplates", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo QuestMap = typeof(SpiralDB).GetField("s_questTemplatesByName", BindingFlags.Static | BindingFlags.NonPublic)!;
        private TutorialFixture(TerminalClaimFixture f) {
            _questList = QuestList.GetValue(null); _questMap = QuestMap.GetValue(null);
            SetTemplates(f.Template);
            System = ActorSystem.Create("tutorial-progress-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        }
        internal void SetTemplates(params QuestTemplate[] templates) {
            QuestList.SetValue(null, templates.ToList());
            QuestMap.SetValue(null, new ConcurrentDictionary<string, QuestTemplate>(templates
                .GroupBy(template => template.m_questName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new KeyValuePair<string, QuestTemplate>(group.Key, group.First())), StringComparer.OrdinalIgnoreCase));
        }
        internal static async Task<TutorialFixture> Create(TerminalClaimFixture store) {
            var f = new TutorialFixture(store);
            try {
                f._socket = f.System.ActorOf(Props.Create(() => new SocketProbe(f.Packets)), "socket");
                f._zone = f.System.ActorOf(Props.Create(() => new ZoneSink(f)), "zone");
                f._session = f.System.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "session");
                f.Session = await f._session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                var zoneService = f.System.ActorOf(Props.Create(() => new ZoneProbe(f.Session, store.Live, f._zone)), "zone-service");
                var actualZone = await zoneService.Ask<ZoneService>(new Ready(), Timeout, TestContext.Current.CancellationToken);
                f.Session.RegisterService(zoneService, actualZone);
                var dispatch = (Dictionary<Type, List<IActorRef>>)typeof(SessionActor).GetField("_dispatchTable", Private)!.GetValue(f.Session)!;
                dispatch[typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST)] = [zoneService];
                ActiveWizardDirectory.SetWizard(f._session, store.Live);
                f._tutorial = f.System.ActorOf(Props.CreateBy(new TutorialProducer(f.Session, store, new CapturedScopes(store))), "tutorial");
                Assert.True(await f._tutorial.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                var watcher = f.System.ActorOf(Props.Create(() => new CloseWatcher(f._session, f.Closed)), "close-watch");
                Assert.True(await watcher.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal Task<StepReply> Call(string method, params object[] arguments)
            => _tutorial.Ask<StepReply>(new CallStep(method, arguments), Timeout, TestContext.Current.CancellationToken);
        internal Task Event(string name) => _events.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        private void Record(string name) { Events.Enqueue(name); _events.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(); }
        internal async Task<IMessage[]> Drain() {
            if (!Session.IsDisposed) await _session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await _socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var result = new List<IMessage>(); while (Packets.TryDequeue(out var packet)) result.Add(packet); return result.ToArray();
        }
        public void Dispose() {
            if (_session is not null) ActiveWizardDirectory.Remove(_session);
            System.Terminate().GetAwaiter().GetResult(); System.Dispose(); QuestList.SetValue(null, _questList); QuestMap.SetValue(null, _questMap);
        }
        private sealed class ZoneSink : ReceiveActor {
            public ZoneSink(TutorialFixture fixture) {
                Receive<ZONE_102_PROTOCOL.MSG_POSTEVENT>(message => fixture.Record((string)message.EventName));
                Receive<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(message => {
                    if (message.Message is ZONE_102_PROTOCOL.MSG_POSTEVENT post) fixture.Record((string)post.EventName);
                    foreach (var value in message.Messages ?? []) if (value is ZONE_102_PROTOCOL.MSG_POSTEVENT nested) fixture.Record((string)nested.EventName);
                }); ReceiveAny(_ => { });
            }
        }
    }
    private sealed class SocketProbe : ReceiveActor {
        public SocketProbe(ConcurrentQueue<IMessage> packets) { Receive<IMessage>(packets.Enqueue); }
    }
    private sealed class CloseWatcher : ReceiveActor {
        public CloseWatcher(IActorRef session, TaskCompletionSource closed) {
            Context.Watch(session); Receive<Ready>(_ => Sender.Tell(true)); Receive<Terminated>(_ => closed.TrySetResult());
        }
    }
    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
    private sealed class TutorialProducer(SessionActor session, TerminalClaimFixture store, CapturedScopes scopes) : IIndirectActorProducer {
        public Type ActorType => typeof(TutorialService);
        public ActorBase Produce() {
            var actual = new TutorialService(session); SetCached(actual, store.Live);
            // The production service is sealed. Only this actor's mailbox adapter is replaced; every tested
            // private/native handler runs on the actual TutorialService instance and its real actor Context.
            var driver = new TutorialDriver(actual, scopes);
            typeof(ActorBase).GetMethod("Become", Private, null, [typeof(Receive)], null)!.Invoke(actual, [new Receive(driver.Dispatch)]);
            return actual;
        }
        public void Release(ActorBase actor) { }
    }
    private sealed class TutorialDriver(TutorialService service, CapturedScopes scopes) {
        internal bool Dispatch(object message) {
            var sender = (IActorRef)typeof(ActorBase).GetProperty("Sender", Private | BindingFlags.Public)!.GetValue(service)!;
            if (message is Ready) { sender.Tell(true); return true; }
            if (message is not CallStep call) return true;
            try {
                using var scope = scopes.Enter();
                var value = typeof(TutorialService).GetMethod(call.Method, Private)!.Invoke(service, call.Arguments);
                var info = (TutorialInfo)typeof(TutorialService).GetField("_tutorialInfo", Private)!.GetValue(service)!;
                sender.Tell(new StepReply(value is not bool result || result, info.m_tutorialStage));
            }
            catch (Exception error) { sender.Tell(new Status.Failure(error)); }
            return true;
        }
    }
    private sealed class ZoneProbe : ZoneService {
        public ZoneProbe(SessionActor session, Wizard wizard, IActorRef endpoint) : base(session) { ZoneActor = endpoint; SetCached(this, wizard); }
        protected override void ConfigureReceivers() {
            Receive<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(message => typeof(ZoneService).GetMethod("ReceiveZoneBroadcast", Private)!.Invoke(this, [message]));
            Receive<Ready>(_ => Sender.Tell(this)); base.ConfigureReceivers();
        }
    }
    private static void SetCached(MessageService service, Wizard wizard) {
        typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(service, wizard);
        typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(service, wizard.GameObject);
    }
    private sealed class CapturedScopes(TerminalClaimFixture store) {
        private readonly QuestMutationDependencies? _mutation = WizardQuestTransactions.TestScope.Value;
        internal IDisposable Enter() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldMutation = WizardQuestTransactions.TestScope.Value;
            var oldClaim = ClassicQuestClaims.TestScope.Value; var oldStack = ClassicStackRewards.TestScope.Value;
            var oldProgression = WizardProgressionTransactions.TestScope.Value; var oldItems = WizardInventoryTransactions.TestRowsScope.Value;
            var oldReagents = WizardReagentCollection.TestRowsScope.Value;
            store.Install(); WizardQuestTransactions.TestScope.Value = _mutation;
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = oldStore; WizardQuestTransactions.TestScope.Value = oldMutation;
                ClassicQuestClaims.TestScope.Value = oldClaim; ClassicStackRewards.TestScope.Value = oldStack;
                WizardProgressionTransactions.TestScope.Value = oldProgression; WizardInventoryTransactions.TestRowsScope.Value = oldItems;
                WizardReagentCollection.TestRowsScope.Value = oldReagents;
            });
        }
    }
    private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
}
