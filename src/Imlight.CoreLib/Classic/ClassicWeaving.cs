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
 * CLASSIC OWNER EXTRA: SCHOOL WEAVING (combat side)
 * ========================================================================
 *
 * PURPOSE:
 * A Weave card (one per school, rules/weaving-*.yaml) dragged onto a trained
 * off-school single-target damage card in hand turns that card, for this
 * fight, into the weaver's school: the hand card becomes the curated client
 * face (so the client draws it, and pips, accuracy charms, blades, traps and
 * shields see the weaver's school), while the cast template is built here
 * from the ORIGINAL card plus the school's rider. The client's own Mutate
 * mechanic carries it (move type 5, kModifyCardMutation): no exe patch.
 *
 * NOTE:
 * Not 2010. Off unless features.experiments.weaving is true. Design and
 * worked numbers: ~/w101c-private/build/playbot-reports/design-weaving.md.
 *
 * Created by: Nick with Claude Code (claude-fable-5-1)
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic;

/// <summary>CLASSIC OWNER EXTRA: School Weaving.</summary>
internal static class ClassicWeaving {

    /// <summary>Resolves a client template path to its id (replaced in tests).</summary>
    internal static Func<string, ulong?> ResolveTemplate { get; set; } = CoreObjectFactory.TryGetTemplateIdByPath;

    /// <summary>Loads a template by id (replaced in tests).</summary>
    internal static Func<ulong, CoreTemplate?> LoadTemplate { get; set; } = id => CoreObjectFactory.GetCoreTemplate(id);

    /// <summary>The table in force, or Empty.</summary>
    internal static WeavingRules Rules => ClassicRuntime.IsInitialized && ClassicProgression.Weaving.AppliesTo(ClassicRuntime.Rules.Profile.Id)
        ? ClassicProgression.Weaving : WeavingRules.Empty;

    /// <summary>True while the experiment is switched on in the active profile and a table applies to it.</summary>
    internal static bool Active
        => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.ExperimentsWeaving) && Rules.Id.Length > 0;

    /// <summary>The school weave whose Weave card has template id <paramref name="templateId"/>, when weaving is active.</summary>
    internal static SchoolWeave? WeaveOfCard(ulong templateId) {
        if (!Active) return null;
        foreach (var weave in Rules.Weaves) {
            if (ResolveTemplate(weave.Card) == templateId) return weave;
        }
        return null;
    }

    /// <summary>
    /// Tries to weave <paramref name="target"/> with the Weave card <paramref name="source"/>. On success the hand card is
    /// the face template (school = weaver's school, pips = the original card's, accuracy = the original's minus the rider
    /// penalty) and <paramref name="castTemplate"/> is the original card converted to the school with the rider added.
    /// </summary>
    internal static bool TryPrepare(Spell source, Spell target, SpellTemplate original, bool pvp,
                                    out Spell? enchanted, out SpellTemplate? castTemplate, out string? refusal) {
        enchanted = null;
        castTemplate = null;
        refusal = null;
        if (WeaveOfCard(source.m_templateID) is not { } weave) return false; // not a Weave card: not ours
        var rules = Rules;
        if (pvp && !rules.Pvp) { refusal = "weaving is off in PvP"; return false; }
        if (rules.RiderFor(weave.School) is not { } rider) { refusal = "no rider"; return false; }
        var input = weave.Inputs.FirstOrDefault(pair => ResolveTemplate(pair.Input) == target.m_templateID);
        if (input is null) { refusal = "that card cannot be woven by this school"; return false; }
        if (string.Equals(original.m_sMagicSchoolName, SchoolName(weave.School), StringComparison.OrdinalIgnoreCase)) {
            refusal = "already the weaver's school"; return false;
        }
        if (ResolveTemplate(input.Face) is not { } faceId || LoadTemplate(faceId) is not SpellTemplate face) { refusal = "the face template is missing"; return false; }
        if (!string.Equals(face.m_sMagicSchoolName, SchoolName(weave.School), StringComparison.OrdinalIgnoreCase)) {
            refusal = "the face is not the weaver's school"; return false;
        }
        if (!TryBuildCast(original, rider, out var built, out refusal)) return false;

        var card = SpellFactory.GetSpell(face, unchecked((uint) faceId));
        card.m_treasureCard = false;
        card.m_itemCard = false;
        card.m_premutationSpellID = target.m_templateID;
        card.m_pipCost = original.m_spellRank; // the woven card costs what the original cost
        card.m_accuracy = (byte) Math.Clamp(target.m_accuracy - rider.AccuracyPenalty, 0, byte.MaxValue);
        card.m_enchantment = source.m_templateID;
        card.m_enchantmentSpellIsItemCard = source.m_itemCard;
        card.m_enchantedThisCombat = true;
        enchanted = card;
        castTemplate = built with { m_name = face.m_name, m_displayName = face.m_displayName, m_imageName = face.m_imageName };
        return true;
    }

    /// <summary>
    /// The cast template of a weave: every damage, damage-over-time and drain of <paramref name="original"/> becomes the
    /// rider's school at hit_percent, then the rider's own effects are added. Pure, so it is tested without a duel.
    /// </summary>
    internal static bool TryBuildCast(SpellTemplate original, WeaveRider rider, out SpellTemplate built, out string? refusal)
        => WeavingSpellBuilder.TryBuildCast(original, rider, out built, out refusal);

    /// <summary>True when <paramref name="path"/> is an active Weave card (a trainer may then teach it).</summary>
    internal static bool IsWeaveCard(string? path)
        => path is not null && Active && Rules.Weaves.Any(weave => string.Equals(weave.Card, path, StringComparison.Ordinal));

    /// <summary>False only for an active Weave card of another school than <paramref name="wizardSchool"/>.</summary>
    internal static bool MayTrain(string? wizardSchool, ulong templateId)
        => WeaveOfCard(templateId) is not { } weave || string.Equals(SchoolName(weave.School), wizardSchool, StringComparison.OrdinalIgnoreCase);

    /// <summary>The School Weaving deck limit for a Weave card: (copies allowed, must the deck be this school).</summary>
    internal static (int Copies, string School)? DeckLimit(ulong templateId)
        => WeaveOfCard(templateId) is { } weave ? (Rules.DeckCopies, SchoolName(weave.School)) : null;

    internal static string SchoolName(string school) => WeavingSpellBuilder.SchoolName(school);
}
