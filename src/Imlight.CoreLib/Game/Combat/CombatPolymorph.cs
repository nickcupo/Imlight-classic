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
 * MOON POLYMORPH (CELESTIA, OCTOBER 2010) AND THE OWNER'S MOON REWORK
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a Moon polymorph turns its caster into a creature for some rounds.
 * The archived October 2010 notes: the wizard looks like the form, and the
 * form changes "Max Health (Current Health is proportionally converted),
 * Power Pip Chance, School of Focus, Accuracy, Resistance to Damage,
 * Vulnerabilities and Damage"; "The new spell deck completely replaces the
 * spell deck"; "A Polymorphed player reverts back to their normal selves when
 * the polymorph time has expired." The form is the client's data
 * (PolymorphForms). While polymorphed the circle's stats, deck and the
 * participant's school are the form's; the wizard's own stats, deck and hand
 * wait in the polymorph state (the client's m_pSaved* fields say so too).
 *
 * OWNER EXTRA (MoonReworkSettings, [Classic] MoonRework; not 2010): the form
 * keeps a link to the wizard: max health is at least a share of the wizard's,
 * the wizard's gear for every school (damage, resist, accuracy, pip chance,
 * critical and block) carries over, and it hits 1% harder per level above
 * 50. It plays a real deck of its own cards (natural attacks four times,
 * the rest twice, reshuffled when it runs out), two cards of the hand the
 * wizard held stay reachable, and a Revert card is always in hand. It lasts
 * 8 rounds or until reverted. In PvP: once per match, level 50 or more, and
 * the health link is capped at 100%.
 *
 * USAGE EXAMPLE:
 * if (CombatPolymorph.TryBegin(caster, effect, action.Spell)) ...   // a polymorph card resolves
 * cinematic += CombatPolymorph.EndOfRound(circles, actions);           // after a round's actions
 *
 * NOTE:
 * The client draws the change itself: the cast plays the "Polymorph"
 * cinematic (ClientPolymorphCinematicAction swaps the model and rescales the
 * health globe), and a cast of "UnPolymorph - Moon" plays its UnPolymorph
 * stage. When the time runs out the server adds that cast to the round's
 * actions so the client reverts the look ([Classic] PolymorphRevertCast,
 * default true). Everything a duel ends, flees, drops or removes puts the
 * wizard back first (health converted back).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>CLASSIC: a polymorphed circle's form and what waits for the wizard's return.</summary>
internal sealed class PolymorphState {
    internal required PolymorphForm Form { get; init; }
    internal required uint SpellTemplateId { get; init; }
    internal required ServerWizGameStats CasterStats { get; init; }
    internal required ServerWizGameStats FormStats { get; init; }
    internal required CombatDeck FormDeck { get; init; }
    internal CombatDeck? CasterDeck { get; set; }
    internal WizGameStats? CasterCombatStats { get; set; }
    internal int CasterSchoolId { get; set; }
    internal int RoundsLeft { get; set; }
    internal bool CastThisRound { get; set; } = true;
    /// <summary>OWNER EXTRA: the Revert card kept in the form's hand (null without the rework).</summary>
    internal Spell? RevertCard { get; init; }
    /// <summary>OWNER EXTRA: how many cards of the wizard's held hand stay reachable (0 without the rework).</summary>
    internal int OwnSlots { get; init; }
    /// <summary>OWNER EXTRA: stun resistance the form gives (the May 2011 Gobbler fix), 0 to 100.</summary>
    internal int StunResistPercent { get; init; }
    internal bool Rework { get; init; }

    /// <summary>True when <paramref name="card"/> is one of the wizard's own held cards shown in the form's hand.</summary>
    internal bool IsOwn(Spell card) => CasterDeck?.LastGivenHand?.Any(held => ReferenceEquals(held, card)) == true;
}

public partial class CombatDuelSubCircle {

    /// <summary>CLASSIC: the polymorph this circle is in, or null.</summary>
    internal PolymorphState? Polymorph { get; private set; }

    /// <summary>CLASSIC: this occupant polymorphed in this duel (the rework's once-per-match PvP guard).</summary>
    internal bool PolymorphedThisMatch { get; set; }

    /// <summary>CLASSIC: a Star aura was cast this round (it counts down from the next round).</summary>
    internal bool AuraCastThisRound { get; set; }

