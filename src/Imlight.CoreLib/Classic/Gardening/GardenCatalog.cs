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
}

internal sealed record SoilInfo(uint TemplateId, GardenSize Size, bool Enchanted, bool Enriched);

internal enum GardenSpellKind { SoilPreparation = 0, Growing = 1, InsectFighting = 2, PlantProtection = 3, Utility = 4 }

/// <summary>A GardenSpellTemplate's gardening fields (client values).</summary>
internal sealed record GardenSpellInfo(uint TemplateId, GardenSpellKind Kind, uint SoilTemplateId, bool[] Provides,
    int Radius, int EnergyCost, int Utility) {
    internal bool ProvidesAny => Provides.Any(p => p);
}

internal interface IGardenCatalog {
    SeedInfo Seed(uint templateId);
    SoilInfo Soil(uint templateId);
    GardenSpellInfo Spell(uint templateId);
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

        public SeedInfo Seed(uint id) => _seeds.GetOrAdd(id, Read);
        public SoilInfo Soil(uint id) => _soils.GetOrAdd(id, ReadSoil);
        public GardenSpellInfo Spell(uint id) => _spells.GetOrAdd(id, ReadSpell);

        private static SeedInfo Read(uint id) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item) return null;
            var seed = item.m_behaviors?.OfType<SeedBehaviorTemplate>().FirstOrDefault();
            return seed is null ? null : FromTemplate(id, seed, item.m_adjectiveList?.Any(a => a?.ToString() == "Seed") == true); // a seed item: "Housing", "Seed"
        }

        private static SoilInfo ReadSoil(uint id) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate item
                || item.m_adjectiveList?.Any(a => a?.ToString() == "GardeningSoil") != true) return null;
            var name = item.m_objectName?.ToString() ?? "";
            var size = name.EndsWith("Large", StringComparison.Ordinal) ? GardenSize.Large
                : name.EndsWith("Medium", StringComparison.Ordinal) ? GardenSize.Medium : GardenSize.Small;
            return new SoilInfo(id, size, name.StartsWith("Enchanted", StringComparison.Ordinal),
                name.StartsWith("Enriched", StringComparison.Ordinal));
        }

        private static GardenSpellInfo ReadSpell(uint id) => CoreObjectFactory.GetCoreTemplate(id) is GardenSpellTemplate s
            ? FromSpell(id, s) : null;
    }

    internal static GardenSpellInfo FromSpell(uint id, GardenSpellTemplate s) => new(id, (GardenSpellKind)(int)s.m_gardenSpellType,
        s.m_soilTemplateID, [s.m_providesWater, s.m_providesSun, s.m_providesPollination, s.m_providesMagic, s.m_providesMusic],
        s.m_affectedRadius, s.m_energyCost, (int)s.m_utilitySpellType);

    internal static SeedInfo FromTemplate(uint id, SeedBehaviorTemplate s, bool isSeedItem) {
        uint R(string rate) => GardenRules.Seconds(rate);
        return new SeedInfo(id, isSeedItem ? GardenStage.Seed : (GardenStage)(int)s.m_plantStage, s.m_nextStageTemplateID,
            R(s.m_nextStageRate),
            [R(s.m_needsWaterMinRate), R(s.m_needsSunMinRate), R(s.m_needsPollinationMinRate), R(s.m_needsMagicMinRate), R(s.m_needsMusicMinRate)],
            [R(s.m_needsWaterMaxRate), R(s.m_needsSunMaxRate), R(s.m_needsPollinationMaxRate), R(s.m_needsMagicMaxRate), R(s.m_needsMusicMaxRate)],
            [R(s.m_needsWaterWiltRate), R(s.m_needsSunWiltRate), R(s.m_needsPollinationWiltRate), R(s.m_needsMagicWiltRate), R(s.m_needsMusicWiltRate)],
            R(s.m_harvestMinRate), R(s.m_harvestMaxRate), s.m_wiltTemplateID, s.m_deadTemplateID, s.m_youngTemplateID,
            s.m_matureTemplateID, s.m_maximumNumberOfHarvests, s.m_harvestXP, (GardenSize)(int)s.m_seedSize,
            s.m_requiresEnchantedSoil, s.m_requiresEnrichedSoil, s.m_gardeningLevelRequirement);
    }
}

/// <summary>A fixed catalog for tests and tools.</summary>
internal sealed class FixedGardenCatalog : IGardenCatalog {
    internal Dictionary<uint, SeedInfo> Seeds { get; } = [];
    internal Dictionary<uint, SoilInfo> Soils { get; } = [];
    internal Dictionary<uint, GardenSpellInfo> Spells { get; } = [];
    public SeedInfo Seed(uint id) => Seeds.GetValueOrDefault(id);
    public SoilInfo Soil(uint id) => Soils.GetValueOrDefault(id);
    public GardenSpellInfo Spell(uint id) => Spells.GetValueOrDefault(id);
}
