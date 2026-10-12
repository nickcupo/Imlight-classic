// CLASSIC: Prospector Zeke's 1 Day mounts at their October 2010 gold price and +20% speed.
//
// classic-data/rules/mounts-october-2010.json lists the six 1 Day horse and broom rentals. Under the
// october-2010-arc1 profile each template's m_baseCost (the vendor price) and its SpeedEffectInfo
// (the speed the equipped mount gives) take the dated values; every other profile keeps the native
// template. tools/mac/classic_mounts.py writes the same values into the player's Root.wad so the
// shop window and the item card agree with the server.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic;

internal static class ClassicMountTemplates {

    internal const string RelativePath = "rules/mounts-october-2010.json";
    internal const string Profile = "october-2010-arc1";
    private const string CutoffText = "2010-10-31T23:59:59Z";

    internal sealed record Mount(uint Id, string Path, float NativeBaseCost, float BaseCost, int NativeSpeed, int Speed);

    private static IReadOnlyDictionary<uint, Mount>? s_mounts;
    private static string? s_profile;

    /// <summary>The loaded rows (tests and validation).</summary>
    internal static IReadOnlyDictionary<uint, Mount>? Mounts => s_mounts;

    /// <summary>
    /// Loads the dated rows when <paramref name="profile"/> is the October profile; otherwise clears them.
    /// </summary>
    internal static void Initialize(string? root, string profile) {
        s_mounts = null;
        s_profile = profile;
        if (profile != Profile) return;
        if (root is null) throw new InvalidDataException("October mount data root is missing.");
        s_mounts = Load(Path.Combine(root, RelativePath));
        Logger.Information("Classic mounts: {0} October 1 Day rental templates (price and speed).", Logger.Args(s_mounts.Count));
    }

    internal static IReadOnlyDictionary<uint, Mount> Load(string filename) {
        using var stream = File.OpenRead(filename);
        if (stream.Length > 64 * 1024) throw new InvalidDataException("October mount data exceeds its bound.");
        using var data = JsonDocument.Parse(stream);
        var document = data.RootElement;
        if (document.GetProperty("format").GetInt32() != 1
            || document.GetProperty("profile").GetString() != Profile
            || document.GetProperty("cutoff").GetString() != CutoffText)
            throw new InvalidDataException("Unexpected October mount data identity.");
        var mounts = new Dictionary<uint, Mount>();
        foreach (var row in document.GetProperty("mounts").EnumerateArray()) {
            var id = row.GetProperty("template_id").GetUInt32();
            var path = row.GetProperty("path").GetString() ?? "";
            var confidence = row.GetProperty("confidence").GetString();
            var mount = new Mount(id, path,
                row.GetProperty("native_base_cost").GetInt32(), row.GetProperty("base_cost").GetInt32(),
                row.GetProperty("native_speed").GetInt32(), row.GetProperty("speed").GetInt32());
            if (!path.StartsWith("ObjectData/Mounts/", StringComparison.Ordinal) || !path.EndsWith(".xml", StringComparison.Ordinal)
                || confidence is not ("verified" or "corroborated")
                || mount.BaseCost is < 1 or > 1_000_000 || mount.NativeBaseCost is < 1 or > 1_000_000
                || mount.Speed is < 1 or > 100 || mount.NativeSpeed is < 1 or > 100
                || !mounts.TryAdd(id, mount))
                throw new InvalidDataException($"Unapproved October mount row {id}.");
        }
        if (mounts.Count is < 1 or > 32) throw new InvalidDataException("October mount data has no rows or too many.");
        return mounts;
    }

    private static bool Active => s_mounts is not null && s_profile == Profile
        && ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.Profile.Id == Profile;

    /// <summary>
    /// Writes the dated price and speed into a freshly loaded mount template (CoreObjectFactory).
    /// A template whose native values differ from the expected ones is left unchanged and logged.
    /// </summary>
    internal static void Apply(CoreTemplate? template, string? path) {
        if (!Active || template is not WizItemTemplate item || !s_mounts!.TryGetValue(item.m_templateID, out var mount)) return;
        _ = Project(item, path, mount);
    }

    /// <summary>
    /// Projects <paramref name="mount"/> onto <paramref name="item"/>; true when the template now carries the dated values.
    /// </summary>
    internal static bool Project(WizItemTemplate item, string? path, Mount mount) {
        if (!string.Equals(path, mount.Path, StringComparison.Ordinal)) {
            Logger.Warning("Classic mounts: template {0} is at {1}, not {2}; left unchanged.", Logger.Args(mount.Id, path, mount.Path));
            return false;
        }

        var speeds = item.m_equipEffects?.OfType<SpeedEffectInfo>().ToList() ?? [];
        if (speeds.Count != 1
            || speeds[0].m_speedMultiplier != mount.NativeSpeed && speeds[0].m_speedMultiplier != mount.Speed
            || item.m_baseCost != mount.NativeBaseCost && item.m_baseCost != mount.BaseCost) {
            Logger.Warning("Classic mounts: template {0} has unexpected price {1} or speed effects; left unchanged.",
                Logger.Args(mount.Id, item.m_baseCost));
            return false;
        }

        item.m_baseCost = mount.BaseCost;
        speeds[0].m_speedMultiplier = mount.Speed;
        return true;
    }

    /// <summary>
    /// Fails boot when an October row did not reach its server template.
    /// </summary>
    internal static void ValidateAfterResources() {
        if (!Active) return;
        foreach (var mount in s_mounts!.Values) {
            var template = CoreObjectFactory.GetCoreTemplate(mount.Id) as WizItemTemplate;
            if (template is null || CoreObjectFactory.GetTemplatePath(mount.Id) != mount.Path
                || template.m_baseCost != mount.BaseCost
                || template.m_equipEffects?.OfType<SpeedEffectInfo>().SingleOrDefault()?.m_speedMultiplier != mount.Speed)
                throw new InvalidDataException($"October mount {mount.Id} failed its server template projection.");
        }
        Logger.Information("Classic mounts: {0} October rental templates verified.", Logger.Args(s_mounts.Count));
    }

}
