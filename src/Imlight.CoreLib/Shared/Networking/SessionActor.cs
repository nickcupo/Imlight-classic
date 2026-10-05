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
 * SESSION ACTOR
 * ========================================================================
 * 
 * PURPOSE:
 * Manages a network session lifecycle, handling socket connections, 
 * message routing, and inter-service communication in a distributed 
 * actor-based networking system.
 * 
 * USAGE EXAMPLE:
 * // Session actor is typically created and managed by server infrastructure
 * // Handles message dispatching, service initialization, and session management
 * 
 * NOTE:
 * - Core component of distributed network communication
 * - Manages socket listeners, message services, and session state
 * - Supports dynamic service loading and message routing
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imlight.Classic.Net;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Services;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>
/// Represents a connected socket as a ReceiveActor.
/// </summary>
public sealed partial class SessionActor : ReceiveActor, IDisposable {
    private ZoneAttachContext _doorAttach;
    internal ZoneAttachContext DoorAttach => Volatile.Read(ref _doorAttach);
    internal void PublishDoorAttach(ZoneAttachContext context) => Volatile.Write(ref _doorAttach, context);

    private readonly byte _serviceRetryCount                 = ConfigurationManager.Settings["Advanced.SessionActorServiceRetryCount"].AsByte();
    private readonly byte _serviceTimeRangeRetryInSeconds    = ConfigurationManager.Settings["Advanced.SessionActorServiceRangeRetry"].AsByte();

    internal Imlight.CoreLib.Game.Monstrology.MonstrologySessionPolicy MonstrologySession { get; } = new();

    public ushort SessionID                                  { get; }
    public uint OfferTime                                    { get; set; }
    public uint OfferMillisecondsIntoSecond                  { get; set; }
    public IActorRef ActorRef                                { get; }
    public IActorRef ServerRef                               { get; }
    public bool SessionValid                                 { get; private set; }
    public bool IsInQueue                                    { get; private set; }
    public ushort QueuePosition                              { get; private set; }
    public IMessage CachedDequeueMessage                     { get; set; }
    public long Ping                                         { get; private set; }
    public TimeSpan LastPacketReceivedAt                     => TimeSpan.FromTicks(Interlocked.Read(ref _lastPacketReceivedTicks)); // CLASSIC: a HeartbeatPolicy.Clock reading.

    // CLASSIC: set when this session has sent the client to another zone (MSG_SERVERTRANSFER); a newer login of the
    // account then closes it without the "logged in elsewhere" notice (Game/AccountSessions.cs).
    private volatile bool _transferringOut;
    internal bool TransferringOut => _transferringOut;
    internal void MarkTransferringOut() => _transferringOut = true;

    public string Ip;
    public string RemoteIp;
    // CLASSIC: the server address this client connected to (KingsIsle's launcher is sent URLs on it).
    public string LocalIp;

    private readonly IActorRef _actorFactoryRef;
    // CLASSIC: filled by each service as it is constructed (RegisterService), on the service's thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IActorRef, MessageService> _services;
    // CLASSIC: the services in creation order, with their types, known at once.
    private readonly List<(IActorRef Ref, Type Type)> _serviceOrder = [];
    private readonly Dictionary<Type, List<IActorRef>> _dispatchTable = [];
    private readonly Socket _socket;
    private readonly List<IMessage> _preInitMessages = new();
    // CLASSIC: [Advanced] PreHandshakeMessageLimit (default 16). The list was unbounded for the whole accept wait.
    private static readonly int s_preHandshakeLimit = Imlight.CoreLib.Auth.SecuritySettings.Int(
        "Advanced.PreHandshakeMessageLimit", Imlight.Classic.Net.ConnectionLimits.DefaultPreHandshakeMessages);
    private IActorRef _socketListenerRef;
    private IActorRef _socketSenderRef;
    private volatile bool _isDisposed;

