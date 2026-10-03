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
 * CLASSIC PET RULES (PET PAVILION, MAY 2010)
 * ========================================================================
 *
 * PURPOSE:
 * The 2010 pet loop as one pure rule set: levels Baby to Epic, pet energy
 * per game, the stat points and experience a pet game gives, what a snack
 * gives (with liked and loved snacks), and which talent a pet learns when
 * it grows a level.
 *
 * USAGE EXAMPLE:
 * var cost = PetRules.EnergyCost(petLevel);
 * var points = PetRules.GamePoints(successes, rounds);
 * var gain = PetRules.DistributePoints(points, track);
 * var levels = PetRules.LevelFor(totalXp, thresholds);
 *
 * NOTE:
 * Sources (Fandom revisions, 2010; the Pavilion is an owner extra after the
 * 2009 cutoff, so 2010 sources are allowed for it):
 * - Training Pets, oldid 124234 (2010-12-25): five levels Baby..Epic; energy
 *   2/4/6/8 for Baby..Ancient and 8 for Epic; one talent per level, four in
 *   all; liked snack +1 Power and +1 experience, loved +2; a Baby turns pet
 *   energy into double experience, a Teen into equal experience; the best
 *   snacks are "+6 to experience" (the sum of the snack's stat changes).
 * - Pet Mini Games, oldid 122046 (2010-11-18): four games (Dance, Gobbler
 *   Drop, Mortar Mayhem cannon, Maze); a full score gives all 4 attribute
 *   points; the Dance Game is 5 rounds or 3 failures, sequences of 3, 4, ...;
 *   quitting early costs no energy.
 * - Pets, oldid 124741 (2010-12-31): a hybrid's stats are picked at 0%, 50%
 *   or 100% between the parents'.
 * - The Pet Hatchery, oldid 113079 (2010-10-11): Adult or older, once every
 *   24 hours, 20,000 to 50,000 gold.
 * Inferred (no dated number found): experience from a game equals the stat
 * points it earned; a hybrid takes after one parent at even odds; the
 * hatching price rises with the pets' combined pedigree.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Pets;

/// <summary>One stat change, as the client's PetStatModification (Strength, Agility, Intellect, Will, Power).</summary>
public sealed record PetStatChange(string Stat, int Change);

/// <summary>How a pet takes to a snack.</summary>
public enum SnackTaste {

    Normal = 0,
    Liked = 1,
    Loved = 2

}

/// <summary>What feeding one snack gives.</summary>
public sealed record SnackResult(SnackTaste Taste, int Xp, IReadOnlyList<PetStatChange> Changes);

/// <summary>The 2010 pet rules.</summary>
public static class PetRules {

    /// <summary>Baby = 1, Teen = 2, Adult = 3, Ancient = 4, Epic = 5 (Mega and Ultra came in 2011).</summary>
    public const int MaxLevel = 5;

    public const int Baby = 1, Teen = 2, Adult = 3, Ancient = 4, Epic = 5;

    /// <summary>A full score in any pet game gives this many stat points.</summary>
    public const int FullGamePoints = 4;

    /// <summary>Talents a pet can know: one per level after Baby.</summary>
    public const int MaxTalents = MaxLevel - 1;

    public static readonly string[] StatNames = ["Strength", "Intellect", "Agility", "Will", "Power"];

    private static readonly string[] s_levelNames = ["Egg", "Baby", "Teen", "Adult", "Ancient", "Epic"];

    public static string LevelName(int level) => level >= 0 && level < s_levelNames.Length ? s_levelNames[level] : level.ToString();

    /// <summary>Pet energy to play one game: 2 a level, and Epic stays at Ancient's 8.</summary>
    public static int EnergyCost(int petLevel) => 2 * Math.Clamp(petLevel, Baby, Ancient);

    /// <summary>
    /// Total experience at which a pet reaches each level. <paramref name="perLevel"/> is the client template's
    /// PetLevelInfo.m_requiredXP by level (index = level; index 1 = Baby's 125), each the experience needed to leave
    /// that level; so Teen is reached at 125, Adult at 375, Ancient at 900 and Epic at 1950.
    /// </summary>
    public static int[] Thresholds(IReadOnlyList<uint> perLevel) {
        // thresholds[L] = total XP to stand at level L (L = 1..MaxLevel).
        var thresholds = new int[MaxLevel + 1];
        var total = 0;
        for (var level = Baby; level <= MaxLevel; level++) {
            thresholds[level] = total;
            var need = level < perLevel.Count ? (int) perLevel[level] : 0;
            total += need > 0 ? need : DefaultRequired(level);
        }

        return thresholds;
    }

    /// <summary>The client's common table, for a template that carries no levels.</summary>
    private static int DefaultRequired(int level) => level switch {
        1 => 125, 2 => 250, 3 => 525, 4 => 1050, _ => 2125
    };

    /// <summary>The level a pet with this much total experience stands at, never above Epic.</summary>
    public static int LevelFor(int totalXp, int[] thresholds) {
        var level = Baby;
        for (var l = Baby + 1; l <= MaxLevel; l++) {
            if (totalXp >= thresholds[l]) {
                level = l;
            }
        }

        return level;
    }

    /// <summary>The total experience the pet needs for its next level (the bar's end), or the Epic mark at Epic.</summary>
    public static int NextLevelXp(int level, int[] thresholds) => thresholds[Math.Clamp(level + 1, Baby + 1, MaxLevel)];

    /// <summary>Experience is capped at the Epic mark: an Epic pet cannot grow further in 2010.</summary>
    public static int CapXp(int totalXp, int[] thresholds) => Math.Min(totalXp, thresholds[MaxLevel]);

    /// <summary>Stat points earned for a score: <paramref name="score"/> of <paramref name="fullScore"/>, rounded, 0..4.</summary>
    public static int GamePoints(double score, double fullScore) {
        if (fullScore <= 0 || score <= 0) {
            return 0;
        }

        return (int) Math.Clamp(Math.Round(FullGamePoints * Math.Min(score, fullScore) / fullScore, MidpointRounding.AwayFromZero),
            0, FullGamePoints);
    }

    /// <summary>
    /// Experience for a played game: the stat points it earned, doubled for a Baby (Training Pets: a Baby turns its
    /// 2 energy into twice the experience, a Teen its 4 into the same, older pets gain slowly).
    /// </summary>
    public static int GameXp(int points, int petLevel) => petLevel <= Baby ? points * 2 : points;

    /// <summary>
    /// Shares <paramref name="points"/> out over a track's stat changes (which add up to 4 at a full score): every stat
    /// gets its share, rounded down, and what is left goes to the track's biggest stats first.
    /// </summary>
    public static IReadOnlyList<PetStatChange> DistributePoints(int points, IReadOnlyList<PetStatChange> track) {
        var full = track.Where(c => c.Change > 0).ToList();
        var total = full.Sum(c => c.Change);
        if (points <= 0 || total <= 0) {
            return [];
        }

        if (points >= total) {
            return full;
        }

        var shares = full.Select(c => (c.Stat, Whole: c.Change * points / total, Remainder: c.Change * points % total, c.Change)).ToList();
        var left = points - shares.Sum(s => s.Whole);
        var order = shares.Select((s, i) => (s, i)).OrderByDescending(x => x.s.Remainder).ThenByDescending(x => x.s.Change).ThenBy(x => x.i)
            .Select(x => x.i).ToList();
        var result = shares.Select(s => s.Whole).ToArray();
        foreach (var i in order) {
            if (left <= 0) {
                break;
            }

            result[i]++;
            left--;
        }

        return [.. shares.Select((s, i) => new PetStatChange(s.Stat, result[i])).Where(c => c.Change > 0)];
    }

    /// <summary>
    /// How a pet takes to a snack: it likes snacks of one of its favourite kinds (the template's
    /// m_favoriteSnackCategories against the snack's adjectives) or of its own school, and loves a favourite kind
    /// that is also of its school.
    /// </summary>
    public static SnackTaste Taste(IEnumerable<string> favouriteKinds, string petSchool, IEnumerable<string> snackAdjectives, string snackSchool) {
        var kinds = new HashSet<string>(favouriteKinds ?? [], StringComparer.OrdinalIgnoreCase);
        var kindMatch = (snackAdjectives ?? []).Any(kinds.Contains);
        var schoolMatch = !string.IsNullOrEmpty(petSchool) && string.Equals(petSchool, snackSchool, StringComparison.OrdinalIgnoreCase);
        return kindMatch && schoolMatch ? SnackTaste.Loved : kindMatch || schoolMatch ? SnackTaste.Liked : SnackTaste.Normal;
    }

    /// <summary>A snack's experience is the sum of its stat changes; a liked snack adds 1 Power and 1 experience, a loved one 2.</summary>
    public static SnackResult Feed(IReadOnlyList<PetStatChange> snackChanges, SnackTaste taste) {
        var changes = snackChanges.Where(c => c.Change != 0).ToList();
        var xp = changes.Sum(c => c.Change) + (int) taste;
        if (taste != SnackTaste.Normal) {
            var power = changes.FindIndex(c => string.Equals(c.Stat, "Power", StringComparison.OrdinalIgnoreCase));
            if (power >= 0) {
                changes[power] = changes[power] with { Change = changes[power].Change + (int) taste };
            }
            else {
                changes.Add(new PetStatChange("Power", (int) taste));
            }
        }

        return new SnackResult(taste, Math.Max(0, xp), changes);
    }

    /// <summary>Adds changes to current stats without passing the pet's maximums; returns the changes actually made.</summary>
    public static IReadOnlyList<PetStatChange> ApplyStats(IDictionary<string, int> current, IReadOnlyDictionary<string, int> max,
            IEnumerable<PetStatChange> changes) {
        var applied = new List<PetStatChange>();
        foreach (var change in changes) {
            current.TryGetValue(change.Stat, out var now);
            var cap = max.TryGetValue(change.Stat, out var m) && m > 0 ? m : int.MaxValue;
            var next = Math.Clamp(now + change.Change, 0, Math.Max(now, cap));
            if (next != now) {
                current[change.Stat] = next;
                applied.Add(new PetStatChange(change.Stat, next - now));
            }
        }

        return applied;
    }

    /// <summary>
    /// Talents a 2010 pet may learn from its template's pool: the client's pools carry later talents too (fishing,
    /// gardening and pet powers came in 2011-2013), and those are left out.
    /// </summary>
    public static bool IsTalentIn2010(string talentName) {
        if (string.IsNullOrWhiteSpace(talentName)) {
            return false;
        }

        string[] later = ["Talent-Fishing", "Talent-Gardening", "Talent-Power-", "-Locked", "Talent-Jewel"];
        return !later.Any(p => talentName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The talent a pet learns on a level up: a random one from its pool it does not know yet, or none.</summary>
    public static string PickTalent(IReadOnlyList<string> pool, IReadOnlyCollection<string> known, Random random) {
        var open = pool.Where(IsTalentIn2010).Where(t => !known.Contains(t)).Distinct().ToList();
        return open.Count == 0 ? null : open[random.Next(open.Count)];
    }

}
