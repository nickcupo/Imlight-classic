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
 * DUELS AND HIRED HENCHMEN
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): a henchman hired from the Crown Shop (October 2009:
 * "hired Wizards that help you in a duel", bought "only while you are in
 * combat", acting "in the same round that they are summoned" and leaving
 * "once the duel has ended"). The duel:
 *   - takes it only in a PvE duel whose circle allows henchmen, during card
 *     selection, for a living wizard, with a free seat on the wizard's side
 *     (HenchmanRules.Check); the Crown Shop refunds any refusal;
 *   - seats it as the buyer's summoned minion (the client's henchman is a
 *     "special type of Minion", Help_00000040), adds it to the round at once
 *     and gives it a wizard's deck for its school and level;
 *   - plays its turns with the ally brain (AmbientCombat / AllyBrain), as
 *     ambient wizards play theirs; its creature AI's moves are dropped;
 *   - removes it when its buyer presses the client's Dismiss button
 *     (MSG_DISMISS_SUMMON; no refund, GUI_DismissHenchmen) and, like every
 *     summoned minion, when the duel ends (DespawnDuel).
 *
 * USAGE EXAMPLE:
 * duelActor.Tell(new COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN { CreatureTid = tid, Actor = session, Level = 20 });
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>A duel's reminder to itself to play a henchman's turn.</summary>
internal sealed record HenchmanTurn(int Slot, int Round);

internal sealed partial class CombatDuelComponent {

    /// <summary>Seconds after a henchman joins mid-round before it picks its card.</summary>
    private static readonly TimeSpan HenchmanJoinThinkingTime = TimeSpan.FromSeconds(1.5);

    // Set while the duel queues a henchman's own (ally brain) move; other moves for a henchman are dropped.
    private bool _choosingHenchmanMove;

    // CLASSIC: a henchman hired from the Crown Shop during this duel joins the buyer's side. The buyer's session is
    // told whether it joined, and why not.
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN))]
    private void ReceiveHireHenchman(COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN message) {
        var buyer = SubCircles?.FirstOrDefault(x => x.ParticipantActor is not null && x.ParticipantActor == message.Actor);
        var refusal = HenchmanRules.Check(
            inDuel: _isActive && buyer is not null,
            pvp: _pvp || Duel?.m_bPVP == true,
            sigilForbids: Duel?.m_noHenchmen == true,
            planning: Duel?.m_duelPhase == kDuelPhase.kPhase_Planning,
            buyerReady: buyer is { IsWizard: true, IsAlive: true, AddedToDuel: true, Disconnected: false }
                && buyer.OccupiedTeam == CombatTeam.Player,
            seatFree: SubCircles is not null && GetAvailableSubCircleTeamPlayer() is not null,
            known: true);
        // The template is looked up only for a hire that may otherwise go ahead.
        if (refusal == HenchmanRefusal.None && CoreObjectFactory.GetCoreTemplate(message.CreatureTid) is null) {
            refusal = HenchmanRefusal.Unavailable;
        }

        CombatDuelSubCircle henchman = null;
        if (refusal == HenchmanRefusal.None) {
            henchman = SpawnAndAssignMinion(message.CreatureTid, buyer);
            if (henchman is null) {
                refusal = HenchmanRefusal.Unavailable;
            }
            else {
                SeatHenchman(henchman, message.Level);
            }
        }

        Logger.Information("Duel {0} | henchman tid {1} (level {2}) for slot {3}: {4}.",
            Logger.Args(Duel?.m_duelID.Full ?? SigilId, message.CreatureTid, message.Level, buyer?.SlotIndex ?? -1,
                refusal == HenchmanRefusal.None ? $"joined slot {henchman!.SlotIndex}" : refusal.ToString()));
        message.Actor?.Tell(new COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED {
            CreatureTid = message.CreatureTid, Success = refusal == HenchmanRefusal.None, Refusal = refusal,
        });
    }

    // The henchman takes its level's wizard deck, joins this round at once and picks its card shortly.
    private void SeatHenchman(CombatDuelSubCircle henchman, int level) {
        var school = (MagicSchool) (henchman.CombatParticipant?.m_primaryMagicSchoolID ?? (int) MagicSchool.Balance);
        henchman.BecomeHenchman(Math.Max(1, level), AmbientCombat.DeckFor(school, Math.Max(1, level)));

        AddCircleToCombat(henchman);
        henchman.DrawHand();
        PublishActiveDuel();
        Timers.StartSingleTimer($"henchman-turn-{henchman.SlotIndex}", new HenchmanTurn(henchman.SlotIndex, Duel.m_roundNum),
            HenchmanJoinThinkingTime);
        // A round that was about to end early (every move in) now waits for the henchman's move.
        ReevaluateOwnedMinionPlanning();
    }

    /// <summary>Starts the timers that play the henchmen's turns this planning phase.</summary>
    private void ScheduleHenchmanTurns() {
        if (SubCircles is null) {
            return;
        }

        foreach (var circle in SubCircles.Where(c => c is { IsHenchman: true, IsAlive: true, AddedToDuel: true })) {
            Timers.StartSingleTimer($"henchman-turn-{circle.SlotIndex}", new HenchmanTurn(circle.SlotIndex, Duel.m_roundNum),
                AmbientCombat.ThinkingTime(circle.SlotIndex, Duel.m_roundNum));
        }
    }

    [MessageHandler(typeof(HenchmanTurn))]
    private void ReceiveHenchmanTurn(HenchmanTurn turn) {
        if (!_isActive || Duel?.m_duelPhase != kDuelPhase.kPhase_Planning || turn.Round != Duel.m_roundNum || !_awaitingCombatMoves) {
            return;
        }

        var circle = SubCircles.FirstOrDefault(c => c.SlotIndex == turn.Slot);
        if (circle is not { IsHenchman: true, IsAlive: true, AddedToDuel: true } || CombatResolver.GetQueuedAction(circle) is not null) {
            return;
        }

        var move = AmbientCombat.Choose(this, circle, out var why);
        Logger.Debug("Duel {0} | Slot {1} | henchman: {2}", Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, why.Reason));
        _choosingHenchmanMove = true;
        try {
            ReceiveCombatMove(move);
        }
        finally {
            _choosingHenchmanMove = false;
        }
    }

    // CLASSIC: the buyer's Dismiss button removes their henchman (Crowns are not refunded).
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_DISMISSHENCHMAN))]
    private void ReceiveDismissHenchman(COMBAT_106_PROTOCOL.MSG_DISMISSHENCHMAN message) {
        if (!_isActive || SubCircles is null || message.SubCircle < 0 || message.SubCircle >= SubCircles.Length) {
            return;
        }

        var owner = SubCircles.FirstOrDefault(x => x.ParticipantActor is not null && x.ParticipantActor == message.Actor);
        var henchman = SubCircles[message.SubCircle];
        if (owner is null || henchman is not { IsHenchman: true } || !henchman.IsOwnedMinionOf(owner)
            || henchman.ParticipantActor is null) {
            Logger.Information("Duel {0} | dismiss of slot {1} refused: not the sender's henchman.",
                Logger.Args(Duel.m_duelID.Full, message.SubCircle));

            return;
        }

        Logger.Information("Duel {0} | slot {1} dismissed its henchman in slot {2}.",
            Logger.Args(Duel.m_duelID.Full, owner.SlotIndex, henchman.SlotIndex));
        // The minion's death path: MSG_COMBATREMOVE to the clients, the seat freed, its queued move withdrawn.
        henchman.ParticipantActor.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());
    }

}
