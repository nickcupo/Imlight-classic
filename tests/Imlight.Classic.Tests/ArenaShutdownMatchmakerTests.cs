// CLASSIC: shutdown cancels unclaimed matches in memory and drains every accepted result through cleanup.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.WizardData.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaShutdownMatchmakerTests {
    private const ulong Human = 1000, Wife = 2000, Watcher = 3000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public ArenaShutdownMatchmakerTests() {
        var root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var path = Path.Combine(root, "arena-shutdown-tests-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={root}/arena-shutdown-tests.log\n");
        ConfigurationManager.Initialize(path);
    }

    private sealed class World : IArenaWorld, IArenaAmbientWorld {
        internal readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        internal readonly ConcurrentDictionary<ulong, ArenaPlayer> Players = new();
        internal readonly ConcurrentBag<ulong> Released = [];
        internal readonly ConcurrentDictionary<ulong, ArenaOutcome> Outcomes = new();
        internal readonly ConcurrentBag<IMessage> Sent = [];
        internal readonly ConcurrentBag<(ulong Who, ulong Run)> Trips = [];
        private readonly ConcurrentDictionary<ulong, string> _zones = new();
        private readonly ConcurrentDictionary<ulong, byte> _npcIds = new();
        private long _runs = 0xA000;
        internal int Reservations;
        internal Action<IMessage>? OnSend;
        internal Action<ulong>? OnRelease;
        public bool AmbientEnabled => true;
        public IArenaLadderStore Ladder { get; set; } = new ArenaLadderCollection.Memory();
        internal ArenaMatchmaker Arena { get; }

        internal World() {
            foreach (var id in new[] { Human, Wife, Watcher })
                Players[id] = new ArenaPlayer(id, id, [0x82, 1, 2, 3], "human", 20, "Fire", 0);
            Arena = new ArenaMatchmaker(Config, this);
        }
        public ArenaPlayer? Player(ulong id) => Players.GetValueOrDefault(id);
        public bool AreFriends(ulong first, ulong second) => first is Human or Wife && second is Human or Wife;
        public void Send(ulong id, IMessage message) { Sent.Add(message); OnSend?.Invoke(message); }
        public void Inform(ulong id, string text) { }
        public void Travel(ulong id, string zone, string location, ulong run) { _zones[id] = zone; Trips.Add((id, run)); }
        public void Deliver(ulong id, ArenaOutcome outcome) => Outcomes[id] = outcome;
        public ulong NewRunId() => (ulong) Interlocked.Increment(ref _runs);
        public string? ZoneOf(ulong id) => _zones.GetValueOrDefault(id) ?? Config.HallZone;
        public bool IsAmbient(ulong id) => _npcIds.ContainsKey(id);
        public ArenaPlayer? ReserveAmbient(int level, int preferredSchool) {
            Interlocked.Increment(ref Reservations);
            for (var variant = 0; variant < 8; variant++) {
                var id = ArenaAmbientParticipants.IdentityId(level, preferredSchool, variant);
                var player = new ArenaPlayer(id, id, [0x82, 2, 3, 4], "friendly", level,
                    ((Imlight.Classic.Ambient.AmbientSchool) preferredSchool).ToString(), 0, true);
                if (!Players.TryAdd(id, player)) continue;
                _npcIds[id] = 0;
                return player;
            }
            return null;
        }
        public void ReleaseAmbient(ulong id) { Released.Add(id); Players.TryRemove(id, out _); OnRelease?.Invoke(id); }
        internal uint Size(ArenaKind kind, int size) => ArenaRules.Hash(ArenaRules.MatchName(Arena.Tournament(kind), size));
    }

    private sealed class ThrowingLadder : IArenaLadderStore {
        internal int Accesses;
        private Exception Access() { Interlocked.Increment(ref Accesses); return new InvalidOperationException("shutdown touched the ladder"); }
        public ArenaLadderEntry? Load(ulong id) => throw Access();
        public void Save(ArenaLadderEntry entry) => throw Access();
        public void SaveMany(IReadOnlyCollection<ArenaLadderEntry> entries) => throw Access();
    }

    private sealed class BlockingLadder(int expectedBatches, int failBatch = 0) : IArenaLadderStore, IDisposable {
        internal readonly ArenaLadderCollection.Memory Memory = new();
        internal readonly TaskCompletionSource[] Entered = Enumerable.Range(0, expectedBatches)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        internal readonly ManualResetEventSlim[] Release = Enumerable.Range(0, expectedBatches)
            .Select(_ => new ManualResetEventSlim()).ToArray();
        internal readonly ConcurrentBag<int> BatchRows = [];
        internal int Batches;
        public ArenaLadderEntry? Load(ulong id) => Memory.Load(id);
        public void Save(ArenaLadderEntry entry) => throw new InvalidOperationException("results must save as one batch");
        public void SaveMany(IReadOnlyCollection<ArenaLadderEntry> entries) {
            var batch = Interlocked.Increment(ref Batches);
            BatchRows.Add(entries.Count);
            if (batch > expectedBatches) throw new InvalidOperationException("duplicate result batch");
            Entered[batch - 1].TrySetResult();
            if (!Release[batch - 1].Wait(Timeout)) throw new TimeoutException("test did not release the result save");
            if (batch == failBatch) throw new InvalidOperationException("injected result save failure");
            Memory.SaveMany(entries);
        }
        internal void ReleaseAll() { foreach (var release in Release) release.Set(); }
        public void Dispose() { foreach (var release in Release) release.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelsAutonomousAndHumanMatchesWithoutLadderAccessAndIgnoresLateCallbacks(bool autonomous) {
        var world = new World();
        var now = DateTime.UtcNow;
        if (autonomous) world.Arena.Tick(now);
        else {
            world.Arena.Create(Human, ArenaKind.Ranked, world.Size(ArenaKind.Ranked, 2), 0, 0, false);
            world.Arena.Tick(now.AddSeconds(ArenaMatchmaker.AmbientWaitSeconds + 1));
            world.Arena.Confirm(Human, true);
        }
        var matches = world.Arena.Snapshot();
        var ranked = matches.Single(m => m.Kind == ArenaKind.Ranked);
        var runs = matches.Select(m => world.Arena.RunOf(m.Id)).Where(id => id != 0).ToArray();
        foreach (var run in runs) world.Arena.Started(run, now);
        world.Arena.Watch(Watcher, ranked.Id);
        var oldTeam = world.Arena.TeamIdsOf(ranked.Id)[0];
        if (!autonomous) world.Arena.Create(Wife, ArenaKind.Practice, world.Size(ArenaKind.Practice, 1), 0, 0, true);
        var npcs = world.Players.Values.Where(p => p.Ambient).Select(p => p.CharId).ToArray();
        var reservations = world.Reservations;
        var trips = world.Trips.Count;
        var ladder = new ThrowingLadder();
        world.Ladder = ladder;
        world.OnRelease = id => {
            Assert.Equal(0UL, world.Arena.MatchOf(id));
            world.Arena.AmbientLost(id); // release callbacks cannot resurrect a cancelled match or award anything
        };

        var drain = world.Arena.QuiesceAsync();
        await drain.WaitAsync(Timeout);
        Assert.Same(drain, world.Arena.QuiesceAsync());
        foreach (var run in runs) {
            world.Arena.Finish(run, 0, []);
            world.Arena.Started(run, now);
            Assert.Null(world.Arena.Run(run));
        }
        world.Arena.Tick(now.AddDays(1));
        foreach (var npc in npcs) world.Arena.AmbientLost(npc);
        world.Arena.Create(Human, ArenaKind.Ranked, world.Size(ArenaKind.Ranked, 1), 0, 0, false);
        world.Arena.QuickJoin(Human, ArenaKind.Ranked, world.Size(ArenaKind.Ranked, 1));
        world.Arena.Join(Wife, ranked.Id, oldTeam);
        world.Arena.Confirm(Human, true);
        world.Arena.Watch(Watcher, ranked.Id);
        world.Arena.Leave(Human);
        world.Arena.Offline(Human, now);
        world.Arena.List(Watcher, world.Arena.TournamentId(ArenaKind.Ranked));
        world.Arena.OpenKiosk(Human, ArenaKind.Ranked, 1);
        world.Arena.CanJoin(Human);

        Assert.Equal(0, ladder.Accesses);
        Assert.Empty(world.Arena.Snapshot());
        Assert.Equal(0UL, world.Arena.MatchOf(Human));
        Assert.Equal(0UL, world.Arena.MatchOf(Wife));
        Assert.False(world.Arena.IsSpectator(Watcher));
        Assert.Empty(world.Outcomes);
        Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
        Assert.Equal(reservations, world.Reservations);
        Assert.Equal(trips, world.Trips.Count); // shutdown closes sessions; it does not start return trips
        Assert.Equal(npcs.Length, world.Released.Count);
        Assert.All(npcs, id => Assert.Single(world.Released.Where(released => released == id)));
        Assert.Equal((byte) 0, Assert.Single(world.Sent.OfType<WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCHRESPONSE>()).CanJoinQueue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedRankedSaveKeepsDrainPendingAndRetainsSuccessOrFailureThroughCleanup(bool failSave) {
        var world = new World();
        world.Arena.Tick(DateTime.UtcNow);
        var ranked = world.Arena.Snapshot().Single(m => m.Kind == ArenaKind.Ranked);
        var runId = world.Arena.RunOf(ranked.Id);
        var run = world.Arena.Run(runId)!;
        var members = run.Side0.Concat(run.Side1).ToArray();
        world.Arena.Watch(Watcher, ranked.Id);
        using var ladder = new BlockingLadder(1, failSave ? 1 : 0);
        world.Ladder = ladder;
        var finish = Task.Run(() => world.Arena.Finish(runId, 0, []));
        try {
            await ladder.Entered[0].Task.WaitAsync(Timeout);
            var drain = world.Arena.QuiesceAsync();
            Assert.False(drain.IsCompleted);
            Assert.All(members, id => Assert.DoesNotContain(id, world.Released));
            Assert.True(world.Arena.IsSpectator(Watcher)); // accepted result still owns its spectator cleanup
            ladder.ReleaseAll();
            await finish.WaitAsync(Timeout);
            if (failSave) {
                var error = await Assert.ThrowsAsync<AggregateException>(() => drain.WaitAsync(Timeout));
                Assert.Contains(error.InnerExceptions, ex => ex.Message == "injected result save failure");
                Assert.All(members, id => Assert.Null(ladder.Memory.Load(id)));
                Assert.Empty(world.Outcomes);
            }
            else {
                await drain.WaitAsync(Timeout);
                Assert.Equal(1, ladder.Memory.Load(run.Side0.Single())!.Wins);
                Assert.Equal(1, ladder.Memory.Load(run.Side1.Single())!.Losses);
                Assert.Equal(members.Length, world.Outcomes.Count);
            }
            world.Arena.Finish(runId, 0, []);
            Assert.Equal(1, ladder.Batches);
            Assert.Equal(members.Length, Assert.Single(ladder.BatchRows));
            Assert.Null(world.Arena.Run(runId));
            Assert.Empty(world.Arena.Snapshot());
            Assert.False(world.Arena.IsSpectator(Watcher));
            Assert.Contains((Watcher, 0UL), world.Trips);
            Assert.All(members, id => Assert.Single(world.Released.Where(released => released == id)));
            Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
        }
        finally { ladder.ReleaseAll(); await finish.WaitAsync(Timeout); }
    }

    [Fact]
    public async Task DrainsASecondAcceptedResultStillWaitingForTheFirstResultLock() {
        var world = new World();
        var now = DateTime.UtcNow;
        world.Arena.Tick(now);
        var first = world.Arena.Snapshot().Single(m => m.Kind == ArenaKind.Ranked);
        var firstRunId = world.Arena.RunOf(first.Id);
        var firstRun = world.Arena.Run(firstRunId)!;
        using var ladder = new BlockingLadder(2);
        world.Ladder = ladder;
        var firstFinish = Task.Run(() => world.Arena.Finish(firstRunId, 0, []));
        Task? secondFinish = null;
        try {
            await ladder.Entered[0].Task.WaitAsync(Timeout);
            world.Arena.Tick(now.AddSeconds(20));
            var second = world.Arena.Snapshot().Single(m => m.Kind == ArenaKind.Ranked);
            var secondRunId = world.Arena.RunOf(second.Id);
            var secondRun = world.Arena.Run(secondRunId)!;
            world.Arena.List(Watcher, world.Arena.TournamentId(ArenaKind.Ranked), requestType: 5);
            var secondClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            world.OnSend = message => {
                if (message is GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE or GAME_5_PROTOCOL.MSG_PVPUPDATEINFO)
                    secondClaimed.TrySetResult();
            };
            secondFinish = Task.Run(() => world.Arena.Finish(secondRunId, 0, []));
            await secondClaimed.Task.WaitAsync(Timeout); // removal broadcast occurs after the claim, before _resultsGate
            var drain = world.Arena.QuiesceAsync();
            Assert.False(drain.IsCompleted);
            Assert.Equal(1, ladder.Batches);
            ladder.Release[0].Set();
            await ladder.Entered[1].Task.WaitAsync(Timeout);
            Assert.False(drain.IsCompleted);
            ladder.Release[1].Set();
            await Task.WhenAll(firstFinish, secondFinish, drain).WaitAsync(Timeout);

            Assert.Equal(2, ladder.Batches);
            Assert.Equal(new[] { 2, 4 }, ladder.BatchRows.OrderBy(rows => rows));
            var accepted = firstRun.Side0.Concat(firstRun.Side1).Concat(secondRun.Side0).Concat(secondRun.Side1).ToArray();
            Assert.All(accepted, id => {
                Assert.NotNull(ladder.Memory.Load(id));
                Assert.Single(world.Released.Where(released => released == id));
            });
            Assert.Equal(accepted.Length, world.Outcomes.Count);
            Assert.Empty(world.Arena.Snapshot());
            Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
        }
        finally {
            ladder.ReleaseAll();
            await firstFinish.WaitAsync(Timeout);
            if (secondFinish is not null) await secondFinish.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task AcceptedRemovalCallbackFailureStillDrainsParticipantAndSpectatorCleanup() {
        var world = new World();
        world.Arena.Create(Human, ArenaKind.Ranked, world.Size(ArenaKind.Ranked, 1), 0, 0, false);
        world.Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.AmbientWaitSeconds + 1));
        world.Arena.Confirm(Human, true);
        var matchId = world.Arena.MatchOf(Human);
        var runId = world.Arena.RunOf(matchId);
        var run = world.Arena.Run(runId)!;
        var npc = run.Side1.Single();
        world.Arena.Watch(Watcher, matchId);
        world.Arena.List(Watcher, world.Arena.TournamentId(ArenaKind.Ranked), requestType: 5);
        using var ladder = new BlockingLadder(0);
        world.Ladder = ladder;
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        world.OnSend = message => {
            if (message is GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE or GAME_5_PROTOCOL.MSG_PVPUPDATEINFO)
                throw new InvalidOperationException("injected accepted removal failure");
        };
        world.OnRelease = id => {
            if (id != npc) return;
            cleanupEntered.TrySetResult();
            if (!releaseCleanup.Wait(Timeout)) throw new TimeoutException("test did not release accepted cleanup");
        };
        var finish = Task.Run(() => world.Arena.Finish(runId, 0, []));
        try {
            await cleanupEntered.Task.WaitAsync(Timeout);
            var drain = world.Arena.QuiesceAsync();
            Assert.False(drain.IsCompleted);
            Assert.Equal(0UL, world.Arena.MatchOf(Human));
            Assert.Equal(0UL, world.Arena.MatchOf(npc));
            Assert.True(world.Arena.IsSpectator(Watcher));
            releaseCleanup.Set();
            var original = await Assert.ThrowsAsync<InvalidOperationException>(() => finish.WaitAsync(Timeout));
            Assert.Equal("injected accepted removal failure", original.Message);
            var shutdown = await Assert.ThrowsAsync<AggregateException>(() => drain.WaitAsync(Timeout));
            Assert.Contains(shutdown.InnerExceptions, ex => ReferenceEquals(ex, original));
            Assert.Equal(0, ladder.Batches);
            Assert.Null(ladder.Memory.Load(Human));
            Assert.Null(ladder.Memory.Load(npc));
            Assert.Empty(world.Outcomes);
            Assert.Empty(world.Arena.Snapshot());
            Assert.Null(world.Arena.Run(runId));
            Assert.False(world.Arena.IsSpectator(Watcher));
            Assert.Single(world.Released, released => released == npc);
            Assert.DoesNotContain(world.Players.Values, player => player.Ambient);
            Assert.Contains((Human, 0UL), world.Trips);
            Assert.Contains((Watcher, 0UL), world.Trips);
        }
        finally {
            releaseCleanup.Set();
            try { await finish.WaitAsync(Timeout); }
            catch (InvalidOperationException ex) when (ex.Message == "injected accepted removal failure") { }
        }
    }

    [Fact]
    public async Task CancellationReleasesEveryReservationOnceAndReportsARealReleaseFailure() {
        var world = new World();
        world.Arena.Tick(DateTime.UtcNow);
        var npcs = world.Players.Values.Where(p => p.Ambient).Select(p => p.CharId).ToArray();
        var ladder = new ThrowingLadder();
        world.Ladder = ladder;
        world.OnRelease = id => { if (id == npcs[0]) throw new InvalidOperationException("injected release failure"); };
        var drain = world.Arena.QuiesceAsync();
        var error = await Assert.ThrowsAsync<AggregateException>(() => drain.WaitAsync(Timeout));
        Assert.Contains(error.InnerExceptions, ex => ex.Message == "injected release failure");
        Assert.Same(drain, world.Arena.QuiesceAsync());
        Assert.Equal(0, ladder.Accesses);
        Assert.Empty(world.Arena.Snapshot());
        Assert.All(npcs, id => Assert.Single(world.Released.Where(released => released == id)));
        Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
    }
}
