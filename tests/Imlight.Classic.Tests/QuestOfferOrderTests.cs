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

    [Fact]
    public void QuestOptionIconsMatchTheClientsIconTable() {
        // r806919 FUN_140a19ea0 fills the option icon table (0x143348660): Start = gold "!", Prep = silver "!"
        // (a quest that cannot be taken yet), Unfinished = silver "?", Complete = gold "?". Zeke's available
        // "Find the Smiths" showed a silver "!" because both the offer and the underway option sent "Prep".
        Assert.Equal("Start", InteractQuestOfferComponent.START_NPC_ICON);
        Assert.Equal("Unfinished", InteractQuestUnderwayComponent.UNFINISHED_NPC_ICON);
    }

    [Fact]
    public void AClickNamesTheOptionAmongItsOwnComponentsOptions() {
        // REA 2026-10-17: an NPC lists one option per quest it can offer, has underway, or has a goal at; the memento
        // tells the component which of its own options was clicked.
        IServiceComponent offer = new FakeComponent(), shop = new FakeComponent(), goal = new FakeComponent();
        var map = new System.Collections.Generic.Dictionary<int, IServiceComponent> {
            [0] = goal, [1] = offer, [2] = shop, [3] = offer, [4] = offer,
        };

        Assert.Equal(0, PlayerOptionIndex.Ordinal(map, 1));
        Assert.Equal(1, PlayerOptionIndex.Ordinal(map, 3));
        Assert.Equal(2, PlayerOptionIndex.Ordinal(map, 4));
        Assert.Equal(0, PlayerOptionIndex.Ordinal(map, 2));
        Assert.Equal(0, PlayerOptionIndex.Ordinal(map, 9)); // not in the list
    }

    [Fact]
    public void AnOrdinalPastAChangedListFallsBackToTheFirst() {
        var quests = new[] { new QuestTemplate { m_questName = "A" }, new QuestTemplate { m_questName = "B" } };

        Assert.Equal("B", InteractQuestOfferComponent.Pick(quests, 1)!.m_questName);
        Assert.Equal("A", InteractQuestOfferComponent.Pick(quests, 5)!.m_questName);
        Assert.Equal("A", InteractQuestOfferComponent.Pick(quests, -1)!.m_questName);
        Assert.Null(InteractQuestOfferComponent.Pick(System.Array.Empty<QuestTemplate>(), 0));
    }

    private sealed class FakeComponent : IServiceComponent {
        public string ServiceName => "Fake";
        public string NpcIcon => "";
        public string NpcNameKey => null!;
        public string NpcTextKey => null!;
        public string StateName => "";
        public string InteractWizBang => "";
        public string DisplayKey => "";
        public Imlight.CoreLib.Game.WizBang.WizBangs WizBang => Imlight.CoreLib.Game.WizBang.WizBangs.None;
        public System.Collections.Generic.IEnumerable<ServiceOptionBase> GetServiceOptions(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard) => [];
        public void OnServiceInteraction(Akka.Actor.IActorRef playerActor, Imlight.CoreLib.WizardData.Models.Player.Wizard playerCharacter,
            CoreObject playerObject, uint serviceOptionIndex) { }
    }

}
