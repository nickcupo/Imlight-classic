using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Gardening;

internal enum GardenStage { Seedling = 0, Young = 1, Mature = 2, Elder = 3, Wilting = 4, Dead = 5, Seed = 99 }
internal enum GardenSize { Small = 0, Medium = 1, Large = 2 }

/// <summary>One plant stage template's SeedBehaviorTemplate, in seconds (client values, owner ruling D7).</summary>
internal sealed record SeedInfo(uint TemplateId, GardenStage Stage, uint NextStageTemplateId, uint NextStageSeconds,
    uint[] NeedMin, uint[] NeedMax, uint[] WiltSeconds, uint HarvestMin, uint HarvestMax,
    uint WiltTemplateId, uint DeadTemplateId, uint YoungTemplateId, uint MatureTemplateId,
    uint MaxHarvests, int HarvestXp, GardenSize Size, bool RequiresEnchantedSoil, bool RequiresEnrichedSoil,
    int GardeningLevelRequirement) {
    internal bool HasNeeds => NeedMax.Any(n => n > 0);
    /// <summary>The template's likes and dislikes: an adjective near the plant and the factor it puts on growth time.</summary>
    internal IReadOnlyList<(string Adjective, float Modifier)> Likes { get; init; } = [];
    /// <summary>The pests the stage can get: chance in percent per hour and the pest template.</summary>
    internal IReadOnlyList<(float PercentPerHour, uint Pest)> Pests { get; init; } = [];
    /// <summary>How often the stage is checked for pests (m_pestUpdateRate), in seconds; 0 = never.</summary>
    internal uint PestCheckSeconds { get; init; }
    /// <summary>The plant's own adjectives (a neighbour's likes look for them).</summary>
    internal IReadOnlyList<string> Adjectives { get; init; } = [];
}

/// <summary>A PestBehaviorTemplate (a pest, or a protection a spell leaves on a plant), client values.</summary>
internal sealed record PestInfo(uint TemplateId, int Rank, uint WiltMinSeconds, uint WiltMaxSeconds, bool Good, bool[] Provides) {
    internal bool ProvidesAny => Provides.Any(p => p);
}

internal sealed record SoilInfo(uint TemplateId, GardenSize Size, bool Enchanted, bool Enriched) {
    /// <summary>A planter (pot) is furniture that holds one plant indoors or out (December 2010 notes).</summary>
    internal bool Pot { get; init; }
}

internal enum GardenSpellKind { SoilPreparation = 0, Growing = 1, InsectFighting = 2, PlantProtection = 3, Utility = 4 }

/// <summary>A GardenSpellTemplate's gardening fields (client values).</summary>
internal sealed record GardenSpellInfo(uint TemplateId, GardenSpellKind Kind, uint SoilTemplateId, bool[] Provides,
    int Radius, int EnergyCost, int Utility) {
    internal bool ProvidesAny => Provides.Any(p => p);
    internal int BugZapLevel { get; init; }
    internal uint ProtectionTemplateId { get; init; }
    internal string Name { get; init; } = "";
}

/// <summary>GardenSpellTemplate::UtilitySpellType (client enum).</summary>
internal enum GardenUtility { None = 0, Zap = 1, Revive = 2, Inspect = 3, Stasis = 4, PlowAll = 5, PlantAll = 6, HarvestNow = 7 }

internal interface IGardenCatalog {
    SeedInfo Seed(uint templateId);
    SoilInfo Soil(uint templateId);
    GardenSpellInfo Spell(uint templateId);
    PestInfo Pest(uint templateId) => null;
    /// <summary>A placed object's adjectives (furniture near a plant, for likes and dislikes).</summary>
    IReadOnlyList<string> Adjectives(uint templateId) => [];
}

// CLASSIC: gardening numbers come from the owned r806919 client templates (D7: unsourced numbers use client values,
// marked unverified); dated December 2010 / May 2011 overrides belong in classic-data when found.
internal static class GardenCatalog {
    internal static readonly AsyncLocal<IGardenCatalog> Test = new();
    internal static IGardenCatalog Current => Test.Value ?? ClientTemplates.Instance;

