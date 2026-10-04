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
 * GROUP INSTANCE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 dungeon rules (Classic.GroupInstances): a sigil's group of up to
 * four shares one new instance, an instance holds four, a friend is refused a
 * full one, doors inside an instance keep the group together, and the attach
 * after a zone change joins the instance that answered.
 *
 * NOTE:
 * The live path (two bots on one sigil, a late joiner, teleport-to-friend)
 * was run with the headless client on a rig; see playbot-reports/group.md.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class GroupInstancesTests {

    private static readonly DateTime T0 = new(2009, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WizardsOnOneSigilDuringItsCountdownShareTheRunAndGetTheirOwnFaces() {
        var group = new SigilGroup(4242, T0, GroupInstances.SigilCountdownSeconds);

        var first = group.Join(1, T0)!;
        var second = group.Join(2, T0.AddSeconds(4))!;

        Assert.Equal(4242UL, first.RunId);
        Assert.Equal(4242UL, second.RunId);
        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(0, first.Slot);
        Assert.Equal(1, second.Slot);
        // The late joiner enters with the group: the time left, not a fresh countdown.
        Assert.Equal(10.0, first.SecondsLeft, 3);
        Assert.Equal(6.0, second.SecondsLeft, 3);
    }

    [Fact]
    public void UsingTheSigilAgainKeepsTheSameFace() {
        var group = new SigilGroup(7, T0, 10);
        group.Join(1, T0);
        group.Join(2, T0.AddSeconds(1));

        var again = group.Join(1, T0.AddSeconds(2))!;

        Assert.Equal(0, again.Slot);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void AFifthWizardIsNotTakenIn() {
        var group = new SigilGroup(7, T0, 10);
        for (ulong id = 1; id <= 4; id++) {
            Assert.NotNull(group.Join(id, T0.AddSeconds(id)));
        }

        Assert.Null(group.Join(5, T0.AddSeconds(5)));
        Assert.Equal(GroupInstances.MaxGroupSize, group.Members.Count);
    }

    [Fact]
    public void TheGroupClosesWhenTheCountdownReachesZero() {
        var group = new SigilGroup(7, T0, 10);
        group.Join(1, T0);

        Assert.True(group.IsOpen(T0.AddSeconds(9.9)));
        Assert.False(group.IsOpen(T0.AddSeconds(10)));
        Assert.Null(group.Join(2, T0.AddSeconds(10)));
    }

    [Fact]
    public void RunIdsAreKnownUntilTheirInstanceEnds() {
        var run = GroupInstances.NewRunId(T0);

        Assert.NotEqual(0UL, run);
        Assert.True(GroupInstances.IsRun(run));
        Assert.NotEqual(run, GroupInstances.NewRunId(T0));
        Assert.False(GroupInstances.IsRun(0));

        GroupInstances.EndRun(run);
        Assert.False(GroupInstances.IsRun(run));
    }

    [Theory]
    [InlineData(0, 4)]   // no limit in the data: four
    [InlineData(1, 1)]   // a one-wizard gauntlet room
    [InlineData(4, 4)]
    [InlineData(12, 4)]  // the zone allows more; a 2009 instance still holds four
    [InlineData(100, 4)]
    public void AnInstanceHoldsFourOrItsLowerHardLimit(int hardLimit, int capacity)
        => Assert.Equal(capacity, GroupInstances.Capacity(hardLimit));

    [Fact]
    public void AFriendIsRefusedOnlyWhenTheInstanceIsFull() {
        Assert.False(GroupInstances.IsFull(3, 12));
        Assert.True(GroupInstances.IsFull(4, 12));
        Assert.True(GroupInstances.IsFull(1, 1));
        Assert.Equal("Your friend is in a full instance.", GroupInstances.FullInstanceMessage);
    }

    [Fact]
    public void DoorsInsideAnInstanceKeepTheInstanceAndOtherTransfersKeepTheirOwner() {
        // A door inside a sigil run leads to that run's next zone, whoever uses it.
        Assert.Equal(900UL, GroupInstances.OwnerForTransfer(requestedOwner: 5, keepInstance: true, currentInstanceOwner: 900));
        // In a public zone a door uses the wizard's own instance (a gate dungeon), as before.
        Assert.Equal(5UL, GroupInstances.OwnerForTransfer(5, keepInstance: true, currentInstanceOwner: 0));
        // Going home, a world door or a friend teleport names its own target.
        Assert.Equal(5UL, GroupInstances.OwnerForTransfer(5, keepInstance: false, currentInstanceOwner: 900));
    }

    [Fact]
    public void TheAttachAfterAZoneChangeJoinsTheInstanceThatAnswered() {
        const ulong wizard = 0xA11CE_0001;
        const string zone = "WizardCity/Interiors/WC_Streets_Tower_T1";

        GroupInstances.QueueEntry(wizard, zone, ownerId: 777, T0);

        // An attach into another zone ignores (and drops) the entry; an entry is used once.
        Assert.Equal(wizard, GroupInstances.OwnerForAttach(wizard, "WizardCity/WC_Hub", T0.AddSeconds(5)));
        GroupInstances.QueueEntry(wizard, zone, ownerId: 777, T0);
        Assert.Equal(777UL, GroupInstances.OwnerForAttach(wizard, zone, T0.AddSeconds(5)));
        Assert.Equal(wizard, GroupInstances.OwnerForAttach(wizard, zone, T0.AddSeconds(6)));
    }

    [Fact]
    public void AStaleOrOwnEntryLeavesTheWizardInTheirOwnInstance() {
        const ulong wizard = 0xA11CE_0002;
        const string zone = "Krokotopia/KT_Tomb_T1";

        GroupInstances.QueueEntry(wizard, zone, ownerId: 777, T0);
        Assert.Equal(wizard, GroupInstances.OwnerForAttach(wizard, zone, T0 + GroupInstances.PendingEntryLifetime));

        GroupInstances.QueueEntry(wizard, zone, ownerId: 777, T0);
        GroupInstances.QueueEntry(wizard, zone, ownerId: 0, T0); // a public destination replaces it
        Assert.Equal(wizard, GroupInstances.OwnerForAttach(wizard, zone, T0.AddSeconds(1)));
    }

    [Fact]
    public void AHeldDuelSeatStillRoutesALogin() {
        const ulong wizard = 0xA11CE_0003;
        const string zone = "MooShu/MS_Tower_T2";
        ActiveDuels.Hold(new HeldSeat(wizard, zone, 31337, DateTime.UtcNow.AddMinutes(2)));
        try {
            Assert.Equal(31337UL, GroupInstances.OwnerForAttach(wizard, zone, DateTime.UtcNow));
        }
        finally {
            ActiveDuels.Release(wizard);
        }
    }

    [Fact]
    public void PlayersAreCountedPerInstanceNotPerZoneName() {
        const string zone = "WizardCity/Interiors/WC_Streets_Tower_T1";
        var players = new[] {
            (Zone: zone, Owner: 900UL),
            (Zone: zone, Owner: 900UL),
            (Zone: zone, Owner: 901UL), // another group's copy of the same dungeon
            (Zone: "WizardCity/WC_Hub", Owner: 0UL),
        };

        Assert.Equal(2, GroupInstances.CountIn(players, p => p.Zone, p => p.Owner, zone, 900));
        Assert.Equal(1, GroupInstances.CountIn(players, p => p.Zone, p => p.Owner, zone, 901));
    }

    [Fact]
    public void TheMessagesCarryTheGroupFields() {
        var entry = new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY { RunId = 9, Slot = 2, CountdownSeconds = 3.5 };
        var transfer = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER { KeepInstance = true, RefuseWhenFull = true };
        var answer = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { InstanceOwnerId = 9, ZoneHardLimit = 4 };

        Assert.Equal((9UL, 2, 3.5), (entry.RunId, entry.Slot, entry.CountdownSeconds));
        Assert.True(transfer.KeepInstance && transfer.RefuseWhenFull);
        Assert.Equal((9UL, 4), (answer.InstanceOwnerId, answer.ZoneHardLimit));
    }

}
