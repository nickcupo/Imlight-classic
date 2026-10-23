using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// CLASSIC: Administer Cure (WC-ST05-C01-005) goal 3, "Talk To Alicane Swiftarrow", targets KingsIsle's
/// WC-ST05-NPC04 (template 38104). The Theatre's 'Prince NPC Trigger' spawns her at the boss point when the possessed
/// Alicane (35196, goal 2) is defeated, and the client's quest arrow points there (poi.dat, template 38104). The 2021
/// static copy WC-FIRECAT-NPC06 (1521054) is r806919-only; a rig without it blocked the Firecat chain at goal 3
/// (srv-regress, 2026-10-23).
/// </summary>
public sealed class AdministerCureAlicaneTests {

    private const string Quest = "WC-ST05-C01-005";
    private const int ClassicAlicane = 38104;
    private const int ModernAlicane = 1521054;

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static string QuestJson()
        => File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", Quest + ".json"));

    [Fact]
    public void TalkGoalTargetsKingsIslesAlicane() {
        var quest = JsonConvert.DeserializeObject<QuestTemplate>(QuestJson(), s_json)!;

        var talk = Assert.IsType<PersonaGoalTemplate>(quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal 3"));
        Assert.Equal("WC-ST05-NPC04", talk.m_personaName.ToString());
        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Firecat_Theatre", talk.m_destinationZone.ToString());

        // Goal 3 follows the boss fight that makes the trigger spawn her.
        var boss = quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal 2");
        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Firecat_Theatre", boss.m_destinationZone.ToString());
    }

    [Fact]
    public void NoGoalOrDialogNamesTheModernCopy() {
        var json = QuestJson();
        Assert.DoesNotContain("WC-FIRECAT-NPC06", json);
        Assert.DoesNotContain(ModernAlicane.ToString(), json);

        // Goal 3's completion lines are spoken by the classic Alicane.
        var goal3 = JObject.Parse(json)["m_goals"]!.First(g => (string?) g["m_goalName"] == "Goal 3");
        var actors = goal3.SelectTokens("$..m_actorTemplateID").Select(t => (int) t).ToList();
        Assert.NotEmpty(actors);
        Assert.All(actors, id => Assert.Equal(ClassicAlicane, id));
    }
}
