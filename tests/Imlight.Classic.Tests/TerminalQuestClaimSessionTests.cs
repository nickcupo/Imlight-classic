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
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class TerminalQuestClaimSessionTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualCompleteGoalCommitsOnceBeforeItsNativeOrderAndRunsFilteredResidualsOnlyForTheNewAck(bool numericRewards) {
        using var f = new TerminalClaimFixture();
        f.Reward.GoldAmount = numericRewards ? 30 : 0;
        f.Goal.m_dialogList = Dialog("Completion", "QA_Goal_Complete");
        f.Template.m_dialogList = Dialog("Complete", "QA_Quest_Complete");
        f.Dependencies.SerializeDialog = (_, flags) => { Assert.Equal(16u, flags); return (ByteString)BitConverter.GetBytes(784001); };
        f.Goal.m_completeResults.m_results.Add(new ResPostEvent { m_eventName = "QA-Residual-Goal", m_requirements = new() {
            m_requirements = [new ReqHasGoal { m_questName = TerminalClaimFixture.QuestName, m_goalName = TerminalClaimFixture.GoalName, m_requiredStatus = GoalStatusRequirement.Complete }] } });
        f.Template.m_endResults.m_results = [new ResDropTable { m_tableName = "QA-END-REWARD" },
            new ResLearnSpell { m_templateID = TerminalClaimFixture.Learned },
            new ResPostEvent { m_eventName = "QA-Residual-End", m_requirements = new() { m_requirements = [new ReqEntryValue {
                m_isQuestRegistry = true, m_questName = TerminalClaimFixture.QuestName, m_entryName = "Complete", m_numericValue = 1,
                m_operatorType = OPERATOR_TYPE.OPERATOR_EQUALS }] } }];
        f.Dependencies.RollQuestReward = (table, _) => { f.Rolls++; return table == "QA-GOAL-REWARD" ? f.Reward : new() { GoldAmount = numericRewards ? 40 : 0 }; };
        var book = f.Live.SpellbookBehavior;
        using var actors = await SessionFixture.Create(f);
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actors.Packets); Assert.Empty(actors.ResidualEvents);
            Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName)); Assert.False(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
            Assert.Single(f.Working!.Receipts); Assert.Single(f.Working.Deleted);
        };
        Assert.True(await actors.Complete()); await actors.WaitForResiduals();
        var packets = await actors.Drain(); Assert.Equal(1, f.Saves); Assert.Equal(2, f.Rolls);
        Assert.Equal(numericRewards ? 170 : 100, f.Saved.GameStats.m_currentGold);
        Assert.Same(book, f.Live.SpellbookBehavior); Assert.Contains(TerminalClaimFixture.Learned, book.LearnedSpellTemplateIds);
        var expected = new List<Type> { typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL), typeof(WIZARD_12_PROTOCOL.MSG_ACTORDIALOG) };
        if (numericRewards) expected.AddRange([typeof(WIZARD_12_PROTOCOL.MSG_UPDATEGOLD), typeof(WIZARD_12_PROTOCOL.MSG_LOOT)]);
        expected.AddRange([typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEQUEST), typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST), typeof(WIZARD_12_PROTOCOL.MSG_ACTORDIALOG)]);
        if (numericRewards) expected.AddRange([typeof(WIZARD_12_PROTOCOL.MSG_UPDATEGOLD), typeof(WIZARD_12_PROTOCOL.MSG_LOOT)]);
        expected.AddRange([typeof(WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK), typeof(WIZARD_12_PROTOCOL.MSG_QUESTREWARDS)]);
        Assert.Equal(expected, packets.Select(packet => packet.GetType()));
        var complete = Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL>(packets[0]);
        Assert.Equal(TerminalClaimFixture.QuestId, complete.QuestID); Assert.Equal(TerminalClaimFixture.GoalId, complete.GoalID);
        var dialogs = packets.OfType<WIZARD_12_PROTOCOL.MSG_ACTORDIALOG>().ToArray();
        Assert.Equal("Completion", (string)dialogs[0].CompletionType); Assert.Equal(TerminalClaimFixture.QuestId, dialogs[0].QuestID);
        Assert.Equal(TerminalClaimFixture.GoalId, dialogs[0].GoalID); Assert.Equal("QuestComplete", (string)dialogs[1].CompletionType);
        Assert.Equal(0UL, dialogs[1].QuestID); Assert.Equal(0UL, dialogs[1].GoalID);
        Assert.Equal(["QA-Residual-End", "QA-Residual-Goal"], actors.ResidualEvents.OrderBy(name => name));
        Assert.True(await actors.Complete()); Assert.Empty(await actors.Drain());
        Assert.Equal(1, f.Saves); Assert.Equal(2, f.Rolls); Assert.Equal(2, actors.ResidualEvents.Count);
        Assert.False(actors.Session.IsDisposed); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("foreign-owner")] [InlineData("missing-template")]
    [InlineData("primitive")] [InlineData("loot")]
    [InlineData("dialog")]
    public async Task TerminalRefusalStopsBeforeTheOldFinalGoalSaveOrAnyNativeSuccessAndResidualEffect(string failure) {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 30;
        f.Goal.m_completeResults.m_results.Add(new ResPostEvent { m_eventName = "QA-Residual-Goal" });
        if (failure == "foreign-owner") f.Expected.OwnerCharId++;
        if (failure == "primitive") f.Dependencies.Prepare = _ => false;
        if (failure == "loot") f.Dependencies.SerializeLoot = (_, _) => new ByteString();
        if (failure == "dialog") { f.Goal.m_dialogList = Dialog("Completion", "QA_Refused"); f.Dependencies.SerializeDialog = (_, _) => new ByteString(); }
        using var actors = await SessionFixture.Create(f, cacheTemplate: failure != "missing-template");
        Assert.True(await actors.Complete()); Assert.Empty(await actors.Drain());
        Assert.Equal(0, f.Saves); Assert.Empty(f.Receipts); Assert.Single(f.Quests); Assert.Empty(actors.ResidualEvents);
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName)); Assert.False(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        Assert.False(actors.Session.IsDisposed); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualCallerClosesAfterUnknownAckWithoutFinalGoalRewardOrResidualSuccess(bool durable) {
        using var f = new TerminalClaimFixture { FailSave = true, Durable = durable }; f.Reward.GoldAmount = 30;
        f.Goal.m_completeResults.m_results.Add(new ResPostEvent { m_eventName = "QA-Residual-Goal" });
        using var actors = await SessionFixture.Create(f); var disposed = false;
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
            Assert.Empty(actors.Packets); Assert.Empty(actors.ResidualEvents); disposed = true;
        };
        Assert.True(await actors.Complete()); await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(disposed); Assert.True(actors.Session.IsDisposed); Assert.Empty(actors.Packets); Assert.Empty(actors.ResidualEvents);
        Assert.Equal(durable ? 130 : 100, f.Saved.GameStats.m_currentGold); Assert.Equal(durable ? 1 : 0, f.Receipts.Count);
        Assert.Equal(901, f.Live.GameStats.m_currentGold); Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Equal(QuestClaimStatus.Refused, f.Claim(out _)); Assert.Equal(1, f.Opened);
    }

    [Fact]
    public async Task ActualCallerClosesAnAcknowledgedPublicationFailureWithoutRerunningPersistentOrTransientResults() {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 30;
        f.Goal.m_completeResults.m_results.Add(new ResPostEvent { m_eventName = "QA-Residual-Goal" });
        f.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored acknowledged publication failure");
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Complete()); await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(actors.Session.IsDisposed); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Single(f.Receipts); Assert.Empty(f.Quests); Assert.Equal(130, f.Saved.GameStats.m_currentGold);
        Assert.Empty(actors.Packets); Assert.Empty(actors.ResidualEvents); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls);
    }

    [Theory]
    [InlineData("stock")] [InlineData("tutorial")] [InlineData("nonterminal")]
    public async Task TheActualCallerSelectionPreservesItsLegacyAndNonterminalFallbackBoundary(string path) {
        using var f = new TerminalClaimFixture();
        if (path == "stock") TerminalClaimFixture.SetRules(ClassicRules.Stock);
        if (path == "tutorial") { f.Template.m_questName = "Tutorial_Intro"; f.Expected.QuestName = "Tutorial_Intro"; }
        if (path == "nonterminal") {
            f.Template.m_goals.Add(new PersonaGoalTemplate { m_goalName = "Next", m_goalNameID = 784002, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA });
            f.Template.m_goalLogic = [new GoalCompleteLogic { m_goalsAND = [TerminalClaimFixture.GoalName], m_goalsToAdd = ["Next"] }];
        }
        using var actors = await SessionFixture.Create(f);
        // Invoke the actual pre-legacy selection; the old persistence path uses a separate real Raven authority.
        Assert.False(await actors.Select()); Assert.Empty(await actors.Drain()); Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls);
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName)); Assert.False(actors.Session.IsDisposed);
    }

    [Fact]
    public async Task ActualQuestLevelUpKeepsItsSingleZoneBroadcastAndPrivateRefillThenXpOrder() {
        using var f = new TerminalClaimFixture(); f.Reward.ExperienceAmount = 150;
        using var actors = await SessionFixture.Create(f);
        Assert.True(await actors.Complete()); await actors.LevelUp.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var packets = await actors.Drain(); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls);
        Assert.Equal(300, f.Live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(4, f.Live.MagicSchoolBehavior.Level);
        Assert.Equal([typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL), typeof(WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH),
            typeof(WIZARD_12_PROTOCOL.MSG_UPDATEMANA), typeof(WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP), typeof(PET_9_PROTOCOL.MSG_PETENERGYMAX),
            typeof(WIZARD_12_PROTOCOL.MSG_UPDATEXP), typeof(WIZARD_12_PROTOCOL.MSG_LOOT), typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEQUEST),
            typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST)], packets.Select(packet => packet.GetType()));
        var broadcast = Assert.Single(actors.Broadcasts.OfType<WIZARD_12_PROTOCOL.MSG_LEVELUP>());
        Assert.Equal(f.Live.GameObjectID, broadcast.GlobalID); Assert.Equal(4, broadcast.NewLevel);
        Assert.DoesNotContain(packets, packet => packet is WIZARD_12_PROTOCOL.MSG_LEVELUP);
        var xp = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEXP>()); Assert.Equal(150, xp.OldXP); Assert.Equal(150, xp.XP);
        Assert.True(await actors.Complete()); Assert.Empty(await actors.Drain()); Assert.Single(actors.Broadcasts.OfType<WIZARD_12_PROTOCOL.MSG_LEVELUP>());
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls);
    }

    [Theory]
    [InlineData("Start")] [InlineData("Prep")]
    public async Task ActualDungeonStartPreparationRefusesEmptyDialogBytesBeforeItsRowsOrJournalSave(string rejectedTag) {
        using var f = new TerminalClaimFixture();
        var template = new QuestTemplate { m_questName = "QA-DUNGEON-START-BYTES", m_questTitle = "QA_Quest", m_questLevel = 2,
            m_goals = [new PersonaGoalTemplate { m_goalName = "Start", m_goalNameID = 784010, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA,
                m_goalTitle = "QA_Goal", m_locationName = "QA_Location", m_completeResults = new() { m_results = [] },
                m_dialogList = Dialog("Prep", "QA_Goal_Prep") }], m_startGoals = ["Start"],
            m_dialogList = Dialog("Start", "QA_Quest_Start"), m_startResults = new() { m_results = [] }, m_endResults = new() { m_results = [] } };
        var refusedBytes = false;
        f.Dependencies.SerializeDialog = (dialog, _) => {
            if (dialog.m_dialogTag == rejectedTag) { refusedBytes = true; return new ByteString(); }
            return (ByteString)BitConverter.GetBytes(784011);
        };
        using var actors = await SessionFixture.Create(f); actors.Index(template);
        Assert.True(await actors.Reconcile()); Assert.True(refusedBytes); Assert.Empty(await actors.Drain());
        Assert.Equal(0, f.Saves); Assert.Single(f.Quests); Assert.Single(f.Saved.QuestBehavior.CurrentQuestIDs);
        Assert.Single(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Empty(actors.ResidualEvents);
        Assert.False(actors.Session.IsDisposed); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    private static ActorDialogList Dialog(string tag, string key) => new() { m_dialogs = [new ActorDialog { m_dialogTag = tag,
        m_dialogEntries = [new NPCDialogEntry { m_dialog = key, m_cameraName = "AuthoredCamera" }] }] };

    private sealed class SessionFixture : IDisposable {
        internal readonly TerminalClaimFixture Store;
        internal readonly ActorSystem System;
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        internal readonly ConcurrentQueue<string> ResidualEvents = new();
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
            System = ActorSystem.Create("terminal-claim-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
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
        internal Task<bool> Complete() => _quest.Ask<bool>(new CompleteStep(), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Select() => _quest.Ask<bool>(new SelectStep(), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Reconcile() => _quest.Ask<bool>(new ReconcileStep(), Timeout, TestContext.Current.CancellationToken);
        internal void Index(QuestTemplate template) => _index[Store.Live.Zone] = [template];
        internal async Task<IMessage[]> Drain() {
            await _session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await _socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var result = new List<IMessage>(); while (Packets.TryDequeue(out var packet)) result.Add(packet); return result.ToArray();
        }
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
        }
        protected override void ConfigureReceivers() {
            Receive<Ready>(_ => Sender.Tell(_store is not null && _scopes is not null));
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
        private readonly WizardCollection.TestStore? _store = WizardCollection.TestStoreScope.Value;
        private readonly QuestClaimDependencies? _claim = ClassicQuestClaims.TestScope.Value;
        private readonly StackRewardDependencies? _stack = ClassicStackRewards.TestScope.Value;
        private readonly ProgressionDependencies? _progression = WizardProgressionTransactions.TestScope.Value;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _items = WizardInventoryTransactions.TestRowsScope.Value;
        private readonly Func<IDocumentSession, List<ClientReagentItem>>? _reagents = WizardReagentCollection.TestRowsScope.Value;
        internal IDisposable Enter() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldClaim = ClassicQuestClaims.TestScope.Value;
            var oldStack = ClassicStackRewards.TestScope.Value; var oldProgression = WizardProgressionTransactions.TestScope.Value;
            var oldItems = WizardInventoryTransactions.TestRowsScope.Value; var oldReagents = WizardReagentCollection.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = _store; ClassicQuestClaims.TestScope.Value = _claim; ClassicStackRewards.TestScope.Value = _stack;
            WizardProgressionTransactions.TestScope.Value = _progression; WizardInventoryTransactions.TestRowsScope.Value = _items; WizardReagentCollection.TestRowsScope.Value = _reagents;
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = oldStore; ClassicQuestClaims.TestScope.Value = oldClaim; ClassicStackRewards.TestScope.Value = oldStack;
                WizardProgressionTransactions.TestScope.Value = oldProgression; WizardInventoryTransactions.TestRowsScope.Value = oldItems; WizardReagentCollection.TestRowsScope.Value = oldReagents;
            });
        }
    }
    private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
}
