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
 * CLASSIC SPELL VALUES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * SpellTemplateMapping reads client spell effects into template shapes and
 * finds the effect a plan's address names, the way the server's adapter
 * uses it with the generated client types.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * FakeEffect stands in for the generated SpellEffect: its type chain and
 * names are those of the r806919 client classes.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SpellTemplateMappingTests {

    private sealed class FakeEffect(string[] typeChain, string type = "kDamage", string target = "kEnemySingle", string school = "Fire",
                                    int param = 0, params FakeEffect?[] children) {

        public string[] TypeChain { get; } = typeChain;
        public string Type { get; } = type;
        public string Target { get; } = target;
        public string School { get; } = school;
        public int Param { get; } = param;
        public FakeEffect?[] Children { get; } = children;

    }

    private static readonly string[] s_plain = ["SpellEffect", "PropertyClass"];
    private static readonly string[] s_random = ["RandomSpellEffect", "SpellEffect", "PropertyClass"];
    private static readonly string[] s_randomPerTarget = ["RandomPerTargetSpellEffect", "RandomSpellEffect", "SpellEffect", "PropertyClass"];
    private static readonly string[] s_variable = ["VariableSpellEffect", "SpellEffect", "PropertyClass"];
    private static readonly string[] s_effectList = ["EffectListSpellEffect", "SpellEffect", "PropertyClass"];

    private static ClientEffectFields Read(FakeEffect effect)
        => new(effect.TypeChain, effect.Type, effect.Target, effect.School, effect.Param, 3, 1, 0.5f);

    private static IReadOnlyList<FakeEffect?>? Children(FakeEffect effect)
        => effect.TypeChain[0] is "RandomSpellEffect" or "RandomPerTargetSpellEffect" or "VariableSpellEffect" ? effect.Children : null;

    [Theory]
    [InlineData("kDamage", TemplateEffectKind.Damage)]
    [InlineData("kDamageOverTime", TemplateEffectKind.DamageOverTime)]
    [InlineData("kHeal", TemplateEffectKind.Heal)]
    [InlineData("kHealOverTime", TemplateEffectKind.HealOverTime)]
    [InlineData("kStealHealth", TemplateEffectKind.StealHealth)]
    [InlineData("kModifyOutgoingDamage", TemplateEffectKind.ModifyOutgoingDamage)]
    [InlineData("kModifyIncomingDamage", TemplateEffectKind.ModifyIncomingDamage)]
    [InlineData("kModifyAccuracy", TemplateEffectKind.ModifyAccuracy)]
    [InlineData("kModifyOutgoingHeal", TemplateEffectKind.ModifyOutgoingHeal)]
    [InlineData("kModifyIncomingHeal", TemplateEffectKind.ModifyIncomingHeal)]
    [InlineData("kAbsorbDamage", TemplateEffectKind.AbsorbDamage)]
    [InlineData("kModifyPips", TemplateEffectKind.ModifyPips)]
    [InlineData("kMaxHealthDamage", TemplateEffectKind.MaxHealthDamage)]
    [InlineData("kStun", TemplateEffectKind.Other)]
    [InlineData("kCritBoost", TemplateEffectKind.Other)]
    [InlineData("kInvalidSpellEffect", TemplateEffectKind.Other)]
    [InlineData(null, TemplateEffectKind.Other)]
    public void EffectTypesMapByName(string? name, TemplateEffectKind kind) {
        Assert.Equal(kind, SpellTemplateMapping.KindOf(name));
        if (kind != TemplateEffectKind.Other) {
            Assert.Equal(name, SpellTemplateMapping.EffectTypeName(kind));
            Assert.Contains(name, SpellTemplateMapping.EffectTypeNames);
        }
    }

    [Fact]
    public void OtherHasNoClientEffectType() {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => SpellTemplateMapping.EffectTypeName(TemplateEffectKind.Other));
    }

    [Theory]
    [InlineData("kEnemySingle", TemplateTarget.EnemySingle)]
    [InlineData("kFriendlySingle", TemplateTarget.FriendlySingle)]
    [InlineData("kMinion", TemplateTarget.MinionSingle)]
    [InlineData("kFriendlyMinion", TemplateTarget.MinionSingle)]
    [InlineData("kEnemyMinion", TemplateTarget.MinionSingle)]
    [InlineData("kCasterMinion", TemplateTarget.MinionSingle)]
    [InlineData("kTargetMinion", TemplateTarget.MinionSingle)]
    [InlineData("kSelf", TemplateTarget.Self)]
    [InlineData("kEnemyTeam", TemplateTarget.EnemyTeam)]
    [InlineData("kEnemyTeamAllAtOnce", TemplateTarget.EnemyTeam)]
    [InlineData("kFriendlyTeam", TemplateTarget.FriendlyTeam)]
    [InlineData("kFriendlyTeamAllAtOnce", TemplateTarget.FriendlyTeam)]
    [InlineData("kGlobal", TemplateTarget.Global)]
    [InlineData("kInvalidTarget", TemplateTarget.Other)]
    [InlineData(null, TemplateTarget.Other)]
    public void TargetsMapByName(string? name, TemplateTarget target) {
        Assert.Equal(target, SpellTemplateMapping.TargetOf(name));
        if (target != TemplateTarget.Other) {
            Assert.Contains(name, SpellTemplateMapping.TargetNames);
        }
    }

    [Fact]
    public void AWidenedTargetIsWrittenAsTheTeamNotAllAtOnce() {
        Assert.Equal("kEnemyTeam", SpellTemplateMapping.EffectTargetName(TemplateTarget.EnemyTeam));
        Assert.Equal("kFriendlyTeam", SpellTemplateMapping.EffectTargetName(TemplateTarget.FriendlyTeam));
        Assert.Equal("kSelf", SpellTemplateMapping.EffectTargetName(TemplateTarget.Self));
        Assert.Null(SpellTemplateMapping.EffectTargetName(TemplateTarget.MinionSingle));
        Assert.Null(SpellTemplateMapping.EffectTargetName(TemplateTarget.Other));
        Assert.All(Enumerable.Range(0, 8).Select(i => SpellTemplateMapping.EffectTargetName((TemplateTarget) i)).OfType<string>(),
            name => Assert.Contains(name, SpellTemplateMapping.TargetNames));
    }

    [Fact]
    public void CompositionFollowsTheClassAndItsBases() {
        Assert.Equal(TemplateComposition.Plain, SpellTemplateMapping.CompositionOf(s_plain));
        Assert.Equal(TemplateComposition.Random, SpellTemplateMapping.CompositionOf(s_random));
        Assert.Equal(TemplateComposition.Random, SpellTemplateMapping.CompositionOf(s_randomPerTarget));
        Assert.Equal(TemplateComposition.PerPip, SpellTemplateMapping.CompositionOf(s_variable));
        Assert.Equal(TemplateComposition.Other, SpellTemplateMapping.CompositionOf(s_effectList));
        Assert.Equal(TemplateComposition.Other, SpellTemplateMapping.CompositionOf(["ConditionalSpellEffect", "SpellEffect", "PropertyClass"]));
        Assert.Equal(TemplateComposition.Other, SpellTemplateMapping.CompositionOf(["DelaySpellEffect", "SpellEffect", "PropertyClass"]));
        Assert.Equal(TemplateComposition.Other, SpellTemplateMapping.CompositionOf(["ShadowSpellEffect", "SpellEffect", "PropertyClass"]));
        Assert.Equal(TemplateComposition.Other, SpellTemplateMapping.CompositionOf([]));
    }

    [Fact]
    public void NodesCarryTheFieldsAndTheRolledChildren() {
        var random = new FakeEffect(s_randomPerTarget, "kInvalidSpellEffect", "kEnemySingle", "", -1,
            new FakeEffect(s_plain, param: 80), null, new FakeEffect(s_plain, param: 120));

        var node = SpellTemplateMapping.NodeOf(random, Read, Children);

        Assert.Equal(TemplateComposition.Random, node.Composition);
        Assert.Equal(TemplateEffectKind.Other, node.Kind);
        Assert.Equal(3, node.Children.Length);
        Assert.Equal((TemplateComposition.Plain, TemplateEffectKind.Damage, TemplateTarget.EnemySingle, "Fire", 80, 3, 1, 0.5f),
            (node.Children[0].Composition, node.Children[0].Kind, node.Children[0].Target, node.Children[0].DamageType,
                node.Children[0].Param, node.Children[0].Rounds, node.Children[0].PipNumber, node.Children[0].HealModifier));
        Assert.Equal(TemplateComposition.Other, node.Children[1].Composition);
        Assert.Equal(120, node.Children[2].Param);
    }

    [Fact]
    public void AnEffectListKeepsItsChildrenOutOfReach() {
        // Immolate: the self-hit sits in a conditional inside an effect list, which the plan cannot address.
        var list = new FakeEffect(s_effectList, "kInvalidSpellEffect", children: new FakeEffect(s_plain, target: "kSelf", param: 200));

        var node = SpellTemplateMapping.NodeOf(list, Read, Children);

        Assert.Equal(TemplateComposition.Other, node.Composition);
        Assert.Empty(node.Children);
        Assert.Equal(TemplateComposition.Other, SpellTemplateMapping.NodeOf<FakeEffect>(null, Read, Children).Composition);
    }

    [Fact]
    public void AnAddressNamesAnEffectOrOneOfItsChildren() {
        var first = new FakeEffect(s_plain, param: 30);
        var low = new FakeEffect(s_plain, param: 10);
        var high = new FakeEffect(s_plain, param: 100);
        var tiers = new FakeEffect(s_variable, children: [low, high]);
        FakeEffect?[] effects = [first, tiers, null];

        Assert.Same(first, SpellTemplateMapping.EffectAt(effects, new EffectAddress(0), Children));
        Assert.Same(tiers, SpellTemplateMapping.EffectAt(effects, new EffectAddress(1), Children));
        Assert.Same(high, SpellTemplateMapping.EffectAt(effects, new EffectAddress(1, 1), Children));
        Assert.Null(SpellTemplateMapping.EffectAt(effects, new EffectAddress(1, 2), Children));
        Assert.Null(SpellTemplateMapping.EffectAt(effects, new EffectAddress(0, 0), Children));
        Assert.Null(SpellTemplateMapping.EffectAt(effects, new EffectAddress(2), Children));
        Assert.Null(SpellTemplateMapping.EffectAt(effects, new EffectAddress(3), Children));
        Assert.Null(SpellTemplateMapping.EffectAt(effects, new EffectAddress(-1), Children));
        Assert.Null(SpellTemplateMapping.EffectAt<FakeEffect>(null, new EffectAddress(0), Children));
    }

    [Fact]
    public void APlanLandsOnTheEffectsItsShapeWasBuiltFrom() {
        // The addresses a plan makes from a shape reach the same client effects the shape was read from.
        var hit = new FakeEffect(s_plain, param: 30);
        var roll = new FakeEffect(s_random, "kInvalidSpellEffect", children: [new FakeEffect(s_plain, param: 90), new FakeEffect(s_plain, param: 130)]);
        FakeEffect?[] effects = [roll, hit];
        var shape = new SpellTemplateShape {
            Path = "Spells/Test.xml",
            Name = "Test",
            Rank = 1,
            Accuracy = 75,
            Effects = [.. effects.Select(effect => SpellTemplateMapping.NodeOf(effect, Read, Children))],
        };

        var plan = SpellFixture.Plan(shape, SpellPips.Of(1), 0.75, SpellFixture.Effect(SpellEffectKind.Damage, min: 80, max: 120));

        var written = plan.EffectChanges.ToDictionary(change => SpellTemplateMapping.EffectAt(effects, change.Address, Children)!, change => change.Param);
        Assert.Equal(80, written[roll.Children[0]!]);
        Assert.Equal(120, written[roll.Children[1]!]);
        Assert.Equal(0, written[hit]);
    }

}
