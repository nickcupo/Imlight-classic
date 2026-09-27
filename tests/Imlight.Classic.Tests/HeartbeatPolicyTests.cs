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
 * KINP SESSION TESTS
 * ========================================================================
 *
 * PURPOSE:
 * HeartbeatPolicy schedules from the ini, keeps a client that talks or is
 * loading a zone, and drops one that has gone silent.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * The timeline tests drive the policy as ControlService does: a tick every
 * Interval sends a heartbeat unless the last one's check is still pending,
 * and the check runs ResponseWait after the heartbeat.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using Imlight.Classic.Net;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class HeartbeatPolicyTests {

    private static readonly TimeSpan s_accepted = TimeSpan.FromHours(5);

    private static HeartbeatPolicy Stock() => new(intervalSeconds: 60, responseWaitSeconds: 15, zoneLoadWaitSeconds: 300);

    private static TimeSpan At(double seconds) => s_accepted + TimeSpan.FromSeconds(seconds);

    [Fact]
    public void StockSettingsScheduleEverySixtySecondsAndWaitFifteen() {
        var policy = Stock();

        Assert.True(policy.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.Interval);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.ResponseWait);
        Assert.Equal(TimeSpan.FromSeconds(300), policy.ZoneLoadWait);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(60, 0)]
    [InlineData(-1, 15)]
    public void ZeroIntervalOrWaitSchedulesNothing(int interval, int wait) {
        var policy = new HeartbeatPolicy(interval, wait, 300);

        Assert.False(policy.IsEnabled);
        Assert.True(policy.Interval >= TimeSpan.Zero);
    }

    [Fact]
    public void ClockNeverRunsBackwards() {
        var first = HeartbeatPolicy.Clock;
        var second = HeartbeatPolicy.Clock;

        Assert.True(first > TimeSpan.Zero);
        Assert.True(second >= first);
    }

    [Fact]
    public void AnyFrameAfterTheHeartbeatIsAnAnswer() {
        var policy = Stock();
        var sent = At(60);

        Assert.Equal(HeartbeatVerdict.Alive, policy.Judge(sent, At(63), At(75)));
        Assert.Equal(HeartbeatVerdict.Alive, policy.Judge(sent, sent, At(75)));
    }

    [Fact]
    public void SilenceSinceTheHeartbeatIsDead() {
        var policy = Stock();
        var sent = At(60);

        Assert.Equal(HeartbeatVerdict.Dead, policy.Judge(sent, At(59), At(75)));
        Assert.Equal(HeartbeatVerdict.Dead, policy.Judge(sent, TimeSpan.Zero, At(75)));
    }

    [Fact]
    public void SilenceWhileLoadingAZoneIsToleratedUntilTheZoneLoadWaitEnds() {
        var policy = Stock();
        policy.ZoneLoadStarted(At(2));
        var sent = At(60);

        Assert.Equal(HeartbeatVerdict.Loading, policy.Judge(sent, At(2), At(75)));
        Assert.Equal(HeartbeatVerdict.Loading, policy.Judge(sent, At(2), At(301)));
        Assert.Equal(HeartbeatVerdict.Dead, policy.Judge(sent, At(2), At(302)));
    }

    [Fact]
    public void FirstMoveEndsTheZoneLoadWait() {
        var policy = Stock();
        policy.ZoneLoadStarted(At(2));

        policy.ClientMoved();

        Assert.Null(policy.ZoneLoadStartedAt);
        Assert.Equal(HeartbeatVerdict.Dead, policy.Judge(At(60), At(40), At(75)));
    }

    [Fact]
    public void ClientThatSendsItsOwnKeepAlivesIsNeverDropped() {
        // It never answers the server's KeepAlive, but its own every 10 s is traffic.
        var dropped = Run(Stock(), minutes: 30, lastHeardAt: now => At(Math.Floor((now - s_accepted).TotalSeconds / 10) * 10));

        Assert.Null(dropped);
    }

    [Fact]
    public void ClientThatStopsIsDroppedWithinOneIntervalAndWait() {
        var policy = Stock();
        var stoppedAt = At(200);

        var dropped = Run(policy, minutes: 30, lastHeardAt: now => now < stoppedAt ? now : stoppedAt);

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - stoppedAt, TimeSpan.Zero, policy.Interval + policy.ResponseWait);
    }

    [Fact]
    public void ClientSilentForNinetySecondsWhileLoadingAZoneSurvives() {
        var loadStarted = At(5);
        var loaded = At(95);

        var dropped = Run(Stock(), minutes: 30, lastHeardAt: now => now < loaded ? loadStarted : now,
                          zoneLoadAt: loadStarted, movedAt: loaded);

        Assert.Null(dropped);
    }

    [Fact]
    public void ClientThatDiesWhileLoadingIsDroppedAfterTheZoneLoadWait() {
        var policy = Stock();
        var loadStarted = At(5);

        var dropped = Run(policy, minutes: 30, lastHeardAt: _ => loadStarted, zoneLoadAt: loadStarted);

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - loadStarted, policy.ZoneLoadWait, policy.ZoneLoadWait + policy.Interval + policy.ResponseWait);
    }

    [Fact]
    public void ClientThatLoadsMovesAndThenDiesIsDroppedWithoutTheZoneLoadWait() {
        var policy = Stock();
        var loadStarted = At(5);
        var diedAt = At(105);

        var dropped = Run(policy, minutes: 30, lastHeardAt: now => now < diedAt ? now : diedAt,
                          zoneLoadAt: loadStarted, movedAt: At(20));

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - diedAt, TimeSpan.Zero, policy.Interval + policy.ResponseWait);
    }

    [Fact]
    public void ClientThatNeverMovesKeepsTheZoneLoadWait() {
        var policy = Stock();
        var loadStarted = At(5);
        var diedAt = At(105);

        var dropped = Run(policy, minutes: 30, lastHeardAt: now => now < diedAt ? now : diedAt, zoneLoadAt: loadStarted);

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - loadStarted, policy.ZoneLoadWait, policy.ZoneLoadWait + policy.Interval + policy.ResponseWait);
    }

    [Theory]
    [InlineData(10, 15)]
    [InlineData(15, 15)]
    [InlineData(10, 45)]
    public void WaitNotBelowTheIntervalStillDrops(int interval, int wait) {
        var policy = new HeartbeatPolicy(interval, wait, 300);
        var stoppedAt = At(200);

        var dropped = Run(policy, minutes: 30, lastHeardAt: now => now < stoppedAt ? now : stoppedAt);

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - stoppedAt, policy.ResponseWait, 2 * (policy.Interval + policy.ResponseWait));
    }

    private static TimeSpan? Run(HeartbeatPolicy policy, int minutes, Func<TimeSpan, TimeSpan> lastHeardAt,
                                 TimeSpan? zoneLoadAt = null, TimeSpan? movedAt = null) {
        var sentAt = TimeSpan.Zero;
        TimeSpan? checkAt = null;
        var nextTick = s_accepted + policy.Interval;
        for (var now = s_accepted; now < s_accepted + TimeSpan.FromMinutes(minutes); now += TimeSpan.FromSeconds(1)) {
            if (now == zoneLoadAt) {
                policy.ZoneLoadStarted(now);
            }
            if (now == movedAt) {
                policy.ClientMoved();
            }

            if (now == checkAt) {
                checkAt = null;
                if (policy.Judge(sentAt, lastHeardAt(now), now) == HeartbeatVerdict.Dead) {
                    return now;
                }
            }

            if (now == nextTick) {
                nextTick += policy.Interval;
                if (checkAt is null) {
                    sentAt = now;
                    checkAt = now + policy.ResponseWait;
                }
            }
        }

        return null;
    }

}
