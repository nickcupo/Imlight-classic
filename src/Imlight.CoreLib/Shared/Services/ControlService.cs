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
 */

using System;
using System.Diagnostics;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Net;
using Imlight.Common;
using Imlight.CoreLib.Patch;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Shared.Services;

internal class ControlService : MessageService, IHandshakeService {

    private readonly byte _keepAliveInterval = ConfigurationManager.Settings["Advanced.KeepAliveInterval"].AsByte();
    private readonly byte _keepAliveRspWaitTime = ConfigurationManager.Settings["Advanced.KeepAliveRspWaitTime"].AsByte();
    // CLASSIC: the first SessionAccept can take far longer than a heartbeat reply. Started with -L, the
    // client connects early in startup and answers only once its patcher window is done, which under
    // CrossOver/Rosetta easily exceeds the heartbeat wait. Advanced.SessionAcceptWaitTime, default 300 s.
    private readonly ushort _sessionAcceptWaitTime = ConfigurationManager.Settings["Advanced.SessionAcceptWaitTime"].AsUShort() is > 0 and var wait
        ? wait
        : (ushort) 300;

    // CLASSIC: a game-server connection is opened by a running client at attach time and answers at once, so it gets
    // [Advanced] GameSessionAcceptWaitTime (default 30 s) instead of the long login wait (H5).
    private static readonly int s_gameAcceptWait = Imlight.CoreLib.Auth.SecuritySettings.Int("Advanced.GameSessionAcceptWaitTime", 30);

    // CLASSIC: a login connection must authenticate within [Login Server] LoginAuthTimeout seconds (default 30) of its
    // SessionAccept. The client accepts only when the player presses Login (or at once with a launcher key), so the
    // long accept wait above is idle-at-the-login-screen time, and this one is not (H5).
    private static readonly int s_loginAuthTimeout = Imlight.CoreLib.Auth.SecuritySettings.Int("Login Server.LoginAuthTimeout", 30);

    private bool _sessionValid;
    private readonly Stopwatch _responseStopwatch;
    private readonly HeartbeatPolicy _heartbeat;
    private bool _isWaitingForHeartbeatResponse;
    private bool _isInGameServer;
    private TimeSpan _heartbeatSentAt;

    public ControlService(SessionActor parentActor) : base(parentActor) {
        this._responseStopwatch = new Stopwatch();
        // CLASSIC: a client busy loading a zone gets as long to answer as one busy starting up.
        this._heartbeat = new HeartbeatPolicy(_keepAliveInterval, _keepAliveRspWaitTime, _sessionAcceptWaitTime);

        SendSessionOffer();
    }

    protected static Props Props(SessionActor parentActor) 
        => Akka.Actor.Props.Create(() => new ControlService(parentActor));

    protected override void ConfigureReceivers() {
        // These are sent from self on interval to remind the actor of the session heartbeat.
        Receive<string>(s => s == "KeepAliveHeartbeat", x => SendHeartbeat());
        Receive<string>(s => s == "KeepAliveEndTimes", x => ReceiveKeepAliveEndTimes());
        Receive<string>(s => s == "SessionAcceptTimer", s => SessionAcceptTimer());
        Receive<string>(s => s == "LoginAuthDeadline", s => LoginAuthDeadline());

        base.ConfigureReceivers();
    }

