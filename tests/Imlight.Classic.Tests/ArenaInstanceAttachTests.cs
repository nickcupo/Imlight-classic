// CLASSIC: an arena match's fighters attach to the match's own copy of the arena, even when a public copy of the same
// zone is loaded (playbot-ios 2026-10-25: finbot04 had logged in to WizardCity/MB_Arena, so a public copy existed;
// both Ranked fighters then attached to it, the circle of the match's instance never saw them, and the match ended as
// a no contest after the arrival timeout). The attach after a server transfer carries only the instance owner
// (GroupInstances.OwnerForAttach), not IsPrivate, so GameWorld must ask the owner's container before the public copy.

using System;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.World;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaInstanceAttachTests {

    private static readonly DateTime T0 = new(2026, 10, 25, 1, 54, 25, DateTimeKind.Utc);

    [Fact]
    public void TheOwnersCopyComesBeforeAPublicOne() {
        // The bug: an attach for the match's run, with a public copy loaded and the run's copy in its container.
        Assert.False(GameWorld.UsePublicCopy(isPrivate: false, housingDeedId: 0, ownerHasZone: true));
        // No copy of the owner's: the public one, as before.
        Assert.True(GameWorld.UsePublicCopy(isPrivate: false, housingDeedId: 0, ownerHasZone: false));
    }

    [Fact]
    public void PrivateTransfersAndHousesNeverUseAPublicCopy() {
        Assert.False(GameWorld.UsePublicCopy(isPrivate: true, housingDeedId: 0, ownerHasZone: false));
        Assert.False(GameWorld.UsePublicCopy(isPrivate: false, housingDeedId: 42, ownerHasZone: false));
        Assert.False(GameWorld.UsePublicCopy(isPrivate: true, housingDeedId: 42, ownerHasZone: true));
    }

    [Fact]
    public void TheAttachAfterTheArenaTripNamesTheMatchsRun() {
        const ulong fighter = 4682065828241877905, run = 15031370212132900952;
        const string arena = "WizardCity/MB_Arena";
        // ZoneService queues the instance that answered the trip's transfer (its owner is the run id) ...
        GroupInstances.QueueEntry(fighter, arena, run, T0);
        // ... and the attach on the new connection uses it, so GameWorld looks in the run's container first.
        Assert.Equal(run, GroupInstances.OwnerForAttach(fighter, arena, T0.AddSeconds(1)));
    }
}
