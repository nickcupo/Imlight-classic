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
 * CLASSIC CINEMATICS TESTS
 * ========================================================================
 *
 * PURPOSE:
 * KingsIsle's property hashes, the zone timer that waits for a staged
 * cinematic, and the classic zone registry reading r806919's cinematic
 * trigger results.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter ClassicCinematicTests
 *
 * NOTE:
 * The r806919 cases read the private extraction under
 * ~/w101c-private/extract/r806919 and skip when it is absent; nothing from
 * the client is committed.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Cinematics;
using Imlight.CoreLib.Classic.Cinematics;
using Xunit;
using Action = System.Action;
using Type = System.Type;

namespace Imlight.Classic.Tests;

public sealed class ClassicCinematicTests {

    [Theory]
    [InlineData("m_cinematicName", "std::string", 0x9BA8BF49u)]   // ResPlayCinematic in the server registry
    [InlineData("m_entryName", "std::string", 0x7A80F14Eu)]       // ResModifyEntry
    [InlineData("m_router", "class ZoneRouter", 0x444373FAu)]     // ResPlayCinematic
    [InlineData("m_locX", "float", 0x12773D2Du)]                  // ZoneRouter
    [InlineData("m_eventName", "std::string", 0xD036EBFEu)]       // ResPostEvent
    public void PropertyHashesMatchTheRegistry(string name, string type, uint hash)
        => Assert.Equal(hash, KingsIsleHash.Property(name, type));

    [Theory]
    [InlineData("class ResCinematic", 82637767u)]
    [InlineData("class ResStartStagedCinematic", 145615551u)]
    public void TypeHashesMatchTheRegistry(string type, uint hash)
        => Assert.Equal(hash, KingsIsleHash.Type(type));

    [Theory]
    [InlineData("CLIENTEVENT.SawMalistaireFightIntro", "SawMalistaireFightIntro")]
    [InlineData("CLIENTEVENT.", null)]
    [InlineData("SawMalistaireFightIntro", null)]
    [InlineData(null, null)]
    public void ConditionsNameTheirClientEvent(string? condition, string? clientEvent)
        => Assert.Equal(clientEvent, CinematicTimers.ClientEventOf(condition));

    [Fact]
    public void TheClientEventEndsTheTimerOnce() {
        var scheduler = new ManualScheduler();
        var timers = new CinematicTimers(scheduler.Schedule);
        var ends = 0;

        timers.Start("lair", "MalistaireCinematic", "CLIENTEVENT.SawMalistaireFightIntro", TimeSpan.FromSeconds(300), () => ends++);

        Assert.Equal(0, timers.ClientEvent("other-lair", "SawMalistaireFightIntro"));
        Assert.Equal(0, timers.ClientEvent("lair", "SawMalistaireFightEnd"));
        Assert.Equal(1, timers.ClientEvent("lair", "SawMalistaireFightIntro"));
        Assert.Equal(0, timers.ClientEvent("lair", "SawMalistaireFightIntro"));
        scheduler.RunAll();

        Assert.Equal(1, ends);
        Assert.Equal(0, timers.Count);
        Assert.Equal("EndMalistaireCinematic", CinematicTimers.EndEventOf("MalistaireCinematic"));
    }

    [Fact]
    public void TheTimeLimitEndsATimerNoClientAnswers() {
        var scheduler = new ManualScheduler();
        var timers = new CinematicTimers(scheduler.Schedule);
        var ends = 0;

        timers.Start("lair", "MalistaireCinematic", "CLIENTEVENT.SawMalistaireFightIntro", TimeSpan.FromSeconds(300), () => ends++);
        Assert.Equal(TimeSpan.FromSeconds(300), scheduler.Delays.Single());
        scheduler.RunAll();
        timers.ClientEvent("lair", "SawMalistaireFightIntro");

        Assert.Equal(1, ends);
    }

    [Fact]
    public void RestartingATimerDropsTheOldEnd() {
        var scheduler = new ManualScheduler();
        var timers = new CinematicTimers(scheduler.Schedule);
        var ends = new List<string>();

        timers.Start("lair", "T", "CLIENTEVENT.E", TimeSpan.FromSeconds(10), () => ends.Add("first"));
        timers.Start("lair", "T", "CLIENTEVENT.E", TimeSpan.FromSeconds(10), () => ends.Add("second"));
        scheduler.RunAll();

        Assert.Equal(["second"], ends);
    }

    [Fact]
    public void AZoneThatUnloadsEndsNothing() {
        var scheduler = new ManualScheduler();
        var timers = new CinematicTimers(scheduler.Schedule);
        var ends = 0;

        timers.Start("lair", "T", "CLIENTEVENT.E", TimeSpan.FromSeconds(10), () => ends++);
        timers.StopZone("lair");
        scheduler.RunAll();

        Assert.Equal(0, ends);
        Assert.Equal(0, timers.Count);
    }

    [Fact]
    public void AClientEventRacingTheTimeLimitEndsOnce() {
        for (var round = 0; round < 500; round++) {
            var scheduler = new ManualScheduler();
            var timers = new CinematicTimers(scheduler.Schedule);
            var ends = 0;
            timers.Start("lair", "T", "CLIENTEVENT.E", TimeSpan.FromSeconds(1), () => Interlocked.Increment(ref ends));

            Parallel.Invoke(scheduler.RunAll, () => timers.ClientEvent("lair", "E"), () => timers.ClientEvent("lair", "E"));

            Assert.Equal(1, ends);
        }
    }

