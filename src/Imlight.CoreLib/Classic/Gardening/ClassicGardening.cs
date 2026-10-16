#nullable enable

using System.IO;
using System.Linq;
using System.Threading;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;

namespace Imlight.CoreLib.Classic.Gardening;

// CLASSIC: the active profile's gardening tables (classic-data/rules/gardening-*.yaml, rules.gardening). Loaded at boot by
// ClassicProgression.Initialize; empty in every profile without the rule. Every consumer reads Active, which is empty
// unless gardening is on (GardenRules.Enabled), so nothing here reaches a profile that does not garden.
internal static class ClassicGardening {
    private static volatile GardeningTables s_tables = GardeningTables.Empty;
    private static volatile string? s_profile;
    internal static readonly AsyncLocal<GardeningTables?> Test = new();

    /// <summary>The loaded tables (tests may override), whether or not gardening is on.</summary>
    internal static GardeningTables Loaded => Test.Value ?? s_tables;

    /// <summary>The tables while gardening is on in the active profile, else <see cref="GardeningTables.Empty"/>.</summary>
    internal static GardeningTables Active => Test.Value is { } test ? test
        : GardenRules.Enabled && s_tables.AppliesTo(s_profile) ? s_tables : GardeningTables.Empty;

    internal static void Initialize(ClassicProfile profile, string classicDataRoot) {
        s_profile = profile.Id;
        s_tables = GardeningTablesLoader.LoadForProfile(profile, classicDataRoot);
        if (!s_tables.AppliesTo(profile.Id)) return;
        Logger.Information("Classic gardening {Table}: Energy {Energy}, {Ranks} ranks, {Vendors} gardeners ({Spells} trainable spells), {Harvests} harvest tables.",
            Logger.Args(s_tables.Id, s_tables.Energy is { } e ? $"{e.Start} +{e.PerEvenLevel}/even level" : "client",
                s_tables.MaxRank, s_tables.Vendors.Length, s_tables.Vendors.Sum(v => v.Spells.Length), s_tables.Harvests.Length));
    }

    /// <summary>The gardener with this NPC template while gardening is on, else null.</summary>
    internal static GardenVendor? Vendor(ulong npcTemplate) => Active.Vendor(npcTemplate);

    /// <summary>Energy at a level while the profile's gardening Energy rule applies, else null (the client's value).</summary>
    internal static int? MaxEnergy(int level) {
        var tables = Loaded;
        return tables.Energy is { } energy && (Test.Value is not null || tables.AppliesTo(s_profile)
            && ClassicRuntime.IsInitialized && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Gardening))
            ? energy.MaxAt(level) : null;
    }
}
