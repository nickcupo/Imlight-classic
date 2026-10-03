using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Monstrology;

// Duel-local observations, never an authoritative wallet. Defeat/flee discards everything.
internal sealed class MonstrologyPendingExtraction {
    private readonly Dictionary<(ulong Owner, ulong Target), PendingEssence> _pending = new();
    private static readonly HashSet<string> s_families = new(StringComparer.Ordinal) {
        "Undead", "Gobbler", "Parrot", "Pig", "PolarBear", "Treant", "Wyrm",
        "MONST_Colossus", "MONST_Cyclops", "MONST_Dinos", "MONST_Draconians", "MONST_Elephant",
        "MONST_Equine", "MONST_Golems", "MONST_Imps", "MONST_Insects", "MONST_Manders", "MONST_Spiders"
    };
    // Exact installed spell adjective/creature adjective pairs; no singularization or name guessing.
    internal static bool TryFamily(SpellTemplate enchantment, out string family) {
        family = null;
        if (enchantment?.m_effects?.Count(e => e?.m_effectType == kSpellEffects.kCollectEssence
            && e.m_effectParam == 100 && e.m_effectTarget == kEffectTarget.kSpell) != 1) return false;
        var matches = enchantment.m_adjectives?.Where(x => x != null && x.StartsWith("Collect_",StringComparison.Ordinal)
            && s_families.Contains(x[8..])).ToArray();
        if (matches?.Length != 1) return false;
        family = matches[0][8..]; return true;
    }

    internal void Observe(ulong owner, ulong target, uint collectedTemplate, bool eligible,
        int healthBefore, int healthAfter, bool countHit = true) {
        if (owner == 0 || target == 0 || collectedTemplate == 0 || !eligible
            || healthBefore <= 0 || healthAfter >= healthBefore || healthAfter < 0) return;
        var key = (owner, target);
        var amount = (countHit ? 1 : 0) + (healthAfter == 0 ? 1 : 0);
        if (amount == 0) return;
        if (_pending.TryGetValue(key, out var prior)) {
            if (prior.Creature != collectedTemplate) throw new InvalidOperationException("Extraction target identity changed");
            amount = checked(amount + prior.Animus);
        }
        _pending[key] = new(owner, target, collectedTemplate, amount);
    }

    internal PendingEssence[] Finish(bool victory, IReadOnlySet<ulong> remainingOwners) {
        var result = victory ? _pending.Values.Where(x => remainingOwners.Contains(x.Owner)).ToArray() : [];
        _pending.Clear();
        return result; // Caller still needs validated XP/rank/resistance policy before persistent Award.
    }
}
internal sealed record PendingEssence(ulong Owner, ulong Target, uint Creature, int Animus);