    [Fact]
    public void TheMalistaireLairTriggersReadTheirCinematics() {
        var triggers = LoadTriggers("DragonSpire-DS_A3_Kings-Interiors-DS_MalistaireLair");

        var start = triggers.Single(t => t.m_triggerName == "StartCinematicTrigger").m_results.m_results;
        var timer = Assert.IsType<ClassicResZoneTimer>(start[0]);
        Assert.Equal("MalistaireCinematic", timer.TimerName);
        Assert.Equal(300f, timer.LimitSeconds);
        Assert.Equal("CLIENTEVENT.SawMalistaireFightIntro", timer.Condition);
        Assert.Equal("MalistaireFightIntro", timer.Cinematic);
        var staged = Assert.IsType<ClassicResStartStagedCinematic>(start[2]);
        Assert.Equal("MalistaireFightIntro", staged.CinematicName);
        Assert.Equal("Part1", staged.StageName);
        Assert.True(staged.IncludeAllPlayersInZone);

        // The fight is set up by the timer's end event, which nothing else posts.
        var setUp = triggers.Single(t => t.m_triggerName == "TriggerSetUpScene");
        Assert.Equal([CinematicTimers.EndEventOf(timer.TimerName)], setUp.m_fireEvents.Select(e => (string) e));
        Assert.Contains(setUp.m_results.m_results, r => r is ResSpawn);
    }

    [Fact]
    public void RavenwoodQuestTriggersReadTheirCinematicActors() {
        var triggers = LoadTriggers("WizardCity-WC_Ravenwood");

        var storm = triggers.Single(t => t.m_triggerName == "STRM-C05-Cinematic Trigger").m_results.m_results
            .OfType<ClassicResPlayCinematic>().Single();
        Assert.Equal("Cinematic_StormLordActor", storm.Playback.CinematicName);
        Assert.Equal("Cinematic_StormLordActor", (string) storm.m_cinematicName);
        Assert.NotNull(storm.Playback.Router);

        var lightning = triggers.Single(t => t.m_triggerName == "LightningActivationTrigger").m_results.m_results
            .OfType<ClassicResCinematic>().Single();
        Assert.Equal("Cinematic_LightningStandAlone", lightning.Playback.CinematicName);
        Assert.True(lightning.Playback.Blocking);
        Assert.True(lightning.Playback.StartAtActor);
    }

    [Fact]
    public void EveryCinematicResultInTheClientDecodes() {
        var zones = Path.Combine(ExtractRoot(), "zones");
        var counts = new Dictionary<Type, int>();
        foreach (var file in Directory.EnumerateFiles(zones, "triggers.xml", SearchOption.AllDirectories)) {
            var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
            Assert.True(serializer.Deserialize<WizZoneTriggers>(File.ReadAllBytes(file), 1, out var triggers), file);
            foreach (var result in triggers.m_triggers.Where(t => t is not null).SelectMany(t => t.m_results?.m_results ?? [])) {
                switch (result) {
                    case ClassicResPlayCinematic play:
                        Assert.False(string.IsNullOrEmpty(play.Playback.CinematicName), file);
                        break;
                    case ClassicResCinematic cinematic:
                        Assert.False(string.IsNullOrEmpty(cinematic.Playback.CinematicName), file);
                        break;
                    case ClassicResStartStagedCinematic staged:
                        Assert.False(string.IsNullOrEmpty(staged.StageName), file);
                        break;
                    case ClassicResZoneTimer timer:
                        Assert.False(string.IsNullOrEmpty(timer.TimerName), file);
                        Assert.True(timer.LimitSeconds > 0, file);
                        break;
                    default:
                        continue;
                }
                counts[result.GetType()] = counts.GetValueOrDefault(result.GetType()) + 1;
            }
        }

        Assert.Equal(57, counts[typeof(ClassicResPlayCinematic)]);
        Assert.Equal(25, counts[typeof(ClassicResCinematic)]);
        Assert.Equal(3, counts[typeof(ClassicResStartStagedCinematic)]);
        Assert.Equal(2, counts[typeof(ClassicResZoneTimer)]);
    }

    private static string ExtractRoot() {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "extract", "r806919");
        if (!Directory.Exists(Path.Combine(root, "zones"))) {
            Assert.Skip("the private r806919 extraction is not on this machine");
        }

        return root;
    }

    private static List<Trigger> LoadTriggers(string zoneFolder) {
        var path = Path.Combine(ExtractRoot(), "zones", zoneFolder, "triggers.xml");
        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
        Assert.True(serializer.Deserialize<WizZoneTriggers>(File.ReadAllBytes(path), 1, out var triggers));

        return [.. triggers.m_triggers.Where(t => t is not null)];
    }

    private sealed class ManualScheduler {

        private readonly ConcurrentQueue<(TimeSpan Delay, Action Action, Cancel Handle)> _queue = new();

        public List<TimeSpan> Delays => [.. _queue.Select(entry => entry.Delay)];

        public IDisposable Schedule(TimeSpan delay, Action action) {
            var handle = new Cancel();
            _queue.Enqueue((delay, action, handle));

            return handle;
        }

        public void RunAll() {
            while (_queue.TryDequeue(out var entry)) {
                if (!entry.Handle.Cancelled) {
                    entry.Action();
                }
            }
        }

        private sealed class Cancel : IDisposable {
            public volatile bool Cancelled;
            public void Dispose() => Cancelled = true;
        }

    }

}
