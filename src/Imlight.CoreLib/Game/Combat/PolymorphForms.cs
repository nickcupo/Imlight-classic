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
 * MOON POLYMORPH FORMS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what a Moon polymorph turns its caster into, read from the
 * client's own data: the polymorph card's kPolymorph effect names a
 * WizPolymorphTemplate (ObjectData/Polymorphs/CL-Polymorph-*.xml), whose
 * m_mobTemplateID is the form creature (CL-Polymorph-Mob-*: health, level,
 * school of focus, equipment with its resistances and damage), and whose
 * TransformationEffectInfo is the look the client draws. The form's cards
 * are the client deck "Decks/Polymorph Decks/Polymorph <Form>.xml".
 *
 * USAGE EXAMPLE:
 * var form = PolymorphForms.Resolve(264517); // the Gobbler
 *
 * NOTE:
 * Forms are cached per polymorph template. Tests register forms directly
 * (PolymorphForms.RegisterForTests). The client data carries numbers with no
 * dated 2010 source: they are client values (owner ruling D7). Equipment the
 * profile's Astral file excludes (the May 2011 Gobbler stun hat) is left out.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>One card of a form's deck: its spell template and whether it is the creature's own natural attack.</summary>
internal readonly record struct PolymorphCard(uint TemplateId, bool NaturalAttack);

/// <summary>CLASSIC: a polymorph form as the client's data describes it.</summary>
internal sealed record PolymorphForm(uint PolymorphTemplateId, uint MobTemplateId, string Name, MagicSchool School, int Level,
                                     int Health, ImmutableArray<WizItemTemplate> Equipment, ImmutableArray<PolymorphCard> Cards);

/// <summary>CLASSIC: the form catalog, read from the client's templates on first use.</summary>
internal static class PolymorphForms {

    private const string MobPrefix = "CL-Polymorph-Mob-";
    private const string DeckFolder = "Decks/Polymorph Decks/Polymorph ";
    private static readonly ConcurrentDictionary<uint, PolymorphForm?> s_forms = new();

    /// <summary>The form a polymorph card's kPolymorph parameter names, or null when the client data is incomplete.</summary>
    internal static PolymorphForm? Resolve(uint polymorphTemplateId)
        => s_forms.GetOrAdd(polymorphTemplateId, Build);

    /// <summary>Tests put a form in the catalog without client data.</summary>
    internal static void RegisterForTests(PolymorphForm form) => s_forms[form.PolymorphTemplateId] = form;

    /// <summary>Tests remove what they registered.</summary>
    internal static void ClearForTests() => s_forms.Clear();

    private static PolymorphForm? Build(uint polymorphTemplateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(polymorphTemplateId) is not WizPolymorphTemplate polymorph
                || CoreObjectFactory.GetCoreTemplate(polymorph.m_mobTemplateID) is not GameObjectTemplate mob
                || mob.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault() is not { } npc) {
                Logger.Warning("Polymorph template {0} has no form creature; the polymorph does nothing.", Logger.Args(polymorphTemplateId));
                return null;
            }

            var school = Enum.TryParse<MagicSchool>(npc.m_schoolOfFocus?.ToString(), out var parsed) ? parsed : MagicSchool.Balance;
            var exclusions = Imlight.CoreLib.Classic.ClassicCelestiaRules.Astral.FormItemExclusions;
            var equipment = mob.m_behaviors.OfType<EquipmentBehaviorTemplate>().FirstOrDefault()?.m_itemList ?? [];
            var items = equipment
                .Where(id => !exclusions.Contains(id))
                .Select(id => CoreObjectFactory.GetCoreTemplate(id) as WizItemTemplate)
                .Where(item => item is not null && !item.m_behaviors.OfType<DeckBehaviorTemplate>().Any())
                .Cast<WizItemTemplate>()
                .ToImmutableArray();

            var objectName = mob.m_objectName?.ToString() ?? "";
            var name = objectName.StartsWith(MobPrefix, StringComparison.Ordinal)
                ? objectName[MobPrefix.Length..].Replace('-', ' ')
                : objectName;
            var cards = DeckOf(name);
            if (cards.IsEmpty) {
                Logger.Warning("Polymorph {0} ({1}) has no client deck at {2}{1}.xml.", Logger.Args(polymorphTemplateId, name, DeckFolder));
            }

            return new PolymorphForm(polymorphTemplateId, polymorph.m_mobTemplateID, name, school, npc.m_nLevel,
                npc.m_nStartingHealth, items, cards);
        }
        catch (Exception ex) {
            Logger.Error("Polymorph template {0} could not be read ({1}); the polymorph does nothing.", Logger.Args(polymorphTemplateId, ex.Message));
            return null;
        }
    }

    private static ImmutableArray<PolymorphCard> DeckOf(string formName) {
        var deck = RootArchiveLoader.GetFile<DeckTemplate>(DeckFolder + formName + ".xml");
        if (deck?.m_spellNameList is not { Count: > 0 } names) {
            return [];
        }

        var cards = ImmutableArray.CreateBuilder<PolymorphCard>();
        foreach (var spellName in names) {
            var name = spellName?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            var templateId = SpellFactory.GetTemplateIdByHash(StringHash.Compute(name));
            if (templateId == 0) {
                Logger.Warning("Polymorph {0}: deck card {1} has no spell template; left out.", Logger.Args(formName, name));
                continue;
            }
            cards.Add(new PolymorphCard(templateId, name.StartsWith("NA ", StringComparison.Ordinal)));
        }

        return cards.ToImmutable();
    }
}
