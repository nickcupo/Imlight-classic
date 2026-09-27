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
 * CLASSIC WORLD UNLOCKS
 * ========================================================================
 *
 * PURPOSE:
 * The profile's world_unlocks rule: which quest (or level) opens each world
 * at the Spiral Door. A world with no rule is always open to every wizard
 * the profile lets in.
 *
 * USAGE EXAMPLE:
 * if (!ClassicRuntime.Rules.IsWorldUnlocked("Krokotopia", progress).Unlocked) { ... }
 *
 * NOTE:
 * A rule is a list of checks; the world opens when any one passes. "quest"
 * passes while the quest is active or once it is complete (as ReqHasQuest
 * reads it), "quest_complete" only once it is complete, "entry" when the
 * registry entry is set, "level" at that level or higher.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace Imlight.Classic.Travel;

/// <summary>
/// What a world-unlock check reads about a wizard.
/// </summary>
public interface IPlayerProgress {

    /// <summary>True while the quest is in the wizard's journal.</summary>
    bool HasActiveQuest(string questName);

    /// <summary>True once the wizard has completed the quest.</summary>
    bool HasCompletedQuest(string questName);

    /// <summary>True when the wizard's quest registry holds the entry.</summary>
    bool HasEntry(string entryName);

    /// <summary>The wizard's level.</summary>
    int Level { get; }

}

/// <summary>
/// The kinds of world-unlock checks.
/// </summary>
public enum UnlockCheckKind {
    Quest,
    QuestComplete,
    Entry,
    Level,
}

/// <summary>
/// One check of a world-unlock rule.
/// </summary>
/// <param name="Kind">What it checks.</param>
/// <param name="Name">The quest or entry name; null for a level check.</param>
/// <param name="MinLevel">The level for a level check; 0 otherwise.</param>
public sealed record UnlockCheck(UnlockCheckKind Kind, string? Name, int MinLevel) {

    /// <summary>The profile YAML keys of the check kinds, in <see cref="UnlockCheckKind"/> order.</summary>
    public static ImmutableArray<string> Keys { get; } = ["quest", "quest_complete", "entry", "level"];

    /// <summary>
    /// True when <paramref name="progress"/> passes the check.
    /// </summary>
    public bool IsMet(IPlayerProgress progress) {
        ArgumentNullException.ThrowIfNull(progress);

        return Kind switch {
            UnlockCheckKind.Quest => progress.HasActiveQuest(Name!) || progress.HasCompletedQuest(Name!),
            UnlockCheckKind.QuestComplete => progress.HasCompletedQuest(Name!),
            UnlockCheckKind.Entry => progress.HasEntry(Name!),
            UnlockCheckKind.Level => progress.Level >= MinLevel,
            _ => false,
        };
    }

    /// <summary>
    /// A short text for logs and refusals, such as <c>quest WC-MAIN-C01-010</c> or <c>level 20</c>.
    /// </summary>
    public string Describe()
        => Kind == UnlockCheckKind.Level
            ? "level " + MinLevel.ToString(CultureInfo.InvariantCulture)
            : $"{Keys[(int) Kind].Replace('_', ' ')} {Name}";

}

/// <summary>
/// The rule that opens one world at the Spiral Door.
/// </summary>
/// <param name="WorldId">The world id.</param>
/// <param name="AnyOf">The checks; the world opens when any one passes.</param>
/// <param name="Source">Where the rule comes from, for the audit.</param>
public sealed record WorldUnlock(string WorldId, ImmutableArray<UnlockCheck> AnyOf, string? Source) {

    /// <summary>
    /// True when any check passes.
    /// </summary>
    public bool IsMet(IPlayerProgress progress)
        => AnyOf.Any(check => check.IsMet(progress));

    /// <summary>
    /// The checks joined with "or".
    /// </summary>
    public string Describe()
        => string.Join(" or ", AnyOf.Select(check => check.Describe()));

}

/// <summary>
/// A Spiral Door decision for one world and one wizard.
/// </summary>
/// <param name="Unlocked">True when the door may list and send the wizard to the world.</param>
/// <param name="WorldId">The world, when the hub key names one.</param>
/// <param name="Rule">The rule that decided, when there is one.</param>
public readonly record struct WorldUnlockDecision(bool Unlocked, string? WorldId, WorldUnlock? Rule) {

    /// <summary>
    /// The reason for the audit log.
    /// </summary>
    public string Reason
        => Rule is null
            ? $"world {WorldId ?? "?"} has no unlock rule"
            : $"world {WorldId} opens with {Rule.Describe()}: {(Unlocked ? "met" : "not met")}";

}
