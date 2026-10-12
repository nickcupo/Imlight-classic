// CLASSIC: authored, inferred bounds from matching 2014 and r806919 native definitions.
// See docs/pet-talents-provenance.yaml. This does not certify October 2010 historical parity.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Game.Spells;

namespace Imlight.CoreLib.Game.Pet;

internal sealed record PetStatBinding(string Name, string TalentName, string Category, string School, string First, string Second,
    string Field, float Coefficient);

internal static class PetTalentPolicy {
    private static readonly IReadOnlyDictionary<string, PetStatBinding> s_stats = BuildStats();
    private static readonly IReadOnlyDictionary<string, string> s_cards = BuildCards();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> s_capacities = BuildCapacities();

    internal static PetStatBinding Binding(string name) => s_stats.GetValueOrDefault(name);

    internal static bool IsStatTalent(PetTalentTemplate talent, StatisticEffectInfo info, PetStatBinding binding)
        => talent is not null && binding is not null && info is not null
            && talent.m_talentName == binding.TalentName && (talent.m_maxStatList?.Count ?? 0) == 0
            && talent.m_effectList is { Count: 1 } && info.m_effectName == binding.Name && info.m_lookupIndex == -1;

    internal static bool Matches(PetBoostPlayerStatEffectTemplate native, PetStatBinding binding) {
        if (native is null || binding is null || native.m_effectName != binding.Name
            || native.m_effectCategory != binding.Category || native.m_school != binding.School
            || native.m_buffAll != (binding.School.Length == 0)
            || native.m_primaryStat1 != binding.First || native.m_primaryStat2 != binding.Second
            || native.m_secondaryStat != "Power" || native.m_secondaryModifier != .5f) return false;
        // Admit one known field only: a later client must not add unrelated bonuses through an old name.
        var fields = new Dictionary<string, float> {
            ["damage"] = native.m_damageBonusPercent, ["accuracy"] = native.m_accuracyBonusPercent,
            ["resistance"] = native.m_damageReducePercent, ["healing"] = native.m_healBonusPercent,
            ["incoming_healing"] = native.m_healIncBonusPercent, ["health"] = native.m_hitPointBonus,
            ["mana"] = native.m_manaBonus, ["power_pips"] = native.m_powerPipBonusPercent,
        };
        return fields.All(pair => float.IsFinite(pair.Value)
            && pair.Value == (pair.Key == binding.Field ? binding.Coefficient : 0))
            && native.m_damageBonusFlat == 0 && native.m_damageReduceFlat == 0
            && native.m_armorPiercingBonusPercent == 0 && native.m_energyBonus == 0
            && native.m_criticalHitRating == 0 && native.m_blockRating == 0
            && native.m_stunResistancePercent == 0 && native.m_shadowPipRating == 0
            && native.m_pipConversionRating == 0 && native.m_fishingLuckBonusPercent == 0;
    }

    internal static bool IsCapacityTalent(PetTalentTemplate talent) {
        var name = talent?.m_talentName.ToString() ?? "";
        var caps = talent?.m_maxStatList;
        return s_capacities.TryGetValue(name, out var expected) && caps is not null && caps.Count == expected.Count
            && caps.All(cap => cap is not null && expected.TryGetValue(cap.m_name.ToString(), out var value) && cap.m_value == value)
            && caps.Select(cap => cap.m_name.ToString()).Distinct().Count() == caps.Count && (talent.m_effectList?.Count ?? 0) == 0;
    }

    internal static bool IsCardTalent(PetTalentTemplate talent, ProvideSpellEffectInfo info)
        => talent is not null && info is not null && (talent.m_maxStatList?.Count ?? 0) == 0
            && talent.m_effectList is { Count: 1 } && info.m_effectName == "ProvideSpell" && info.m_numSpells == 1
            && s_cards.TryGetValue(talent.m_talentName.ToString(), out var spell) && info.m_spellName == spell;

    // Provider agreement does not prove the provided spell's values. This separate bounded map admits only
    // the two payloads checked in both owned clients; their current ranks agree with the dated trained counterparts.
    internal static bool IsApprovedGrantedSpell(string name) {
        if (name is not ("Pet - Sprite" or "Pet - Pixie")) return false;
        var template = SpellFactory.GetTemplate(name);
        if (template is null || template.m_name != name || template.m_sMagicSchoolName != "Life"
            || template.m_sTypeName != "Heal" || template.m_spellSourceType.ToString() != "kPet"
            || template.m_accuracy != 100 || template.m_spellRank is null
            || template.m_spellRank.m_spellRank != (name == "Pet - Sprite" ? 1 : 2)) return false;
        var effects = template.m_effects;
        if (effects?.Count != (name == "Pet - Sprite" ? 2 : 1)) return false;
        return name == "Pet - Sprite"
            ? Heal(effects[0], "kHeal", 50, "kFriendlySingle", 0) && Heal(effects[1], "kHealOverTime", 300, "kFriendlySingle", 3)
            : Heal(effects[0], "kHeal", 400, "kSelf", 0);
        static bool Heal(SpellEffect effect, string type, int value, string target, int rounds)
            => effect is not null && effect.GetType() == typeof(SpellEffect) && effect.m_effectType.ToString() == type
                && effect.m_effectParam == value && effect.m_effectTarget.ToString() == target
                && effect.m_numRounds == rounds && effect.m_paramPerRound == 0 && effect.m_healModifier == 1f
                && effect.m_sDamageType == "Life" && effect.m_pipNum == 0;
    }

