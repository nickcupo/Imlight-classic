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
 * ZONE MARKER LIFECYCLE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Exercise production zone departure, movement dispatch, and marker delivery after re-entry.
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~ZoneMarkerLifecycleTests
 *
 * NOTE:
 * Local actors use synthetic zone data and players; no database or network server is started.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ZoneMarkerLifecycleTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly object? _originalPriority;
    private readonly object? _originalOrder;

    public ZoneMarkerLifecycleTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicRules.Stock);
        _originalPriority = PriorityField("s_wizBangPriority").GetValue(null);
        _originalOrder = PriorityField("s_wizBangList").GetValue(null);
        PriorityField("s_wizBangPriority").SetValue(null, new WizBangPriorityTemplate());
        PriorityField("s_wizBangList").SetValue(null, new List<WizBangs> { WizBangs.StartQuest });
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-marker-tests.log")}\n");
            ConfigurationManager.Initialize(path);
        } finally {
            File.Delete(path);
        }
    }

    public void Dispose() {
        PriorityField("s_wizBangPriority").SetValue(null, _originalPriority);
        PriorityField("s_wizBangList").SetValue(null, _originalOrder);
        ClassicRuntime.ResetForTests();
    }

    private static FieldInfo PriorityField(string name)
        => typeof(WizBangPriority).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public async Task LastPlayerRemovalPurgesQueuedMovesAndRejectsOldSessionsAfterMobileIdReuse() {
        using var system = ActorSystem.Create("zone-marker-lifecycle", "akka.actor.provider = local");
        try {
            var objects = system.ActorOf(Props.Create(() => new BroadcastRecorder()));
            var players = system.ActorOf(Props.Create(() => new BroadcastRecorder()));
            var first = system.ActorOf(Props.Create(() => new BroadcastRecorder()));
            var returning = system.ActorOf(Props.Create(() => new BroadcastRecorder()));
            var zone = system.ActorOf(Props.Create(() => new ZoneHarness(objects, players)));
            zone.Tell(new ExerciseLifecycle(first, returning));
            var snapshot = await zone.Ask<ZoneSnapshot>(new ReadMessages(), Timeout, TestContext.Current.CancellationToken);
            var objectMessages = await objects.Ask<IServerMessage[]>(new ReadMessages(), Timeout, TestContext.Current.CancellationToken);
            var playerMessages = await players.Ask<IServerMessage[]>(new ReadMessages(), Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, snapshot.PendingAfterDeparture);
            Assert.True(snapshot.MobileIdReused);
            Assert.Equal(1, snapshot.ActivePlayers);
            Assert.Equal(1, snapshot.PendingAfterReentry);
            Assert.Equal(returning, Assert.Single(objectMessages.OfType<ZONE_102_PROTOCOL.MSG_PLAYERMOVE>()).PlayerActor);
            Assert.Equal(first, Assert.Single(playerMessages.OfType<ZONE_102_PROTOCOL.MSG_REMOVEPLAYER>()).PlayerActor);
            Assert.Equal(2, playerMessages.OfType<ZONE_102_PROTOCOL.MSG_ADDPLAYER>().Count());
        } finally {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData(true, WizBangs.StartQuest)]
    [InlineData(false, WizBangs.None)]
    public async Task RealMementoResumesMarkersAfterEmptyRangeAndResendsOncePerExplicitTick(bool hasOptions, WizBangs expectedMarker) {
        using var system = ActorSystem.Create("memento-marker-lifecycle", "akka.actor.provider = local");
        try {
            var entity = system.ActorOf(Props.Create(() => new MarkerEntity(hasOptions)));
            var ready = await entity.Ask<MarkerSnapshot>(new InspectMarker(), Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, ready.TimerCount);
            await entity.Ask<MarkerSnapshot>(new StopAutomaticTicks(), Timeout, TestContext.Current.CancellationToken);

            for (var cycle = 0; cycle < 3; cycle++) {
                var received = Channel.CreateUnbounded<GAME_5_PROTOCOL.MSG_WIZBANG>();
                var player = system.ActorOf(Props.Create(() => new MarkerPlayer(received)), $"session-{cycle}");
                entity.Tell(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE {
                    PlayerActor = player, PlayerObject = new CoreObject {
                        m_globalID = 77UL, m_location = new Vector3(1000, 0, 0),
                    },
                });
                var inRange = await entity.Ask<MarkerSnapshot>(new InspectMarker(), Timeout, TestContext.Current.CancellationToken);
                Assert.Equal(1, inRange.PlayersInRange);

                for (var tick = 0; tick < 2; tick++) {
                    entity.Tell(new ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL());
                    var marker = await received.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    Assert.Equal(900UL, marker.GameObjectID);
                    Assert.Equal((uint) expectedMarker, marker.WizBangID);
                }
                Assert.Equal(2, await player.Ask<int>(new QueryCount(), Timeout, TestContext.Current.CancellationToken));
                Assert.False(received.Reader.TryRead(out _));

                entity.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER { PlayerActor = player, GlobalId = 77UL });
                entity.Tell(new ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL());
                var empty = await entity.Ask<MarkerSnapshot>(new InspectMarker(), Timeout, TestContext.Current.CancellationToken);
                Assert.Equal(0, empty.PlayersInRange);
                Assert.Equal(2, await player.Ask<int>(new QueryCount(), Timeout, TestContext.Current.CancellationToken));
                Assert.False(received.Reader.TryRead(out _));
                system.Stop(player);
            }
        } finally {
            await system.Terminate();
        }
    }

    private sealed record ExerciseLifecycle(IActorRef First, IActorRef Returning);
    private sealed record ZoneSnapshot(int PendingAfterDeparture, bool MobileIdReused, int ActivePlayers, int PendingAfterReentry);
    private sealed record ReadMessages;
    private sealed record InspectMarker;
    private sealed record StopAutomaticTicks;
    private sealed record MarkerSnapshot(int TimerCount, int PlayersInRange);
    private sealed record QueryCount;

    private sealed class BroadcastRecorder : ReceiveActor {
        public BroadcastRecorder() {
            var messages = new List<IServerMessage>();
            Receive<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(message => {
                if (message.Messages is not null) messages.AddRange(message.Messages);
            });
            Receive<ReadMessages>(_ => Sender.Tell(messages.ToArray()));
            ReceiveAny(_ => { });
        }
    }

    private sealed class ZoneHarness : Zone {
        private ZoneSnapshot? _snapshot;

        public ZoneHarness(IActorRef objects, IActorRef players) : base("WizardCity/WC_Hub", 1) {
            // Supply already-loaded supervisors; exercise Zone's own message handlers and routing.
            Timers.CancelAll();
            SetField(typeof(Zone), this, "_isLoading", false);
            SetField(typeof(Zone), this, "_objectSupervisor", objects);
            SetField(typeof(Zone), this, "_playerSupervisor", players);
            foreach (var name in new[] { "_volumeSupervisor", "_triggerSupervisor", "_pathSupervisor", "_sigilSupervisor" }) {
                SetField(typeof(Zone), this, name, ActorRefs.Nobody);
            }
        }

        [MessageHandler(typeof(ExerciseLifecycle))]
        private void Exercise(ExerciseLifecycle request) {
            var mobileId = ReserveMobileId();
            var firstObject = new CoreObject { m_globalID = 77UL, m_nMobileID = mobileId };
            ReceiveAddPlayer(new ZONE_102_PROTOCOL.MSG_ADDPLAYER { PlayerActor = request.First, PlayerObject = firstObject });
            ReceivePlayerMove(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE { PlayerActor = request.First, PlayerObject = firstObject });
            var remove = new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER { PlayerActor = request.First, GlobalId = 77UL, MobileId = mobileId };
            ReceiveRemovePlayer(remove);
            var pending = Field<List<ZONE_102_PROTOCOL.MSG_PLAYERMOVE>>(typeof(Zone), this, "_pendingPlayerMoves");
            var pendingAfterDeparture = pending.Count;
            Dispatch(new ZONE_102_PROTOCOL.MSG_FLUSHMOVES());
            Timers.CancelAll();
            Dispatch(new ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID { MobileId = mobileId });
            var reused = ReserveMobileId();
            var returningObject = new CoreObject { m_globalID = 77UL, m_nMobileID = reused };
            ReceiveAddPlayer(new ZONE_102_PROTOCOL.MSG_ADDPLAYER { PlayerActor = request.Returning, PlayerObject = returningObject });
            ReceiveRemovePlayer(remove);
            ReceivePlayerMove(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE { PlayerActor = request.First, PlayerObject = firstObject });
            ReceivePlayerMove(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE { PlayerActor = request.Returning, PlayerObject = returningObject });
            var snapshot = new ZoneSnapshot(pendingAfterDeparture, reused == mobileId,
                Field<HashSet<IActorRef>>(typeof(Zone), this, "_players").Count, pending.Count);
            Dispatch(new ZONE_102_PROTOCOL.MSG_FLUSHMOVES());
            _snapshot = snapshot;
        }

        [MessageHandler(typeof(ReadMessages))]
        private void ReadSnapshot(ReadMessages _) => Sender.Tell(_snapshot!);

        private void Dispatch(object message)
            => MessageHandlerTable.DispatcherFor(typeof(Zone), message.GetType())!(this, message);
    }

    private sealed class MarkerEntity : ZoneEntity {
        public MarkerEntity(bool hasOptions) : base(new CoreObject { m_globalID = 900UL },
            new GameObjectTemplate { m_displayName = "Test NPC" }, null!, ActorRefs.Nobody, null!) {
            AddComponent(typeof(MarkerService));
            GetComponentOfType<MarkerService>().HasOptions = hasOptions;
            AddComponent(typeof(InteractServiceMementoComponent));
            GetComponentOfType<InteractServiceMementoComponent>().OnStart();
        }

        [MessageHandler(typeof(InspectMarker))]
        private void Inspect(InspectMarker _) => Sender.Tell(Snapshot());

        [MessageHandler(typeof(StopAutomaticTicks))]
        private void StopTicks(StopAutomaticTicks _) {
            Timers.CancelAll();
            Sender.Tell(Snapshot());
        }

        private MarkerSnapshot Snapshot() {
            var component = GetComponentOfType<InteractServiceMementoComponent>();
            return new MarkerSnapshot(component.Timers.ActiveTimers.Count,
                Field<PlayersInRange>(typeof(InteractServiceMementoComponent), component, "_playersInRenderRange").Count);
        }
    }

    private sealed class MarkerPlayer : ReceiveActor {
        public MarkerPlayer(Channel<GAME_5_PROTOCOL.MSG_WIZBANG> received) {
            var queries = 0;
            Receive<CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD>(_ => {
                queries++;
                Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER { Wizard = new Wizard() });
            });
            Receive<GAME_5_PROTOCOL.MSG_WIZBANG>(marker => received.Writer.TryWrite(marker));
            Receive<QueryCount>(_ => Sender.Tell(queries));
        }
    }

    private sealed class MarkerService(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent {
        public bool HasOptions { get; set; }
        public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
            => HasOptions ? [new InteractableOption()] : [];
        public void OnServiceInteraction(IActorRef actor, Wizard wizard, CoreObject obj, uint index) { }
        public string ServiceName => "Test";
        public string NpcIcon => "";
        public string NpcNameKey => "";
        public string NpcTextKey => "";
        public WizBangs WizBang => WizBangs.StartQuest;
        public string StateName => "";
        public string InteractWizBang => "";
        public string DisplayKey => "";
    }

    private static T Field<T>(Type type, object instance, string name)
        => (T) type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void SetField(Type type, object instance, string name, object value)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
