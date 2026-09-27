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
 * CLASSIC RULES SCHEMA
 * ========================================================================
 * 
 * PURPOSE:
 * The feature switch paths a profile's features block may set, as constants
 * so hook sites never spell a feature name by hand.
 * 
 * USAGE EXAMPLE:
 * ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsEnergy)
 * 
 * NOTE:
 * Nested switches use dotted paths (pets.energy). SchemaDriftTests pins All
 * to profile.schema.json and to the featurePath enum of zones.schema.json.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Imlight.Classic;

/// <summary>
/// Feature switch paths of the classic profile schema.
/// </summary>
public static class ClassicFeatures {

    public const string TrainingPoints = "training_points";
    public const string TreasureCards = "treasure_cards";
    public const string Housing = "housing";
    public const string PvpArena = "pvp_arena";
    public const string Crafting = "crafting";
    public const string Bazaar = "bazaar";
    public const string Mounts = "mounts";
    public const string Henchmen = "henchmen";
    public const string Elixirs = "elixirs";
    public const string Seamstress = "seamstress";
    public const string HubTeleporters = "hub_teleporters";
    public const string PetsEnabled = "pets.enabled";
    public const string PetsLeveling = "pets.leveling";
    public const string PetsTalents = "pets.talents";
    public const string PetsHatching = "pets.hatching";
    public const string PetsEnergy = "pets.energy";
    public const string CriticalAndBlock = "critical_and_block";
    public const string ArmorPiercing = "armor_piercing";
    public const string ShadowMagic = "shadow_magic";
    public const string Archmastery = "archmastery";
    public const string Gardening = "gardening";
    public const string Fishing = "fishing";
    public const string Jewels = "jewels";
    public const string Cantrips = "cantrips";
    public const string Monstrology = "monstrology";
    public const string TeamUp = "team_up";

    /// <summary>
    /// The name of the nested switch group; its members are the <c>pets.*</c> paths.
    /// </summary>
    internal const string PetsGroup = "pets";

    /// <summary>
    /// Every feature path, in schema order.
    /// </summary>
    public static ImmutableArray<string> All { get; } = [
        TrainingPoints, TreasureCards, Housing, PvpArena, Crafting, Bazaar, Mounts, Henchmen,
        Elixirs, Seamstress, HubTeleporters, PetsEnabled, PetsLeveling, PetsTalents, PetsHatching,
        PetsEnergy, CriticalAndBlock, ArmorPiercing, ShadowMagic, Archmastery, Gardening, Fishing,
        Jewels, Cantrips, Monstrology, TeamUp
    ];

    private static readonly FrozenSet<string> s_known = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="path"/> is one of <see cref="All"/>.
    /// </summary>
    /// <param name="path">A feature path such as <c>pets.energy</c>.</param>
    /// <returns>True if the schema defines the path.</returns>
    public static bool IsKnown(string? path)
        => path is not null && s_known.Contains(path);

}
