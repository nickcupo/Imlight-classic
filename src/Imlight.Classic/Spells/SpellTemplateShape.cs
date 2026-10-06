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
 * A server-neutral picture of a client spell template: its pip cost,
 * accuracy and effect tree, and the changes a plan makes to it. The server
 * builds the shape from its generated SpellTemplate and applies the changes
 * back, so the planning itself stays testable without the client types.
 *
 * USAGE EXAMPLE:
 * var shape = new SpellTemplateShape { Path = path, Name = "Fire Cat", Rank = 1, Accuracy = 75, Effects = [...] };
 * var plan = overrides.PlanFor(shape);
 *
 * NOTE:
 * An address is the effect's index in the template's effect list, and the
 * child index inside a random or per-pip effect (-1 for the effect itself).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Immutable;

namespace Imlight.Classic.Spells;

/// <summary>
/// The template effect types the classic values can override. Anything else is <see cref="Other"/>.
/// </summary>
public enum TemplateEffectKind {
    Other,
    Damage,
    DamageOverTime,
    Heal,
    HealOverTime,
    StealHealth,
    ModifyOutgoingDamage,
    ModifyIncomingDamage,
    ModifyAccuracy,
    ModifyOutgoingHeal,
    ModifyIncomingHeal,
    AbsorbDamage,
    ModifyPips,
    MaxHealthDamage,
    StunResist,
    CriticalBlock,
}

/// <summary>
/// Who a template effect lands on. Minion targets count as single targets.
/// </summary>
public enum TemplateTarget {
    Other,
    EnemySingle,
    FriendlySingle,
    MinionSingle,
    Self,
    EnemyTeam,
    FriendlyTeam,
    Global,
}

/// <summary>
/// How a template effect holds its values.
/// </summary>
public enum TemplateComposition {

    /// <summary>
    /// One effect with one value.
    /// </summary>
    Plain,

    /// <summary>
    /// One of the children is rolled per cast (a damage range).
    /// </summary>
    Random,

    /// <summary>
    /// One child per pip paid, for X cards.
    /// </summary>
    PerPip,

    /// <summary>
    /// A structure the classic values do not reach (effect lists, conditions).
    /// </summary>
    Other,

}

/// <summary>
/// One effect of a template, with its children for random and per-pip effects.
/// </summary>
public sealed record TemplateEffectNode {

    public TemplateComposition Composition { get; init; } = TemplateComposition.Plain;
    public TemplateEffectKind Kind { get; init; } = TemplateEffectKind.Other;
    public TemplateTarget Target { get; init; } = TemplateTarget.Other;

    /// <summary>
    /// The school the effect deals or guards against, such as <c>Fire</c> or <c>All</c>.
    /// </summary>
    public string DamageType { get; init; } = "";

    public int Param { get; init; }
    public int Rounds { get; init; }

    /// <summary>
    /// The pip tier of a per-pip child, 1-based; 0 when unset.
    /// </summary>
    public int PipNumber { get; init; }

    public float HealModifier { get; init; }
    public ImmutableArray<TemplateEffectNode> Children { get; init; } = [];

    /// <summary>
    /// The client's kSpellEffects member name, such as <c>kStun</c>, including types <see cref="Kind"/> calls <see cref="TemplateEffectKind.Other"/>.
    /// </summary>
    public string EffectType { get; init; } = "";

    /// <summary>
    /// The client's kEffectTarget member name, such as <c>kEnemySingle</c>.
    /// </summary>
    public string TargetName { get; init; } = "";

    /// <summary>
    /// The effect's class name, such as <c>SpellEffect</c> or <c>ConditionalSpellEffect</c>.
    /// </summary>
    public string ClassName { get; init; } = "";

    /// <summary>
    /// <see cref="EffectType"/>, or the member <see cref="Kind"/> names when the node was built without one.
    /// </summary>
    public string EffectTypeName
        => EffectType.Length > 0 ? EffectType : Kind == TemplateEffectKind.Other ? "" : SpellTemplateMapping.EffectTypeName(Kind);

    /// <summary>
    /// <see cref="TargetName"/>, or the member <see cref="Target"/> names when the node was built without one.
    /// </summary>
    public string TargetMemberName => TargetName.Length > 0 ? TargetName : SpellTemplateMapping.EffectTargetName(Target) ?? "";

    /// <summary>
    /// The type the effect deals in: its own, or for a container (a roll, an effect list, a condition) its first child's.
    /// </summary>
    public string LeadType
        => EffectTypeName is "" or "kInvalidSpellEffect" && !Children.IsEmpty ? Children[0].LeadType : EffectTypeName;

}

/// <summary>
/// A client spell template as the classic values see it.
/// </summary>
public sealed record SpellTemplateShape {

    /// <summary>
    /// The template's Root.wad path, such as <c>Spells/Tiered Spells/Fire Cat.xml</c>.
    /// </summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The pip cost (spell rank); 0 on X cards.
    /// </summary>
    public int Rank { get; init; }

    public bool IsXPip { get; init; }

    /// <summary>
    /// The school pips the rank requires, all schools together.
    /// </summary>
    public int SchoolPips { get; init; }

    /// <summary>
    /// The accuracy as a whole percentage.
    /// </summary>
    public int Accuracy { get; init; }

    public ImmutableArray<TemplateEffectNode> Effects { get; init; } = [];

}

/// <summary>
/// Where an effect sits in a template.
/// </summary>
/// <param name="Index">The index in the template's effect list.</param>
/// <param name="Child">The child index inside a random or per-pip effect, or -1 for the listed effect itself.</param>
public readonly record struct EffectAddress(int Index, int Child = -1);

/// <summary>
/// New values for one template effect; a null field stays as it is.
/// </summary>
/// <param name="Address">The effect.</param>
/// <param name="Param">The new amount or percentage.</param>
/// <param name="Rounds">The new duration.</param>
/// <param name="HealModifier">The new heal share of a drain.</param>
/// <param name="Target">The new target.</param>
/// <param name="Kind">The new effect type, when the classic effect means something else than the client's.</param>
/// <param name="EffectType">The new effect type as a kSpellEffects member name, for types <see cref="TemplateEffectKind"/> does not name
/// (a power pip chance bubble); wins over <paramref name="Kind"/>.</param>
/// <param name="DamageType">The new school the effect deals or guards against, such as <c>Myth</c> or <c>All</c>.</param>
public sealed record EffectChange(EffectAddress Address, int? Param = null, int? Rounds = null, float? HealModifier = null,
                                  TemplateTarget? Target = null, TemplateEffectKind? Kind = null, string? EffectType = null,
                                  string? DamageType = null) {

    /// <summary>
    /// The kSpellEffects member the change writes, or null when the type stays.
    /// </summary>
    public string? EffectTypeName => EffectType ?? (Kind is { } kind ? SpellTemplateMapping.EffectTypeName(kind) : null);

}
