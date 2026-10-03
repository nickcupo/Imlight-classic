/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * CLASSIC PET HATCHING RULES (2010)
 * ========================================================================
 *
 * PURPOSE:
 * Two pets make an egg: who may hatch, what it costs, and what the baby
 * inherits (species, maximum stats, talent pool).
 *
 * USAGE EXAMPLE:
 * var refusal = PetHatchRules.Check(mine, theirs, now);
 * var gold = PetHatchRules.GoldCost(mine.Pedigree + theirs.Pedigree);
 * var baby = PetHatchRules.Breed(mine, theirs, random);
 *
 * NOTE:
 * The Pet Hatchery, oldid 113079 (2010-10-11): Adult or older, one hatch a
 * pet every 24 hours, 20,000 to 50,000 gold by the pets. Pets, oldid 124741
 * (2010-12-31): a hybrid is second generation, mixes the parents' stats
 * (each picked at 0%, 50% or 100% between the parents) and talents, and
 * "will take after one" parent. The price formula between the two ends and
 * the talent mix are inferred (no dated rule found).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Pets;

/// <summary>A parent's part in a hatching.</summary>
public sealed record HatchParent(ulong TemplateId, int Level, IReadOnlyDictionary<string, int> MaxStats,
    IReadOnlyList<string> TalentPool, IReadOnlyList<string> Talents, int Pedigree, long LastHatchUnix);

/// <summary>What the egg will hatch into.</summary>
public sealed record HatchResult(ulong TemplateId, IReadOnlyDictionary<string, int> MaxStats, IReadOnlyList<string> TalentPool);

public static class PetHatchRules {

    public const int MinLevel = PetRules.Adult;
    public const int MinGold = 20_000;
    public const int MaxGold = 50_000;
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    /// <summary>The pedigree span (two pets' summed talent ranks) the price is spread over: 10 talents of rank 1..5 each.</summary>
    public const int MinPedigree = 20, MaxPedigree = 100;

    /// <summary>Why these two may not hatch now, or null.</summary>
    public static string Check(HatchParent mine, HatchParent theirs, long nowUnix) {
        if (mine is null || theirs is null) {
            return "a pet is missing";
        }

        if (mine.Level < MinLevel || theirs.Level < MinLevel) {
            return "both pets must be Adult or older";
        }

        foreach (var p in new[] { mine, theirs }) {
            if (p.LastHatchUnix > 0 && nowUnix - p.LastHatchUnix < (long) Cooldown.TotalSeconds) {
                return "a pet can hatch once every 24 hours";
            }
        }

        return null;
    }

    /// <summary>Gold to hatch: 20,000 for the plainest pair up to 50,000 for the best pedigrees.</summary>
    public static int GoldCost(int combinedPedigree) {
        var t = Math.Clamp((combinedPedigree - MinPedigree) / (double) (MaxPedigree - MinPedigree), 0, 1);
        return (int) (Math.Round((MinGold + t * (MaxGold - MinGold)) / 250.0) * 250);
    }

    /// <summary>
    /// The baby: one parent's species; each maximum stat at 0%, 50% or 100% of the way between the parents'; a talent
    /// pool of up to 10 drawn from both parents, a parent's learned talents first.
    /// </summary>
    public static HatchResult Breed(HatchParent mine, HatchParent theirs, Random random) {
        var species = random.Next(2) == 0 ? mine.TemplateId : theirs.TemplateId;
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var stat in mine.MaxStats.Keys.Union(theirs.MaxStats.Keys, StringComparer.OrdinalIgnoreCase)) {
            mine.MaxStats.TryGetValue(stat, out var a);
            theirs.MaxStats.TryGetValue(stat, out var b);
            var f = random.Next(3) * 0.5;
            stats[stat] = (int) Math.Round(a + (b - a) * f, MidpointRounding.AwayFromZero);
        }

        // Learned talents carry over more often than ones a parent only might learn.
        var learned = mine.Talents.Concat(theirs.Talents).Where(PetRules.IsTalentIn2010).Distinct().OrderBy(_ => random.Next()).ToList();
        var pooled = mine.TalentPool.Concat(theirs.TalentPool).Where(PetRules.IsTalentIn2010).Distinct().OrderBy(_ => random.Next()).ToList();
        var pool = new List<string>();
        foreach (var t in learned.Where(_ => random.NextDouble() < 0.75).Concat(pooled)) {
            if (pool.Count >= 10) {
                break;
            }

            if (!pool.Contains(t)) {
                pool.Add(t);
            }
        }

        return new HatchResult(species, stats, pool);
    }

}
