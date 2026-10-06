// CLASSIC: ambient arena wizards wear the researched starter kit and obey its real client deck restrictions.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Pvp;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Pvp;

internal static class ArenaPvpLoadout {
    internal static bool Prepare(Wizard wizard) {
        if (wizard is null || !ClassicRuntime.IsInitialized || !ClassicSpellTemplates.RestrictsTraining) return false;
        var level = wizard.MagicSchoolBehavior.Level;
        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        var profile = ClassicRuntime.Rules.Profile;
        if (level is < 1 or > 50 || level > (profile.LevelCap ?? 50)) return false;
        // No unresearched vendor gear: the already-approved starter deck supports all ranks, 14 cards/3 copies.
        if (CoreObjectFactory.GetCoreTemplate(ClassicStart.StarterDeckTemplateId) is not WizItemTemplate item
            || EquipRules.Check(wizard, item) != EquipRefusal.None
            || item.m_behaviors?.OfType<DeckBehaviorTemplate>().FirstOrDefault() is not { } deckTemplate) return false;
        if (wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Deck)?.m_templateID != ClassicStart.StarterDeckTemplateId
            && !AmbientWizards.EquipInMemory(wizard, ClassicStart.StarterDeckTemplateId)) return false;
        wizard.SpellbookBehavior.InitializeProperties(deckTemplate);
        var available = new List<ArenaDeckSpell>();
        foreach (var record in ClassicSpellTemplates.Records.Where(r => r.ClientTemplate is not null && r.IsInProfile(profile.Id)
                     && (r.Kind == "trained" && string.Equals(r.School, school, StringComparison.OrdinalIgnoreCase)
                         || r.Kind == "crossover" && r.Id == "spell.balance.reshuffle"))) {
            if (!ClassicSpellTemplates.IsTrainable(record.ClientTemplate)
                || CoreObjectFactory.TryGetTemplateIdByPath(record.ClientTemplate) is not { } id
                || id > uint.MaxValue || CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate spell) continue;
            available.Add(new ArenaDeckSpell(record, (uint) id, spell.m_spellRank?.m_spellRank ?? 0,
                (int) spell.m_maxCopies, !spell.m_Treasure && spell.m_spellFusion == 0));
        }
        var limits = new ArenaDeckLimits(deckTemplate.m_primarySchoolName ?? "", deckTemplate.m_maxSpells,
            deckTemplate.m_schoolMaxInstances, deckTemplate.m_genericMaxInstances,
            deckTemplate.m_schoolMaxRank, deckTemplate.m_genericMaxRank);
        var planned = ArenaPvpDeckPlanner.Plan(available, school, level, profile.Id, profile.Lineage, limits);
        var cards = new List<SpellData>();
        foreach (var entry in planned) {
            for (var copy = 0; copy < entry.Copies; copy++) {
                if (ClassicDeckRules.CanAdd(deckTemplate, cards, entry.Spell.TemplateId,
                        id => CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate) != DeckAddRefusal.None) break;
                var existing = cards.FirstOrDefault(c => c.m_templateID == entry.Spell.TemplateId);
                if (existing is null) cards.Add(new SpellData { m_templateID = entry.Spell.TemplateId, m_quantity = 1 });
                else existing.m_quantity++;
            }
        }
        var hasAttack = planned.Any(e => cards.Any(c => c.m_templateID == e.Spell.TemplateId)
            && e.Spell.Record.ValuesFor(profile.Lineage).Effects.Any(f => f.HasAmount
                && f.Kind is SpellEffectKind.Damage or SpellEffectKind.Dot or SpellEffectKind.Steal));
        if (!hasAttack) return false;
        wizard.SpellbookBehavior.SpellList = cards;
        wizard.SpellbookBehavior.LearnedSpellTemplateIds = [.. cards.Select(c => c.m_templateID)];
        return true;
    }
}
