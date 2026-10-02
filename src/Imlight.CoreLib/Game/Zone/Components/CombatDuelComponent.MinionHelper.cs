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
 * MINION HELPER VIEW
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the duel as the Minion Helper shows it. Built on the duel's own
 * thread from the same state that checks orders, so the helper never guesses:
 * each card lists exactly the sigil slots the server would accept for it, and
 * a card the minion cannot cast says why. Also lets an owner take control for
 * a whole duel from any phase (MSG_OWNEDMINIONOPTIN).
 *
 * The view (one JSON object; ids are numbers):
 *   {"op":"state","phase":"planning"|"execution"|"resolution"|"waiting",
 *    "duel":D,"round":R,"secondsLeft":S,"myth":true,"controlling":true,
 *    "request":N,"accepted":true,"status":"Accepted",
 *    "minions":[{"id":M,"slot":5,"name":"Troll","school":"Myth","health":..,"maxHealth":..,
 *                "pips":3,"powerPips":0,"order":{"move":"cast","card":2,"target":0}|null,
 *                "hand":[{"index":0,"spell":TID,"name":"Blood Bat","school":"Myth","pips":1,"xPips":false,
 *                         "accuracy":80,"treasure":false,"enchant":"","castable":true,
 *                         "targets":[0,1],"untargeted":false,"reason":""}]}],
 *    "combatants":[{"slot":0,"name":"..","side":"enemy"|"ally","health":..,"maxHealth":..,
 *                   "alive":true,"minion":false,"you":false,"yours":false,"school":".."}]}
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    /// <summary>True when the owner (not the AI) chose this minion's move this round.</summary>
    private bool IsOwnerChosenMinionMove(CombatDuelSubCircle minion)
        => _ownedMinionControl.TryGetOrder(minion.ParticipantObject, out var order) && order.MoveType != OwnedMinionAiMove;

    private static bool IsMythWizard(CombatDuelSubCircle circle)
        => circle is { IsSummonedMinion: false } && circle._wizard?.MagicSchoolBehavior?.MagicSchool == MagicSchool.Myth;

    [MessageHandler(typeof(MSG_OWNEDMINIONOPTIN))]
    private void ReceiveOwnedMinionOptIn(MSG_OWNEDMINIONOPTIN message) {
        if (!_isActive || Duel is null || SubCircles is null) return;
        var owner = SubCircles.FirstOrDefault(circle => circle.Occupied && circle.ParticipantActor == message.OwnerActor);
        if (owner is null) return;
        // The owner may not be added to the duel yet (this comes as they join), so check the wizard's school only.
        var myth = IsMythWizard(owner);
        var status = !EnhancedGameplaySettings.Enabled ? OwnedMinionStatus.Disabled
            : !myth ? OwnedMinionStatus.NotMyth : OwnedMinionStatus.Accepted;
        if (status == OwnedMinionStatus.Accepted) {
            if (message.Enable) _ownedMinionControl.OptIn(owner.ParticipantObject);
            PublishOwnedMinionSnapshots(owner);
            ReevaluateOwnedMinionPlanning();
            return;
        }

        var response = new MSG_OWNEDMINIONRESPONSE {
            OwnerActor = owner.ParticipantActor, DuelID = Duel.m_duelID.Full, Round = Duel.m_roundNum, Status = status,
        };
        response.HelperView = BuildMinionHelperView(owner.ParticipantActor, response);
        owner.ParticipantActor?.Tell(response);
    }

    /// <summary>The helper's view of this duel for the owner behind <paramref name="ownerActor"/>.</summary>
    internal string BuildMinionHelperView(IActorRef ownerActor, MSG_OWNEDMINIONRESPONSE response) {
        try {
            return BuildMinionHelperViewUnsafe(ownerActor, response);
        } catch (Exception ex) {
            // The view is a convenience; never let it break the duel.
            Logger.Warning("Duel {0} | Minion Helper view failed: {1}", Logger.Args(Duel?.m_duelID.Full, ex.Message));
            return null;
        }
    }

    private string BuildMinionHelperViewUnsafe(IActorRef ownerActor, MSG_OWNEDMINIONRESPONSE response) {
        if (Duel is null || SubCircles is null || ownerActor is null) return null;
        var owner = SubCircles.FirstOrDefault(circle => circle.Occupied && circle.ParticipantActor == ownerActor);
        if (owner is null) return null;

        var planning = Duel.m_duelPhase == kDuelPhase.kPhase_Planning && (_awaitingCombatMoves || _ownedMinionEarlyFinishScheduled);
        var phase = Duel.m_duelPhase switch {
            kDuelPhase.kPhase_Planning => planning ? "planning" : "waiting",
            kDuelPhase.kPhase_Execution => "execution",
            kDuelPhase.kPhase_Resolution => "resolution",
            _ => "waiting",
        };
        var secondsLeft = planning ? Math.Max(0, (_ownedMinionPlanningDeadline - DateTime.UtcNow).TotalSeconds) : 0;
        var myth = IsMythWizard(owner);

        var minions = new List<object>();
        if (myth) {
            foreach (var minion in SubCircles.Where(circle => IsSupportedOwnedMinion(owner, circle))) {
                minions.Add(MinionView(minion, planning));
            }
        }

        var combatants = SubCircles.Where(circle => circle.Occupied && circle.AddedToDuel).Select(circle => new {
            slot = circle.SlotIndex,
            name = ParticipantName(circle),
            side = circle.OccupiedTeam == owner.OccupiedTeam ? "ally" : "enemy",
            health = Math.Max(0, circle.ParticipantGameStats?.m_currentHitpoints ?? 0),
            maxHealth = MaxHealth(circle),
            alive = circle.IsAlive,
            minion = circle.IsSummonedMinion,
            you = ReferenceEquals(circle, owner),
            yours = circle.IsSummonedMinion && circle.IsOwnedMinionOf(owner),
            school = SchoolName(circle.CombatParticipant?.m_primaryMagicSchoolID ?? 0),
        }).ToArray();

        return JsonSerializer.Serialize(new {
            op = "state", phase, duel = Duel.m_duelID.Full.ToString(), round = Duel.m_roundNum, wizard = ParticipantName(owner),
            secondsLeft = Math.Round(secondsLeft, 1), myth,
            controlling = myth && _ownedMinionControl.IsOptedIn(owner.ParticipantObject),
            request = response?.RequestID ?? 0, accepted = response?.Accepted ?? false,
            status = (response?.Status ?? OwnedMinionStatus.Accepted).ToString(),
            minions, combatants,
        });
    }

    private object MinionView(CombatDuelSubCircle minion, bool planning) {
        var hand = new List<object>();
        var cards = minion._combatDeck?.LastGivenHand ?? [];
        for (var index = 0; index < cards.Count && index < byte.MaxValue; index++) {
            hand.Add(CardView(minion, cards[index], (byte) index, planning));
        }

        object order = null;
        if (_ownedMinionControl.TryGetOrder(minion.ParticipantObject, out var chosen)) {
            order = new {
                move = chosen.MoveType switch {
                    (byte) CombatMoveType.Attack => "cast",
                    (byte) CombatMoveType.Pass => "pass",
                    OwnedMinionAiMove => "ai",
                    _ => "other",
                },
                card = (int) chosen.SpellSelection,
                target = chosen.SpellTarget == uint.MaxValue ? -1 : (long) chosen.SpellTarget,
            };
        }

        return new {
            id = minion.ParticipantObject.m_globalID.Full.ToString(), // strings: JavaScript numbers lose 64-bit ids
            slot = minion.SlotIndex,
            name = ParticipantName(minion),
            school = SchoolName(minion.CombatParticipant?.m_primaryMagicSchoolID ?? 0),
            health = Math.Max(0, minion.ParticipantGameStats?.m_currentHitpoints ?? 0),
            maxHealth = MaxHealth(minion),
            pips = (int) (minion.CombatParticipant?.m_pipCount?.m_genericPips ?? 0),
            powerPips = (int) (minion.CombatParticipant?.m_pipCount?.m_powerPips ?? 0),
            stunned = (minion.CombatParticipant?.m_stunned ?? 0) > 0,
            order, hand,
        };
    }

    private object CardView(CombatDuelSubCircle minion, Spell spell, byte index, bool planning) {
        var template = spell is null ? null : SafeTemplate(spell.m_templateID) as SpellTemplate;
        var targets = new List<int>();
        var untargeted = false;
        var reason = OwnedMinionStatus.InvalidCard;
        if (spell is not null) {
            var none = ValidateOwnedMinionOrder(minion, new OwnedMinionOrder((byte) CombatMoveType.Attack, index, uint.MaxValue), out _, out _);
            untargeted = none == OwnedMinionStatus.Accepted;
            reason = none;
            for (var slot = 0; slot < SubCircles.Length; slot++) {
                var status = ValidateOwnedMinionOrder(minion, new OwnedMinionOrder((byte) CombatMoveType.Attack, index, (uint) slot), out _, out _);
                if (status == OwnedMinionStatus.Accepted) targets.Add(slot);
                else if (reason != OwnedMinionStatus.Accepted && status != OwnedMinionStatus.InvalidTarget) reason = status;
            }

            if (targets.Count > 0) reason = OwnedMinionStatus.Accepted;
        }

        var castable = spell is not null && (untargeted || targets.Count > 0);
        var enchant = spell is { m_enchantment: not 0 } && SafeTemplate(spell.m_enchantment) is SpellTemplate enchantTemplate
            ? SpellName(enchantTemplate) : "";
        return new {
            index = (int) index,
            spell = spell?.m_templateID ?? 0,
            name = template is null ? $"Spell {spell?.m_templateID}" : SpellName(template),
            school = template?.m_sMagicSchoolName ?? "",
            pips = (int) (spell?.m_pipCost?.m_spellRank ?? 0),
            xPips = spell is not null && CombatActionResolver.IsXPipSpell(spell),
            accuracy = (int) (spell?.m_accuracy ?? 0),
            treasure = spell?.m_treasureCard ?? false,
            enchant,
            castable,
            targets,
            untargeted,
            reason = castable ? "" : ReasonText(reason),
        };
    }

    private static string ReasonText(OwnedMinionStatus status) => status switch {
        OwnedMinionStatus.InsufficientPips => "not enough pips",
        OwnedMinionStatus.Disabled => "Monstrology is off",
        OwnedMinionStatus.InvalidTarget => "no target",
        _ => "cannot be cast",
    };

    private static string SpellName(SpellTemplate template) {
        var name = string.IsNullOrEmpty(template.m_displayName) ? "" : Locale.GetEnglishName(template.m_displayName);
        return string.IsNullOrEmpty(name) ? template.m_name ?? "" : name;
    }

    private static string ParticipantName(CombatDuelSubCircle circle) {
        if (circle._wizard?.PlayerNameBehavior is { } names) {
            var wizardName = names.GetWizardName();
            if (!string.IsNullOrEmpty(wizardName)) return wizardName;
        }

        var objectName = circle.ParticipantObject?.m_debugName;
        if (SafeTemplate(circle.ParticipantObject?.m_templateID ?? 0) is GameObjectTemplate template
            && !string.IsNullOrEmpty(template.m_displayName)) {
            var display = Locale.GetEnglishName(template.m_displayName);
            if (!string.IsNullOrEmpty(display)) return display;
        }

        return string.IsNullOrEmpty(objectName) ? $"Slot {circle.SlotIndex + 1}" : objectName;
    }

    private static CoreTemplate SafeTemplate(ulong id) {
        if (id == 0) return null;
        try {
            return CoreObjectFactory.GetCoreTemplate(id);
        } catch (Exception) {
            return null; // Tests without the template index; a name falls back to the object's debug name.
        }
    }

    private static int MaxHealth(CombatDuelSubCircle circle) {
        var stats = circle.ParticipantGameStats;
        if (stats is null) return 0;
        var max = stats.m_baseHitpoints + stats.m_bonusHitpoints;
        return Math.Max(max, stats.m_currentHitpoints);
    }

    private static string SchoolName(int schoolId)
        => schoolId != 0 && Enum.IsDefined(typeof(MagicSchool), schoolId) ? ((MagicSchool) schoolId).ToString() : "";
}
