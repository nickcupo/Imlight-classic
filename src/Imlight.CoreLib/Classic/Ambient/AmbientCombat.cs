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
 * AMBIENT COMBAT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: an ambient wizard in a duel. It sits in a player slot and plays
 * by the 2009 rules like a player (its deck is its school's trained
 * spells up to its level, from the classic spell records), but it has no
 * client: in each planning phase the duel asks AllyBrain for its move a
 * few seconds in (as a person would take), reading the duel's own state
 * on the duel's actor thread, and queues it through the normal combat
 * move path. This file turns duel state into an AllyView, builds the
 * deck, and holds the PvP sparring hook.
 *
 * USAGE EXAMPLE:
 * var move = AmbientCombat.Choose(duel, circle);          // in CombatDuelComponent
 * var deck = AmbientCombat.DeckFor(MagicSchool.Fire, 12);  // when the wizard is built
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A duel's reminder to itself to play an ambient wizard's turn.</summary>
internal sealed record AmbientTurn(int Slot, int Round);

/// <summary>Duel state for ambient wizards (see the file header).</summary>
internal static class AmbientCombat {

    /// <summary>Seconds into planning before an ambient wizard picks its card: 2 to 5, by slot and round.</summary>
    internal static TimeSpan ThinkingTime(int slot, int round) => TimeSpan.FromSeconds(2 + (slot * 7 + round * 3) % 4);

