using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Housing;

// CLASSIC: authoritative floats are deliberately separate from the client's packed cache. Picking
// up a supporting crate never snaps, rounds, or moves a floating rug in another slot.
internal sealed class HousingEntry {
    public ulong ItemId { get; set; }
    public string ItemDocumentId { get; set; } = "";
    public uint TemplateId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public bool Removed { get; set; }
    internal HousingEntry Copy() => (HousingEntry)MemberwiseClone();
}

internal sealed class HousingLedger {
    public ulong OwnerId { get; set; }
    public int PackageNumber { get; set; }
    public uint Version { get; set; } = 1;
    public List<HousingEntry> Entries { get; set; } = [];
    internal static string DocumentId(ulong owner) => $"ClassicHousing/{owner}/dorm";

    internal HousingLedger Copy() => new() {
        OwnerId = OwnerId, PackageNumber = PackageNumber, Version = Version,
        Entries = Entries.Select(e => e.Copy()).ToList(),
    };

    internal bool TryPlace(HousingEntry entry, out int slot) {
        slot = -1;
        if (entry is null || entry.ItemId == 0 || entry.TemplateId == 0 || entry.TemplateId >= (1u << 28)
            || string.IsNullOrEmpty(entry.ItemDocumentId) || Version == uint.MaxValue
            || !HousingRules.ValidPosition(entry.X, entry.Y, entry.Z, entry.Yaw)
            || Entries.Any(e => !e.Removed && e.ItemId == entry.ItemId)) return false;
        slot = Entries.FindIndex(e => e.Removed);
        if (slot < 0) {
            if (Entries.Count >= HousingRules.PackageSlots) return false;
            slot = Entries.Count;
            Entries.Add(entry);
        }
        else Entries[slot] = entry;
        Version++;
        return true;
    }

    internal bool TryUpdate(int slot, float x, float y, float z, float yaw) {
        if (!Active(slot) || Version == uint.MaxValue || !HousingRules.ValidPosition(x, y, z, yaw)) return false;
        var entry = Entries[slot];
        (entry.X, entry.Y, entry.Z, entry.Yaw) = (x, y, z, yaw);
        Version++;
        return true;
    }

    internal bool TryPickup(int slot) {
        if (!Active(slot) || Version == uint.MaxValue) return false;
        Entries[slot].Removed = true;
        Version++;
        return true;
    }

    internal bool Active(int slot) => slot >= 0 && slot < Entries.Count && !Entries[slot].Removed;
}

// Persisted allocator: distinct owners must never share a package/cache key. Existing ZoneID is
// left untouched; native cache identity is (type, subtype, associated ZoneID, package number).
internal sealed class HousingPackageAllocator {
    internal const string DocumentId = "ClassicHousingPackages/allocator";
    public int NextPackageNumber { get; set; } = 1;
}
