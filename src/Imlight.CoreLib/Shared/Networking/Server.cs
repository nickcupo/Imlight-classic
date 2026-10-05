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
 * SERVER
 * ========================================================================
 * 
 * PURPOSE:
 * Provides a base implementation for network servers using Akka.NET 
 * actor model, managing session creation, connection handling, 
 * and server-level operations.
 * 
 * USAGE EXAMPLE:
 * // Inherit and implement a custom server
 * public class LoginServer : Server {
 *     public LoginServer(string name, int port) : base(name, port, factoryProps) { }
 * }
 * 
 * NOTE:
 * - Abstract base class for network server implementations
 * - Manages active sessions and TCP connections
 * - Provides unique session ID generation and connection handling
 * 
 * TODO:
 * 
 * Created by: Jooty with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Structures;

namespace Imlight.CoreLib.Shared.Networking;

public abstract class Server : ReceiveProtocolDispatcher {

    public string Name { get; }
    public string Ip { get; }
    public int Port { get; }
    public virtual string RealmName { get; protected set; }

    protected readonly ObservableHashSet<SessionActor> ActiveSessions;

    private readonly IActorRef _actorFactoryRef;
    private readonly long _serverStartTime;
    private readonly Props _factoryProps;

    // CLASSIC: open connections per client address on this server (H5). [Network] MaxConnectionsPerIp (default 16,
    // 0 = no limit) and MaxConnections (default 2000); loopback is exempt unless ConnectionLimitLoopback = true.
    private readonly System.Collections.Generic.Dictionary<string, int> _connectionsPerIp = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<IActorRef, string> _connectionIps = new();
    private readonly int _maxConnectionsPerIp = Auth.SecuritySettings.Int("Network.MaxConnectionsPerIp", 16);
    private readonly int _maxConnections = Auth.SecuritySettings.Int("Network.MaxConnections", 2000);
    private readonly bool _limitLoopback = Auth.SecuritySettings.Bool("Network.ConnectionLimitLoopback", false);
    private long _refusedConnections;

    public Server(string name, int port, Props factoryProps, string ip = null) {
        this.Name = name;
        this.Port = port;
        this.ActiveSessions = new ObservableHashSet<SessionActor>();
        this._serverStartTime = DateTimeOffset.Now.ToUnixTimeSeconds();
        this._factoryProps = factoryProps;

        // Use provided IP if set, otherwise auto-detect.
        if (!string.IsNullOrWhiteSpace(ip)) {
            this.Ip = ip;
        }
        else {
#if !DEBUG
            try {
                this.Ip = new HttpClient()
                    .GetStringAsync("https://api.ipify.org/")
                    .GetAwaiter()
                    .GetResult();
            }
            catch {
                // If the ipify call fails (offline, timeout, blocked), fall back
                // to loopback so the server can still start.
                Logger.Warning("Failed to resolve public IP from ipify.org — falling back to 127.0.0.1.");
                this.Ip = "127.0.0.1";
            }
#else
            this.Ip = "127.0.0.1";
#endif
        }

        CreateTcpListener();
        _actorFactoryRef = CreateActorFactory();
    }

