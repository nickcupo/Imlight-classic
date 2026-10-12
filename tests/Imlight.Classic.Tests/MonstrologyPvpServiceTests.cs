// CLASSIC: a delayed, persisted PvE receipt must not open an extraction result during a new PvP duel.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class MonstrologyPvpServiceTests {
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);
    private const ulong Owner=998771,DuelId=998772;

    [Theory] [InlineData("WizardCity/WC_Duel_Arena_New")] [InlineData("Authored/OpenPvp")]
    public async Task ActualServiceRejectsStaleReceiptInTrustedPvpBeforeAnyLedgerRead(string zone) {
        using var f=await Fixture.Create();
        f.Wizard.Zone=zone;
        ElixirService.PublishCombatTransition(f.Wizard,true,true);
        ActiveDuels.Update(new ActiveDuelInfo(DuelId,zone,true,2,0,0,DateTime.UtcNow,[Owner,Owner+10]));
        try {
            f.Service.Tell(new MonstrologyExtractionCommitted {OwnerId=Owner,OperationId="authored-pve-receipt"});
            await Processed(f.Service);
            Assert.Empty(await f.Packets());
            Assert.Equal(0,f.Store.Reads);
            Assert.Equal(1,f.Store.Ledger.Animus[35085]);
            Assert.Single(f.Store.Ledger.Extractions);
        }
        finally {ActiveDuels.Remove(DuelId);}
    }
    [Fact]
    public async Task PersistedPveReceiptStillUsesNativeEssenceAndProgressionOutsidePvpOnce() {
        using var f=await Fixture.Create();
        f.Service.Tell(new MonstrologyExtractionCommitted {OwnerId=Owner,OperationId="authored-pve-receipt"});
        await Processed(f.Service);
        var packets=await f.Packets();
        Assert.Equal(3,packets.Length);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATECOLLECTEDESSENCES>(packets[0]);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_DISPLAYCOLLECTEDESSENCES>(packets[1]);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP>(packets[2]);
        foreach(var packet in packets) Assert.Equal(packet.GetType(),Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(packet))!).GetType());
        Assert.Equal(2,f.Store.Reads);
        f.Service.Tell(new MonstrologyExtractionCommitted {OwnerId=Owner,OperationId="authored-pve-receipt"});
        await Processed(f.Service); Assert.Empty(await f.Packets()); Assert.Equal(2,f.Store.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ArenaWithoutActiveDuelOrAfterActualReleaseRejectsBeforeLedgerRead(bool released) {
        using var f=await Fixture.Create();
        f.Wizard.Zone=ClassicArena.Config!.Arenas[0].Zone;
        Assert.True(ClassicArena.IsArenaZone(f.Wizard.Zone));
        if(released) await f.ReleasePvp();
        Assert.False(f.Wizard.IsInDuel); Assert.Empty(ActiveDuels.Snapshot().Where(d=>d.CharacterIds.Contains(Owner)));
        f.Service.Tell(new MonstrologyExtractionCommitted {OwnerId=Owner,OperationId="authored-pve-receipt"});
        await Processed(f.Service); Assert.Empty(await f.Packets()); Assert.Equal(0,f.Store.Reads);
    }
    [Fact]
    public async Task UnknownInDuelAuthorityRejectsBeforeLedgerRead() {
        using var f=await Fixture.Create(); f.Wizard.IsInDuel=true;
        f.Service.Tell(new MonstrologyExtractionCommitted {OwnerId=Owner,OperationId="authored-pve-receipt"});
        await Processed(f.Service); Assert.Empty(await f.Packets()); Assert.Equal(0,f.Store.Reads);
    }
    [Fact]
    public async Task TrustedDetachedPvpStillRejectsAndActualReleaseOutsideArenaAllowsDelayedPveOnce() {
        using var f=await Fixture.Create();
        ElixirService.PublishCombatTransition(f.Wizard,true,true); ElixirService.DetachCombatSession(f.Wizard);
        Assert.False(f.Wizard.IsInDuel);
        var receipt=new MonstrologyExtractionCommitted {OwnerId=Owner,OperationId="authored-pve-receipt"};
        f.Service.Tell(receipt); await Processed(f.Service); Assert.Empty(await f.Packets()); Assert.Equal(0,f.Store.Reads);
        await f.ReleasePvp();
        f.Service.Tell(receipt); await Processed(f.Service);
        Assert.Equal(3,(await f.Packets()).Length); Assert.Equal(2,f.Store.Reads);
    }
    private sealed record Drain;
    private sealed class Sink:ReceiveActor {
        public Sink() {var messages=new List<IMessage>();Receive<Drain>(_=>{Sender.Tell(messages.ToArray());messages.Clear();});Receive<IMessage>(m=>messages.Add(m));}
    }
    private static Task<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY> Processed(IActorRef actor)=>actor.Ask<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY>(
        new SERVICE_101_PROTOCOL.MSG_QUERYMESSAGESERVICEIDENTITY(),Timeout,TestContext.Current.CancellationToken);

    internal sealed class Fixture:IDisposable {
        private readonly ActorSystem _system;
        private readonly IActorRef _sink;
        internal readonly IActorRef Service;
        internal readonly StoreProxy Store;
        internal Wizard Wizard=null!;
        private readonly bool _initializeRuntime;
        private Fixture(bool initializeRuntime) {
            _initializeRuntime=initializeRuntime;
            EquipmentAttachConcurrencyTests.Configure("[Classic]\nMonstrology=true\nArenaMatches=true\n");
            if(initializeRuntime) {
                ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
                ClassicArena.UseForTests(Imlight.Classic.Pvp.ArenaLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root,"pvp","arena-2009.yaml")));
            }
            Assert.False(PlayerDatabase.IsCreated);
            var store=DispatchProxy.Create<IDocumentStore,StoreProxy>();Store=(StoreProxy)(object)store;
            _system=ActorSystem.Create("monstrology-pvp-service-"+Guid.NewGuid().ToString("N"),"akka.actor.provider=local");
            _sink=_system.ActorOf(Props.Create(()=>new Sink()));
            var session=(SessionActor)RuntimeHelpers.GetUninitializedObject(typeof(SessionActor));
            typeof(SessionActor).GetField("<ActorRef>k__BackingField",Private)!.SetValue(session,_sink);
            typeof(SessionActor).GetField("<MonstrologySession>k__BackingField",Private)!.SetValue(session,new MonstrologySessionPolicy());
            Service=_system.ActorOf(Props.Create(()=>new MonstrologyService(session,new MonstrologyRepository(store))));
        }
        internal static async Task<Fixture> Create(Wizard? supplied=null,bool initializeRuntime=true) {
            var f=new Fixture(initializeRuntime);
            var identity=await Processed(f.Service);
            var wizard=supplied??new Wizard {CharId=Owner,Location=new Imcodec.Math.Vector3(0,0,0),EquipmentBehavior=new ServerWizEquipmentBehavior {EquippedItems=new(),EquippedItemIds=[],SlotList=[]},GameStats=new ServerWizGameStats(default,1) {m_monsterMagicLevel=1,m_currentHitpoints=1000,m_baseHitpoints=1000}};
            f.Wizard=wizard; f.Store.Ledger.OwnerId=wizard.CharId;
            typeof(MessageService).GetField("_cachedWizard",Private)!.SetValue(identity.Service,wizard);
            typeof(MessageService).GetField("_cachedWizardGameObject",Private)!.SetValue(identity.Service,wizard.GameObject);
            return f;
        }
        internal async Task ReleasePvp() {
            var session=(SessionActor)RuntimeHelpers.GetUninitializedObject(typeof(SessionActor));
            typeof(SessionActor).GetField("<ActorRef>k__BackingField",Private)!.SetValue(session,_sink);
            var guardStore=DispatchProxy.Create<IDocumentStore,StoreProxy>();
            var guard=(StoreProxy)(object)guardStore; guard.ForbidAccess=true;
            var scope=new Imlight.CoreLib.WizardData.Collections.WizardCollection.TestStore(
                ()=>guardStore.OpenSession(),(_,_)=>throw new InvalidOperationException("No character load expected"));
            var combat=_system.ActorOf(Props.Create(()=>new PvpMonstrologyTransportTests.CombatHarness(session,scope)));
            var identity=await Processed(combat);
            typeof(MessageService).GetField("_cachedWizard",Private)!.SetValue(identity.Service,Wizard);
            typeof(MessageService).GetField("_cachedWizardGameObject",Private)!.SetValue(identity.Service,Wizard.GameObject);
            combat.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE {Fought=true});
            await Processed(combat); await Packets(); Assert.Equal(0,guard.Opens); Assert.False(PlayerDatabase.IsCreated);
        }
        internal Task<IMessage[]> Packets()=>_sink.Ask<IMessage[]>(new Drain(),Timeout,TestContext.Current.CancellationToken);
        public void Dispose() {_system.Terminate().GetAwaiter().GetResult();Assert.False(PlayerDatabase.IsCreated);if(_initializeRuntime) {ClassicArena.UseForTests(null);ClassicRuntime.ResetForTests();}}
    }
    public class StoreProxy:DispatchProxy {
        internal int Reads,Opens;
        internal bool ForbidAccess;
        internal readonly MonstrologyLedger Ledger=new() {OwnerId=Owner,Level=1,Experience=10,
            Animus=new() {[35085]=1},Extractions=new() {["authored-pve-receipt"]=new ExtractionReceipt(35085,1,10)}};
        protected override object? Invoke(MethodInfo? method,object?[]? args) {
            if(method!.Name=="OpenSession") {Opens++;if(ForbidAccess) throw new InvalidOperationException("Transport fixture forbids all PlayerDatabase access");var session=DispatchProxy.Create<IDocumentSession,SessionProxy>();((SessionProxy)(object)session).Store=this;return session;}
            throw new NotSupportedException("Unexpected authored store operation: "+method.Name);
        }
    }
    public class SessionProxy:DispatchProxy {
        internal StoreProxy Store=null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args) {
            if(method!.Name=="Load") {Assert.Equal("MonstrologyLedgers/"+Store.Ledger.OwnerId,(string)args![0]!);Store.Reads++;return Store.Ledger;}
            if(method.Name=="Dispose") return null;
            throw new NotSupportedException("Unexpected authored ledger operation: "+method.Name);
        }
    }
}
