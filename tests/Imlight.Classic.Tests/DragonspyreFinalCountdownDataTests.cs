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
 * DRAGONSPYRE FINAL COUNTDOWN DATA
 * ========================================================================
 *
 * PURPOSE:
 * Pins the two data records The Final Countdown needs to be finished: the Volcano door into Malistaire's Lair
 * and the event that lights the portal back to Merle Ambrose.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * Found by the Dragonspyre playthrough (playbot-ds): the client file's Volcano door only opens on UpperInteract
 * (nothing posts it) and the lair portal on CLIENTEVENT.SummonPortal (only a client cinematic posts it, and the
 * server never lets a client fire a trigger).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DragonspyreFinalCountdownDataTests {

    private const string Volcano2 = "DragonSpire-DS_A3_Kings-DS_A3Z3_Volcano-DS_Volcano2";
    private const string Lair = "DragonSpire/DS_A3_Kings/Interiors/DS_MalistaireLair";

    private static JObject Load(params string[] path)
        => JObject.Parse(File.ReadAllText(Path.Combine([ClassicDataFixture.Root, "spiraldb-overlay", .. path])));

    [Fact]
    public void TheVolcanoDoorTeleportsIntoTheLairWhileTheQuestIsHeld() {
        var door = Load("ZoneTransfer", Volcano2 + ".travel.json")["Teleports"]!
            .Single(t => (string?) t["TriggerName"] == "Trigger-LockedDoor")["Teleport"]!;

        Assert.Equal(Lair, (string?) door["m_destinationZone"]);
        Assert.Contains("QT-DS-ACAD-C01-005", door["m_requirements"]!.ToString());
    }

    [Fact]
    public void DefeatingMalistairePostsTheEventThatLightsTheAmbrosePortal() {
        var goal3 = Load("QuestTemplates", "DS-ACAD-C01-005.json")["m_goals"]!
            .Single(g => (string?) g["m_goalName"] == "Goal 3");

        Assert.Contains(goal3["m_completeResults"]!["m_results"]!,
            result => ((string?) result["$type"])!.Contains("ResPostEvent")
                && (string?) result["m_eventName"] == "CLIENTEVENT.SummonPortal");
    }

}
