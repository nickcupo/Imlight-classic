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
    public ulong DeedId { get; set; }
    public string Zone { get; set; } = "";
    public int Capacity { get; set; } = HousingRules.PackageSlots;
    public int PackageNumber { get; set; }
    public uint Version { get; set; } = 1;
    public int SecondPackageNumber { get; set; }
    public uint SecondVersion { get; set; } = 1;
    public List<HousingEntry> Entries { get; set; } = [];
    internal static string DocumentId(ulong owner) => $"ClassicHousing/{owner}/dorm";
    internal static string DocumentId(HousingRoomIdentity room) => room.DeedId == 0 ? DocumentId(room.OwnerId)
        : $"ClassicHousing/{room.OwnerId}/house/{room.DeedId}/{room.Zone.Replace('/', '~')}";
    internal int PackageCount => SecondPackageNumber > 0 ? 2 : 1;
    internal int Count => Entries.Count(e => !e.Removed);
    internal uint CacheIndex(int slot) => slot < HousingRules.PackageSlots ? (uint)slot
        : HousingRules.SecondRoomUserData + (uint)(slot - HousingRules.PackageSlots);
    internal int PackageForSlot(int slot) => slot < HousingRules.PackageSlots ? PackageNumber : SecondPackageNumber;
    internal uint VersionForSlot(int slot) => slot < HousingRules.PackageSlots ? Version : SecondVersion;
    internal IEnumerable<HousingEntry> PackageEntries(int index) => Entries.Skip(index * HousingRules.PackageSlots).Take(HousingRules.PackageSlots);
    internal bool TrySlot(ulong id, uint dynamicProc, out int slot) {
        var index = (uint)id;
        slot = index < HousingRules.PackageSlots ? (int)index
            : index >= HousingRules.SecondRoomUserData && index < HousingRules.SecondRoomUserData + HousingRules.PackageSlots
                ? HousingRules.PackageSlots + (int)(index - HousingRules.SecondRoomUserData) : -1;
        return slot >= 0 && slot < Entries.Count && Entries.Count <= PackageCount * HousingRules.PackageSlots
            && id == HousingRules.PlacedGlobalId(index, dynamicProc);
    }

    internal HousingLedger Copy() => new() {
        OwnerId = OwnerId, DeedId = DeedId, Zone = Zone, Capacity = Capacity,
        PackageNumber = PackageNumber, Version = Version, SecondPackageNumber = SecondPackageNumber, SecondVersion = SecondVersion,
        Entries = Entries.Select(e => e.Copy()).ToList(),
    };

    internal bool TryPlace(HousingEntry entry, out int slot) {
        slot = -1;
        if (entry is null || entry.ItemId == 0 || entry.TemplateId == 0 || entry.TemplateId >= (1u << 28)
            || string.IsNullOrEmpty(entry.ItemDocumentId) || Count >= Capacity
            || !HousingRules.ValidPosition(entry.X, entry.Y, entry.Z, entry.Yaw)
            || Entries.Any(e => !e.Removed && e.ItemId == entry.ItemId)) return false;
        slot = Entries.FindIndex(e => e.Removed);
        if (slot < 0) {
            if (Entries.Count >= PackageCount * HousingRules.PackageSlots) return false;
            slot = Entries.Count;
            if (VersionForSlot(slot) == uint.MaxValue) return false;
            Entries.Add(entry);
        }
        else {
            if (VersionForSlot(slot) == uint.MaxValue) return false;
            Entries[slot] = entry;
        }
        Bump(slot);
        return true;
    }

    internal bool TryUpdate(int slot, float x, float y, float z, float yaw) {
        if (!Active(slot) || VersionForSlot(slot) == uint.MaxValue || !HousingRules.ValidPosition(x, y, z, yaw)) return false;
        var entry = Entries[slot];
        (entry.X, entry.Y, entry.Z, entry.Yaw) = (x, y, z, yaw);
        Bump(slot);
        return true;
    }

    internal bool TryPickup(int slot) {
        if (!Active(slot) || VersionForSlot(slot) == uint.MaxValue) return false;
        Entries[slot].Removed = true;
        Bump(slot);
        return true;
    }

    internal bool Active(int slot) => slot >= 0 && slot < Entries.Count && !Entries[slot].Removed;
    private void Bump(int slot) { if (slot < HousingRules.PackageSlots) Version++; else SecondVersion++; }
}

// A trusted attach chooses this identity. Client cache ids never choose an owner's deed or room.
internal sealed record HousingRoomIdentity(ulong OwnerId, ulong DeedId, string Zone) {
    internal static HousingRoomIdentity Dorm(ulong owner) => new(owner, 0, HousingRules.DormZone);
}

// Persisted allocator: distinct owners must never share a package/cache key. Existing ZoneID is
// left untouched; native cache identity is (type, subtype, associated ZoneID, package number).
internal sealed class HousingPackageAllocator {
    internal const string DocumentId = "ClassicHousingPackages/allocator";
    public int NextPackageNumber { get; set; } = 1;
}
