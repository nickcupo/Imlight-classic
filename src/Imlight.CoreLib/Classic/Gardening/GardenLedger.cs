using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Gardening;

// CLASSIC: one plant or prepared-soil pile. Field names follow the client's GardenPlant; times are absolute Unix
// seconds (the client's _ftime32 clock). Positions are stored exactly as the client sent them.
internal sealed class GardenPlantState {
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public uint TemplateId { get; set; }          // current stage template (or the soil template)
    public uint Flags { get; set; }               // native GardenPlant.m_flags
    public uint SoilTemplateId { get; set; }      // the soil it was planted in (0 for soil piles themselves)
    public uint StageStart { get; set; }
    public uint NextStageAt { get; set; }         // 0 = no further stage
    public uint HarvestAt { get; set; }           // 0 = not harvestable
    public uint[] NeedAt { get; set; } = new uint[5];   // when each need next arises (0 = never)
    public uint[] NeedSince { get; set; } = new uint[5]; // when an active need began (0 = not active)
    public uint PausedSince { get; set; }         // growth pauses while a need is active
    public uint HarvestCount { get; set; }
    public uint PestTemplateId { get; set; }
    public uint PestProtectionTemplateId { get; set; }
    public ulong SeedItemId { get; set; }         // the original seed item, kept for audit
    public string SeedItemDocumentId { get; set; } = "";
    public ulong PlantId { get; set; }            // m_plantIdentifierForETL
    internal bool IsSoil => (Flags & GardenRules.FlagSoil) != 0;
    internal GardenPlantState Copy() {
        var c = (GardenPlantState)MemberwiseClone();
        c.NeedAt = (uint[])NeedAt.Clone(); c.NeedSince = (uint[])NeedSince.Clone();
        return c;
    }
}

internal sealed class GardenLedger {
    public ulong OwnerId { get; set; }
    public ulong DeedId { get; set; }
    public string Zone { get; set; } = "";
    public int PackageNumber { get; set; }
    public uint Version { get; set; } = 1;
    public uint LastAdvanced { get; set; }
    public ulong NextPlantId { get; set; } = 1;
    public List<GardenPlantState> Plants { get; set; } = [];

    internal static string DocumentId(ulong owner, ulong deed, string zone)
        => $"ClassicGarden/{owner}/{deed}/{zone.Replace('/', '~')}";

    internal GardenPlantState At(float x, float y, float z, bool soil)
        => Plants.FirstOrDefault(p => p.IsSoil == soil && GardenRules.SamePoint(p.X, p.Y, p.Z, x, y, z));
    internal GardenPlantState Any(float x, float y, float z)
        => Plants.FirstOrDefault(p => GardenRules.SamePoint(p.X, p.Y, p.Z, x, y, z));

    internal GardenLedger Copy() => new() {
        OwnerId = OwnerId, DeedId = DeedId, Zone = Zone, PackageNumber = PackageNumber, Version = Version,
        LastAdvanced = LastAdvanced, NextPlantId = NextPlantId, Plants = Plants.Select(p => p.Copy()).ToList(),
    };
}

/// <summary>One MSG_PATCHGARDENING to publish after the ledger is saved (Version is the package version it brings).</summary>
internal readonly record struct GardenPatch(byte Command, float X, float Y, float Z, uint TemplateOrFlags, uint Time, uint Version);
