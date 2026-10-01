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
 * OWNED MINION CONTROL
 * ========================================================================
 *
 * PURPOSE:
 * Validates optional owner orders independently of creature AI and combat resolution.
 *
 * USAGE EXAMPLE:
 * Used by CombatDuelComponent during planning.
 *
 * NOTE:
 * Owner and minion keys use object identity, not reusable sigil slots.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {
    private const int MinionSnapshotBudget = 22000;
    private readonly OwnedMinionControl _ownedMinionControl = new();
    private readonly Dictionary<CoreObject, CoreObject> _ownedControllableSummons = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CoreObject, QueuedCombatAction> _ownedMinionFallbacks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CoreObject, MSG_ACTORCOMBATMOVE> _ownedMinionHeldAiMoves = new(ReferenceEqualityComparer.Instance);
    private DateTime _ownedMinionPlanningDeadline;
    private bool _ownedMinionEarlyFinishScheduled;

    internal void RegisterOwnedMinionForControl(CombatDuelSubCircle minion, CombatDuelSubCircle owner) {
        if (minion.IsOwnedMinionOf(owner)) {
            _ownedControllableSummons[minion.ParticipantObject] = owner.ParticipantObject;
            OptInForMinionHand(owner, minion); // CLASSIC: Myth wizards pick their minions' moves in the card window.
        }
    }

    private static bool IsMythOwner(CombatDuelSubCircle owner)
        => owner is { Occupied: true, AddedToDuel: true, IsSummonedMinion: false }
           && owner.CombatParticipant?.m_isPlayer == true
           && owner._wizard?.MagicSchoolBehavior?.MagicSchool == MagicSchool.Myth;

    private bool IsSupportedOwnedMinion(CombatDuelSubCircle owner, CombatDuelSubCircle minion)
        => minion is { Occupied: true, AddedToDuel: true, IsAlive: true }
           && minion.IsOwnedMinionOf(owner)
           && _ownedControllableSummons.TryGetValue(minion.ParticipantObject, out var capturedOwner)
           && ReferenceEquals(capturedOwner, owner.ParticipantObject);

    private IEnumerable<(CombatDuelSubCircle Owner, CombatDuelSubCircle Minion)> LiveControllableMinions() {
        foreach (var owner in SubCircles.Where(owner => IsMythOwner(owner) && owner.IsAlive)) {
            foreach (var minion in SubCircles.Where(minion => IsSupportedOwnedMinion(owner, minion))) {
                yield return (owner, minion);
            }
        }
    }

    internal bool HaveAllOwnedMinionOrders()
        => !EnhancedGameplaySettings.Enabled || _ownedMinionControl.AllOrdered(
            LiveControllableMinions().Select(pair => ((object) pair.Owner.ParticipantObject, (object) pair.Minion.ParticipantObject)));

    [MessageHandler(typeof(MSG_OWNEDMINIONREQUEST))]
    private void ReceiveOwnedMinionRequest(MSG_OWNEDMINIONREQUEST request) {
        var response = ProcessOwnedMinionRequest(request);
        Logger.Debug("Duel {0} | Owned minion request {1} move {2} card {3} target {4} query {5}: {6}",
            Logger.Args(Duel?.m_duelID.Full, request.RequestID, request.MoveType, request.SpellSelection, request.SpellTarget,
                request.Query, response.Status.ToString()));
        response.HelperView = BuildMinionHelperView(request.OwnerActor, response);
        request.OwnerActor?.Tell(response);
    }

    internal MSG_OWNEDMINIONRESPONSE ProcessOwnedMinionRequest(MSG_OWNEDMINIONREQUEST request) {
        var response = new MSG_OWNEDMINIONRESPONSE {
            OwnerActor = request.OwnerActor, DuelID = request.DuelID, Round = request.Round,
            MinionID = request.MinionID, RequestID = request.RequestID,
        };
        if (!EnhancedGameplaySettings.Enabled) {
            DisableAllOwnedMinionControl();
            response.Status = OwnedMinionStatus.Disabled;
            return response;
        }
        var owner = SubCircles?.FirstOrDefault(circle => circle.Occupied && circle.ParticipantActor == request.OwnerActor);
        var minion = SubCircles?.FirstOrDefault(circle => circle.Occupied && circle.ParticipantObject.m_globalID == request.MinionID);
        var allQuery = request.Query && request.MinionID == 0;
        response.Status = OwnedMinionControl.ValidateAccess(new OwnedMinionAccess(
            _isActive && Duel?.m_duelID.Full == request.DuelID,
            Duel?.m_roundNum == request.Round,
            Duel?.m_duelPhase == kDuelPhase.kPhase_Planning && (_awaitingCombatMoves || _ownedMinionEarlyFinishScheduled),
            owner is { AddedToDuel: true }, owner?.IsAlive == true, owner is not null && IsMythOwner(owner),
            minion is { AddedToDuel: true }, minion?.IsAlive == true,
            minion is not null && owner is not null && minion.IsOwnedMinionOf(owner),
            minion is not null && owner is not null && IsSupportedOwnedMinion(owner, minion)), allQuery);
        if (response.Status != OwnedMinionStatus.Accepted) return response;
        if (!_ownedMinionControl.IsNewRequest(owner.ParticipantObject, request.RequestID)) {
            response.Status = OwnedMinionStatus.RequestReplay;
            return response;
        }
        if (!TryOwnedMinionSnapshots(owner, out var snapshots)) {
            response.Status = OwnedMinionStatus.SnapshotUnavailable;
            return response;
        }
        response.Snapshots = snapshots;
        if (request.Query) {
            _ownedMinionControl.OptIn(owner.ParticipantObject);
        } else {
            if (!_ownedMinionControl.IsOptedIn(owner.ParticipantObject)) {
                response.Status = OwnedMinionStatus.NotOptedIn;
                return response;
            }
            var order = new OwnedMinionOrder(request.MoveType, request.SpellSelection, request.SpellTarget);
            response.Status = ValidateOwnedMinionOrder(minion, order, out var spell, out var target);
            if (response.Status != OwnedMinionStatus.Accepted) return response;
            var fallback = CombatResolver.GetQueuedAction(minion);
            if (!_ownedMinionControl.HasOrder(minion.ParticipantObject) && fallback is not null) _ownedMinionFallbacks[minion.ParticipantObject] = fallback;
            if (request.MoveType == (byte) CombatMoveType.ChangeMind) {
                _ownedMinionControl.Withdraw(minion.ParticipantObject);
                RestoreOwnedMinionFallback(minion);
                TelegraphMinionMoves();
            } else if (request.MoveType == OwnedMinionAiMove) {
                // CLASSIC: "let the minion choose" is the owner's order for this round: its AI move stands.
                _ownedMinionControl.Withdraw(minion.ParticipantObject);
                RestoreOwnedMinionFallback(minion);
                _ownedMinionControl.SetOrder(minion.ParticipantObject, order);
                TelegraphMinionMoves();
            } else {
                _ownedMinionControl.SetOrder(minion.ParticipantObject, order with { TargetIdentity = target?.ParticipantObject });
                QueueOwnedMinionOrder(minion, order, spell, target);
            }
        }
        _ownedMinionControl.RecordRequest(owner.ParticipantObject, request.RequestID);
        response.Accepted = true;
        // Identity and hand bytes stay unchanged; only the order fields need updating after mutation.
        foreach (var snapshot in response.Snapshots) SetOwnedMinionOrderSnapshot(snapshot);
        ReevaluateOwnedMinionPlanning();
        return response;
    }

    internal OwnedMinionStatus ValidateOwnedMinionOrder(CombatDuelSubCircle minion, OwnedMinionOrder order,
                                                      out Spell spell, out CombatDuelSubCircle target) {
        spell = null;
        target = null;
        if (order.MoveType is (byte) CombatMoveType.Pass or (byte) CombatMoveType.ChangeMind or OwnedMinionAiMove) return OwnedMinionStatus.Accepted;
        if (order.MoveType != (byte) CombatMoveType.Attack) return OwnedMinionStatus.InvalidMove;
        if (minion?._combatDeck is null) return OwnedMinionStatus.InvalidCard;
        spell = minion.GetSpellFromLastHand(order.SpellSelection);
        if (spell?.m_pipCost is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate baseTemplate)
            return OwnedMinionStatus.InvalidCard;
        var template = minion._combatDeck.CastTemplateFor(spell, baseTemplate);
        if (template?.m_effects is not { Count: > 0 }) return OwnedMinionStatus.InvalidCard;
        if (!AllowsMonstrologyCast(minion, spell, baseTemplate)) return OwnedMinionStatus.Disabled;
        if (!minion.HasPipsForSpell(spell)) return OwnedMinionStatus.InsufficientPips;
        var provided = order.SpellTarget != uint.MaxValue;
        target = provided ? SubCircles.FirstOrDefault(circle => circle.SlotIndex == order.SpellTarget) : minion;
        if (order.TargetIdentity is not null && !ReferenceEquals(order.TargetIdentity, target?.ParticipantObject))
            return OwnedMinionStatus.InvalidTarget;
        var effects = FlattenOwnedMinionEffects(template.m_effects).ToArray();
        if (effects.Length == 0) return OwnedMinionStatus.InvalidCard;
        var needsAreaTarget = ClassicRuntime.IsActive && !ClassicRuntime.Rules.UntargetedAreaSpells;
        var requirements = new List<OwnedMinionTarget>();
        foreach (var effect in effects) {
            switch (effect.m_effectTarget) {
                case kEffectTarget.kEnemySingle: requirements.Add(OwnedMinionTarget.Enemy); break;
                case kEffectTarget.kFriendlySingle: requirements.Add(OwnedMinionTarget.Friend); break;
                case kEffectTarget.kFriendlySingleNotMe: requirements.Add(OwnedMinionTarget.FriendNotSelf); break;
                case kEffectTarget.kMinion:
                case kEffectTarget.kCasterMinion: requirements.Add(OwnedMinionTarget.OwnMinion); break;
                case kEffectTarget.kFriendlyMinion: requirements.Add(OwnedMinionTarget.FriendMinion); break;
                case kEffectTarget.kEnemyMinion: requirements.Add(OwnedMinionTarget.EnemyMinion); break;
                case kEffectTarget.kTargetMinion: requirements.Add(OwnedMinionTarget.AnyMinion); break;
                case kEffectTarget.kEnemyTeam:
                case kEffectTarget.kEnemyTeamAllAtOnce:
                    if (provided || needsAreaTarget) requirements.Add(OwnedMinionTarget.Enemy);
                    break;
                case kEffectTarget.kFriendlyTeam:
                case kEffectTarget.kFriendlyTeamAllAtOnce:
                    if (provided) requirements.Add(OwnedMinionTarget.Friend);
                    break;
                case kEffectTarget.kSelf:
                case kEffectTarget.kInvalidTarget:
                case kEffectTarget.kGlobal: break;
                default: return OwnedMinionStatus.InvalidCard;
            }
        }
        if (requirements.Count == 0) requirements.Add(OwnedMinionTarget.Self);
        var live = target is { Occupied: true, AddedToDuel: true, IsAlive: true };
        foreach (var requirement in requirements) {
            if (!OwnedMinionControl.ValidTarget(requirement, provided, live,
                target?.OccupiedTeam == minion.ActingTeam, target == minion,
                target?.IsSummonedMinion == true, target?.IsOwnedMinionOf(minion) == true)) return OwnedMinionStatus.InvalidTarget;
        }
        return OwnedMinionStatus.Accepted;
    }

    private static IEnumerable<SpellEffect> FlattenOwnedMinionEffects(IEnumerable<SpellEffect> effects) {
        foreach (var effect in effects) {
            if (effect is null) continue;
            var children = SpellTemplateEditor.ChildrenOf(effect);
            if (children is { Count: > 0 }) {
                foreach (var child in FlattenOwnedMinionEffects(children)) yield return child;
            } else {
                yield return effect;
            }
        }
    }

    private void QueueOwnedMinionOrder(CombatDuelSubCircle minion, OwnedMinionOrder order, Spell spell, CombatDuelSubCircle target) {
        CombatResolver.AddCombatMove((CombatMoveType) order.MoveType, minion, target, spell);
        if (spell is not null && CombatResolver.GetQueuedAction(minion) is { } action)
            action.SpellTemplate = minion._combatDeck.CastTemplateFor(spell, action.SpellTemplate);
        SendCombatMoveSelection(minion.ParticipantObject.m_globalID, order.MoveType, spell,
            order.SpellTarget < 8 ? (byte) order.SpellTarget : (byte) 0);
    }

    private void RestoreOwnedMinionFallback(CombatDuelSubCircle minion) {
        CombatResolver.AddCombatMove(CombatMoveType.ChangeMind, minion, null, null);
        if (!_ownedMinionFallbacks.ContainsKey(minion.ParticipantObject)
            && _ownedMinionHeldAiMoves.Remove(minion.ParticipantObject, out var held)) {
            // CLASSIC: the AI moved while the owner's order stood; replay that move now (planning may already be in
            // its one-second completion grace, which ReceiveCombatMove would refuse).
            if (held.MoveType == (byte) CombatMoveType.Attack) HandleAttackMove(minion, held.SpellSelection, held.SpellTarget);
            else HandlePassMove(minion);
            if (CombatResolver.GetQueuedAction(minion) is { } replayed) _ownedMinionFallbacks[minion.ParticipantObject] = replayed;
            return;
        }
        if (_ownedMinionFallbacks.TryGetValue(minion.ParticipantObject, out var fallback) && minion is { Occupied: true, AddedToDuel: true, IsAlive: true }) {
            CombatResolver.AddCombatMove(fallback.Spell is null ? CombatMoveType.Pass : CombatMoveType.Attack,
                minion, fallback.SelectedTarget, fallback.Spell);
            if (CombatResolver.GetQueuedAction(minion) is { } restored) restored.SpellTemplate = fallback.SpellTemplate;
        }
    }

    private bool TryOwnedMinionSnapshots(CombatDuelSubCircle owner, out OwnedMinionSnapshot[] snapshots) {
        var result = new List<OwnedMinionSnapshot>();
        var budget = 1024;
        foreach (var pair in LiveControllableMinions().Where(pair => pair.Owner == owner)) {
            var minion = pair.Minion;
            if (minion._combatDeck is null || minion.CombatParticipant?.m_pipCount is null
                || !_serializer.Serialize(minion.GetCurrentHand(), _combatParticipantHandFlags, out var hand)
                || !_serializer.Serialize(minion.CombatParticipant, _combatParticipantFlags, out var participant)) {
                snapshots = [];
                return false;
            }
            budget += 1024 + ((hand.Length + 2) / 3) * 4 + ((participant.Length + 2) / 3) * 4;
            // CLASSIC: an oversized participant used to switch control off for the rest of the duel. Only the opaque
            // service-90 blobs are bounded; the Minion Helper reads HelperView instead.
            var oversized = budget > MinionSnapshotBudget;
            var snapshot = new OwnedMinionSnapshot {
                OwnerID = owner.ParticipantObject.m_globalID, MinionID = minion.ParticipantObject.m_globalID,
                Slot = (byte) minion.SlotIndex, Team = (byte) minion.OccupiedTeam,
                Health = minion.ParticipantGameStats.m_currentHitpoints,
                GenericPips = minion.CombatParticipant.m_pipCount.m_genericPips,
                PowerPips = minion.CombatParticipant.m_pipCount.m_powerPips,
                HandData = oversized ? [] : hand, ParticipantData = oversized ? [] : participant,
            };
            SetOwnedMinionOrderSnapshot(snapshot);
            result.Add(snapshot);
        }
        snapshots = result.ToArray();
        return true;
    }

    private void SetOwnedMinionOrderSnapshot(OwnedMinionSnapshot snapshot) {
        var minion = SubCircles.FirstOrDefault(circle => circle.Occupied && circle.ParticipantObject.m_globalID == snapshot.MinionID);
        snapshot.HasOrder = minion is not null && _ownedMinionControl.TryGetOrder(minion.ParticipantObject, out _);
        if (snapshot.HasOrder && _ownedMinionControl.TryGetOrder(minion.ParticipantObject, out var order)) {
            snapshot.MoveType = order.MoveType;
            snapshot.SpellSelection = order.SpellSelection;
            snapshot.SpellTarget = order.SpellTarget;
        } else {
            snapshot.MoveType = (byte) CombatMoveType.ChangeMind;
            snapshot.SpellSelection = 0;
            snapshot.SpellTarget = uint.MaxValue;
        }
    }

    [MessageHandler(typeof(MSG_OWNEDMINIONDISABLE))]
    private void ReceiveOwnedMinionDisable(MSG_OWNEDMINIONDISABLE message) {
        var owner = SubCircles?.FirstOrDefault(circle => circle.Occupied && circle.ParticipantActor == message.OwnerActor);
        if (owner is null) return;
        DisableOwnedMinionControl(owner.ParticipantObject);
        if (Duel?.m_duelPhase == kDuelPhase.kPhase_Planning) TelegraphMinionMoves();
        ReevaluateOwnedMinionPlanning();
        PublishOwnedMinionSnapshots(owner);
    }

    private void DisableOwnedMinionControl(CoreObject ownerObject) {
        _ownedMinionControl.Disable(ownerObject);
        foreach (var minion in SubCircles.Where(circle => circle.Occupied
                     && _ownedControllableSummons.TryGetValue(circle.ParticipantObject, out var owner)
                     && ReferenceEquals(owner, ownerObject))) {
            if (!_ownedMinionControl.HasOrder(minion.ParticipantObject)) continue;
            _ownedMinionControl.Withdraw(minion.ParticipantObject);
            if (Duel?.m_duelPhase == kDuelPhase.kPhase_Planning) RestoreOwnedMinionFallback(minion);
        }
    }

    private void DisableAllOwnedMinionControl() {
        if (SubCircles is null) return;
        foreach (var owner in SubCircles.Where(circle => circle.Occupied)) DisableOwnedMinionControl(owner.ParticipantObject);
        ReevaluateOwnedMinionPlanning();
    }

    private void ReevaluateOwnedMinionPlanning() {
        if (!_isActive || Duel?.m_duelPhase != kDuelPhase.kPhase_Planning) return;
        var complete = CombatResolver.HaveAllParticipantsEnqueuedActions() && HaveAllOwnedMinionOrders();
        if (complete && !_ownedMinionEarlyFinishScheduled) {
            _ownedMinionEarlyFinishScheduled = true;
            _awaitingCombatMoves = false;
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new MSG_PLANNINGPHASEOVER(), TimeSpan.FromSeconds(1));
        } else if (!complete && _ownedMinionEarlyFinishScheduled) {
            _ownedMinionEarlyFinishScheduled = false;
            _awaitingCombatMoves = true;
            var remaining = _ownedMinionPlanningDeadline - DateTime.UtcNow;
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new MSG_PLANNINGPHASEOVER(),
                remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
        }
    }

    private void PrepareOwnedMinionExecution() {
        foreach (var minion in SubCircles.Where(circle => circle.Occupied && _ownedMinionControl.HasOrder(circle.ParticipantObject))) {
            var owner = SubCircles.FirstOrDefault(owner => IsMythOwner(owner) && owner.IsAlive && IsSupportedOwnedMinion(owner, minion));
            if (!EnhancedGameplaySettings.Enabled || owner is null
                || !_ownedMinionControl.TryGetOrder(minion.ParticipantObject, out var order)
                || ValidateOwnedMinionOrder(minion, order, out _, out _) != OwnedMinionStatus.Accepted) {
                _ownedMinionControl.Withdraw(minion.ParticipantObject);
                RestoreOwnedMinionFallback(minion);
            }
        }
    }

    private void PublishOwnedMinionSnapshots() {
        foreach (var owner in SubCircles.Where(circle => circle.Occupied && _ownedMinionControl.IsOptedIn(circle.ParticipantObject)).ToArray()) {
            PublishOwnedMinionSnapshots(owner);
        }
    }

    private void PublishOwnedMinionSnapshots(CombatDuelSubCircle owner) {
        if (owner?.ParticipantActor is null || Duel is null) return;
        var available = TryOwnedMinionSnapshots(owner, out var snapshots) && EnhancedGameplaySettings.Enabled;
        if (!available) DisableOwnedMinionControl(owner.ParticipantObject);
        var response = new MSG_OWNEDMINIONRESPONSE {
            OwnerActor = owner.ParticipantActor, DuelID = Duel.m_duelID.Full, Round = Duel.m_roundNum,
            MinionID = 0, RequestID = 0, Accepted = available,
            Status = available ? OwnedMinionStatus.Accepted : OwnedMinionStatus.SnapshotUnavailable,
            Snapshots = available ? snapshots : [],
        };
        response.HelperView = BuildMinionHelperView(owner.ParticipantActor, response);
        owner.ParticipantActor.Tell(response);
    }

    private void ForgetOwnedMinion(CombatDuelSubCircle minion, CoreObject identity) {
        if (Duel?.m_duelPhase == kDuelPhase.kPhase_Planning)
            CombatResolver.AddCombatMove(CombatMoveType.ChangeMind, minion, null, null);
        if (identity is not null) {
            MinionLeftMinionHand(identity);
            _ownedMinionControl.Withdraw(identity);
            _ownedMinionFallbacks.Remove(identity);
            _ownedMinionHeldAiMoves.Remove(identity);
            _ownedControllableSummons.Remove(identity);
        }
        PublishOwnedMinionSnapshots();
        ReevaluateOwnedMinionPlanning();
    }
}
