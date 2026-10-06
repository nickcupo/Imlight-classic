using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Elixirs;

internal sealed class ElixirEntry {
    public ulong ItemId { get; set; }
    public string ItemDocumentId { get; set; } = "";
    public uint TemplateId { get; set; }
    public uint RemainingSeconds { get; set; }
    public List<string> Families { get; set; } = [];
    internal ElixirEntry Copy() => new() {
        ItemId = ItemId, ItemDocumentId = ItemDocumentId, TemplateId = TemplateId,
        RemainingSeconds = RemainingSeconds, Families = [.. Families],
    };
}

// CLASSIC: remaining Game seconds are durable; no wall-clock last-seen timestamp is charged
// on login. Original item documents survive consumption, but leave every usable item container.
internal sealed class ElixirLedger {
    public ulong OwnerId { get; set; }
    public uint Version { get; set; } = 1;
    public List<ElixirEntry> Active { get; set; } = [];
    internal static string DocumentId(ulong owner) => $"ClassicElixirs/{owner}";
    internal ElixirLedger Copy() => new() {
        OwnerId = OwnerId, Version = Version, Active = Active.Select(e => e.Copy()).ToList(),
    };

    internal bool TryActivate(ElixirEntry entry) {
        if (OwnerId == 0 || Version == uint.MaxValue || entry is null || entry.ItemId == 0
            || entry.TemplateId == 0 || entry.RemainingSeconds == 0 || string.IsNullOrEmpty(entry.ItemDocumentId)
            || entry.Families is not { Count: > 0 } || entry.Families.Any(string.IsNullOrWhiteSpace)
            || Active.Any(e => e.ItemId == entry.ItemId || e.RemainingSeconds == 0)) return false;
        if (Active.Count >= ElixirRules.MaximumActive
            || Active.Any(e => ElixirRules.Overlaps(e.Families, entry.Families))) return false;
        Active = Active.Append(entry.Copy()).ToList();
        Version++;
        return true;
    }

    internal ElixirEntry[] AdvanceOnline(uint seconds) {
        return AdvanceOnline(Active.ToDictionary(e => e.ItemId, _ => seconds));
    }

    // CLASSIC: the native active-elixir confirmation consumes only the selected original,
    // using the same zero-second cleanup state as natural expiry; it never refunds a boost.
    internal ElixirEntry Cancel(ulong itemId) {
        if (itemId == 0 || Version == uint.MaxValue) return null;
        var selected = Active.SingleOrDefault(e => e.ItemId == itemId);
        if (selected is null || selected.RemainingSeconds == 0) return null;
        var removed = selected.Copy();
        removed.RemainingSeconds = 0;
        Active = Active.Where(e => e.ItemId != itemId).ToList();
        Version++;
        return removed;
    }

    internal ElixirEntry[] AdvanceOnline(IReadOnlyDictionary<ulong, uint> elapsedByItem) {
        if (elapsedByItem is null || !elapsedByItem.Values.Any(s => s > 0) || Active.Count == 0
            || Version == uint.MaxValue || elapsedByItem.Keys.Any(id => !Active.Any(e => e.ItemId == id))) return [];
        foreach (var entry in Active) {
            if (!elapsedByItem.TryGetValue(entry.ItemId, out var seconds)) continue;
            entry.RemainingSeconds = seconds >= entry.RemainingSeconds ? 0 : entry.RemainingSeconds - seconds;
        }
        var expired = Active.Where(e => e.RemainingSeconds == 0).Select(e => e.Copy()).ToArray();
        Active = Active.Where(e => e.RemainingSeconds > 0).ToList();
        Version++;
        return expired;
    }
}