    private void SendSessionOffer() {
        // Ask the game client for a session.
        var currentUnixTimestamp = (uint) (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        var timestampUpper = (int) (currentUnixTimestamp >> 32);
        var timestampLower = (int) (currentUnixTimestamp & uint.MaxValue);
        var millisecondsIntoCurrentSecond = (uint) (DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 1000);

        var offer = new ControlMessageProtocol.SessionOffer() {
            SessionId = SessionActor.SessionID,
            TimestampUpper = timestampUpper,
            TimestampLower = timestampLower,
            Milliseconds = millisecondsIntoCurrentSecond,
        };

        // Set SessionActor variables.
        SessionActor.OfferTime = currentUnixTimestamp;
        SessionActor.OfferMillisecondsIntoSecond = millisecondsIntoCurrentSecond;

        SendToSocket(offer);

        // Start the stopwatch so we can later get RTT (ping).
        _responseStopwatch.Restart();

        // Send a message to ourselves to check if we've received a response.
        var timer = TimeSpan.FromSeconds(AcceptWait(_sessionAcceptWaitTime, s_gameAcceptWait,
            SessionActor.ServerRef is { } server && Game.GameServer.Refs.ContainsKey(server))); // CLASSIC: was _keepAliveRspWaitTime
        Timers.StartSingleTimer("SessionAcceptTimer", "SessionAcceptTimer", timer);
    }

    /// <summary>CLASSIC: the first SessionAccept's wait: the login wait, or the shorter game wait for a game server.</summary>
    internal static int AcceptWait(int loginWait, int gameWait, bool gameServer)
        => gameServer && gameWait > 0 && gameWait < loginWait ? Math.Max(5, gameWait) : loginWait;

    [MessageHandler(typeof(ControlMessageProtocol.SessionAccept))]
    private void ReceiveSessionAccept(ControlMessageProtocol.SessionAccept message) {
        // The game client approves of the agreed upon session.
        _responseStopwatch.Stop();
        if (message.SessionId != SessionActor.SessionID) {
            throw new Exception($"SessionActor [{SessionActor.SessionID}] misaligned Session ID.");
        }

        // Set local variables.
        _sessionValid = true;
        _isWaitingForHeartbeatResponse = false;

        // The session is now valid. For optimization purposes, our parent SessionActor doesn't load
        // all the services on creation. Instead, we wait for the session to be valid.
        // We need to now tell our SessionActor that the session is created, and to grab the rest of its services.
        var msg = new SERVICE_101_PROTOCOL.MSG_GETALLSERVICES();
        SessionActor.ActorRef.Tell(msg);
        SessionActor
            .ActorRef
            .Tell(new SERVER_100_PROTOCOL.MSG_PING() { Ping = _responseStopwatch.ElapsedMilliseconds });

        // Once the session is created, we need to send a heartbeat to keep it active.
        // To do that. we'll have this actor send a message to itself on interval to check on the heartbeat.
        Timers.Cancel("SessionAcceptTimer"); // CLASSIC: was a periodic SessionAcceptTimer.
        if (s_loginAuthTimeout > 0 && SessionActor.ServerRef is { } server && server.Equals(Login.LoginServer.Instance)) {
            Timers.StartSingleTimer("LoginAuthDeadline", "LoginAuthDeadline", TimeSpan.FromSeconds(Math.Max(5, s_loginAuthTimeout)));
        }
        // CLASSIC: a launcher's patch connection may idle without answering, so it gets no heartbeat, as before.
        if (_heartbeat.IsEnabled && !SessionActor.ServerRef.Equals(PatchServer.Instance)) {
            Timers.StartPeriodicTimer("KeepAliveHeartbeat", "KeepAliveHeartbeat", _heartbeat.Interval, _heartbeat.Interval);
        }

        _responseStopwatch.Reset();
    }

    [MessageHandler(typeof(ControlMessageProtocol.KeepAlive))]
    private void ReceiveKeepAlive(ControlMessageProtocol.KeepAlive message) {
        if (message.SessionId != SessionActor.SessionID) {
            throw new Exception($"SessionActor [{SessionActor.SessionID}] misaligned Session ID.");
        }

        var millisecondsIntoCurrentSecond = (ushort) (DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 1000);
        var rsp = new ControlMessageProtocol.KeepAliveResponse() {
            SessionId = SessionActor.SessionID,
            Milliseconds = millisecondsIntoCurrentSecond,
            ElapsedSessionTime = message.ElapsedSessionTime,
        };

        SendToSocket(rsp);
    }

    [MessageHandler(typeof(ControlMessageProtocol.KeepAliveResponse))]
    private void ReceiveKeepAliveRsp(ControlMessageProtocol.KeepAliveResponse message) {
        _responseStopwatch.Reset();
        _isWaitingForHeartbeatResponse = false;

        SessionActor
            .ActorRef
            .Tell(new SERVER_100_PROTOCOL.MSG_PING() { Ping = _responseStopwatch.ElapsedMilliseconds });
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_OPCODE_HALT))]
    private void ReceiveHalt(SERVICE_101_PROTOCOL.MSG_OPCODE_HALT message) => _isInGameServer = true;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_OPCODE_RESUME))]
    private void ReceiveResume(SERVICE_101_PROTOCOL.MSG_OPCODE_RESUME message) => _isInGameServer = false;

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PRELOGIN))]
    private void ReceivePreLogin(ZONE_102_PROTOCOL.MSG_PRELOGIN message) {
        // CLASSIC: AttachService sends it just before MSG_LOGINCOMPLETE, when the client starts loading the zone.
        _heartbeat.ZoneLoadStarted(HeartbeatPolicy.Clock);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENTMOVE))]
    private void ReceiveClientMove(GAME_5_PROTOCOL.MSG_CLIENTMOVE message) => _heartbeat.ClientMoved(); // CLASSIC

    private void LoginAuthDeadline() {
        if (SessionActor.GetAssociatedAccount() is not null) {
            return;
        }

        Logger.Information("Login session {0} ({1}) did not log in within {2} s; closing.",
            Logger.Args(SessionActor.SessionID, SessionActor.RemoteIp, s_loginAuthTimeout));
        CloseSession();
    }

    private void SendHeartbeat() {
        if (!_sessionValid) {
            Logger.Error("{Name} {SessionID} tried to send heartbeat to an invalid session",
                Logger.Args(nameof(SessionActor), SessionActor.SessionID));
            CloseSession();
            return;
        }

        // CLASSIC: restarting a pending check's timer would postpone it, forever when the wait is not below the interval.
        if (Timers.IsTimerActive("KeepAliveEndTimes")) {
            return;
        }

        // We're going to send a heartbeat to our connected session.
        // If we don't receive a response for `KEEP_ALIVE_RSP_WAIT_TIME` time, we'll drop the session.
        _isWaitingForHeartbeatResponse = true;
        _heartbeatSentAt = HeartbeatPolicy.Clock; // CLASSIC

        var keepAlive = new ControlMessageProtocol.KeepAliveServer() {
            SessionId = SessionActor.SessionID,
            Milliseconds = (uint) 0,
        };

        SendToSocket(keepAlive);

        // Send message to self after x seconds to remind CommunicationActor to check
        // the status of the KeepAlive.
        var reminderTime = TimeSpan.FromSeconds(_keepAliveRspWaitTime);
        Timers.StartSingleTimer("KeepAliveEndTimes", "KeepAliveEndTimes", reminderTime);

        _responseStopwatch.Start();
    }

    private void ReceiveKeepAliveEndTimes() {
        if (!_isWaitingForHeartbeatResponse || _isInGameServer) {
            return;
        }

        // CLASSIC: any frame from the client answers, and a client loading a zone may be silent for a while.
        var now = HeartbeatPolicy.Clock;
        var lastHeardAt = SessionActor.LastPacketReceivedAt;
        var verdict = _heartbeat.Judge(_heartbeatSentAt, lastHeardAt, now);
        if (verdict == HeartbeatVerdict.Alive) {
            _isWaitingForHeartbeatResponse = false;

            return;
        }
        if (verdict == HeartbeatVerdict.Loading) {
            Logger.Debug("SessionActor {SessionID} is silent while loading a zone; the next heartbeat checks again.",
                Logger.Args(SessionActor.SessionID));

            return;
        }

        // A heartbeat-response timeout is a real drop the player sees as "connection lost"; it needs to be
        // visible in the log. It only fires outside the game server (_isInGameServer guards it above).
        Logger.Warning("SessionActor {SessionID} heartbeat response timed out after {Silence}s of silence; closing session.",
            Logger.Args(SessionActor.SessionID, (int) (now - lastHeardAt).TotalSeconds)); // CLASSIC: was _keepAliveRspWaitTime.
        CloseSession();
    }

    private void SessionAcceptTimer() {
        if (_sessionValid) {
            return;
        }

        // This is the initial-handshake timeout; the drop players see as "connection lost" on game-world
        // entry / character switch. It needs to be visible in the log so it can be correlated with
        // concurrent zone loads.
        Logger.Warning("SessionActor {SessionID} did not return a SessionAccept within {Wait}s; closing session " +
                  "(initial-handshake timeout).", Logger.Args(SessionActor.SessionID, _sessionAcceptWaitTime));
        CloseSession();
    }

}
