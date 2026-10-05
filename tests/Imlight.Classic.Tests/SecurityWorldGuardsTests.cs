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
 * SECURITY WORLD GUARDS TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the pure world checks from the 2026-10-04 security audit: minigame runs, interaction range, movement,
 * chat, combat targets, egg hatching, voluntary teleports, cooldowns, rate limits and the quest offer cache.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Text;
using Imlight.Classic.Security;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SecurityWorldGuardsTests {

    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    // ---- HIGH 1: minigame rewards ----

    [Fact]
    public void MinigameResultIsPaidOncePerRunAndNotTooQuick() {
        var paid = new Dictionary<ulong, DateTimeOffset>();
        var run = new MinigameRun();

        Assert.Equal(MinigameRewardDecision.NotStarted, run.Decide(T0, 7, paid));
        run.Start(T0);
        Assert.Equal(MinigameRewardDecision.TooQuick, run.Decide(T0.AddSeconds(5), 7, paid));
        Assert.Equal(MinigameRewardDecision.Reward, run.Decide(T0.AddSeconds(25), 7, paid));
        Assert.Equal(MinigameRewardDecision.AlreadyRewarded, run.Decide(T0.AddSeconds(26), 7, paid));
    }

    [Fact]
    public void MinigameCooldownSpansRunsOfOneWizardOnly() {
        var paid = new Dictionary<ulong, DateTimeOffset>();
        var first = new MinigameRun();
        first.Start(T0);
        Assert.Equal(MinigameRewardDecision.Reward, first.Decide(T0.AddSeconds(21), 7, paid));

        // Play again at once: a new run, but within the wizard's cooldown.
        var again = new MinigameRun();
        again.Start(T0.AddSeconds(21));
        Assert.Equal(MinigameRewardDecision.Cooldown, again.Decide(T0.AddSeconds(42), 7, paid));
        Assert.Equal(MinigameRewardDecision.Reward, again.Decide(T0.AddSeconds(52), 7, paid));

        var other = new MinigameRun();
        other.Start(T0);
        Assert.Equal(MinigameRewardDecision.Reward, other.Decide(T0.AddSeconds(42), 8, paid));
    }

    [Fact]
    public void MinigameScoreIsCappedAtTenTimesTheTopThreshold() {
        Assert.Equal(5000, MinigameRun.ClampScore(int.MaxValue, [100, 300, 500]));
        Assert.Equal(250, MinigameRun.ClampScore(250, [100, 300, 500]));
        Assert.Equal(0, MinigameRun.ClampScore(-50, [100]));
        Assert.Equal(MinigameRun.AbsoluteScoreCap, MinigameRun.ClampScore(int.MaxValue, null));
    }

    // ---- HIGH 3: interaction range ----

    [Fact]
    public void InteractionIsHonouredNearTheNpcOnly() {
        Assert.Equal(600, InteractionRange.Allowed(300));
        Assert.True(InteractionRange.Within(299 * 299, 300));
        Assert.True(InteractionRange.Within(590 * 590, 300)); // a click just before stepping out (lag)
        Assert.False(InteractionRange.Within(5000.0 * 5000.0, 300));
        Assert.False(InteractionRange.Within(double.NaN, 300));
    }

    // ---- MED 8: movement ----

    [Fact]
    public void RunningAndSpeedGearPass() {
        var guard = new MovementGuard();
        Assert.Equal(MoveVerdict.Accept, guard.Check(0, 0, 0, T0)); // first move anchors
        var x = 0.0;
        for (var i = 1; i <= 100; i++) {
            x += 1000 * 0.1; // 1000 u/s, well over the 600 u/s run speed, ten moves a second
            Assert.Equal(MoveVerdict.Accept, guard.Check(x, 0, 0, T0.AddSeconds(5 + (i * 0.1))));
        }
    }

    [Fact]
    public void ALagSpikeOfBufferedMovesPasses() {
        var guard = new MovementGuard();
        guard.Check(0, 0, 0, T0);
        guard.Check(0, 0, 0, T0.AddSeconds(6)); // out of the entry grace
        // Two seconds of nothing, then 2 s of 600 u/s running arrive in one burst.
        var at = T0.AddSeconds(8);
        for (var i = 1; i <= 20; i++) {
            Assert.Equal(MoveVerdict.Accept, guard.Check(i * 60, 0, 0, at.AddMilliseconds(i)));
        }
    }

    [Fact]
    public void ATeleportHackIsSnappedBackOnceThenDroppedQuietly() {
        var guard = new MovementGuard();
        guard.Check(100, 100, 0, T0);
        guard.Check(100, 100, 0, T0.AddSeconds(6));

        Assert.Equal(MoveVerdict.SnapBack, guard.Check(20000, 100, 0, T0.AddSeconds(6.1)));
        Assert.Equal((100.0, 100.0, 0.0), guard.Anchor);
        Assert.Equal(MoveVerdict.Drop, guard.Check(20000, 100, 0, T0.AddSeconds(6.2)));
        Assert.Equal(MoveVerdict.SnapBack, guard.Check(20000, 100, 0, T0.AddSeconds(7.5)));
    }

    [Fact]
    public void ManySmallHopsCannotOutrunTheBudget() {
        var guard = new MovementGuard();
        guard.Check(0, 0, 0, T0);
        guard.Check(0, 0, 0, T0.AddSeconds(6));
        var x = 0.0;
        var refused = false;
        for (var i = 1; i <= 200 && !refused; i++) {
            x += 300; // 300 units every 10 ms = 30000 u/s
            refused = guard.Check(x, 0, 0, T0.AddSeconds(6).AddMilliseconds(i * 10)) != MoveVerdict.Accept;
        }

        Assert.True(refused);
    }

    [Fact]
    public void AServerTeleportReanchorsAndDropsMovesStillInFlight() {
        var guard = new MovementGuard();
        guard.Check(0, 0, 0, T0);
        guard.Check(0, 0, 0, T0.AddSeconds(6));

        guard.ServerTeleport(50000, 0, 0, T0.AddSeconds(7)); // a recall across the zone
        Assert.Equal(MoveVerdict.Drop, guard.Check(10, 0, 0, T0.AddSeconds(7.1))); // sent before the client saw it
        Assert.Equal(MoveVerdict.Accept, guard.Check(50050, 0, 0, T0.AddSeconds(7.3)));
    }

    [Fact]
    public void AZoneChangeTrustsTheSpawnPoint() {
        var guard = new MovementGuard();
        guard.Check(0, 0, 0, T0);
        guard.Check(0, 0, 0, T0.AddSeconds(6));
        guard.Reset(T0.AddSeconds(10));
        Assert.Equal(MoveVerdict.Accept, guard.Check(90000, 0, 0, T0.AddSeconds(10.1)));
        Assert.Equal(MoveVerdict.Accept, guard.Check(-90000, 0, 0, T0.AddSeconds(10.2))); // grace: old-zone stragglers
    }

    // ---- MED 6/7: chat ----

    [Fact]
    public void RadialChatKeepsThePrefixFiltersMarkupAndCuts() {
        var raw = Encoding.ASCII.GetBytes("\u0001hi <color;ff0000>there\u0007!");
        var clean = ChatGuard.SanitizeRadial(raw);
        Assert.NotNull(clean);
        Assert.Equal(1, clean![0]);
        Assert.Equal("hi color;ff0000there!", Encoding.ASCII.GetString(clean, 1, clean.Length - 1));

        var longLine = new byte[2000];
        Array.Fill(longLine, (byte) 'a');
        Assert.Equal(1 + ChatGuard.MaxLength, ChatGuard.SanitizeRadial(longLine)!.Length);
    }

    [Fact]
    public void EmptyChatIsDroppedNotFatal() {
        Assert.Null(ChatGuard.SanitizeRadial(null));
        Assert.Null(ChatGuard.SanitizeRadial([]));
        Assert.Null(ChatGuard.SanitizeRadial([1])); // the prefix alone
        Assert.Null(ChatGuard.SanitizeRadial([1, (byte) ' ', (byte) '<']));
        Assert.Null(ChatGuard.SanitizeText(null));
        Assert.Null(ChatGuard.SanitizeText("  <> "));
        Assert.Equal("hello bfriend", ChatGuard.SanitizeText("hello <b>friend"));
        Assert.False(ChatGuard.AcceptsQuickChatExt([]));
        Assert.True(ChatGuard.AcceptsQuickChatExt([1, 2, 3]));
    }

    [Fact]
    public void ChatRateLimitAllowsABurstThenOneASecond() {
        var bucket = new TokenBucket(ChatGuard.RateBurst, ChatGuard.RatePerSecond);
        for (var i = 0; i < ChatGuard.RateBurst; i++) Assert.True(bucket.TryTake(T0));
        Assert.False(bucket.TryTake(T0));
        Assert.False(bucket.TryTake(T0.AddMilliseconds(500)));
        Assert.True(bucket.TryTake(T0.AddMilliseconds(1100)));
        Assert.False(bucket.TryTake(T0.AddMilliseconds(1200)));
    }

    // ---- MED 13: combat targets ----

    [Fact]
    public void ACombatTargetMustBeAnOccupiedCircle() {
        bool Occupied(int slot) => slot is 0 or 4;
        Assert.Equal(4, CombatTargets.Resolve(4, 8, Occupied));
        Assert.Equal(-1, CombatTargets.Resolve(5, 8, Occupied)); // empty circle
        Assert.Equal(-1, CombatTargets.Resolve(8, 8, Occupied)); // out of range
        Assert.Equal(-1, CombatTargets.Resolve(uint.MaxValue, 8, Occupied)); // "no target"
    }

    // ---- MED 5: hatch now ----

    [Fact]
    public void AnEggHatchesOnlyWhenItsTimerIsDone() {
        Assert.False(EggHatch.Ready(1_000, 999));
        Assert.True(EggHatch.Ready(1_000, 1_000));
    }

    // ---- MED 11/12, HIGH/MED 4: voluntary teleports ----

    [Fact]
    public void VoluntaryTeleportsAreRefusedInADuel() {
        Assert.Equal(TeleportRefusal.InDuel, VoluntaryTeleport.Check(inDuel: true));
        Assert.Equal(TeleportRefusal.None, VoluntaryTeleport.Check(inDuel: false));
        Assert.Equal(TeleportRefusal.InDuel, VoluntaryTeleport.CheckRecall(true, "WizardCity/WC_Hub"));
        Assert.Equal(TeleportRefusal.NoMark, VoluntaryTeleport.CheckRecall(false, ""));
        Assert.Equal(TeleportRefusal.None, VoluntaryTeleport.CheckRecall(false, "WizardCity/WC_Hub"));
        Assert.Equal(TeleportRefusal.MarkInInstance, VoluntaryTeleport.CheckMark(false, inInstance: true));
        Assert.Equal(TeleportRefusal.None, VoluntaryTeleport.CheckMark(false, inInstance: false));
        Assert.NotEmpty(VoluntaryTeleport.Message(TeleportRefusal.InDuel));
    }

    // ---- MED 9: cantrip cooldowns ----

    [Fact]
    public void ACantripCooldownIsTheServersOwn() {
        var cooldowns = new CooldownTracker();
        Assert.True(cooldowns.IsReady(42, T0));
        cooldowns.Start(42, T0, TimeSpan.FromSeconds(30));
        Assert.False(cooldowns.IsReady(42, T0.AddSeconds(29)));
        Assert.True(cooldowns.IsReady(43, T0.AddSeconds(1)));
        Assert.True(cooldowns.IsReady(42, T0.AddSeconds(30)));
    }

    // ---- LOW: quest offers ----

    [Fact]
    public void QuestOffersAreKeyedByNameAndCapped() {
        var offers = new BoundedOffers<string>(capacity: 3);
        offers.Add("a", "a1");
        offers.Add("a", "a2");
        Assert.Equal(1, offers.Count);
        Assert.Equal("a2", offers.Find("a"));
        offers.Add("b", "b");
        offers.Add("c", "c");
        offers.Add("d", "d");
        Assert.Equal(3, offers.Count);
        Assert.Null(offers.Find("a"));
        offers.Remove("d");
        Assert.Null(offers.Find("d"));
    }

}
