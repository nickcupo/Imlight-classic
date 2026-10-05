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
 * SESSION FAULT TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Multiplayer audit item D: a service's exception closes its session the normal way. Every service's OnPreDispose
 * runs (the wizard is saved, the zone is told to remove it) and the session stops; the exception is counted once.
 * Before, AllForOneStrategy stopped every service at once and none of that ran (a frozen ghost in the zone).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SessionFaultTests {

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // What the probe services saw, per test run (keyed by the session's actor path).
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> s_saved = new();
    private static readonly ConcurrentDictionary<string, IActorRef> s_zones = new();

    private sealed record Boom : IServerMessage {
        public byte MessageOrder => 0;
        public byte ServiceID => 101;
    }

    private sealed class ThrowingService : MessageService {
        public ThrowingService(SessionActor session) : base(session) { }

        [MessageHandler(typeof(Boom))]
        public void Explode(Boom message) => throw new InvalidOperationException("injected handler failure");
    }

    /// <summary>Stands in for WizardService: saves the wizard on pre-dispose.</summary>
    private sealed class SaveProbeService : MessageService {
        public SaveProbeService(SessionActor session) : base(session) { }

        protected override void OnPreDispose() {
            s_saved.GetOrAdd(SessionActor.ActorRef.Path.ToString(), _ => NewSignal()).TrySetResult(true);
            base.OnPreDispose();
        }
    }

    /// <summary>Stands in for ZoneService: tells its zone to remove the player on pre-dispose.</summary>
    private sealed class ZoneProbeService : MessageService {
        public ZoneProbeService(SessionActor session) : base(session) { }

        protected override void OnPreDispose() {
            if (s_zones.TryGetValue(SessionActor.ActorRef.Path.ToString(), out var zone)) {
                zone.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER { PlayerActor = SessionActor.ActorRef, GlobalId = 42 });
            }

            base.OnPreDispose();
        }
    }

    /// <summary>A service that fails while it is being built (reaches the supervisor, not a handler).</summary>
    private sealed class BrokenConstructorService : MessageService {
        public BrokenConstructorService(SessionActor session) : base(session)
            => throw new InvalidOperationException("injected constructor failure");
    }

    /// <summary>A service whose own pre-dispose throws: the others must still be asked without waiting it out.</summary>
    private sealed class ThrowingPreDisposeService : MessageService {
        public ThrowingPreDisposeService(SessionActor session) : base(session) { }

        protected override void OnPreDispose() => throw new InvalidOperationException("injected pre-dispose failure");
    }

    private sealed class Sink : ReceiveActor {
        public Sink(TaskCompletionSource<object> first) => ReceiveAny(message => first.TrySetResult(message));
    }

    private sealed class Watcher : ReceiveActor {
        public Watcher(IActorRef target, TaskCompletionSource<bool> stopped) {
            Context.Watch(target);
            Receive<Terminated>(_ => stopped.TrySetResult(true));
        }
    }

    private sealed class SessionProducer(IActorRef socket, Type[] services) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket, services);
        public void Release(ActorBase actor) { }
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Run(ActorSystem System, IActorRef Session, SessionActor Instance, Task<bool> Saved,
                              Task<object> ZoneMessage, Task<bool> Stopped) : IDisposable {
        public void Dispose() => System.Dispose();
    }

    private static Task<Run> Start(params Type[] services) => StartWith(null, services);

    private static async Task<Run> StartWith(string? config, params Type[] services) {
        EquipmentAttachConcurrencyTests.Configure(config);
        var system = ActorSystem.Create("session-fault-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var socket = system.ActorOf(Props.Create(() => new Sink(new TaskCompletionSource<object>())), "socket");
        var zoneMessage = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var zone = system.ActorOf(Props.Create(() => new Sink(zoneMessage)), "zone");

        // The probes look themselves up by the session's path, known before the session exists.
        var key = new RootActorPath(new Address("akka", system.Name)) / "user" / "session";
        var saved = s_saved.GetOrAdd(key.ToString(), _ => NewSignal());
        s_zones[key.ToString()] = zone;

        var session = system.ActorOf(Props.CreateBy(new SessionProducer(socket, services)), "session");
        var stopped = NewSignal();
        system.ActorOf(Props.Create(() => new Watcher(session, stopped)));
        var instance = await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);

        return new Run(system, session, instance, saved.Task, zoneMessage.Task, stopped.Task);
    }

    private static async Task<T> Within<T>(Task<T> task, string what) {
        var done = await Task.WhenAny(task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
        Assert.True(done == task, $"timed out waiting for {what}");

        return await task;
    }

    [Fact]
    public async Task AThrowingHandlerSavesLeavesTheZoneAndClosesTheSession() {
        using var run = await Start(typeof(ThrowingService), typeof(SaveProbeService), typeof(ZoneProbeService));

        run.Session.Tell(new Boom());

        Assert.True(await Within(run.Saved, "the wizard save (pre-dispose)"));
        var removal = Assert.IsType<ZONE_102_PROTOCOL.MSG_REMOVEPLAYER>(await Within(run.ZoneMessage, "the zone removal"));
        Assert.Equal(run.Session, removal.PlayerActor);
        Assert.True(await Within(run.Stopped, "the session to stop"));
        Assert.True(run.Instance.IsDisposed);
        Assert.Equal(1, run.Instance.FaultCount);
    }

    [Fact]
    public async Task AServiceThatFailsToStartStillLetsTheOthersSaveAndLeave() {
        using var run = await Start(typeof(SaveProbeService), typeof(BrokenConstructorService), typeof(ZoneProbeService));

        Assert.True(await Within(run.Saved, "the wizard save (pre-dispose)"));
        Assert.IsType<ZONE_102_PROTOCOL.MSG_REMOVEPLAYER>(await Within(run.ZoneMessage, "the zone removal"));
        Assert.True(await Within(run.Stopped, "the session to stop"));
        Assert.True(run.Instance.FaultCount >= 1);
    }

    [Fact]
    public async Task AThrowingPreDisposeDoesNotHoldUpTheRest() {
        // The faulting pre-dispose answers anyway, so the zone removal is not 2 s (an Ask timeout) late.
        using var run = await Start(typeof(ThrowingService), typeof(ThrowingPreDisposeService), typeof(SaveProbeService),
            typeof(ZoneProbeService));
        var started = DateTime.UtcNow;

        run.Session.Tell(new Boom());

        Assert.True(await Within(run.Saved, "the wizard save (pre-dispose)"));
        Assert.IsType<ZONE_102_PROTOCOL.MSG_REMOVEPLAYER>(await Within(run.ZoneMessage, "the zone removal"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5), "a pre-dispose Ask waited out its timeout");
        Assert.True(await Within(run.Stopped, "the session to stop"));
        Assert.Equal(2, run.Instance.FaultCount); // the handler, then the pre-dispose (logged as a one-line follow-up)
    }

    [Fact]
    public async Task ARetryExceptionStillGoesToTheSupervisorAndRestartsOnlyThatService() {
        using var run = await StartWith("[Advanced]\nSessionActorServiceRetryCount = 3\nSessionActorServiceRangeRetry = 30\n",
            typeof(RetryService), typeof(SaveProbeService), typeof(ZoneProbeService));

        run.Session.Tell(new Boom());
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(run.Saved.IsCompleted);
        Assert.False(run.Stopped.IsCompleted);
        Assert.False(run.Instance.IsDisposed);
        Assert.Equal(0, run.Instance.FaultCount);
    }

    [Fact]
    public async Task AServiceOutOfRetriesClosesTheSessionTheNormalWay() {
        // No retries configured: Akka stops the service, and the session closes on its Terminated.
        using var run = await StartWith("[Advanced]\nSessionActorServiceRetryCount = 0\nSessionActorServiceRangeRetry = 30\n",
            typeof(RetryService), typeof(SaveProbeService), typeof(ZoneProbeService));

        run.Session.Tell(new Boom());

        Assert.True(await Within(run.Saved, "the wizard save (pre-dispose)"));
        Assert.IsType<ZONE_102_PROTOCOL.MSG_REMOVEPLAYER>(await Within(run.ZoneMessage, "the zone removal"));
        Assert.True(await Within(run.Stopped, "the session to stop"));
    }

    private sealed class RetryService : MessageService {
        public RetryService(SessionActor session) : base(session) { }

        [MessageHandler(typeof(Boom))]
        public void Explode(Boom message) => throw new ServiceRetryException("injected retry");
    }

}
