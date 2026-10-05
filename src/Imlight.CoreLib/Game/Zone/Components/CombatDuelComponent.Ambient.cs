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
 * DUELS AND AMBIENT WIZARDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what a duel does about ambient wizards (Classic/Ambient):
 *   - plays their turns (AllyBrain through AmbientCombat), a few seconds
 *     into each planning phase, through the normal combat move path;
 *   - lets one walk in only with a permit for this duel (a player's yes,
 *     its own street fight, or a PvP practice seat);
 *   - gives a real player who reaches a full circle an ambient wizard's
 *     slot (the ambient wizard flees);
 *   - tells the zone's ambient wizards about duels with real players in
 *     them, so one of them can offer to help.
 * Nothing here runs when no ambient wizard exists.
 *
 * USAGE EXAMPLE:
 * ScheduleAmbientTurns();          // in ReceivePlanningPhaseBegin
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    private bool IsAmbientCircle(CombatDuelSubCircle circle)
        => circle is { Occupied: true } && AmbientWizards.IsAmbient(circle.ParticipantActor);

    /// <summary>Starts the timers that play the ambient wizards' turns this planning phase.</summary>
    private void ScheduleAmbientTurns() {
        if (AmbientWizards.Count == 0 || SubCircles is null) {
            return;
        }

        NotifyAmbientWizards(); // CLASSIC (2026-10-04): each round, so onlookers see how the duel is going.
        foreach (var circle in SubCircles.Where(c => IsAmbientCircle(c) && c.IsAlive && c.AddedToDuel)) {
            Timers.StartSingleTimer($"ambient-turn-{circle.SlotIndex}", new AmbientTurn(circle.SlotIndex, Duel.m_roundNum),
                AmbientCombat.ThinkingTime(circle.SlotIndex, Duel.m_roundNum));
        }
    }

    [MessageHandler(typeof(AmbientTurn))]
    private void ReceiveAmbientTurn(AmbientTurn turn) {
        if (!_isActive || Duel?.m_duelPhase != kDuelPhase.kPhase_Planning || turn.Round != Duel.m_roundNum || !_awaitingCombatMoves) {
            return;
        }

        var circle = SubCircles.FirstOrDefault(c => c.SlotIndex == turn.Slot);
        if (!IsAmbientCircle(circle) || !circle.IsAlive || CombatResolver.GetQueuedAction(circle) is not null) {
            return;
        }

        var move = AmbientCombat.Choose(this, circle, out var why);
        Logger.Debug("Duel {0} | Slot {1} | ambient wizard: {2}", Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, why.Reason));
        ReceiveCombatMove(move);
    }

    /// <summary>
    /// A real player reached a full circle: an ambient wizard in a player slot gives it up (it flees), unless it was
    /// seated there for a PvP practice match.
    /// </summary>
    private bool MakeRoomForRealPlayer(IActorRef arriving) {
        if (AmbientWizards.Count == 0 || AmbientWizards.IsAmbient(arriving)) {
            return false;
        }

        var ambient = SubCircles.FirstOrDefault(c => IsAmbientCircle(c) && c.OccupiedTeam == CombatTeam.Player && !c.IsSummonedMinion
                                                     && !AmbientWizards.IsSparringIn(c.ParticipantActor, SigilId));
        if (ambient is null) {
            return false;
        }

        Logger.Debug("Duel {0} | Slot {1} | an ambient wizard makes room for a player.", Logger.Args(Duel.m_duelID.Full, ambient.SlotIndex));
        HandleFleeAction(ambient);

        return _isActive && IsSlotAvailable(CombatTeam.Player);
    }

    /// <summary>Tells the zone's ambient wizards about this duel (its real players and free slots), or that it ended.</summary>
    private void NotifyAmbientWizards(bool active = true) {
        if (AmbientWizards.Count == 0 || SubCircles is null) {
            return;
        }

        var players = SubCircles
            .Where(c => c is { Occupied: true } && c.ParticipantObject.m_templateID == 1 && !c.IsSummonedMinion
                        && !AmbientWizards.IsAmbient(c.ParticipantActor))
            .Select(c => Wizard.TryGetCharacterId(c.ParticipantObject.m_globalID, out var id) ? id : 0)
            .Where(id => id != 0).ToArray();
        if (active && players.Length == 0) {
            return;
        }

        var pvp = SubCircles.Any(c => c is { Occupied: true } && c.OccupiedTeam == CombatTeam.Monster && c.ParticipantObject.m_templateID == 1);
        AmbientWizards.NotifyDuel(Entity.ZoneRef, new AmbientDuelNotice(SigilId, Entity.ActiveGameObject.m_location, players,
            4 - PlayerCount, active && _isActive, pvp, pvp ? null : AmbientOdds()));
    }

    /// <summary>
    /// CLASSIC (2026-10-04): how the duel looks to an onlooker: enemies standing, their health left as a share of their
    /// total, and the lowest real player's health share (Imlight.Classic.Ambient.HelpManners).
    /// </summary>
    private DuelOdds AmbientOdds() {
        double enemyNow = 0, enemyMax = 0, lowest = 1;
        var standing = 0;
        foreach (var circle in SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true })) {
            var stats = circle.ParticipantGameStats;
            var max = Math.Max(1, stats?.m_baseHitpoints ?? 1);
            var now = Math.Max(0, stats?.m_currentHitpoints ?? 0);
            if (circle.OccupiedTeam == CombatTeam.Monster) {
                enemyNow += circle.IsAlive ? now : 0;
                enemyMax += max;
                standing += circle.IsAlive ? 1 : 0;
            }
            else if (circle.ParticipantObject.m_templateID == 1 && !circle.IsSummonedMinion
                     && !AmbientWizards.IsAmbient(circle.ParticipantActor)) {
                lowest = Math.Min(lowest, circle.IsAlive ? (double) now / max : 0);
            }
        }

        return enemyMax <= 0 ? DuelOdds.Unknown : new DuelOdds(standing, enemyNow / enemyMax, lowest);
    }

}
