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
 * CLASSIC (2026-10-10): not on arrival any more, but once the client is in
 * the zone (its MSG_CLIENTZONED, at least 2 s after arrival; 10 s without it;
 * Imlight.Classic.Rules.RejoinTiming). Until then the seat stays held.
 * Verified with the headless client; the official client is unverified.
 * A wizard away when the fight ends gets no rewards; one defeated while
 * away comes back at the world's commons with 1 health.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
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

    // CLASSIC: wizards who logged back in to a seat held here, waiting until their client is in the zone
    // (Imlight.Classic.Rules.RejoinTiming).
    private sealed record PendingRejoin(CoreObject Object, IActorRef Actor, Wizard Wizard, DateTime ArrivedUtc) {
        internal bool Zoned { get; set; }
    }

    private const string REJOIN_READY_TIMER_PREFIX = "RejoinReady_";
    private const string REJOIN_WAIT_TIMER_PREFIX = "RejoinWait_";
    private readonly Dictionary<ulong, PendingRejoin> _pendingRejoins = [];

    /// <summary>True when a seat is held here for <paramref name="playerWizard"/> (and the duel is still on).</summary>
    private bool HoldsSeatFor(Wizard playerWizard)
        => _isActive && playerWizard is not null && SubCircles is not null
            && SubCircles.Any(circle => circle is { Occupied: true, Disconnected: true }
                && circle.HeldCharacterId == playerWizard.CharId);

    /// <summary>
    /// CLASSIC: a wizard with a seat held here arrived in the zone. The seat stays held (it passes; a duel whose every
    /// wizard is away keeps waiting) until their client is in the zone; then they take it (TryRejoin). False when no
    /// seat is held for them.
    /// </summary>
    private bool QueueRejoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (!HoldsSeatFor(playerWizard) || playerActor is null) {
            return false;
        }

        var charId = playerWizard.CharId;
        var pending = new PendingRejoin(playerObj, playerActor, playerWizard, DateTime.UtcNow);
        _pendingRejoins[charId] = pending;
        pending.Zoned = ClientZoneSignals.Await(charId, playerActor, ActorRef);
        Timers.StartSingleTimer(REJOIN_READY_TIMER_PREFIX + charId,
            new CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY { CharacterId = charId }, RejoinTiming.ClientLoadDelay);
        Timers.StartSingleTimer(REJOIN_WAIT_TIMER_PREFIX + charId,
            new CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY { CharacterId = charId }, RejoinTiming.ZonedWait);
        Logger.Information("Duel {0} | wizard {1} is back; the held seat is theirs once their client is in the zone{2}.",
            Logger.Args(Duel?.m_duelID.Full ?? 0UL, charId, pending.Zoned ? " (it is)" : ""));

        return true;
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY))]
    private void ReceiveRejoinClientReady(CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY message) {
        if (!_pendingRejoins.TryGetValue(message.CharacterId, out var pending)) {
            return;
        }

        if (message.Zoned) {
            pending.Zoned = true;
        }

        if (!RejoinTiming.Due(pending.ArrivedUtc, pending.Zoned, DateTime.UtcNow)) {
            return; // the load delay or the wait timer brings it back
        }

        DropPendingRejoin(message.CharacterId);
        if (!HoldsSeatFor(pending.Wizard)) {
            return; // the hold ran out or the duel ended meanwhile: the wizard is simply in the zone
        }

        if (!TryRejoin(pending.Object, pending.Actor, pending.Wizard) && _arena
                && !(ElixirService.PreparesCombatSnapshots && HoldsSeatFor(pending.Wizard))) {
            ArenaOnPlayer(pending.Object, pending.Actor, pending.Wizard);
        }
    }

    private void DropPendingRejoin(ulong charId) {
        if (!_pendingRejoins.Remove(charId, out var pending)) {
            return;
        }

        Timers.Cancel(REJOIN_READY_TIMER_PREFIX + charId);
        Timers.Cancel(REJOIN_WAIT_TIMER_PREFIX + charId);
        ClientZoneSignals.Forget(charId, pending.Actor);
    }

    /// <summary>CLASSIC: a wizard waiting to take a held seat left the zone (dropped again): the seat stays held.</summary>
    private void ForgetPendingRejoins(IActorRef playerActor) {
        foreach (var charId in _pendingRejoins.Where(entry => Equals(entry.Value.Actor, playerActor))
                     .Select(entry => entry.Key).ToList()) {
            DropPendingRejoin(charId);
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

        if (!circle.TryRejoinSeat(playerActor, playerObj, playerWizard, out var elixirReceipt)) return false;
        // CLASSIC: failed preparation retains the held seat and its expiry; only an admitted alias releases them.
        Timers.Cancel(REJOIN_TIMER_PREFIX + playerWizard.CharId);
        ActiveDuels.Release(playerWizard.CharId);

        // The session needs the duel actor (moves, flee, logout); the client needs the duel and its slot.
        playerActor.Tell(new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {
            DuelActor = ActorRef,
            Duel = this,
            SubCircle = circle,
            SlotPosition = circle.WorldPosition,
            SlotOrientation = circle.WorldRotation,
            ElixirReceipt = elixirReceipt,
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
        ReleaseHeldSeat(circle, walkedAway: true);

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
    /// 1 health, as a defeat in person would (owner ruling 2026-10-01). A wizard still standing whose seat ran out
    /// (<paramref name="walkedAway"/>: the hold expired, or the duel was abandoned) pays what fleeing costs: all mana.
    /// </summary>
    private void ReleaseHeldSeat(CombatDuelSubCircle circle, bool walkedAway = false) {
        if (_pvp) {
            ArenaMarkFled(circle); // CLASSIC: an arena match: the seat ran out, a loss
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
        else if (wizard is not null && walkedAway) {
            // CLASSIC: closing the client instead of fleeing no longer keeps the mana. 2009: "If you flee from a duel,
            // your mana also goes down to zero" (Fandom Health and Mana, oldid 4804, 2009-01-23, unchanged to oldid
            // 41879, 2009-09-13); the same as HandleFlee. The wizard's live copy, if they are back online elsewhere,
            // is the one changed (and saved).
            ApplyWalkAwayPenalty(wizard);
            Logger.Information("Duel {0} | Slot {1} | wizard {2} did not come back: mana drained as for a flee.",
                Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, wizard.CharId));
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
    /// CLASSIC: what a flee costs (all mana), for a wizard whose held seat ran out. Applied to the wizard's live copy
    /// when they are back online elsewhere, else to the copy the seat held; either saves it.
    /// </summary>
    internal static Wizard ApplyWalkAwayPenalty(Wizard held) {
        var target = ActiveWizardDirectory.TryGetByCharId(held.CharId, out var live) && live is not null ? live : held;
        target.UpdateMana(0);

        return target;
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
            ReleaseHeldSeat(circle, walkedAway: true);
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
