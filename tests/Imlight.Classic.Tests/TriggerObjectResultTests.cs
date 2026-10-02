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
 * CLASSIC TRIGGER OBJECT RESULTS
 * ========================================================================
 *
 * PURPOSE:
 * Decodes classic object state changes and their fractional-second waits.
 *
 * USAGE EXAMPLE:
 * Registered by ClassicZoneTypeRegistry for classic zone data.
 *
 * NOTE:
 * Unknown nonempty state-change fields are not executable.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Cinematics;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Newtonsoft.Json;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class TriggerObjectResultTests : IDisposable {
    private const string Lair = "DragonSpire-DS_A3_Kings-Interiors-DS_MalistaireLair";
    private const string Burial = "MooShu-MS_Death-MS_Death_Zone1_BurialGround";
    private const string Tree = "MooShu-MS_Death-MS_Death_Zone3_AncientTree";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public TriggerObjectResultTests() {
        EquipmentAttachConcurrencyTests.Configure();
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Fact]
    public void FieldHashesConfirmStringsAndDoubleRatherThanBoolAndUInt() {
        Assert.Equal(ClassicResModifyTriggerObject.NameHash, KingsIsleHash.Property("m_triggerObjName", "std::string"));
        Assert.Equal(ClassicResModifyTriggerObject.StateHash, KingsIsleHash.Property("m_triggerObjState", "std::string"));
        Assert.Equal(ClassicResWait.SecondsHash, KingsIsleHash.Property("m_secondsToWait", "double"));
    }

    [Theory]
    [InlineData(Lair, "TeleportToAmbrose", 1)]
    [InlineData(Burial, "MS_SpiritWorldPortal_Death1 instance", 2)]
    [InlineData(Tree, "MS_SpiritWorldPortal instance", 1)]
    public void ProductionZoneLoaderDecodesPortalStateAndRegistersHandler(string zone, string name, int count) {
        var results = Load(zone).m_triggers.OfType<Trigger>().SelectMany(t => t.m_results?.m_results ?? [])
            .OfType<ClassicResModifyTriggerObject>().Where(r => r.ObjectName == name).ToArray();
        Assert.Equal(count, results.Length);
        Assert.Equal("On", results[0].State);
        if (count == 2) Assert.Equal("Off", results[1].State);
        foreach (var result in results) {
            Assert.True(result.CanExecute);
            Assert.Equal("", result.UnknownState);
            var context = new GenericResultContext(new ResultList { m_results = [result] }, null, null);
            Assert.NotNull(ResultDispatcher.FindHandlerForResult(result.GetType(), context));
        }
        if (zone == Burial) {
            var candle = Load(zone).m_triggers.Single(t => t?.m_triggerName == "CandleCheckTrigger");
            Assert.NotNull(candle);
            Assert.Equal(30, Assert.Single(candle.m_results.m_results.OfType<ClassicResWait>()).Seconds);
        }
    }

    [Fact]
    public void AllExtractedStateChangesDecodeAndOnlyNonemptyUnknownFieldsAreUnsupported() {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "extract", "r806919", "zones");
        if (!Directory.Exists(root)) Assert.Skip("private r806919 zone extraction is required");
        var changes = new List<ClassicResModifyTriggerObject>();
        foreach (var file in Directory.EnumerateFiles(root, "triggers.xml", SearchOption.AllDirectories)) {
            changes.AddRange(Load(Path.GetFileName(Path.GetDirectoryName(file))!).m_triggers
                .OfType<Trigger>().SelectMany(t => t.m_results?.m_results ?? []).OfType<ClassicResModifyTriggerObject>());
        }
        Assert.Equal(685, changes.Count);
        Assert.Equal(683, changes.Count(change => change.CanExecute));
        Assert.All(changes.Where(change => !change.CanExecute), change => {
            Assert.Equal("Shared-TeleporterArchEffects instance", change.ObjectName);
            Assert.Equal("ON", change.State);
            Assert.Equal("Off", change.UnknownState);
        });
    }

    [Fact]
    public void StockZoneLoaderKeepsStockRegistry() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicRules.Stock);
        var results = Load(Lair).m_triggers.OfType<Trigger>().SelectMany(t => t.m_results?.m_results ?? []).ToArray();
        Assert.DoesNotContain(results, r => r is ClassicResModifyTriggerObject or ClassicResWait);
        Assert.Contains(results, r => r is ResModifyTriggerObject);
    }

    [Theory]
    [InlineData(Lair, "TeleportToAmbrose")]
    // The Burial Grounds portal opens on its quest entry alone (classic-data 9250ec3: the server cannot light its
    // candles), so it has no RequiresState and is not in this case list.
    [InlineData(Tree, "MS_SpiritWorldPortal instance")]
    public async Task RealResultDispatchOpensAndClosesOnlyNamedPortalAndReplaysState(string zone, string name) {
        using var system = ActorSystem.Create("portal-result", "akka.actor.provider = local");
        var packets = Channel.CreateUnbounded<object>();
        try {
            var portalTemplate = new GameObjectTemplate {
                m_objectName = "DS_Teleport_AmbrosePlace",
                m_behaviors = [new AnimationBehaviorTemplate()],
            };
            Assert.True(RenderComponent.ShouldAttachToEntity(portalTemplate));
            Assert.True(InteractTeleportObjectComponent.ShouldAttachToEntity(portalTemplate));
            var entry = JsonConvert.DeserializeObject<WizardZoneData>(File.ReadAllText(Path.Combine(
                ClassicDataFixture.Root, "spiraldb-overlay", "ZoneTransfer", zone + ".travel.json")), new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto })!.Teleports.Single(t => t.TriggerName == name);
            var router = system.ActorOf(Props.Create(() => new ResultRouter(packets, name, entry)));
            var entities = await router.Ask<IActorRef[]>(new GetEntities(), Timeout, TestContext.Current.CancellationToken);
            var player = system.ActorOf(Props.Create(() => new PacketRecorder(packets)));
            var wizard = new Wizard { CharId = 7, QuestBehavior = new ServerQuestBehavior() };
            var initial = await entities[0].Ask<PortalSnapshot>(new InspectPortal(wizard), Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, initial.Options);
            var open = Load(zone).m_triggers.OfType<Trigger>().SelectMany(t => t.m_results?.m_results ?? [])
                .OfType<ClassicResModifyTriggerObject>().First(r => r.ObjectName == name && r.State == "On");
            var completed = await router.Ask<CHARACTER_103_PROTOCOL.MSG_RESULTEXECUTED>(new RunResults(new ResultList { m_results = [open] }), Timeout, TestContext.Current.CancellationToken);
            Assert.True(completed.Success);
            var state = Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await Next(packets));
            Assert.Equal(900ul, state.GameObjectID);
            Assert.Equal(StringHash.Compute("On"), state.State);
            if (entry.Teleport.m_requirements?.m_requirements is { Count: > 0 } requirements) {
                Assert.Equal(0, (await entities[0].Ask<PortalSnapshot>(new InspectPortal(wizard), Timeout, TestContext.Current.CancellationToken)).Options);
                var required = Assert.IsType<ReqHasEntry>(Assert.Single(requirements));
                wizard.QuestBehavior.SetRegistryValue(required.m_entryName, 1);
            }
            Assert.Equal(1, (await entities[0].Ask<PortalSnapshot>(new InspectPortal(wizard), Timeout, TestContext.Current.CancellationToken)).Options);
            Assert.Null((await entities[1].Ask<PortalSnapshot>(new InspectPortal(wizard), Timeout, TestContext.Current.CancellationToken)).State);
            entities[0].Tell(new UsePortal(player, wizard));
            var transfer = Assert.IsType<ZONE_102_PROTOCOL.MSG_ZONETRANSFER>(await Next(packets));
            Assert.Equal(entry.Teleport.m_destinationZone, transfer.DestinationZone);
            entities[0].Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYER { PlayerActor = player, PlayerObject = wizard.GameObject, Wizard = wizard });
            Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(await Next(packets));
            Assert.Equal(StringHash.Compute("On"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await Next(packets)).State);
            var close = new ClassicResModifyTriggerObject { ObjectName = name, State = "Off" };
            Assert.True((await router.Ask<CHARACTER_103_PROTOCOL.MSG_RESULTEXECUTED>(new RunResults(new ResultList { m_results = [close] }), Timeout, TestContext.Current.CancellationToken)).Success);
            Assert.Equal(StringHash.Compute("Off"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await Next(packets)).State);
            Assert.Equal(0, (await entities[0].Ask<PortalSnapshot>(new InspectPortal(wizard), Timeout, TestContext.Current.CancellationToken)).Options);
            entities[0].Tell(new UsePortal(player, wizard));
            await entities[0].Ask<PortalSnapshot>(new InspectPortal(wizard), Timeout, TestContext.Current.CancellationToken);
            await player.Ask<bool>(new Barrier(), Timeout, TestContext.Current.CancellationToken);
            Assert.False(packets.Reader.TryRead(out _));
        } finally {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData(true, "", "On", "")]
    [InlineData(true, "TeleportToAmbrose", "", "")]
    [InlineData(true, "TeleportToAmbrose", "On", "Off")]
    [InlineData(false, "TeleportToAmbrose", "On", "")]
    public async Task UnsupportedOrStockResultsFailWithoutSendingState(bool classic, string name, string state, string extra) {
        if (!classic) {
            ClassicRuntime.ResetForTests();
            ClassicRuntime.Initialize(ClassicRules.Stock);
        }
        using var system = ActorSystem.Create("portal-rejection", "akka.actor.provider = local");
        var packets = Channel.CreateUnbounded<object>();
        try {
            var router = system.ActorOf(Props.Create(() => new ResultRouter(packets, "portal", new WizardTeleportData())));
            var result = new ClassicResModifyTriggerObject { ObjectName = name, State = state, UnknownState = extra };
            var response = await router.Ask<CHARACTER_103_PROTOCOL.MSG_RESULTEXECUTED>(new RunResults(new ResultList { m_results = [result] }), Timeout, TestContext.Current.CancellationToken);
            Assert.False(response.Success);
            Assert.False(packets.Reader.TryRead(out _));
        } finally {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task ProductionExecutorPreservesFractionalWaitBetweenPortalStates() {
        using var system = ActorSystem.Create("portal-wait", "akka.actor.provider = local");
        var packets = Channel.CreateUnbounded<object>();
        try {
            var router = system.ActorOf(Props.Create(() => new ResultRouter(packets, "portal", new WizardTeleportData())));
            var wait = Load("Marleybone-MB_Station-Interiors-MB_ChelseaCourt_LD").m_triggers
                .OfType<Trigger>().SelectMany(t => t.m_results?.m_results ?? [])
                .OfType<ClassicResWait>().First(result => result.Seconds == 0.1);
            var sequence = new ResultList { m_results = [
                new ClassicResModifyTriggerObject { ObjectName = "portal", State = "On" },
                wait,
                new ClassicResModifyTriggerObject { ObjectName = "portal", State = "Off" },
            ] };
            var completion = router.Ask<CHARACTER_103_PROTOCOL.MSG_RESULTEXECUTED>(new RunResults(sequence), Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(StringHash.Compute("On"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await Next(packets)).State);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(StringHash.Compute("Off"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await Next(packets)).State);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(60));
            Assert.True((await completion).Success);
        } finally {
            await system.Terminate();
        }
    }

    private static WizZoneTriggers Load(string zone) {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "extract", "r806919", "zones", zone, "triggers.xml");
        if (!File.Exists(path)) Assert.Skip("private r806919 zone extraction is required");
        var loader = typeof(ZoneEntity).Assembly.GetType("Imlight.CoreLib.Game.Zone.Core.ZoneLoader", true)!;
        return (WizZoneTriggers) loader.GetMethod("Deserialize", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(WizZoneTriggers)).Invoke(null, [File.ReadAllBytes(path), "triggers", "triggers.xml"])!;
    }

    private static Task<object> Next(Channel<object> packets)
        => packets.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

    private sealed record RunResults(ResultList Results);
    private sealed record GetEntities;
    private sealed record Barrier;
    private sealed record InspectPortal(Wizard Wizard);
    private sealed record UsePortal(IActorRef Player, Wizard Wizard);
    private sealed record PortalSnapshot(string? State, int Options);

    private sealed class ResultRouter : ReceiveActor {
        public ResultRouter(Channel<object> packets, string name, WizardTeleportData entry) {
            var portal = Context.ActorOf(Props.Create(() => new PortalEntity(Self, name, entry, 900)));
            var other = Context.ActorOf(Props.Create(() => new PortalEntity(Self, name + "-other", entry, 901)));
            Receive<GetEntities>(_ => Sender.Tell(new[] { portal, other }));
            Receive<RunResults>(run => ResultDispatcher.ExecuteResults(Context, run.Results, ActorRefs.Nobody, null, Sender, Self));
            Receive<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(message => {
                if (message.Messages != null) {
                    Assert.Equal(ZoneBroadcastTarget.Objects, message.Targets);
                    foreach (var part in message.Messages) {
                        portal.Tell(part);
                        other.Tell(part);
                    }
                }
                if (message.Message != null) packets.Writer.TryWrite(message.Message);
            });
        }
    }

    private sealed class PortalEntity : ZoneEntity {
        public PortalEntity(IActorRef zone, string name, WizardTeleportData entry, ulong id)
            : base(new WizClientObject { m_globalID = id }, new GameObjectTemplate { m_objectName = "portal", m_behaviors = [] },
                   new CoreObjectInfo { m_zoneTag = name, m_startState = "Off" }, zone, null) {
            AddComponent(typeof(RenderComponent));
            AddComponent(typeof(InteractTeleportObjectComponent));
            var teleport = GetComponentOfType<InteractTeleportObjectComponent>();
            typeof(InteractTeleportObjectComponent).GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(teleport, entry);
            typeof(InteractTeleportObjectComponent).GetField("_resolved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(teleport, true);
        }
        protected override void ConfigureReceivers() {
            Receive<InspectPortal>(query => Sender.Tell(new PortalSnapshot(TriggerObjectState,
                GetComponentOfType<InteractTeleportObjectComponent>().GetServiceOptions(query.Wizard).Count())));
            Receive<UsePortal>(request => GetComponentOfType<InteractTeleportObjectComponent>()
                .OnServiceInteraction(request.Player, request.Wizard, request.Wizard.GameObject, 0));
            base.ConfigureReceivers();
        }
    }

    private sealed class PacketRecorder : ReceiveActor {
        public PacketRecorder(Channel<object> packets) {
            Receive<Barrier>(_ => Sender.Tell(true));
            Receive<object>(message => packets.Writer.TryWrite(message));
        }
    }
}
