using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Commands;
using Xunit;

namespace Imlight.Classic.Tests;

public class EquipRulesTests {

    private static ReqMagicLevel Level(int level, Operator join = Operator.ROP_AND) => new() {
        m_applyNOT = false,
        m_operator = join,
        m_numericValue = level,
        m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ,
    };

    private static ReqSchoolOfFocus School(string school, Operator join = Operator.ROP_AND) => new() {
        m_applyNOT = false,
        m_operator = join,
        m_magicSchool = school,
    };

    private static WizItemTemplate Item(List<string> adjectives, params Requirement[] requirements) => new() {
        m_adjectiveList = adjectives,
        m_equipRequirements = requirements.Length == 0 ? null : new RequirementList {
            m_applyNOT = false,
            m_operator = Operator.ROP_AND,
            m_requirements = [.. requirements],
        },
    };

    [Fact]
    public void A_wizard_who_meets_the_level_and_school_may_equip() {
        var hat = Item(["Hat"], Level(10), School("Fire"));
        Assert.Equal(EquipRefusal.None, EquipRules.Check(hat, level: 10, school: "Fire", inDuel: false));
        Assert.Equal(EquipRefusal.None, EquipRules.Check(Item(["Robe"]), level: 1, school: "Ice", inDuel: false));
    }

    [Fact]
    public void Level_and_school_requirements_are_enforced() {
        var hat = Item(["Hat"], Level(10), School("Fire"));
        Assert.Equal(EquipRefusal.RequirementsNotMet, EquipRules.Check(hat, level: 9, school: "Fire", inDuel: false));
        Assert.Equal(EquipRefusal.RequirementsNotMet, EquipRules.Check(hat, level: 50, school: "Ice", inDuel: false));
    }

    [Fact]
    public void Either_school_of_an_or_list_may_equip() {
        var wand = Item(["Weapon"], School("Fire", Operator.ROP_OR), School("Ice"));
        Assert.Equal(EquipRefusal.None, EquipRules.Check(wand, level: 1, school: "Ice", inDuel: false));
        Assert.Equal(EquipRefusal.RequirementsNotMet, EquipRules.Check(wand, level: 1, school: "Life", inDuel: false));
    }

    [Fact]
    public void Unfamiliar_requirement_kinds_never_lock_gear() {
        var ring = Item(["Ring"], new ReqHasEntry { m_applyNOT = false, m_operator = Operator.ROP_AND });
        Assert.Equal(EquipRefusal.None, EquipRules.Check(ring, level: 1, school: "Myth", inDuel: false));
    }

    [Fact]
    public void Gear_does_not_change_during_a_duel() {
        Assert.Equal(EquipRefusal.InDuel, EquipRules.Check(Item(["Hat"]), level: 50, school: "Fire", inDuel: true));
    }

    [Fact]
    public void An_item_with_no_slot_is_not_equippable() {
        Assert.Equal(EquipRefusal.NotEquippable, EquipRules.Check(Item(["Reagent"]), level: 50, school: "Fire", inDuel: false));
        Assert.Equal(EquipRefusal.NotEquippable, EquipRules.Check(new WizItemTemplate(), level: 50, school: "Fire", inDuel: false));
        Assert.Null(EquipRules.SlotOf(null!));
    }

    [Fact]
    public void A_failed_command_tells_the_player_nothing_about_the_exception() {
        Assert.DoesNotContain("Exception", CommandDispatcher.CommandErrorReply);
        Assert.DoesNotContain(" at ", CommandDispatcher.CommandErrorReply);
    }

}
