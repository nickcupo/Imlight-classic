using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using System.IO;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ExcludedEnemyDespawnTests {
    [Theory]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", 1)]
    [InlineData("WizardCity/WC_Streets/WC_Colossus", 2)]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", 4)]
    public async Task EnemyAtUnavailableCircleReceivesDespawnOnce(string path, int enemies) {
        Configure();
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var system = ActorSystem.Create("excluded-" + Guid.NewGuid().ToString("N"));
        try {
            var channel = Channel.CreateUnbounded<object>();
            var zoneActor = system.ActorOf(Props.Create(() => new Recorder(channel)));
            var duel = CombatRegressionTests.MakeDuel();
            var zone = (Zone)RuntimeHelpers.GetUninitializedObject(typeof(Zone));
            CombatRegressionTests.SetProperty(zone, "ZonePath", path);
            var sigil = Entity(zone); var enemyEntity = Entity(zone);
            CombatRegressionTests.SetProperty(sigil, "ActiveGameObject", new CoreObject());
            typeof(ZoneEntityComponent).GetProperty("Entity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, sigil);
            typeof(CombatDuelComponent).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, true);
            typeof(CombatDuelComponent).GetField("_combatSigilObjectInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, new CombatSigilObjectInfo { m_radius = 200 });
            CombatRegressionTests.SetProperty(duel.SubCircles[4], "ParticipantObject", new CoreObject { m_templateID = 1 });
            for(var slot=0; slot < enemies; slot++) CombatRegressionTests.SetProperty(duel.SubCircles[slot], "ParticipantObject", new CoreObject { m_templateID = 2 });
            var objectAtCircle = new CoreObject { m_globalID = 123 };
            CombatRegressionTests.SetProperty(enemyEntity, "ActiveGameObject", objectAtCircle);
            CombatRegressionTests.SetProperty(enemyEntity, "ZoneRef", zoneActor);
            var roaming = system.ActorOf(Props.Create(() => new AiDriver(enemyEntity)));
            duel.OnCreatureMove(objectAtCircle, roaming, enemyEntity);
            duel.OnCreatureMove(objectAtCircle, roaming, enemyEntity);
            var broadcast = Assert.IsType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            var deletion = Assert.IsType<GAME_5_PROTOCOL.MSG_DELETEOBJECT>(broadcast.Message);
            Assert.Equal(123UL, deletion.GameObjectID);
            Assert.False(channel.Reader.TryRead(out _));
            Assert.Equal(enemies, duel.CreatureCount);
        } finally {
            await system.Terminate(); ClassicRuntime.ResetForTests();
        }
    }
    // CLASSIC: playbot ms (MS_Plague3_T1): a creature that has only just spawned inside a full circle is not deleted (its
    // delete, sent with its creation, left a copy in the client every spawn interval); it stays out of the duel.
    [Theory]
    [InlineData("WizardCity/WC_Streets/WC_Colossus", 2)]
    [InlineData("MooShu/MS_Plague/Interiors/MS_Plague3_T1", 2)]
    public async Task CreatureSpawnedInsideAFullCircleStaysOutOfTheDuel(string path, int enemies) {
        Configure();
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var system = ActorSystem.Create("fresh-spawn-" + Guid.NewGuid().ToString("N"));
        try {
            var channel = Channel.CreateUnbounded<object>();
            var zoneActor = system.ActorOf(Props.Create(() => new Recorder(channel)));
            var duel = CombatRegressionTests.MakeDuel();
            var zone = (Zone)RuntimeHelpers.GetUninitializedObject(typeof(Zone));
            CombatRegressionTests.SetProperty(zone, "ZonePath", path);
            var sigil = Entity(zone); var enemyEntity = Entity(zone);
            CombatRegressionTests.SetProperty(sigil, "ActiveGameObject", new CoreObject());
            typeof(ZoneEntityComponent).GetProperty("Entity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, sigil);
            typeof(CombatDuelComponent).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, true);
            typeof(CombatDuelComponent).GetField("_combatSigilObjectInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, new CombatSigilObjectInfo { m_radius = 200 });
            CombatRegressionTests.SetProperty(duel.SubCircles[4], "ParticipantObject", new CoreObject { m_templateID = 1 });
            for(var slot=0; slot < enemies; slot++) CombatRegressionTests.SetProperty(duel.SubCircles[slot], "ParticipantObject", new CoreObject { m_templateID = 2 });
            var objectAtCircle = new CoreObject { m_globalID = 123 };
            CombatRegressionTests.SetProperty(enemyEntity, "ActiveGameObject", objectAtCircle);
            CombatRegressionTests.SetProperty(enemyEntity, "ZoneRef", zoneActor);
            var fresh = system.ActorOf(Props.Create(() => new AiDriver(enemyEntity, TimeSpan.Zero)));
            duel.OnCreatureMove(objectAtCircle, fresh, enemyEntity);
            var probe = new Probe();
            fresh.Tell(probe);
            var (inDuel, finalKill, _) = await probe.Result.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(inDuel);
            Assert.False(finalKill);
            Assert.False(channel.Reader.TryRead(out _));
            Assert.Equal(enemies, duel.CreatureCount);
        } finally {
            await system.Terminate(); ClassicRuntime.ResetForTests();
        }
    }

    [Theory]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", 1)]
    [InlineData("WizardCity/WC_Streets/WC_Colossus", 2)]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", 4)]
    public async Task CompetingSigilRejectionDoesNotKillAnAdmittedCreature(string path, int enemies) {
        Configure();
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        using var system = ActorSystem.Create("competing-sigil-" + Guid.NewGuid().ToString("N"));
        try {
            var zone = (Zone)RuntimeHelpers.GetUninitializedObject(typeof(Zone));
            CombatRegressionTests.SetProperty(zone, "ZonePath", path);
            var output = Channel.CreateUnbounded<object>();
            var zoneActor = system.ActorOf(Props.Create(() => new Recorder(output)));
            var creature = new CoreObject { m_globalID = 123 };
            var entity = Entity(zone);
            CombatRegressionTests.SetProperty(entity, "ActiveGameObject", creature);
            CombatRegressionTests.SetProperty(entity, "ZoneRef", zoneActor);
            var aiActor = system.ActorOf(Props.Create(() => new AiDriver(entity)));
            var admitted = CombatRegressionTests.MakeDuel();
            CombatRegressionTests.SetProperty(admitted.SubCircles[0], "ParticipantObject", creature);
            admitted.SubCircles[0].AddedToDuel = true;
            // Send real admission and then generate rejection through a different sigil's movement path.
            aiActor.Tell(new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL { Duel = admitted, SubCircle = admitted.SubCircles[0] });
            var rejected = CombatRegressionTests.MakeDuel();
            var sigil = Entity(zone);
            CombatRegressionTests.SetProperty(sigil, "ActiveGameObject", new CoreObject());
            typeof(ZoneEntityComponent).GetProperty("Entity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rejected, sigil);
            typeof(CombatDuelComponent).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rejected, true);
            typeof(CombatDuelComponent).GetField("_combatSigilObjectInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rejected, new CombatSigilObjectInfo { m_radius = 200 });
            CombatRegressionTests.SetProperty(rejected.SubCircles[4], "ParticipantObject", new CoreObject { m_templateID = 1 });
            for (var slot = 0; slot < enemies; slot++) CombatRegressionTests.SetProperty(rejected.SubCircles[slot], "ParticipantObject", new CoreObject { m_templateID = 2 });
            rejected.OnCreatureMove(creature, aiActor, entity);
            var probe = new Probe(); aiActor.Tell(probe);
            var state = await probe.Result.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(state.InDuel); Assert.False(state.Killed); Assert.Same(admitted, state.Duel);
            Assert.False(output.Reader.TryRead(out _));
            Assert.Same(creature, admitted.SubCircles[0].ParticipantObject);
            Assert.True(admitted.SubCircles[0].AddedToDuel);
        } finally { await system.Terminate(); ClassicRuntime.ResetForTests(); }
    }

    [Fact]
    public async Task RoamingRejectionChecksObjectIdentityThenBroadcastsActualDeletion() {
        Configure();
        using var system = ActorSystem.Create("roaming-rejection-" + Guid.NewGuid().ToString("N"));
        try {
            var output = Channel.CreateUnbounded<object>();
            var zoneActor = system.ActorOf(Props.Create(() => new Recorder(output)));
            var entity = Entity((Zone)RuntimeHelpers.GetUninitializedObject(typeof(Zone)));
            var creature = new CoreObject { m_globalID = 123 };
            CombatRegressionTests.SetProperty(entity, "ActiveGameObject", creature);
            CombatRegressionTests.SetProperty(entity, "ZoneRef", zoneActor);
            var aiActor = system.ActorOf(Props.Create(() => new AiDriver(entity)));
            // Even an equal global ID is insufficient: a delayed message cannot kill a replacement object.
            aiActor.Tell(new COMBAT_106_PROTOCOL.MSG_REJECTEDROAMINGCREATURE { ExpectedCreature = new CoreObject { m_globalID = 123 } });
            var probe = new Probe(); aiActor.Tell(probe);
            var state = await probe.Result.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(state.InDuel); Assert.False(state.Killed); Assert.False(output.Reader.TryRead(out _));
            aiActor.Tell(new COMBAT_106_PROTOCOL.MSG_REJECTEDROAMINGCREATURE { ExpectedCreature = creature });
            var broadcast = Assert.IsType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(await output.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            var deleted = Assert.IsType<GAME_5_PROTOCOL.MSG_DELETEOBJECT>(broadcast.Message);
            Assert.Equal(123UL, deleted.GameObjectID);
        } finally { await system.Terminate(); }
    }

    private static void Configure() {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, "[Logging]\nLogLevel=FATAL\n"); ConfigurationManager.Initialize(path); }
        finally { File.Delete(path); }
    }
    private sealed class Probe {
        internal TaskCompletionSource<(bool InDuel, bool Killed, CombatDuelComponent Duel)> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    // Actor mailbox routes the actual component handlers; deletion uses the actual ZoneEntity broadcast path.
    private sealed class AiDriver : ReceiveActor {
        public AiDriver(ZoneEntity entity) : this(entity, TimeSpan.FromMinutes(1)) { }

        // CLASSIC: the creature's age (RoamingRejection): one that walked in is old, one that just spawned is not.
        public AiDriver(ZoneEntity entity, TimeSpan age) {
            var ai = new CombatCreatureAIComponent(entity);
            typeof(CombatCreatureAIComponent).GetField("_bornUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ai, DateTime.UtcNow - age);
            Receive<COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL>(m => CombatRegressionTests.Invoke(ai, "ReceiveCombatAdded", m));
            Receive<COMBAT_106_PROTOCOL.MSG_REJECTEDROAMINGCREATURE>(m => CombatRegressionTests.Invoke(ai, "ReceiveRoamingRejection", m));
            Receive<Probe>(p => p.Result.SetResult((
                (bool)Field(ai, "_isInDuel")!, (bool)Field(ai, "_sentFinalKill")!, (CombatDuelComponent)Field(ai, "_currentDuelComponent")!)));
        }
        private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    }

    private static ZoneEntity Entity(Zone zone) {
        var entity = (ZoneEntity)RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
        CombatRegressionTests.SetProperty(entity, "Zone", zone);
        typeof(ZoneEntity).GetField("Components", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entity, new Dictionary<ZoneEntityComponent,IActorRef>());
        return entity;
    }
    private sealed class Recorder : ReceiveActor {
        public Recorder(Channel<object> channel) { Receive<object>(message => channel.Writer.TryWrite(message)); }
    }
}
