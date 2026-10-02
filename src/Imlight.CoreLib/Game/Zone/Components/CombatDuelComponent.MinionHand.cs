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
 * MYTH MINION HAND (SEQUENTIAL PICK)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner decision 2026-10-01): a Myth wizard chooses their own
 * minion's card and target in the game's own card window. After the wizard
 * picks their own card (or passes), the server deals the minion's hand into
 * the wizard's card window, shows the minion's pips on the wizard's pip ring,
 * and says in chat whose turn it is. The wizard picks one of those cards and
 * a target with the normal arrow, or passes for it. With several minions,
 * each gets its turn, in summon order. One planning timer covers every pick;
 * when it runs out, a minion not yet chosen for uses its own AI.
 *
 * The wizard's own pick is queued before the minion's hand is shown, and
 * every move the wizard's client sends while a minion's hand is showing is
 * read as the minion's move, so the wizard's pick is never overwritten. The
 * minion's move goes through ValidateOwnedMinionOrder (owner, Myth school,
 * duel, round, card, pips, target) like every other owner order.
 *
 * "Change" (the stock button after a pick) reopens the last minion's pick.
 * A stunned minion is skipped (a stun uses up its action this round).
 * [Classic] MythMinionHand = false turns this off (minions use their AI,
 * or the Minion Helper page if the player has paired one).
 *
 * The stock client is told only what it already handles every round:
 * MSG_COMBATHAND (for its own participant), MSG_COMBATPIPS,
 * MSG_SHOWCOMBATUI, MSG_SETPLANNINGPHASETIMER, plus a MSG_SERVERMESSAGE
 * chat line.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Types;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>[Classic] MythMinionHand: Myth wizards pick their minions' moves in the game's own card window.</summary>
internal static class MythMinionHandSettings {
    internal static bool Enabled {
        get {
            if (!EnhancedGameplaySettings.Enabled) return false;
            var value = ConfigurationManager.Settings["Classic.MythMinionHand"].AsString();
            return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value, out var enabled) && enabled);
        }
    }
}

internal sealed partial class CombatDuelComponent {

    /// <summary>One wizard's minion picks this round.</summary>
    private sealed class MinionHandStage {
        internal CoreObject Current;      // the minion whose hand is in the wizard's card window, or null when done
        internal CoreObject LastPicked;   // the minion "Change" reopens
        internal readonly HashSet<CoreObject> Done = new(ReferenceEqualityComparer.Instance);
    }

    private readonly Dictionary<CoreObject, MinionHandStage> _minionHandStages = new(ReferenceEqualityComparer.Instance);
    private readonly List<CoreObject> _summonOrder = [];
    private bool _minionHandPipsShown;

    /// <summary>A Myth wizard whose minion this is will pick its moves (called when the minion is registered).</summary>
    private void OptInForMinionHand(CombatDuelSubCircle owner, CombatDuelSubCircle minion) {
        if (!_summonOrder.Contains(minion.ParticipantObject, ReferenceEqualityComparer.Instance)) _summonOrder.Add(minion.ParticipantObject);
        if (MythMinionHandSettings.Enabled && IsMythWizard(owner) && !_tutorialDirector.IsActive) {
            _ownedMinionControl.OptIn(owner.ParticipantObject);
        }
    }

    private bool MinionHandStageActive(CombatDuelSubCircle owner)
        => owner is not null && _minionHandStages.TryGetValue(owner.ParticipantObject, out var stage) && stage.Current is not null;

