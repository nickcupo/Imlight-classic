// CLASSIC: select researched, profile-listed cards within the real equipped deck's rules.
// Composition is a tactical preference; no historical capacity, spell amount or training level is invented here.
using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Spells;

namespace Imlight.Classic.Pvp;

public sealed record ArenaDeckLimits(string PrimarySchool, int MaxCards, int SchoolCopies, int OtherCopies,
                                     int SchoolMaxRank, int OtherMaxRank);
public sealed record ArenaDeckSpell(ClassicSpellRecord Record, uint TemplateId, int NativeRank, int MaxCopies,
                                    bool Supported = true);
public sealed record ArenaDeckEntry(ArenaDeckSpell Spell, int Copies);

public static class ArenaPvpDeckPlanner {
    private sealed record Card(ArenaDeckSpell Native, SpellValues Values, ArenaCardRole Role, double Strength);

    public static IReadOnlyList<ArenaDeckEntry> Plan(IEnumerable<ArenaDeckSpell> available, string school, int level,
        string profile, IReadOnlyList<string> lineage, ArenaDeckLimits limits) {
        if (level is < 1 or > 50 || limits.MaxCards <= 0) return [];
        var cards = available.Where(s => s.Supported && s.Record.ClientTemplate is not null && s.Record.IsInProfile(profile)
            && (s.Record.Kind == "trained" && string.Equals(s.Record.School, school, StringComparison.OrdinalIgnoreCase)
                // Independently taught for one point, with no prerequisite school chain; recorded level20.
                || s.Record.Kind == "crossover" && s.Record.Id == "spell.balance.reshuffle"))
            .DistinctBy(s => s.TemplateId).Select(s => Classify(s, s.Record.ValuesFor(lineage)))
            .OfType<Card>().Where(c => c.Values.LevelLearned is >= 1 && c.Values.LevelLearned <= level)
            .Where(c => AllowedRank(c.Native, limits)).ToList();
        var counts = new Dictionary<uint, int>();
        var order = new List<Card>();
        int Count() => counts.Values.Sum();
        bool Put(Card card) {
            var own = string.Equals(card.Native.Record.School, limits.PrimarySchool, StringComparison.OrdinalIgnoreCase);
            var cap = own ? limits.SchoolCopies : limits.OtherCopies;
            if (card.Native.MaxCopies > 0) cap = Math.Min(cap, card.Native.MaxCopies);
            var have = counts.GetValueOrDefault(card.Native.TemplateId);
            if (Count() >= limits.MaxCards || have >= cap) return false;
            if (have == 0) order.Add(card);
            counts[card.Native.TemplateId] = have + 1;
            return true;
        }
        Card? Best(ArenaCardRole role) => cards.Where(c => c.Role == role).OrderByDescending(c => c.Strength)
            .ThenBy(c => c.Values.Pips.Fixed ?? 7).ThenBy(c => c.Native.TemplateId).FirstOrDefault();
        // Reserve attacks before including support cards, so low-level/limited decks cannot draw seven unused utilities.
        var supportBudget = limits.MaxCards / 2;
        foreach (var role in new[] { ArenaCardRole.Reshuffle, ArenaCardRole.Heal, ArenaCardRole.Shield,
                     ArenaCardRole.Blade, ArenaCardRole.Trap, ArenaCardRole.RemoveWard, ArenaCardRole.Weakness,
                     ArenaCardRole.RemoveCharm }) {
            if (Count() < supportBudget && Best(role) is { } card) Put(card);
        }
        var hits = cards.Where(c => c.Role == ArenaCardRole.Damage).ToList();
        var cheap = hits.Where(c => c.Values.Pips.Fixed is <= 2).OrderByDescending(c => c.Strength).FirstOrDefault();
        var big = hits.OrderByDescending(c => c.Strength).ThenBy(c => c.Native.TemplateId).FirstOrDefault();
        var area = hits.Where(c => c.Values.Effects.Any(e => e.Targets == SpellTargets.AllEnemies))
            .OrderByDescending(c => c.Strength).FirstOrDefault();
        var dot = hits.Where(c => c.Values.Effects.Any(e => e.Kind == SpellEffectKind.Dot))
            .OrderByDescending(c => c.Strength).FirstOrDefault();
        foreach (var card in new[] { cheap, big, area, dot }.OfType<Card>().Distinct()) Put(card);
        // Cycle strong attacks instead of filling the entire deck with one costly card.
        var pool = new[] { cheap, big, area, dot }.OfType<Card>()
            .Concat(hits.OrderByDescending(c => c.Strength / Math.Max(1, c.Values.Pips.Fixed ?? 4)))
            .Distinct().ToList();
        while (Count() < limits.MaxCards) {
            var before = Count();
            foreach (var card in pool) Put(card);
            if (Count() == before) break;
        }
        return order.Select(c => new ArenaDeckEntry(c.Native, counts[c.Native.TemplateId])).ToList();
    }