    /// <summary>The move for <paramref name="me"/>, as an MSG_ACTORCOMBATMOVE the duel queues as usual.</summary>
    internal static COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE Choose(CombatDuelComponent duel, CombatDuelSubCircle me,
                                                                    out AllyMove move) {
        var view = ViewFor(duel, me);
        move = AllyBrain.Choose(view);

        return move.Kind == AllyMoveKind.Cast
            ? new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor = me.ParticipantActor,
                MoveType = (byte) CombatMoveType.Attack,
                SpellSelection = (byte) move.HandIndex,
                SpellTarget = move.TargetSlot < 0 ? uint.MaxValue : (uint) move.TargetSlot,
            }
            : new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor = me.ParticipantActor, MoveType = (byte) CombatMoveType.Pass,
            };
    }

    /// <summary>The ally's view of the duel.</summary>
    internal static AllyView ViewFor(CombatDuelComponent duel, CombatDuelSubCircle me) {
        var participant = me.CombatParticipant;
        var pips = participant?.m_pipCount is { } count ? count.m_genericPips + count.m_powerPips * 2 : 0;
        var hand = me.GetCurrentHand()?.m_spellList ?? [];
        var cards = new List<AllyCard>(hand.Count);
        for (var i = 0; i < hand.Count; i++) {
            if (hand[i] is { } spell && CardFor(i, spell) is { } card) {
                cards.Add(card);
            }
        }

        var team = me.OccupiedTeam;
        var circles = duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true }).ToList();
        var hits = new Dictionary<int, int>();
        foreach (var mate in circles.Where(c => c != me && c.OccupiedTeam == team)) {
            if (duel.CombatResolver?.GetQueuedAction(mate)?.SelectedTarget is { } target && target.OccupiedTeam != team) {
                hits[target.SlotIndex] = hits.GetValueOrDefault(target.SlotIndex) + 1;
            }
        }

        var combatants = circles.Select(c => new AllyCombatant(
            c.SlotIndex, c.OccupiedTeam == team,
            Math.Max(0, c.ParticipantGameStats?.m_currentHitpoints ?? 0),
            Math.Max(1, c.ParticipantGameStats?.m_baseHitpoints ?? 1),
            hits.GetValueOrDefault(c.SlotIndex),
            Count(c, kSpellEffects.kModifyOutgoingDamage),
            Count(c, kSpellEffects.kModifyIncomingDamage))).ToList();

        return new AllyView(me.SlotIndex, pips, (participant?.m_stunned ?? 0) > 0, cards, combatants);
    }

    private static int Count(CombatDuelSubCircle circle, kSpellEffects type)
        => circle.CombatParticipant?.m_hangingEffects?.Count(e => e?.m_effectType == type && e.m_effectParam > 0) ?? 0;

    /// <summary>What a hand card is for, from its template's effects.</summary>
    internal static AllyCard CardFor(int index, Spell spell) {
        if (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template || template.m_effects is null) {
            return null;
        }

        var effects = Flatten(template.m_effects).ToList();
        var rank = spell.m_pipCost?.m_spellRank ?? 0;
        var accuracy = Math.Clamp((spell.m_accuracy > 0 ? spell.m_accuracy : template.m_accuracy) / 100.0, 0.05, 1.0);
        var name = template.m_name ?? $"spell {spell.m_templateID}";

        var damage = effects.Where(e => e.m_effectType is kSpellEffects.kDamage or kSpellEffects.kDamageNoCrit
                                        or kSpellEffects.kDamageOverTime).ToList();
        if (damage.Count > 0) {
            var amount = damage.Sum(e => e.m_effectType == kSpellEffects.kDamageOverTime
                ? Math.Max(e.m_effectParam, e.m_paramPerRound * Math.Max(1, e.m_numRounds))
                : e.m_effectParam);
            var all = damage.Any(e => e.m_effectTarget is kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce);

            return new AllyCard(index, name, rank, accuracy, AllyCardRole.Damage, amount, amount, AllEnemies: all);
        }

        var heal = effects.Where(e => e.m_effectType is kSpellEffects.kHeal or kSpellEffects.kHealOverTime).ToList();
        if (heal.Count > 0) {
            var team = heal.Any(e => e.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce);
            return new AllyCard(index, name, rank, accuracy, AllyCardRole.Heal, Heal: heal.Sum(e => e.m_effectParam), HealsTeam: team);
        }

        if (effects.Any(e => e.m_effectType == kSpellEffects.kModifyOutgoingDamage && e.m_effectParam > 0)) {
            return new AllyCard(index, name, rank, accuracy, AllyCardRole.Blade);
        }

        if (effects.Any(e => e.m_effectType == kSpellEffects.kModifyIncomingDamage && e.m_effectParam > 0
                             && e.m_effectTarget is kEffectTarget.kEnemySingle or kEffectTarget.kEnemyTeam)) {
            return new AllyCard(index, name, rank, accuracy, AllyCardRole.Trap);
        }

        return new AllyCard(index, name, rank, accuracy, AllyCardRole.Other);
    }

    // A compound card (a random or variable spell) keeps its parts in child effects.
    private static IEnumerable<SpellEffect> Flatten(IEnumerable<SpellEffect> effects) {
        foreach (var effect in effects) {
            if (effect is null) {
                continue;
            }

            var children = SpellTemplateEditor.ChildrenOf(effect);
            if (children is { Count: > 0 }) {
                foreach (var child in Flatten(children)) {
                    yield return child;
                }
            }
            else {
                yield return effect;
            }
        }
    }

    /// <summary>
    /// A 2009 deck for the school and level: the school's trained spells up to the level (classic spell records),
    /// two copies of each (four of the cheapest damage spell), plus the Life heal everyone could learn (Fairy) at 14+.
    /// </summary>
    internal static List<SpellData> DeckFor(MagicSchool school, int level) {
        var schoolName = school.ToString().ToLowerInvariant();
        var records = ClassicSpellTemplates.Records
            .Where(r => r.Kind == "trained" && r.ClientTemplate is not null
                        && string.Equals(r.School, schoolName, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Record: r, Level: r.Values.LevelLearned ?? 99))
            .Where(r => r.Level <= level)
            .OrderBy(r => r.Level)
            .ToList();

        var deck = new List<SpellData>();
        foreach (var (record, _) in records) {
            if (CoreObjectFactory.TryGetTemplateIdByPath(record.ClientTemplate) is not { } id) {
                continue;
            }

            deck.Add(new SpellData { m_templateID = (uint) id, m_quantity = (uint) (deck.Count == 0 ? 4 : 2) });
        }

        if (level >= 14 && school != MagicSchool.Life
            && ClassicSpellTemplates.Records.FirstOrDefault(r => r.Name == "Fairy") is { ClientTemplate: { } fairy }
            && CoreObjectFactory.TryGetTemplateIdByPath(fairy) is { } fairyId) {
            deck.Add(new SpellData { m_templateID = (uint) fairyId, m_quantity = 2 });
        }

        return deck;
    }

}

