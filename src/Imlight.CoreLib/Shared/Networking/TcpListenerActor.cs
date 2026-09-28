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
 * TCP LISTENER ACTOR
 * ========================================================================
 * 
 * PURPOSE:
 * Manages asynchronous TCP socket listening and new connection 
 * allocation for network servers, handling incoming socket connections.
 * 
 * USAGE EXAMPLE:
 * // TCP listener is typically created by a server instance
 * // Handles incoming network connection acceptance
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Shared.Networking;

public class TcpListenerActor : ReceiveActor {

    public string Name { get; }
    public int Port { get; }
    public bool Listening { get; private set; }
    public TcpListener Listener { get; }

    private readonly CancellationTokenSource _tokenSource;
    private readonly IActorRef _serverRef;

    public TcpListenerActor(string name, int port, IActorRef serverRef) {
        this.Name = name;
        this.Port = port;
        this.Listener = new TcpListener(ParseListenAddress(ConfigurationManager.GetSetting("Network.ListenAddress")), port);
        this._tokenSource = new CancellationTokenSource();
        this._serverRef = serverRef;

        Start();
    }

    public static Props Props(string name, int port, IActorRef serverRef) 
        => Akka.Actor.Props.Create(() => new TcpListenerActor(name, port, serverRef));

    internal static IPAddress ParseListenAddress(string configuredAddress) {
        if (configuredAddress is null) {
            return IPAddress.Any;
        }

        if (!IPAddress.TryParse(configuredAddress, out var address)) {
            throw new ArgumentException("Network.ListenAddress must be a valid IP address.", nameof(configuredAddress));
        }

        return address;
    }

    public void Start() {
        try {
            Listener.Start();
            Listening = true;
            Logger.Information("TcpListener for {Name} bound to {EndPoint}, listening.",
                Logger.Args(Name, Listener.LocalEndpoint));
            _ = ListenAsync(_tokenSource.Token);
        } catch (Exception ex) {
            Stop();
            _tokenSource.Dispose();
            throw new InvalidOperationException($"TcpListener for {Name} could not bind {Listener.LocalEndpoint}.", ex);
        }
    }

    public void Stop() {
        if (_tokenSource.IsCancellationRequested) {
            return;
        }

        Listening = false;
        _tokenSource.Cancel();
        Listener.Stop();
        Logger.Debug("TcpListener for {Name} on port {Port} stopped.", Logger.Args(Name, Port));
    }

    protected override void PostStop() {
        Stop();
        _tokenSource.Dispose();
        base.PostStop();
    }

    /// <summary>
    /// Asynchronously listen for incoming connections.
    /// </summary>
    protected async Task ListenAsync(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                var socket = await Listener.AcceptSocketAsync(token);
                if (token.IsCancellationRequested) {
                    socket.Dispose();
                    break;
                }
                AllocateNewSocket(socket);
            }
            catch (OperationCanceledException) {
                // Normal shutdown — token was cancelled.
                break;
            }
            catch (ObjectDisposedException) {
                // Listener was disposed — normal during shutdown.
                break;
            }
            catch (Exception ex) {
                Logger.Error("TcpListener for {Name} on port {Port} accept error: {Exception}",
                    Logger.Args(Name, Port, ex));
            }
        }
    }

    private void AllocateNewSocket(Socket socket) {
        Logger.Debug("TcpListener for {Name} accepted connection from {RemoteEndPoint}.",
            Logger.Args(Name, socket.RemoteEndPoint?.ToString()));

        var msg = new SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET() { Socket = socket };
        _serverRef.Tell(msg);
    }

}
