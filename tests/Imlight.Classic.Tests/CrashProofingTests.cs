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
 * CRASH PROOFING TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Crash and hang review of the live server (2026-10-28): each test pins one failure that could take down live play
 * (the process, a zone or the game world) or leak for good, found by reading the code, not reported in game.
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~CrashProofingTests
 *
 * NOTE:
 * Real actors in a local ActorSystem; no database or network server.
 *
 * Created by: Nick with Claude Code
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Login.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CrashProofingTests {

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An exception in an async void method is rethrown on the thread pool and ends the process (.NET has no way to
    /// catch it there). AttachService.AddPlayerToOnlineCollection was one: a RavenDB error while it stored the online
    /// row (the embedded database restarting, a timeout) took the server down for everyone.
    /// </summary>
    [Fact]
    public void NoServerMethodIsAsyncVoid() {
        var asyncVoid = typeof(SessionActor).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                          | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(void) && m.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToList();

        Assert.Empty(asyncVoid);
    }

    /// <summary>
    /// LoginAFKService's System.Timers.Timer was never stopped. Every login session that ended without a HALT (a failed
    /// login, a quit at the character screen) kept a timer firing for the life of the process, and after the AFK timeout
    /// its CloseSession took off the online list whichever live game session had the same session number.
    /// </summary>
    [Fact]
    public async Task ALoginSessionsAfkTimerStopsWithTheSession() {
        EquipmentAttachConcurrencyTests.Configure("[Login Server]\nLoginAfkTimeout = 3600\nLoginAfkCheckInterval = 1\n");
        var system = ActorSystem.Create("crash-afk-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var socket = system.ActorOf(Props.Create(() => new Sink()), "socket");
            var session = system.ActorOf(Props.CreateBy(new SessionProducer(socket, [typeof(LoginAFKService)])), "session");
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            system.ActorOf(Props.Create(() => new Watcher(session, stopped)));
            var instance = await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);

            var services = (ConcurrentDictionary<IActorRef, MessageService>) typeof(SessionActor)
                .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
            var deadline = DateTime.UtcNow + Timeout;
            while (services.Values.OfType<LoginAFKService>().FirstOrDefault() is null && DateTime.UtcNow < deadline) {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            var afk = Assert.Single(services.Values.OfType<LoginAFKService>());
            var timer = (System.Timers.Timer) typeof(LoginAFKService)
                .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(afk)!;
            Assert.True(timer.Enabled);

            session.Tell("Close");
            var done = await Task.WhenAny(stopped.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.True(done == stopped.Task, "timed out waiting for the session to stop");

            Assert.False(timer.Enabled, "the AFK timer outlived its login session");
        } finally {
            await system.Terminate();
        }
    }

    /// <summary>
    /// GameWorld asks an owner's InstanceContainer whether it holds the zone, and the answer comes back later (PipeTo).
    /// A sigil run's container that dropped its last zone meanwhile (a party lost, the dungeon expired) is forgotten by
    /// MSG_RUNCONTAINEREMPTY; the late "has the zone" answer then indexed the missing container: KeyNotFoundException.
    /// GameWorld's parent stops it on any exception, which took every zone of the server with it.
    /// </summary>
    [Fact]
    public async Task ALateHasZoneAnswerForAForgottenContainerFailsTheTransferInsteadOfTheWorld() {
        EquipmentAttachConcurrencyTests.Configure();
        var system = ActorSystem.Create("crash-world-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var answer = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = system.ActorOf(Props.Create(() => new Recorder(answer)));

            // The constructor preloads NPC and drop data the tests do not have; the handler needs only the maps.
            var world = (GameWorld) RuntimeHelpers.GetUninitializedObject(typeof(GameWorld));
            foreach (var name in new[] { "_publicZones", "_zoneLoaderActors" }) {
                SetField(typeof(GameWorld), world, name, new System.Collections.Generic.Dictionary<string, IActorRef>());
            }
            var containers = typeof(GameWorld).GetField("_instanceContainers", BindingFlags.Instance | BindingFlags.NonPublic)!;
            containers.SetValue(world, Activator.CreateInstance(containers.FieldType));

            var result = new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINER_QUERY_RESULT {
                OriginalSender = player,
                OriginalTransfer = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER { DestinationZone = "Run/Dungeon", OwnerCharId = 77 },
                Response = new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONERSP { HasZone = true, OwnerCharId = 77 },
            };
            typeof(GameWorld).GetMethod("ReceiveInstanceContainerQueryResult", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(world, [result]);

            var done = await Task.WhenAny(answer.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.True(done == answer.Task, "the transfer was never answered");
            Assert.NotEqual(0u, Assert.IsType<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(await answer.Task).ErrorCode);
        } finally {
            await system.Terminate();
        }
    }

    /// <summary>
    /// A transfer forwarded to an InstanceContainer for a zone it no longer holds (dropped between GameWorld's "has the
    /// zone" question and the transfer: a party member entering as the leader's sigil entry reset the dungeon, an empty
    /// zone dropping itself) threw on purpose. The container restarted, stopping every zone of that instance with the
    /// party inside, and the transfer was never answered.
    /// </summary>
    [Fact]
    public async Task AContainerAskedForAZoneItNoLongerHoldsAnswersAndKeepsRunning() {
        EquipmentAttachConcurrencyTests.Configure();
        var system = ActorSystem.Create("crash-container-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var (container, failures) = await Hosted(system, InstanceContainer.Props(77));
            var reply = await container.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(
                new ZONE_102_PROTOCOL.MSG_ZONETRANSFER { DestinationZone = "Run/Dungeon", OwnerCharId = 77 },
                Timeout, TestContext.Current.CancellationToken);

            Assert.NotEqual(0u, reply.ErrorCode);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.Empty(failures);
        } finally {
            await system.Terminate();
        }
    }

    /// <summary>
    /// A zone supervisor that throws restarts: its children (every combat sigil, object or creature of the zone) stop and
    /// are never loaded again. ZoneSigilSupervisor's First() over an empty participant list is one such throw: the zone
    /// would have no combat until the server restarts. The supervisor now logs and keeps its sigils.
    /// </summary>
    [Fact]
    public async Task AZoneSupervisorThatFailsOnAMessageKeepsRunning() {
        EquipmentAttachConcurrencyTests.Configure();
        var system = ActorSystem.Create("crash-supervisor-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var (supervisor, failures) = await Hosted(system, Props.Create(() => new ZoneSigilSupervisor(null!)));
            supervisor.Tell(new ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL { StartingParticipants = [] });

            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Empty(failures);
        } finally {
            await system.Terminate();
        }
    }

    /// <summary>
    /// A Zone that throws restarts with its supervisors and entities stopped and waits again for the load results it
    /// already had: every later transfer into it is parked and never answered, until the server restarts. It now keeps
    /// its state, and a transfer whose handling failed is answered with an error.
    /// </summary>
    [Fact]
    public async Task AZoneThatFailsOnAMessageKeepsItsPlayersAndAnswersAFailedTransfer() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicRules.Stock);
        EquipmentAttachConcurrencyTests.Configure();
        var system = ActorSystem.Create("crash-zone-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var (zoneRef, failures) = await Hosted(system, Props.Create(() => new FaultyZone()));
            var ct = TestContext.Current.CancellationToken;
            var before = (await zoneRef.Ask<ZoneBox>(new GetZone(), Timeout, ct)).Zone;
            var player = system.ActorOf(Props.Create(() => new Sink()));
            await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(new ZONE_102_PROTOCOL.MSG_ADDPLAYER {
                PlayerActor = player, PlayerObject = new CoreObject { m_globalID = 5UL },
            }, Timeout, ct);

            zoneRef.Tell(new Boom());
            var rsp = await zoneRef.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(
                new ZONE_102_PROTOCOL.MSG_ZONETRANSFER { DestinationZone = FaultyZone.Path }, Timeout, ct);

            Assert.NotEqual(0u, rsp.ErrorCode);
            var after = (await zoneRef.Ask<ZoneBox>(new GetZone(), Timeout, ct)).Zone;
            Assert.Same(before, after); // not a restarted instance
            var players = (System.Collections.Generic.HashSet<IActorRef>) typeof(Zone)
                .GetField("_players", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(after)!;
            Assert.Contains(player, players);
            Assert.Empty(failures);
        } finally {
            await system.Terminate();
            ClassicRuntime.ResetForTests();
        }
    }

    private static async Task<(IActorRef Child, ConcurrentQueue<Exception> Failures)> Hosted(ActorSystem system, Props child) {
        var failures = new ConcurrentQueue<Exception>();
        var started = new TaskCompletionSource<IActorRef>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.ActorOf(Props.Create(() => new Host(child, failures, started)));
        var done = await Task.WhenAny(started.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
        Assert.True(done == started.Task, "the hosted actor did not start");

        return (await started.Task, failures);
    }

    /// <summary>Stands in for GameWorld or a Zone: records every failure that reaches it as the supervisor.</summary>
    private sealed class Host : ReceiveActor {
        private readonly ConcurrentQueue<Exception> _failures;

        public Host(Props child, ConcurrentQueue<Exception> failures, TaskCompletionSource<IActorRef> started) {
            _failures = failures;
            started.TrySetResult(Context.ActorOf(child, "child"));
            ReceiveAny(_ => { });
        }

        protected override SupervisorStrategy SupervisorStrategy() => new OneForOneStrategy(ex => {
            _failures.Enqueue(ex);
            return Directive.Restart;
        });
    }

    private sealed record GetZone;
    private sealed record ZoneBox(Zone Zone);
    private sealed record Boom;

    /// <summary>A loaded zone with no supervisors, a handler that throws, and transfers that throw.</summary>
    private sealed class FaultyZone : Zone {
        internal const string Path = "WizardCity/WC_Streets/WC_Unicorn";

        public FaultyZone() : base(Path, 1) {
            Timers.CancelAll();
            SetField(typeof(Zone), this, "_isLoading", false);
            foreach (var name in new[] { "_objectSupervisor", "_playerSupervisor", "_volumeSupervisor", "_triggerSupervisor",
                         "_pathSupervisor", "_sigilSupervisor" }) {
                SetField(typeof(Zone), this, name, ActorRefs.Nobody);
            }
        }

        [MessageHandler(typeof(GetZone))]
        private void Get(GetZone _) => Sender.Tell(new ZoneBox(this));

        [MessageHandler(typeof(Boom))]
        private void Fail(Boom _) => throw new InvalidOperationException("test fault");

        protected override void ReceiveZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message)
            => throw new InvalidOperationException("test transfer fault");
    }

    private sealed class Recorder : ReceiveActor {
        public Recorder(TaskCompletionSource<object> first) => ReceiveAny(message => first.TrySetResult(message));
    }

    private static void SetField(System.Type type, object instance, string name, object value)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class Sink : ReceiveActor {
        public Sink() => ReceiveAny(_ => { });
    }

    private sealed class Watcher : ReceiveActor {
        public Watcher(IActorRef target, TaskCompletionSource<bool> stopped) {
            Context.Watch(target);
            Receive<Terminated>(_ => stopped.TrySetResult(true));
        }
    }

    private sealed class SessionProducer(IActorRef socket, System.Type[] services) : IIndirectActorProducer {
        public System.Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket, services);
        public void Release(ActorBase actor) { }
    }

}