    /// <summary>CLASSIC: becomes the form: its stats, deck, school and health, with the wizard's own kept aside.</summary>
    internal void EnterPolymorph(PolymorphState state) {
        Polymorph = state;
        PolymorphedThisMatch = true;
        ParticipantGameStats = state.FormStats;
        state.CasterDeck = _combatDeck;
        _combatDeck = state.FormDeck;
        if (CombatParticipant is { } participant) {
            state.CasterCombatStats = participant.m_pGameStats;
            state.CasterSchoolId = participant.m_primaryMagicSchoolID;
            participant.m_pSavedGameStats = participant.m_pGameStats;
            participant.m_savedPrimaryMagicSchoolID = participant.m_primaryMagicSchoolID;
            participant.m_pSavedHand = new Hand { m_spellList = [.. state.CasterDeck?.LastGivenHand ?? []] };
            participant.m_pSavedPlayDeck = participant.m_pPlayDeck;
            participant.m_pGameStats = SafeCombatStats(state.FormStats) ?? participant.m_pGameStats;
            participant.m_primaryMagicSchoolID = (int) state.Form.School;
            participant.m_nPolymorphTurnLength = state.RoundsLeft;
            participant.m_polymorphSpellTemplateID = state.SpellTemplateId;
            participant.m_playerHealth = state.FormStats.m_currentHitpoints;
            participant.m_maxPlayerHealth = state.FormStats.m_baseHitpoints;
        }
    }

    /// <summary>
    /// CLASSIC: the wizard comes back: own stats, deck and hand, with health converted from the form's share (0 when the form
    /// was defeated). Nothing happens when the circle is not polymorphed.
    /// </summary>
    internal void LeavePolymorph(bool defeated = false) {
        if (Polymorph is not { } state) {
            return;
        }

        Polymorph = null;
        var form = state.FormStats;
        ParticipantGameStats = state.CasterStats;
        _combatDeck = state.CasterDeck ?? _combatDeck;
        var health = defeated ? 0 : PolymorphMath.ConvertHealth(form.m_currentHitpoints, form.m_baseHitpoints, state.CasterStats.m_baseHitpoints);
        if (_wizard is not null) {
            _wizard.UpdateHealth(health);
        }
        else {
            state.CasterStats.m_currentHitpoints = health;
        }

        if (CombatParticipant is { } participant) {
            participant.m_pGameStats = state.CasterCombatStats ?? participant.m_pGameStats;
            participant.m_primaryMagicSchoolID = state.CasterSchoolId;
            participant.m_pSavedGameStats = null;
            participant.m_pSavedHand = null;
            participant.m_pSavedPlayDeck = null;
            participant.m_nPolymorphTurnLength = 0;
            participant.m_polymorphSpellTemplateID = 0;
            participant.m_playerHealth = health;
            participant.m_maxPlayerHealth = state.CasterStats.m_baseHitpoints;
        }
    }

    /// <summary>CLASSIC: puts the wizard back without a cinematic (the duel ends, a flee, a drop, a removal).</summary>
    internal void EndPolymorphSilently() => LeavePolymorph(defeated: Polymorph is { } state && state.FormStats.m_currentHitpoints <= 0);

    /// <summary>
    /// CLASSIC: the form's hand: the Revert card (rework), up to OwnSlots regular cards of the wizard's held hand (rework),
    /// then the form's own cards. A rework form whose deck runs out shuffles its played cards back in.
    /// </summary>
    private Hand DrawPolymorphHand(PolymorphState state) {
        var hand = _combatDeck.LastGivenHand;
        if (state.RevertCard is { } revert && !hand.Any(card => ReferenceEquals(card, revert))) {
            hand.Insert(0, revert);
        }

        if (state.OwnSlots > 0 && state.CasterDeck?.LastGivenHand is { } held) {
            var shown = hand.Count(state.IsOwn);
            foreach (var card in held) {
                if (shown >= state.OwnSlots) break;
                if (card.m_treasureCard || hand.Any(existing => ReferenceEquals(existing, card))) continue;
                hand.Add(card);
                shown++;
            }
        }

        if (state.Rework && _combatDeck.RemainingCardCount <= 0) {
            _combatDeck.Reshuffle();
        }

        var newHand = _combatDeck.GetHand();
        if (CombatParticipant is { } participant) participant.m_pHand = newHand;
        return newHand;
    }

