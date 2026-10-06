using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Elixirs;

// CLASSIC: each active item accrues only authenticated online monotonic elapsed time.
// Failed saves retain their debt; new activations cannot inherit an older item's elapsed time.
internal sealed class ElixirOnlineClock(long frequency) {
    private readonly long _frequency = frequency > 0 && frequency <= long.MaxValue / uint.MaxValue
        ? frequency : throw new ArgumentOutOfRangeException(nameof(frequency));
    private readonly Dictionary<ulong, long> _anchors = [];

    internal void Begin(IEnumerable<ulong> ids, long now) {
        _anchors.Clear();
        foreach (var id in ids.Where(id => id != 0).Distinct()) _anchors[id] = now;
    }

    internal Dictionary<ulong, uint> Pending(IEnumerable<ulong> ids, long now) {
        var current = ids.Where(id => id != 0).ToHashSet();
        foreach (var id in _anchors.Keys.Where(id => !current.Contains(id)).ToArray()) _anchors.Remove(id);
        var elapsed = new Dictionary<ulong, uint>();
        foreach (var id in current) {
            if (!_anchors.TryGetValue(id, out var anchor)) { _anchors[id] = now; continue; }
            if (now <= anchor) continue;
            var seconds = (ulong)(now - anchor) / (ulong)_frequency;
            if (seconds > 0) elapsed[id] = (uint)Math.Min(seconds, uint.MaxValue);
        }
        return elapsed;
    }

    internal void Commit(IReadOnlyDictionary<ulong, uint> saved) {
        foreach (var (id, seconds) in saved) {
            if (!_anchors.TryGetValue(id, out var anchor))
                throw new InvalidOperationException("An untracked elixir cannot commit online time.");
            _anchors[id] = checked(anchor + seconds * _frequency);
        }
    }
}
