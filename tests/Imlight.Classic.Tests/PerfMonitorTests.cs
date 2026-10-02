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
 * PERFORMANCE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The PERF log's histogram, and the active wizard directory that lets zone
 * actors read a session's wizard without asking it: the same reference the
 * Ask returned, no Ask for a listed session, the Ask kept for one that is
 * not listed, and nothing for a session that has stopped.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter PerfMonitorTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Threading;
using Akka.Actor;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PerfMonitorTests {

    [Fact]
    public void HistogramReportsBucketPercentilesAndExactMax() {
        var histogram = new LatencyHistogram();
        for (var i = 1; i <= 100; i++) {
            histogram.Record(i);
        }

        Assert.Equal(100, histogram.Count);
        Assert.Equal(50, histogram.Percentile(0.5));
        Assert.Equal(100, histogram.Percentile(0.95));
        Assert.Equal(100, histogram.Max, 3);
        Assert.Equal(50.5, histogram.Mean, 3);
    }

    [Fact]
    public void HistogramPutsHugeValuesInTheLastBucketAndReportsTheirMax() {
        var histogram = new LatencyHistogram();
        histogram.Record(25_000);
        histogram.Record(-3); // A clock step backwards counts as zero.

        Assert.Equal(25_000, histogram.Percentile(1.0), 3);
        Assert.Equal(0.05, histogram.Percentile(0.5));
    }

    [Fact]
    public void EmptyHistogramDescribesItself() => Assert.Equal("n=0", new LatencyHistogram().Describe());

    private sealed class SilentSession : ReceiveActor {
        // Never answers MSG_QUERYACTIVEWIZARD: an Ask would wait out its timeout.
        public SilentSession() => ReceiveAny(_ => { });
    }

    private sealed class CountingSession : ReceiveActor {
        public static int Queries;

        public CountingSession(Wizard wizard) => Receive<CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD>(_ => {
            Interlocked.Increment(ref Queries);
            Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER { Wizard = wizard });
        });
    }

    [Fact]
    public void ListedSessionAnswersFromTheDirectoryWithoutAnAsk() {
        using var system = ActorSystem.Create("wizard-directory-listed", "akka.actor.provider = local");
        var session = system.ActorOf(Props.Create(() => new SilentSession()));
        var wizard = new Wizard();
        ActiveWizardDirectory.SetWizard(session, wizard);
        try {
            var started = DateTime.UtcNow;
            Assert.True(PlayerQuery.TryActiveWizard(session, TimeSpan.FromSeconds(5), out var found, out var error));
            Assert.Same(wizard, found);
            Assert.Null(error);
            Assert.Same(wizard, PlayerQuery.Character(session, TimeSpan.FromSeconds(5)).Wizard);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        }
        finally {
            ActiveWizardDirectory.Remove(session);
        }
    }

    [Fact]
    public void UnlistedSessionIsStillAsked() {
        using var system = ActorSystem.Create("wizard-directory-unlisted", "akka.actor.provider = local");
        var wizard = new Wizard();
        var session = system.ActorOf(Props.Create(() => new CountingSession(wizard)));
        var before = Volatile.Read(ref CountingSession.Queries);

        Assert.True(PlayerQuery.TryActiveWizard(session, TimeSpan.FromSeconds(5), out var found, out _));
        Assert.Same(wizard, found);
        Assert.Equal(before + 1, Volatile.Read(ref CountingSession.Queries));
    }

    [Fact]
    public void GameObjectAloneOrRemovedEntryIsNotAnAnswer() {
        using var system = ActorSystem.Create("wizard-directory-partial", "akka.actor.provider = local");
        var session = system.ActorOf(Props.Create(() => new SilentSession()));
        ActiveWizardDirectory.SetGameObject(session, new Imcodec.ObjectProperty.TypeCache.CoreObject());
        try {
            Assert.False(ActiveWizardDirectory.TryGet(session, out _, out _));
            var wizard = new Wizard();
            ActiveWizardDirectory.SetWizard(session, wizard);
            Assert.True(ActiveWizardDirectory.TryGet(session, out var found, out var gameObject));
            Assert.Same(wizard, found);
            Assert.NotNull(gameObject);
        }
        finally {
            ActiveWizardDirectory.Remove(session);
        }

        Assert.False(ActiveWizardDirectory.TryGet(session, out _, out _));
    }

    [Fact]
    public void StoppedSessionIsNeverAnsweredFromTheDirectory() {
        using var system = ActorSystem.Create("wizard-directory-stopped", "akka.actor.provider = local");
        var session = system.ActorOf(Props.Create(() => new SilentSession()));
        ActiveWizardDirectory.SetWizard(session, new Wizard());
        try {
            session.GracefulStop(TimeSpan.FromSeconds(5)).Wait();
            Assert.False(ActiveWizardDirectory.TryGet(session, out _, out _));
        }
        finally {
            ActiveWizardDirectory.Remove(session);
        }
    }

}
