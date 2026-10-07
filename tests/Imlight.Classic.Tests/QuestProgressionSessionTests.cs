// CLASSIC: the actual QuestService terminal boundary consumes only newly acknowledged claim receipts.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class QuestProgressionSessionTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ActualAcceptanceSavesOriginalAndJournalOnceBeforeInitialNativeGoalsAndNeverRestartsOnRepeat() {
        using var f = Acceptance();
        using var actors = await SessionFixture.Create(f);
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actors.Packets);
            Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Empty(f.Live.QuestBehavior.CurrentQuestInstances);
            Assert.Single(f.Working!.Quests); Assert.Single(f.Working.Wizard.QuestBehavior.CurrentQuestIDs); };
        var command = new QUEST_MESSAGES_52_PROTOCOL.MSG_ACCEPTQUEST { QuestName = f.Template.m_questName };
        Assert.True(await actors.Step("ReceiveQuestAccept", command));
        var packets = await actors.Drain();
        Assert.Equal([typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST), typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL)], packets.Select(p => p.GetType()));
        Assert.Equal(1, f.Saves); Assert.Single(f.Quests); Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances);
        var nativeQuest = Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST>(packets[0]);
        Assert.Equal(Assert.Single(f.Quests).ID, nativeQuest.QuestID);
        Assert.Equal(0, Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL>(packets[1]).GoalCount);
        Assert.True(await actors.Step("ReceiveQuestAccept", command)); Assert.Empty(await actors.Drain()); Assert.Equal(1, f.Saves);
        Assert.False(actors.Session.IsDisposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualAcceptanceClosesUnknownSaveWithoutSendingAQuestOrGoals(bool durable) {
        using var f = Acceptance(); f.FailSave = true; f.Durable = durable;
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Step("ReceiveQuestAccept", new QUEST_MESSAGES_52_PROTOCOL.MSG_ACCEPTQUEST { QuestName = f.Template.m_questName }));
        await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Empty(await actors.Drain());
        Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Equal(durable ? 1 : 0, f.Quests.Count); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task ActualGoalStartKeepsCapturedGoalAliasAndOnlyRunsActivationForItsNewAck() {
        using var f = Start();
        using var actors = await SessionFixture.Create(f);
        var alias = f.Expected.GoalProgress[0];
        f.OnSave = () => { Assert.Empty(actors.Packets); Assert.Equal(-1, alias.CurrentProgress); };
        Assert.True(await actors.Step("StartGoal", f.Expected, f.Goal));
        var packet = Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL>(Assert.Single(await actors.Drain()));
        Assert.Equal(1, packet.SendType); Assert.Equal(0, packet.GoalCount); Assert.Equal(0, alias.CurrentProgress);
        Assert.Same(alias, f.Expected.GoalProgress[0]); Assert.Equal(1, f.Saves);
        Assert.True(await actors.Step("StartGoal", f.Expected, f.Goal)); Assert.Empty(await actors.Drain()); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualGoalStartEvaluatesItsGateOnTheFreshPreActivationRegistry(bool savedAllows) {
        using var f = Start();
        f.Saved.QuestBehavior.Registry["QA-Allow-Goal"] = savedAllows ? 1UL : 0UL;
        f.Live.QuestBehavior.Registry["QA-Allow-Goal"] = savedAllows ? 0UL : 1UL;
        f.Goal.m_goalRequirements = new() { m_requirements = [new ReqEntryValue {
            m_isQuestRegistry = false, m_entryName = "QA-Allow-Goal", m_numericValue = 1,
            m_operatorType = OPERATOR_TYPE.OPERATOR_EQUALS }] };
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Step("StartGoal", f.Expected, f.Goal));
        var packets = await actors.Drain(); Assert.Equal(savedAllows ? 1 : 0, packets.Length);
        Assert.Equal(savedAllows ? 1 : 0, f.Saves); Assert.Equal(savedAllows ? 0 : -1, f.Expected.GoalProgress[0].CurrentProgress);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.False(actors.Session.IsDisposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualGoalStartStopsAfterUnknownSaveWithoutAFalseProgressPacket(bool durable) {
        using var f = Start(); f.FailSave = true; f.Durable = durable;
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Step("StartGoal", f.Expected, f.Goal));
        await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Empty(await actors.Drain());
        Assert.Equal(-1, f.Expected.GoalProgress[0].CurrentProgress);
        Assert.Equal(durable ? 0 : -1, Assert.Single(f.Quests).GoalProgress[0].CurrentProgress);
    }

    [Fact]
    public async Task ActualUsageTallyEmitsPreparedSavedProgressAndPreservesTheCapturedAlias() {
        using var f = Usage(); using var actors = await SessionFixture.Create(f);
        var alias = f.Expected.GoalProgress[0];
        f.OnSave = () => { Assert.Empty(actors.Packets); Assert.Equal(0, alias.CurrentProgress); };
        Assert.True(await actors.Step("ReceiveCompleteScavengeGoal", new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
            QuestID = f.Expected.ID, GoalID = alias.ID }));
        var packet = Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL>(Assert.Single(await actors.Drain()));
        Assert.Equal(2, packet.SendType); Assert.Equal(1, packet.GoalCount); Assert.Equal(1, alias.CurrentProgress);
        Assert.Same(alias, f.Expected.GoalProgress[0]); Assert.Equal(1, f.Saves); Assert.Empty(f.Receipts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualUsageTallyClosesUnknownSaveAndStopsBeforeCompletionOrFurtherResults(bool durable) {
        using var f = Usage(); f.FailSave = true; f.Durable = durable;
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Step("ReceiveCompleteScavengeGoal", new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
            QuestID = f.Expected.ID, GoalID = f.Expected.GoalProgress[0].ID }));
        await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(await actors.Drain()); Assert.Equal(0, f.Expected.GoalProgress[0].CurrentProgress);
        Assert.Equal(durable ? 1 : 0, Assert.Single(f.Quests).GoalProgress[0].CurrentProgress);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Empty(f.Receipts); Assert.Equal(0, f.Rolls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualCombatCreditStopsItsOuterTraversalAndMonsterEventAfterUnknownSave(bool durable) {
        using var f = new TerminalClaimFixture(); NativeFields(f); f.FailSave = true; f.Durable = durable;
        const ulong mob = 789020;
        var cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.TryGetValue(mob, out var previous);
        cache[mob] = new GameObjectTemplate { m_objectName = "AuthoredOpponent", m_templateID = (uint)mob };
        try {
            var bounty = new BountyGoalTemplate { m_goalName = TerminalClaimFixture.GoalName, m_goalNameID = f.Goal.m_goalNameID,
                m_goalType = GOAL_TYPE.GOAL_TYPE_BOUNTY, m_goalTitle = "QA_Bounty", m_locationName = "QA_Location",
                m_npcAdjectives = ["AuthoredOpponent"], m_tallyCounter = new() { m_count = 3 }, m_completeResults = new() { m_results = [] } };
            f.Template.m_goals = [bounty]; f.Expected.GoalProgress[0].GoalType = bounty.m_goalType;
            f.Quests[0].GoalProgress[0].GoalType = bounty.m_goalType;
            using var actors = await SessionFixture.Create(f);
            Assert.True(await actors.Step("ReceiveCombatVictory", new COMBAT_106_PROTOCOL.MSG_COMBATWIN {
                MobAdjectives = ["AuthoredOpponent"], MobTemplateIds = [mob] }));
            await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Empty(await actors.Drain()); await actors.DrainZone(); Assert.Empty(actors.ZoneEvents);
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Equal(1, f.Saves);
            Assert.Equal(0, f.Expected.GoalProgress[0].CurrentProgress); Assert.Empty(f.Receipts); Assert.Equal(0, f.Rolls);
        }
        finally { if (previous is null) cache.Remove(mob); else cache[mob] = previous; }
    }

    [Fact]
    public async Task ActualNonterminalCompletionSavesAndStartsItsSuccessorWithoutTerminalRetirementOrRepeat() {
        using var f = new TerminalClaimFixture(); NativeFields(f);
        var next = new PersonaGoalTemplate { m_goalName = "Next", m_goalNameID = 789001, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA,
            m_goalTitle = "QA_Next", m_locationName = "QA_Location", m_completeResults = new() { m_results = [] } };
        f.Template.m_goals.Add(next); f.Template.m_goalLogic = [new GoalCompleteLogic { m_goalsAND = [TerminalClaimFixture.GoalName], m_goalsToAdd = ["Next"] }];
        f.Quests[0].GoalProgress = [f.Quests[0].GoalProgress[0], new GoalInstance { ID = 789002,
            OwnerCharId = f.Live.CharId, GoalName = "Next", GoalType = next.m_goalType }];
        f.Expected.GoalProgress = [f.Expected.GoalProgress[0], TerminalClaimFixture.CloneQuest(f.Quests[0]).GoalProgress[1]];
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Complete());
        var packets = await actors.Drain(); Assert.Equal([typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL), typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL)], packets.Select(p => p.GetType()));
        Assert.Equal(2, f.Saves); Assert.Empty(f.Receipts); Assert.Single(f.Quests); Assert.Equal(0, f.Rolls);
        Assert.True(f.Expected.GoalProgress[0].IsGoalCompleted()); Assert.Equal(0, f.Expected.GoalProgress[1].CurrentProgress);
        Assert.True(await actors.Complete()); Assert.Empty(await actors.Drain()); Assert.Equal(2, f.Saves);
    }

    private static TerminalClaimFixture Acceptance() {
        var f = new TerminalClaimFixture(); NativeFields(f); f.Quests.Clear();
        f.Saved.QuestBehavior.CurrentQuestIDs = []; f.Live.QuestBehavior.CurrentQuestIDs = []; f.Live.QuestBehavior.CurrentQuestInstances = [];
        return f;
    }
    private static TerminalClaimFixture Start() {
        var f = new TerminalClaimFixture(); NativeFields(f);
        typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(f.Quests[0].GoalProgress[0], -1);
        typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(f.Expected.GoalProgress[0], -1);
        return f;
    }
    private static TerminalClaimFixture Usage() {
        var f = new TerminalClaimFixture(); NativeFields(f); f.Goal.m_goalType = GOAL_TYPE.GOAL_TYPE_USAGE;
        f.Goal.m_tallyCounter = new() { m_count = 3 };
        f.Quests[0].GoalProgress[0].GoalType = GOAL_TYPE.GOAL_TYPE_USAGE; f.Expected.GoalProgress[0].GoalType = GOAL_TYPE.GOAL_TYPE_USAGE;
        return f;
    }
    private static void NativeFields(TerminalClaimFixture f) {
        f.Template.m_questTitle = "QA_Quest"; f.Template.m_questLevel = 2;
        f.Template.m_startResults = new() { m_results = [] }; f.Template.m_endResults = new() { m_results = [] };
        f.Goal.m_goalTitle = "QA_Goal"; f.Goal.m_locationName = "QA_Location";
        f.Goal.m_completeResults = new() { m_results = [] }; f.Goal.m_activateResults = new() { m_results = [] };
    }
    private sealed class SessionFixture : IDisposable {
        internal readonly TerminalClaimFixture Store;
        internal readonly ActorSystem System;
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        internal readonly ConcurrentQueue<string> ResidualEvents = new();
        internal readonly ConcurrentQueue<string> ZoneEvents = new();
        internal readonly ConcurrentQueue<IMessage> Broadcasts = new();
        internal readonly TaskCompletionSource LevelUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _goal = new(TaskCreationOptions.RunContinuationsAsynchronously), _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SessionActor Session = null!;
        private IActorRef _session = null!, _quest = null!, _socket = null!, _zone = null!;
        private readonly object? _questList, _questMap, _indexBuilt;
        private readonly Dictionary<string, List<QuestTemplate>> _index;
        private readonly List<QuestTemplate>? _previousZoneIndex;
        private static readonly FieldInfo QuestList = typeof(SpiralDB).GetField("s_questTemplates", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo QuestMap = typeof(SpiralDB).GetField("s_questTemplatesByName", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo IndexBuilt = typeof(DungeonQuestIndex).GetField("s_built", BindingFlags.Static | BindingFlags.NonPublic)!;
        private SessionFixture(TerminalClaimFixture store) {
            Store = store; _questList = QuestList.GetValue(null); _questMap = QuestMap.GetValue(null); _indexBuilt = IndexBuilt.GetValue(null);
            _index = (Dictionary<string, List<QuestTemplate>>)typeof(DungeonQuestIndex).GetField("s_questsByZone", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _index.TryGetValue(store.Live.Zone, out _previousZoneIndex); _index.Remove(store.Live.Zone);
            QuestList.SetValue(null, new List<QuestTemplate> { store.Template });
            QuestMap.SetValue(null, new ConcurrentDictionary<string, QuestTemplate>(new[] { new KeyValuePair<string, QuestTemplate>(store.Template.m_questName, store.Template) }, StringComparer.OrdinalIgnoreCase));
            IndexBuilt.SetValue(null, true); // Authored QA/Claim has no indexed successor; no asset loader runs.
            System = ActorSystem.Create("quest-progress-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        }
        internal static async Task<SessionFixture> Create(TerminalClaimFixture store, bool cacheTemplate = true) {
            var f = new SessionFixture(store);
            try {
                f._socket = f.System.ActorOf(Props.Create(() => new SocketProbe(f.Packets)), "socket");
                f._zone = f.System.ActorOf(Props.Create(() => new ZoneSink(f)), "zone");
                f._session = f.System.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "session");
                f.Session = await f._session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                var scope = new CapturedScopes();
                var zoneService = f.System.ActorOf(Props.Create(() => new ZoneProbe(f.Session, store.Live, f._zone)), "zone-service");
                var actualZone = await zoneService.Ask<ZoneService>(new Ready(), Timeout, TestContext.Current.CancellationToken);
                f.Session.RegisterService(zoneService, actualZone);
                var dispatch = (Dictionary<Type, List<IActorRef>>)typeof(SessionActor).GetField("_dispatchTable", Private)!.GetValue(f.Session)!;
                dispatch[typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST)] = [zoneService];
                ActiveWizardDirectory.SetWizard(f._session, store.Live);
                f._quest = f.System.ActorOf(Props.Create(() => new QuestProbe(f.Session, store, scope, cacheTemplate)), "quest");
                Assert.True(await f._quest.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                var watcher = f.System.ActorOf(Props.Create(() => new CloseWatcher(f._session, f.Closed)), "close-watch");
                Assert.True(await watcher.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal Task<bool> Step(string method, params object[] arguments)
            => _quest.Ask<bool>(new InvokeStep(method, arguments), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Complete() => _quest.Ask<bool>(new CompleteStep(), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Select() => _quest.Ask<bool>(new SelectStep(), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Reconcile() => _quest.Ask<bool>(new ReconcileStep(), Timeout, TestContext.Current.CancellationToken);
        internal void Index(QuestTemplate template) => _index[Store.Live.Zone] = [template];
        internal async Task<IMessage[]> Drain() {
            if (!Session.IsDisposed) await _session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await _socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var result = new List<IMessage>(); while (Packets.TryDequeue(out var packet)) result.Add(packet); return result.ToArray();
        }
        internal Task<ActorIdentity> DrainZone() => _zone.Ask<ActorIdentity>(new Identify("zone-drain"), Timeout, TestContext.Current.CancellationToken);
        internal Task WaitForResiduals() => Task.WhenAll(_goal.Task, _end.Task).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        public void Dispose() {
            if (_session is not null) ActiveWizardDirectory.Remove(_session);
            System.Terminate().GetAwaiter().GetResult(); System.Dispose();
            if (_previousZoneIndex is null) _index.Remove(Store.Live.Zone); else _index[Store.Live.Zone] = _previousZoneIndex;
            QuestList.SetValue(null, _questList); QuestMap.SetValue(null, _questMap); IndexBuilt.SetValue(null, _indexBuilt);
        }
        private sealed class ZoneSink : ReceiveActor {
            public ZoneSink(SessionFixture fixture) {
                Receive<ZONE_102_PROTOCOL.MSG_POSTEVENT>(message => {
                    var eventName = (string)message.EventName;
                    fixture.ZoneEvents.Enqueue(eventName);
                    if (!eventName.StartsWith("QA-Residual-", StringComparison.Ordinal)) return;
                    fixture.ResidualEvents.Enqueue(eventName);
                    if (eventName == "QA-Residual-Goal") fixture._goal.TrySetResult();
                    if (eventName == "QA-Residual-End") fixture._end.TrySetResult();
                });
                Receive<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(message => {
                    if (message.Message is not null) { fixture.Broadcasts.Enqueue(message.Message); if (message.Message is WIZARD_12_PROTOCOL.MSG_LEVELUP) fixture.LevelUp.TrySetResult(); }
                }); ReceiveAny(_ => { });
            }
        }
    }
    private sealed record Ready;
    private sealed record InvokeStep(string Method, object[] Arguments);
    private sealed record CompleteStep;
    private sealed record SelectStep;
    private sealed record ReconcileStep;
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
    private sealed class QuestProbe : QuestService {
        private readonly TerminalClaimFixture _store;
        private readonly CapturedScopes _scopes;
        public QuestProbe(SessionActor session, TerminalClaimFixture store, CapturedScopes scopes, bool cacheTemplate) : base(session) {
            _store = store; _scopes = scopes; SetCached(this, store.Live);
            if (cacheTemplate) ((List<QuestTemplate>)typeof(QuestService).GetField("_cachedQuestTemplates", Private)!.GetValue(this)!).Add(store.Template);
            ((Imlight.Classic.Security.BoundedOffers<QuestTemplate>)typeof(QuestService)
                .GetField("_cachedQuestOffers", Private)!.GetValue(this)!).Add(store.Template.m_questName, store.Template);
        }
        protected override void ConfigureReceivers() {
            Receive<Ready>(_ => Sender.Tell(_store is not null && _scopes is not null));
            Receive<InvokeStep>(step => Execute(step.Method, step.Arguments));
            Receive<CompleteStep>(_ => Execute("CompleteGoal", [_store.Expected, _store.Goal]));
            Receive<SelectStep>(_ => Execute("TryCompleteTerminalClaim", [_store.Live, _store.Expected, _store.Goal]));
            Receive<ReconcileStep>(_ => Execute("TryGrantCommittedDungeonQuests", [_store.Live]));
            Receive<string>(message => message == "DoneDisposing", _ => { });
            base.ConfigureReceivers();
        }
        private void Execute(string method, object[] arguments) {
            try { using var scope = _scopes.Enter(); var result = typeof(QuestService).GetMethod(method, Private)!.Invoke(this, arguments); Sender.Tell(result ?? true); }
            catch (Exception exception) { Sender.Tell(new Status.Failure(exception)); }
        }
    }
    private sealed class ZoneProbe : ZoneService {
        private readonly Wizard _wizard;
        public ZoneProbe(SessionActor session, Wizard wizard, IActorRef endpoint) : base(session) { _wizard = wizard; ZoneActor = endpoint; SetCached(this, wizard); }
        protected override void ConfigureReceivers() {
            // The reflection table omits private base handlers for a derived fixture. Register before its catch-all.
            var receiveBroadcast = typeof(ZoneService).GetMethod("ReceiveZoneBroadcast", Private)!;
            Receive<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(message => receiveBroadcast.Invoke(this, [message]));
            Receive<Ready>(_ => Sender.Tell(this)); base.ConfigureReceivers();
        }
    }
    private static void SetCached(MessageService service, Wizard wizard) {
        typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(service, wizard);
        typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(service, wizard.GameObject);
    }
    private sealed class CapturedScopes {
        private readonly QuestMutationDependencies? _questMutation = WizardQuestTransactions.TestScope.Value;
        private readonly WizardCollection.TestStore? _store = WizardCollection.TestStoreScope.Value;
        private readonly QuestClaimDependencies? _claim = ClassicQuestClaims.TestScope.Value;
        private readonly StackRewardDependencies? _stack = ClassicStackRewards.TestScope.Value;
        private readonly ProgressionDependencies? _progression = WizardProgressionTransactions.TestScope.Value;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _items = WizardInventoryTransactions.TestRowsScope.Value;
        private readonly Func<IDocumentSession, List<ClientReagentItem>>? _reagents = WizardReagentCollection.TestRowsScope.Value;
        internal IDisposable Enter() {
            var oldQuestMutation = WizardQuestTransactions.TestScope.Value;
            WizardQuestTransactions.TestScope.Value = _questMutation;
            var oldStore = WizardCollection.TestStoreScope.Value; var oldClaim = ClassicQuestClaims.TestScope.Value;
            var oldStack = ClassicStackRewards.TestScope.Value; var oldProgression = WizardProgressionTransactions.TestScope.Value;
            var oldItems = WizardInventoryTransactions.TestRowsScope.Value; var oldReagents = WizardReagentCollection.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = _store; ClassicQuestClaims.TestScope.Value = _claim; ClassicStackRewards.TestScope.Value = _stack;
            WizardProgressionTransactions.TestScope.Value = _progression; WizardInventoryTransactions.TestRowsScope.Value = _items; WizardReagentCollection.TestRowsScope.Value = _reagents;
            return new Restore(() => {
                WizardQuestTransactions.TestScope.Value = oldQuestMutation;
                WizardCollection.TestStoreScope.Value = oldStore; ClassicQuestClaims.TestScope.Value = oldClaim; ClassicStackRewards.TestScope.Value = oldStack;
                WizardProgressionTransactions.TestScope.Value = oldProgression; WizardInventoryTransactions.TestRowsScope.Value = oldItems; WizardReagentCollection.TestRowsScope.Value = oldReagents;
            });
        }
    }
    private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
}
