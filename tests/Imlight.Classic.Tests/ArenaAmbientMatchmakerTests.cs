// CLASSIC: friendly arena participants share the real match state machine, without claiming human seats or saves.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.WizardData.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaAmbientMatchmakerTests {
    private const ulong Human = 1000, Wife = 2000, Watcher = 3000;
    public ArenaAmbientMatchmakerTests() {
        var root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var path = Path.Combine(root, "arena-ambient-tests-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={root}/arena-ambient-tests.log\n");
        ConfigurationManager.Initialize(path);
    }

    private sealed class World : IArenaWorld, IArenaAmbientWorld, IArenaFriendlyWorld {
        internal readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        internal readonly Dictionary<ulong, ArenaPlayer> Players = [];
        internal readonly List<(ulong Who, string Zone, ulong Run)> Trips = [];
        internal readonly List<(ulong Who, IMessage Message)> Sent = [];
        internal readonly List<ulong> Released = [];
        internal readonly Dictionary<ulong, ArenaOutcome> Outcomes = [];
        private readonly HashSet<ulong> _npcIds = [];
        internal int Reservations, FailAtReservation;
        internal ulong FailDeliveryTo;
        private ulong _runs = 0xA000;
        public bool AmbientEnabled { get; set; } = true;
        public bool FriendlyEnabled { get; set; }
        internal int MaxActive = 64;
        public ArenaPlayer? PreviewFriendly(int level, int school, ArenaPvpSkill skill) {
            var id = ArenaAmbientParticipants.IdentityId(level, school, (int) skill * 2);
            return new ArenaPlayer(id, id, [0x82, 2, 3, 4], "friendly", level,
                ((Imlight.Classic.Ambient.AmbientSchool) school).ToString(), 0, true, skill);
        }
        public ArenaPlayer? ReserveFriendly(int level, int school, ArenaPvpSkill skill) {
            Reservations++;
            if (Players.Values.Count(p => p.Ambient) >= MaxActive || FailAtReservation > 0 && Reservations >= FailAtReservation) return null;
            for (var offset = 0; offset < 1; offset++)
                for (var variant = (int) skill * 2; variant < (int) skill * 2 + 2; variant++) {
                    var actualSchool = (school + offset) % 7;
                    var id = ArenaAmbientParticipants.IdentityId(level, actualSchool, variant);
                    if (Players.ContainsKey(id)) continue;
                    var player = new ArenaPlayer(id, id, [0x82, 2, 3, 4], "friendly", level,
                        ((Imlight.Classic.Ambient.AmbientSchool) actualSchool).ToString(), 0, true, skill);
                    Players[id] = player; _npcIds.Add(id); return player;
                }
            return null;
        }
        public IArenaLadderStore Ladder { get; set; } = new ArenaLadderCollection.Memory();
        internal ArenaMatchmaker Arena { get; }
        internal World() {
            foreach (var id in new[] { Human, Wife, Watcher }) Players[id] = new ArenaPlayer(id, id, [0x82, 1, 2, 3], "human", 20, "Fire", 0);
            Arena = new ArenaMatchmaker(Config, this);
        }
        public ArenaPlayer? Player(ulong id) => Players.GetValueOrDefault(id);
        public bool AreFriends(ulong first, ulong second) => first is Human or Wife && second is Human or Wife;
        public void Send(ulong id, IMessage message) => Sent.Add((id, message));
        public void Inform(ulong id, string text) { }
        public void Travel(ulong id, string zone, string location, ulong run) => Trips.Add((id, zone, run));
        public void Deliver(ulong id, ArenaOutcome outcome) {
            if (id == FailDeliveryTo) throw new InvalidOperationException("injected delivery failure");
            Outcomes[id] = outcome;
        }
        public ulong NewRunId() => ++_runs;
        public string? ZoneOf(ulong id) => Trips.LastOrDefault(t => t.Who == id).Zone ?? Config.HallZone;
        public bool IsAmbient(ulong id) => _npcIds.Contains(id);
        public ArenaPlayer? ReserveAmbient(int level, int preferredSchool) {
            Reservations++;
            if (FailAtReservation > 0 && Reservations >= FailAtReservation) return null;
            for (var variant = 0; variant < 8; variant++) {
                var id = ArenaAmbientParticipants.IdentityId(level, preferredSchool, variant);
                if (Players.ContainsKey(id)) continue;
                var player = new ArenaPlayer(id, id, [0x82, 2, 3, 4], "friendly", level,
                    ((Imlight.Classic.Ambient.AmbientSchool) preferredSchool).ToString(), 0, true);
                Players[id] = player; _npcIds.Add(id); return player;
            }
            return null;
        }
        public void ReleaseAmbient(ulong id) { Released.Add(id); Players.Remove(id); }
        internal uint Size(ArenaKind kind, int size) => ArenaRules.Hash(ArenaRules.MatchName(kind == ArenaKind.Practice ? Config.PracticeTournament : Config.RankedTournament, size));
        internal ulong Queue(ArenaKind kind, int size, bool friends = false, int min = 0, int max = 0) {
            Arena.Create(Human, kind, Size(kind, size), min, max, friends);
            return Arena.MatchOf(Human);
        }
        internal void Fill() => Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.AmbientWaitSeconds + 1));
    }

    [Theory]
    [InlineData(ArenaKind.Practice, 1)] [InlineData(ArenaKind.Practice, 2)] [InlineData(ArenaKind.Practice, 3)] [InlineData(ArenaKind.Practice, 4)]
    [InlineData(ArenaKind.Ranked, 1)] [InlineData(ArenaKind.Ranked, 2)] [InlineData(ArenaKind.Ranked, 3)] [InlineData(ArenaKind.Ranked, 4)]
    public void EveryKindAndSizeGetsLegalMatchingLevelNpcSeatsButNeverAutoConfirmsAHuman(ArenaKind kind, int size) {
        var world = new World(); var match = world.Queue(kind, size);
        world.Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.AmbientWaitSeconds - 2));
        Assert.Equal(0, world.Reservations);
        world.Fill();
        var snapshot = Assert.Single(world.Arena.Snapshot());
        Assert.Equal((size, size), (snapshot.Side0, snapshot.Side1)); Assert.Equal("Confirming", snapshot.Phase);
        Assert.Empty(world.Trips); Assert.Equal(size * 2 - 1, world.Reservations);
        Assert.All(world.Players.Values.Where(p => p.Ambient), p => Assert.Equal(20, p.Level));
        Assert.Equal(Math.Min(7, size * 2 - 1), world.Players.Values.Where(p => p.Ambient).Select(p => p.School).Distinct().Count());
        world.Arena.Confirm(Human, true);
        Assert.NotEqual(0UL, world.Arena.RunOf(match)); Assert.Equal(size * 2, world.Trips.Count);
    }

    [Fact]
    public void FriendsOnlyPracticeKeepsTheHusbandAndWifesSeatsHumanOnly() {
        var world = new World(); var match = world.Queue(ArenaKind.Practice, 1, friends: true);
        world.Fill(); Assert.Equal(0, world.Reservations);
        world.Arena.Join(Wife, match, world.Arena.TeamIdsOf(match)[1]);
        Assert.Equal("Confirming", Assert.Single(world.Arena.Snapshot()).Phase);
        Assert.Empty(world.Trips);
        world.Arena.Confirm(Human, true); world.Arena.Confirm(Wife, true);
        Assert.Equal(2, world.Trips.Count);
    }

    [Fact]
    public void HumanJoinReplacesWaitingNpcWithoutResultPenaltyOrUnrequestedTravel() {
        var world = new World(); var match = world.Queue(ArenaKind.Ranked, 1); world.Fill();
        var npc = Assert.Single(world.Players.Values.Where(p => p.Ambient));
        world.Arena.Join(Wife, match, world.Arena.TeamIdsOf(match)[1]);
        Assert.Contains(npc.CharId, world.Released); Assert.Empty(world.Outcomes); Assert.Null(world.Ladder.Load(npc.CharId));
        Assert.Empty(world.Trips); Assert.Equal(match, world.Arena.MatchOf(Wife));
        world.Arena.Confirm(Human, true); Assert.Empty(world.Trips);
        world.Arena.Confirm(Wife, true); Assert.Equal(2, world.Trips.Count);
    }

    [Fact]
    public void QuickJoinAlsoReplacesANpcAndPublicRowsKeepTheSeatAvailable() {
        var world = new World(); var match = world.Queue(ArenaKind.Practice, 1); world.Fill();
        world.Arena.List(Wife, world.Arena.TournamentId(ArenaKind.Practice));
        var message = world.Sent.Where(x => x.Who == Wife).Select(x => x.Message).OfType<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>().Last();
        var list = ArenaMessages.Read<NewListUpdate>(message.TournamentInfo)!;
        var row = Assert.IsType<PvPMatchInfo>(Assert.Single(list.m_matches));
        Assert.Empty(row.m_teams[1].m_actors); Assert.Equal(0, row.m_status);
        world.Arena.QuickJoin(Wife, ArenaKind.Practice, world.Size(ArenaKind.Practice, 1));
        Assert.Equal(match, world.Arena.MatchOf(Wife)); Assert.Single(world.Released); Assert.Empty(world.Trips);
    }

    [Fact]
    public void ChangedTwoPlayerLineupRequiresAllHumansToConfirmAgain() {
        var world = new World(); var match = world.Queue(ArenaKind.Practice, 2);
        world.Arena.Join(Wife, match, world.Arena.TeamIdsOf(match)[1]);
        world.Fill();
        world.Arena.Confirm(Human, true); Assert.Empty(world.Trips);
        world.Arena.Join(Watcher, match, world.Arena.TeamIdsOf(match)[0]);
        world.Arena.Confirm(Wife, true); Assert.Empty(world.Trips);
        world.Arena.Confirm(Watcher, true); Assert.Empty(world.Trips);
        world.Arena.Confirm(Human, true);
        Assert.NotEqual(0UL, world.Arena.RunOf(match));
        Assert.Equal(4, world.Trips.Count); Assert.Single(world.Released);
    }

    [Fact]
    public void LastHumanCancellationReleasesAllNpcReservationsWithoutLadderWrites() {
        var world = new World(); world.Queue(ArenaKind.Ranked, 4); world.Fill();
        var npcs = world.Players.Values.Where(p => p.Ambient).Select(p => p.CharId).ToArray();
        world.Arena.Leave(Human);
        Assert.Empty(world.Arena.Snapshot()); Assert.Equal(7, world.Released.Count); Assert.Empty(world.Outcomes);
        Assert.All(npcs, id => Assert.Null(world.Ladder.Load(id)));
    }

    [Fact]
    public void PartialReservationFailureRollsBackEveryNpcAndKeepsTheHumanQueued() {
        var world = new World { FailAtReservation = 3 }; var match = world.Queue(ArenaKind.Ranked, 4);
        world.Fill();
        Assert.Equal(match, world.Arena.MatchOf(Human)); Assert.Equal("Open", Assert.Single(world.Arena.Snapshot()).Phase);
        Assert.Equal(2, world.Released.Count); Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
        Assert.Empty(world.Outcomes); Assert.Empty(world.Trips);
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void NpcAndHumanResultsUseTheirOwnStandingsAndPracticeDoesNotWriteRatings(ArenaKind kind) {
        var world = new World(); var match = world.Queue(kind, 1); world.Fill();
        var npc = Assert.Single(world.Players.Values.Where(p => p.Ambient));
        world.Arena.Confirm(Human, true); var run = world.Arena.RunOf(match);
        world.Arena.Started(run); world.Arena.Finish(run, 0, []);
        Assert.Single(world.Released); Assert.Equal(2, world.Outcomes.Count);
        if (kind == ArenaKind.Ranked) {
            Assert.True(world.Ladder.Load(Human)!.Rating > world.Config.StartRating);
            Assert.True(world.Ladder.Load(npc.CharId)!.Rating < world.Config.StartRating);
            Assert.Equal(1, world.Ladder.Load(npc.CharId)!.Losses);
        } else { Assert.Null(world.Ladder.Load(Human)); Assert.Null(world.Ladder.Load(npc.CharId)); }
    }

    [Fact]
    public void LostNpcAbortsTheMatchWithoutAwardsAndReleasesAllReservations() {
        var world = new World(); var match = world.Queue(ArenaKind.Ranked, 2); world.Fill();
        var npc = world.Players.Values.First(p => p.Ambient);
        world.Arena.Confirm(Human, true); var run = world.Arena.RunOf(match); world.Arena.Started(run);
        world.Arena.AmbientLost(npc.CharId);
        Assert.Empty(world.Arena.Snapshot()); Assert.Equal(3, world.Released.Count);
        Assert.True(world.Outcomes[Human].NoContest); Assert.Equal(0, world.Outcomes[Human].Tickets);
        Assert.Null(world.Ladder.Load(Human)); Assert.Null(world.Ladder.Load(npc.CharId));
    }

    [Fact]
    public void AutonomousPracticeAndRankedStartWithoutAnyHumanAndStayBounded() {
        var world = new World(); world.Arena.Tick(DateTime.UtcNow);
        Assert.Equal(2, world.Arena.Snapshot().Count);
        Assert.Equal(new[] { ArenaKind.Practice, ArenaKind.Ranked }, world.Arena.Snapshot().Select(m => m.Kind).Order().ToArray());
        Assert.All(world.Trips, trip => Assert.True(world.IsAmbient(trip.Who)));
        var initialReservations = world.Reservations;
        world.Arena.Tick(DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(2, world.Arena.Snapshot().Count); Assert.Equal(initialReservations, world.Reservations);
    }

    [Fact]
    public void AutonomousMatchesCycleEverySizeWhileRetainingStableIndividualRatings() {
        var world = new World(); var seen = new Dictionary<ArenaKind, HashSet<int>> { [ArenaKind.Practice] = [], [ArenaKind.Ranked] = [] };
        for (var round = 0; round < 4; round++) {
            world.Arena.Tick(DateTime.UtcNow.AddSeconds(round * 20));
            foreach (var match in world.Arena.Snapshot()) {
                seen[match.Kind].Add(match.TeamSize); var runId = world.Arena.RunOf(match.Id);
                var run = world.Arena.Run(runId)!;
                world.Arena.Started(runId); world.Arena.Finish(runId, 0, []);
                if (match.Kind == ArenaKind.Ranked) Assert.All(run.Side0.Concat(run.Side1), id => Assert.NotNull(world.Ladder.Load(id)));
            }
        }
        Assert.All(seen.Values, sizes => Assert.Equal(new[] { 1, 2, 3, 4 }, sizes.Order().ToArray()));
        Assert.Empty(world.Players.Values.Where(p => p.Ambient));
    }

    [Fact]
    public void HumanQueuesReceiveReservationPriorityOverNewAutonomousMatches() {
        var world = new World(); world.Queue(ArenaKind.Practice, 4);
        world.Arena.Tick(DateTime.UtcNow); Assert.Equal(0, world.Reservations); Assert.Single(world.Arena.Snapshot());
        world.Fill(); Assert.Equal(7, world.Reservations); Assert.Single(world.Arena.Snapshot());
    }

    [Fact]
    public void BothAutonomousModesVisitEveryLevelFromOneThroughFifty() {
        var world = new World(); var seen = new Dictionary<ArenaKind, HashSet<int>> { [ArenaKind.Practice] = [], [ArenaKind.Ranked] = [] };
        var now = DateTime.UtcNow;
        for (var round = 0; round < 50; round++) {
            world.Arena.Tick(now.AddMinutes(round + 1));
            foreach (var match in world.Arena.Snapshot().ToList()) {
                var runId = world.Arena.RunOf(match.Id); var run = world.Arena.Run(runId)!;
                seen[match.Kind].Add(world.Players[run.Side0[0]].Level);
                world.Arena.Started(runId); world.Arena.Finish(runId, 0, []);
            }
        }
        Assert.All(seen.Values, levels => Assert.Equal(Enumerable.Range(1, 50).ToArray(), levels.Order().ToArray()));
    }

    [Fact]
    public void SpectatingNeverSeatsOrRanksTheWatcherAndReturnsThemWhenTheMatchEnds() {
        var world = new World(); world.Arena.Tick(DateTime.UtcNow);
        var match = world.Arena.Snapshot().First(m => m.Kind == ArenaKind.Ranked); var run = world.Arena.RunOf(match.Id);
        var before = world.Arena.Run(run)!;
        world.Arena.Watch(Watcher, match.Id);
        Assert.True(world.Arena.IsSpectator(Watcher)); Assert.Equal(0UL, world.Arena.MatchOf(Watcher));
        Assert.DoesNotContain(Watcher, before.Side0.Concat(before.Side1));
        Assert.Equal(run, world.Trips.Last(t => t.Who == Watcher).Run);
        Assert.Contains(world.Sent.Where(m => m.Who == Watcher).Select(m => m.Message), m => m is GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE);
        world.Arena.Started(run); world.Arena.Finish(run, 0, []);
        Assert.False(world.Arena.IsSpectator(Watcher)); Assert.Null(world.Ladder.Load(Watcher)); Assert.DoesNotContain(Watcher, world.Outcomes.Keys);
        Assert.Equal((world.Config.HallZone, 0UL), (world.Trips.Last(t => t.Who == Watcher).Zone, world.Trips.Last(t => t.Who == Watcher).Run));
    }

    [Fact]
    public void SpectatorCanLeaveAndLateSpectatorsReturnOnAmbientFailure() {
        var world = new World(); world.Arena.Tick(DateTime.UtcNow);
        var match = world.Arena.Snapshot().First(m => m.Kind == ArenaKind.Ranked); var run = world.Arena.RunOf(match.Id);
        world.Arena.Started(run); world.Arena.Watch(Watcher, match.Id); world.Arena.Leave(Watcher);
        Assert.False(world.Arena.IsSpectator(Watcher)); Assert.Equal(0UL, world.Trips.Last(t => t.Who == Watcher).Run);
        world.Arena.Watch(Watcher, match.Id);
        world.Arena.AmbientLost(world.Arena.Run(run)!.Side0[0]);
        Assert.False(world.Arena.IsSpectator(Watcher)); Assert.Equal(0UL, world.Trips.Last(t => t.Who == Watcher).Run);
        Assert.Null(world.Ladder.Load(Watcher)); Assert.DoesNotContain(Watcher, world.Outcomes.Keys);
    }

    [Fact]
    public void AmbientFightTimeLimitEndsWithoutAwardsAndReturnsLateOnlookers() {
        var world = new World(); var match = world.Queue(ArenaKind.Ranked, 1); world.Fill();
        world.Arena.Confirm(Human, true); var run = world.Arena.RunOf(match);
        var now = DateTime.UtcNow; world.Arena.Started(run, now);
        world.Arena.Tick(now + ArenaMatchmaker.AmbientFightLimit - TimeSpan.FromSeconds(1));
        Assert.NotNull(world.Arena.Run(run));
        world.Arena.Watch(Watcher, match);
        world.Arena.Tick(now + ArenaMatchmaker.AmbientFightLimit);
        Assert.Null(world.Arena.Run(run)); Assert.True(world.Outcomes[Human].NoContest);
        Assert.Equal(0, world.Outcomes[Human].Tickets); Assert.Null(world.Ladder.Load(Human));
        Assert.False(world.Arena.IsSpectator(Watcher)); Assert.Equal(0UL, world.Trips.Last(t => t.Who == Watcher).Run);
    }

    [Fact]
    public void HumanOnlyFightsKeepTheirExistingUnlimitedDuration() {
        var world = new World { AmbientEnabled = false }; var match = world.Queue(ArenaKind.Ranked, 1);
        world.Arena.Join(Wife, match, world.Arena.TeamIdsOf(match)[1]);
        world.Arena.Confirm(Human, true); world.Arena.Confirm(Wife, true);
        var run = world.Arena.RunOf(match); var now = DateTime.UtcNow; world.Arena.Started(run, now);
        world.Arena.Tick(now + ArenaMatchmaker.AmbientFightLimit + TimeSpan.FromMinutes(1));
        Assert.NotNull(world.Arena.Run(run)); Assert.Empty(world.Outcomes); Assert.Empty(world.Released);
    }

    [Fact]
    public void AmbientOffPreservesHumanOnlyMatchesAndStopsAutonomousScheduling() {
        var world = new World { AmbientEnabled = false }; world.Queue(ArenaKind.Practice, 4);
        world.Fill(); Assert.Equal(0, world.Reservations); Assert.Single(world.Arena.Snapshot());
        world.Arena.Leave(Human); world.Arena.Tick(DateTime.UtcNow.AddMinutes(1)); Assert.Empty(world.Arena.Snapshot());
    }

    [Fact]
    public void EverySchoolLevelAndVariantHasAStableDistinctReservedIdentity() {
        var ids = new HashSet<ulong>();
        for (var level = 1; level <= 50; level++) for (var school = 0; school < 7; school++) for (var variant = 0; variant < 8; variant++) {
            var id = ArenaAmbientParticipants.IdentityId(level, school, variant);
            Assert.True(ids.Add(id)); Assert.True(ArenaAmbientParticipants.IsIdentity(id));
            Assert.Equal(id, ArenaAmbientParticipants.IdentityId(level, school, variant));
        }
        Assert.Equal(2800, ids.Count); Assert.False(ArenaAmbientParticipants.IsIdentity(Human));
    }
    private sealed class FailingLadder : IArenaLadderStore {
        internal int Batches, Rows;
        public ArenaLadderEntry? Load(ulong id) => null;
        public void Save(ArenaLadderEntry entry) => throw new InvalidOperationException("single-row save must not be used");
        public void SaveMany(IReadOnlyCollection<ArenaLadderEntry> entries) {
            Batches++; Rows = entries.Count;
            throw new InvalidOperationException("injected atomic batch failure");
        }
    }

    [Fact]
    public void FailedRankedSaveStillReleasesAllReservationsAndReturnsHumansAndSpectators() {
        var world = new World(); var match = world.Queue(ArenaKind.Ranked, 4); world.Fill();
        world.Arena.Confirm(Human, true); var run = world.Arena.RunOf(match);
        var members = world.Arena.Run(run)!.Side0.Concat(world.Arena.Run(run)!.Side1).ToList();
        world.Arena.Started(run); world.Arena.Watch(Watcher, match);
        var ladder = new FailingLadder(); world.Ladder = ladder;
        world.Arena.Finish(run, 0, []);
        Assert.Equal(1, ladder.Batches); Assert.Equal(8, ladder.Rows); Assert.Empty(world.Outcomes);
        Assert.Null(world.Arena.Run(run)); Assert.Equal(0UL, world.Arena.MatchOf(Human));
        Assert.False(world.Arena.IsSpectator(Watcher));
        Assert.All(members.Where(m => m != Human), member => Assert.Contains(member, world.Released));
        Assert.Equal(0UL, world.Trips.Last(t => t.Who == Human).Run);
        Assert.Equal(0UL, world.Trips.Last(t => t.Who == Watcher).Run);
        world.Arena.Finish(run, 0, []); Assert.Equal(1, ladder.Batches); // duplicate completion is inert
    }

    [Fact]
    public void OneFailedDeliveryDoesNotSuppressOtherResultsOrLeakThePool() {
        var world = new World(); var match = world.Queue(ArenaKind.Ranked, 1); world.Fill();
        world.Arena.Confirm(Human, true); var run = world.Arena.RunOf(match);
        var npc = world.Arena.Run(run)!.Side1.Single();
        world.Arena.Started(run); world.FailDeliveryTo = Human;
        world.Arena.Finish(run, 0, []);
        Assert.NotNull(world.Ladder.Load(Human)); Assert.NotNull(world.Ladder.Load(npc));
        Assert.Contains(npc, world.Outcomes.Keys); Assert.Contains(npc, world.Released);
        Assert.Equal(0UL, world.Trips.Last(t => t.Who == Human).Run);
        Assert.Null(world.Arena.Run(run)); Assert.Equal(0UL, world.Arena.MatchOf(Human));
    }

    private static PvPMatchInfo[] ListedRows(World world, ulong who) {
        var messages = world.Sent.Where(m => m.Who == who).Select(m => m.Message).ToArray();
        var initial = ArenaMessages.Read<NewListUpdate>(messages.OfType<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>().Last().TournamentInfo)!;
        var rows = initial.m_matches.Cast<PvPMatchInfo>().ToList();
        foreach (var message in messages.OfType<GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE>()) {
            var update = ArenaMessages.Read<TournamentUpdateList>(message.Updates)!;
            rows.AddRange(update.m_updates.OfType<AddMatchUpdate>().Select(add => (PvPMatchInfo) add.m_matchInfo));
        }
        return rows.ToArray();
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void EveryLevelHasNamedJoinableSchoolsSkillsAndSizesWithoutSpawningAnyActors(ArenaKind kind) {
        var world = new World { FriendlyEnabled = true };
        for (var level = 1; level <= 50; level++) {
            world.Players[Human] = world.Players[Human] with { Level = level };
            world.Sent.Clear();
            world.Arena.List(Human, world.Arena.TournamentId(kind), qualifiedOnly: true, qualifiedLevel: (uint) level);
            var rows = ListedRows(world, Human);
            Assert.Equal(84, rows.Length); Assert.Equal(84, rows.Select(row => row.m_matchID.Full).Distinct().Count());
            Assert.All(rows, row => {
                Assert.Equal(0, row.m_status); Assert.Empty(row.m_teams[0].m_actors);
                var opponent = Assert.IsType<PvPActor>(Assert.Single(row.m_teams[1].m_actors));
                Assert.Equal(level, opponent.m_level); Assert.Equal(4, opponent.m_nameBlob.Length);
                Assert.Equal(level, row.m_joinQueueRequirements.m_minLevel);
                Assert.Equal(level, row.m_joinQueueRequirements.m_maxLevel);
                Assert.True(row.m_teams[0].m_actors.Count < row.m_teamSize);
            });
            Assert.Equal(7, rows.Select(row => ((PvPActor) row.m_teams[1].m_actors[0]).m_sSchool).Distinct().Count());
            Assert.Equal(new uint[] { 1, 2, 3, 4 }, rows.Select(row => row.m_teamSize).Distinct().Order().ToArray());
            Assert.Equal(new[] { 400, 600, 900 }, rows.Select(row => ((PvPActor) row.m_teams[1].m_actors[0]).m_rating).Distinct().Order().ToArray());
            Assert.All(world.Sent, sent => {
                var writer = new BitWriter(); sent.Message.Encode(writer);
                Assert.InRange(writer.GetData().Length + 4, 1, ushort.MaxValue);
            });
        }
        Assert.Equal(0, world.Reservations); Assert.Empty(world.Arena.Snapshot()); Assert.Empty(world.Trips);
        Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
    }

    [Fact]
    public void NativePaginationUsesStableChallengeIdsAndCanBrowseBeyondTheViewersLevel() {
        var world = new World { FriendlyEnabled = true };
        world.Arena.List(Human, world.Arena.TournamentId(ArenaKind.Practice), numberOfElements: 5);
        var first = ListedRows(world, Human); Assert.Equal(5, first.Length);
        world.Sent.Clear();
        world.Arena.List(Human, world.Arena.TournamentId(ArenaKind.Practice), startingIndex: 5, numberOfElements: 5);
        var next = ListedRows(world, Human); Assert.Equal(5, next.Length);
        Assert.Empty(first.Select(row => row.m_matchID.Full).Intersect(next.Select(row => row.m_matchID.Full)));
        world.Sent.Clear();
        world.Arena.List(Wife, world.Arena.TournamentId(ArenaKind.Practice), startingIndex: 5, numberOfElements: 5);
        Assert.Equal(next.Select(row => row.m_matchID.Full), ListedRows(world, Wife).Select(row => row.m_matchID.Full));
        world.Sent.Clear();
        world.Arena.List(Human, world.Arena.TournamentId(ArenaKind.Practice), startingIndex: 84, numberOfElements: 5);
        Assert.All(ListedRows(world, Human), row => Assert.Equal(19, ((PvPActor) row.m_teams[1].m_actors[0]).m_level));
        var initial = ArenaMessages.Read<NewListUpdate>(world.Sent.Select(m => m.Message).OfType<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>().Single().TournamentInfo)!;
        Assert.Equal(ArenaFriendlyRoster.ChallengeCountPerKind, initial.m_totalTeams);
    }

    [Theory]
    [InlineData(ArenaKind.Practice, 1)] [InlineData(ArenaKind.Practice, 2)] [InlineData(ArenaKind.Practice, 3)] [InlineData(ArenaKind.Practice, 4)]
    [InlineData(ArenaKind.Ranked, 1)] [InlineData(ArenaKind.Ranked, 2)] [InlineData(ArenaKind.Ranked, 3)] [InlineData(ArenaKind.Ranked, 4)]
    public void JoiningDormantChallengeImmediatelyReservesLegalSeatsAndWaitsForHumanConfirmation(ArenaKind kind, int size) {
        var world = new World { FriendlyEnabled = true };
        var challenge = new ArenaFriendlyChallenge(kind, 20, 4, ArenaPvpSkill.Beginner, size);
        world.Arena.Join(Human, challenge.Id, challenge.TeamIds[0]);
        var match = Assert.Single(world.Arena.Snapshot());
        Assert.Equal((size, size), (match.Side0, match.Side1)); Assert.Equal("Confirming", match.Phase);
        Assert.Equal(size * 2 - 1, world.Reservations); Assert.Empty(world.Trips);
        Assert.All(world.Players.Values.Where(p => p.Ambient), p => {
            Assert.Equal(20, p.Level); Assert.Equal(ArenaPvpSkill.Beginner, p.FriendlySkill);
            Assert.Equal(400, world.Arena.Standing(p.CharId).Rating);
        });
        world.Arena.Confirm(Human, true); Assert.Equal(size * 2, world.Trips.Count);
        var run = world.Arena.RunOf(match.Id); world.Arena.Started(run); world.Arena.Finish(run, 0, []);
        Assert.Equal(size * 2 - 1, world.Released.Count); Assert.Empty(world.Arena.Snapshot());
        Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
    }

    [Fact]
    public void HumanCreatedMatchHasImmediateFriendlySeatsWhileFriendsOnlyStillReservesNone() {
        var world = new World { FriendlyEnabled = true };
        world.Queue(ArenaKind.Practice, 4);
        Assert.Equal(7, world.Reservations); Assert.Equal("Confirming", Assert.Single(world.Arena.Snapshot()).Phase);
        world.Arena.Leave(Human); world.Reservations = 0;
        world.Queue(ArenaKind.Practice, 4, friends: true);
        Assert.Equal(0, world.Reservations); Assert.Equal("Open", Assert.Single(world.Arena.Snapshot()).Phase);
    }

    [Fact]
    public void HumanReplacementInFriendlyMatchDoesNotTakeATicketOrAutoconfirmEitherHuman() {
        var world = new World { FriendlyEnabled = true };
        var challenge = new ArenaFriendlyChallenge(ArenaKind.Ranked, 20, 1, ArenaPvpSkill.Advanced, 1);
        world.Arena.Join(Human, challenge.Id, challenge.TeamIds[0]);
        var match = Assert.Single(world.Arena.Snapshot());
        world.Arena.Join(Wife, match.Id, world.Arena.TeamIdsOf(match.Id)[1]);
        Assert.Single(world.Released); Assert.Empty(world.Outcomes); Assert.Empty(world.Trips);
        world.Arena.Confirm(Human, true); Assert.Empty(world.Trips);
        world.Arena.Confirm(Wife, true); Assert.Equal(2, world.Trips.Count);
    }

    [Fact]
    public void BusyFriendlyPoolRetainsHumanQueueAndRollsBackPartialReservationsAtTheExistingCap() {
        var world = new World { FriendlyEnabled = true, MaxActive = 3 };
        var challenge = new ArenaFriendlyChallenge(ArenaKind.Practice, 20, 1, ArenaPvpSkill.Intermediate, 4);
        world.Arena.Join(Human, challenge.Id, challenge.TeamIds[0]);
        Assert.Equal("Open", Assert.Single(world.Arena.Snapshot()).Phase);
        Assert.Equal(3, world.Released.Count); Assert.DoesNotContain(world.Players.Values, p => p.Ambient);
        Assert.Empty(world.Trips); Assert.Empty(world.Outcomes);
    }

    [Fact]
    public void FriendlyChallengeCannotMoveAnAlreadyFightingHumanOrAcceptTheWrongLevel() {
        var world = new World { FriendlyEnabled = true };
        var wrongLevel = new ArenaFriendlyChallenge(ArenaKind.Practice, 21, 1, ArenaPvpSkill.Beginner, 1);
        world.Arena.Join(Human, wrongLevel.Id, wrongLevel.TeamIds[0]);
        Assert.Equal(0, world.Reservations); Assert.Empty(world.Arena.Snapshot());
        var challenge = wrongLevel with { Level = 20 };
        world.Arena.Join(Human, challenge.Id, challenge.TeamIds[0]); world.Arena.Confirm(Human, true);
        var match = world.Arena.MatchOf(Human); var reservations = world.Reservations;
        world.Arena.Join(Human, (challenge with { School = 2 }).Id, 0);
        Assert.Equal(match, world.Arena.MatchOf(Human)); Assert.Equal(reservations, world.Reservations);
        Assert.Equal(2, world.Trips.Count);
    }

    [Fact]
    public void AllDormantIdsRoundTripAndInvalidOrDisabledRosterNeverCreatesParticipants() {
        var challenges = Enum.GetValues<ArenaKind>().SelectMany(kind => ArenaFriendlyRoster.Challenges(kind, 20)).ToArray();
        Assert.Equal(8400, challenges.Length); Assert.Equal(8400, challenges.Select(c => c.Id).Distinct().Count());
        Assert.All(challenges, c => {
            Assert.Equal(c, ArenaFriendlyRoster.Read(c.Id)); Assert.Equal(c, ArenaFriendlyRoster.Read(c.TeamIds[0]));
            Assert.Equal(c, ArenaFriendlyRoster.Read(c.TeamIds[1])); Assert.Null(ArenaFriendlyRoster.Read(c.Id + 3));
        });
        Assert.Null(ArenaFriendlyRoster.Read(ArenaFriendlyRoster.FirstId - 1));
        Assert.Null(ArenaFriendlyRoster.Read(ArenaFriendlyRoster.FirstId + 8400 * 4));
        var world = new World { FriendlyEnabled = true, AmbientEnabled = false };
        world.Arena.List(Human, world.Arena.TournamentId(ArenaKind.Practice));
        Assert.Empty(ListedRows(world, Human));
        world.Arena.Join(Human, challenges.First(c => c.Level == 20).Id, 0);
        Assert.Equal(0, world.Reservations); Assert.Empty(world.Arena.Snapshot());
    }

    [Fact]
    public void RealHumanRowsComeBeforeTheDormantRosterAndQuiesceReleasesOnlyReservedParticipants() {
        var world = new World { FriendlyEnabled = true };
        var match = world.Queue(ArenaKind.Practice, 1, friends: true);
        world.Arena.List(Wife, world.Arena.TournamentId(ArenaKind.Practice), numberOfElements: 1);
        Assert.Equal(match, Assert.Single(ListedRows(world, Wife)).m_matchID.Full);
        world.Arena.Leave(Human); world.Queue(ArenaKind.Ranked, 4);
        world.Arena.QuiesceAsync().GetAwaiter().GetResult();
        Assert.Equal(7, world.Released.Count); Assert.Empty(world.Arena.Snapshot()); Assert.Empty(world.Outcomes);
        var sent = world.Sent.Count; world.Arena.List(Human, world.Arena.TournamentId(ArenaKind.Practice));
        Assert.Equal(sent, world.Sent.Count);
    }

    [Fact]
    public void ViewersOfEveryRankKeepEligibleChoicesAndSchoolSelectionSurvivesReservation() {
        var world = new World { FriendlyEnabled = true };
        for (var rank = 0; rank < world.Config.Ranks.Length; rank++) {
            world.Sent.Clear();
            world.Arena.List(Human, world.Arena.TournamentId(ArenaKind.Ranked), qualifiedOnly: true,
                qualifiedLevel: 20, qualifiedRank: rank);
            Assert.Equal(84, ListedRows(world, Human).Length);
        }
        var challenge = new ArenaFriendlyChallenge(ArenaKind.Ranked, 20, 4, ArenaPvpSkill.Advanced, 1);
        world.Arena.Join(Human, challenge.Id, challenge.TeamIds[0]);
        Assert.Equal("Life", Assert.Single(world.Players.Values.Where(p => p.Ambient)).School);
    }

}
