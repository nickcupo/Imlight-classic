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
 * CLASSIC RULES PROFILE
 * ========================================================================
 * 
 * PURPOSE:
 * The immutable, fully merged rules profile the server enforces: cutoff,
 * level cap, world allowlist, feature switches and rule table references.
 * 
 * USAGE EXAMPLE:
 * var profile = ClassicProfileLoader.Load(profilesDir, "late-2009");
 * if (profile.LevelCap is int cap) { ... }
 * 
 * NOTE:
 * A null cutoff, level cap or world list means "no restriction". A feature
 * switch the profile does not set is enabled, which is stock Imlight.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Imlight.Classic.Travel;

namespace Imlight.Classic;

/// <summary>
/// The status a profile declares.
/// </summary>
public enum ProfileStatus {
    Canonical,
    Optional,
    Debug,
}

/// <summary>
/// The feature switches a profile sets explicitly. Switches it does not set are enabled.
/// </summary>
public sealed class FeatureSwitches {

    private readonly FrozenDictionary<string, bool> _explicit;

    /// <summary>
    /// Creates the switch set from explicit values keyed by feature path.
    /// </summary>
    /// <param name="explicitValues">Explicit switch values, keyed by <see cref="ClassicFeatures"/> paths.</param>
    /// <exception cref="ArgumentException">A key is not a known feature path.</exception>
    public FeatureSwitches(IReadOnlyDictionary<string, bool> explicitValues) {
        foreach (var path in explicitValues.Keys) {
            if (!ClassicFeatures.IsKnown(path)) {
                throw new ArgumentException($"unknown feature path '{path}'", nameof(explicitValues));
            }
        }

        _explicit = explicitValues.ToFrozenDictionary(StringComparer.Ordinal);
        AllEnabled = !_explicit.Values.Contains(false);
    }

    /// <summary>
    /// A switch set with nothing set, so every feature is enabled.
    /// </summary>
    public static FeatureSwitches None { get; } = new(new Dictionary<string, bool>());

    /// <summary>
    /// The switches the profile sets, keyed by feature path.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Explicit => _explicit;

    /// <summary>
    /// True when no switch is off.
    /// </summary>
    public bool AllEnabled { get; }

    /// <summary>
    /// The paths that are switched off, in schema order.
    /// </summary>
    public ImmutableArray<string> Disabled
        => [.. ClassicFeatures.All.Where(path => _explicit.TryGetValue(path, out var on) && !on)];

    /// <summary>
    /// Returns the switch value for <paramref name="path"/>, or true when the profile does not set it.
    /// </summary>
    /// <param name="path">A <see cref="ClassicFeatures"/> path.</param>
    /// <returns>True if the feature is enabled.</returns>
    /// <exception cref="ArgumentException">The path is not a known feature path.</exception>
    public bool IsEnabled(string path) {
        if (!ClassicFeatures.IsKnown(path)) {
            throw new ArgumentException($"unknown feature path '{path}'", nameof(path));
        }

        return !_explicit.TryGetValue(path, out var on) || on;
    }

}

/// <summary>
/// The rule table references and enums under a profile's <c>rules</c> block.
/// </summary>
public sealed class ProfileRules {

    /// <summary>
    /// An empty rules block.
    /// </summary>
    public static ProfileRules None { get; } = new();

    /// <summary>
    /// Path under classic-data of the accuracy table, if any.
    /// </summary>
    public string? AccuracyTable { get; init; }

    /// <summary>
    /// Path under classic-data of the XP table, if any.
    /// </summary>
    public string? XpTable { get; init; }

    /// <summary>
    /// Path under classic-data of the base health per level and school, if any. CLASSIC.
    /// </summary>
    public string? PlayerHealth { get; init; }

    /// <summary>
    /// Path under classic-data of the mob reward rules (combat XP, gold, drops), if any.
    /// </summary>
    public string? MobRewards { get; init; }

    /// <summary>
    /// Path under classic-data of the badges and how each is awarded, if any.
    /// </summary>
    public string? Badges { get; init; }

    /// <summary>
    /// Path under classic-data of the treasure cards quests give on completion, if any.
    /// </summary>
    public string? QuestCards { get; init; }

    /// <summary>
    /// Path under classic-data of what a library charged for each treasure card, if any.
    /// </summary>
    public string? TreasurePrices { get; init; }

    /// <summary>
    /// Path under classic-data of creature health at the cutoff, if any.
    /// </summary>
    public string? MobStats { get; init; }

    /// <summary>
    /// Path under classic-data of the Crown Shop catalog at the cutoff, if any.
    /// </summary>
    public string? CrownShop { get; init; }

    /// <summary>
    /// Path under classic-data of the zone objects the server does not spawn because they belong to later versions, if any.
    /// </summary>
    public string? LaterObjects { get; init; }

