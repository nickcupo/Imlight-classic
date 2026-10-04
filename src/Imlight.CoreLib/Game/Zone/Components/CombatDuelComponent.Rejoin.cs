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
 * COMBAT REJOIN AND CLEAN DUEL ENDINGS
 * ========================================================================
 *
 * PURPOSE:
 * A wizard whose client drops mid-fight keeps their seat for a while
 * ([Classic] CombatRejoinSeconds): the seat passes each round, a duel whose
 * every wizard has dropped waits instead of playing on, and logging back in
 * puts the wizard back in the same seat. A duel that ends without the
 * creatures' defeat (the wizards lost, fled, dropped for good) leaves the
 * creatures alive at full health instead of deleting them, so a dungeon is
 * never left without its guards or boss; a party loss in an instanced zone
 * resets that instance once it is empty.
 *
 * NOTE:
 * The rejoining client gets the duel the way a wizard walking into a running
 * fight does: the sigil's duel behavior on zone entry, MSG_DUEL, its slot
 * (MSG_AGGRO), then the next planning phase's hand, pips and health.
 * Verified with the headless client; the official client is unverified.
 * A wizard away when the fight ends gets no rewards; one defeated while
 * away comes back at the world's commons with 1 health.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    private const string REJOIN_TIMER_PREFIX = "Rejoin_";

    // True while every wizard in the duel has dropped: no new round starts until one is back or the holds run out.
    private bool _waitingForRejoin;
    private DateTime _startedUtc;

    /// <summary>Holds a dropped wizard's seat if the rejoin window is on; false when they should flee instead.</summary>
    private bool TryHoldSeat(CombatDuelSubCircle circle) {
        var seconds = ClassicSettings.CombatRejoinSeconds;
        if (!_isActive || seconds <= 0 || !circle.IsWizard || (_pvp && _pvpLobby)
                || circle._wizard is null || IsScriptedDuel()) {
            return false;
        }

        var now = DateTime.UtcNow;
        circle.HoldSeat(now);
        DisableOwnedMinionControl(circle.ParticipantObject);
        var characterId = circle.HeldCharacterId;
        ActiveDuels.Hold(new HeldSeat(characterId, Entity.Zone?.ZonePath ?? "", Entity.Zone?.InstanceOwnerId ?? 0,
            now.AddSeconds(seconds)));
        Timers.StartSingleTimer(REJOIN_TIMER_PREFIX + characterId,
            new CLASSIC_FEATURES_PROTOCOL.MSG_REJOINEXPIRED { CharacterId = characterId }, TimeSpan.FromSeconds(seconds));

        Logger.Information("Duel {0} | Slot {1} | wizard {2} dropped; seat held {3} s.",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, characterId, seconds));

        // A move already queued this round stands; otherwise the seat passes.
        if (Duel.m_duelPhase == kDuelPhase.kPhase_Planning && CombatResolver.GetQueuedAction(circle) is null) {
            CombatResolver.AddCombatMove(CombatMoveType.Pass, circle, null, null);
            ReevaluateOwnedMinionPlanning();
        }

        PublishActiveDuel();

        return true;
    }

    /// <summary>Queues a pass for every held seat at the start of planning.</summary>
    private void PassHeldSeats() {
        foreach (var circle in SubCircles.Where(circle => circle is { Occupied: true, Disconnected: true, IsAlive: true })) {
            CombatResolver.AddCombatMove(CombatMoveType.Pass, circle, null, null);
        }
    }

    /// <summary>True when the next round must wait: wizards are seated, all of them dropped.</summary>
    private bool ShouldWaitForRejoin() {
        var wizards = SubCircles.Where(circle => circle is { Occupied: true, IsWizard: true } && circle.IsAlive).ToList();

        return wizards.Count > 0 && wizards.All(circle => circle.Disconnected);
    }

    /// <summary>
    /// True (and the wait is over) when the duel was waiting for dropped wizards but a connected wizard is now seated:
    /// one who walked into the circle. Without this the duel kept waiting with the newcomer stuck in it, and every
    /// newcomer who gave up and logged out added another held seat (the "zombie duel").
    /// </summary>
    private bool TakeResumeAfterWaiting() {
        if (!_waitingForRejoin || ShouldWaitForRejoin()) {
            return false;
        }

        _waitingForRejoin = false;

        return true;
    }

    /// <summary>Starts the next round of a waiting duel that a connected wizard has joined.</summary>
    private void ResumeIfNoLongerWaiting() {
        if (TakeResumeAfterWaiting()) {
            Logger.Information("Duel {0} | a wizard joined; play resumes.", Logger.Args(Duel.m_duelID.Full));
            Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());
        }
    }

    /// <summary>
    /// Puts a wizard who logged back in into the seat held for them. Returns false when no seat is held for them.
    /// </summary>
    private bool TryRejoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is null || SubCircles is null) {
            return false;
        }

        var circle = SubCircles.FirstOrDefault(circle => circle is { Occupied: true, Disconnected: true }
            && circle.HeldCharacterId == playerWizard.CharId);
        if (circle is null) {
            return false;
        }

        Timers.Cancel(REJOIN_TIMER_PREFIX + playerWizard.CharId);
        ActiveDuels.Release(playerWizard.CharId);
        circle.RejoinSeat(playerActor, playerObj, playerWizard);

        // The session needs the duel actor (moves, flee, logout); the client needs the duel and its slot.
        playerActor.Tell(new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {
            DuelActor = ActorRef,
            Duel = this,
            SubCircle = circle,
            SlotPosition = circle.WorldPosition,
            SlotOrientation = circle.WorldRotation,
        });
        if (_serializer.Serialize(GetClientBehaviorInstance(), _combatParticipantFlags, out var duelData)) {
            playerActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DUEL { Data = duelData });
        }

        // Everyone already in the fight, the wizard's own seat included, as a client present all along saw them added.
        foreach (var seated in SubCircles.Where(seated => seated is { Occupied: true, AddedToDuel: true })) {
            if (_serializer.Serialize(seated.CombatParticipant, _combatParticipantFlags, out var participantData)) {
                playerActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATADD {
                    DuelID = SigilId,
                    ParticipantData = participantData,
                });
            }
        }

        playerObj.m_location = new Vector3(circle.WorldPosition.X, circle.WorldPosition.Y, circle.WorldPosition.Z);
        playerObj.m_orientation = new Vector3(0, 0, circle.WorldRotation);
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = playerObj.m_globalID,
            State = StringHash.Compute("Sigil"),
        });
        ZoneBroadcast(new WIZARD_12_PROTOCOL.MSG_AGGRO {
            GlobalID = playerObj.m_globalID,
            LocX = circle.WorldPosition.X,
            LocY = circle.WorldPosition.Y,
            LocZ = circle.WorldPosition.Z,
            Yaw = circle.WorldRotation,
            SigilGID = SigilId,
        });
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = playerObj.m_globalID,
            State = StringHash.Compute("Stationary"),
        });

        Logger.Information("Duel {0} | Slot {1} | wizard {2} rejoined.",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, playerWizard.CharId));

        // Mid-planning: the wizard gets this round's hand now and may still change the queued pass.
        if (Duel.m_duelPhase == kDuelPhase.kPhase_Planning) {
            SendCurrentCombatHand(circle);
            SendCombatPips();
            SendCombatHealth();
            playerActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI { DuelID = SigilId });
        }

        if (_waitingForRejoin) {
            _waitingForRejoin = false;
            Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());
        }

        PublishActiveDuel();

        return true;
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_REJOINEXPIRED))]
    private void ReceiveRejoinExpired(CLASSIC_FEATURES_PROTOCOL.MSG_REJOINEXPIRED message) {
        ActiveDuels.Release(message.CharacterId);
        if (!_isActive || SubCircles is null) {
            return;
        }

        var circle = SubCircles.FirstOrDefault(circle => circle is { Occupied: true, Disconnected: true }
            && circle.HeldCharacterId == message.CharacterId);
        if (circle is null) {
            return;
        }

        Logger.Information("Duel {0} | Slot {1} | wizard {2} did not come back; seat released.",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, message.CharacterId));
        ReleaseHeldSeat(circle);

        if (PlayerCount == 0) {
            AbandonDuel();
        }
        else if (AlivePlayerCount == 0) {
            EndDuel();
        }
        else if (TakeResumeAfterWaiting()) {
            Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());
        }
        else {
            PublishActiveDuel();
        }
    }

    /// <summary>
    /// Takes a held seat out of the duel. A wizard defeated while away comes back at the world's commons with
    /// 1 health, as a defeat in person would (owner ruling 2026-10-01).
    /// </summary>
    private void ReleaseHeldSeat(CombatDuelSubCircle circle) {
        if (_pvp) {
            PvpReleaseSeat(circle, won: false, fought: true); // CLASSIC: no defeat penalty in open PvP

            return;
        }

        var wizard = circle._wizard;
        var participantId = circle.ParticipantObject?.m_globalID ?? 0;
        if (circle.HeldCharacterId != 0) {
            Timers.Cancel(REJOIN_TIMER_PREFIX + circle.HeldCharacterId);
            ActiveDuels.Release(circle.HeldCharacterId);
        }

        if (wizard is not null && wizard.GameStats.m_currentHitpoints <= 0) {
            wizard.UpdateHealth(1);
            if (ClassicGate.HubFor(wizard.Zone) is { } hub) {
                wizard.SetZone(hub.Zone, hub.Zone);
                wizard.SetPersistentLocation(default);
            }
        }

        circle.RemoveParticipant();
        if (participantId != 0) {
            ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
                DuelID = SigilId,
                ParticipantID = participantId,
            });
        }
    }

    /// <summary>
    /// Ends a duel nobody is left to fight: held seats are released, creatures go back to the world at full health.
    /// </summary>
    private void AbandonDuel() {
        Logger.Information("Duel {0} | abandoned: no wizard is left; the creatures reset.", Logger.Args(Duel.m_duelID.Full));
        _waitingForRejoin = false;
        Timers.CancelAll();
        FinishMonstrologyDuel(false);
        _tutorialDirector.OnDuelEnded();
        foreach (var circle in SubCircles.Where(circle => circle is { Occupied: true, Disconnected: true })) {
            ReleaseHeldSeat(circle);
        }

        ResetCreatures();
        Duel.m_duelPhase = kDuelPhase.kPhase_Ended;
        SendCombatPhase((byte) Duel.m_duelPhase);
        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId });
        DespawnDuel();
        _isActive = false;
    }

    /// <summary>
    /// CLASSIC: the creatures of a duel the wizards did not win go back to the world at full health (2009: monsters
    /// that won stayed where they were; a dungeon kept its guards and boss). Minions are removed.
    /// </summary>
    private void ResetCreatures() {
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam != CombatTeam.Monster && !circle.IsSummonedMinion) {
                return;
            }

            if (circle.IsSummonedMinion) {
                circle.ParticipantActor?.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());

                return;
            }

            ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = circle.ParticipantObject.m_globalID,
                State = (uint) NPCStates.Idle,
            });
            circle.ParticipantActor?.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_COMBATRESET());
        });
    }

    /// <summary>Tells the zone a party lost here, so an instanced zone resets once it is empty.</summary>
    private void ReportPartyLost() => Entity.ZoneRef?.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_INSTANCEPARTYLOST());

    /// <summary>Publishes this duel to the server-wide view (safe restarts, dashboard).</summary>
    private void PublishActiveDuel() {
        if (!_isActive || Duel is null || SubCircles is null) {
            return;
        }

        var wizards = SubCircles.Count(circle => circle is { Occupied: true, IsSummonedMinion: false }
            && circle.ParticipantObject.m_templateID == 1);
        var creatures = SubCircles.Count(circle => circle is { Occupied: true, IsSummonedMinion: false }
            && circle.ParticipantObject.m_templateID != 1);
        var held = SubCircles.Count(circle => circle is { Occupied: true, Disconnected: true });
        var characters = SubCircles.Where(circle => circle is { Occupied: true, IsSummonedMinion: false })
            .Select(circle => circle.Disconnected ? circle.HeldCharacterId : circle._wizard?.CharId ?? 0)
            .Where(id => id != 0).ToList();
        ActiveDuels.Update(new ActiveDuelInfo(SigilId, Entity.Zone?.ZonePath ?? "", Duel.m_bPVP, wizards, creatures, held,
            _startedUtc, characters));
    }

}
