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
 * QUEST BUDDIES (WHO IS ON MY QUEST?)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner request 2026-10-09, not a 2010 feature): friends questing
 * side by side see, without typing anything, whether a friend is on the same
 * quest, further along or behind in the same quest chain, or on a different
 * quest. This file holds the pure decisions:
 *   - Compare: the relation between two wizards' active quests over the chain
 *     graph (quest B follows quest A when B asks for A's "Complete" entry and
 *     both are story quests or share a chain name, e.g. WC-UNICORN-MAIN);
 *   - Signature: what a notice is about, so the same news is not repeated;
 *   - Text: the chat line the viewer reads (a whisper from the friend).
 *
 * USAGE EXAMPLE:
 * var relation = QuestBuddies.Compare(mine, theirs, next, isMainline);
 * var line = QuestBuddies.Text(relation, title, zone);
 *
 * NOTE:
 * The wiring (who is a friend, who is online, sending the whisper) lives in
 * Imlight.CoreLib.Classic.QuestBuddyNotices; the switch is
 * [Classic] QuestBuddyNotices (default on).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Imlight.Classic.Quests;

/// <summary>How a friend's quests relate to the viewer's.</summary>
public enum QuestRelationKind {

    /// <summary>No shared quest and no quest of the same chain.</summary>
    Different,

    /// <summary>Both hold the same quest.</summary>
    SameQuest,

    /// <summary>A quest of the friend's follows (or leads to) one of the viewer's in the same chain.</summary>
    SameChain,

}

/// <summary>
/// The relation of a friend's quests to the viewer's: <paramref name="Quest"/> is the shared quest (SameQuest) or the
/// friend's quest the relation is about; <paramref name="Steps"/> is how many quests the friend is ahead (positive) or
/// behind (negative) in the chain, 0 otherwise.
/// </summary>
public sealed record QuestRelation(QuestRelationKind Kind, string? Quest, int Steps);

/// <summary>CLASSIC: the decisions of the quest buddy notices (see the file header).</summary>
public static partial class QuestBuddies {

    /// <summary>How far along a chain a relation is still looked for.</summary>
    public const int MaxSteps = 40;

    /// <summary>The tag every notice starts with, so a player can tell it from a typed whisper.</summary>
    public const string Tag = "[Quest]";