    /// <summary>
    /// A move from a wizard whose card window shows a minion's hand is that minion's move. True when handled here.
    /// </summary>
    private bool TryHandleMinionHandMove(CombatDuelSubCircle owner, MSG_ACTORCOMBATMOVE message) {
        if (owner.IsSummonedMinion || !_minionHandStages.TryGetValue(owner.ParticipantObject, out var stage)) return false;
        if (Duel?.m_duelPhase != kDuelPhase.kPhase_Planning) return false;

        if (stage.Current is null) {
            // After the last pick the stock "Change" button reopens the last minion's pick. Other moves are the
            // wizard's own (a flee, say) and go the normal way.
            if (message.MoveType != (byte) CombatMoveType.ChangeMind || stage.LastPicked is null) return false;
            var last = LiveMinion(owner, stage.LastPicked);
            if (last is null) return false;
            _ownedMinionControl.Withdraw(last.ParticipantObject);
            RestoreOwnedMinionFallback(last);
            stage.Done.Remove(last.ParticipantObject);
            stage.Current = last.ParticipantObject;
            TelegraphMinionMoves();
            PresentMinionHand(owner, last, $"Choose again for your {ParticipantName(last)}.");
            ReevaluateOwnedMinionPlanning();
            return true;
        }

        var minion = LiveMinion(owner, stage.Current);
        if (minion is null) {
            AdvanceMinionHand(owner, stage);
            return message.MoveType != (byte) CombatMoveType.Flee; // a flee is still the wizard's
        }

        switch (message.MoveType) {
            case (byte) CombatMoveType.Flee:
                // The wizard runs; their minions go back to the AI.
                EndMinionHand(owner);
                return false;
            case (byte) CombatMoveType.Attack:
            case (byte) CombatMoveType.Pass: {
                var target = message.MoveType == (byte) CombatMoveType.Attack ? message.SpellTarget : uint.MaxValue;
                var status = OrderMinionFromHand(owner, minion, message.MoveType, message.SpellSelection, target);
                if (status == OwnedMinionStatus.Accepted) {
                    stage.Done.Add(minion.ParticipantObject);
                    stage.LastPicked = minion.ParticipantObject;
                    AdvanceMinionHand(owner, stage);
                } else {
                    PresentMinionHand(owner, minion, $"Your {ParticipantName(minion)} can't do that ({ReasonText(status)}). Choose again.");
                }

                return true;
            }
            default:
                // Discard, enchant, draw or a stray change: the minion's hand is not the wizard's deck. Show it again.
                PresentMinionHand(owner, minion, null);
                return true;
        }
    }

    /// <summary>The wizard asked for a treasure card while a minion's hand is showing: not for the minion.</summary>
    private bool BlockMinionHandDraw(CombatDuelSubCircle owner) {
        if (!MinionHandStageActive(owner)) return false;
        var stage = _minionHandStages[owner.ParticipantObject];
        if (LiveMinion(owner, stage.Current) is { } minion) PresentMinionHand(owner, minion, null);
        return true;
    }

    /// <summary>After the wizard's own move: deal the first minion's hand, if the wizard has one to choose for.</summary>
    private void MaybeBeginMinionHand(CombatDuelSubCircle owner, byte moveType) {
        if (!MythMinionHandSettings.Enabled || _tutorialDirector.IsActive || owner.IsSummonedMinion
            || moveType is not ((byte) CombatMoveType.Attack or (byte) CombatMoveType.Pass)
            || Duel?.m_duelPhase != kDuelPhase.kPhase_Planning || !IsMythOwner(owner) || !owner.IsAlive
            || !_ownedMinionControl.IsOptedIn(owner.ParticipantObject)
            || CombatResolver.GetQueuedAction(owner) is null) return;
        if (!_minionHandStages.TryGetValue(owner.ParticipantObject, out var stage)) {
            stage = new MinionHandStage();
            _minionHandStages[owner.ParticipantObject] = stage;
        }

        if (stage.Current is not null) return;
        AdvanceMinionHand(owner, stage);
    }

