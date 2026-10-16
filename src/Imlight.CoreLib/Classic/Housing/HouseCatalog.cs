using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Imlight.Classic;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;
using YamlDotNet.RepresentationModel;

namespace Imlight.CoreLib.Classic.Housing;

// CLASSIC: only our allowlist supplies house offers/room allowances. Modern native values
// remain research until dated evidence or an explicit owner ruling approves each definition.
internal sealed record HouseDefinition(uint TemplateId, uint StructureTemplateId, string Name,
    string ExteriorZone, string InteriorZone, string PreviewZone, int ExteriorCapacity,
    int InteriorCapacity, int Gold, int Crowns, int MinimumLevel, bool Enabled, bool ResaleAllowed = false, int ResaleGold = 0) {
    internal bool Complete => Enabled && TemplateId > 0 && StructureTemplateId > 0
        && ExteriorCapacity is > 0 and <= 400 && InteriorCapacity is > 0 and <= 400
        && MinimumLevel > 0 && (Gold > 0 || Crowns > 0);
}

// CLASSIC: the owner's pending housing rulings (resale value, attic capacity and ownership), read from
// the catalog's single `pending_rulings.enabled` switch. Off, every value is today's: no resale, attic 0.
internal sealed record HousePendingRulings(bool Enabled, int AtticCapacity, string AtticOwner) {
    internal static readonly HousePendingRulings Off = new(false, 0, "wizard");
}

internal static class HouseCatalog {
    internal const int MaximumOwned = 3; // Housing oldid68623, 2010-05-10: three Islands per wizard.
    internal static readonly AsyncLocal<IReadOnlyDictionary<uint, HouseDefinition>> TestDefinitions = new();
    internal static readonly AsyncLocal<HousePendingRulings> TestRulings = new();
    private static readonly Lazy<(IReadOnlyDictionary<uint, HouseDefinition> Houses, HousePendingRulings Rulings)> Runtime = new(ReadRuntime);

    /// <summary>The attic allowance in force: 0 (closed) until the owner's pending-rulings switch is on.</summary>
    internal static int AtticCapacity => Rulings.Enabled ? Rulings.AtticCapacity : 0;
    internal static HousePendingRulings Rulings => TestRulings.Value
        ?? (!ClassicRuntime.IsInitialized ? HousePendingRulings.Off : Runtime.Value.Rulings);

    internal static IEnumerable<HouseDefinition> Approved => Definitions.Values.Where(d => d.Complete);
    private static IReadOnlyDictionary<uint, HouseDefinition> Definitions => TestDefinitions.Value
        ?? (!ClassicRuntime.IsInitialized ? new Dictionary<uint, HouseDefinition>() : Runtime.Value.Houses);
    internal static bool IsDeed(WizItemTemplate template) => template?.m_adjectiveList?.Contains("Islands") == true
        && template.m_adjectiveList.Contains("Deed");

