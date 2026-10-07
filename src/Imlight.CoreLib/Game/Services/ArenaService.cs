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
 * ARENA SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a wizard's side of the 2009 arena (owner ruling 2026-10-05): the
 * r806919 client's PvP window and status window messages go to the
 * matchmaker (Classic/Arena/ArenaMatchmaker), and the matchmaker's trips
 * and results come back here (into the arena, the Arena Tickets, the
 * result window, back to the arena hall).
 *
 * USAGE EXAMPLE (client messages, read from WizardGraphicalClient.exe r806919):
 * MSG_PVPINTENT Command 1 + TargetData (PvPMatchRequest): Create Match (Practice)
 * MSG_PVPINTENT Command 2 + MatchNameID: Quick Join
 * MSG_PVPINTENT Command 3 + TargetObject (team) + MatchID: Join a side
 * MSG_PVPINTENT Command 4 / 9: leave the match or queue
 * MSG_PVPINTENT Command 5: watch a match (not built; see the report)
 * MSG_PVPCONFIRM Confirm 1/0: Go to Arena / Decline
 * MSG_PVPUPDATEREQUEST InfoRequest (TournamentInfoRequest): the match list
 * MSG_PVP5THAGECANJOINMATCH: may this wizard Quick Join / Create
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal sealed class ArenaService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string RETURN_TIMER = "ArenaReturn";

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new ArenaService(parentActor));

    private ulong _charId;
    private ArenaKind _lastKind = ArenaKind.Practice;

    private ArenaMatchmaker Matchmaker {
        get {
            var made = ClassicArena.Matchmaker(Context.System);
            if (made is not null && GetActiveWizard() is { } wizard) {
                ServerArenaWorld.Register(wizard);
                _charId = wizard.CharId;
            }

            return made;
        }
    }

    // ------------------------------------------------------------ the guards and the window

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_ARENAKIOSK))]
    private void ReceiveKiosk(CLASSIC_FEATURES_PROTOCOL.MSG_ARENAKIOSK message) {
        if (Matchmaker is not { } arena) {
            return;
        }

        _lastKind = message.Ranked ? ArenaKind.Ranked : ArenaKind.Practice;
        arena.OpenKiosk(_charId, _lastKind, message.KioskGid);
    }

    // The client echoes MSG_PREPVPKIOSK back once it has checked its tournament files (PvPClientManager::MSG_PrePvPKiosk).
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK))]
    private void ReceivePreKioskEcho(WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK message)
        => Logger.Debug("Arena: kiosk echo, tournament {0}, patching {1}.", Logger.Args(message.TournamentNameID, message.Patching));

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCH))]
    private void ReceiveCanJoin(WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCH message) => Matchmaker?.CanJoin(_charId);

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PVPUPDATEREQUEST))]
    private void ReceiveUpdateRequest(WIZARD_12_PROTOCOL.MSG_PVPUPDATEREQUEST message) {
        if (Matchmaker is not { } arena) {
            return;
        }

        var request = ArenaMessages.Read<TournamentInfoRequest>(message.InfoRequest);
        var tournament = request?.m_tournamentNameID ?? 0;
        if (arena.KindOf(tournament) is null) {
            tournament = arena.TournamentId(_lastKind);
        }

        arena.List(_charId, tournament, request?.m_startingIndex ?? 0, request?.m_numberOfElements ?? 0,
            request?.m_qualifiedOnly ?? false, request?.m_qualifiedLevel ?? 0, request?.m_qualifiedRank ?? -1);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PVPINTENT))]
    private void ReceiveIntent(WIZARD_12_PROTOCOL.MSG_PVPINTENT message) {
        if (Matchmaker is not { } arena) {
            return;
        }

        var kind = arena.KindOf(message.TournamentNameID) ?? _lastKind;
        Logger.Information("Arena: {0} intent {1} (tournament {2}, match name {3}, match {4}, target {5}, data {6} bytes).",
            Logger.Args(_charId, message.Command, message.TournamentNameID, message.MatchNameID, message.MatchID,
                message.TargetObject, ((byte[]) message.TargetData)?.Length ?? 0));
        switch (message.Command) {
            case 1:
                Create(arena, kind, message);
                break;
            case 2:
                arena.QuickJoin(_charId, kind, message.MatchNameID);
                break;
            case 3:
                arena.Join(_charId, message.MatchID, message.TargetObject);
                break;
            case 4:
            case 9:
                arena.Leave(_charId);
                break;
            case 5:
                // CLASSIC: the existing Watch intent takes an onlooker to the match without assigning a combat seat.
                arena.Watch(_charId, message.MatchID);
                break;
            default:
                Logger.Information("Arena: intent {0} is not a 2009 action; ignored.", Logger.Args(message.Command));
                break;
        }
    }

    private void Create(ArenaMatchmaker arena, ArenaKind kind, WIZARD_12_PROTOCOL.MSG_PVPINTENT message) {
        var request = ArenaMessages.Read<PvPMatchRequest>(message.TargetData);
        if (request is null) {
            Logger.Warning("Arena: {0} sent a Create Match whose request does not decode ({1} bytes).",
                Logger.Args(_charId, ((byte[]) message.TargetData)?.Length ?? 0));
            SendToSocket(ArenaMessages.Error(ArenaErrors.NoSlots));

            return;
        }

        var requirements = request.m_joinQueueRequirements;
        var friendsOnly = (requirements?.m_friendsOnly ?? false) || (request.m_teams?.Any(t => t?.m_friendsOnly == true) ?? false);
        var matchName = request.m_matchNameID != 0 ? request.m_matchNameID : message.MatchNameID;
        Logger.Information("Arena: create request from {0}: match {1}, levels {2}-{3}, friends only {4}, {5} team(s), actor {6}.",
            Logger.Args(_charId, matchName, requirements?.m_minLevel ?? 0, requirements?.m_maxLevel ?? 0, friendsOnly,
                request.m_teams?.Count ?? 0, request.m_teams?.FirstOrDefault()?.m_actors?.FirstOrDefault()?.m_nActorID.Full ?? 0));
        arena.Create(_charId, kind, matchName, requirements?.m_minLevel ?? 0, requirements?.m_maxLevel ?? 0, friendsOnly);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PVPCONFIRM))]
    private void ReceiveConfirm(WIZARD_12_PROTOCOL.MSG_PVPCONFIRM message) {
        if (Matchmaker is not { } arena) {
            return;
        }

        if (message.Confirm != 0 && GetActiveWizard()?.IsInDuel == true) {
            InformGameClient("Finish your duel, then press Go to Arena.");

            return;
        }

        arena.Confirm(_charId, message.Confirm != 0);
    }

    // The status window's "expand search" box and the result window's ladder refresh: nothing to do in 2009.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_EXPANDPVPSEARCH))]
    private void ReceiveExpandSearch(WIZARD_12_PROTOCOL.MSG_EXPANDPVPSEARCH message) { }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_GETLADDER))]
    private void ReceiveGetLadder(GAME_5_PROTOCOL.MSG_GETLADDER message) { }

    // ------------------------------------------------------------ the matchmaker's trips and results

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_ARENATRAVEL))]
    private void ReceiveTravel(CLASSIC_FEATURES_PROTOCOL.MSG_ARENATRAVEL message) {
        _charId = GetActiveWizard()?.CharId ?? _charId;
        Logger.Information("Arena: {0} goes to {1} (instance {2}).", Logger.Args(_charId, message.Zone, message.RunId));
        Timers.Cancel(RETURN_TIMER);
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = message.Zone,
            DestinationLocation = message.Location,
            SendToClient = true,
            IsPrivate = message.RunId != 0,
            OwnerCharId = message.RunId,
            ResetInstance = false,
        });
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME))]
    private void ReceiveOutcome(CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME message) {
        var outcome = message.Outcome;
        var wizard = GetActiveWizard();
        if (message.Receipt is { } receipt) {
            if (!receipt.ApplyAward(() => outcome is not null && wizard is not null
                    && wizard.CharId == receipt.CharacterId && SaveOutcomeTickets(wizard, outcome.Tickets))) {
                return; // CLASSIC: the same receipt was already applied; never award twice.
            }
        }
        if (outcome is null || wizard is null) {
            return;
        }

        _charId = wizard.CharId;

        if (message.Receipt is null && !SaveOutcomeTickets(wizard, outcome.Tickets))
            throw new InvalidOperationException("The arena ticket outcome was not persisted.");

        SendToSocket(ArenaMessages.ArenaPoints(wizard.GameStats.m_currentArenaPoints));
        SendToSocket(ArenaMessages.PvpCurrency(wizard.GameStats.m_currentPvPCurrency));
        SendToSocket(outcome.Result);
        var verdict = outcome.NoContest ? "The match ended without a contest." : outcome.Won ? "You won the match!" : outcome.Fled ? "You fled the match, so it counts as a loss." : "You lost the match.";
        InformGameClient(outcome.Kind == ArenaKind.Ranked
            ? $"{verdict} Rating {outcome.RatingAfter} ({outcome.RatingAfter - outcome.RatingBefore:+#;-#;0}), rank {outcome.Rank}. "
              + $"+{outcome.Tickets} Arena Tickets."
            : $"{verdict} (Practice: no rank or tickets.)");
        Timers.StartSingleTimer(RETURN_TIMER, new CLASSIC_FEATURES_PROTOCOL.MSG_ARENARETURN {
            Zone = outcome.HallZone, Location = outcome.HallLocation,
        }, TimeSpan.FromSeconds(Math.Max(0, outcome.ReturnSeconds)));
    }

    internal static bool SaveOutcomeTickets(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, int tickets) {
        if (wizard is null || tickets < 0) return false;
        if (tickets == 0) return true;
        return WizardCollection.ChangeArenaTickets(wizard, tickets);
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_ARENARETURN))]
    private void ReceiveReturn(CLASSIC_FEATURES_PROTOCOL.MSG_ARENARETURN message) {
        if (message.DelaySeconds > 0) {
            Timers.StartSingleTimer(RETURN_TIMER, new CLASSIC_FEATURES_PROTOCOL.MSG_ARENARETURN {
                Zone = message.Zone, Location = message.Location,
            }, TimeSpan.FromSeconds(message.DelaySeconds));

            return;
        }

        var wizard = GetActiveWizard();
        if (wizard is null || string.Equals(wizard.Zone, message.Zone, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        if (wizard.IsInDuel) {
            Timers.StartSingleTimer(RETURN_TIMER, message, TimeSpan.FromSeconds(3));

            return;
        }

        Logger.Information("Arena: {0} returns to {1}.", Logger.Args(_charId, message.Zone));
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = message.Zone,
            DestinationLocation = message.Location,
            SendToClient = true,
            IsPrivate = false,
            OwnerCharId = 0,
        });
    }

}
