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
 * INTERACTABLE QUEST EVENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: names the quest events that using a world object fires, so a
 * captured "Use" waypoint goal tagged with one of them can complete.
 *
 * USAGE EXAMPLE:
 * InteractableQuestEvents.AddGoalsCompletedBy(objectTemplate, goalsByQuest); // InteractQuestSelectComponent
 * InteractableQuestEvents.IsCompletedByEvent(goal); // ParallelStartGoals
 *
 * NOTE:
 * Some captured "Use" goals are GOAL_TYPE_WAYPOINT with no zone or
 * proximity tag; only a client tag names what completes them. The object's
 * InteractableBehaviorTemplate (a server type Imcodec does not know, so it
 * deserializes to null) fires a quest event with that name when the object
 * is used. The events below were read from those behaviors in Root.wad,
 * and each object spawns in its goal's zone. An event the behavior allows
 * in one zone only keeps that zone (the Amphitheatre's crystal stands share
 * templates between towers). Goals with a zone or proximity trigger of their
 * own keep it, even when a client tag names an event (DS-LIB1-C01-008's
 * explore goal names the blue crystal's).
 *
 * Arc 1 added the events the audit's tagless goals name (Mantra2/3,
 * TakeBook, UseForge, TouchTomb, UseCrystalStand6, the obsidian chests and
 * the Necropolis pedestals). The pedestals' behaviors fire
 * "UsedEmptyCrystalPedestalC01-00N" while the captured goals are tagged
 * "UseEmptyCrystalPedestalC01-00N"; the table keys on the goal's spelling.
 * An object whose behavior also starts a zone spawner (a ResSpawn result)
 * lists it with the requirements KingsIsle's behavior checks, read from
 * the same bytes: the Windhammer tower's crystal stand summons the drake
 * DS-NEC1-C01-004/006 talk to and DS-NEC1-C02-001/002 are given by, so the
 * stand stays usable for that branch without an open goal.
 * Sunken City's book pedestal fires "DecayBook" (Finding A Way, the Tome
 * of Decay).
 *
 * TODO:
 * - DS-ACAD1-C01-002's goals 1 to 5 complete on entering the Crystal Grove;
 *   should the other five samples' events complete them instead?
 * - The stand's first branch also checks that the stand is not already in
 *   its DrakeSummoned state; the spawner's limit of one stands in for it.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// CLASSIC: the quest events world objects fire when used, which the server cannot read from
/// their InteractableBehaviorTemplate.
/// </summary>
internal static class InteractableQuestEvents {

    private const string AmphitheatreTowers = "DragonSpire/DS_A3_Kings/Interiors/DS_Amphitheatre_T";