    private static bool AllowedRank(ArenaDeckSpell card, ArenaDeckLimits limits) {
        var own = string.Equals(card.Record.School, limits.PrimarySchool, StringComparison.OrdinalIgnoreCase);
        var rank = own ? limits.SchoolMaxRank : limits.OtherMaxRank;
        return rank < 0 || card.NativeRank <= rank;
    }

    private static Card? Classify(ArenaDeckSpell card, SpellValues values) {
        var effects = values.Effects;
        var hits = effects.Where(e => (e.Kind is SpellEffectKind.Damage or SpellEffectKind.Dot or SpellEffectKind.Steal)
                                     && e.HasAmount && e.Targets != SpellTargets.Self).ToList();
        if (hits.Count > 0) return new(card, values, ArenaCardRole.Damage,
            hits.Sum(e => (e.Min!.Value + e.Max!.Value) / 2.0) * values.Accuracy);
        var heal = effects.Where(e => e.Kind is SpellEffectKind.Heal or SpellEffectKind.Hot && e.HasAmount).ToList();
        if (heal.Count > 0) return new(card, values, ArenaCardRole.Heal, heal.Sum(e => (e.Min!.Value + e.Max!.Value) / 2.0));
        if (effects.Any(e => e.Kind == SpellEffectKind.Reshuffle)) return new(card, values, ArenaCardRole.Reshuffle, 1);
        if (effects.Any(e => e.Kind == SpellEffectKind.RemoveWard)) return new(card, values, ArenaCardRole.RemoveWard, 1);
        if (effects.Any(e => e.Kind == SpellEffectKind.RemoveCharm)) return new(card, values, ArenaCardRole.RemoveCharm, 1);
        if (effects.Any(e => e.Kind == SpellEffectKind.Blade && e.Percent > 0)) return new(card, values, ArenaCardRole.Blade,
            effects.Where(e => e.Kind == SpellEffectKind.Blade).Sum(e => e.Percent ?? 0));
        if (effects.Any(e => e.Kind == SpellEffectKind.Trap && e.Percent > 0)) return new(card, values, ArenaCardRole.Trap,
            effects.Where(e => e.Kind == SpellEffectKind.Trap).Sum(e => e.Percent ?? 0));
        if (effects.Any(e => e.Kind == SpellEffectKind.Shield)) return new(card, values, ArenaCardRole.Shield,
            effects.Where(e => e.Kind == SpellEffectKind.Shield).Sum(e => Math.Abs(e.Percent ?? 0)));
        if (effects.Any(e => e.Kind == SpellEffectKind.Charm && e.Percent < 0)) return new(card, values, ArenaCardRole.Weakness,
            effects.Where(e => e.Kind == SpellEffectKind.Charm).Sum(e => Math.Abs(e.Percent ?? 0)));
        return null;
    }
}
