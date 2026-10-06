using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Housing;

// CLASSIC: the client sees synthetic cache ids; the server retains the one original item
// document and item id. No container-id or cache-id received from a client is an owner claim.
internal sealed class AtticPackage {
    public int PackageNumber { get; set; }
    public uint UserData { get; set; }
    public uint Version { get; set; } = 1;
    public List<HousingEntry> Entries { get; set; } = [];
    internal AtticPackage Copy() => new() { PackageNumber = PackageNumber, UserData = UserData,
        Version = Version, Entries = Entries.Select(x => x.Copy()).ToList() };
}

internal sealed class AtticLedger {
    public ulong OwnerId { get; set; }
    public ulong ContainerId { get; set; }
    public List<AtticPackage> Packages { get; set; } = [];
    internal static string DocumentId(ulong owner) => $"ClassicHousing/{owner}/attic";
    internal int Count => Packages.Sum(p => p.Entries.Count(e => !e.Removed));
    internal AtticLedger Copy() => new() { OwnerId = OwnerId, ContainerId = ContainerId,
        Packages = Packages.Select(p => p.Copy()).ToList() };

    internal bool Valid() => OwnerId != 0 && ContainerId != 0 && Packages.Count == HousingRules.AtticPackageCount
        && Packages.Select(p => p.PackageNumber).Distinct().Count() == Packages.Count
        && Packages.Select((p, i) => p.PackageNumber > 0 && p.UserData == HousingRules.AtticUserData(i)
            && p.Version > 0 && p.Entries.Count <= HousingRules.PackageSlots).All(x => x)
        && Count <= HousingRules.AtticCompatibilityCeiling;

    internal bool TryAdd(HousingEntry entry, int capacity, out AtticPatch patch) {
        patch = default;
        if (!Valid() || capacity <= 0 || capacity > HousingRules.AtticCompatibilityCeiling || Count >= capacity
            || entry is null || entry.ItemId == 0 || entry.TemplateId == 0 || entry.TemplateId >= (1u << 28)
            || string.IsNullOrEmpty(entry.ItemDocumentId)
            || Packages.Any(p => p.Entries.Any(e => !e.Removed && e.ItemId == entry.ItemId))) return false;
        foreach (var package in Packages) {
            if (package.Version == uint.MaxValue) continue;
            var slot = package.Entries.FindIndex(e => e.Removed);
            if (slot < 0 && package.Entries.Count >= HousingRules.PackageSlots) continue;
            var copy = entry.Copy();
            copy.Removed = false;
            if (slot < 0) { slot = package.Entries.Count; package.Entries.Add(copy); }
            else package.Entries[slot] = copy;
            package.Version++;
            patch = new(package.PackageNumber, package.UserData, package.Version, slot, copy.Copy());
            return true;
        }
        return false;
    }

    internal bool TryFind(ulong syntheticId, uint dynamicProc, out int packageIndex, out int slot) {
        packageIndex = slot = -1;
        if (!Valid()) return false;
        for (var i = 0; i < Packages.Count; i++) {
            if (!HousingRules.TryAtticSlot(syntheticId, dynamicProc, i, Packages[i].Entries.Count, out var found)
                || Packages[i].Entries[found].Removed) continue;
            packageIndex = i; slot = found; return true;
        }
        return false;
    }

    internal bool TryRemove(int packageIndex, int slot, out AtticPatch patch) {
        patch = default;
        if (!Valid() || packageIndex < 0 || packageIndex >= Packages.Count) return false;
        var p = Packages[packageIndex];
        if (slot < 0 || slot >= p.Entries.Count || p.Entries[slot].Removed || p.Version == uint.MaxValue) return false;
        var entry = p.Entries[slot].Copy();
        p.Entries[slot].Removed = true;
        p.Version++;
        patch = new(p.PackageNumber, p.UserData, p.Version, slot, entry);
        return true;
    }
}

internal readonly record struct AtticPatch(int PackageNumber, uint UserData, uint Version, int Slot, HousingEntry Entry) {
    internal uint CacheIndex => checked(UserData + (uint)Slot);
}
internal readonly record struct HousingDeletePatch(uint Version, int Slot);
internal sealed class DiscardedHousingItem {
    public ulong OwnerId { get; set; }
    public HousingEntry Entry { get; set; }
}
