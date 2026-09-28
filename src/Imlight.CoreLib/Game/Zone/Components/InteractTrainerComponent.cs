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
 * INTERACT TRAINER
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spell training interactions for NPCs, dynamically generating 
 * spell training options based on player characteristics and spell requirements.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Dynamically resolves spell training options from NPC spell inventory.
 * Considers player's magic school, level, and existing spellbook.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractTrainerComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizTrainingService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => "GUI_NPCInteractText";
    public WizBangs WizBang       => WizBangs.Training;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_ShopOptionEquipment";

    public List<NPCSpellEntry> SpellInventory { get; private set; } = [];
    private List<ServiceOptionBase> _serviceOptionBases = [];

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        // Attach if the template is an NPC and has a spell inventory in Dragon database.
        => template is GameObjectTemplate goTemplate 
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate) 
        && NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _);

    public override void OnStart() {
        // Search Dragon database for our spell inventory.
        if (    Entity.Template is not GameObjectTemplate goTemplate
            || !NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out var spellInventory)) {
            Logger.Error("{0} on entity {1} has no spell inventory.", 
                Logger.Args(ServiceName, Entity.ActiveGameObject.m_debugName));

            return;
        }

        SpellInventory = OfferedSpells(spellInventory.Spells); // CLASSIC: was every spell in the inventory.
        InitializeServiceBases();
    }

    // CLASSIC: the spells this trainer teaches in the active profile, in the inventory's order. A spell whose
    // template (or required spell's template) does not load is left out here rather than skipped when the options
    // are built, so an option's training index and the inventory stay aligned for MSG_TRAIN.
    private List<NPCSpellEntry> OfferedSpells(List<NPCSpellEntry> inventory) {
        var names = new Dictionary<ulong, string>();
        bool Loads(ulong templateId) {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is not SpellTemplate template) {
                Logger.Error("Trainer {0} has an invalid spell template with ID {1}",
                    Logger.Args(Entity.ActiveGameObject.m_debugName, templateId));

                return false;
            }

            names[templateId] = template.m_name;

            return true;
        }

        var offered = OfferedSpells(inventory, Loads,
            templateId => ClassicSpellTemplates.IsTrainable(CoreObjectFactory.GetTemplatePath(templateId)));
        if (offered.Count < inventory.Count) {
            var kept = offered.Select(spell => spell.TemplateID).ToHashSet();
            Logger.Information("Classic trainer {0} teaches {1} of its {2} spells ({3}); not in the profile: {4}.",
                Logger.Args(Entity.ActiveGameObject.m_debugName, offered.Count, inventory.Count,
                    string.Join(", ", offered.Select(spell => names.GetValueOrDefault(spell.TemplateID, spell.TemplateID.ToString()))),
                    string.Join(", ", inventory.Where(spell => !kept.Contains(spell.TemplateID))
                        .Select(spell => names.GetValueOrDefault(spell.TemplateID, spell.TemplateID.ToString())))));
        }

        return offered;
    }

    /// <summary>
    /// The entries of a trainer's inventory it may teach: those whose templates load and that the profile can train,
    /// in order. An entry whose required spell cannot be trained requires that spell's own requirement instead
    /// (Power Play follows Gearhead Destroyer in the r806919 Balance tree, and Sandstorm in 2009), or nothing when
    /// the chain leaves the inventory. Such entries are copies; the inventory is not changed.
    /// </summary>
    /// <param name="inventory">The trainer's SpiralDB inventory.</param>
    /// <param name="loads">True when a spell template loads.</param>
    /// <param name="trainable">True when the profile can train a spell.</param>
    internal static List<NPCSpellEntry> OfferedSpells(IReadOnlyList<NPCSpellEntry> inventory, Func<ulong, bool> loads,
                                                      Func<ulong, bool> trainable) {
        var byTemplate = new Dictionary<ulong, NPCSpellEntry>();
        foreach (var spell in inventory) {
            byTemplate.TryAdd(spell.TemplateID, spell);
        }

        var offered = new List<NPCSpellEntry>(inventory.Count);
        foreach (var spell in inventory) {
            if (!loads(spell.TemplateID) || !trainable(spell.TemplateID)) {
                continue;
            }

            var required = spell.RequiredSpellID;
            var seen = new HashSet<ulong>();
            while (required != 0 && !trainable(required) && seen.Add(required)) {
                required = byTemplate.TryGetValue(required, out var link) ? link.RequiredSpellID : 0;
            }

            if (required != 0 && (seen.Contains(required) || !loads(required))) {
                required = 0;
            }

            offered.Add(required == spell.RequiredSpellID
                ? spell
                : new NPCSpellEntry { TemplateID = spell.TemplateID, RequiredSpellID = required, Level = spell.Level });
        }

        return offered;
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        if (wizard is null) {
            return _serviceOptionBases;
        }

        // We've initialized a list of service option bases in InitializeServiceBases.
        // However, these options can change depending on how far the player has progressed.
        // We must now edit each service option to reflect the player's status.
        List<ServiceOptionBase> newServiceOptions = [];

        for (int i = 0; i < _serviceOptionBases.Count; i++) {
            var spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(SpellInventory[i].TemplateID);

            var hasSpell = wizard.SpellbookBehavior.HasSpell((uint) SpellInventory[i].TemplateID);
            if (hasSpell) {
                continue;
            }

            var newBase = (WizTrainingOption) _serviceOptionBases[i];

            // If a wizard is the same school as the spell, it is free to train.
            var schoolName = wizard.MagicSchoolBehavior.MagicSchool.ToString();
            if (spellTemplate.m_sMagicSchoolName == schoolName) {
                newBase.m_trainingCost = 0;
            }
            else {
                newBase.m_trainingCost = 1;
            }

            // Check if wizard has enough training points to train the spell, and if the
            // wizard is high enough level to train the spell.
            if (wizard.MagicSchoolBehavior.TrainingPoints < newBase.m_trainingCost
                    || wizard.MagicSchoolBehavior.Level < SpellInventory[i].Level) {
                newBase.m_bCanTrain = false;
                newBase.m_failedRequirement = null;

                newServiceOptions.Add(newBase);
                continue;
            }

            // If the spell has no required spell, it can be trained, else check if the wizard has the required spell.
            if (SpellInventory[i].RequiredSpellID == 0) {
                newBase.m_bCanTrain = true;
                newBase.m_failedRequirement = null;
            }
            else {
                var hasReqSpell = wizard.SpellbookBehavior.HasSpell((uint) SpellInventory[i].RequiredSpellID);

                if (!hasReqSpell) {
                    var reqSpellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(SpellInventory[i].RequiredSpellID);

                    newBase.m_bCanTrain = false;
                    newBase.m_failedRequirement = new ReqHasSpell() {
                        m_applyNOT = false,
                        m_operator = Operator.ROP_AND,
                        m_spellName = reqSpellTemplate.m_name
                    };
                }
                else {
                    newBase.m_bCanTrain = true;
                    newBase.m_failedRequirement = null;
                }
            }

            newServiceOptions.Add(newBase);
        }

        return newServiceOptions;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) { }

    private void InitializeServiceBases() {
        // Foreach spell in the inventory, create a service option. This method serves to initialize defaults.
        // When a player interacts with this object, we'll go back and edit each service option
        // depending on the player's current level, school, and existing spell inventory.
        _serviceOptionBases = [];

        foreach (var spell in SpellInventory) {
            var template = CoreObjectFactory.GetCoreTemplate(spell.TemplateID);
            if (template is null || template is not SpellTemplate spellTemplate) {
                Logger.Error("Trainer {0} has an invalid spell template with ID {1}",
                    Logger.Args(Entity.ActiveGameObject.m_templateID, spell.TemplateID));

                continue;
            }

            var spellOption = new WizTrainingOption {
                m_iconKey = WizBang.ToString(),
                m_serviceName = ServiceName,

                m_spellName = spellTemplate.m_name,
                m_displayKey = spellTemplate.m_displayName,
                m_requiredLevel = spell.Level,
                m_trainingIndex = SpellInventory.IndexOf(spell),
                m_trainingCost = spellTemplate.m_trainingCost,
            };

            if (spell.RequiredSpellID != 0) {
                var reqTemplate = CoreObjectFactory.GetCoreTemplate(spell.RequiredSpellID);
                if (reqTemplate is null || reqTemplate is not SpellTemplate reqSpellTemplate) {
                    Logger.Error("Trainer {0} has an invalid required spell template with ID {1}",
                        Logger.Args(Entity.ActiveGameObject.m_templateID, spell.RequiredSpellID));

                    continue;
                }

                spellOption.m_requirements = new RequirementList() {
                    m_applyNOT = false,
                    m_operator = Operator.ROP_AND,
                    m_requirements = new List<Requirement>() {
                        new ReqHasSpell() {
                            m_applyNOT = false,
                            m_operator = Operator.ROP_AND,
                            m_spellName = reqSpellTemplate.m_name,
                        }
                    }
                };
            }
            else {
                spellOption.m_requirements = null;
            }

            _serviceOptionBases.Add(spellOption);
        }
    }

}