    private static readonly Dictionary<string, QuestEvent[]> s_questEventsByObject = new() {
        // CLASSIC: Hallowe'en 2009's Jack O'Lanterns fire one event per street (Pumpkin.xml's InteractableBehavior; the three
        // pumpkin templates share them); the Commons and the Shopping District both fire GetTreat.
        ["HO_Pumpkin"] = PumpkinEvents(),
        ["HO_Pumpkin02"] = PumpkinEvents(),
        ["HO_Pumpkin03"] = PumpkinEvents(),
        ["MS_SoulChainForge"] = [new("forgeSoulChain")], // MS-DTH2-C02-001 goal 3, in the Village of Sorrow
        // CLASSIC: the Tree of Life (MS_LifeTree.xml's InteractableBehavior fires "TreeUsed"); MS-DTH3-C03-002 goal 1 counts the
        // use, and the zone's OpenSpiritGate trigger opens the spirit-world portal on the same event (ZoneEventsFiredIn).
        ["MS_LifeTree"] = [new("TreeUsed", "MooShu/MS_Death/MS_Death_Zone3_AncientTree")],
        ["MS_AirShrine"] = [new("AirShrineUsed", "MooShu/MS_Plague/Interiors/MS_Plague2_T5")], // MS-PLAG2-C04-005 goal 2
        ["KT_MapRoomStaff"] = [new("UseStaff")], // KT-PYMHub-C01-005 goal 3, in the Throne Room of Fire
        ["DS_Crystal_SampleGrove2_006"] = [new("CollectCrystal_Grove2_6")], // DS-ACAD1-C01-002 goal 6, in the Crystal Grove
        ["DS_Desk"] = [new("UseTable")], // DS-ACAD1-C04-003 goal 2, in the Crystal Grove's first tower
        ["DS_CrystalStand_ACAD2-C01-001"] = [new("CrystalStandOne", AmphitheatreTowers + "6")], // DS-ACAD2-C01-001 goal 2
        ["DS_KnowledgeCrystal"] = [new("CrystalStandTwo", AmphitheatreTowers + "7")], // DS-ACAD2-C01-002 goal 2
        ["DS_CrystalStandGreenBlue"] = [new("CrystalStandThree", AmphitheatreTowers + "8")], // DS-ACAD2-C01-005 goal 2
        ["DS_ActivationCrystal_Yellow"] = [new("activateCrystalYellow")], // DS-LIB1-C01-002 goal 2, in the Tower Archives
        ["DS_ActivationCrystal_Orange"] = [new("ActivateCrystalOrange")], // DS-LIB1-C01-004 goal 2
        ["DS_ActivationCrystal_Green"] = [new("ActivateCrystalGreen")], // DS-LIB1-C01-006 goal 2
        ["DS_ActivationCrystal_Blue"] = [new("ActivateCrystalBlue")], // DS-LIB1-C01-008 goal 2
        ["DS_CrystalStandDSNECHatch"] = [new("UseCrystalStand3")], // DS-NEC1-C01-004 goal 2, in Windhammer's tower
        ["DS_DragonEgg"] = [new("EggHatch")], // DS-NEC1-C05-004 goal 2, in the Drake Hatchery
        // Arc 1 (r806919 Root.wad InteractableBehavior strings; each object's only placement is the goal's zone).
        ["Generic_Forge"] = [new("UseForge")], // KT-SPH2-C02-001 goal 2, in KT_ChampHall_T3
        ["MS_Mantra1Tablet"] = [new("Mantra1")], // MS-DTH1-C01-001, in the Burial Ground (its goal has a zone of its own)
        ["MS_Mantra2Tablet"] = [new("Mantra2")], // MS-DTH1-C01-002 goal 2, in MS_Death1_T1
        ["MS_Mantra3Tablet"] = [new("Mantra3")], // MS-DTH1-C01-003 goal 2, in MS_Death1_T2
        ["MS_BookPed"] = [new("TakeBook")], // MS-MAIN-C03-001 goal 3, in the Rock Dojo (MS_RockDojoT2)
        // Sunken City's book tower: the pedestal's behavior fires "DecayBook", which completes Finding A Way's
        // (WC-ST07-C02-001) "Goal" and makes the tower's own trigger play the Tome of Decay narration.
        ["WC_BookPedestal_SunkenCity"] = [new("DecayBook", "WizardCity/WC_Streets/Interiors/WC_Sunken_City_T2")],
        ["DS_Coffin"] = [new("TouchTomb")], // DS-NEC2-C01-009 goal 2, in the Necropolis crypt (5Room3_5)
        ["DS_CrystalStand_NEC1_C02_002"] = [new("UseCrystalStand6")], // DS-NEC1-C02-002 goal 3, in DS_Hatchery_T4
        ["DS_CrystalStand_C01-002"] = [new("UseEmptyCrystalPedestal")], // DS-NEC2-C01-002, in DS_A2Z2_Arena (its goal has a zone of its own)
        ["DS_CrystalStand_C01-003"] = [new("UseEmptyCrystalPedestalC01-003")], // DS-NEC2-C01-003 goal 2, in DS_A2Z2_Arena
        ["DS_CrystalStand_C01-005"] = [new("UseEmptyCrystalPedestalC01-005")], // DS-NEC2-C01-005 (zone of its own)
        ["DS_CrystalStand_C01-006"] = [new("UseEmptyCrystalPedestalC01-006")], // DS-NEC2-C01-006 goal 2, in DS_A2Z2_Arena
        ["DS_CrystalStand_C01-007"] = [new("UseEmptyCrystalPedestalC01-007")], // DS-NEC2-C01-007 (zone of its own)
        ["DS-ObsidianChest"] = [ // DS-ACAD-C01-001 goals 1, 3, 6, 7: one chest template, one event per zone
            new("GotObsidianChest1", "WizardCity/WC_Streets/Interiors/WC_HauntedCave_T1"),
            new("GotObsidianChest2", "WizardCity/WC_Streets/Interiors/WC_Colossus_ThroneRoom"),
            new("GotObsidianChest3", "Krokotopia/KT_Pyramid/Interiors/KT_Hall_T1"),
            new("GotObsidianChest4", "Krokotopia/KT_Krokosphinx/Interiors/KT_Arena_T1"),
            new("GotObsidianChest5", "Marleybone/MB_ScotlandYard/Interiors/MB_Prison_T5"),
            new("GotObsidianChest6", "Marleybone/MB_Station/Interiors/MB_ChelseaCourt_T6"),
            new("GotObsidianChest7", "MooShu/MS_Death/Interiors/MS_Death1_T4"),
            new("GotObsidianChest8", "MooShu/MS_Plague/Interiors/MS_Plague1_T3"),
            new("GotObsidianChest9", "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_T4"),
        ],
    };