    private static Dictionary<string, PetStatBinding> BuildStats() {
        var map = new Dictionary<string, PetStatBinding>(StringComparer.Ordinal);
        foreach (var school in new[] { "", "Balance", "Death", "Fire", "Ice", "Life", "Myth", "Storm" }) {
            var prefix = school.Length == 0 ? "All" : school;
            for (var tier = 1; tier <= (school.Length == 0 ? 2 : 3); tier++) {
                Add($"PetTalent{prefix}Damage0{tier}", prefix + "Damage", school, "Strength", "Will", "damage", new[] { .00005f, .00010f, .00015f }[tier - 1]);
                Add($"PetTalent{prefix}Accuracy0{tier}", prefix + "Accuracy", school, "Agility", "Intellect", "accuracy", new[] { .00005f, .00010f, .00015f }[tier - 1]);
                Add($"PetTalent{prefix}ReduceDamage0{tier}", prefix + "ReduceDamage", school, "Agility", "Strength", "resistance", new[] { .00008f, .00016f, .00024f }[tier - 1]);
            }
        }
        for (var tier = 1; tier <= 4; tier++) {
            Add($"PetTalentMaxHealth0{tier}", "MaxHealth", "", "Agility", "Will", "health", new[] { .12f, .16f, .20f, .24f }[tier - 1]);
            Add($"PetTalentMaxMana0{tier}", "MaxMana", "", "Intellect", "Will", "mana", new[] { .08f, .12f, .16f, .20f }[tier - 1]);
        }
        for (var tier = 1; tier <= 2; tier++) {
            var coefficient = tier == 1 ? .00006f : .00013f;
            Add($"PetTalentLifeHealing0{tier}", "AllHealing", "", "Strength", "Will", "healing", coefficient);
            Add($"PetTalentIncHealing0{tier}", "AllIncHealing", "", "Intellect", "Agility", "incoming_healing", coefficient);
        }
        Add("PetTalentPowerPips", "PowerPips", "", "Intellect", "Strength", "power_pips", .00008f);
        return map;
        void Add(string name, string category, string school, string first, string second, string field, float coefficient)
            => map.Add(name, new(name, TalentName(name, school, field), category, school, first, second, field, coefficient));
        static string TalentName(string name, string school, string field) {
            var tier = name[^2..];
            var group = school.Length == 0 ? "All" : school;
            return field switch {
                "damage" => $"Talent-Damage-{group}{tier}", "accuracy" => $"Talent-Accuracy-{group}{tier}",
                "resistance" => $"Talent-Resist-{group}{tier}", "health" => "Talent-Health" + tier,
                "mana" => "Talent-Mana" + tier, "healing" => "Talent-LifeHealing-" + tier,
                "incoming_healing" => "Talent-IncHealing-" + tier, _ => "Talent-Pips",
            };
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, int>> BuildCapacities() {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (shortName, stat) in new Dictionary<string, string> {
            ["Str"] = "Strength", ["Int"] = "Intellect", ["Agi"] = "Agility", ["Will"] = "Will", ["Pwr"] = "Power" })
            for (var tier = 1; tier <= 4; tier++) result.Add($"Talent-Stat01-{shortName}0{tier}",
                new Dictionary<string, int> { [stat] = new[] { 25, 40, 50, 65 }[tier - 1] });
        foreach (var (pair, varying, fixedStat) in new[] {
            ("AgiInt", "Agility", "Intellect"), ("AgiPwr", "Power", "Agility"), ("AgiStr", "Strength", "Agility"),
            ("AgiWill", "Agility", "Will"), ("IntPwr", "Intellect", "Power"), ("IntWill", "Intellect", "Will"),
            ("StrInt", "Strength", "Intellect"), ("StrPwr", "Power", "Strength"), ("StrWill", "Will", "Strength"),
            ("WillPwr", "Will", "Power"), })
            for (var tier = 2; tier <= 4; tier++) result.Add($"Talent-Stat02-{pair}0{tier}",
                new Dictionary<string, int> { [varying] = new[] { 15, 25, 40 }[tier - 2], [fixedStat] = 25 });
        return result;
    }

    private static Dictionary<string, string> BuildCards() {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var schools = new Dictionary<string, string[]> {
            ["Balance"] = ["Hex", "Weakness", "Power Play", "Balanceblade"],
            ["Death"] = ["Curse", "Feint", "Plague", "Infection"],
            ["Fire"] = ["Fire Elf", "Take Power", "Fireblade", "Sunbird"],
            ["Ice"] = ["Evil Snowman", "Iceblade", "Blizzard", "Tower Shield"],
            ["Life"] = ["Sprite", "Pixie", "Lifeblade", "Spirit Armor"],
            ["Myth"] = ["Mend Minion", "Buff Minion", "Humongofrog", "Pierce"],
            ["Storm"] = ["Lightning Strike", "Stormblade", "Storm Shark", "Disarm"],
        };
        foreach (var (school, spells) in schools)
            for (var index = 0; index < spells.Length; index++)
                map.Add($"Talent-Spell-{school}0{index + 1}", "Pet - " + spells[index]);
        foreach (var (talent, spell) in new Dictionary<string, string> {
            ["BlindingLight"] = "Blinding Light", ["Blizzard"] = "Blizzard", ["DarkPact"] = "Dark Pact",
            ["Dryad"] = "Dryad", ["IceArmor"] = "Ice Armor", ["LegionShield"] = "Legion Shield",
            ["Minotaur01"] = "Minotaur", ["Reshuffle"] = "Reshuffle", ["Firezilla"] = "Firezilla - Pet",
            ["Stormzilla"] = "Stormzilla - Pet",
        }) map.Add("Talent-Spell-" + talent, "Pet - " + spell);
        return map;
    }
}