    /// <summary>CLASSIC: a card played in form leaves the hand it came from (form, held wizard hand or the Revert card).</summary>
    private bool DiscardPolymorphCard(PolymorphState state, Spell spell) {
        if (state.RevertCard is { } revert && ReferenceEquals(spell, revert)) {
            RemoveReference(_combatDeck.LastGivenHand, spell);
            return true;
        }

        if (state.IsOwn(spell)) {
            RemoveReference(_combatDeck.LastGivenHand, spell);
            return state.CasterDeck!.Discard(spell);
        }

        return _combatDeck.Discard(spell);
    }

    private static void RemoveReference(List<Spell> hand, Spell spell) {
        var index = hand.FindIndex(card => ReferenceEquals(card, spell));
        if (index >= 0) hand.RemoveAt(index);
    }

    private static WizGameStats? SafeCombatStats(ServerWizGameStats stats) {
        try {
            return stats.GetCombatGameStats();
        }
        catch (Exception) {
            return null; // no classic rules or client config loaded (some tests)
        }
    }
}

/// <summary>CLASSIC: casting and ending Moon polymorphs.</summary>
internal static class CombatPolymorph {

    /// <summary>The wait for the revert cinematic the server adds when a form's time runs out (Summon + UnPolymorph stage).</summary>
    internal const float RevertCinematicSeconds = 3.5f;

