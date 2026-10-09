/*
 * CLASSIC (2026-10-08): the runtime half of the October 2010 Bazaar shelf
 * (ClassicBazaar.LotsPerRestock and ClassicBazaar.LevelOf). Server
 * integration test: it uses CoreLib and the generated requirement types.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Bazaar;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class BazaarShelfServerTests {

    [Fact]
    public void TheOctoberShelfSetsTheFloorOfLotsPerRestock() {
        var october = BazaarRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-october-2010.yaml"));
        var classic = BazaarRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-2009.yaml"));
        Assert.Equal(october.Shelf!.Lots, ClassicBazaar.LotsPerRestock(october, stocked: true, setting: 120));
        Assert.Equal(900, ClassicBazaar.LotsPerRestock(october, stocked: true, setting: 900));
        Assert.Equal(0, ClassicBazaar.LotsPerRestock(october, stocked: false, setting: 120));
        Assert.Equal(120, ClassicBazaar.LotsPerRestock(classic, stocked: true, setting: 120));
        Assert.Equal(0, ClassicBazaar.LotsPerRestock(classic, stocked: false, setting: 120));
    }

    [Fact]
    public void LevelOfReadsTheItemsLevelRequirement() {
        Assert.Equal(0, ClassicBazaar.LevelOf(new WizItemTemplate()));
        Assert.Equal(0, ClassicBazaar.LevelOf(null));
        var item = new WizItemTemplate {
            m_equipRequirements = new() {
                m_requirements = [
                    new ReqSchoolOfFocus { m_magicSchool = "Fire" },
                    new RequirementList { m_requirements = [new ReqMagicLevel { m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ, m_numericValue = 25 }] },
                ],
            },
        };
        Assert.Equal(25, ClassicBazaar.LevelOf(item));
        var above = new WizItemTemplate {
            m_equipRequirements = new() { m_requirements = [new ReqMagicLevel { m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN, m_numericValue = 9 }] },
        };
        Assert.Equal(10, ClassicBazaar.LevelOf(above));
        var notBelow = new WizItemTemplate {
            m_equipRequirements = new() { m_requirements = [new ReqMagicLevel { m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ, m_numericValue = 30, m_applyNOT = true }] },
        };
        Assert.Equal(0, ClassicBazaar.LevelOf(notBelow));
    }

}
