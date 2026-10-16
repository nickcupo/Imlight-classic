using System;
using System.Text.RegularExpressions;
using Imlight.Classic;

namespace Imlight.CoreLib.Classic.Gardening;

// CLASSIC: gardening (December 2010, patch 1.130; archived update notes un_december2010.txt) for the May 2011
// Wintertusk target only. Off in every profile that exists today: it needs the `gardening` feature switch AND a
// profile that is (or extends) wintertusk-2011. The owner ruled (2026-10-16) that gardening comes after houses.
//
// Native r806919 facts used here (Ghidra, wgc.gpr; see playbot-reports/house-garden.md for addresses):
// - a garden is one GardeningProxy object (template 329071, its only behavior is GardeningBehavior) in a housing blob
//   package of type "Housing", subtype "Garden" (ClientBlobPatcher::MSG_PatchGardening 0x140f20430 looks that cache up);
// - the object's HousingBlobCustomObject.m_customData is GardeningCompression's short form (0x141d9c180 writer with
//   param 1, 0x141d9c740 reader with param 1; magic 0xDECADE02, version 4);
// - MSG_PATCHGARDENING needs VersionNumber == cached + 1, and its GIDID selects the proxy in that package;
// - patch commands (0x140f286e0): 1 add soil (TemplateID = soil), 2 remove at position, 3 add plant (TemplateID),
//   4 set flags (TemplateID = flags), 5 set flags and next harvest time (Time), 8 revive, 10 harvest-ready glow,
//   13 advance a mature plant to its next stage;
// - client requests: MSG_GARDENINGCOMMAND (0x140f277b0; 2 = plow at a point, 3 = plant a seed: TemplateID + ObjectGID),
//   MSG_GARDENINGCASTSPELL (0x140f28270), MSG_GARDENINGHARVESTPLANT (0x140fc1a30); the client waits for
//   MSG_GARDENINGCOMMANDRESPONSE (0x140f5aa60 clears its busy flag).
internal static class GardenRules {
    internal const string Wintertusk2011 = "wintertusk-2011";
    internal const string BlobType = "Housing";
    internal const string SubType = "Garden";
    internal const uint ProxyTemplate = 329071;     // ObjectData/Housing/Structures/GardeningProxy.xml
    internal const uint PackageUserData = 800;      // after furniture 0..199, attic 200..599 and the second room 600..799
    internal const uint ShortMagic = 0xDECADE02;    // DAT_1432e9244
    internal const uint FullMagic = 0xDECADE01;     // DAT_1432e9240
    internal const ushort Version = 4;              // DAT_1432e9248
    internal const int MaxPlants = 500;             // our bound; native truncates past 19,900 bytes
    internal const float SamePointDistance = 1.5f;  // positions travel as rounded integers in the packed XY

    // Native GardenPlant.m_flags bits (0x140f286e0 / 0x141d9c740).
    internal const uint FlagSoil = 0x1;
    internal const uint FlagNeedWater = 0x4, FlagNeedSun = 0x8, FlagNeedPollination = 0x10,
        FlagNeedMagic = 0x20, FlagNeedMusic = 0x40;
    internal const uint NeedMask = 0x7c;
    internal const uint FlagPest = 0x80;
    internal const uint FlagHarvestReady = 0x800;

    internal static readonly uint[] NeedFlags = [FlagNeedWater, FlagNeedSun, FlagNeedPollination, FlagNeedMagic, FlagNeedMusic];

    // Patch commands (client side of MSG_PATCHGARDENING).
    internal const byte PatchAddSoil = 1, PatchRemove = 2, PatchAddPlant = 3, PatchFlags = 4, PatchHarvested = 5,
        PatchRevive = 8, PatchHarvestReady = 10, PatchAdvanceMature = 13;
    // Client commands (MSG_GARDENINGCOMMAND).
    internal const byte CommandPlow = 2, CommandPlant = 3;

    /// <summary>On only where the profile allows gardening and the profile is the May 2011 one (or a child).</summary>
    internal static bool Enabled => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Gardening)
        && ClassicProfiles.IsOrExtends(ClassicRuntime.Rules.Profile.Id, Wintertusk2011);

    private static readonly Regex Rate = new(@"^\s*(?:(\d+)d)?\s*(?:(\d+)h)?\s*(?:(\d+)m)?\s*$", RegexOptions.CultureInvariant);

    /// <summary>A client rate string ("9d0h0m", "0d13h0m", "0m") in seconds; 0 means never / not used.</summary>
    internal static uint Seconds(string rate) {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var m = Rate.Match(rate);
        if (!m.Success) throw new FormatException($"Unknown gardening rate '{rate}'.");
        long Part(int i) => m.Groups[i].Success ? long.Parse(m.Groups[i].Value) : 0;
        var seconds = Part(1) * 86400 + Part(2) * 3600 + Part(3) * 60;
        return seconds is >= 0 and <= uint.MaxValue / 4 ? (uint)seconds : throw new FormatException($"Rate '{rate}' is too long.");
    }

    internal static uint Between(uint min, uint max, Random random) {
        if (max < min) (min, max) = (max, min);
        return max == min ? min : (uint)(min + (long)(random.NextDouble() * (max - min)));
    }

    internal static bool SamePoint(float ax, float ay, float az, float bx, float by, float bz)
        => MathF.Abs(ax - bx) <= SamePointDistance && MathF.Abs(ay - by) <= SamePointDistance && MathF.Abs(az - bz) <= 8f;
}
