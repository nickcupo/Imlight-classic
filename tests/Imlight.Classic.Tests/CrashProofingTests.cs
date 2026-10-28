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
using Imlight.CoreLib.Login.Services;
using Imlight.CoreLib.Shared.Networking;
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

    private sealed class Sink : ReceiveActor {
        public Sink() => ReceiveAny(_ => { });
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

}
