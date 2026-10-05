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
 * SIGIL ENTRY MATCH TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Which ZoneTransfer entry a dungeon sigil's tag picks (InteractDungeonSigilComponent.MatchEntranceTeleport).
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter SigilEntryMatchTests
 *
 * NOTE:
 * Found by the datamine-fixes playbot: Colossus Boulevard's "Street 6 Tower 1 Instance Sigil" took the entry of
 * "Street 6 CustTower 1 Instance Sigil" (listed first, and "tower1" is inside "custtower1").
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SigilEntryMatchTests : IDisposable {

    private static WizardZoneData Colossus() => new() {
        ZoneName = "WizardCity/WC_Streets/WC_Colossus",
        Teleports = [
            Entry("Street 6 CustTower 1 Instance Sigil", "WizardCity/WC_Streets/Interiors/WC_Colossus_T1"),
            Entry("Street 6 Tower 1 Instance Sigil", "WizardCity/WC_Streets/Interiors/WC_Colossus_T2"),
            Entry("Street 6 Tower 2 Instance Sigil", "WizardCity/WC_Streets/Interiors/WC_Colossus_T3"),
        ],
    };

    private static WizardTeleportData Entry(string trigger, string zone)
        => new() { TriggerName = trigger, Teleport = new ResTeleport { m_destinationZone = zone } };

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    public void ASigilTakesTheEntryNamedExactlyLikeIt(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));

        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Colossus_T2",
            InteractDungeonSigilComponent.MatchEntranceTeleport("Street 6 Tower 1 Instance Sigil", Colossus())?.Teleport?.m_destinationZone);
        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Colossus_T1",
            InteractDungeonSigilComponent.MatchEntranceTeleport("Street 6 CustTower 1 Instance Sigil", Colossus())?.Teleport?.m_destinationZone);
        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Colossus_T3",
            InteractDungeonSigilComponent.MatchEntranceTeleport("Street 6 Tower 2 Instance Sigil", Colossus())?.Teleport?.m_destinationZone);
    }

    [Fact]
    public void TheStreetTowerMatchStillFindsAnEntryNamedDifferently() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var zone = new WizardZoneData { Teleports = [Entry("Teleport Street 4 Tower 2 Entrance", "WizardCity/WC_Streets/Interiors/WC_Triton_T2")] };

        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Triton_T2",
            InteractDungeonSigilComponent.MatchEntranceTeleport("Street 4 Tower 2 Instance Sigil", zone)?.Teleport?.m_destinationZone);
    }

    [Fact]
    public void WithoutTheClassicQuestRulesTheStockMatchIsKept() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicRules.Stock);

        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_Colossus_T1",
            InteractDungeonSigilComponent.MatchEntranceTeleport("Street 6 Tower 1 Instance Sigil", Colossus())?.Teleport?.m_destinationZone);
    }

}
