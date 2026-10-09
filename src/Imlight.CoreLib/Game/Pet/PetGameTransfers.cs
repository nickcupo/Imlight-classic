// CLASSIC: a phantom-zone pet game travels inside the server; the client's attach names no game, track or pet.
//
// The kiosk JOIN is admitted in the wizard's own scene (the Pavilion); the transfer then creates a new SessionActor and
// PetGameService in the game's private phantom zone, so the admitted game waits here, bound to the account, character,
// game, track, inventory pet and destination zone. It is consumed once, after the new session's normal attach
// (transfer proof, account claim and character ownership checks) completed in exactly that zone, and it expires.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>An admitted pet game on its way to its phantom zone, and where the wizard came from.</summary>
internal sealed record PendingPetGame(ulong Account, ulong Character, string Game, int Track, ulong PetItem, string Zone,
    string OriginZone, string OriginLocation, DateTime Expires);

internal sealed class PetGameTransferRegistry(int capacity = 1024, TimeSpan? lifetime = null) {
    private readonly object _gate = new();
    private readonly Dictionary<ulong, PendingPetGame> _entries = [];
    internal TimeSpan Lifetime { get; } = lifetime ?? TimeSpan.FromMinutes(2);

    internal bool Queue(PendingPetGame entry, DateTime now) {
        if (entry is null || entry.Account == 0 || entry.Character == 0 || entry.PetItem == 0
            || string.IsNullOrWhiteSpace(entry.Zone) || string.IsNullOrWhiteSpace(entry.Game)) return false;
        lock (_gate) {
            Prune(now);
            if (!_entries.ContainsKey(entry.Character) && _entries.Count >= capacity) return false;
            _entries[entry.Character] = entry with { Expires = now + Lifetime };
            return true;
        }
    }

    internal bool TryConsume(ulong account, ulong character, string zone, DateTime now, out PendingPetGame entry) {
        entry = null;
        lock (_gate) {
            Prune(now);
            if (!_entries.TryGetValue(character, out var found) || found.Account != account
                || !string.Equals(found.Zone, zone, StringComparison.OrdinalIgnoreCase)) return false;
            _entries.Remove(character);
            entry = found;
            return true;
        }
    }

    internal void Cancel(ulong character) { lock (_gate) _entries.Remove(character); }

    private void Prune(DateTime now) {
        foreach (var id in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToArray()) _entries.Remove(id);
    }
}

internal static class PetGameTransfers {
    private static readonly PetGameTransferRegistry s_entries = new();
    internal static bool Queue(PendingPetGame entry, DateTime now) => s_entries.Queue(entry, now);
    internal static bool TryConsume(ulong account, ulong character, string zone, DateTime now, out PendingPetGame entry)
        => s_entries.TryConsume(account, character, zone, now, out entry);
    internal static void Cancel(ulong character) => s_entries.Cancel(character);
}
