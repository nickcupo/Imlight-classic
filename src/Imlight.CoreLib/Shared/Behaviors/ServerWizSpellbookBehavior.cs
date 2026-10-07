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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Imlight.CoreLib.Game.Spells;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizSpellbookBehavior : ServerSpellbookBehavior {

    [JsonIgnore] public new bool NoTransfer { get; set; } = false;

    [JsonIgnore] public MagicSchool PrimarySchool { get; set; }
    [JsonIgnore] public int GenericMaxRank { get; set; }
    [JsonIgnore] public int SchoolMaxRank { get; set; }
    [JsonIgnore] public int GenericMaxInstances { get; set; }
    [JsonIgnore] public int SchoolMaxInstances { get; set; }
    [JsonIgnore] public int MaxSpells { get; set; }
    [JsonIgnore] public int MaxTreasureCards { get; set; }
    // CLASSIC: the equipped deck's own rules, for the stock client's add check (Classic/ClassicDeckRules.cs).
    [JsonIgnore] public DeckBehaviorTemplate DeckTemplate { get; set; }

    public Dictionary<ulong, HashSet<uint>> ExcludedItemSpellIds { get; set; } = [];

    /// <summary>
    /// CLASSIC: the Treasure Cards this wizard put in each deck: deck item id, then spell template id, then copies.
    /// They are kept apart from the deck's own card list (DeckBehavior.m_spellList, regular cards only), so a Treasure
    /// Card of a spell the wizard knows stays a Treasure Card (spent when cast, in a Treasure Card place) and only the
    /// cards recorded here go back to the book. The ledger is the wizard's, not the deck's: a deck handed to another
    /// wizard through the shared bank carries no Treasure Cards, and they are there again when it comes back.
    /// Changed only on the saved copy (WizardCollection.MoveTreasureCardToDeck and FromDeck) and then published here
    /// as a new dictionary, so a reader never sees one half-changed.
    /// </summary>
    public Dictionary<ulong, Dictionary<uint, int>> DeckTreasureCards { get; set; } = [];

    /// <summary>CLASSIC: 1 once the deck Treasure Cards of older saves were moved into <see cref="DeckTreasureCards"/>.</summary>
    public int DeckTreasureLedgerVersion { get; set; }

    /// <summary>CLASSIC: copies of <paramref name="templateId"/> this wizard has as Treasure Cards in the deck.</summary>
    public int DeckTreasureCount(ulong deckId, uint templateId)
        => DeckTreasureCards is { } ledger && ledger.TryGetValue(deckId, out var cards) && cards.TryGetValue(templateId, out var n) ? n : 0;

    /// <summary>CLASSIC: all of this wizard's Treasure Cards in the deck.</summary>
    public int DeckTreasureTotal(ulong deckId)
        => DeckTreasureCards is { } ledger && ledger.TryGetValue(deckId, out var cards) ? cards.Values.Sum() : 0;

    /// <summary>CLASSIC: the Treasure Cards in the deck (template id, copies); empty when none.</summary>
    public IReadOnlyDictionary<uint, int> DeckTreasureCardsOf(ulong deckId)
        => DeckTreasureCards is { } ledger && ledger.TryGetValue(deckId, out var cards) ? cards : new Dictionary<uint, int>();

    /// <summary>CLASSIC: adds (or, negative, removes) Treasure Cards of a deck in the ledger. For the saved copy.</summary>
    internal bool ChangeDeckTreasure(ulong deckId, uint templateId, int delta) {
        DeckTreasureCards ??= [];
        if (!DeckTreasureCards.TryGetValue(deckId, out var cards)) {
            cards = [];
            DeckTreasureCards[deckId] = cards;
        }

        cards.TryGetValue(templateId, out var have);
        var now = have + delta;
        if (now < 0) {
            return false;
        }

        if (now == 0) {
            cards.Remove(templateId);
            if (cards.Count == 0) {
                DeckTreasureCards.Remove(deckId);
            }
        }
        else {
            cards[templateId] = now;
        }

        return true;
    }

    /// <summary>CLASSIC: a deep copy of a ledger (publishing the saved one to the live wizard).</summary>
    internal static Dictionary<ulong, Dictionary<uint, int>> CopyLedger(Dictionary<ulong, Dictionary<uint, int>> ledger)
        => ledger?.ToDictionary(pair => pair.Key, pair => new Dictionary<uint, int>(pair.Value)) ?? [];

    public void InitializeProperties(DeckBehaviorTemplate deckTemplate) {
        SetPropertiesFromDeckTemplate(deckTemplate);
    }

    public void InitializeSpells(DeckBehavior deckBehavior) {
        if (deckBehavior is null) {
            return;
        }

        base.SpellList = deckBehavior.m_spellList;
    }

    public bool EquipDeck(WizItemTemplate template, DeckBehavior deckBehavior) {
        if (template is null) {
            return false;
        }

        // Search for a deck behavior template within the item template.
        foreach (var behaviorTemplate in template.m_behaviors) {
            if (behaviorTemplate is not DeckBehaviorTemplate deckBehaviorTemplate) {
                continue;
            }

            // We've found what we're looking for. Set the deck behavior properties.
            SetPropertiesFromDeckTemplate(deckBehaviorTemplate);
            base.SpellList = deckBehavior.m_spellList;

            return true;
        }

        return false;
    }

    public bool AddSpellToDeck(uint spellTemplateId) {
        base.SpellList ??= new List<SpellData>();

        // CLASSIC: exactly the stock client's check, so a card the deck screen shows is never refused afterwards
        // (Treasure Cards count against m_maxTreasureCards only, with no per-card limit).
        if (DeckTemplate is not null) {
            var refusal = Classic.ClassicDeckRules.CanAdd(DeckTemplate, SpellList, spellTemplateId,
                id => CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate);
            if (refusal != Classic.DeckAddRefusal.None) {
                Logger.Debug("Deck add of spell {0} refused: {1}.", Logger.Args(spellTemplateId, refusal.ToString()));
                return false;
            }

            var known = SpellList.Find(x => x.m_templateID == spellTemplateId);
            if (known is null) SpellList.Add(new SpellData { m_templateID = spellTemplateId, m_quantity = 1 });
            else known.m_quantity++;
            return true;
        }

        if (TotalSpellCount() >= MaxSpells) {
            Logger.Debug("The deck already has the maximum amount of allowed spells.");
            
            return false;
        }

        // Get the spells template; we'll need it for the magic school ID.
        var spellTemplate = SpellFactory.GetSpell(spellTemplateId);
        if (spellTemplate is null) {
            Logger.Debug("Failed to create spell from template {0}.", Logger.Args(spellTemplateId));
            
            return false;
        }

        // Create a new SpellData for this spell, if one doesn't already exist.
        var spellData = SpellList.Find(x => x.m_templateID == spellTemplateId);
        if (spellData is null) {
            // If the spell doesn't exist in the deck, we'll want to add it.
            spellData = new SpellData {
                m_templateID = spellTemplateId,
                m_quantity = 1
            };
            SpellList.Add(spellData);
        }
        else {
            // Otherwise, we'll want to increase the quantity so long as the number of max instances hasn't been reached.
            var spellSchool = (MagicSchool) spellTemplate.m_magicSchoolID;
            var maxInstances = spellSchool == PrimarySchool ? SchoolMaxInstances : GenericMaxInstances;
            if (spellData.m_quantity >= maxInstances) {
                Logger.Debug("The deck already has the maximum amount of allowed instances of spell {0}.", Logger.Args(spellTemplateId));
                
                return false;
            }

            spellData.m_quantity++;
        }

        return true;
    }

    public bool RemoveSpellFromDeck(uint spellTemplateId) {
        if (SpellList is null) {
            return false;
        }

        var spellData = SpellList.Find(x => x.m_templateID == spellTemplateId);
        if (spellData is null) {
            Logger.Debug("The deck does not contain spell {0}.", Logger.Args(spellTemplateId));
            return false;
        }

        // Decrease the quantity, if we can. Otherwise, remove the spell data.
        if (spellData.m_quantity - 1 <= 0) {
            SpellList.Remove(spellData);
        }
        else {
            spellData.m_quantity--;
        }

        return true;
    }

    /// <summary>
    /// Adds or removes a spell template ID from a deck item's exclusion list.
    /// </summary>
    /// <param name="deckId">The global ID of the deck item.</param>
    /// <param name="spellTemplateId">The spell template ID to exclude/include.</param>
    /// <param name="exclude">True to exclude, false to include (un-exclude).</param>
    public void SetItemSpellExclusion(ulong deckId, uint spellTemplateId, bool exclude) {
        if (exclude) {
            if (!ExcludedItemSpellIds.TryGetValue(deckId, out var set)) {
                set = [];
                ExcludedItemSpellIds[deckId] = set;
            }
            set.Add(spellTemplateId);
        }
        else {
            if (ExcludedItemSpellIds.TryGetValue(deckId, out var set)) {
                set.Remove(spellTemplateId);
                if (set.Count == 0) {
                    ExcludedItemSpellIds.Remove(deckId);
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the given spell template ID is excluded for the given deck item.
    /// </summary>
    public bool IsItemSpellExcluded(ulong deckId, uint spellTemplateId) 
        => ExcludedItemSpellIds.TryGetValue(deckId, out var set)
            && set.Contains(spellTemplateId);

    public new ClientSpellbookBehavior GetClientBehaviorInstance() {
        var spellIdList = new List<SpellIDTracker>();
        foreach (var templateId in LearnedSpellTemplateIds) {
            spellIdList.Add(new SpellIDTracker {
                m_isRetired = false,
                m_spellID = templateId,
                m_tieredSpellGroupIndex = -1, // CLASSIC: native AddSpell's ordinary-spell sentinel, also on a fresh attach.
            });
        }

        return new ClientSpellbookBehavior {
            m_spellIDList = spellIdList
        };
    }

    private void SetPropertiesFromDeckTemplate(DeckBehaviorTemplate template) {
        // Set the deck behavior properties.
        // Try to parse the string school as a MagicSchool enum.
        MagicSchool school;
        if (string.IsNullOrEmpty(template.m_primarySchoolName)
            || !Enum.TryParse(template.m_primarySchoolName, true, out school)) {
            school = MagicSchool.None;
        }
        this.PrimarySchool = school;
        this.GenericMaxRank = template.m_genericMaxRank;
        this.SchoolMaxRank = template.m_schoolMaxRank;
        this.GenericMaxInstances = template.m_genericMaxInstances;
        this.SchoolMaxInstances = template.m_schoolMaxInstances;
        this.MaxSpells = template.m_maxSpells;
        this.MaxTreasureCards = template.m_maxTreasureCards;
        this.DeckTemplate = template;
    }
    
}
