using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic.Gardening;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

// CLASSIC: a gardener's trainable gardening spells (December 2010; rules.gardening vendors[].spells, wintertusk-2011 only).
// The r806919 client opens its training window for options of the service "GardenTrainingService" (0x1420e04e0) and asks
// to train with MSG_TRAIN (MobileID, TrainingIndex) as for school spells; TrainService routes it here. A gardening spell
// costs gold (m_trainingCost) and needs a gardening rank (ReqGardeningLevel), from the server's table only.
internal sealed class InteractGardenTrainerComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    internal const string Service = "GardenTrainingService";

    public string ServiceName     => Service;
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => "GUI_NPCInteractText";
    public WizBangs WizBang       => WizBangs.Training;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_ShopOptionEquipment";

    internal ulong TrainerTemplateId => Entity.Template is GameObjectTemplate template ? template.m_templateID : 0;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && ClassicGardening.Vendor(goTemplate.m_templateID) is { Spells.Length: > 0 };

    /// <summary>The gardener's spells in table order (the training index is the position in this list).</summary>
    internal IReadOnlyList<GardenVendorSpell> Spells => ClassicGardening.Vendor(TrainerTemplateId)?.Spells ?? [];

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        var spells = Spells;
        var options = new List<ServiceOptionBase>();
        for (var i = 0; i < spells.Count; i++) {
            var spell = spells[i];
            if (CoreObjectFactory.GetCoreTemplate(spell.Template) is not GardenSpellTemplate template) continue;
            if (wizard?.SpellbookBehavior?.HasSpell(spell.Template) == true) continue;
            var rank = GardenProgress.Rank(wizard);
            var canTrain = wizard is null || Refusal(wizard, spell) is null;
            options.Add(Option(template, spell, i, canTrain, rank < spell.Rank ? RankRequirement(spell.Rank) : null));
        }
        return options;
    }

    internal static WizTrainingOption Option(SpellTemplate template, GardenVendorSpell spell, int index, bool canTrain,
        Requirement failed) => new() {
        m_iconKey = WizBangs.Training.ToString(),
        m_serviceName = Service,
        m_spellName = template.m_name,
        m_displayKey = template.m_displayName,
        m_requiredLevel = spell.Rank,
        m_trainingIndex = index,
        m_trainingCost = spell.Gold,
        m_bCanTrain = canTrain,
        m_requirements = new RequirementList { m_applyNOT = false, m_operator = Operator.ROP_AND, m_requirements = [RankRequirement(spell.Rank)] },
        m_failedRequirement = failed,
    };

    internal static ReqGardeningLevel RankRequirement(int rank) => new() {
        m_applyNOT = false,
        m_operator = Operator.ROP_AND,
        m_numericValue = rank,
        m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ,
    };

    /// <summary>Why <paramref name="wizard"/> may not learn <paramref name="spell"/> here, or null.</summary>
    internal static string Refusal(Wizard wizard, GardenVendorSpell spell) {
        if (wizard is null) return "No wizard.";
        if (wizard.SpellbookBehavior?.HasSpell(spell.Template) == true) return "You already know that spell.";
        if (GardenProgress.Rank(wizard) < spell.Rank) return "Your gardening rank is too low for that spell.";
        if ((wizard.GameStats?.m_currentGold ?? 0) < spell.Gold) return "You don't have enough gold.";
        return null;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) { }
}
