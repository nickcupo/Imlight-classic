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
 * SAFE RESTART PLAN TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The warning countdown, the wait for fights and its hard cap.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Admin;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class RestartPlanTests {

    private static readonly DateTime s_start = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FiveMinuteWarningCountsDownByTheMinuteThenSeconds() {
        var plan = new RestartPlan(s_start, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), RestartKind.Restart, null);
        var said = new List<string>();
        for (var second = 0; second <= 300; second++) {
            said.AddRange(plan.DueWarnings(s_start.AddSeconds(second)));
        }

        Assert.Equal([
            "Server restarting in 5 minutes.",
            "Server restarting in 4 minutes.",
            "Server restarting in 3 minutes.",
            "Server restarting in 2 minutes.",
            "Server restarting in 1 minute.",
            "Server restarting in 30 seconds.",
            "Server restarting in 10 seconds.",
        ], said);
    }

    [Fact]
    public void WaitsForFightsButNotPastTheCap() {
        var plan = new RestartPlan(s_start, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), RestartKind.Restart, "update");
        Assert.Equal(RestartDecision.Countdown, plan.Decide(s_start.AddSeconds(30), wizardsInFights: 0));
        Assert.Equal(RestartDecision.Go, plan.Decide(s_start.AddMinutes(1), wizardsInFights: 0));
        Assert.Equal(RestartDecision.WaitForFights, plan.Decide(s_start.AddMinutes(1), wizardsInFights: 2));
        Assert.Equal(RestartDecision.WaitForFights, plan.Decide(s_start.AddMinutes(10.9), wizardsInFights: 2));
        Assert.Equal(RestartDecision.Go, plan.Decide(s_start.AddMinutes(11), wizardsInFights: 2));
        Assert.NotNull(plan.WaitMessage());
        Assert.Null(plan.WaitMessage());
    }

    [Fact]
    public void ImmediateRestartSaysNothingBeforeItWaitsOrGoes() {
        var plan = new RestartPlan(s_start, TimeSpan.Zero, TimeSpan.FromMinutes(10), RestartKind.Backup, null);
        Assert.Empty(plan.DueWarnings(s_start));
        Assert.Equal(RestartDecision.Go, plan.Decide(s_start, 0));
    }

    [Fact]
    public void BackupWarningSaysWhenItIsBack() {
        var plan = new RestartPlan(s_start, TimeSpan.FromMinutes(2), TimeSpan.Zero, RestartKind.Backup, "nightly");
        var first = plan.DueWarnings(s_start).Single();
        Assert.Equal("Server going down for a backup in 2 minutes. (nightly) It will be back in a minute or two.", first);
    }

    [Fact]
    public void LateTickGivesOnlyTheLatestMark() {
        var plan = new RestartPlan(s_start, TimeSpan.FromMinutes(10), TimeSpan.Zero, RestartKind.Restart, null);
        plan.DueWarnings(s_start);
        // A stalled timer wakes at 1:40 left: one warning, for 2 minutes, not five.
        Assert.Equal(["Server restarting in 2 minutes."], plan.DueWarnings(s_start.AddSeconds(500)));
    }

}
