// CLASSIC: dialog chaining (r806919 NewNPCInteraction 0x14172ad00: on a Reinteract-2 answer for the NPC being talked
// to, an AvailableQuestEntry or AvailableCompleteEntry makes the client interact again at once, no X). The real case:
// "Get Some Bling" (KT-SPH2-C02-007) from General Khaba in KT_ChampHall: a waypoint to the hall, then talk to Khaba.
// Accepted there, the talk-back is the turn-in, then Khaba offers "KT-SPH2-C02-008".

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DialogChainTests {

    private static readonly Lazy<Dictionary<string, QuestTemplate>> s_quests = new(() => {
        EquipmentAttachConcurrencyTests.Configure();
        var list = new List<QuestTemplate>();
        var dict = new ConcurrentDictionary<string, QuestTemplate>(StringComparer.Ordinal);
        typeof(SpiralDB).GetMethod("LoadQuestTemplates", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay"), list, dict]);
        return dict.ToDictionary(p => p.Key, p => p.Value);
    });

    private static QuestTemplate Quest(string name) => s_quests.Value[name];

    // The wizard's KT-SPH2-C02-007 with its waypoint done or not, and the talk to Khaba begun.
    private static QuestInstance Bling(bool waypointDone) {
        var template = Quest("KT-SPH2-C02-007");
        var goals = template.m_goals.Select((g, i) => new GoalInstance(g, 1) { ID = 7000 + (ulong) i }).ToArray();
        foreach (var goal in goals) goal.BeginGoal();
        if (waypointDone) goals.Single(g => g.GoalType != GOAL_TYPE.GOAL_TYPE_PERSONA).CompleteGoal();
        return new QuestInstance { ID = 7700, OwnerCharId = 1, QuestName = template.m_questName, GoalProgress = goals };
    }

    private static List<ServiceOptionBase> KhabasOptions(QuestInstance bling) {
        var talk = bling.GoalProgress.Single(g => g.GoalType == GOAL_TYPE.GOAL_TYPE_PERSONA);
        return [
            new GoalEntry { m_serviceName = DialogChain.PersonaGoalService, m_serviceIndex = 0, m_questID = bling.ID,
                m_goalID = talk.ID, m_questTitle = Quest("KT-SPH2-C02-007").m_questTitle, m_displayKey = Quest("KT-SPH2-C02-007").m_questTitle },
            new PrepEntry { m_serviceName = DialogChain.OfferService, m_serviceIndex = 1, m_displayKey = Quest("KT-SPH2-C02-008").m_questTitle },
            new EquipmentShopOption { m_serviceName = "WizShoppingService", m_serviceIndex = 2 },
        ];
    }

    private static QuestTemplate? ByTitle(string title) => s_quests.Value.Values.FirstOrDefault(q => q.m_questTitle == title);

    [Fact]
    public void KhabasTalkBackTurnsInAndHisNextQuestIsOfferedAtOnce() {
        var bling = Bling(waypointDone: true);
        var chained = DialogChain.Forms(KhabasOptions(bling), id => id == bling.ID ? bling : null, ByTitle);

        var turnIn = Assert.IsType<AvailableCompleteEntry>(chained[0]);
        Assert.Equal((DialogChain.PersonaGoalService, 0u, bling.ID), (turnIn.m_serviceName, turnIn.m_serviceIndex, turnIn.m_questID.Full));
        var next = Assert.IsType<AvailableQuestEntry>(chained[1]);
        Assert.Equal((DialogChain.OfferService, 1u, "KT-SPH2-C02-008"), (next.m_serviceName, next.m_serviceIndex, next.m_questName));
        Assert.Equal(Quest("KT-SPH2-C02-008").m_mainline, next.m_isMainline);
        Assert.IsType<EquipmentShopOption>(chained[2]);                       // other services are untouched

        // They reach the client in their own classes (MSG_SENDNPCOPTIONS' memento, property mask 4).
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Serialize(new ServiceMementoBase { m_serviceOptions = chained }, 4, out var bytes));
        Assert.True(serializer.Deserialize<ServiceMementoBase>(bytes, 4, out var received));
        Assert.IsType<AvailableCompleteEntry>(received!.m_serviceOptions[0]);
        Assert.Equal("KT-SPH2-C02-008", Assert.IsType<AvailableQuestEntry>(received.m_serviceOptions[1]).m_questName);
    }

    [Fact]
    public void ATalkGoalThatDoesNotFinishTheQuestStaysAGoalEntry() {
        var bling = Bling(waypointDone: false);                               // the waypoint is still to do
        Assert.False(DialogChain.TurnsIn(bling, bling.GoalProgress.Single(g => g.GoalType == GOAL_TYPE.GOAL_TYPE_PERSONA).ID));
        var chained = DialogChain.Forms(KhabasOptions(bling), id => id == bling.ID ? bling : null, ByTitle);
        Assert.Equal(typeof(GoalEntry), chained[0].GetType());
    }

    [Fact]
    public void AnUnknownQuestOrTitleIsLeftAsItWas() {
        var bling = Bling(waypointDone: true);
        var chained = DialogChain.Forms(KhabasOptions(bling), _ => null, _ => null);
        Assert.Equal(typeof(GoalEntry), chained[0].GetType());
        Assert.Equal(typeof(PrepEntry), chained[1].GetType());
    }

    // r806919's AvailableCompleteEntry sends MSG_INTERACTCOMPLETEGOAL {NPC, quest, goal} and AvailableQuestEntry sends
    // MSG_INTERACTAVAILABLEQUEST {NPC, quest name} instead of MSG_INTERACTNPC; the server finds the option they chose in
    // the wizard's current list and routes it like that option's click.
    [Fact]
    public void TheChainedMessagesFindTheirOptionInTheCurrentList() {
        var bling = Bling(waypointDone: true);
        var talk = bling.GoalProgress.Single(g => g.GoalType == GOAL_TYPE.GOAL_TYPE_PERSONA);
        var plain = KhabasOptions(bling);                                    // the list as built (not chained)
        var chained = DialogChain.Forms(plain, id => id == bling.ID ? bling : null, ByTitle);
        foreach (var list in new[] { plain, chained }) {
            Assert.Equal(0, DialogChain.FindChosen(list, DialogChain.PersonaGoalService, bling.ID, talk.ID, null));
            Assert.Equal(1, DialogChain.FindChosen(list, DialogChain.OfferService, 0, 0, Quest("KT-SPH2-C02-008").m_questTitle));
        }
        // Not at this NPC now: another goal, another quest, or the wrong service.
        Assert.Equal(-1, DialogChain.FindChosen(plain, DialogChain.PersonaGoalService, bling.ID, talk.ID + 99, null));
        Assert.Equal(-1, DialogChain.FindChosen(plain, DialogChain.OfferService, 0, 0, Quest("KT-SPH2-C02-007").m_questTitle));
        Assert.Equal(-1, DialogChain.FindChosen(plain, DialogChain.OfferService, bling.ID, talk.ID, null));
    }
}