    /// <summary>
    /// The chain a quest name belongs to: the name without its trailing step number and variant
    /// ("WC-UNICORN-MAIN-007" and "WC-COMMONS-MAIN-002-FIRE" give "WC-UNICORN-MAIN" and "WC-COMMONS-MAIN"); null when
    /// the name has no step number.
    /// </summary>
    public static string? ChainKey(string? questName) {
        if (string.IsNullOrWhiteSpace(questName)) {
            return null;
        }

        var match = StepRegex().Match(questName);

        return match.Success ? match.Groups["chain"].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// True when <paramref name="next"/> asking for <paramref name="prereq"/>'s completion makes it the next step of the
    /// same chain: both are story (mainline) quests, or both carry the same chain name.
    /// </summary>
    public static bool SameChain(string prereq, bool prereqMainline, string next, bool nextMainline)
        => (prereqMainline && nextMainline)
           || (ChainKey(prereq) is { } a && string.Equals(a, ChainKey(next), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The relation of <paramref name="theirs"/> (a friend's active quests) to <paramref name="mine"/>: the same quest
    /// first (a story quest before a side quest), else the nearest quest of a shared chain (ahead before behind), else
    /// different, about the friend's main quest (<see cref="MainQuest"/>).
    /// </summary>
    /// <param name="mine">The viewer's active quest names.</param>
    /// <param name="theirs">The friend's active quest names.</param>
    /// <param name="next">The quests that directly follow a quest in its chain.</param>
    /// <param name="isMainline">Whether a quest is a story quest.</param>
    public static QuestRelation Compare(IReadOnlyCollection<string> mine, IReadOnlyCollection<string> theirs,
        Func<string, IEnumerable<string>> next, Func<string, bool> isMainline) {
        var mineSet = new HashSet<string>(mine.Where(q => !string.IsNullOrEmpty(q)), StringComparer.OrdinalIgnoreCase);
        var theirSet = new HashSet<string>(theirs.Where(q => !string.IsNullOrEmpty(q)), StringComparer.OrdinalIgnoreCase);

        var shared = theirs.Where(mineSet.Contains).OrderByDescending(isMainline).ThenBy(q => q, StringComparer.Ordinal)
            .FirstOrDefault();
        if (shared is not null) {
            return new QuestRelation(QuestRelationKind.SameQuest, shared, 0);
        }

        var ahead = Nearest(mineSet, theirSet, next);
        var behind = Nearest(theirSet, mineSet, next);
        if (ahead is { } a && (behind is not { } b0 || a.Steps <= b0.Steps)) {
            return new QuestRelation(QuestRelationKind.SameChain, a.Target, a.Steps);
        }

        if (behind is { } b) {
            return new QuestRelation(QuestRelationKind.SameChain, b.Source, -b.Steps);
        }

        return new QuestRelation(QuestRelationKind.Different, MainQuest(theirs, isMainline), 0);
    }

    /// <summary>The quest a wizard is mainly on: the first story quest, else the first quest; null when none.</summary>
    public static string? MainQuest(IEnumerable<string> quests, Func<string, bool> isMainline) {
        var list = quests.Where(q => !string.IsNullOrEmpty(q)).ToList();

        return list.FirstOrDefault(isMainline) ?? list.FirstOrDefault();
    }

    // The fewest chain steps from any quest of `from` forward to any quest of `to`, with the start and end quests.
    private static (string Source, string Target, int Steps)? Nearest(HashSet<string> from, HashSet<string> to,
        Func<string, IEnumerable<string>> next) {
        (string Source, string Target, int Steps)? best = null;
        foreach (var start in from.OrderBy(q => q, StringComparer.Ordinal)) {
            var limit = best?.Steps - 1 ?? MaxSteps;
            if (Reach(start, to, next, limit) is { } hit) {
                best = (start, hit.Target, hit.Steps);
            }
        }

        return best;
    }

    // Breadth-first along the chain from `start`: the first quest of `to` within `limit` steps.
    private static (string Target, int Steps)? Reach(string start, HashSet<string> to,
        Func<string, IEnumerable<string>> next, int limit) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var frontier = new List<string> { start };
        for (var depth = 1; depth <= limit && frontier.Count > 0; depth++) {
            var following = new List<string>();
            foreach (var quest in frontier) {
                foreach (var after in next(quest) ?? []) {
                    if (string.IsNullOrEmpty(after) || !seen.Add(after)) {
                        continue;
                    }

                    if (to.Contains(after)) {
                        return (after, depth);
                    }

                    following.Add(after);
                }
            }

            frontier = following;
        }

        return null;
    }

    /// <summary>
    /// What a notice is about. The same signature is not repeated for a quest change; a change of quest, of chain
    /// direction or of kind is news.
    /// </summary>
    public static string Signature(QuestRelation relation) => relation.Kind switch {
        QuestRelationKind.SameQuest => $"same:{relation.Quest}",
        QuestRelationKind.SameChain => relation.Steps > 0 ? "chain:ahead" : "chain:behind",
        _ => $"different:{relation.Quest}",
    };

    /// <summary>
    /// The line the viewer reads, as a whisper from the friend: <paramref name="questTitle"/> is the relation's quest as
    /// players see it, <paramref name="zone"/> where the friend is (null when the friend is right here).
    /// </summary>
    public static string Text(QuestRelation relation, string? questTitle, string? zone) {
        var title = string.IsNullOrWhiteSpace(questTitle) ? "" : $": {questTitle.Trim()}";
        var steps = Math.Abs(relation.Steps);
        var quests = steps == 1 ? "quest" : "quests";
        var core = relation.Kind switch {
            QuestRelationKind.SameQuest => $"Same quest as you{title}",
            QuestRelationKind.SameChain when relation.Steps > 0 => $"Same quest chain, {steps} {quests} ahead of you{title}",
            QuestRelationKind.SameChain => $"Same quest chain, {steps} {quests} behind you{title}",
            _ when relation.Quest is null => "No quest right now",
            _ => $"Different quest{title}",
        };
        var line = string.IsNullOrWhiteSpace(zone) ? core : $"{core} (in {zone.Trim()})";

        return $"{Tag} {line}{(line.EndsWith('.') || line.EndsWith('!') || line.EndsWith('?') ? "" : ".")}";
    }

    [GeneratedRegex(@"^(?<chain>.+?)-(?<step>\d{2,4})[A-Za-z]?(?:-[A-Za-z]+)?$")]
    private static partial Regex StepRegex();

}
