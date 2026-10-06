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
 * ARENA MATCH CIRCLES (2009 PRACTICE AND RANKED)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the duel circle the server places in each arena
 * (classic-data/pvp/arena-*.yaml) runs the match the matchmaker sent to
 * this arena instance (Classic/Arena/ArenaMatchmaker; the instance key is
 * the match's run id). Each wizard of the match is seated on their team's
 * side as they arrive (side 1 in slots 1-4, side 2 in slots 5-8); the
 * fight starts when all have arrived, or when the arrival time runs out
 * with someone on each side (a side nobody came for loses without a
 * fight). The fight is the open PvP duel (m_bPVP and the sigil's PvP
 * numbers). Its end goes back to the matchmaker: the winning side, and who
 * fled, dropped or never came (they lose).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    /// <summary>Seconds after a wizard arrives before they are seated (their client has loaded the arena).</summary>
    private const double ARENA_SEAT_DELAY_SECONDS = 2;

    private bool _arena;
    internal bool IsArenaPvp => _arena && _pvp; // CLASSIC: the separate arena AI never changes PvE helper decisions.
    private ArenaRun _arenaRun;
    private bool _arenaReported;
    private readonly HashSet<ulong> _arenaFled = [];
    private readonly Dictionary<IActorRef, ulong> _arenaOnlookers = [];
    private readonly Dictionary<ulong, (IActorRef Actor, CoreObject Object, DateTime ArrivedUtc)> _arenaWaiting = [];

    private ArenaRun ArenaRunHere => _arenaRun ??= ArenaMatchmaker.Instance?.Run(Entity.Zone?.InstanceOwnerId ?? 0);

    [MessageHandler(typeof(ArenaAmbientFailed))]
    private void ReceiveArenaAmbientFailed(ArenaAmbientFailed failure) {
        if (!_arena || _arenaReported || ArenaRunHere?.RunId != failure.RunId) return;
        ArenaReport(-1);
        if (_isActive) PvpClose("the arena match could not continue");
    }

    /// <summary>A wizard of this arena's match arrived (or moved): seat them on their team's side.</summary>
    private void ArenaOnPlayer(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is null || ArenaRunHere is not { } run || _arenaReported) {
            Logger.Debug("Arena circle in {0} (instance {1}): {2} arrived; no match here ({3}).",
                Logger.Args(Entity.Zone?.ZonePath, Entity.Zone?.InstanceOwnerId, playerWizard?.CharId,
                    _arenaReported ? "already reported" : "no run"));

            return;
        }

        var charId = playerWizard.CharId;
        if (!run.Side0.Contains(charId) && !run.Side1.Contains(charId)) {
            // CLASSIC: onlookers arriving after the opening broadcast need the current public duel snapshot.
            if (_isActive && ArenaMatchmaker.Instance?.IsSpectator(charId, run.MatchId) == true
                && _arenaOnlookers.TryAdd(playerActor, charId)) {
                if (_serializer.Serialize(GetClientBehaviorInstance(), _combatParticipantFlags, out var snapshot))
                    playerActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DUEL { Data = snapshot });
                foreach (var seated in SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true })) {
                    if (_serializer.Serialize(seated.CombatParticipant, _combatParticipantFlags, out var participantData))
                        playerActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATADD { DuelID = SigilId, ParticipantData = participantData });
                }
                SendCombatPhase((byte) Duel.m_duelPhase, playerActor);
            }
            return; // not one of this match's wizards
        }

        if (SubCircles?.Any(c => c is { Occupied: true } && c.ParticipantActor == playerActor) == true) {
            return;
        }

        if (!_isActive) {
            PvpOpenLobby();
            Logger.Information("Duel {0} | arena match {1} ({2}): waiting for {3} v {4}.",
                Logger.Args(Duel.m_duelID.Full, run.MatchId, run.Kind, run.Side0.Count, run.Side1.Count));
        }

        _arenaWaiting.TryAdd(charId, (playerActor, playerObj, DateTime.UtcNow));
    }

    /// <summary>The arena circle's clock (the open PvP tick): seat arrivals, start, or give up waiting.</summary>
    private void ArenaTick() {
        if (ArenaRunHere is not { } run || _arenaReported) {
            return;
        }
        if (ArenaMatchmaker.Instance?.Run(run.RunId) is null) {
            _arenaReported = true;
            if (_isActive) PvpClose("the arena match ended without a contest");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var (charId, waiting) in _arenaWaiting.Where(w => (now - w.Value.ArrivedUtc).TotalSeconds >= ARENA_SEAT_DELAY_SECONDS).ToList()) {
            _arenaWaiting.Remove(charId);
            var side = run.Side0.Contains(charId) ? 0 : 1;
            if (Classic.Ambient.AmbientWizards.IsAmbient(waiting.Actor)) {
                Classic.Ambient.AmbientWizards.PermitJoin(waiting.Actor, SigilId);
                Classic.Ambient.AmbientWizards.MarkSparring(waiting.Actor, SigilId);
            }
            if (!PvpSeat(waiting.Actor, waiting.Object, side)) {
                Logger.Warning("Duel {0} | arena: no seat for {1} on side {2}.", Logger.Args(Duel.m_duelID.Full, charId, side + 1));
            }
        }

        var seated = SeatedCharIds();
        var expected = run.Side0.Count + run.Side1.Count;
        var (seated0, seated1) = PvpSeats();
        if (seated.Count >= expected) {
            ArenaBegin(run);

            return;
        }

        if (now < run.ArrivalEndsUtc) {
            return;
        }

        // The arrival time is over: fight with who came; a side nobody came for loses without a fight.
        var absent = run.Side0.Concat(run.Side1).Where(c => !seated.Contains(c)).ToList();
        foreach (var missing in absent) {
            _arenaFled.Add(missing);
        }

        if (seated0 > 0 && seated1 > 0) {
            Logger.Information("Duel {0} | arena match {1}: {2} never came; fighting {3} v {4}.",
                Logger.Args(Duel.m_duelID.Full, run.MatchId, absent.Count, seated0, seated1));
            ArenaBegin(run);

            return;
        }

        var winner = seated0 > 0 ? 0 : seated1 > 0 ? 1 : -1;
        Logger.Information("Duel {0} | arena match {1}: nobody came for {2}; no fight.",
            Logger.Args(Duel.m_duelID.Full, run.MatchId, winner < 0 ? "either side" : $"side {2 - winner}"));
        ArenaReport(winner);
        PvpClose(winner < 0 ? "nobody came" : "the other side never came");
    }

    private void ArenaBegin(ArenaRun run) {
        ArenaMatchmaker.Instance?.Started(run.RunId);
        PvpBegin();
    }

    // CLASSIC: only the existing public phase/stats broadcasts reach onlookers; combat hands remain participant-local.
    private void ArenaOnlookerBroadcast(IMessage message) {
        if (!_arena || ArenaRunHere is not { } run) return;
        foreach (var (actor, charId) in _arenaOnlookers.ToList()) {
            if (ArenaMatchmaker.Instance?.IsSpectator(charId, run.MatchId) == true) actor.Tell(message);
            else _arenaOnlookers.Remove(actor);
        }
    }

    private HashSet<ulong> SeatedCharIds()
        => [.. SubCircles?.Where(c => c is { Occupied: true, IsWizard: true }).Select(c => c._wizard?.CharId ?? c.HeldCharacterId).Where(id => id != 0) ?? []];

    /// <summary>A seated wizard fled, dropped for good or left: they lose the match.</summary>
    private void ArenaMarkFled(CombatDuelSubCircle circle) {
        var charId = circle._wizard?.CharId ?? circle.HeldCharacterId;
        if (_arena && charId != 0) {
            _arenaFled.Add(charId);
            Logger.Information("Duel {0} | arena: {1} fled or dropped; it counts as a loss.", Logger.Args(Duel.m_duelID.Full, charId));
        }
    }

    /// <summary>The fight is over: tell the matchmaker once.</summary>
    private void ArenaReport(int winningSide) {
        if (!_arena || _arenaReported || ArenaRunHere is not { } run) {
            return;
        }

        _arenaReported = true;
        ArenaMatchmaker.Instance?.Finish(run.RunId, winningSide, [.. _arenaFled]);
    }

}