    private sealed class ClientTemplates : IGardenCatalog {
        internal static readonly ClientTemplates Instance = new();
        private readonly ConcurrentDictionary<uint, SeedInfo> _seeds = new();
        private readonly ConcurrentDictionary<uint, SoilInfo> _soils = new();
        private readonly ConcurrentDictionary<uint, GardenSpellInfo> _spells = new();

        private readonly ConcurrentDictionary<uint, PestInfo> _pests = new();
        private readonly ConcurrentDictionary<uint, IReadOnlyList<string>> _adjectives = new();
        public SeedInfo Seed(uint id) => _seeds.GetOrAdd(id, Read);
        public SoilInfo Soil(uint id) => _soils.GetOrAdd(id, ReadSoil);
        public GardenSpellInfo Spell(uint id) => _spells.GetOrAdd(id, ReadSpell);
        public PestInfo Pest(uint id) => _pests.GetOrAdd(id, ReadPest);
        public IReadOnlyList<string> Adjectives(uint id) => _adjectives.GetOrAdd(id, ReadAdjectives);

        private static IReadOnlyList<string> ReadAdjectives(uint id) => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate item
            ? item.m_adjectiveList?.Select(a => a?.ToString()).Where(a => !string.IsNullOrEmpty(a)).ToArray() ?? [] : [];

