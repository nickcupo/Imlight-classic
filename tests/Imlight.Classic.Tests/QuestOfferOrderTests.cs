using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class QuestOfferOrderTests {

    [Fact]
    public void AnNpcOffersItsStoryQuestBeforeItsSideQuests() {
        // Ambrose: Colossal Trouble (side) sorts before A Look of Horror (story) by title; the story quest comes first.
        var side = new QuestTemplate { m_questName = "WC-MISC-C02-001", m_questTitle = "A", m_mainline = false };
        var story = new QuestTemplate { m_questName = "WC-MAIN-C01-006", m_questTitle = "B", m_mainline = true };
        var otherSide = new QuestTemplate { m_questName = "WC-CLASSIC-SIDE-025", m_questTitle = "C", m_mainline = false };
        var quests = new[] { otherSide, side, story }.ToList();

        quests.Sort(InteractQuestOfferComponent.OfferOrder);

        Assert.Equal(["WC-MAIN-C01-006", "WC-MISC-C02-001", "WC-CLASSIC-SIDE-025"], quests.Select(q => q.m_questName));
    }

}
