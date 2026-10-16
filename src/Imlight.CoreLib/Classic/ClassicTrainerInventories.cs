// CLASSIC: dated trainer corrections applied after every SpiralDB resource load.
// Stun Block was taught by Sabrina Greenstar in July 2009 and moved to Diego in May 2011.
// Evidence and profile availability: classic-data/spells/ice/stun-block.yaml.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic;

internal static class ClassicTrainerInventories {

    private const ulong Sabrina = 38210, Diego = 38226;

    internal static void Refresh() {
        Refresh(ClassicSpellTemplates.Records.SingleOrDefault(spell => spell.Id == "spell.ice.stun_block"),
            CoreObjectFactory.TryGetTemplateIdByPath);
        RefreshOctoberTraining(ClassicSpellTemplates.Records, CoreObjectFactory.TryGetTemplateIdByPath);
    }

    // CLASSIC: publish only active researched October additions after every reload. A deferred
    // record must also remove the native modern offer; retaining implementation does not enable stock.
    internal static void RefreshOctoberTraining(IEnumerable<ClassicSpellRecord> records, Func<string, ulong?> resolveTemplate) {
        if (!ClassicOctoberRules.Active) return;
        SpiralDB.TryGetNpcSpellInventory(Diego, out var inventory);
        var stock = inventory?.Spells.ToList() ?? [];
        stock.RemoveAll(spell => spell.TemplateID == ClassicOctoberTraining.Cloak);
        var restored = false;
        foreach (var record in records.Where(spell => spell.Id == "spell.sun.cloak" && !ClassicProfiles.IsCelestiaOrLater(ClassicRuntime.Rules.Profile))) { // CLASSIC: no Diego stand-in from celestia-2010 on
            if (!record.IsInProfile(ClassicRuntime.Rules.Profile.Id) || record.ClientTemplate is not { } path
                || resolveTemplate(path) is not { } templateId || templateId != ClassicOctoberTraining.Cloak) continue;
            var values = record.ValuesFor(ClassicRuntime.Rules.Profile.Lineage);
            if (values.Trainer != "Diego the Duelmaster" || (values.LevelLearned ?? 0) != ClassicOctoberTraining.RequiredLevel(templateId)) {
                Logger.Warning("Classic October trainer: invalid trainer or approved temporary level for {0}; entry omitted.", Logger.Args(record.Name));
                continue;
            }
            stock.RemoveAll(spell => spell.TemplateID == templateId);
            stock.Add(new NPCSpellEntry { TemplateID = templateId, RequiredSpellID = 0, Level = values.LevelLearned ?? 0 });
            restored = true;
        }
        SpiralDB.RegisterNpcSpellInventory(new NPCSpellInventory { TemplateID = Diego, Spells = stock });
        Logger.Information(restored
            ? "Classic October trainer: restored Cloak stock; temporary Private requirement, exact October eligibility unverified."
            : "Classic October trainer: Cloak stock deferred with Celestia.");
    }

    internal static void Refresh(ClassicSpellRecord? record, Func<string, ulong?> resolveTemplate) {
        if (!ClassicRuntime.IsActive || record?.ClientTemplate is not { } path
            || !record.IsInProfile(ClassicRuntime.Rules.Profile.Id) || resolveTemplate(path) is not { } templateId) {
            return;
        }

        var values = record.ValuesFor(ClassicRuntime.Rules.Profile.Lineage);
        if (values.Trainer == "Diego the Duelmaster") { // CLASSIC: wintertusk-2011 (May 2011): only Diego, rank Private.
            MoveStunBlockToDiego(templateId, values);

            return;
        }

        if (values.Trainer != "Sabrina Greenstar"
            || !SpiralDB.TryGetNpcSpellInventory(Sabrina, out var sabrina)) {
            Logger.Warning("Classic trainer: cannot place {0} with its dated trainer; Sabrina's inventory is unavailable.",
                Logger.Args(record.Name));
            return;
        }

        SpiralDB.TryGetNpcSpellInventory(Diego, out var diego);
        var (correctSabrina, correctDiego) = MoveStunBlock(sabrina, diego, templateId, values);
        SpiralDB.RegisterNpcSpellInventory(correctSabrina);
        if (correctDiego is not null) {
            SpiralDB.RegisterNpcSpellInventory(correctDiego);
        }

        Logger.Information("Classic trainer: {0} taught by Sabrina Greenstar, not Diego ({1}).",
            Logger.Args(record.Name, record.SourceFile));
    }

    private static void MoveStunBlockToDiego(ulong templateId, SpellValues values) {
        SpiralDB.TryGetNpcSpellInventory(Sabrina, out var sabrina);
        SpiralDB.TryGetNpcSpellInventory(Diego, out var diego);
        var (newSabrina, newDiego) = StunBlockStock(sabrina, diego, templateId, values);
        if (newSabrina is not null) {
            SpiralDB.RegisterNpcSpellInventory(newSabrina);
        }

        SpiralDB.RegisterNpcSpellInventory(newDiego);
        ClassicOctoberTraining.StunBlock = templateId;
        Logger.Information("Classic trainer: Stun Block taught only by Diego the Duelmaster at PvP rank Private (May 2011).");
    }

    // CLASSIC: Sabrina loses the card, Diego gains it; copies, idempotent.
    internal static (NPCSpellInventory? Sabrina, NPCSpellInventory Diego) StunBlockStock(
        NPCSpellInventory? sabrina, NPCSpellInventory? diego, ulong templateId, SpellValues values) {
        var newSabrina = sabrina is null ? null : new NPCSpellInventory {
            TemplateID = sabrina.TemplateID,
            Spells = [.. sabrina.Spells.Where(spell => spell.TemplateID != templateId)],
        };
        var newDiego = new NPCSpellInventory {
            TemplateID = Diego,
            Spells = [.. (diego?.Spells ?? []).Where(spell => spell.TemplateID != templateId), new NPCSpellEntry {
                TemplateID = templateId, RequiredSpellID = 0, Level = values.LevelLearned ?? 0,
            }],
        };
        return (newSabrina, newDiego);
    }

    // CLASSIC: copy lists so existing readers retain their old inventory; replace this one entry idempotently.
    internal static (NPCSpellInventory Sabrina, NPCSpellInventory? Diego) MoveStunBlock(
        NPCSpellInventory sabrina, NPCSpellInventory? diego, ulong templateId, SpellValues values) {
        var correctSabrina = new NPCSpellInventory {
            TemplateID = sabrina.TemplateID,
            Spells = [.. sabrina.Spells.Where(spell => spell.TemplateID != templateId), new NPCSpellEntry {
                TemplateID = templateId, RequiredSpellID = 0, Level = values.LevelLearned ?? 0,
            }],
        };
        var correctDiego = diego is null ? null : new NPCSpellInventory {
            TemplateID = diego.TemplateID,
            Spells = [.. diego.Spells.Where(spell => spell.TemplateID != templateId)],
        };
        return (correctSabrina, correctDiego);
    }
}
