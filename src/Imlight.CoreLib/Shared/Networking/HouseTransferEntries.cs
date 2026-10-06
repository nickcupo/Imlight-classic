// CLASSIC: house identities travel only inside the server; client attach packets do not name a deed.
using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Classic.Housing;

namespace Imlight.CoreLib.Shared.Networking;

internal readonly record struct ZoneInstanceIdentity(ulong OwnerCharId, ulong HousingDeedId = 0);

internal sealed class HouseTransferRegistry(int capacity = 1024, TimeSpan? lifetime = null) {
    private sealed record Entry(ulong Owner, ulong Deed, string Zone, DateTime Expires);
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Entry> _entries = [];
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(2);

    internal bool Queue(ulong character, ulong owner, ulong deed, string zone, DateTime now) {
        if (character == 0 || owner == 0 || deed == 0 || string.IsNullOrWhiteSpace(zone)) return false;
        lock (_gate) {
            Prune(now);
            if (!_entries.ContainsKey(character) && _entries.Count >= capacity) return false;
            _entries[character] = new(owner, deed, zone, now + _lifetime);
            return true;
        }
    }

    internal bool TryConsume(ulong character, ulong owner, string zone, DateTime now, out ulong deed) {
        deed = 0;
        lock (_gate) {
            Prune(now);
            if (!_entries.TryGetValue(character, out var entry) || entry.Owner != owner
                || !string.Equals(entry.Zone, zone, StringComparison.OrdinalIgnoreCase)) return false;
            _entries.Remove(character);
            deed = entry.Deed;
            return true;
        }
    }

    internal void Cancel(ulong character) { lock (_gate) _entries.Remove(character); }

    private void Prune(DateTime now) {
        foreach (var id in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToArray()) _entries.Remove(id);
    }
}

internal static class HouseTransferEntries {
    private static readonly HouseTransferRegistry s_entries = new();
    internal static bool Queue(ulong character, ulong owner, ulong deed, string zone, DateTime now)
        => s_entries.Queue(character, owner, deed, zone, now);
    internal static bool TryConsume(ulong character, ulong owner, string zone, DateTime now, out ulong deed)
        => s_entries.TryConsume(character, owner, zone, now, out deed);
    internal static void Cancel(ulong character) => s_entries.Cancel(character);

    internal static bool OwnerPresent(IEnumerable<OnlinePlayer> players, ulong owner, ulong deed)
        => owner != 0 && deed != 0 && players.Any(p => p.CharacterId == owner && p.InstanceOwnerId == owner
            && p.HousingDeedId == deed);

    internal static bool SameInstance(ulong owner, ulong deed, ulong otherOwner, ulong otherDeed)
        => new ZoneInstanceIdentity(owner, deed) == new ZoneInstanceIdentity(otherOwner, otherDeed);

    // CLASSIC: durable ownership and approved rooms are rechecked at both transfer and attach.
    internal static bool MayEnter(ulong character, ulong owner, ulong deed, string zone)
        => character != 0 && HouseCollection.TryGetOwned(owner, deed, out var house)
            && HouseCatalog.TryRoom(house.TemplateId, zone, out _)
            && (character == owner || OwnerPresent(OnlinePlayerCollection.GetOnlinePlayers()
                .Where(p => HouseCatalog.TryRoom(house.TemplateId, p.CurrentZone, out _)), owner, deed));
}