    private void AdvanceMinionHand(CombatDuelSubCircle owner, MinionHandStage stage) {
        stage.Current = null;
        foreach (var minion in MinionsInSummonOrder(owner)) {
            if (stage.Done.Contains(minion.ParticipantObject)) continue;
            if (IsOwnerChosenMinionMove(minion) || _ownedMinionControl.HasOrder(minion.ParticipantObject)) {
                stage.Done.Add(minion.ParticipantObject); // chosen elsewhere (the Minion Helper page)
                continue;
            }

            if (minion.CombatParticipant?.m_stunned > 0) {
                // A stunned minion loses this action anyway; leave it to the round.
                stage.Done.Add(minion.ParticipantObject);
                _ownedMinionControl.SetOrder(minion.ParticipantObject, new OwnedMinionOrder(OwnedMinionAiMove, 0, uint.MaxValue));
                Notify(owner, $"Your {ParticipantName(minion)} is stunned this round.");
                continue;
            }

            stage.Current = minion.ParticipantObject;
            PresentMinionHand(owner, minion, $"Now choose a spell for your {ParticipantName(minion)}.");
            ReevaluateOwnedMinionPlanning();
            return;
        }

        // Every minion has its move: show the wizard's own pips again and every chosen card over its caster.
        RestoreOwnerPips(owner);
        if (CombatResolver.GetQueuedAction(owner) is { } own) {
            SendCombatMoveSelection(owner.ParticipantObject.m_globalID, own.Spell is null ? (byte) CombatMoveType.Pass : (byte) CombatMoveType.Attack,
                own.Spell, (byte) (own.SelectedTarget?.SlotIndex ?? 0));
        }

        TelegraphMinionMoves();
        PublishOwnedMinionSnapshots(owner);
        ReevaluateOwnedMinionPlanning();
    }

    /// <summary>Queues a minion's move picked in the wizard's card window, through the owner-order checks.</summary>
    private OwnedMinionStatus OrderMinionFromHand(CombatDuelSubCircle owner, CombatDuelSubCircle minion, byte moveType, byte card, uint target) {
        if (!IsSupportedOwnedMinion(owner, minion) || !IsMythOwner(owner)) return OwnedMinionStatus.NotOwnedMinion;
        var order = new OwnedMinionOrder(moveType, card, target);
        var status = ValidateOwnedMinionOrder(minion, order, out var spell, out var chosen);
        if (status != OwnedMinionStatus.Accepted) return status;
        if (!_ownedMinionControl.HasOrder(minion.ParticipantObject) && CombatResolver.GetQueuedAction(minion) is { } fallback) {
            _ownedMinionFallbacks[minion.ParticipantObject] = fallback;
        }

        _ownedMinionControl.SetOrder(minion.ParticipantObject, order with { TargetIdentity = chosen?.ParticipantObject });
        QueueOwnedMinionOrder(minion, order, spell, chosen);
        Logger.Debug("Duel {0} | Slot {1} | Minion slot {2} move {3} card {4} target {5} chosen in the card window",
            Logger.Args(Duel.m_duelID.Full, owner.SlotIndex, minion.SlotIndex, moveType, card, target));
        return OwnedMinionStatus.Accepted;
    }

    private IEnumerable<CombatDuelSubCircle> MinionsInSummonOrder(CombatDuelSubCircle owner) {
        var live = SubCircles.Where(circle => IsSupportedOwnedMinion(owner, circle)).ToList();
        return live.OrderBy(circle => {
            var at = _summonOrder.FindIndex(identity => ReferenceEquals(identity, circle.ParticipantObject));
            return at < 0 ? int.MaxValue : at;
        }).ThenBy(circle => circle.SlotIndex);
    }

    private CombatDuelSubCircle LiveMinion(CombatDuelSubCircle owner, CoreObject identity)
        => identity is null ? null : SubCircles.FirstOrDefault(circle => ReferenceEquals(circle.ParticipantObject, identity)
                                                                         && IsSupportedOwnedMinion(owner, circle));