        private static PestInfo ReadPest(uint id) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item) return null;
            var pest = item.m_behaviors?.OfType<PestBehaviorTemplate>().FirstOrDefault();
            return pest is null ? null : FromPest(id, pest);
        }

        private static SeedInfo Read(uint id) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item) return null;
            var seed = item.m_behaviors?.OfType<SeedBehaviorTemplate>().FirstOrDefault();
            return seed is null ? null : FromTemplate(id, seed, item.m_adjectiveList?.Any(a => a?.ToString() == "Seed") == true) with { // a seed item: "Housing", "Seed"
                Adjectives = item.m_adjectiveList?.Select(a => a?.ToString()).Where(a => !string.IsNullOrEmpty(a)).ToArray() ?? [],
            };
        }

        private static SoilInfo ReadSoil(uint id) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item
                || item.m_adjectiveList?.Any(a => a?.ToString() == "GardeningSoil") != true) return null;
            return SoilFromName(id, item.m_objectName?.ToString() ?? "", item.m_adjectiveList.Any(a => a?.ToString() == "GardeningPot"));
        }

        private static GardenSpellInfo ReadSpell(uint id) => CoreObjectFactory.GetCoreTemplate(id) is GardenSpellTemplate s
            ? FromSpell(id, s) : null;
    }

    internal static GardenSpellInfo FromSpell(uint id, GardenSpellTemplate s) => new(id, (GardenSpellKind)(int)s.m_gardenSpellType,
        s.m_soilTemplateID, [s.m_providesWater, s.m_providesSun, s.m_providesPollination, s.m_providesMagic, s.m_providesMusic],
        s.m_affectedRadius, s.m_energyCost, (int)s.m_utilitySpellType) {
        BugZapLevel = s.m_bugZapLevel, ProtectionTemplateId = s.m_protectionTemplateID, Name = s.m_name ?? "",
    };

    internal static PestInfo FromPest(uint id, PestBehaviorTemplate p) => new(id, p.m_pestRank, GardenRules.Seconds(p.m_wiltMinRate),
        GardenRules.Seconds(p.m_wiltMaxRate), p.m_goodPest,
        [p.m_providesWater, p.m_providesSun, p.m_providesPollination, p.m_providesMagic, p.m_providesMusic]);

    /// <summary>
    /// A soil pile or planter from its template name: prepared soil "Prepared/Enchanted/Enriched Soil - Small/Medium/Large";
    /// a pot HOUSE_GDN_SM_/_Med_/_LG_Pot_Normal/_Enchant/_Enriched_... (client templates).
    /// </summary>
    internal static SoilInfo SoilFromName(uint id, string name, bool pot) {
        if (pot) {
            var size = name.Contains("_LG_", StringComparison.OrdinalIgnoreCase) ? GardenSize.Large
                : name.Contains("_Med_", StringComparison.OrdinalIgnoreCase) ? GardenSize.Medium : GardenSize.Small;
            return new SoilInfo(id, size, name.Contains("_Enchant", StringComparison.OrdinalIgnoreCase),
                name.Contains("_Enriched", StringComparison.OrdinalIgnoreCase)) { Pot = true };
        }
        var pile = name.EndsWith("Large", StringComparison.Ordinal) ? GardenSize.Large
            : name.EndsWith("Medium", StringComparison.Ordinal) ? GardenSize.Medium : GardenSize.Small;
        return new SoilInfo(id, pile, name.StartsWith("Enchanted", StringComparison.Ordinal), name.StartsWith("Enriched", StringComparison.Ordinal));
    }

    internal static SeedInfo FromTemplate(uint id, SeedBehaviorTemplate s, bool isSeedItem) {
        uint R(string rate) => GardenRules.Seconds(rate);
        return new SeedInfo(id, isSeedItem ? GardenStage.Seed : (GardenStage)(int)s.m_plantStage, s.m_nextStageTemplateID,
            R(s.m_nextStageRate),
            [R(s.m_needsWaterMinRate), R(s.m_needsSunMinRate), R(s.m_needsPollinationMinRate), R(s.m_needsMagicMinRate), R(s.m_needsMusicMinRate)],
            [R(s.m_needsWaterMaxRate), R(s.m_needsSunMaxRate), R(s.m_needsPollinationMaxRate), R(s.m_needsMagicMaxRate), R(s.m_needsMusicMaxRate)],
            [R(s.m_needsWaterWiltRate), R(s.m_needsSunWiltRate), R(s.m_needsPollinationWiltRate), R(s.m_needsMagicWiltRate), R(s.m_needsMusicWiltRate)],
            R(s.m_harvestMinRate), R(s.m_harvestMaxRate), s.m_wiltTemplateID, s.m_deadTemplateID, s.m_youngTemplateID,
            s.m_matureTemplateID, s.m_maximumNumberOfHarvests, s.m_harvestXP, (GardenSize)(int)s.m_seedSize,
            s.m_requiresEnchantedSoil, s.m_requiresEnrichedSoil, s.m_gardeningLevelRequirement) {
            Likes = s.m_likesDislikesList?.Where(l => l is not null && !string.IsNullOrEmpty(l.m_adjective))
                .Select(l => (l.m_adjective.ToString(), l.m_modifier)).ToArray() ?? [],
            Pests = s.m_pestList?.Where(p => p is not null && p.m_pestTemplateID != 0)
                .Select(p => (p.m_percentPerHour, p.m_pestTemplateID)).ToArray() ?? [],
            PestCheckSeconds = R(s.m_pestUpdateRate),
        };
    }
}

/// <summary>A fixed catalog for tests and tools.</summary>
internal sealed class FixedGardenCatalog : IGardenCatalog {
    internal Dictionary<uint, SeedInfo> Seeds { get; } = [];
    internal Dictionary<uint, SoilInfo> Soils { get; } = [];
    internal Dictionary<uint, GardenSpellInfo> Spells { get; } = [];
    internal Dictionary<uint, PestInfo> Pests { get; } = [];
    internal Dictionary<uint, string[]> Adjectives { get; } = [];
    public SeedInfo Seed(uint id) => Seeds.GetValueOrDefault(id);
    public SoilInfo Soil(uint id) => Soils.GetValueOrDefault(id);
    public GardenSpellInfo Spell(uint id) => Spells.GetValueOrDefault(id);
    public PestInfo Pest(uint id) => Pests.GetValueOrDefault(id);
    IReadOnlyList<string> IGardenCatalog.Adjectives(uint id) => Adjectives.GetValueOrDefault(id) ?? [];
}