    /// <summary>CLASSIC: true once Dispose ran; a service that adds this session somewhere checks it afterwards.</summary>
    internal bool IsDisposed => _isDisposed;
    private long _lastPacketReceivedTicks;

    // ctor
    public SessionActor(Socket socket, ushort sessionId, IActorRef server, IActorRef actorFactoryRef = null) {
        this._socket = socket;
        this.Ip = socket.RemoteEndPoint.ToString();
        // CLASSIC: the address itself (an IPv6 endpoint "[::ffff:a.b.c.d]:port" split on ':' gave "[" for everyone).
        this.RemoteIp = Imlight.Classic.Net.GameSessionKeys.NormalizeAddress(
            (socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString())
            ?? socket.RemoteEndPoint.ToString().Split(':')[0];
        this.LocalIp = Imlight.CoreLib.Classic.Launcher.LauncherPatchServer.AddressText(socket.LocalEndPoint); // CLASSIC
        this.SessionID = sessionId;
        this._services = new System.Collections.Concurrent.ConcurrentDictionary<IActorRef, MessageService>();
        this.ServerRef = server;

        if (actorFactoryRef != null) {
            this._actorFactoryRef = actorFactoryRef;
        }
        else {
            // Fallback for callers that don't provide the ref.
            var query = new SERVER_100_PROTOCOL.MSG_QUERYACTORFACTORY();
            this._actorFactoryRef = server.Ask<SERVER_100_PROTOCOL.MSG_ACTORFACTORYINFO>(query)
                .Result
                .Reference;
        }

        ActorRef = Context.Self;

        CreateSocketActors(socket);
        ConfigureReceivers();
    }

    // Akka.NET ctor
    public static Props Props(Socket socket, ushort sessionId, IActorRef server, IActorRef actorFactoryRef = null)
        => Akka.Actor.Props.Create(() => new SessionActor(socket, sessionId, server, actorFactoryRef));

    /// <summary>
    /// Places the session in the queue.
    /// </summary>
    /// <param name="pos"></param>
    public void PlaceInQueue(ushort pos) {
        IsInQueue = true;
        QueuePosition = pos;
    }

    /// <summary>
    /// Removes the session from the queue.
    /// </summary>
    public void Dequeue() {
        // Send the dequeue message to the socket.
        _socketListenerRef.Tell(CachedDequeueMessage);
    }

    /// <summary>
    /// Enqueues the session to the server.
    /// </summary>
    /// <returns></returns>
    public SERVER_100_PROTOCOL.MSG_PLAYERENQUEUEDRSP EnqueueToServer() {
        var msg = new SERVER_100_PROTOCOL.MSG_PLAYERENQUEUED() {
            SessionActor = this
        };

        var rsp = ServerRef.Ask<SERVER_100_PROTOCOL.MSG_PLAYERENQUEUEDRSP>(msg)
            .Result;

        return rsp;
    }

    /// <summary>
    /// Enqueues the session to the server.
    /// </summary>
    /// <param name="serverRef"></param>
    /// <returns></returns>
    public IMessage EnqueueToServer(IActorRef serverRef) {
        var msg = new SERVER_100_PROTOCOL.MSG_PLAYERENQUEUED() {
            SessionActor = this
        };

        var rsp = serverRef.Ask<IMessage>(msg)
            .Result;

        return rsp;
    }

    /// <summary>
    /// Dispatches a <see cref="IServerMessage"/> to any service that can handle the message.
    /// </summary>
    /// <param name="msg"></param>
    private void HandleInternalTell(IServerMessage msg) {
        if (_dispatchTable.TryGetValue(msg.GetType(), out var handlers)) {
            var wasDispatched = false;
            foreach (var handler in handlers) {
                if (handler == Sender) {
                    continue;
                }
                handler.Forward(msg);
                wasDispatched = true;
            }

            if (!wasDispatched) {
                Unhandled(msg);
            }
            return;
        }

        Unhandled(msg);
    }

    /// <summary>
    /// Dispatches a <see cref="IServerMessage"/> to any service that can handle the message and waits for the answer.
    /// </summary>
    /// <remarks>
    /// This blocks the calling service (not this SessionActor) until the answer or the timeout. CLASSIC: new code uses
    /// <see cref="HandleInternalAskAsync{T}"/> with PipeTo instead. A disposed session's siblings are stopping, so the
    /// wait there is short: a disposed session's ZoneService used to sit out the full 20 s (live 2026-10-01).
    /// </remarks>
    public T HandleInternalAsk<T>(IServerMessage msg)
        where T : IServerMessage {
        var timeout = _isDisposed ? DisposedAskTimeout : InternalAskTimeout;
        var task = HandleInternalAskAsync<T>(msg, timeout);
        try {
            return task.Result;
        }
        catch (Exception ex) {
            Logger.Warning("SessionActor {0} service asked another service with {1}, but got no answer{2}: {3}",
                Logger.Args(SessionID, msg.GetType().Name, _isDisposed ? " (session disposing)" : "", ex.GetBaseException().Message));

            return default;
        }
    }

    /// <summary>CLASSIC: how long an internal Ask waits for a sibling service.</summary>
    internal static readonly TimeSpan InternalAskTimeout = TimeSpan.FromSeconds(20);

    /// <summary>CLASSIC: how long it waits once the session is disposing.</summary>
    internal static readonly TimeSpan DisposedAskTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// CLASSIC: the non-blocking internal Ask. The task completes with the first handling service's answer, or with
    /// default when no other service handles <paramref name="msg"/>; it faults on timeout. Pipe it to the asking actor
    /// (<c>PipeTo(Self, ...)</c>) rather than waiting on it.
    /// </summary>
    public Task<T> HandleInternalAskAsync<T>(IServerMessage msg, TimeSpan? timeout = null)
        where T : IServerMessage {
        // Sender is only meaningful on this actor's own thread; services call this from theirs, where it is NoSender.
        IActorRef asker = null;
        try { asker = Sender; } catch (NotSupportedException) { }

        if (_dispatchTable.TryGetValue(msg.GetType(), out var handlers)) {
            foreach (var handler in handlers) {
                if (handler == asker || handler is IInternalActorRef { IsTerminated: true }) {
                    continue;
                }

                return handler.Ask<T>(msg, timeout: timeout ?? InternalAskTimeout);
            }
        }

        Unhandled(msg);

        return Task.FromResult(default(T));
    }

    /// <summary>
    /// Sends a message to the server and awaits a response.
    /// </summary>
    /// <param name="msg"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="SessionFatalException"></exception>
    public T AskServer<T>(IServerMessage msg)
        where T : IServerMessage {
        if (ServerRef is not null) {
            return ServerRef.Ask<T>(msg).Result;
        }

        throw new SessionFatalException($"SessionActor [{SessionID}] contained a null server reference!");
    }

    /// <summary>
    /// CLASSIC: a service of this session reports its instance when it is constructed (on its own thread).
    /// </summary>
    internal void RegisterService(IActorRef service, MessageService instance) {
        if (service is not null && instance is not null && _services is not null) { // null only in a bare test double
            _services[service] = instance;
        }
    }

    /// <summary>
    /// Gets the actor reference for the zone.
    /// </summary>
    /// <returns>The actor reference for the zone, or null if the zone service is not available.</returns>
    public IActorRef GetZoneActor() {
        // Check to see if we have a ZoneService.
        var zoneService = _services.FirstOrDefault(x => x.Value is ZoneService);
        if (zoneService.Key is null) {
            return null;
        }

        return ((ZoneService)zoneService.Value).ZoneActor;
    }

    /// <summary>
    /// Retrieves the associated account for the session actor.
    /// </summary>
    /// <returns>The associated account, or null if no account is found.</returns>
    public Account GetAssociatedAccount() {
        // Check to see if we have a LoginService.
        var accountService = _services.FirstOrDefault(x => x.Value is AccountService);
        if (accountService.Key is null) {
            return null;
        }

        return ((AccountService) accountService.Value).Account;
    }

    /// <summary>
    /// Disposes of the SessionActor.
    /// </summary>
    public void Dispose() {
        // Avoid duplicate Dispose calls.
        if (_isDisposed) {
            return;
        }

        Logger.Debug("SessionActor {Id} disposing.", Logger.Args(SessionID));
        _isDisposed = true;

        // CLASSIC: the session is gone for everyone at once. Zone triggers stop asking it for its wizard (each Ask
        // waited 5 s, one after another: 40 s of stalled triggers on live 2026-10-01), and it leaves the online list
        // (a teleport-to-friend asked a partner disposed seconds earlier, rig-pg-ms 2026-10-04).
        Classic.PlayerQuery.MarkGone(ActorRef);
        Imlight.CoreLib.WizardData.Collections.OnlinePlayerCollection.RemoveSessionByActorPath(ActorRef?.Path.ToString());

        // Send a message to the server to deallocate this SessionActor.
        var msg = new SERVER_100_PROTOCOL.MSG_DEALLOCATESOCKET() {
            Id = SessionID,
            Socket = this._socket,
            Ip = this.RemoteIp
        };
        ServerRef.Tell(msg);

        // Don't preemptively close the socket; the client disconnects itself
        // after receiving the final message (e.g. MSG_CHARACTERSELECTED).
        // Closing it here races with any pending SocketSender messages.

        // Dispose services.
        SendPreDisposeToServices();
        SendDisposeToServices();

        Sender.Tell("DoneDisposing");

        // Dispose self.
        ActorRef.Tell(PoisonPill.Instance);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        // Recall that child actors of the SessionActor are the message services.
        new AllForOneStrategy(
            maxNrOfRetries: _serviceRetryCount,
            withinTimeRange: TimeSpan.FromSeconds(_serviceTimeRangeRetryInSeconds),
            localOnlyDecider: ex => {
                switch (ex) {
                    case ServiceRetryException tex: {
                            Logger.Error("SessionActor {Sid} service {Class} L:{LineNumber} threw restart exception: " +
                                      "{Message}", Logger.Args(SessionID, tex.CallingClass, tex.LineNumber, tex.Message));
                            return Directive.Restart;
                        }
                    case SessionFatalException tex: {
                            Logger.Error("SessionActor {Sid} service {Class} L:{LineNumber} threw fatal exception: " +
                                      "{Message}", Logger.Args(SessionID, tex.CallingClass, tex.LineNumber, tex.Message));
                            return Directive.Stop;
                        }
                    default:
                        Logger.Error("SessionActor {Sid} service {Class} L:{LineNumber} threw unknown exception: " +
                                     "{Message}. Exception details: {Exception}. Inner exception: {InnerException}",
                                     Logger.Args(SessionID, ex.TargetSite.DeclaringType, ex.TargetSite.Name, ex.Message, ex, ex.InnerException));
                        return Directive.Stop;
                }
            }
        );

    protected override void PreStart() {
        // Ask the ActorFactory for this actor's message services.
        var msg = new SERVICE_101_PROTOCOL.MSG_QUERYUNLOADEDSERVICES();
        var services = _inProcessServices?.ToList() ?? _actorFactoryRef
            .Ask<SERVICE_101_PROTOCOL.MSG_SERVICESLIST>(msg)
            .Result
            .Services;

        SetServices(services);

        base.PreStart();
    }

    internal const string AccountSessionsCloseSoon = "CloseAfterNotice";

    // CLASSIC: every service has stopped by now, so nothing of this session writes any more: a newer login of the
    // account waiting for it (Game/AccountSessions.cs) may load the account.
    protected override void PostStop() {
        try {
            Imlight.CoreLib.Game.AccountSessions.Release(this);
        }
        finally {
            base.PostStop();
        }
    }

    protected override void Unhandled(object message) {
        // CLASSIC: a client request no service handles is a silent spinner or hang for the player. Warn once per message
        // type per process, so one play session lists the real missing handlers; the rest stay at Verbose.
        if (message is not IServerMessage && Classic.UnhandledMessageLog.FirstTime(message?.GetType())) {
            Logger.Warning("SessionActor {Id} received unhandled message of type {Type} (first of its type; later ones at Verbose).",
                Logger.Args(SessionID, message?.GetType().Name));

            return;
        }

        Logger.Verbose("SessionActor {Id} received unhandled message of type {Type}.",
            Logger.Args(SessionID, message?.GetType()));
    }

    private void ConfigureReceivers() {
        // Specific message handlers.
        Receive<string>(x => x == "Close", x => Dispose());
        // CLASSIC: closed by a newer login of the account; the notice it was just sent gets half a second to go out.
        Receive<string>(x => x == AccountSessionsCloseSoon, x => Context.System.Scheduler.ScheduleTellOnce(
            TimeSpan.FromMilliseconds(500), Self, "Close", ActorRefs.NoSender));
        Receive<string>(x => x == "Identify", x => Sender.Tell(this));
        Receive<SERVICE_101_PROTOCOL.MSG_GETALLSERVICES>(InitializeActiveSession);
        Receive<SERVER_100_PROTOCOL.MSG_PING>(x => this.Ping = x.Ping);
        Receive<Exception>(ReceiveException);
        Receive<SERVER_100_PROTOCOL.MSG_RECEIVEDPACKET>(x => HandlePacket(x.Packet));

        Receive<LegacyDoorOwnerObject>(ReceiveLegacyDoorOwnerObject);
        Receive<LegacyDoorSocketBatch>(ReceiveLegacyDoorSocketBatch);

        // CLASSIC: a batch of client messages (ambient wizards' moves): each goes to the socket, in order.
        Receive<ZONE_102_PROTOCOL.MSG_CLIENTBATCH>(batch => {
            foreach (var message in batch.Messages ?? []) {
                SendToSocket(message);
            }
        });

        // Generic message handlers.
        Receive<IServerMessage>(HandleInternalTell);
        Receive<IMessage>(SendToSocket);
    }

    private void CreateSocketActors(Socket socket) {
        // Create the socket receiver actor.
        var props = Akka.Actor.Props.Create(() => new SocketListener(Self, socket, SessionID));
        _socketListenerRef = Context.ActorOf(props, $"SocketListener-{SessionID}");

        // Create the socket sender actor.
        var senderProps = Akka.Actor.Props.Create(() => new SocketSender(Self, socket, SessionID));
        _socketSenderRef = Context.ActorOf(senderProps, $"SocketSender-{SessionID}");
    }

    private void SendToSocket(IMessage message) {
        ObserveOutgoing(message); // CLASSIC: a server teleport re-anchors the movement guard (SessionActor.Movement.cs)
        _socketSenderRef.Forward(message);
    }

    private void InitializeActiveSession(SERVICE_101_PROTOCOL.MSG_GETALLSERVICES message) {
        SessionValid = true;

        Logger.Debug("SessionActor {Id} initialized with all services.", Logger.Args(SessionID));

        foreach (var preInitMessage in _preInitMessages) {
            HandlePacket(preInitMessage);
        }

        _preInitMessages.Clear(); // CLASSIC: was kept for the session's lifetime
    }

    private void SetServices(List<Type> services) {
        // Create handshake services (ControlService) first. Their constructor sends the SessionOffer, the
        // first thing the client waits for on connect, so they must not queue behind the other services'
        // blocking identity Asks in this loop. See IHandshakeService.
        foreach (var service in services.OrderByDescending(t => typeof(IHandshakeService).IsAssignableFrom(t))) {
            var serviceName = $"{service}";
            var props = Akka.Actor.Props.Create(service, this);
            var childRef = Context.ActorOf(props, serviceName);

            Logger.Verbose("New actor created for session {Id}: {Name}",
                Logger.Args(SessionID, serviceName));

            // CLASSIC: the dispatch table comes from the service's type (the same table its instance reports through
            // MSG_QUERYMESSAGESERVICEIDENTITY); the instance registers itself (RegisterService) when it is constructed.
            // A blocking identity Ask per service held a pool thread for every service of every new connection.
            _serviceOrder.Add((childRef, service));

            // Populate the dispatch table.
            foreach (var msgType in MessageHandlerTable.HandlersOf(service).Keys) {
                if (!_dispatchTable.TryGetValue(msgType, out var list)) {
                    list = [];
                    _dispatchTable[msgType] = list;
                }
                list.Add(childRef);
            }
        }
    }

    private void ReceiveException(Exception ex) {
        Dispose();
    }

    private void HandlePacket(IMessage packet) {
        if (_isDisposed) {
            return; // CLASSIC: closing; nothing more is cached or dispatched
        }

        Interlocked.Exchange(ref _lastPacketReceivedTicks, HeartbeatPolicy.Clock.Ticks); // CLASSIC: ControlService's heartbeat reads it.

        // If the session still is not valid (the client hasn't completed the session handshake)
        // we'll cache all non-control messages for later processing.
        if (!SessionValid && packet.ServiceId != 0) {
            if (!Imlight.Classic.Net.ConnectionLimits.MayCachePreHandshake(_preInitMessages.Count, s_preHandshakeLimit)) {
                Logger.Warning("SessionActor {Id} ({Ip}) sent more than {Limit} messages before its handshake; closing.",
                    Logger.Args(SessionID, RemoteIp, s_preHandshakeLimit));
                _preInitMessages.Clear();
                Dispose();

                return;
            }

            _preInitMessages.Add(packet);

            Logger.Verbose("SessionActor {Id} cached message {MessageName} for later processing.",
                Logger.Args(SessionID, packet.GetType().Name));
                
            return;
        }

        // Apply session policy in packet order before forwarding to independently scheduled child services.
        if (packet is EnhancedClassicProtocol.Hello enhancementHello)
            MonstrologySession.Negotiate(enhancementHello.ProtocolVersion, enhancementHello.StrictClassic);

        // CLASSIC: an impossible move reaches no service; the client is put back (SessionActor.Movement.cs).
        if (packet is Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL.MSG_CLIENTMOVE clientMove && !AdmitClientMove(clientMove)) {
            return;
        }

        if (_dispatchTable.TryGetValue(packet.GetType(), out var handlers)) {
            foreach (var handler in handlers) {
                handler.Forward(packet);
            }
            return;
        }

        Unhandled(packet);
    }

    private void SendPreDisposeToServices() {
        // Iterate through each service and send them a pre-dispose message. This lets a service gracefully handle
        // the dispose in the case that it requires another service to still be active.
        foreach (var (actorRef, type) in _serviceOrder) {
            // If the service doesn't have a pre-dispose message handler, we'll just skip it.
            if (!MessageHandlerTable.HandlersOf(type).ContainsKey(typeof(SERVICE_101_PROTOCOL.MSG_PREDISPOSE))) {
                continue;
            }

            // Await a reply. This is a blocking call to ensure that the service gracefully disposes.
            try {
                actorRef.Ask(new SERVICE_101_PROTOCOL.MSG_PREDISPOSE(), timeout: TimeSpan.FromSeconds(2)).Wait();
            }
            catch {
                continue;
            }
        }
    }

    private void SendDisposeToServices() {
        // Iterate through our services and send them a dispose message.
        foreach (var (actorRef, _) in _serviceOrder) {
            actorRef.Tell(new SERVICE_101_PROTOCOL.MSG_DISPOSE());
        }
    }
    
}