    private static QuestEvent[] PumpkinEvents() => [
        new("GetTreat", "WizardCity/WC_Hub"), new("GetTreat", "WizardCity/WC_Shop_Area"),
        new("FirecatPumpkin", "WizardCity/WC_Streets/WC_Firecat"), new("UnicornPumpkin", "WizardCity/WC_Streets/WC_Unicorn"),
        new("TritonPumpkin", "WizardCity/WC_Streets/WC_Triton"), new("CyclopsPumpkin", "WizardCity/WC_Streets/WC_Cyclops"),
    ];

    // CLASSIC: the drop table an object's InteractableBehavior (InteractLoot) rolls for whoever uses it for a quest goal.
    private static readonly Dictionary<string, string> s_useLootByObject = new() {
        ["HO_Pumpkin"] = "HO-Gold-Pumpkins", ["HO_Pumpkin02"] = "HO-Gold-Pumpkins", ["HO_Pumpkin03"] = "HO-Gold-Pumpkins",
        ["HO_AppleTub01"] = "HO-AppleTub-01", ["HO_AppleTub02"] = "HO-AppleTub-02",
        ["HO_AppleTub03"] = "HO-AppleTub-03", ["HO_AppleTub04"] = "HO-AppleTub-04",
    };

    /// <summary>
    /// CLASSIC: the drop table using the object rolls (Hallowe'en pumpkins give gold, apple tubs gold and an item), or null.
    /// </summary>
    /// <param name="objectTemplate">The used object's template.</param>
    internal static string UseLootTable(GameObjectTemplate objectTemplate)
        => objectTemplate?.m_objectName is not null && s_useLootByObject.TryGetValue(objectTemplate.m_objectName, out var table) ? table : null;

    // CLASSIC: zone spawners an object's use starts (its InteractableBehavior's ResSpawn), with the
    // requirements that branch of the behavior checks.
    private static readonly Dictionary<string, UseSpawn[]> s_spawnsByObject = new() {
        ["DS_CrystalStandDSNECHatch"] = [ // Windhammer's tower (DS_Hatchery_T1): the drake DS-NEC1-NPC02
            new([812068], () => [HasEntry("QT-DS-NEC1-C01-004"), Not(HasQuest("DS-NEC1-C02-002"))]),
            new([812068], () => [HasQuest("DS-NEC1-C02-002"), GoalIncomplete("DS-NEC1-C02-002", "Goal")]),
        ],
    };

    /// <summary>
    /// CLASSIC: the events using the object posts to its zone, which zone triggers listen for (the Tree of Life's
    /// "TreeUsed" opens the spirit-world portal). The same names its quest goals count.
    /// </summary>
    /// <param name="objectTemplate">The used object's template.</param>
    /// <param name="zonePath">The zone the object is in.</param>
    internal static IReadOnlyList<string> ZoneEventsFiredIn(GameObjectTemplate objectTemplate, string zonePath)
        => ClassicQuestEngine.IsActive && objectTemplate?.m_objectName is not null
            && s_questEventsByObject.TryGetValue(objectTemplate.m_objectName, out var questEvents)
            ? [.. questEvents.Where(questEvent => questEvent.FiresIn(zonePath)).Select(questEvent => questEvent.Name).Distinct()]
            : [];

    /// <summary>
    /// CLASSIC: every event some object's use can post in the zone (the zone trigger plan's root events).
    /// </summary>
    /// <param name="zonePath">The zone.</param>
    internal static IEnumerable<string> ZoneEventsAnyObjectFiresIn(string zonePath)
        => s_questEventsByObject.Values.SelectMany(events => events)
            .Where(questEvent => questEvent.FiresIn(zonePath)).Select(questEvent => questEvent.Name).Distinct();

    /// <summary>
    /// Whether the object fires any quest event a goal can name.
    /// </summary>
    /// <param name="objectTemplate">The object's template.</param>
    internal static bool FiresQuestEvents(GameObjectTemplate objectTemplate)
        => objectTemplate?.m_objectName is not null
            && (s_questEventsByObject.ContainsKey(objectTemplate.m_objectName)
                || (ClassicQuestEngine.IsActive && s_spawnsByObject.ContainsKey(objectTemplate.m_objectName))); // CLASSIC: use-spawns.

