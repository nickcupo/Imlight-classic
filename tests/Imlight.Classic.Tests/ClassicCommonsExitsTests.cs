using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicCommonsExitsTests {

    private static Trigger Exit(string name) => new() {
        m_triggerName = name,
        m_fireEvents = ["Enter_Activator Volume (3)"],
        m_requirements = new RequirementList {
            m_operator = Operator.ROP_AND,
            m_requirements = [
                new ReqHasEntry { m_operator = Operator.ROP_OR, m_entryName = "WC-ST01-C01-006_Complete" },
                new ReqHasEntry { m_operator = Operator.ROP_AND, m_entryName = "WC-UNICORN-MAIN-008_Complete" },
            ],
        },
        m_results = new ResultList { m_results = [new ResTeleport()] },
    };

    [Fact]
    public void GolemCourtAndShoppingDistrictOpenAtLevelTwoAsInThe2014Client() {
        var triggers = new List<Trigger> { Exit("TeleportToGolemCourt"), Exit("TeleportToShoppingDistrict"), Exit("Trigger TelepotToUW") };

        Assert.Equal(2, ClassicCommonsExits.Apply("WizardCity/WC_Hub", triggers));

        foreach (var exit in triggers.Take(2)) {
            var level = Assert.IsType<ReqMagicLevel>(Assert.Single(exit.m_requirements.m_requirements));
            Assert.Equal(2, level.m_numericValue);
            Assert.Equal(OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ, level.m_operatorType);
            Assert.False(level.m_applyNOT);
        }

        // Unicorn Way keeps Stillson's gate.
        Assert.Equal(2, triggers[2].m_requirements.m_requirements.Count);

        var gates = triggers.Skip(3).ToList();
        Assert.Equal(2, gates.Count);
        Assert.Equal(new[] { "WC_GateCommons_ToGolemCourt", "WC_GateCommons_ToShoppingDist" },
            gates.Select(g => (string) Assert.IsType<ResAddDynaMod>(Assert.Single(g.m_results.m_results)).m_dynaModClientTag).OrderBy(x => x));
        Assert.All(gates, g => {
            Assert.Equal("EnterZone", (string) Assert.Single(g.m_fireEvents));
            Assert.Equal("IdleOpen", (string) ((ResAddDynaMod) g.m_results.m_results[0]).m_dynaModState);
            Assert.IsType<ReqMagicLevel>(Assert.Single(g.m_requirements.m_requirements));
        });

        // Applying again (a zone reload) adds no second gate trigger.
        ClassicCommonsExits.Apply("WizardCity/WC_Hub", triggers);
        Assert.Equal(5, triggers.Count);
    }

    [Fact]
    public void OtherZonesAreLeftAsTheyAre() {
        var triggers = new List<Trigger> { Exit("TeleportToGolemCourt") };

        Assert.Equal(0, ClassicCommonsExits.Apply("WizardCity/WC_Ravenwood", triggers));
        Assert.Equal(0, ClassicCommonsExits.Apply("WizardZone_TheCommons", triggers));
        Assert.Single(triggers);
        Assert.Equal(2, triggers[0].m_requirements.m_requirements.Count);
    }

}
