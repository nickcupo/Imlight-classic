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
 * OPEN PVP CIRCLES
 * ========================================================================
 *
 * PURPOSE:
 * The old open PvP (owner ruling 2026-10-01) on a server-placed duel
 * circle (classic-data/pvp): the first wizard to walk in opens the circle;
 * each wizard takes the side of the half they walked in from (or the other
 * when it is full), up to four a side. Once both sides have a wizard a
 * countdown ([Classic] PvpCountdownSeconds) runs; a full circle, or
 * everyone saying ".pvp ready", starts it sooner. The fight is an ordinary
 * duel with m_bPVP and the sigil's PvP scalars; it ends when one side is
 * defeated. Nobody is sent home or loses anything: the defeated stay in the
 * arena with 1 health, which regenerates. A circle nobody fights in closes
 * after the data's lobby timeout.
 *
 * NOTE:
 * Ambient wizards (claude/perf-ambient) are seated through
 * ClassicPvp.Seat (MSG_PVPSEATREQUEST). In PvP a wizard's move selection is
 * shown to their own side only.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    private const string PVP_TICK_KEY = "PvpTick";

    private bool _pvp;
    private bool _pvpLobby;
    private DateTime _pvpLobbyOpenedUtc;
    private DateTime? _pvpCountdownStartedUtc;
    private readonly HashSet<IActorRef> _pvpReady = [];
    private readonly HashSet<IActorRef> _pvpResultRecipients = []; // CLASSIC: personal winner already sent before Ended.

    /// <summary>Wizards on each side (seated, whether or not the fight has started).</summary>
    private (int Side0, int Side1) PvpSeats()
        => (SubCircles?.Count(c => c is { Occupied: true, IsWizard: true } && c.SlotIndex < 4) ?? 0,
            SubCircles?.Count(c => c is { Occupied: true, IsWizard: true } && c.SlotIndex >= 4) ?? 0);

    private void PvpOnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        var inside = IsInRadius(playerObj, _combatSigilObjectInfo.m_radius);
        if (!inside) {
            _entitiesInRange.Remove(playerObj);

            return;
        }

        if (_entitiesInRange.ContainsKey(playerObj)) {
            return;
        }

        _entitiesInRange.Add(playerObj, playerActor);
        if (!ClassicPvp.Enabled || playerWizard is null || playerWizard.IsInDuel || ServerAdmin.BlockNewDuels) {
            return;
        }

        if (!_isActive) {
            PvpOpenLobby();
        }

        if (_pvpLobby) {
            PvpSeat(playerActor, playerObj, -1);
        }
    }

    private void PvpOpenLobby() {
        Duel = CreateDuelWithDefaults();
        SubCircles = CreateDuelActorSubCircles(_sigilTemplate);
        CombatResolver = new Combat.CombatResolver(Duel, SubCircles);
        DuelSeed = CombatRng.NewDuelSeed();
        _rng = CombatRng.Stream(DuelSeed, CombatRng.DuelStream);
        _randomFirstTeam = DetermineFirstTeam();
        Duel.m_firstTeamToAct = (int) _randomFirstTeam;
        Logger.Information("[COMBAT-SEED] Duel {0} | seed {1} (open PvP)", Logger.Args(Duel.m_duelID.Full, DuelSeed));

        _isActive = true;
        _pvpLobby = true;
        _pvpLobbyOpenedUtc = DateTime.UtcNow;
        _pvpCountdownStartedUtc = null;
        _pvpReady.Clear();
        _startedUtc = DateTime.UtcNow;
        _renderComponent?.Enable();
        if (_serializer.Serialize(GetClientBehaviorInstance(), _combatParticipantFlags, out var duelData)) {
            ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DUEL { Data = duelData });
        }

        Timers.StartPeriodicTimer(PVP_TICK_KEY, new CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOUNTDOWN(), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        Logger.Information("Duel {0} | open PvP circle {1} opened.", Logger.Args(Duel.m_duelID.Full, _combatSigilObjectInfo.m_zoneTag));
        PvpPublish();
    }

    /// <summary>Seats a wizard on <paramref name="side"/> (-1: the half they stand in). Returns false when it cannot.</summary>
    private bool PvpSeat(IActorRef actor, CoreObject participant, int side) {
        if (!_pvpLobby || SubCircles.Any(c => c.Occupied && (c.ParticipantActor == actor || ReferenceEquals(c.ParticipantObject, participant)))) {
            return false;
        }

        var (seated0, seated1) = PvpSeats();
        if (side is not (0 or 1)) {
            side = ClassicPvp.Config?.SideRule == OpenPvpRules.SideRuleAlternate
                ? OpenPvpRules.ChooseSideByArrival(seated0, seated1)
                : OpenPvpRules.ChooseSide(DistanceToSide(participant, 0), DistanceToSide(participant, 1), seated0, seated1);
        }
        else if ((side == 0 ? seated0 : seated1) >= OpenPvpRules.SideSize) {
            side = 1 - side;
            if ((side == 0 ? seated0 : seated1) >= OpenPvpRules.SideSize) {
                side = -1;
            }
        }

        var slot = side < 0 ? null : SubCircles.FirstOrDefault(c => !c.Occupied && (side == 0 ? c.SlotIndex < 4 : c.SlotIndex >= 4));
        if (slot is null) {
            PvpNote("This duel circle is full.");

            return false;
        }

        if (!AssignParticipantToSubCircle(slot, actor, participant)) return false;
        var (now0, now1) = PvpSeats();
        PvpNote(_arena
            ? $"You are on side {side + 1} ({now0} v {now1}). The match starts when everyone is here."
            : now0 > 0 && now1 > 0
            ? $"You joined side {side + 1} ({now0} v {now1}). The duel starts soon; say .pvp ready to start sooner, .pvp leave to step out."
            : $"You joined side {side + 1}. Waiting for a wizard on the other side; .pvp leave to step out.");
        Logger.Information("Duel {0} | open PvP: {1} joined side {2} ({3} v {4}).",
            Logger.Args(Duel.m_duelID.Full, participant.m_globalID.Full, side + 1, now0, now1));
        PvpPublish();

        return true;
    }

    private double DistanceToSide(CoreObject participant, int side) {
        var slots = SubCircles.Where(c => side == 0 ? c.SlotIndex < 4 : c.SlotIndex >= 4).ToList();
        var x = slots.Average(c => c.WorldPosition.X);
        var y = slots.Average(c => c.WorldPosition.Y);
        var dx = participant.m_location.X - x;
        var dy = participant.m_location.Y - y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOUNTDOWN))]
    private void ReceivePvpCountdown(CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOUNTDOWN message) {
        if (!_pvp || !_isActive || !_pvpLobby) {
            Timers.Cancel(PVP_TICK_KEY);

            return;
        }

        if (_arena) {
            ArenaTick(); // CLASSIC: an arena match circle waits for its own wizards

            return;
        }

        var now = DateTime.UtcNow;
        var (seated0, seated1) = PvpSeats();
        if (seated0 + seated1 == 0) {
            PvpClose("nobody is seated");

            return;
        }

        var countdown = TimeSpan.FromSeconds(ClassicSettings.PvpCountdownSeconds);
        var over = _pvpCountdownStartedUtc is { } started && now - started >= countdown;
        var ready = SubCircles.Count(c => c is { Occupied: true, IsWizard: true } && _pvpReady.Contains(c.ParticipantActor));
        switch (OpenPvpRules.Decide(seated0, seated1, ready, over)) {
            case OpenPvpStart.Wait:
                _pvpCountdownStartedUtc = null;
                if (now - _pvpLobbyOpenedUtc > TimeSpan.FromSeconds(ClassicPvp.LobbyTimeoutSeconds)) {
                    PvpClose("no one came for the other side");
                }

                break;
            case OpenPvpStart.CountDown:
                if (_pvpCountdownStartedUtc is null) {
                    _pvpCountdownStartedUtc = now;
                    PvpTellSeated($"Both sides have a wizard: the duel starts in {(int) countdown.TotalSeconds} seconds.");
                }

                break;
            case OpenPvpStart.Start:
                PvpBegin();
                break;
        }
    }

    private void PvpBegin() {
        _pvpLobby = false;
        Timers.Cancel(PVP_TICK_KEY);
        var (seated0, seated1) = PvpSeats();
        Logger.Information("Duel {0} | open PvP starts, {1} v {2}.", Logger.Args(Duel.m_duelID.Full, seated0, seated1));
        PvpTellSeated($"Duel! {seated0} v {seated1}.");
        Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());
        PvpPublish();
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPSEATREQUEST))]
    private void ReceivePvpSeatRequest(CLASSIC_FEATURES_PROTOCOL.MSG_PVPSEATREQUEST message) {
        if (!_pvp || !ClassicPvp.Enabled || message.ParticipantActor is null || message.ParticipantObject is null) {
            return;
        }

        if (!_isActive) {
            PvpOpenLobby();
        }

        var side = message.Side;
        if (side is not (0 or 1)) {
            var (seated0, seated1) = PvpSeats();
            side = seated0 <= seated1 ? 0 : 1;
        }

        PvpSeat(message.ParticipantActor, message.ParticipantObject, side);
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND))]
    private void ReceivePvpCommand(CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND message) {
        var seat = SubCircles?.FirstOrDefault(c => c is { Occupied: true } && c.ParticipantActor == message.Actor);
        if (!_pvp || !_isActive || seat is null) {
            PvpNote("You are not in an open PvP circle.");

            return;
        }

        if (message.Leave) {
            if (_pvpLobby) {
                PvpReleaseSeat(seat, won: false, fought: false);
                if (PvpSeats() == (0, 0)) {
                    PvpClose("everyone left");
                }
            }
            else {
                HandleFleeAction(seat);
            }

            return;
        }

        _pvpReady.Add(message.Actor);
        PvpTellSeated("A wizard is ready.");
    }

    /// <summary>Takes a wizard out of the circle with no penalty: they stay where they are, at least at 1 health.</summary>
    private void PvpReleaseSeat(CombatDuelSubCircle circle, bool won, bool fought) {
        var actor = circle.ParticipantActor;
        var participantId = circle.ParticipantObject?.m_globalID ?? 0;
        var wasAdded = circle.AddedToDuel;
        // CLASSIC: the native ending phase closes the planning hand. Once the seat is cleared, the final
        // DuelBroadcast cannot reach this wizard, and native EndDuel sees a duel we no longer participate in.
        // End only this client's combat before release; teammates may still be fighting in the shared duel.
        if (wasAdded && circle.Occupied && actor is not null && !circle.Disconnected) {
            // CLASSIC: deliver the native winner through the same actor route before its Ended phase.
            // A team conceder loses personally now even if their remaining teammates later win.
            if (fought) {
                var ownTeam = circle.OccupiedTeam;
                var winningTeam = won ? ownTeam : ownTeam == CombatTeam.Monster ? CombatTeam.Player : CombatTeam.Monster;
                actor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT {
                    DuelID = SigilId, WinningTeam = (int) winningTeam,
                });
                _pvpResultRecipients.Add(actor);
            }
            SendCombatPhase((byte) kDuelPhase.kPhase_Ended, actor);
        }

        if (circle.Disconnected) {
            if (circle._wizard is { } away && away.GameStats.m_currentHitpoints <= 0) {
                away.UpdateHealth(1);
            }

            ActiveDuels.Release(circle.HeldCharacterId);
            Timers.Cancel(REJOIN_TIMER_PREFIX + circle.HeldCharacterId);
        }

        actor?.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE { Won = won, Fought = fought });
        _pvpReady.Remove(actor);
        circle.RemoveParticipant();
        if (participantId != 0) {
            if (wasAdded) {
                ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE { DuelID = SigilId, ParticipantID = participantId });
            }

            ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = participantId, State = (uint) NPCStates.Idle });
        }
    }

    /// <summary>Ends a started PvP fight: one side is defeated (or gone). Nobody is penalized.</summary>
    private void PvpEndDuel() {
        var side1Won = AliveAndInDuelCreatureCount <= 0;   // slots 0-3 (team Monster) are down: side 2 won
        var winning = side1Won ? CombatTeam.Player : CombatTeam.Monster;
        FinishMonstrologyDuel(false);
        _tutorialDirector.OnDuelEnded();
        Logger.Information("Duel {0} | open PvP over: side {1} won.", Logger.Args(Duel.m_duelID.Full, side1Won ? 2 : 1));

        foreach (var seat in SubCircles.Where(c => c is { Occupied: true, IsWizard: true }).ToList()) {
            if (seat.Disconnected) {
                ArenaMarkFled(seat); // CLASSIC: still away when an arena match ends: a loss
            }

            PvpReleaseSeat(seat, won: seat.OccupiedTeam == winning, fought: true);
        }

        ArenaReport(side1Won ? 1 : 0); // CLASSIC: slots 1-4 are side 1 (index 0)

        // CLASSIC: preserve the public winner for observers without re-sending a contrary shared result to
        // a wizard who already conceded. Copy the recipients: cleanup runs before asynchronous zone fanout.
        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Selfless = false, Sender = Self,
            Message = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT { DuelID = SigilId, WinningTeam = (int) winning },
            ExcludedRecipients = _pvpResultRecipients.ToArray(),
        });
        Duel.m_duelPhase = kDuelPhase.kPhase_Ended;
        SendCombatPhase((byte) Duel.m_duelPhase);
        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId });
        PvpCleanUp();
    }

    /// <summary>Closes a circle whose fight never started.</summary>
    private void PvpClose(string reason) {
        Logger.Information("Duel {0} | open PvP circle closed: {1}.", Logger.Args(Duel.m_duelID.Full, reason));
        ArenaReport(-1); // CLASSIC: an arena match that never fought (no-op when already reported)
        foreach (var seat in SubCircles.Where(c => c is { Occupied: true }).ToList()) {
            PvpNote($"The duel circle closed: {reason}.");
            PvpReleaseSeat(seat, won: false, fought: false);
        }

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId });
        PvpCleanUp();
    }

    private void PvpCleanUp() {
        Timers.Cancel(PVP_TICK_KEY);
        _pvpLobby = false;
        _pvpCountdownStartedUtc = null;
        _pvpReady.Clear();
        _pvpResultRecipients.Clear();
        ClassicPvp.Forget(Entity.Zone?.ZonePath ?? "", _combatSigilObjectInfo?.m_zoneTag ?? "");

        // The circle stays in the zone for the next fight: the duel state goes, the entity does not. Wizards still
        // standing in it stay counted as inside, so a new fight needs them to step out and back in.
        var stillInside = _entitiesInRange.ToList();
        DespawnDuel();
        foreach (var (inside, actor) in stillInside) {
            _entitiesInRange[inside] = actor;
        }
    }

    // CLASSIC (owner, 2026-10-08): r806919 shows every non-modal MSG_SERVERMESSAGE as a "!" alert
    // (WizardGUIManager::HandleServerMessage), and the 2009 arena sent no such notices, so PvP status lines go to the
    // server log only. The circle, PvP window and duel phases already show the player what happens.
    private void PvpTellSeated(string text) => PvpNote(text);

    private void PvpNote(string text)
        => Logger.Debug("Duel {0} | PvP notice (log only): {1}", Logger.Args(Duel?.m_duelID.Full ?? 0UL, text));

    private void PvpPublish() {
        if (!_isActive) {
            return;
        }

        var (side0, side1) = PvpSeats();
        var ready = SubCircles.Count(c => c is { Occupied: true, IsWizard: true } && _pvpReady.Contains(c.ParticipantActor));
        ClassicPvp.Publish(new PvpCircleState(Entity.Zone?.ZonePath ?? "", _combatSigilObjectInfo.m_zoneTag, SigilId, ActorRef,
            _pvpLobby ? "waiting" : "fighting", side0, side1, ready));
        PublishActiveDuel();
    }

    /// <summary>The PvP version of the duel's numbers: the sigil's PvP scalars and m_bPVP.</summary>
    private void ApplyPvpDuelSettings(Duel duel) {
        duel.m_bPVP = true;
        duel.m_scalarDamage = _sigilTemplate.m_scalarDamagePvP;
        duel.m_scalarResist = _sigilTemplate.m_scalarResistPvP;
        duel.m_scalarPierce = _sigilTemplate.m_scalarPiercePvP;
        duel.m_damageLimit = _sigilTemplate.m_damageLimitPvP;
        duel.m_dK0 = _sigilTemplate.m_dK0PvP;
        duel.m_dN0 = _sigilTemplate.m_dN0PvP;
        duel.m_resistLimit = _sigilTemplate.m_resistLimitPvP;
        duel.m_rK0 = _sigilTemplate.m_rK0PvP;
        duel.m_rN0 = _sigilTemplate.m_rN0PvP;
    }

}