    protected override void ConfigureReceivers() {
        // CLASSIC: a session actor that stopped (however it stopped) frees its address's connection slot.
        Receive<Terminated>(terminated => ReleaseConnection(terminated.ActorRef));
        base.ConfigureReceivers();
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET))]
    protected virtual void ReceiveAllocateSocket(SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET message) {
        // CLASSIC: refuse a connection over the per-address or total limit before any session state exists.
        var address = Imlight.Classic.Net.GameSessionKeys.NormalizeAddress(
            (message.Socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString()) ?? "?";
        var refusal = Imlight.Classic.Net.ConnectionLimits.Refuse(address, _connectionsPerIp.GetValueOrDefault(address),
            _connectionIps.Count, _maxConnectionsPerIp, _maxConnections, _limitLoopback);
        if (refusal is not null) {
            if (_refusedConnections++ % 100 == 0) {
                Logger.Warning("{Name} refused a connection from {Ip}: {Reason} ({Count} refused so far)",
                    Logger.Args(Name, address, refusal, _refusedConnections));
            }

            try {
                message.Socket.Close();
            }
            catch (Exception) {
                // already gone
            }

            return;
        }

        // Create a new child actor, which represents the active socket connection.
        var id = GetNewUniqueId();
        // CLASSIC: ids come from ActiveSessions, which on the login server holds only enqueued sessions; an id still
        // used by a child would throw InvalidActorNameException here.
        for (var tries = 0; !Context.Child($"SessionActor.{id}").IsNobody() && tries < 64; tries++) {
            id = GetNewUniqueId();
        }

        var sessionProps = SessionActor.Props(message.Socket, id, Context.Self, _actorFactoryRef);
        var child = Context.ActorOf(sessionProps, $"SessionActor.{id}");
        Context.Watch(child);
        _connectionIps[child] = address;
        _connectionsPerIp[address] = _connectionsPerIp.GetValueOrDefault(address) + 1;

        // Logger
        Logger.Debug("{Type} new connection from {RemoteEndPoint} given session ID {Id}",
            Logger.Args(GetType(), message.Socket.RemoteEndPoint?.ToString(), id));
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_DEALLOCATESOCKET))]
    protected virtual void ReceiveDeallocateSocket(SERVER_100_PROTOCOL.MSG_DEALLOCATESOCKET message) {
        if (!ActiveSessions.Remove(ActiveSessions.FirstOrDefault(x => x.SessionID == message.Id))) {
            // It's fine if no session was found. This is a common occurrence.
        }
        else {
            Logger.Information("{Name} lost connection to {Ip}", Logger.Args(Name, message.Ip, message.Id));
        }
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_QUERYACTORFACTORY))]
    protected void ReceiveQueryActorFactory(SERVER_100_PROTOCOL.MSG_QUERYACTORFACTORY message) {
        var reply = new SERVER_100_PROTOCOL.MSG_ACTORFACTORYINFO() {
            Reference = _actorFactoryRef
        };

        Sender.Tell(reply);
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_QUERYSERVER))]
    protected void ReceiveQueryServer(SERVER_100_PROTOCOL.MSG_QUERYSERVER message) {
        // Get a list of strings for the connected IPs.
        var ips = ActiveSessions.Select(x => x.RemoteIp).ToArray();
        var msg = new SERVER_100_PROTOCOL.MSG_SERVERINFO() {
            IP = this.Ip,
            Port = Port,
            PlayerCount = (ushort) ActiveSessions.Count,
            ActorRef = Context.Self,
            ConnectedIps = ips,
            RealmName = RealmName
        };

        Sender.Tell(msg);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        // There is no attempting to stabilize the connection server side. The client will attempt to
        // reconnect on any given failure. This is a good thing, as it allows us to simply stop the session actor
        // and let the client handle the rest.
        new OneForOneStrategy(
            maxNrOfRetries: 1,
            withinTimeRange: TimeSpan.FromSeconds(30),
            localOnlyDecider: ex => {
                // Client regularly shuts down the socket. No need to log it.
                if (ex.Message.ToLower().Contains("shutdown")) {
                    return Directive.Stop;
                }

                Logger.Error("SessionActor {Source} has failed with exception {Exception}",
                    Logger.Args(ex.InnerException?.Source ?? ex.Source ?? ex.GetType().Name, ex));
                return Directive.Stop;
            }
        );

    private void ReleaseConnection(IActorRef child) {
        if (!_connectionIps.Remove(child, out var address)) {
            return;
        }

        var left = _connectionsPerIp.GetValueOrDefault(address) - 1;
        if (left <= 0) {
            _connectionsPerIp.Remove(address);
        }
        else {
            _connectionsPerIp[address] = left;
        }
    }

    protected virtual ushort GetNewUniqueId() {
        ushort newId = 0;
        var isUniqueId = false;
        var random = new Random();

        while (!isUniqueId) {
            newId = (ushort) random.Next(ushort.MaxValue);

            if (ActiveSessions.All(s => s.SessionID != newId)) {
                isUniqueId = true;
            }
        }

        return newId;
    }

    private void CreateTcpListener() {
        var actorName = $"{Name}.TcpListener.{Port}";
        var tcpProps = TcpListenerActor.Props(Name, Port, Context.Self);
        Context.ActorOf(tcpProps, actorName);

        Logger.Verbose("New actor created under {Path}: {ActorName}", Logger.Args(Context.Self.Path, actorName));
    }

    private IActorRef CreateActorFactory() {
        if (_factoryProps is null) {
            return null;
        }

        var actorName = $"{Name}.ActorFactory";

        Logger.Verbose("New actor created under {Path}: {ActorName}", Logger.Args(Context.Self.Path, actorName));

        return Context.ActorOf(_factoryProps, actorName);
    }

}