    /// <summary>
    /// CLASSIC: the spawners using the object starts for a player, one list per behavior branch whose
    /// requirements the player meets.
    /// </summary>
    /// <param name="objectTemplate">The used object's template.</param>
    /// <param name="meets">Evaluates a branch's requirements for the player.</param>
    internal static IReadOnlyList<uint> SpawnsOnUse(GameObjectTemplate objectTemplate, Func<RequirementList, bool> meets) {
        if (!ClassicQuestEngine.IsActive || objectTemplate?.m_objectName is null
            || !s_spawnsByObject.TryGetValue(objectTemplate.m_objectName, out var branches)) {
            return [];
        }

        return branches
            .Where(branch => meets(branch.Requirements()))
            .SelectMany(branch => branch.SpawnerIds)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// CLASSIC: every spawner some object's use starts (so the dormant-spawner rule leaves them alone).
    /// </summary>
    internal static IEnumerable<uint> SpawnersStartedByObjects()
        => s_spawnsByObject.Values.SelectMany(branches => branches).SelectMany(branch => branch.SpawnerIds).Distinct();

    /// <summary>
    /// Whether using the object in the zone completes the goal: a waypoint goal without a zone or
    /// proximity trigger whose client tags name one of the quest events the object fires there.
    /// </summary>
    /// <param name="objectTemplate">The used object's template.</param>
    /// <param name="zonePath">The zone the object is in.</param>
    /// <param name="goal">The goal template.</param>
    internal static bool CompletesGoal(GameObjectTemplate objectTemplate, string zonePath, GoalTemplate goal)
        => goal is WaypointGoalTemplate waypoint
            && !HasOwnTrigger(waypoint)
            && objectTemplate?.m_objectName is not null
            && s_questEventsByObject.TryGetValue(objectTemplate.m_objectName, out var questEvents)
            && questEvents.Any(questEvent => questEvent.FiresIn(zonePath) && goal.m_clientTags?.Contains(questEvent.Name) == true);

    /// <summary>
    /// Whether some object's quest event completes the goal, a waypoint goal without a zone or
    /// proximity trigger.
    /// </summary>
    /// <param name="goal">The goal template.</param>
    internal static bool IsCompletedByEvent(GoalTemplate goal)
        => goal is WaypointGoalTemplate waypoint
            && !HasOwnTrigger(waypoint)
            && goal.m_clientTags?.Any(tag => s_questEventsByObject.Values.Any(questEvents => questEvents.Any(questEvent => questEvent.Name == tag))) == true;

    /// <summary>
    /// Adds every quest goal that using the object in the zone completes to the goals it is
    /// interactable for.
    /// </summary>
    /// <param name="objectTemplate">The object's template.</param>
    /// <param name="zonePath">The zone the object is in.</param>
    /// <param name="goalsByQuest">The object's goals, by quest name.</param>
    internal static void AddGoalsCompletedBy(GameObjectTemplate objectTemplate, string zonePath,
                                             Dictionary<string, List<GoalTemplate>> goalsByQuest) {
        if (!FiresQuestEvents(objectTemplate)) {
            return;
        }

        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            if (quest?.m_goals is null) {
                continue;
            }

            foreach (var goal in quest.m_goals.Where(goal => CompletesGoal(objectTemplate, zonePath, goal))) {
                if (!goalsByQuest.TryGetValue(quest.m_questName, out var goals)) {
                    goals = [];
                    goalsByQuest[quest.m_questName] = goals;
                }
                goals.Add(goal);
            }
        }
    }

    private static bool HasOwnTrigger(WaypointGoalTemplate waypoint)
        => !string.IsNullOrEmpty(waypoint.m_proximityTag)
            || (!string.IsNullOrEmpty(waypoint.m_zoneTag) && (waypoint.m_zoneEntry || waypoint.m_zoneExit));

    private static RequirementList All(params Requirement[] requirements)
        => new() { m_requirements = [.. requirements] };

    private static ReqHasEntry HasEntry(string entryName)
        => new() { m_entryName = entryName, m_operator = Operator.ROP_AND };

    private static ReqHasQuest HasQuest(string questName)
        => new() { m_questName = questName, m_operator = Operator.ROP_AND };

    private static ReqHasGoal GoalIncomplete(string questName, string goalName)
        => new() { m_questName = questName, m_goalName = goalName, m_requiredStatus = GoalStatusRequirement.Incomplete, m_operator = Operator.ROP_AND };

    private static T Not<T>(T requirement) where T : Requirement {
        requirement.m_applyNOT = true;

        return requirement;
    }

    private sealed record UseSpawn(uint[] SpawnerIds, Func<Requirement[]> Items) {

        // A fresh list per use: requirement records are mutable.
        public RequirementList Requirements() => All(Items());

    }

    private sealed record QuestEvent(string Name, string OnlyInZone = null) {

        public bool FiresIn(string zonePath)
            => OnlyInZone is null || string.Equals(OnlyInZone, zonePath, StringComparison.OrdinalIgnoreCase);

    }

}
