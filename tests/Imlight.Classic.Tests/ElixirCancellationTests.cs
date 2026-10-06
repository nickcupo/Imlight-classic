using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using Action = System.Action;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed partial class ElixirCancellationTests {
    [Fact]
    public void ConfirmedCancellationConsumesOnlySelectedOriginalAndPublishesNativeExpiryWithoutRefund() {
        using var fixture = new Fixture();
        fixture.ApplyEffects();
        var saves = fixture.Store.Saves;
        var balance = fixture.Wizard.Account.Crowns;
        var result = fixture.Cancel();
        Assert.True(result.Saved);
        var packets = ElixirService.ExpireCommitted(fixture.Wizard, result, true);
        Assert.Equal(0u, Assert.IsType<WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER>(packets[0]).TimerTime);
        Assert.Equal((sbyte)0, Assert.IsType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(packets[1]).EffectEnabled);
        Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(packets[2]);
        Assert.Equal(9100ul, Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM>(packets[3]).ItemID);
        Assert.DoesNotContain(packets, p => p is GAME_5_PROTOCOL.MSG_EQUIPITEM or GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM);
        Assert.Equal(saves + 1, fixture.Store.Saves);
        Assert.Equal(balance, fixture.Wizard.Account.Crowns);
        Assert.Equal(balance, fixture.Store.SavedAccount.Crowns);
        fixture.AssertConsumedOnlySelected();
        Assert.False(fixture.Cancel().Saved);
        Assert.Equal(saves + 1, fixture.Store.Saves);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("foreign-id")]
    [InlineData("foreign-original")]
    [InlineData("foreign-live")]
    [InlineData("spoof-template")]
    [InlineData("matching-nonzero-template")]
    [InlineData("account")]
    [InlineData("dual-backpack")]
    [InlineData("dual-bank")]
    [InlineData("ordinary-gear")]
    [InlineData("unvalidated")]
    public void FailedOrForgedCancellationCannotChangeOriginalMembershipTimerEffectsOrBalance(string failure) {
        using var fixture = new Fixture();
        fixture.ApplyEffects();
        ulong id = 9100, template = 0;
        if (failure == "save") fixture.Store.FailSave = true;
        if (failure == "foreign-id") id = 9999;
        if (failure == "foreign-original") fixture.Original.m_characterId = 99;
        if (failure == "foreign-live") fixture.Wizard.EquipmentBehavior.GetItem(9100).m_characterId = 99;
        if (failure == "spoof-template") template = ulong.MaxValue;
        if (failure == "matching-nonzero-template") template = 191103;
        if (failure == "account") fixture.Wizard.AccountId = 99;
        if (failure == "dual-backpack") fixture.Store.SavedWizard.InventoryBehavior.InventoryItemIds.Add(9100);
        if (failure == "dual-bank") fixture.Store.SavedWizard.StorageBehavior.BankItemIds.Add(9100);
        if (failure == "ordinary-gear") {
            id = 9200;
            fixture.Wizard.EquipmentBehavior.ForceEquipItem(new WizClientObjectItem { m_globalID = id, m_characterId = 42, m_templateID = 191103 });
        }
        if (failure == "unvalidated") ElixirRuntime.Invalidate(fixture.Wizard);
        var saves = fixture.Store.Saves;
        var version = fixture.Store.Ledger.Version;
        var effects = fixture.Wizard.GameEffects.Snapshot().ToArray();
        var equipped = fixture.Wizard.EquipmentBehavior.EquippedItemIds.ToArray();
        var result = ElixirCollection.Cancel(fixture.Wizard, id, template);
        Assert.False(result.Saved);
        Assert.Empty(ElixirService.ExpireCommitted(fixture.Wizard, result, true));
        Assert.Equal(saves, fixture.Store.Saves);
        Assert.Equal(version, fixture.Store.Ledger.Version);
        Assert.Equal(1800u, fixture.Original.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
        Assert.Equal(equipped, fixture.Wizard.EquipmentBehavior.EquippedItemIds);
        Assert.Equal(effects, fixture.Wizard.GameEffects.Snapshot());
        Assert.Equal(.15f, fixture.Wizard.GameStats.m_dmgBonusPercentAll, 5);
        Assert.Equal(.15f, fixture.Wizard.GameStats.m_accBonusPercentAll, 5);
        Assert.Equal(9575, fixture.Wizard.Account.Crowns);
        Assert.Equal(9575, fixture.Store.SavedAccount.Crowns);
    }

    [Fact]
    public async Task ExpiryAndCancellationContendingForTheSameOriginalCommitOnlyOneConsumption() {
        using var fixture = new Fixture();
        fixture.ApplyEffects();
        var initial = fixture.Store.Saves;
        var results = await Task.WhenAll(Task.Run(fixture.Cancel), Task.Run(() => ElixirCollection.AdvanceOnline(
            fixture.Wizard, new Dictionary<ulong, uint> { [9100] = uint.MaxValue })));
        Assert.Single(results.Where(r => r.Saved));
        Assert.Equal(initial + 1, fixture.Store.Saves);
        var packets = results.SelectMany(r => ElixirService.ExpireCommitted(fixture.Wizard, r, true)).ToArray();
        Assert.Single(packets.OfType<WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER>());
        Assert.Single(packets.OfType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>());
        fixture.AssertConsumedOnlySelected();
    }

    [Fact]
    public void CommittedTimerRefreshPreservesOnlyMatchingTransientAppliedState() {
        using var fixture = new Fixture();
        fixture.ApplyEffects();
        Assert.True(ElixirCollection.AdvanceOnline(fixture.Wizard, 1).Saved);
        Assert.True(fixture.Wizard.EquipmentBehavior.GetItem(9100).m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied);
        Assert.False(fixture.Original.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied);
        var substituted = fixture.Wizard.EquipmentBehavior.GetItem(9100) with {
            m_templateID = 191104, m_inactiveBehaviors = [new ClientElixirBehavior { m_expireTime = 1798, m_statsApplied = false }],
        };
        fixture.Wizard.EquipmentBehavior.PublishElixirItems([substituted, fixture.Wizard.EquipmentBehavior.GetItem(9101)]);
        Assert.False(substituted.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied);
    }

    [Fact]
    public async Task RealElixirServiceEmitsOneEnableAcrossCheckpointsAndOnlyTransitionsForPvpAndExpiry() {
        using var fixture = new Fixture();
        using var system = ActorSystem.Create("elixir-cancel-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var packets = Channel.CreateUnbounded<IMessage>();
        var socket = system.ActorOf(Props.Create(() => new SocketProbe(packets)), "socket");
        var session = system.ActorOf(Props.CreateBy(new SessionProducer(socket)), "session");
        var instance = await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
        var actor = system.ActorOf(Props.Create(() => new ElixirProbe(instance, fixture.Store, fixture.Wizard)), "elixirs");
        try {
            var all = new List<IMessage>();
            async Task<IMessage[]> Step(Action? before = null, bool pvp = false) {
                Assert.True(await actor.Ask<bool>(new RefreshStep(before, pvp), Timeout, TestContext.Current.CancellationToken));
                await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                await socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
                var got = new List<IMessage>();
                while (packets.Reader.TryRead(out var packet)) got.Add(packet);
                all.AddRange(got);
                return [.. got];
            }
            Assert.Equal(2, (await Step()).OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>().Count(m => m.EffectEnabled == 1));
            for (var i = 0; i < 3; i++)
                Assert.Empty((await Step(() => Assert.True(ElixirCollection.AdvanceOnline(fixture.Wizard, 1).Saved)))
                    .OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>());
            Assert.Equal(2, (await Step(pvp: true)).OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>().Count(m => m.EffectEnabled == 0));
            Assert.Empty((await Step(pvp: true)).OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>());
            Assert.Equal(2, (await Step()).OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>().Count(m => m.EffectEnabled == 1));
            Assert.Empty((await Step(() => Assert.True(ElixirCollection.AdvanceOnline(fixture.Wizard, 1).Saved)))
                .OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>());
            var consumed = fixture.Cancel();
            Assert.True(consumed.Saved);
            // These are the same production cleanup packets used by InventoryService after its commit.
            var cleanup = ElixirService.ExpireCommitted(fixture.Wizard, consumed, true);
            Assert.Equal((sbyte)0, Assert.Single(cleanup.OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>()).EffectEnabled);
            Assert.Empty((await Step()).OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>());
            Assert.Equal(4, all.OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>().Count(m => m.EffectEnabled == 1));
            fixture.AssertConsumedOnlySelected();
        }
        finally { await system.Terminate(); }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private sealed record RefreshStep(Action? Before, bool Pvp);
    private sealed class SocketProbe : ReceiveActor {
        public SocketProbe(Channel<IMessage> packets) {
            Receive<IMessage>(message => packets.Writer.TryWrite(message));
        }
    }
    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
    // Exercise the real actor's Refresh/Checkpoint/effect publication on its actor thread.
    // Only time advancement is deterministic; timer callbacks are held so no test-store scope leaks.
    private sealed class ElixirProbe : ElixirService {
        private readonly ElixirTests.Store _store;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public ElixirProbe(SessionActor session, ElixirTests.Store store, Wizard wizard) : base(session) {
            _store = store;
            typeof(ElixirService).GetField("_wizard", Flags)!.SetValue(this, wizard);
        }
        protected override void ConfigureReceivers() {
            Receive<string>(value => value == "ClassicElixirTick", _ => { });
            Receive<RefreshStep>(step => {
                try {
                    using var scope = _store.Scope();
                    step.Before?.Invoke();
                    typeof(ElixirService).GetField("_pvp", Flags)!.SetValue(this, step.Pvp);
                    typeof(ElixirService).GetField("_combat", Flags)!.SetValue(this, step.Pvp);
                    typeof(ElixirService).GetMethod("Refresh", Flags)!.Invoke(this, [false]);
                    Sender.Tell(true);
                }
                catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            base.ConfigureReceivers();
        }
        protected override void OnPreDispose() { using var scope = _store.Scope(); base.OnPreDispose(); }
    }

    private sealed class Fixture : IDisposable {
        private readonly ElixirTests.CanonicalFixture _canonical = new();
        private readonly IDisposable _runtime;
        private readonly IDisposable _scope;
        private readonly IDictionary<ulong, CoreTemplate> _templates;
        private readonly Dictionary<ulong, CoreTemplate?> _previous = new();
        internal ElixirTests.Store Store { get; } = new();
        internal Wizard Wizard { get; }
        internal WizClientObjectItem Original => (WizClientObjectItem)Store.Documents["ClassicElixirItems/42/9100"];
        internal Fixture() {
            _runtime = _canonical.OctoberRuntime();
            _scope = Store.Scope();
            Wizard = Store.Login();
            Wizard.GameStats = new ServerWizGameStats(default!, 1);
            _templates = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
                .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            foreach (var pair in new[] { (9100ul, 191103u), (9101ul, 191101u) }) {
                var template = _canonical.Template(pair.Item2);
                _previous[pair.Item2] = _templates.TryGetValue(pair.Item2, out var previous) ? previous : null;
                _templates[pair.Item2] = template;
                Assert.True(ElixirCollection.Purchase(Wizard, _canonical.Item(pair.Item1, pair.Item2), template, true,
                    _canonical.Definition, Store.LoadAccount, _ => new Imcodec.IO.ByteString(new byte[] { 1 })).Saved);
            }
        }
        internal ElixirResult Cancel() => ElixirCollection.Cancel(Wizard, 9100);
        internal void ApplyEffects() {
            foreach (var item in Wizard.EquipmentBehavior.EquippedItems)
                Assert.Single(ElixirRuntime.AddApprovedEffects(Wizard, item, (WizItemTemplate)_templates[item.m_templateID.Full], false, false));
        }
        internal void AssertConsumedOnlySelected() {
            Assert.Equal(new ulong[] { 9001 }, Wizard.InventoryBehavior.InventoryItemIds);
            Assert.Equal(new ulong[] { 9001 }, Store.SavedWizard.InventoryBehavior.InventoryItemIds);
            Assert.Equal(9101ul, Assert.Single(Wizard.EquipmentBehavior.EquippedItemIds));
            Assert.Equal(9101ul, Assert.Single(Store.SavedWizard.EquipmentBehavior.EquippedItemIds));
            Assert.Equal(9101ul, Assert.Single(Store.Ledger.Active).ItemId);
            Assert.Equal(0u, Original.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
            Assert.True(Store.Documents.ContainsKey("ClassicElixirItems/42/9100"));
            Assert.DoesNotContain(Wizard.GameEffects.Snapshot(), e => e.m_originatorID == 9100);
            Assert.Single(Wizard.GameEffects.Snapshot(), e => e.m_originatorID == 9101);
            Assert.Equal(0f, Wizard.GameStats.m_dmgBonusPercentAll, 5);
            Assert.Equal(.15f, Wizard.GameStats.m_accBonusPercentAll, 5);
        }
        public void Dispose() {
            foreach (var pair in _previous) { if (pair.Value is null) _templates.Remove(pair.Key); else _templates[pair.Key] = pair.Value; }
            _scope.Dispose(); _runtime.Dispose(); _canonical.Dispose();
        }
    }
}
