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
 * The timeline tests drive the policy as ControlService does: a heartbeat
 * every Interval, judged ResponseWait later.
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

    private static readonly DateTimeOffset s_accepted = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly HeartbeatPolicy _stock = new(intervalSeconds: 60, responseWaitSeconds: 15, zoneLoadWaitSeconds: 300);

    [Fact]
    public void StockSettingsScheduleEverySixtySecondsAndWaitFifteen() {
        Assert.True(_stock.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(60), _stock.Interval);
        Assert.Equal(TimeSpan.FromSeconds(15), _stock.ResponseWait);
        Assert.Equal(TimeSpan.FromSeconds(300), _stock.ZoneLoadWait);
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
    public void AnyFrameAfterTheHeartbeatIsAnAnswer() {
        var sent = s_accepted.AddSeconds(60);

        Assert.Equal(HeartbeatVerdict.Alive, _stock.Judge(sent, sent.AddSeconds(3), null, sent.AddSeconds(15)));
        Assert.Equal(HeartbeatVerdict.Alive, _stock.Judge(sent, sent, null, sent.AddSeconds(15)));
    }

    [Fact]
    public void SilenceSinceTheHeartbeatIsDead() {
        var sent = s_accepted.AddSeconds(60);

        Assert.Equal(HeartbeatVerdict.Dead, _stock.Judge(sent, sent.AddSeconds(-1), null, sent.AddSeconds(15)));
        Assert.Equal(HeartbeatVerdict.Dead, _stock.Judge(sent, DateTimeOffset.MinValue, null, sent.AddSeconds(15)));
    }

    [Fact]
    public void SilenceWhileLoadingAZoneIsToleratedUntilTheZoneLoadWaitEnds() {
        var loadStarted = s_accepted.AddSeconds(2);
        var sent = s_accepted.AddSeconds(60);

        Assert.Equal(HeartbeatVerdict.Loading, _stock.Judge(sent, loadStarted, loadStarted, sent.AddSeconds(15)));
        Assert.Equal(HeartbeatVerdict.Loading, _stock.Judge(sent, loadStarted, loadStarted, loadStarted.AddSeconds(299)));
        Assert.Equal(HeartbeatVerdict.Dead, _stock.Judge(sent, loadStarted, loadStarted, loadStarted.AddSeconds(300)));
    }

    [Fact]
    public void ClientThatSendsItsOwnKeepAlivesIsNeverDropped() {
        // It never answers the server's KeepAlive, but its own every 10 s is traffic.
        var dropped = Run(minutes: 30, lastHeardAt: now => s_accepted.AddSeconds(Math.Floor((now - s_accepted).TotalSeconds / 10) * 10),
                          zoneLoadStartedAt: null);

        Assert.Null(dropped);
    }

    [Fact]
    public void ClientThatStopsIsDroppedWithinOneIntervalAndWait() {
        var stoppedAt = s_accepted.AddSeconds(200);

        var dropped = Run(minutes: 30, lastHeardAt: now => now < stoppedAt ? now : stoppedAt, zoneLoadStartedAt: null);

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - stoppedAt, TimeSpan.Zero, _stock.Interval + _stock.ResponseWait);
    }

    [Fact]
    public void ClientSilentForNinetySecondsWhileLoadingAZoneSurvives() {
        var loadStarted = s_accepted.AddSeconds(5);
        var loaded = loadStarted.AddSeconds(90);

        var dropped = Run(minutes: 30, lastHeardAt: now => now < loaded ? loadStarted : now, zoneLoadStartedAt: loadStarted);

        Assert.Null(dropped);
    }

    [Fact]
    public void ClientThatDiesWhileLoadingIsDroppedAfterTheZoneLoadWait() {
        var loadStarted = s_accepted.AddSeconds(5);

        var dropped = Run(minutes: 30, lastHeardAt: _ => loadStarted, zoneLoadStartedAt: loadStarted);

        Assert.NotNull(dropped);
        Assert.InRange(dropped.Value - loadStarted, _stock.ZoneLoadWait, _stock.ZoneLoadWait + _stock.Interval + _stock.ResponseWait);
    }

    private DateTimeOffset? Run(int minutes, Func<DateTimeOffset, DateTimeOffset> lastHeardAt, DateTimeOffset? zoneLoadStartedAt) {
        for (var sent = s_accepted + _stock.Interval; sent < s_accepted.AddMinutes(minutes); sent += _stock.Interval) {
            var judgedAt = sent + _stock.ResponseWait;
            if (_stock.Judge(sent, lastHeardAt(judgedAt), zoneLoadStartedAt, judgedAt) == HeartbeatVerdict.Dead) {
                return judgedAt;
            }
        }

        return null;
    }

}