    /// <summary>[Classic] PolymorphRevertCast: add the UnPolymorph cast when a form times out (default true).</summary>
    internal static bool RevertCastEnabled {
        get {
            if (RevertCastOverrideForTests is { } forced) return forced;
            try {
                var value = ConfigurationManager.Settings["Classic.PolymorphRevertCast"].AsString();
                return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value.Trim(), out var on) && on);
            }
            catch (Exception) {
                return true;
            }
        }
    }

    internal static bool? RevertCastOverrideForTests { get; set; }

    /// <summary>The client's revert card, "UnPolymorph - Moon" (kUnPolymorph on self), used for every revert.</summary>
    internal const string RevertCardPath = "Spells/UnPolymorph - Moon.xml";

    /// <summary>True for a card whose only effect is a polymorph (every Moon card).</summary>
    internal static SpellEffect? PolymorphEffectOf(SpellTemplate? template)
        => ClassicCelestiaRules.Active && template?.m_effects is { Count: 1 } effects
            && effects[0] is { m_effectType: kSpellEffects.kPolymorph, m_effectTarget: kEffectTarget.kSelf } effect
            && effect.GetType() == typeof(SpellEffect) ? effect : null;

    /// <summary>True for a card whose only effect reverts the caster (the Revert / UnPolymorph card).</summary>
    internal static bool IsRevertCard(SpellTemplate? template)
        => ClassicCelestiaRules.Active && template?.m_effects is { Count: 1 } effects
            && effects[0] is { m_effectType: kSpellEffects.kUnPolymorph, m_effectTarget: kEffectTarget.kSelf };

    /// <summary>
    /// A polymorph card resolves on its caster. False (the cast does nothing, like a pass) when the circle cannot polymorph:
    /// no Celestia profile, a minion, already polymorphed, no form in the client data, or a rework PvP guard.
    /// </summary>
    internal static bool TryBegin(CombatDuelSubCircle caster, SpellEffect effect, Spell? spell) {
        if (!ClassicCelestiaRules.Active || caster?.CombatParticipant is null || caster.ParticipantGameStats is not { } casterStats
            || caster.IsSummonedMinion || caster.Polymorph is not null || !caster.IsAlive
            || PolymorphForms.Resolve(unchecked((uint) effect.m_effectParam)) is not { } form) {
            return false;
        }

        var rework = MoonReworkSettings.Current;
        var pvp = caster._duelActor?.Duel?.m_bPVP == true;
        var level = casterStats.Level > 0 ? casterStats.Level : form.Level;
        if (rework is not null && pvp && (level < rework.Pvp.MinLevel || (rework.Pvp.OncePerMatch && caster.PolymorphedThisMatch))) {
            Logger.Debug("Moon rework: polymorph refused in PvP (level {0}, already polymorphed {1}).", Logger.Args(level, caster.PolymorphedThisMatch));
            return false;
        }

        var reworkForm = rework?.FormFor(MoonRecordIdFor(spell));
        var formMax = rework is null
            ? form.Health
            : PolymorphMath.ReworkFormMaxHealth(form.Health, casterStats.m_baseHitpoints, reworkForm?.HealthLinkPercent ?? 100,
                pvp ? rework.Pvp.MaxHealthLinkPercent : null);
        var formStats = BuildStats(form, casterStats, level, formMax, rework);
        formStats.m_currentHitpoints = PolymorphMath.ConvertHealth(casterStats.m_currentHitpoints, casterStats.m_baseHitpoints, formMax);

        var seed = caster._duelActor?.Rng.Next() ?? Environment.TickCount;
        var state = new PolymorphState {
            Form = form,
            SpellTemplateId = spell?.m_templateID ?? 0,
            CasterStats = casterStats,
            FormStats = formStats,
            FormDeck = new CombatDeck(DeckOf(form, rework), [], 7, new Random(seed)),
            RoundsLeft = rework?.Rounds ?? Math.Max(1, effect.m_numRounds),
            RevertCard = rework is null ? null : RevertCard(),
            OwnSlots = rework?.OwnDeckSlots ?? 0,
            StunResistPercent = rework is null ? 0 : reworkForm?.StunResistPercent ?? 0,
            Rework = rework is not null,
        };

        caster.EnterPolymorph(state);
        Logger.Information("Polymorph: slot {0} becomes the {1} ({2} health of {3}, {4} rounds{5}).",
            Logger.Args(caster.SlotIndex, form.Name, formStats.m_currentHitpoints, formMax, state.RoundsLeft,
                rework is null ? ", dated 2010 form" : ", Moon rework"));
        return true;
    }

    /// <summary>The Revert card ends the caster's form here (its cast plays the client's UnPolymorph stage).</summary>
    internal static bool Revert(CombatDuelSubCircle caster) {
        if (caster?.Polymorph is null) {
            return false;
        }

        caster.LeavePolymorph();
        return true;
    }

    /// <summary>
    /// The end of a round: forms cast before this round count down; a form at 0 reverts, and (with
    /// <see cref="RevertCastEnabled"/>) the round's actions get the UnPolymorph cast so the client puts the wizard's look
    /// back. Returns the cinematic time added.
    /// </summary>
    internal static float EndOfRound(IEnumerable<CombatDuelSubCircle> circles, CombatActionListObj? actions) {
        var time = 0f;
        foreach (var circle in circles) {
            if (circle?.Polymorph is not { } state) continue;
            if (state.CastThisRound) {
                state.CastThisRound = false;
                continue;
            }

            state.RoundsLeft--;
            if (circle.CombatParticipant is { } participant) participant.m_nPolymorphTurnLength = Math.Max(state.RoundsLeft, 0);
            if (state.RoundsLeft > 0) continue;

            if (RevertCastEnabled && actions?.m_actionList is { } list && RevertCard() is { } card) {
                list.Add(new CombatAction {
                    m_spellCaster = circle.SlotIndex,
                    m_targetSubcircleList = [circle.SlotIndex],
                    m_showCast = true,
                    m_spellHits = (char) 1,
                    m_spell = card,
                    m_serializedBlocks = "\u0000\u0000",
                    m_shadowPactTarget = -1,
                    m_petCastTarget = -1,
                    m_CritHitList = [],
                });
                time += RevertCinematicSeconds;
            }

            circle.LeavePolymorph();
            Logger.Information("Polymorph: slot {0} reverts; the form's time is up.", Logger.Args(circle.SlotIndex));
        }

        return time;
    }

    /// <summary>A fresh Revert card (no pips: the server never charges for it).</summary>
    internal static Spell? RevertCard() {
        var path = MoonReworkSettings.Current?.RevertCard ?? RevertCardPath;
        Spell? spell;
        try {
            spell = CoreObjectFactory.TryGetTemplateIdByPath(path) is { } id ? SpellFactory.GetSpell((uint) id) : null;
        }
        catch (Exception) {
            spell = null; // no client data loaded (tests, tools)
        }
        if (spell is null) {
            return null;
        }

        spell.m_pipCost = new SpellRank();
        return spell;
    }

    /// <summary>The Moon spell record id of a polymorph card (by its client template), or null.</summary>
    internal static string? MoonRecordIdFor(Spell? spell) {
        if (spell is null) return null;
        string? path;
        try {
            path = CoreObjectFactory.GetTemplatePath(spell.m_templateID);
        }
        catch (Exception) {
            return null; // no client data loaded (tests, tools)
        }
        return ClassicSpellTemplates.Records
            .FirstOrDefault(record => record.School == "moon" && string.Equals(record.ClientTemplate, path, StringComparison.Ordinal))?.Id;
    }

    /// <summary>
    /// The form's stats: its school, the caster's level, its equipment (resistances, damage, accuracy), its max health and the
    /// level's power pip chance (client values). With the rework the wizard's gear for every school, critical and block and
    /// the level bonus carry over.
    /// </summary>
    internal static ServerWizGameStats BuildStats(PolymorphForm form, ServerWizGameStats caster, int level, int formMax, MoonRework? rework) {
        var stats = NewStats(form.School, level);
        foreach (var item in form.Equipment) {
            CharacterEffectHelper.AddEffectsToGameStats(stats, item);
        }

        stats.m_baseHitpoints = formMax;
        stats.m_baseMana = caster.m_baseMana;
        stats.m_currentMana = caster.m_currentMana;
        stats.m_powerPipBase = LevelPipChance(form.School, level) ?? caster.m_powerPipBase;
        if (rework is null) {
            return stats;
        }

        if (rework.KeepCasterUniversalGear) {
            stats.m_dmgBonusPercentAll += caster.m_dmgBonusPercentAll;
            stats.m_dmgBonusFlatAll += caster.m_dmgBonusFlatAll;
            stats.m_dmgReducePercentAll += caster.m_dmgReducePercentAll;
            stats.m_dmgReduceFlatAll += caster.m_dmgReduceFlatAll;
            stats.m_accBonusPercentAll += caster.m_accBonusPercentAll;
            stats.m_healBonusPercentAll += caster.m_healBonusPercentAll;
            stats.m_healIncBonusPercentAll += caster.m_healIncBonusPercentAll;
            stats.m_powerPipBonusPercentAll += caster.m_powerPipBonusPercentAll;
        }

        if (rework.KeepCasterCriticalAndBlock) {
            stats.m_criticalHitRatingAll += caster.m_criticalHitRatingAll;
            stats.m_blockRatingAll += caster.m_blockRatingAll;
            stats.m_criticalHitRatingBySchool = Add(stats.m_criticalHitRatingBySchool, caster.m_criticalHitRatingBySchool);
            stats.m_blockRatingBySchool = Add(stats.m_blockRatingBySchool, caster.m_blockRatingBySchool);
        }

        stats.m_dmgBonusPercentAll += PolymorphMath.ReworkLevelDamageBonus(level, rework.LevelDamagePercentPerLevel, rework.LevelDamagePercentMax);
        return stats;
    }

    /// <summary>
    /// The form's deck: the client deck's cards. Dated: endless, as every creature's deck (the form is a creature template).
    /// Rework: a real deck, natural attacks <c>NaturalAttackCopies</c> times and the rest <c>UtilityCopies</c> times.
    /// </summary>
    internal static List<CombatDeckSpellData> DeckOf(PolymorphForm form, MoonRework? rework)
        => [.. form.Cards.GroupBy(card => card.TemplateId).Select(group => new CombatDeckSpellData {
            TemplateId = group.Key,
            Quantity = rework is null ? 9999u
                : (uint) (group.Count() * (group.First().NaturalAttack ? rework.NaturalAttackCopies : rework.UtilityCopies)),
        })];

    private static ServerWizGameStats NewStats(MagicSchool school, int level) {
        try {
            return new ServerWizGameStats(school, level);
        }
        catch (Exception) {
            // No configuration loaded (tests): the same fields without the gold pouch setting.
            var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
            stats.MagicSchool = school;
            stats.m_schoolID = (uint) school;
            stats.Level = level;
            return stats;
        }
    }

    private static float? LevelPipChance(MagicSchool school, int level) {
        try {
            return MagicLevelsConfig.GetPlayerLevelInfo(school, level)?.m_pipChance;
        }
        catch (Exception) {
            return null;
        }
    }

    private static List<float>? Add(List<float>? a, List<float>? b) {
        if (b is null) return a;
        if (a is null) return [.. b];
        var sum = new List<float>(Math.Max(a.Count, b.Count));
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++) {
            sum.Add((i < a.Count ? a[i] : 0f) + (i < b.Count ? b[i] : 0f));
        }
        return sum;
    }
}
