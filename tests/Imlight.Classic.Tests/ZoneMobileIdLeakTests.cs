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
 * ZONE MOBILE ID LEAK TESTS
 * ========================================================================
 *
 * PURPOSE:
 * A zone object (a mob, a combat minion, a summoned pet) gives its reserved mobile id back when its actor stops.
 * Live Unicorn Way ran out of all 3276 reserved ids after ~12 h of ambient wizards fighting (2026-10-09) and
 * stopped spawning: "Failed to generate a reserved mobile ID - all IDs in use."
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~ZoneMobileIdLeakTests
 *
 * NOTE:
 * Real Zone and ZoneEntity actors; synthetic objects with no components; no database or network server.
 *
 * Created by: Nick with Claude
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ZoneMobileIdLeakTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public ZoneMobileIdLeakTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicRules.Stock);
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-mobileid-tests.log")}\n");
            ConfigurationManager.Initialize(path);
        } finally {
            File.Delete(path);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Fact]
    public async Task SpawnDespawnCyclesPastThePoolSizeNeverRunOutAndGiveEveryIdBack() {
        using var system = ActorSystem.Create("zone-mobileid-cycles", "akka.actor.provider = local");
        try {
            var (zoneRef, zone) = await StartZone(system);
            var poolSize = Zone.ReservedMobileIdMax;
            const int perRound = 400;
            var rounds = poolSize / perRound + 4; // well past the 3276 reserved ids
            HashSet<ushort> previous = [];
            var globalId = 1_000_000UL;

            for (var round = 0; round < rounds; round++) {
                var objects = new List<CoreObject>();
                var actors = new List<IActorRef>();
                for (var i = 0; i < perRound; i++) {
                    var obj = new CoreObject { m_globalID = globalId++ };
                    objects.Add(obj);
                    actors.Add(system.ActorOf(Props.Create(() => new BareEntity(obj, zoneRef, zone))));
                }

                await Task.WhenAll(actors.Select(a => a.Ask<ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS>(
                    new ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN(), Timeout, TestContext.Current.CancellationToken)));

                var ids = objects.Select(o => o.m_nMobileID).ToList();
                Assert.DoesNotContain((ushort) 0, ids); // 0 is "all IDs in use"
                Assert.Equal(perRound, ids.Distinct().Count());
                Assert.All(ids, id => Assert.InRange(id, (ushort) 1, poolSize));
                Assert.Equal(perRound, zone.ReservedMobileIdOwners);
                if (round == 1) {
                    // With free ids left, an id given back is not handed out again at once (the client's DELETEOBJECT
                    // of the old object must land first).
                    Assert.Empty(previous.Intersect(ids));
                }

                previous = [.. ids];
                await Task.WhenAll(actors.Select(a => a.GracefulStop(Timeout)));
                Assert.Equal(0, zone.ReservedMobileIdOwners);
            }

            // After the cooldown every id is free again.
            await Task.Delay(TimeSpan.FromSeconds(2.3), TestContext.Current.CancellationToken);
            Assert.Equal(0, zone.ReservedMobileIdsInUse);
        } finally {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AnEntityThatRestartsKeepsItsIdAndGivesItBackOnlyWhenItStops() {
        using var system = ActorSystem.Create("zone-mobileid-restart", "akka.actor.provider = local");
        try {
            var (zoneRef, zone) = await StartZone(system);
            var obj = new CoreObject { m_globalID = 42UL };
            var entity = system.ActorOf(Props.Create(() => new BareEntity(obj, zoneRef, zone)));
            await entity.Ask<ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS>(new ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN(),
                Timeout, TestContext.Current.CancellationToken);
            var id = obj.m_nMobileID;
            Assert.NotEqual((ushort) 0, id);

            // A second load of the same actor keeps its one id.
            await entity.Ask<ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS>(new ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN(),
                Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(id, obj.m_nMobileID);
            Assert.Equal(1, zone.ReservedMobileIdsInUse);

            entity.Tell(new Boom()); // a handler fault: the default supervisor restarts the actor
            Assert.Equal("alive", await entity.Ask<string>(new Ping(), Timeout, TestContext.Current.CancellationToken));
            Assert.Equal(1, zone.ReservedMobileIdOwners);
            Assert.Equal(1, zone.ReservedMobileIdsInUse);

            await entity.GracefulStop(Timeout);
            Assert.Equal(0, zone.ReservedMobileIdOwners);
            await Task.Delay(TimeSpan.FromSeconds(2.3), TestContext.Current.CancellationToken);
            Assert.Equal(0, zone.ReservedMobileIdsInUse);
        } finally {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task ZoneTransfersNobodyAddsGiveTheirIdsBackAndAPlayerIsReleasedByTheIdItWasAddedWith() {
        using var system = ActorSystem.Create("zone-mobileid-transfers", "akka.actor.provider = local");
        var window = Zone.TransferClaimWindow;
        Zone.TransferClaimWindow = TimeSpan.FromMilliseconds(300);
        try {
            var (zoneRef, zone) = await StartZone(system);
            var ct = TestContext.Current.CancellationToken;

            // A same-zone teleport, a session that left mid-transfer, a companion whose transfer timed out on its side:
            // a transfer answer nobody follows with an ADDPLAYER.
            for (var i = 0; i < 200; i++) {
                var rsp = await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(Transfer(), Timeout, ct);
                Assert.Equal(0u, rsp.ErrorCode);
            }
            Assert.Equal(200, zone.PlayerMobileIdsInUse);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            Assert.Equal(0, zone.PlayerMobileIdsInUse);

            // Two players added: claimed ids outlive the window.
            var first = system.ActorOf(Props.Create(() => new Sink()));
            var second = system.ActorOf(Props.Create(() => new Sink()));
            var firstId = (await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(Transfer(), Timeout, ct)).MobileId;
            var secondId = (await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(Transfer(), Timeout, ct)).MobileId;
            await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(Add(first, 1UL, firstId), Timeout, ct);
            await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(Add(second, 2UL, secondId), Timeout, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            Assert.Equal(2, zone.PlayerMobileIdsInUse);

            // The first leaves with a REMOVEPLAYER that names the second's id (its object already carries its next
            // zone's id): its own id is given back, the second keeps its id.
            await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP>(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
                PlayerActor = first, GlobalId = 1UL, MobileId = secondId,
            }, Timeout, ct);
            await Task.Delay(TimeSpan.FromSeconds(2.3), ct);
            Assert.Equal(1, zone.PlayerMobileIdsInUse);
            var next = (await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(Transfer(), Timeout, ct)).MobileId;
            Assert.NotEqual(secondId, next);
        } finally {
            Zone.TransferClaimWindow = window;
            await system.Terminate();
        }
    }

    private static ZONE_102_PROTOCOL.MSG_ZONETRANSFER Transfer() => new() {
        DestinationZone = "WizardCity/WC_Streets/WC_Unicorn", DestinationLocation = "1,2,3,0",
    };

    private static ZONE_102_PROTOCOL.MSG_ADDPLAYER Add(IActorRef player, ulong globalId, ushort mobileId) => new() {
        PlayerActor = player, PlayerObject = new CoreObject { m_globalID = globalId, m_nMobileID = mobileId },
    };

    private sealed class Sink : ReceiveActor {
        public Sink() => ReceiveAny(_ => { });
    }

    private static async Task<(IActorRef, Zone)> StartZone(ActorSystem system) {
        var zoneRef = system.ActorOf(Props.Create(() => new ZoneHarness()));
        var box = await zoneRef.Ask<ZoneBox>(new GetZone(), Timeout, TestContext.Current.CancellationToken);

        return (zoneRef, box.Zone);
    }

    private sealed record GetZone;
    private sealed record ZoneBox(Zone Zone);
    private sealed record Boom;
    private sealed record Ping;

    private sealed class ZoneHarness : Zone {
        public ZoneHarness() : base("WizardCity/WC_Streets/WC_Unicorn", 1) {
            Timers.CancelAll();
            SetField(typeof(Zone), this, "_isLoading", false);
            foreach (var name in new[] { "_objectSupervisor", "_playerSupervisor", "_volumeSupervisor", "_triggerSupervisor",
                         "_pathSupervisor", "_sigilSupervisor" }) {
                SetField(typeof(Zone), this, name, ActorRefs.Nobody);
            }
        }

        [MessageHandler(typeof(GetZone))]
        private void Get(GetZone _) => Sender.Tell(new ZoneBox(this));

        // Zone's own handler is private, so the table of this subclass does not see it (production zones are Zone).
        [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID))]
        private void Release(ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID message)
            => MessageHandlerTable.DispatcherFor(typeof(Zone), message.GetType())!(this, message);
    }

    // A zone object with no components: only the load handshake and the mobile id.
    private sealed class BareEntity(CoreObject obj, IActorRef zoneRef, Zone zone) : ZoneEntity(obj, null!, null!, zoneRef, zone) {
        protected override void AutoAttachComponents() { }

        [MessageHandler(typeof(Boom))]
        private void Fail(Boom _) => throw new InvalidOperationException("test fault");

        [MessageHandler(typeof(Ping))]
        private void Answer(Ping _) => Sender.Tell("alive");
    }

    private static void SetField(System.Type type, object instance, string name, object value)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