    /// <summary>
    /// The creature-deck file (such as creatures/creature-decks-2009.yaml): each listed creature template casts its own spells. CLASSIC.
    /// </summary>
    public string? CreatureDecks { get; init; }

    /// <summary>
    /// Path under classic-data of the potion flask rules (minigame fill, Hilda Brewer's prices), if any. CLASSIC.
    /// </summary>
    public string? Potions { get; init; }

    /// <summary>
    /// Path under classic-data of the Second Chance chests (October 2009), if any; null in a profile that switches them off. CLASSIC.
    /// </summary>
    public string? SecondChance { get; init; }

    /// <summary>
    /// Path under classic-data of the scripted boss cheats (Briskbreeze Tower, October 2009), if any; null in a profile
    /// that switches them off. CLASSIC.
    /// </summary>
    public string? BossCheats { get; init; }

    /// <summary>
    /// The rank from which power pips appear; null when unset or set to null.
    /// </summary>
    public string? PowerPipsFromRank { get; init; }

    /// <summary>
    /// The Dragonspyre difficulty era, if set.
    /// </summary>
    public string? DragonspyreDifficulty { get; init; }

    /// <summary>
    /// The tutorial variant, if set.
    /// </summary>
    public string? Tutorial { get; init; }

    /// <summary>
    /// How teleport stones are discovered (<c>discover</c>: by reaching the far stone, as in 2009; <c>open</c>: on
    /// arriving in the zone, as the r806919 client data has it), if set.
    /// </summary>
    public string? TeleportStones { get; init; }

}

/// <summary>
/// A fully merged classic rules profile.
/// </summary>
public sealed class ClassicProfile {

    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required ProfileStatus Status { get; init; }
    public string? Extends { get; init; }

    /// <summary>
    /// The last in-game day the profile represents; null means no cutoff.
    /// </summary>
    public DateOnly? Cutoff { get; init; }

    /// <summary>
    /// The highest level a wizard may reach; null means no cap.
    /// </summary>
    public int? LevelCap { get; init; }

    /// <summary>
    /// The allowed world ids in profile order; null means every world.
    /// </summary>
    public ImmutableArray<string>? Worlds { get; init; }

    public required FeatureSwitches Features { get; init; }
    public required ProfileRules Rules { get; init; }

    /// <summary>
    /// The quest or level that opens each world at the Spiral Door, keyed by world id. A world without a rule
    /// is open to every wizard; an empty map means no world is locked.
    /// </summary>
    public ImmutableDictionary<string, WorldUnlock> WorldUnlocks { get; init; }
        = ImmutableDictionary<string, WorldUnlock>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableArray<string> Notes { get; init; } = [];

    /// <summary>
    /// CLASSIC: quests the profile removes once every SpiralDB overlay has loaded, as a quest tombstone would:
    /// content of a later update in a profile whose cutoff predates it (<c>disabled_quests</c>).
    /// </summary>
    public ImmutableArray<string> DisabledQuests { get; init; } = [];

    /// <summary>
    /// The files the profile was merged from, child first.
    /// </summary>
    public required ImmutableArray<string> SourceFiles { get; init; }

    /// <summary>
    /// The profile ids of the extends chain, child first, such as <c>[arc1-2009h1, late-2009]</c>.
    /// </summary>
    public ImmutableArray<string> Lineage
        => SourceFiles.IsEmpty ? [Id] : [.. SourceFiles.Select(file => Path.GetFileNameWithoutExtension(file))];

    /// <summary>
    /// True when the profile restricts nothing, so it must behave exactly like stock Imlight.
    /// </summary>
    public bool IsUnrestricted => Cutoff is null && LevelCap is null && Worlds is null && Features.AllEnabled;

    /// <summary>
    /// True when <paramref name="worldId"/> is allowed by the world list.
    /// </summary>
    /// <param name="worldId">A world id.</param>
    /// <returns>True if the profile has no world list or lists the world.</returns>
    public bool AllowsWorld(string worldId)
        => Worlds is null || Worlds.Value.Contains(worldId, StringComparer.Ordinal);

    /// <summary>
    /// A one-line summary for the startup log.
    /// </summary>
    /// <returns>The id, status, cutoff, cap, worlds and the features that are off.</returns>
    public string Describe() {
        var cutoff = Cutoff?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none";
        var cap = LevelCap?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var worlds = Worlds is null ? "all" : string.Join(", ", Worlds.Value);
        var disabled = Features.Disabled;
        var off = disabled.IsEmpty ? "none" : string.Join(", ", disabled);
        var status = Status.ToString().ToLowerInvariant();

        return $"{Id} ({status}): cutoff {cutoff}, level cap {cap}, worlds {worlds}; features off: {off}";
    }

}
