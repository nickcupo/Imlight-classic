// CLASSIC: exercise real duel releases, player CombatService and the production ambient endpoint with authored state.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Collections;
using Raven.Client.Documents;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class PvpMonstrologyTransportTests : IDisposable {
    private const ulong Sigil=997681;
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);

    public PvpMonstrologyTransportTests() {
        EquipmentAttachConcurrencyTests.Configure("[Classic]\nMonstrology=true\n");
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
    }
    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData(false,false,"completed")] [InlineData(true,false,"completed")]
    [InlineData(false,false,"concede")] [InlineData(true,false,"concede")]
    [InlineData(false,false,"flee")] [InlineData(true,false,"flee")]
    [InlineData(false,true,"completed")] [InlineData(true,true,"completed")]
    [InlineData(false,true,"concede")] [InlineData(true,true,"concede")]
    [InlineData(false,true,"flee")] [InlineData(true,true,"flee")]
    public async Task PlayerAndAmbientPvpEndPathsCannotPublishExtractionResultsOrNativePostCombatEffects(bool ambient,bool arena,string ending) {
        using var f=await Fixture.Create(ambient,arena);
        await f.Run(duel=> {
            if(ending=="completed") {
                duel.SubCircles[0].ParticipantGameStats.m_currentHitpoints=0;
                CombatRegressionTests.Invoke(duel,"EndDuel");
            }
            else if(ending=="concede") CombatRegressionTests.Invoke(duel,"HandleFleeAction",duel.SubCircles[4]);
            else CombatRegressionTests.Invoke(duel,"ReceiveCombatMove",new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor=f.OwnerEndpoint,MoveType=(byte)CombatMoveType.Flee,
            });
            Assert.Equal(kDuelPhase.kPhase_Ended,duel.Duel.m_duelPhase);
            return true;
        });
        var packets=await f.Drain();
        AssertNoMonstrology(packets);
        Assert.DoesNotContain(packets,p=>p is GAME_5_PROTOCOL.MSG_ADDEFFECT or GAME_5_PROTOCOL.MSG_REMOVEEFFECT);
        Assert.Contains(packets,p=>p is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT);
        Assert.Contains(packets,p=>p is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL);
        Assert.False(f.Owner.IsInDuel); Assert.True(f.Owner.IsInCombatGrace);
        Assert.Empty(f.Owner.GameEffects.Snapshot());
        Assert.Equal(1000,f.Owner.GameStats.m_currentHitpoints);
        Assert.Empty(f.Internal.OfType<Imlight.CoreLib.Game.Monstrology.MonstrologyExtractionCommitted>());
        foreach(var packet in packets) {
            var decoded=Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(packet))!);
            Assert.Equal(packet.GetType(),decoded.GetType());
        }
    }

    [Fact]
    public async Task SilentPvpGraceRetainsMovementAndExpiryAuthorityWithoutCreatingAnUnpublishedNamedEffect() {
        using var f=await Fixture.Create(false);
        f.OwnerService.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE {Won=true,Fought=true});
        Assert.Empty(await f.Drain());
        Assert.True(f.Owner.IsInCombatGrace);
        f.OwnerService.Tell(new GAME_5_PROTOCOL.MSG_CLIENTMOVE {LocationX=100,LocationY=0,LocationZ=0});
        Assert.Empty(await f.Drain());
        Assert.Equal(PostCombatPhase.Moving,Grace(f.OwnerServiceInstance!).Phase);
        Assert.True(f.Owner.IsInCombatGrace); Assert.Empty(f.Owner.GameEffects.Snapshot());
        f.OwnerService.Tell(new COMBAT_106_PROTOCOL.MSG_NOAGGROGRACEOVER());
        Assert.Empty(await f.Drain());
        Assert.False(f.Owner.IsInCombatGrace); Assert.Equal(PostCombatPhase.None,Grace(f.OwnerServiceInstance!).Phase);
    }

    [Fact]
    public async Task PveGraceStillPublishesItsKnownNativeEffectsAndRemovesThemInOrder() {
        using var f=await Fixture.Create(false);
        var service=f.OwnerServiceInstance!;
        f.OwnerService.Tell(new PublishPveGrace());
        var initial=await f.Drain();
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(Assert.Single(initial));
        Assert.True(f.Owner.IsInCombatGrace); Assert.Single(f.Owner.GameEffects.Snapshot());
        f.OwnerService.Tell(new GAME_5_PROTOCOL.MSG_CLIENTMOVE {LocationX=100,LocationY=0,LocationZ=0});
        var swap=await f.Drain();
        Assert.Equal(2,swap.Length); Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(swap[0]);
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(swap[1]);
        f.OwnerService.Tell(new COMBAT_106_PROTOCOL.MSG_NOAGGROGRACEOVER());
        Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(Assert.Single(await f.Drain()));
        Assert.False(f.Owner.IsInCombatGrace); Assert.Empty(f.Owner.GameEffects.Snapshot());
    }

    [Fact]
    public async Task PveGraceIsRemovedAtPvpEntryAndLaterRealPveVictoryRestoresNativePublication() {
        using var f=await Fixture.Create(false);
        f.OwnerService.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATWIN {UsedPips=0,MobTemplateIds=[]});
        var first=await f.Drain();
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(Assert.Single(first));
        Assert.True(f.Owner.IsInCombatGrace); Assert.Single(f.Owner.GameEffects.Snapshot());
        var duel=(CombatDuelComponent)await f.Run(d=>d);
        f.OwnerService.Tell(new EnterPvp(new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {Duel=duel}));
        Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(Assert.Single(await f.Drain()));
        Assert.True(f.Owner.IsInDuel); Assert.False(f.Owner.IsInCombatGrace); Assert.Empty(f.Owner.GameEffects.Snapshot());
        f.OwnerService.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE {Won=false,Fought=true});
        Assert.Empty(await f.Drain()); Assert.True(f.Owner.IsInCombatGrace); Assert.Empty(f.Owner.GameEffects.Snapshot());
        f.OwnerService.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATWIN {UsedPips=0,MobTemplateIds=[]});
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(Assert.Single(await f.Drain()));
        Assert.False(f.Owner.IsInDuel); Assert.True(f.Owner.IsInCombatGrace); Assert.Single(f.Owner.GameEffects.Snapshot());
        f.OwnerService.Tell(new GAME_5_PROTOCOL.MSG_CLIENTMOVE {LocationX=100,LocationY=0,LocationZ=0});
        var moving=await f.Drain(); Assert.Equal(2,moving.Length);
        Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(moving[0]); Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(moving[1]);
        f.OwnerService.Tell(new COMBAT_106_PROTOCOL.MSG_NOAGGROGRACEOVER());
        Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(Assert.Single(await f.Drain()));
        Assert.False(f.Owner.IsInCombatGrace); Assert.Empty(f.Owner.GameEffects.Snapshot());
    }

    [Fact]
    public async Task ActualLobbyLeaveWithAnotherSeatCannotUseStaleDirectoryToBlockDelayedPveReceipt() {
        using var f=await Fixture.Create(false,false);
        ElixirService.PublishCombatTransition(f.Owner,true,true);
        await f.Run(d=> {
            typeof(CombatDuelComponent).GetField("_pvpLobby",Private)!.SetValue(d,true);
            CombatRegressionTests.Invoke(d,"PublishActiveDuel");
            CombatRegressionTests.Invoke(d,"HandleFleeAction",d.SubCircles[4]);
            Assert.True(d.SubCircles[0].Occupied); Assert.False(d.SubCircles[4].Occupied);
            return true;
        });
        await f.Drain(); Assert.False(f.Owner.IsInDuel);
        Assert.Contains(ActiveDuels.Snapshot(),d=>d.Pvp&&d.CharacterIds.Contains(f.Owner.CharId));
        using var receipt=await MonstrologyPvpServiceTests.Fixture.Create(f.Owner,initializeRuntime:false);
        receipt.Service.Tell(new MonstrologyExtractionCommitted {OwnerId=f.Owner.CharId,OperationId="authored-pve-receipt"});
        await Processed(receipt.Service);
        var packets=await receipt.Packets();
        Assert.Equal(3,packets.Length); Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATECOLLECTEDESSENCES>(packets[0]);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_DISPLAYCOLLECTEDESSENCES>(packets[1]);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP>(packets[2]); Assert.Equal(2,receipt.Store.Reads);
    }

    private static PostCombatGrace Grace(CombatService service) => (PostCombatGrace)typeof(CombatService).GetField("_grace",Private)!.GetValue(service)!;
    private static void AssertNoMonstrology(IEnumerable<IMessage> packets) => Assert.DoesNotContain(packets,p=>p is
        WIZARD2_53_PROTOCOL.MSG_UPDATECOLLECTEDESSENCES or WIZARD2_53_PROTOCOL.MSG_DISPLAYCOLLECTEDESSENCES
        or WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME or WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP
        or WIZARD2_53_PROTOCOL.MSG_MONSTERMAGICLEVELUP or DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATVICTORY);
    private sealed record Snapshot;
    private sealed record Barrier;
    private sealed record PublishPveGrace;
    private sealed record EnterPvp(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL Message);
    private sealed record RunDuel(Func<CombatDuelComponent,object> Action);

    private sealed class Fixture : IDisposable {
        private readonly ActorSystem _system;
        private readonly WizardCollection.TestStore _storeScope;
        private readonly MonstrologyPvpServiceTests.StoreProxy _storeGuard;
        private readonly IActorRef _sink;
        private IActorRef _zone=null!,_host=null!;
        private readonly IActorRef[] _services,_endpoints;
        private AmbientWizard? _ambient;
        internal readonly Wizard Owner;
        internal readonly List<object> Internal=[];
        internal IActorRef OwnerEndpoint=>_endpoints[1];
        internal IActorRef OwnerService=>_services[1];
        internal CombatService? OwnerServiceInstance;
        private Fixture() {
            // Guard the existing scoped character-store seam without constructing the database singleton.
            Assert.False(PlayerDatabase.IsCreated);
            var store=DispatchProxy.Create<IDocumentStore,MonstrologyPvpServiceTests.StoreProxy>();
            _storeGuard=(MonstrologyPvpServiceTests.StoreProxy)(object)store; _storeGuard.ForbidAccess=true;
            _storeScope=new WizardCollection.TestStore(()=>store.OpenSession(),(_,_)=>throw new InvalidOperationException("No character load expected"));
            _system=ActorSystem.Create("pvp-monstrology-"+Guid.NewGuid().ToString("N"),"akka.actor.provider=local");
            _sink=_system.ActorOf(Props.Create(()=>new Sink()));
            _services=new IActorRef[2]; _endpoints=new IActorRef[2];
            Owner=Wizard(997683);
        }
        internal static async Task<Fixture> Create(bool ambient,bool arena=true) {
            var f=new Fixture();
            for(var i=0;i<2;i++) {
                var wizard=i==1?f.Owner:Wizard(997682);
                f._services[i]=f._system.ActorOf(Props.Create(()=>new CombatHarness(Session(f._sink),f._storeScope)));
                var identity=await Processed(f._services[i]);
                typeof(MessageService).GetField("_cachedWizard",Private)!.SetValue(identity.Service,wizard);
                typeof(MessageService).GetField("_cachedWizardGameObject",Private)!.SetValue(identity.Service,wizard.GameObject);
                if(i==1) f.OwnerServiceInstance=(CombatService)identity.Service;
                f._endpoints[i]=f._system.ActorOf(Props.Create(()=>new Endpoint(f._services[i],f._sink,f.Internal)));
            }
            Wizard opponent=Wizard(997682);
            if(ambient) {
                var identity=AmbientIdentity.Generate(3,"WizardCity/WC_Duel_Arena_New",new NameTableSizes(100,100,50,50),(20,20));
                f._ambient=new AmbientWizard(AmbientWizardRecord.From(identity,opponent.CharId),opponent);
                var driver=f._system.ActorOf(Props.Create(()=>new Sink()));
                f._ambient.Endpoint=f._system.ActorOf(AmbientEndpoint.Props(f._ambient,driver));
                f._endpoints[0]=f._ambient.Endpoint;
                AmbientWizards.Register(f._ambient);
            }
            f._zone=f._system.ActorOf(Props.Create(()=>new Zone(f._endpoints)));
            f._host=f._system.ActorOf(Props.Create(()=>new Host(f._zone,f._endpoints,opponent,f.Owner,arena,f._storeScope)));
            return f;
        }
        internal Task<object> Run(Func<CombatDuelComponent,object> action)=>_host.Ask<object>(new RunDuel(action),Timeout,TestContext.Current.CancellationToken);
        internal async Task<IMessage[]> Drain() {
            await _zone.Ask<bool>(new Barrier(),Timeout,TestContext.Current.CancellationToken);
            foreach(var endpoint in _endpoints) {
                if(_ambient?.Endpoint==endpoint) continue;
                await endpoint.Ask<bool>(new Barrier(),Timeout,TestContext.Current.CancellationToken);
            }
            foreach(var service in _services) await Processed(service);
            var captured=await _sink.Ask<object[]>(new Snapshot(),Timeout,TestContext.Current.CancellationToken);
            return captured.SelectMany(p=>p is ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {Message:{ } packet}
                ?new[]{packet}:p is IMessage wire?new[]{wire}:Array.Empty<IMessage>()).ToArray();
        }
        public void Dispose() {
            _system.Terminate().GetAwaiter().GetResult();
            if(_ambient is not null) AmbientWizards.Unregister(_ambient);
            Assert.Equal(0,_storeGuard.Opens); Assert.False(PlayerDatabase.IsCreated);
        }
        private static Wizard Wizard(ulong id)=>new() {CharId=id,IsInDuel=true,
            GameObject=new WizClientObject {m_templateID=1,m_globalID=Imlight.CoreLib.WizardData.Models.Player.Wizard.GetGameObjectId(id),m_location=new Imcodec.Math.Vector3(0,0,0)},
            EquipmentBehavior=new ServerWizEquipmentBehavior {EquippedItems=new(),EquippedItemIds=[],SlotList=[]},
            GameStats=new ServerWizGameStats(default,1) {m_currentHitpoints=1000,m_baseHitpoints=1000,m_monsterMagicLevel=1},
        };
        private static SessionActor Session(IActorRef sink) {
            var session=(SessionActor)RuntimeHelpers.GetUninitializedObject(typeof(SessionActor));
            typeof(SessionActor).GetField("<ActorRef>k__BackingField",Private)!.SetValue(session,sink); return session;
        }
    }
    private static Task<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY> Processed(IActorRef actor)=>actor.Ask<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY>(
        new SERVICE_101_PROTOCOL.MSG_QUERYMESSAGESERVICEIDENTITY(),Timeout,TestContext.Current.CancellationToken);
    // Keep the private production handlers on their declaring type; the additional message only runs
    // the PvE positive control inside ActorContext, rather than invoking an actor from the test thread.
    internal sealed class CombatHarness : CombatService {
        private readonly WizardCollection.TestStore _store;
        public CombatHarness(SessionActor session,WizardCollection.TestStore store):base(session) { _store=store; }
        protected override void ConfigureReceivers() {
            Receive<PublishPveGrace>(_=>WithStore(_store,()=>typeof(CombatService).GetMethod("SetNoAggroGrace",Private)!.Invoke(this,null)));
            Receive<EnterPvp>(entry=>WithStore(_store,()=> {
                // Exercise the real receipt-consumption boundary and its production grace removal callback.
                Assert.True(CompleteCombatEntry(GetActiveWizard(),entry.Message,
                    ()=>typeof(CombatService).GetMethod("RemoveNoAggroEffect",Private)!.Invoke(this,null),SendToSocket));
            }));
            Receive<object>(message=>WithStore(_store,()=> {
                var dispatch=MessageHandlerTable.DispatcherFor(typeof(CombatService),message.GetType());
                if(dispatch is not null) dispatch(this,message); else Unhandled(message);
            }));
        }
    }
    private static void WithStore(WizardCollection.TestStore store,System.Action action) {
        var previous=WizardCollection.TestStoreScope.Value;
        WizardCollection.TestStoreScope.Value=store;
        try {action();} finally {WizardCollection.TestStoreScope.Value=previous;}
    }
    private sealed class Sink : ReceiveActor {
        public Sink() {var messages=new List<object>(); Receive<Snapshot>(_=>{Sender.Tell(messages.ToArray());messages.Clear();});ReceiveAny(m=>messages.Add(m));}
    }
    private sealed class Endpoint : ReceiveActor {
        public Endpoint(IActorRef service,IActorRef sink,List<object> messages) {
            Receive<Barrier>(_=>Sender.Tell(true));
            ReceiveAny(m=> {lock(messages) messages.Add(m);if(m is IServerMessage) service.Tell(m,Self);else sink.Tell(m,Self);});
        }
    }
    private sealed class Zone : ReceiveActor {
        public Zone(IActorRef[] players) {
            Receive<Barrier>(_=>Sender.Tell(true));
            ReceiveAny(m=> {if(m is ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {Message:{ } packet}) foreach(var player in players) player.Tell(packet,Self);});
        }
    }
    private sealed class Host : ZoneEntity {
        private readonly CombatDuelComponent _duel;
        private readonly WizardCollection.TestStore _store;
        public Host(IActorRef zone,IActorRef[] players,Wizard opponent,Wizard owner,bool arena,WizardCollection.TestStore store):base(new CoreObject {m_globalID=Sigil},
            new GameObjectTemplate {m_behaviors=[]},new CoreObjectInfo(),zone,null!) {
            _store=store;
            _duel=new CombatDuelComponent(this);_duel.AttachTo(Self);
            _duel.Timers=DispatchProxy.Create<ITimerScheduler,PvpReleaseTimers>();
            CombatRegressionTests.SetProperty(_duel,"Duel",new Duel {m_duelID=Sigil,m_bPVP=true,m_duelPhase=kDuelPhase.kPhase_Planning,
                m_firstTeamToAct=(int)CombatTeam.Player,m_flatParticipantList=[],m_duelModifier=new DuelModifier {m_battlefieldEffects=[]}});
            CombatRegressionTests.SetProperty(_duel,"SubCircles",Enumerable.Range(0,8).Select(slot=>new CombatDuelSubCircle(_duel,0,0,default,slot) {
                PvpTeam=slot<4?CombatTeam.Monster:CombatTeam.Player,SlotType=slot<4?CombatSlotType.Creature:CombatSlotType.Player,
            }).ToArray());
            Field("_pvp",true);Field("_arena",arena);Field("_isActive",true);Field("_awaitingCombatMoves",true);
            CombatRegressionTests.SetProperty(_duel,"CombatResolver",new Imlight.CoreLib.Game.Combat.CombatResolver(_duel.Duel,_duel.SubCircles));_duel.CombatResolver.Reset();
            Field("_tutorialDirector",new TutorialDuelDirector(_duel,""));
            Field("_combatSigilObjectInfo",new CombatSigilObjectInfo {m_zoneTag="Animus fixture"});
            Occupy(0,players[0],opponent);Occupy(4,players[1],owner);
        }
        private void Field(string name,object value)=>typeof(CombatDuelComponent).GetField(name,Private)!.SetValue(_duel,value);
        private void Occupy(int slot,IActorRef actor,Wizard wizard) {
            var c=_duel.SubCircles[slot];c._wizard=wizard;c._combatDeck=new CombatDeck([],[],7);
            CombatRegressionTests.SetProperty(c,"ParticipantObject",wizard.GameObject);
            CombatRegressionTests.SetProperty(c,"ParticipantActor",actor);
            CombatRegressionTests.SetProperty(c,"ParticipantGameStats",wizard.GameStats);
            CombatRegressionTests.SetProperty(c,"CombatParticipant",new CombatParticipant {m_subcircle=slot,m_hangingEffects=[],m_pipCount=new PipCount()});
            c.AddedToDuel=true;
        }
        protected override void ConfigureReceivers() {Receive<RunDuel>(r=>{try{WithStore(_store,()=>Sender.Tell(r.Action(_duel)));}catch(Exception e){Sender.Tell(new Status.Failure(e));}});base.ConfigureReceivers();}
        protected override void PostStop() {ActiveDuels.Remove(Sigil);ClassicPvp.Forget("","Animus fixture");base.PostStop();}
    }
}
