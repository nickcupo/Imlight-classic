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
 * CLASSIC SPELL VALUES
 * ========================================================================
 *
 * PURPOSE:
 * The numbers of one spell card in one profile: pip cost, accuracy,
 * training data and the typed effect list of classic-data/spells records.
 *
 * USAGE EXAMPLE:
 * var values = record.ValuesFor(profile.Lineage);
 * if (values.Pips.Fixed is int pips) { ... }
 *
 * NOTE:
 * Effect amounts are totals for effects over time and per pip on X cards,
 * as classic-data/spells/README.md defines them.
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

namespace Imlight.Classic.Spells;

/// <summary>
/// The effect vocabulary of spell.schema.json, in schema order.
/// </summary>
public enum SpellEffectKind {
    Damage,
    Dot,
    Heal,
    Hot,
    Steal,
    Pip,
    Minion,
    Beguile,
    Stun,
    Threat,
    Blade,
    Charm,
    Enchant,
    Trap,
    Shield,
    Ward,
    Prism,
    Global,
    Mutate,
    Dispel,
    RemoveCharm,
    RemoveWard,
    Reshuffle,
    Cloak,
    StunResist,
    CriticalBlock,
}

/// <summary>
/// Who an effect lands on, as spell.schema.json names it.
/// </summary>
public enum SpellTargets {
    Self,
    Single,
    AllEnemies,
    AllAllies,
}

/// <summary>
/// A pip cost: a fixed number, or X (every pip the caster has).
/// </summary>
public readonly record struct SpellPips {

    private SpellPips(int? fixedCost) {
        Fixed = fixedCost;
    }

    /// <summary>
    /// The X cost.
    /// </summary>
    public static SpellPips X { get; } = new(null);

    /// <summary>
    /// The fixed cost, or null for X.
    /// </summary>
    public int? Fixed { get; }

    public bool IsX => Fixed is null;

    public static SpellPips Of(int cost) => new(cost);

    public override string ToString() => Fixed?.ToString(CultureInfo.InvariantCulture) ?? "X";

}

/// <summary>
/// One entry of a record's effect list.
/// </summary>
/// <param name="Kind">The effect type.</param>
/// <param name="School">The effect's school, or <c>all</c>.</param>
/// <param name="Min">The smallest amount, if the effect has one.</param>
/// <param name="Max">The largest amount; set whenever <paramref name="Min"/> is.</param>
/// <param name="Percent">The signed percentage of blades, charms, traps, shields, globals and drains.</param>
/// <param name="Rounds">The duration in rounds, if any.</param>
/// <param name="Targets">Who the effect lands on; null for globals and some utility effects.</param>
/// <param name="Notes">What the fields cannot say.</param>
public sealed record SpellEffectValues(SpellEffectKind Kind, string School, int? Min, int? Max, int? Percent, int? Rounds,
                                       SpellTargets? Targets, string? Notes) {

    /// <summary>
    /// CLASSIC: explicit equally selectable damage outcomes, in client replay order. Empty means the ordinary min/max range.
    /// </summary>
    public ImmutableArray<int> Outcomes { get; init; } = [];

    /// <summary>
    /// True when the effect carries an amount.
    /// </summary>
    public bool HasAmount => Min is not null && Max is not null;

}

/// <summary>
/// The numbers of a spell card in one profile.
/// </summary>
/// <param name="Pips">The pip cost.</param>
/// <param name="Accuracy">The accuracy as a fraction (0.75 = 75%).</param>
/// <param name="LevelLearned">The level the card is learned at; null when it is not learned or unknown.</param>
/// <param name="TrainingPoints">The per-spell training point cost; null when not trainable or unknown.</param>
/// <param name="Trainer">The trainer; null when not learned or unknown.</param>
/// <param name="Effects">The effects, in card order.</param>
public sealed record SpellValues(SpellPips Pips, double Accuracy, int? LevelLearned, int? TrainingPoints, string? Trainer,
                                 ImmutableArray<SpellEffectValues> Effects) {

    /// <summary>
    /// The accuracy as the whole percentage a spell template stores.
    /// </summary>
    public int AccuracyPercent => (int) Math.Round(Accuracy * 100, MidpointRounding.AwayFromZero);

}

/// <summary>
/// A <c>profile_values</c> entry: only the fields that differ in that profile.
/// </summary>
public sealed class SpellValuesOverride {

    public SpellPips? Pips { get; init; }
    public double? Accuracy { get; init; }
    public bool SetsLevelLearned { get; init; }
    public int? LevelLearned { get; init; }
    public bool SetsTrainingPoints { get; init; }
    public int? TrainingPoints { get; init; }
    public bool SetsTrainer { get; init; }
    public string? Trainer { get; init; }

    /// <summary>
    /// The replacement effect list; when set it replaces the whole list.
    /// </summary>
    public ImmutableArray<SpellEffectValues>? Effects { get; init; }

    /// <summary>
    /// Applies the fields this entry names over <paramref name="values"/>.
    /// </summary>
    /// <param name="values">The values to override.</param>
    /// <returns>The overridden values.</returns>
    public SpellValues ApplyTo(SpellValues values)
        => values with {
            Pips = Pips ?? values.Pips,
            Accuracy = Accuracy ?? values.Accuracy,
            LevelLearned = SetsLevelLearned ? LevelLearned : values.LevelLearned,
            TrainingPoints = SetsTrainingPoints ? TrainingPoints : values.TrainingPoints,
            Trainer = SetsTrainer ? Trainer : values.Trainer,
            Effects = Effects ?? values.Effects,
        };

}
