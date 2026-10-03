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
 * COMBAT CREATURE DECK
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spelldecks for combat-enabled creature entities, initializing 
 * their spellbooks from both client spell names and SpiralDB deck data.
 * 
 * USAGE EXAMPLE:
 * var deckComponent = new CombatCreatureDeckComponent(zoneEntity);
 * var availableSpells = deckComponent.Spells;
 * 
 * NOTE:
 * Requires prior initialization of CoreObjectFactory and SpellFactory.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/15/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class CombatCreatureDeckComponent : ZoneEntityComponent, IComponentFactory {

    public List<SpellData> Spells { get; } = [];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && gameObjectTemplate.m_behaviors.Any(x => x is DuelistBehaviorTemplate);

    // ctor
    public CombatCreatureDeckComponent(ZoneEntity entity) : base(entity) { 
        // Collect every behavior from the creature's equipment items.
        var equipmentItemBehaviors = entity.Template.m_behaviors
            .OfType<EquipmentBehaviorTemplate>()
            .SelectMany(x => x.m_itemList)
            .Select(x => CoreObjectFactory.GetCoreTemplate(x))
            .Where(x => x != null)
            .SelectMany(x => x.m_behaviors ?? []);

        // Combine equipment item behaviors with the entity's own behaviors.
        // MobDeckBehaviorTemplate can appear in either location.
        var allBehaviors = entity.Template.m_behaviors
            .Concat(equipmentItemBehaviors);

        // CLASSIC: a creature the profile's creature-deck file lists (from its 2009 wiki page) casts that deck and nothing
        // else, so each minion, Monstrology creature and mob has its own spells rather than its school's.
        var templateId = (uint) entity.ActiveGameObject.m_templateID;
        var classicSpells = ClassicDeckSpellIds(ClassicProgression.CreatureDecks, templateId,
            name => SpellFactory.GetSpell(name)?.m_templateID);
        var classicCounts = ClassicDeckSpellCounts(ClassicProgression.CreatureDecks, templateId,
            name => SpellFactory.GetSpell(name)?.m_templateID);
        foreach (var spellId in classicSpells) {
            // CLASSIC: the wiki's count weighs the draw (owner ruling 2026-10-02); creatures never run out, so each copy
            // stands for CREATURE_COPIES_PER_COUNT.
            AddSpell(spellId, (uint) (classicCounts.GetValueOrDefault(spellId, 1) * CREATURE_COPIES_PER_COUNT));
        }

        if (classicSpells.Count > 0) {
            Logger.Debug("{0} ({1}) casts its own creature-deck: {2}.",
                Logger.Args((entity.Template as GameObjectTemplate)?.m_objectName.ToString(), templateId,
                    string.Join(", ", Spells.Select(x => (CoreObjectFactory.GetCoreTemplate(x.m_templateID) as SpellTemplate)?.m_name?.ToString()))));
        }

        // MobDeckBehaviorTemplate stores spell names directly, if it exists.
        var mobDeck = classicSpells.Count == 0 ? allBehaviors.OfType<MobDeckBehaviorTemplate>().FirstOrDefault() : null;
        if (mobDeck != null) {
            AddSpellsFromNames(mobDeck.m_spellList);
        }

        // DeckBehaviorTemplate stores a deck name that maps to a SpiralDB spellbook.
        // The deck is the union of both sources: the client's own spell names and the
        // SpiralDB spellbook named by the deck behavior. Either source may be missing.
        var deckBehavior = classicSpells.Count == 0 ? allBehaviors.OfType<DeckBehaviorTemplate>().FirstOrDefault() : null;
        if (deckBehavior is not null && !string.IsNullOrEmpty(deckBehavior.m_defaultDeck)) {
            var spellbook = CreatureSpellbookCollection.GetCreatureSpellbook(deckBehavior.m_defaultDeck);
            if (spellbook is not null) {
                AddSpellbookSpells(spellbook);
            }
        }

        // A creature with an empty deck (no usable client names and no SpiralDB deck) falls
        // back to the default spellbook so it can cast instead of passing every round.
        // CLASSIC: before that, the client's generic deck for the creature's school and rank (Mdeck-<school>-R<n>), so
        // summoned minions and Monstrology creatures cast their own school's spells instead of the same starter set.
        if (Spells.Count == 0) {
            var npc = allBehaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault();
            var school = (entity.Template as WizGameObjectTemplate)?.m_primarySchoolName.ToString();
            if (string.IsNullOrEmpty(school)) {
                school = npc?.m_schoolOfFocus.ToString();
            }
            var generic = CreatureSpellbookCollection.GetGenericCreatureSpellbook(school, npc?.m_nLevel ?? 1);
            if (generic is not null) {
                Logger.Debug("{0} has no deck of its own; using {1} ({2}, rank {3}).",
                    Logger.Args((entity.Template as GameObjectTemplate)?.m_objectName.ToString(), generic.DeckName, school, npc?.m_nLevel ?? 1));
                AddSpellbookSpells(generic);
            }
        }

        if (Spells.Count == 0) {
            Logger.Warning(
                "{0} {1} has no usable spells from its client deck or SpiralDB deck, falling back to the default spellbook.",
                Logger.Args(nameof(ZoneEntity), entity.ActiveGameObject.m_debugName)
            );
            AddSpellbookSpells(CreatureSpellbookCollection.GetDefaultCreatureSpellbook());
        }
    }

    /// <summary>
    /// CLASSIC: the spell template ids of the creature's own 2009 deck, in file order; empty when the file lists no deck
    /// for <paramref name="template"/> or none of its spell names resolve.
    /// </summary>
    /// <param name="decks">The profile's creature decks.</param>
    /// <param name="template">The creature's template id.</param>
    /// <param name="resolve">Maps a spell template name to its template id, or null when the client has no such spell.</param>
    internal static List<uint> ClassicDeckSpellIds(CreatureDecks decks, uint template, System.Func<string, uint?> resolve) {
        var ids = new List<uint>();
        if (!decks.TryGet(template, out var deck)) {
            return ids;
        }

        foreach (var spell in deck.Spells) {
            if (resolve(spell.Spell) is { } id && !ids.Contains(id)) {
                ids.Add(id);
            }
        }

        return ids;
    }

    private const int CREATURE_COPIES_PER_COUNT = 1000;

    /// <summary>
    /// CLASSIC: how many copies of each resolved spell the creature's deck file lists (repeated names add up).
    /// </summary>
    internal static Dictionary<uint, int> ClassicDeckSpellCounts(CreatureDecks decks, uint template, System.Func<string, uint?> resolve) {
        var counts = new Dictionary<uint, int>();
        if (!decks.TryGet(template, out var deck)) {
            return counts;
        }

        foreach (var spell in deck.Spells) {
            if (resolve(spell.Spell) is { } id) {
                counts[id] = counts.GetValueOrDefault(id) + spell.Count;
            }
        }

        return counts;
    }

    private void AddSpellbookSpells(CreatureSpellbook spellbook) {
        foreach (var spellId in spellbook.SpellTemplateIds) {
            AddSpell(spellId);
        }
    }

    private void AddSpellsFromNames(List<ByteString> spellNames) {
        foreach (var spellName in spellNames) {
            if (spellName.ToString().Length == 0) {
                continue;
            }

            // Unknown names are skipped; SpellFactory logs the miss.
            var spell = SpellFactory.GetSpell(spellName.ToString());
            if (spell is not null) {
                AddSpell(spell.m_templateID);
            }
        }
    }

    private void AddSpell(uint spellId, uint quantity = 9999) {
        // Both sources may list the same spell; creatures carry one entry per spell.
        if (Spells.Any(x => x.m_templateID == spellId)) {
            return;
        }

        // Unknown spell ids are skipped; SpellFactory logs the miss.
        if (SpellFactory.GetSpell(spellId) is null) {
            return;
        }

        // Creatures have infinite spells in their spellbook.
        Spells.Add(new SpellData {
            m_templateID = spellId,
            m_quantity = quantity,
        });
    }

}