/// <summary>
/// CLASSIC: the hook for the open-PvP circle (owner, 2026-10-01): when no real players are around, a player can invite
/// an ambient wizard to a practice match. The PvP code (branch claude/server-features) asks who is free in the zone,
/// seats one through <see cref="TrySeat"/> (which marks it busy and returns the actor and object to put in its circle's
/// slot), lets the duel ask <see cref="AmbientCombat"/> for its moves like any ambient ally, and calls
/// <see cref="Release"/> when the match ends. Ambient wizards never join PvP any other way.
/// </summary>
public static class AmbientSparring {

    /// <summary>An ambient wizard free for a practice match.</summary>
    public sealed record Partner(ulong CharId, string Name, int Level, string School);

    /// <summary>Up to <paramref name="max"/> ambient wizards in <paramref name="zone"/> that are not busy.</summary>
    public static IReadOnlyList<Partner> Available(string zone, int max = 7)
        => [.. AmbientWizards.All
            .Where(w => w.Present && string.Equals(w.Zone, zone, StringComparison.OrdinalIgnoreCase)
                        && w.Activity is AmbientActivity.Idle or AmbientActivity.Walking or AmbientActivity.Shopping)
            .OrderBy(w => w.CharId).Take(Math.Max(0, max))
            .Select(w => new Partner(w.CharId, w.Name, w.Wizard.MagicSchoolBehavior.Level, w.Identity.School.ToString()))];

    /// <summary>
    /// Seats an ambient wizard for a PvP match in the circle <paramref name="sigilId"/>: it stops wandering and may only
    /// join that circle. The caller adds <paramref name="actor"/> with <paramref name="playerObject"/> to its slot.
    /// </summary>
    /// <returns>False when the wizard is not free.</returns>
    public static bool TrySeat(ulong ambientCharId, ulong sigilId, out IActorRef actor, out CoreObject playerObject) {
        actor = null;
        playerObject = null;
        if (!AmbientWizards.TryGet(ambientCharId, out var wizard) || !wizard.Present || wizard.Endpoint is null
            || wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Helping) {
            return false;
        }

        AmbientWizards.MarkSparring(wizard.Endpoint, sigilId);
        AmbientWizards.PermitJoin(wizard.Endpoint, sigilId);
        wizard.Endpoint.Tell(new AmbientSparringSeat(sigilId));
        (actor, playerObject) = (wizard.Endpoint, wizard.Wizard.GameObject);
        Logger.Information("Ambient wizard {Name} seated for a PvP practice match in circle {Sigil}.",
            Logger.Args(wizard.Name, sigilId));

        return true;
    }

    /// <summary>The match is over: the wizard goes back to what it was doing.</summary>
    public static void Release(ulong ambientCharId) {
        if (AmbientWizards.TryGet(ambientCharId, out var wizard) && wizard.Endpoint is not null) {
            AmbientWizards.EndSparring(wizard.Endpoint);
            AmbientWizards.RevokeJoin(wizard.Endpoint);
            wizard.Endpoint.Tell(new AmbientSparringSeat(0));
        }
    }

    /// <summary>The ally rule set, for a PvP duel that builds its own view.</summary>
    public static AllyMove ChooseMove(AllyView view) => AllyBrain.Choose(view);

}

/// <summary>Tells an ambient wizard it was seated for (or released from, sigil 0) a PvP practice match.</summary>
internal sealed record AmbientSparringSeat(ulong SigilId);