    internal static bool TryGet(uint template, out HouseDefinition definition) {
        if (Definitions.TryGetValue(template, out definition) && definition.Complete) return true;
        definition = null; return false;
    }
    internal static bool TryRoom(uint template, string zone, out int capacity) {
        capacity = 0;
        if (!TryGet(template, out var d)) return false;
        if (Same(zone, d.ExteriorZone)) capacity = d.ExteriorCapacity;
        else if (Same(zone, d.InteriorZone)) capacity = d.InteriorCapacity;
        return capacity > 0;
    }
    internal static bool IsApprovedRoom(string zone)
        => Approved.Any(d => Same(zone, d.ExteriorZone) || Same(zone, d.InteriorZone));
    internal static bool Same(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static (IReadOnlyDictionary<uint, HouseDefinition>, HousePendingRulings) ReadRuntime() {
        try {
            if (!ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive || ClassicStartup.ClassicDataRoot is not { } root)
                return (new Dictionary<uint, HouseDefinition>(), HousePendingRulings.Off);
            var path = Path.Combine(root, "housing", "houses-october-2010.yaml");
            if (!File.Exists(path)) return (new Dictionary<uint, HouseDefinition>(), HousePendingRulings.Off);
            var houses = Load(path, ClassicRuntime.Rules.Profile.Id, out var rulings);
            if (rulings.Enabled) Logger.Information("Classic housing: pending owner rulings ON (resale, attic {0}, {1} attic).",
                Logger.Args(rulings.AtticCapacity, rulings.AtticOwner));
            return (houses, rulings);
        }
        catch (Exception ex) {
            Logger.Error("Classic house catalog is unavailable; house offers remain closed: {0}", Logger.Args(ex.Message));
            return (new Dictionary<uint, HouseDefinition>(), HousePendingRulings.Off);
        }
    }

    internal static IReadOnlyDictionary<uint, HouseDefinition> Load(string path, string profile) => Load(path, profile, out _);

    internal static IReadOnlyDictionary<uint, HouseDefinition> Load(string path, string profile, out HousePendingRulings rulings) {
        rulings = HousePendingRulings.Off;
        var stream = new YamlStream();
        using (var reader = File.OpenText(path)) stream.Load(reader);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("House catalog needs one mapping.");
        if (Text(root, "id") != "houses-october-2010" || Text(root, "license_tag") != "own")
            throw new InvalidDataException("House catalog identity/license is invalid.");
        if (Node(root, "profiles") is not YamlSequenceNode profiles || Node(root, "houses") is not YamlSequenceNode houses)
            throw new InvalidDataException("House catalog needs profiles and houses lists.");
        if (!ClassicProfiles.Lists(profiles.Children.OfType<YamlScalarNode>().Select(p => p.Value ?? ""), profile)) return new Dictionary<uint, HouseDefinition>();
        rulings = ReadRulings(root);
        var result = new Dictionary<uint, HouseDefinition>();
        var allZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in houses.Children) {
            if (node is not YamlMappingNode item) throw new InvalidDataException("House entry must be a mapping.");
            var template = checked((uint)Number(item, "template"));
            var d = new HouseDefinition(template, checked((uint)Number(item, "structure")), Text(item, "name"),
                Text(item, "exterior"), Text(item, "interior"), Text(item, "preview"),
                Number(item, "exterior_capacity"), Number(item, "interior_capacity"),
                Number(item, "gold"), Number(item, "crowns"), Number(item, "minimum_level"),
                bool.TryParse(Text(item, "enabled"), out var enabled) ? enabled : throw new InvalidDataException("Invalid house enabled flag."),
                Node(item, "resale_allowed") is null ? false : bool.TryParse(Text(item, "resale_allowed"), out var sale) ? sale
                    : throw new InvalidDataException("Invalid house resale flag."),
                Number(item, "resale_gold"));
            // CLASSIC: a pending resale value sells only while the owner's switch is on (or if explicitly approved).
            if (d.ResaleGold < 0 || d.ResaleGold > Math.Max(d.Gold, 1)) throw new InvalidDataException("Invalid house resale value.");
            d = d with { ResaleAllowed = d.ResaleAllowed || (rulings.Enabled && d.ResaleGold > 0) };
            if (d.ResaleAllowed && d.ResaleGold <= 0) throw new InvalidDataException("An approved house resale needs a resale value.");
            if (template == 0 || d.StructureTemplateId == 0 || string.IsNullOrEmpty(d.Name)
                || !SafeZone(d.ExteriorZone) || !SafeZone(d.InteriorZone) || !SafeZone(d.PreviewZone)
                || !allZones.Add(d.ExteriorZone) || !allZones.Add(d.InteriorZone)
                || d.ExteriorCapacity is < 0 or > 400 || d.InteriorCapacity is < 0 or > 400
                || d.Gold < 0 || d.Crowns < 0 || d.MinimumLevel < 0 || d.MinimumLevel > ClassicProfiles.MaxLevelOf(profile) // CLASSIC: the cap, not 50
                || (d.Enabled && !d.Complete) || !result.TryAdd(template, d))
                throw new InvalidDataException("Duplicate, unsafe or incomplete house definition.");
        }
        return result;
    }
    private static HousePendingRulings ReadRulings(YamlMappingNode root) {
        if (Node(root, "pending_rulings") is not YamlMappingNode block) return HousePendingRulings.Off;
        if (!bool.TryParse(Text(block, "enabled"), out var enabled)) throw new InvalidDataException("Invalid pending_rulings switch.");
        var capacity = Number(block, "attic_capacity");
        var owner = Text(block, "attic_owner");
        if (capacity is < 0 or > HousingRules.AtticCompatibilityCeiling || owner is not ("wizard" or "account"))
            throw new InvalidDataException("Invalid pending attic ruling.");
        // The attic backend is per wizard (one ledger per character). An account-wide attic is not built.
        if (enabled && owner != "wizard") throw new InvalidDataException("Only a per-wizard attic is implemented.");
        return enabled ? new HousePendingRulings(true, capacity, owner) : HousePendingRulings.Off with { AtticOwner = owner };
    }
    private static bool SafeZone(string zone) => zone is { Length: > 8 and < 180 }
        && zone.StartsWith("Housing/", StringComparison.Ordinal) && !zone.Contains("..")
        && zone.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_');
    private static YamlNode Node(YamlMappingNode map, string key)
        => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static string Text(YamlMappingNode map, string key) => Node(map, key) is YamlScalarNode s ? s.Value ?? "" : "";
    private static int Number(YamlMappingNode map, string key)
        => Node(map, key) is null ? 0 : int.TryParse(Text(map, key), out var n) ? n : throw new InvalidDataException($"Invalid {key}.");
}
