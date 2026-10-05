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
 * INSTANCE RESET TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 dungeon reset rule (classic-data/rules/instance-resets-2009.yaml,
 * Classic.Travel.InstanceGroups, CoreLib.Classic.InstanceResets): one test
 * per rule, and a regression over the instance-group list.
 *
 * NOTE:
 * The live path (kill a boss, leave, come back; walk between rooms; two
 * grouped wizards) ran on a rig; see playbot-reports/instance-reset.md.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Classic.Travel;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class InstanceResetRuleTests {

    private static readonly DateTime T0 = new(2009, 6, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    internal static InstanceResetRules Load()
        => InstanceResetRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "instance-resets-2009.yaml"));

    private static readonly InstanceGroup Golem = InstanceResetRules.BuiltIn.Groups[0];
    private static readonly InstanceGroup Sunken = new("WC_Sunken_City", InstanceKind.Dungeon, [
        "WizardCity/WC_Streets/WC_Sunken_City", "WizardCity/WC_Streets/Interiors/WC_Sunken_City_T1"]);

    private const ulong Me = 11, Friend = 22;
    private const string Outside = "WizardCity/WC_NightSide";

    // ---- the file ----

    [Fact]
    public void TheRulesFileLoadsWithThe2009Windows() {
        var rules = Load();

        Assert.Equal("instance-resets-2009", rules.Id);
        Assert.Contains("late-2009", rules.Profiles);
        Assert.Equal(TimeSpan.FromMinutes(30), rules.EmptyLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), rules.ReturnWindow);
    }

    [Fact]
    public void TheGauntletsAreTheFourDatedOnes() {
        var gauntlets = Load().Groups.Where(g => g.Kind == InstanceKind.Gauntlet).Select(g => g.Id).Order().ToArray();

        Assert.Equal(["DS_Hatchery_Gauntlet_8Room", "KT_Hall_T2", "WC_Gauntlet_01", "WC_Golem_Tower"], gauntlets);
    }

    [Fact]
    public void TheGolemTowerIsTheSameFiveFloorsAsBefore() {
        var golem = Load().GroupOf("WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_3")!;

        Assert.Equal(InstanceKind.Gauntlet, golem.Kind);
        Assert.Equal(InstanceResetRules.GolemTowerFloors, golem.Zones);
    }

    // Regression over the instance-group list: the multi-room dungeons of the six worlds and their room counts.
    [Theory]
    [InlineData("WizardCity/Gauntlets/WC_Gauntlet_01/Room01", "WC_Gauntlet_01", 11)]          // Briskbreeze Tower
    [InlineData("WizardCity/WC_Streets/WC_Sunken_City", "WC_Sunken_City", 4)]
    [InlineData("WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_5", "WC_Golem_Tower", 5)]
    [InlineData("Krokotopia/KT_Krokosphinx/KT_Retreat", "KT_Retreat", 4)]                       // Emperor's Retreat
    [InlineData("Krokotopia/KT_Krokosphinx/KT_Vault", "KT_Vault", 2)]                           // Vault of Ice
    [InlineData("Krokotopia/KT_Tomb/Interiors/KT_Crypt06_Map00_Storm", "KT_Crypt06_Map00_Storm", 4)]
    [InlineData("Marleybone/MB_ScotlandYard/MB_KatzLab", "MB_KatzLab", 5)]
    [InlineData("Marleybone/MB_Station/MB_Ironworks", "MB_Ironworks", 6)]
    [InlineData("MooShu/MS_Plague/MS_Plague_Zone3_CliffsidePalace", "MS_Plague_Zone3_CliffsidePalace", 8)]
    [InlineData("MooShu/MS_War/MS_War_BattlefieldA", "MS_War_BattlefieldA", 3)]
    [InlineData("MooShu/MS_Death/MS_Death_Zone3_AncientTree", "MS_Death_Zone3_AncientTree", 5)]
    [InlineData("DragonSpire/DS_A3_Kings/Interiors/DS_MalistaireLair", "DS_Volcano1", 9)]
    [InlineData("DragonSpire/DS_A2_Battle/DS_A2Z3_Detention", "DS_A2Z3_Detention", 9)]
    [InlineData("DragonSpire/DS_A2_Battle/Interiors/DS_Hatchery_Gauntlet_8Room", "DS_Hatchery_Gauntlet_8Room", 4)] // Secure House
    [InlineData("Grizzleheim/Interiors/GH_RedClaw_End", "GH_RedClaw_T1", 2)]
    public void MultiRoomDungeonsAreOneGroup(string zone, string id, int rooms) {
        var group = Load().GroupOf(zone);

        Assert.NotNull(group);
        Assert.Equal(id, group.Id);
        Assert.Equal(rooms, group.Zones.Length);
    }

    [Fact]
    public void TheListCoversTheClassicWorldsAndEveryZoneOnce() {
        var rules = Load();
        var zones = rules.Groups.SelectMany(g => g.Zones).ToArray();

        Assert.True(rules.Groups.Length > 600, $"only {rules.Groups.Length} groups");
        Assert.True(rules.Groups.Count(g => g.Zones.Length > 1) >= 50);
        Assert.Equal(zones.Length, zones.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(zones, z => Assert.Matches("^(WizardCity|Krokotopia|Marleybone|MooShu|DragonSpire|Grizzleheim|GrizzleheimLite)/", z));
    }

    [Theory]
    [InlineData("WizardCity/WC_Hub")]                                 // hubs and streets are public
    [InlineData("WizardCity/WC_Golem_Tower")]                         // Golem Court, outside the tower
    [InlineData("WizardCity/WC_Streets/WC_Colossus")]
    [InlineData("WizardCity/Interiors/WC_Housing_Dorm_Interior")]     // the dorm keeps its own rules
    [InlineData("WizardCity/WC_Duel_Arena")]
    [InlineData("WizardCity/Tutorial_Interior")]
    public void PublicZonesTheDormAndTheTutorialAreNotDungeons(string zone) => Assert.Null(Load().GroupOf(zone));

    // ---- the rules ----

    [Fact]
    public void AGauntletStartsFreshOnEveryTripInFromOutside() {
        var leftHome = InstanceResetPolicy.Departure(Golem, Me, DepartureWay.Elsewhere, T0);

        Assert.False(leftHome.Resumable);
        Assert.True(InstanceResetPolicy.ResetsOnEntry(Golem, "WizardCity/WC_Golem_Tower", true, false, leftHome, Me, T0.AddMinutes(1), Window));
    }

    [Fact]
    public void MovingBetweenRoomsIsNotLeaving() {
        Assert.False(InstanceResetPolicy.ResetsOnEntry(Golem, Golem.Zones[1], true, false, null, Me, T0, Window));
        Assert.False(InstanceResetPolicy.Leaves(Golem, Golem.Zones[2]));
        Assert.True(InstanceResetPolicy.Leaves(Golem, "WizardCity/WC_Golem_Tower"));
    }

    [Fact]
    public void LeavingADungeonThroughItsEntranceResetsIt() {
        var exit = InstanceResetPolicy.Departure(Sunken, Me, DepartureWay.Exit, T0);

        Assert.True(InstanceResetPolicy.ResetsOnEntry(Sunken, Outside, true, false, exit, Me, T0.AddMinutes(1), Window));
    }

    [Fact]
    public void LeavingADungeonAnotherWayKeepsItForThirtyMinutes() {
        var home = InstanceResetPolicy.Departure(Sunken, Me, DepartureWay.Elsewhere, T0);

        Assert.True(home.Resumable);
        Assert.False(InstanceResetPolicy.ResetsOnEntry(Sunken, Outside, true, false, home, Me, T0.AddMinutes(29), Window));
        Assert.True(InstanceResetPolicy.ResetsOnEntry(Sunken, Outside, true, false, home, Me, T0.AddMinutes(31), Window));
    }

    [Fact]
    public void ADungeonIsNeverResetUnderSomeoneStillInside() {
        Assert.False(InstanceResetPolicy.ResetsOnEntry(Golem, "WizardCity/WC_Golem_Tower", true, occupied: true, null, Me, T0, Window));
        Assert.False(InstanceResetPolicy.ResetsOnLogin(Sunken, true, occupied: true));
    }

    [Fact]
    public void AFriendsCopyOrASigilRunIsJoinedAsItIs()
        => Assert.False(InstanceResetPolicy.ResetsOnEntry(Sunken, Outside, ownCopy: false, false, null, Friend, T0, Window));

    [Fact]
    public void LoggingOutResetsTheDungeon() {
        Assert.True(InstanceResetPolicy.ResetsOnLogin(Sunken, ownCopy: true, occupied: false));
        Assert.False(InstanceResetPolicy.ResetsOnLogin(null, true, false));
    }

    [Fact]
    public void AReturnIsOnlyToTheSameCopyOfTheSameDungeon() {
        var home = InstanceResetPolicy.Departure(Sunken, 777, DepartureWay.Elsewhere, T0);

        Assert.True(InstanceResetPolicy.CanResume(home, Sunken, 777, T0.AddMinutes(5), Window));
        Assert.False(InstanceResetPolicy.CanResume(home, Sunken, 778, T0.AddMinutes(5), Window));
        Assert.False(InstanceResetPolicy.CanResume(home, Golem, 777, T0.AddMinutes(5), Window));
    }

}

