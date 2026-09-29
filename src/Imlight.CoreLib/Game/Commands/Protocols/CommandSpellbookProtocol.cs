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

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandSpellbookProtocol : CommandProtocol {

    internal override string Group { get; set; } = "sb";

    [Command("learn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LearnSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(spellTemplateIdUint);
        if (template == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        if (template is not SpellTemplate spellTemplate) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        var spellName = spellTemplate.m_name;

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        if (!Context.Character.LearnSpell(spell)) {
            InformSenderClient("Failed to learn spell. You may already know this spell.");
        }
        else {
            InformSenderClient($"You have learned the spell {spellName}.");
        }

        var clientMsg = new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK {
            SpellID = (int) spellTemplateIdUint
        };
        Context.SessionActor.Tell(clientMsg);
    }

    // CLASSIC: QA setup for playthrough tests: learn spells by template name (';' between names), e.g.
    // ".sb learnname Sunbird;Heck Hound", so a test wizard set to a level can carry the spells a player of that level
    // trained (the spell templates have no TemplateManifest id to pass to "learn").
    [Command("learnname")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LearnSpellByNameCommand([Remainder] string spellNames) {
        foreach (var entry in spellNames.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)) {
            // "Name*N": N copies in the deck (default 4).
            var star = entry.LastIndexOf('*');
            var name = star > 0 ? entry[..star].Trim() : entry;
            var wanted = star > 0 && int.TryParse(entry[(star + 1)..], out var n) ? n : 4;
            var template = SpellFactory.GetTemplate(name);
            var hash = Imcodec.Cryptography.StringHash.Compute(name);
            var templateId = template is null ? 0 : SpellFactory.GetTemplateIdByHash(hash);
            var spell = template is null ? null : SpellFactory.GetSpell(template, templateId);
            if (spell is null) {
                InformSenderClient($"No spell template named '{name}'.");

                continue;
            }

            if (Context.Character.LearnSpell(spell)) {
                Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) templateId });
            }

            // Put copies in the equipped deck too (up to 4, or what the deck's limits allow), as a player would.
            var deckId = Context.Character.EquipmentBehavior.SlotList
                .FirstOrDefault(slot => slot.SlotType == EquipmentSlotType.Deck)?.ItemId;
            var copies = 0;
            while (deckId is not null && copies < wanted && Context.Character.AddSpellToDeck(templateId, deckId.Value)) {
                copies++;
            }

            InformSenderClient($"You have learned the spell {name} ({copies} in the deck).");
        }
    }

    // CLASSIC: QA setup for playthrough tests: empty the equipped deck (not the treasure cards) so ".sb learnname" can
    // fill it with the spells a wizard of the test level would carry.
    [Command("deckclear")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DeckClearCommand() {
        var deckId = Context.Character.EquipmentBehavior.SlotList
            .FirstOrDefault(slot => slot.SlotType == EquipmentSlotType.Deck)?.ItemId;
        if (deckId is null) {
            InformSenderClient("No deck equipped.");

            return;
        }

        var removed = 0;
        foreach (var spell in (Context.Character.SpellbookBehavior.SpellList ?? []).ToList()) {
            if (!Context.Character.SpellbookBehavior.LearnedSpellTemplateIds.Contains(spell.m_templateID)) {
                continue;
            }

            while (Context.Character.RemoveSpellFromDeck(spell.m_templateID, deckId.Value)) {
                removed++;
            }
        }

        InformSenderClient($"Removed {removed} card(s) from the deck.");
    }

    [Command("unlearn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UnlearnSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(spellTemplateIdUint);
        if (template == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        if (template is not SpellTemplate spellTemplate) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        var spellName = spellTemplate.m_name;

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        if (!Context.Character.UnlearnSpell(spellTemplateIdUint)) {
            InformSenderClient("Failed to unlearn spell. You may not know this spell.");
        }
        else {
            InformSenderClient($"You have unlearned the spell {spellName}.");
        }

        var clientMsg = new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK {
            SpellID = (int) spellTemplateIdUint
        };
        Context.SessionActor.Tell(clientMsg);
    }
    [Command("unlearnall")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UnlearnallSpellCommand() {

        if (Context.Character.SpellbookBehavior.LearnedSpellTemplateIds != null) {
            List<uint> spellList = [.. Context.Character.SpellbookBehavior.LearnedSpellTemplateIds];

            foreach (var spell in spellList) {
                var clientMsg = new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK {
                    SpellID = (int) spell
                };
                Context.SessionActor.Tell(clientMsg);

                Context.Character.SpellbookBehavior.RemoveSpellFromBook(spell);

                // Persistent save.
                WizardCollection.UnlearnSpell(Context.Character, spell);
                InformSenderClient($"You have unlearned the spell {spell}.");
            }
        }
        else {
            InformSenderClient($"No Spells To Unlearn");
        }
    }

    [Command("add")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        Context.Character.AddTemporarySpell(spell);
        InformSenderClient("You have added the spell.");
    }

    [Command("remove")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void RemoveSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        Context.Character.RemoveTemporarySpell(spellTemplateIdUint);
        InformSenderClient("You have removed the spell.");
    }

    [Command("allcantrips")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LearnallCantripsCommand() {
        List<uint> spellList = [ 1964703872, 1462882588, 290925625, 1521398842, 189772023, 1767938148, 536458141,
            1189939434, 717871356, 999765474, 1494670771, 577368445, 1672654002, 1799150966, 1282482033, 1884951148, 467451672,
            1419042141, 220931873, 466468632, 2083836762, 1992492243 ];

        foreach (var spell in spellList) {
            var learn_spell = SpellFactory.GetSpell(spell);
            if (!Context.Character.LearnSpell(learn_spell)) {
                continue;
            }
            var clientMsg = new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { 
                SpellID = (int) spell
            };
            Context.SessionActor.Tell(clientMsg);
        }

        InformSenderClient($"You have learned all cantrips.");
    }

}
