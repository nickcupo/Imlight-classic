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
 * TOMB OF THE BEGUILER AND STEALTHY STUFF
 * ========================================================================
 *
 * PURPOSE:
 * Pins two pre-cutoff pieces the datamine gap report found unplayable:
 * the Well of Spirits sigil into the Tomb of the Beguiler (its entry
 * QT-KT-CRY7-INSTANCE was set by nothing) and Big Ben's own instance
 * quest Stealthy Stuff (MB-MUSE3-C03-001, not served at all).
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter BeguilerStealthyTests
 *
 * NOTE:
 * Stealthy Stuff's goal names and ids come from the client's quest-helper
 * (POI) table: "Goal" points at the level-5 Travis Pawman (39870), "Goal 2"
 * at the level-1 one (39734). The level-1 goal comes first because the
 * level-5 Travis only appears when the zone event MovePawman runs Big Ben's
 * MoveTravisPawman trigger, and the quest's first goal posts it.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class BeguilerStealthyTests : IDisposable {

    private const string DangerBeware = "KT-CLASSIC-SIDE-060";
    private const string TombEntry = "QT-KT-CRY7-INSTANCE";
    private const string StealthyStuff = "MB-MUSE3-C03-001";
    private const string BigBen = "Marleybone/MB_BigBen/MB_BigBen";

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public BeguilerStealthyTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap()));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-begst-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static string Overlay(params string[] path)
        => File.ReadAllText(Path.Combine([ClassicDataFixture.Root, "spiraldb-overlay", .. path]));

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(Overlay("QuestTemplates", name + ".json"), s_json)!;

    private static Wizard NewWizard() => new() {
        CharId = 1,
        MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = 30 },
        QuestBehavior = new ServerQuestBehavior(),
    };

    private static bool Meets(RequirementList requirements, Wizard wizard)
        => RequirementDispatcher.EvaluateRequirements(requirements,
            new QuestRequirementContext(requirements, null, null, wizard, ""));

    // The sigil's own requirement as the client zone data states it (r806919 obj0 / 2014 obj4 in KT_WellOfSpirits).
    private static RequirementList ClientSigil() => new() {
        m_operator = Operator.ROP_AND,
        m_requirements = [new ReqHasEntry { m_entryName = TombEntry, m_isQuestRegistry = false, m_operator = Operator.ROP_AND }],
    };

    private static JToken TombSigilTeleport()
        => JObject.Parse(Overlay("ZoneTransfer", "Krokotopia-KT_Tomb-KT_WellOfSpirits.travel.json"))["Teleports"]!
            .Single(t => (string?) t["TriggerName"] == "KT-CRY7-C01-001-Sigil")["Teleport"]!;

    private static RequirementList TravelSigil()
        => JsonConvert.DeserializeObject<RequirementList>(TombSigilTeleport()["m_requirements"]!.ToString(), s_json)!;

    [Fact]
    public void DangerBewareLightsTheTombSigilAndItStaysLit() {
        var quest = LoadQuest(DangerBeware);
        var wizard = NewWizard();
        Assert.False(Meets(ClientSigil(), wizard));

        // What ResModifyEntryHandler does with the start result (Wizard.SetRegistryValue, without its save).
        var start = Assert.IsType<ResModifyEntry>(Assert.Single(quest.m_startResults.m_results));
        Assert.Equal(TombEntry, start.m_entryName.ToString());
        Assert.False(start.m_isQuestRegistry);
        Assert.True(wizard.QuestBehavior.SetRegistryValue(start.m_entryName, (ulong) start.m_value));
        Assert.True(Meets(ClientSigil(), wizard));

        Assert.All(quest.m_endResults.m_results, result => Assert.IsNotType<ResModifyEntry>(result));
        Assert.All(quest.m_goals.SelectMany(goal => goal.m_completeResults?.m_results ?? []),
            result => Assert.IsNotType<ResModifyEntry>(result));
    }

    [Fact]
    public void TheTombSigilLeadsIntoTheTomb() {
        Assert.Equal("Krokotopia/KT_Tomb/Interiors/KT_Crypt06_Map00_Storm", (string?) TombSigilTeleport()["m_destinationZone"]);
    }

    [Fact]
    public void TheTombSigilAlsoAdmitsWizardsWhoTookDangerBewareBeforeItSetTheEntry() {
        var sigil = TravelSigil();

        Assert.False(Meets(sigil, NewWizard()));

        var withEntry = NewWizard();
        withEntry.QuestBehavior.SetRegistryValue(TombEntry, 1);
        Assert.True(Meets(sigil, withEntry));

        var holder = NewWizard(); // took the quest from a build that did not set the entry
        Assert.True(holder.QuestBehavior.AddQuest(new QuestInstance(LoadQuest(DangerBeware), holder.CharId)));
        Assert.True(Meets(sigil, holder));

        var done = NewWizard();
        done.QuestBehavior.SetQuestRegistryValue(DangerBeware, "Complete", 1);
        Assert.True(Meets(sigil, done));

        var other = NewWizard(); // a later quest of the chain alone does not open it
        other.QuestBehavior.SetQuestRegistryValue("KT-HUB-C03-004", "Complete", 1);
        Assert.False(Meets(sigil, other));
    }

    [Fact]
    public void StealthyStuffIsGrantedOnEnteringBigBen() {
        var quest = LoadQuest(StealthyStuff);
        var requirement = Assert.IsType<ReqInZone>(Assert.Single(quest.m_requirements.m_requirements));
        Assert.Equal(BigBen, requirement.m_zoneName.ToString());

        var wizard = NewWizard();
        wizard.Zone = "Marleybone/MB_BigBen/MB_Museum";
        Assert.False(Meets(quest.m_requirements, wizard));
        wizard.Zone = BigBen;
        Assert.True(Meets(quest.m_requirements, wizard));
        Assert.False(quest.m_mainline);
        Assert.Equal("QuestTitle_9B8D", quest.m_questTitle.ToString());
    }

    [Fact]
    public void StealthyStuffTalksToTravisDownstairsThenUpstairs() {
        var quest = LoadQuest(StealthyStuff);

        // Arrow ids: KingsIsle's goal names, as the r806919 POI records under StringHash("MB-MUSE3-C03-001") key them.
        foreach (var goal in quest.m_goals) {
            Assert.Equal(Imcodec.Cryptography.StringHash.Compute(goal.m_goalName.ToString()), goal.m_goalNameID);
        }

        var visited = new List<string>();
        var active = quest.m_startGoals.Select(goal => goal.ToString()).ToList();
        var completed = false;
        while (active.Count > 0 && !completed) {
            var goal = Assert.Single(active);
            visited.Add(goal);
            var step = quest.m_goalLogic.Single(logic => logic.m_goalsAND.Select(g => g.ToString()).SequenceEqual([goal]));
            completed = step.m_completeQuest;
            active = [.. step.m_goalsToAdd.Select(g => g.ToString())];
        }

        Assert.True(completed);
        Assert.Equal(["Goal 2", "Goal"], visited);

        var downstairs = Assert.IsType<PersonaGoalTemplate>(quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal 2"));
        var upstairs = Assert.IsType<PersonaGoalTemplate>(quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal"));
        Assert.Equal("MB-MUSE3-NPC03", downstairs.m_personaName.ToString());
        Assert.Equal("MB-MUSE3-NPC05_Maintenance", upstairs.m_personaName.ToString());

        // The first talk sends Travis up the stairs: Big Ben's MoveTravisPawman trigger waits for this event.
        var post = Assert.IsType<ResPostEvent>(Assert.Single(downstairs.m_completeResults.m_results));
        Assert.Equal("MovePawman", post.m_eventName.ToString());
        Assert.Empty(upstairs.m_completeResults.m_results);

        Assert.Equal(["WizQst9B8D_00000005"], Lines(downstairs));
        Assert.Equal(["WizQst9B8D_00000006", "WizQst9B8D_00000007"], Lines(upstairs));
        Assert.All(quest.m_goals, goal => Assert.Equal(BigBen, goal.m_destinationZone.ToString()));
    }

    [Fact]
    public void StealthyStuffPaysItsApril2009Rewards() {
        var quest = LoadQuest(StealthyStuff);
        var roll = Assert.IsType<ResDropTable>(Assert.Single(quest.m_endResults.m_results));
        Assert.Equal(StealthyStuff, roll.m_tableName.ToString());

        var table = JObject.Parse(Overlay("DropTables", StealthyStuff + ".json"));
        Assert.Equal(83, (int) table["MinGold"]!);
        Assert.Equal(83, (int) table["MaxGold"]!);
        Assert.Equal(255, (int) table["ExperienceAmount"]!);
    }

    private static List<string> Lines(GoalTemplate goal)
        => ((ActorDialogList) goal.m_dialogList).m_dialogs.Single(d => d.m_dialogTag.ToString() == "Completion").m_dialogEntries
            .Select(e => ((NPCDialogEntry) e).m_dialog.ToString()).ToList();

}