    /// <summary>Deals the minion's hand into the wizard's card window, with the minion's pips, and reopens the pick.</summary>
    private void PresentMinionHand(CombatDuelSubCircle owner, CombatDuelSubCircle minion, string notice) {
        if (owner.ParticipantActor is null || minion._combatDeck is null) return;
        if (!_serializer.Serialize(minion.GetCurrentHand(), _combatParticipantHandFlags, out var hand)) {
            Logger.Error("Duel {0} | Could not serialize a minion's hand for its owner", Logger.Args(Duel.m_duelID.Full));
            return;
        }

        // The deck counter keeps the wizard's own numbers (a creature's deck is not a player deck: tens of thousands).
        owner.ParticipantActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
            DeckCount = (byte) owner.AvailableSpells,
            TotalDeckCount = (ushort) owner.TotalSpells,
            TreasureCardCount = 0,
            ParticipantID = owner.ParticipantObject.m_globalID,
            HandData = hand,
        });
        SendPipsTo(owner, minion);
        _minionHandPipsShown = true;
        var remaining = (int) Math.Ceiling(Math.Max(1, (_ownedMinionPlanningDeadline - DateTime.UtcNow).TotalSeconds));
        owner.ParticipantActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI { DuelID = SigilId });
        owner.ParticipantActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SETPLANNINGPHASETIMER {
            DuelID = SigilId, Time = Math.Min(remaining, PLANNING_TIME),
        });
        if (notice is not null) Notify(owner, notice);
    }

    /// <summary>
    /// MSG_COMBATPIPS for the wizard's client only, with <paramref name="shown"/>'s pips on the wizard's own entry,
    /// so the card window greys out exactly the cards the shown participant cannot pay for.
    /// </summary>
    private void SendPipsTo(CombatDuelSubCircle owner, CombatDuelSubCircle shown) {
        var pips = new CombatPipListObj { m_pipList = [], m_duelID = SigilId };
        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel || !circle.IsAlive) return;
            var source = ReferenceEquals(circle, owner) ? shown : circle;
            pips.m_pipList.Add(new ParticipantPipData {
                m_acq = 1,
                m_partID = (GID) circle.ParticipantObject.m_globalID,
                m_pips = new PipCount {
                    m_genericPips = source.CombatParticipant.m_pipCount.m_genericPips,
                    m_powerPips = source.CombatParticipant.m_pipCount.m_powerPips,
                },
            });
        });
        if (!_serializer.Serialize(pips, _combatParticipantStatFlags, out var buffer)) return;
        owner.ParticipantActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS { DuelID = SigilId, PipData = buffer });
    }

    private void RestoreOwnerPips(CombatDuelSubCircle owner) {
        if (owner.ParticipantActor is not null && owner.CombatParticipant?.m_pipCount is not null) SendPipsTo(owner, owner);
    }

    private static void Notify(CombatDuelSubCircle owner, string text)
        => owner.ParticipantActor?.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });

    /// <summary>The wizard is leaving the stage (fled, removed): their minions go back to the AI for this round.</summary>
    private void EndMinionHand(CombatDuelSubCircle owner) {
        if (!_minionHandStages.Remove(owner.ParticipantObject, out var stage)) return;
        if (stage.Current is not null) RestoreOwnerPips(owner);
    }

    /// <summary>A minion left mid-planning: move its owner's stage on if it was that minion's turn.</summary>
    private void MinionLeftMinionHand(CoreObject identity) {
        foreach (var (ownerObject, stage) in _minionHandStages.ToArray()) {
            stage.Done.Remove(identity);
            if (!ReferenceEquals(stage.Current, identity)) continue;
            var owner = SubCircles.FirstOrDefault(circle => ReferenceEquals(circle.ParticipantObject, ownerObject));
            if (owner is null) {
                _minionHandStages.Remove(ownerObject);
                continue;
            }

            AdvanceMinionHand(owner, stage);
        }
    }

    /// <summary>Planning ended or a new round began: no stage carries over, and every client sees true pips.</summary>
    private void ResetMinionHand(bool sendTruePips) {
        _minionHandStages.Clear();
        if (sendTruePips && _minionHandPipsShown) SendCombatPips();
        _minionHandPipsShown = false;
    }
}
