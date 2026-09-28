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
 * TRANSFER AND LISTENER REGRESSION TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Exercises production effect attachment and actor listener lifetime.
 *
 * USAGE EXAMPLE:
 * Run with the classic server test suite.
 *
 * NOTE:
 * Local actor systems and loopback sockets only.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class TcpListenerLifecycleTests {
    [Theory]
    [InlineData(null, "0.0.0.0")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    public void ListenAddressAcceptsLiteralAddressesAndStockFallback(string? value, string expected) {
        Assert.Equal(IPAddress.Parse(expected), TcpListenerActor.ParseListenAddress(value!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:18123")]
    [InlineData("not-an-address")]
    public void InvalidListenAddressFailsClearly(string value) {
        var error = Assert.Throws<ArgumentException>(() => TcpListenerActor.ParseListenAddress(value));
        Assert.Contains("Network.ListenAddress", error.Message);
    }

    [Fact]
    public async Task ActorRestartAndStopReleaseTheConfiguredLoopbackPort() {
        EquipmentAttachConcurrencyTests.Configure("[Network]\nListenAddress=127.0.0.1\n");
        using var system = ActorSystem.Create("listener-lifecycle", "akka.actor.provider = local");
        var instances = Channel.CreateUnbounded<IPEndPoint>();
        var accepted = Channel.CreateUnbounded<Socket>();
        var state = new ListenerState();
        try {
            var sink = system.ActorOf(Props.Create(() => new SocketSink(accepted)));
            var listener = system.ActorOf(Props.Create(() => new RestartableListener(state, sink, instances)));
            var first = await instances.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(IPAddress.Loopback, first.Address);
            using (var client = new TcpClient()) {
                await client.ConnectAsync(first.Address, first.Port, TestContext.Current.CancellationToken);
                using var socket = await accepted.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.True(socket.Connected);
            }
            listener.Tell(new Crash());
            var restarted = await instances.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(first, restarted);
            Assert.True(await listener.GracefulStop(TimeSpan.FromSeconds(5)));
            using var replacement = new TcpListener(first);
            replacement.Start();
            Assert.Equal(first, replacement.LocalEndpoint);
        } finally {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task SessionFailureWithoutInnerExceptionDoesNotRestartServerOrSibling() {
        EquipmentAttachConcurrencyTests.Configure("[Network]\nListenAddress=127.0.0.1\n");
        using var system = ActorSystem.Create("server-failure-isolation", "akka.actor.provider = local");
        try {
            var server = system.ActorOf(Props.Create(() => new ServerHarness()));
            var before = await server.Ask<Guid>(new ReadIdentity(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var survivor = await server.Ask<IActorRef>(new NewChild(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var siblingIdentity = await survivor.Ask<Guid>(new ReadIdentity(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(await server.Ask<bool>(new FailChild(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(before, await server.Ask<Guid>(new ReadIdentity(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(siblingIdentity, await survivor.Ask<Guid>(new ReadIdentity(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        } finally {
            await system.Terminate();
        }
    }

    private sealed record Crash;
    private sealed record ReadIdentity;
    private sealed record NewChild;
    private sealed record FailChild;
    private sealed class ListenerState { public int Port; }

    private sealed class RestartableListener : TcpListenerActor {
        public RestartableListener(ListenerState state, IActorRef sink, Channel<IPEndPoint> instances)
            : base("regression", state.Port, sink) {
            var endpoint = (IPEndPoint) Listener.LocalEndpoint;
            state.Port = endpoint.Port;
            instances.Writer.TryWrite(endpoint);
            Receive<Crash>(_ => throw new InvalidOperationException("test listener restart"));
        }
    }

    private sealed class SocketSink : ReceiveActor {
        public SocketSink(Channel<Socket> accepted) {
            Receive<SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET>(message => accepted.Writer.TryWrite(message.Socket));
        }
    }

    private sealed class FaultableChild : ReceiveActor {
        public FaultableChild() {
            var identity = Guid.NewGuid();
            Receive<Crash>(_ => throw new InvalidOperationException("session failure without inner exception"));
            Receive<ReadIdentity>(_ => Sender.Tell(identity));
        }
    }

    private sealed class ServerHarness : Server {
        private readonly Guid _identity = Guid.NewGuid();
        private IActorRef? _waiting;

        public ServerHarness() : base("isolated-server", 0, null, "127.0.0.1") { }

        protected override void ConfigureReceivers() {
            Receive<ReadIdentity>(_ => Sender.Tell(_identity));
            Receive<NewChild>(_ => Sender.Tell(Context.ActorOf(Props.Create(() => new FaultableChild()))));
            Receive<FailChild>(_ => {
                _waiting = Sender;
                var child = Context.ActorOf(Props.Create(() => new FaultableChild()));
                Context.Watch(child);
                child.Tell(new Crash());
            });
            Receive<Terminated>(_ => _waiting!.Tell(true));
        }
    }
}