/// <summary>The live side (CoreLib.Classic.InstanceResets) over the online-player list, held seats and runs.</summary>
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class InstanceResetRuntimeTests : IDisposable {

    private const ulong A = 0x5EED0001, B = 0x5EED0002, AccountB = 0x5EEDA002;
    private const string Court = "WizardCity/WC_Golem_Tower";
    private const string Floor1 = "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_1";
    private const string Floor3 = "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_3";
    private const string Street = "WizardCity/WC_NightSide";
    private const string SunkenOut = "WizardCity/WC_Streets/WC_Sunken_City";
    private const string SunkenIn = "WizardCity/WC_Streets/Interiors/WC_Sunken_City_T1";
    private const string KatzLab = "Marleybone/MB_ScotlandYard/MB_KatzLab";

    public InstanceResetRuntimeTests() {
        InstanceGroups.Use(InstanceResetRuleTests.Load());
        InstanceResets.ClearForTests();
    }

    public void Dispose() {
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(AccountB);
        InstanceResets.ClearForTests();
        ActiveDuels.Release(A);
        ActiveDuels.Release(B);
        foreach (var run in _runs) {
            GroupInstances.EndRun(run);
        }
    }

    private readonly System.Collections.Generic.List<ulong> _runs = [];

    private ulong NewRun(DateTime now) {
        var run = NewRun(now);
        _runs.Add(run);

        return run;
    }

    private static void Inside(ulong charId, ulong account, string zone, ulong owner)
        => OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            AccountId = account, CharacterId = charId, CurrentZone = zone, InstanceOwnerId = owner,
        });

    [Fact]
    public void ATripBackIntoTheTowerStartsFresh() {
        var now = DateTime.UtcNow;
        InstanceResets.NoteTransfer(A, Floor1, A, Court, 0, keepInstance: true, now);

        Assert.True(InstanceResets.ResetOnEntry(A, Court, Floor1, A, now.AddMinutes(1)));
        Assert.False(InstanceResets.ResetOnEntry(A, Floor1, Floor3, A, now.AddMinutes(1)));
    }

    [Fact]
    public void AGroupedWizardStillInsideKeepsTheCopy() {
        var now = DateTime.UtcNow;
        Inside(B, AccountB, SunkenIn, A);   // B is in A's copy, A walked out and comes back

        Assert.False(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now));

        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(AccountB);
        Assert.True(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now));
    }

    [Fact]
    public void AWizardOnTheWayIntoTheCopyCountsAsInside() {
        var now = DateTime.UtcNow;
        InstanceResets.NoteTransfer(B, SunkenOut, A, SunkenIn, A, keepInstance: true, now);   // B between rooms

        Assert.False(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now));
    }

    [Fact]
    public void AHeldSeatInAFightKeepsTheCopyAndItsLogin() {
        var now = DateTime.UtcNow;
        ActiveDuels.Hold(new HeldSeat(A, Floor3, A, now.AddMinutes(5)));

        Assert.False(InstanceResets.ResetOnLogin(A, Floor3, A, now));     // a dropped connection rejoins its fight
        Assert.True(InstanceResets.ResetOnEntry(A, Court, Floor1, A, now));       // walked out of it: a new trip

        ActiveDuels.Hold(new HeldSeat(B, Floor3, A, now.AddMinutes(5)));      // a grouped wizard's fight is not wiped
        Assert.False(InstanceResets.ResetOnEntry(A, Court, Floor1, A, now));
        ActiveDuels.Release(B);
    }

    [Fact]
    public void ALoginInsideADungeonResetsItButTheAttachAfterATransferDoesNot() {
        var now = DateTime.UtcNow;
        Assert.True(InstanceResets.ResetOnLogin(A, Floor3, A, now));

        InstanceResets.NoteTransfer(A, Floor1, A, Floor3, A, keepInstance: true, now);
        Assert.False(InstanceResets.ResetOnLogin(A, Floor3, A, now));
    }

    [Fact]
    public void LeavingTheEntranceRecordsAnExitAndGoHomeARecordToReturnTo() {
        var now = DateTime.UtcNow;
        InstanceResets.NoteTransfer(A, SunkenIn, A, SunkenOut, A, keepInstance: true, now);   // a room move
        Assert.Null(InstanceResets.DepartureOf(A));

        InstanceResets.NoteTransfer(A, SunkenOut, A, Street, 0, keepInstance: true, now);     // the dungeon's exit
        Assert.False(InstanceResets.DepartureOf(A)!.Resumable);
        Assert.True(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now));

        InstanceResets.NoteTransfer(A, SunkenIn, A, "WizardCity/WC_Hub", 0, keepInstance: false, now);   // Go Home
        Assert.True(InstanceResets.DepartureOf(A)!.Resumable);
        Assert.False(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now.AddMinutes(10)));
        Assert.True(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now.AddMinutes(31)));
    }

    [Fact]
    public void GoingToAnotherDungeonForgetsTheOldOne() {
        var now = DateTime.UtcNow;
        InstanceResets.NoteTransfer(A, SunkenIn, A, "WizardCity/WC_Hub", 0, keepInstance: false, now);
        InstanceResets.NoteTransfer(A, "Marleybone/MB_ScotlandYard/MB_Roof", 0, KatzLab, A, keepInstance: false, now);

        Assert.Null(InstanceResets.DepartureOf(A));
        Assert.True(InstanceResets.ResetOnEntry(A, Street, SunkenOut, A, now));
    }

    [Fact]
    public void ASigilTakesTheWizardBackToTheRunTheyLeftAnotherWay() {
        var now = DateTime.UtcNow;
        var run = NewRun(now);
        InstanceResets.NoteTransfer(A, SunkenIn, run, "WizardCity/WC_Hub", 0, keepInstance: false, now);

        Assert.Equal(run, InstanceResets.ResumableRun(A, SunkenOut, now.AddMinutes(5)));
        Assert.Equal(0UL, InstanceResets.ResumableRun(A, KatzLab, now.AddMinutes(5)));
        Assert.Equal(0UL, InstanceResets.ResumableRun(A, SunkenOut, now.AddMinutes(31)));

        GroupInstances.EndRun(run);   // the run's copy expired
        Assert.Equal(0UL, InstanceResets.ResumableRun(A, SunkenOut, now.AddMinutes(5)));
    }

    [Fact]
    public void ASigilRunLeftThroughTheExitIsNotResumed() {
        var now = DateTime.UtcNow;
        var run = NewRun(now);
        InstanceResets.NoteTransfer(A, SunkenOut, run, Street, 0, keepInstance: true, now);

        Assert.Equal(0UL, InstanceResets.ResumableRun(A, SunkenOut, now.AddMinutes(1)));
    }

    [Fact]
    public void AResetDropsEveryZoneOfTheDungeon() {
        Assert.Equal(11, InstanceGroups.ZonesOf("WizardCity/Gauntlets/WC_Gauntlet_01/Room07").Length);
        Assert.True(InstanceGroups.SameGroup("WizardCity/Gauntlets/WC_Gauntlet_01/Room01", "WizardCity/Gauntlets/WC_Gauntlet_01/Room02"));
        Assert.Equal("WizardCity/WC_Hub", InstanceGroups.GroupKey("WizardCity/WC_Hub"));
    }

}
