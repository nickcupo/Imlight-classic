// CLASSIC: advertised r806919 match states agree with the real server's Join and Watch admission.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.WizardData.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaNativeMatchStatusTests {
    private const ulong Human = 1000, Opponent = 2000, Watcher = 3000, Offline = 4000;

    public ArenaNativeMatchStatusTests() {
        var root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var path = Path.Combine(root, "arena-native-status-tests-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={root}/arena-native-status-tests.log\n");
        ConfigurationManager.Initialize(path);
    }

    private sealed class World : IArenaWorld, IArenaAmbientWorld, IArenaFriendlyWorld {
        internal readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        internal readonly Dictionary<ulong, ArenaPlayer> Players = [];
        internal readonly List<(ulong Who, IMessage Message)> Sent = [];
        internal readonly List<(ulong Who, ulong Run)> Trips = [];
        internal Action<ulong, IMessage>? Observe;
        private ulong _nextRun = 0xA000;
        public bool AmbientEnabled { get; set; } = true;
        public bool FriendlyEnabled { get; set; }
        public IArenaLadderStore Ladder { get; } = new ArenaLadderCollection.Memory();
        internal ArenaMatchmaker Arena { get; }

        internal World() {
            foreach (var id in new[] { Human, Opponent, Watcher })
                Players[id] = new ArenaPlayer(id, id, [0x82, 1, 2, 3], "authored human", 20, "Fire", 0);
            Arena = new ArenaMatchmaker(Config, this);
        }

        public ArenaPlayer? Player(ulong id) => Players.GetValueOrDefault(id);
        public bool AreFriends(ulong first, ulong second) => first is Human or Opponent && second is Human or Opponent;
        public void Send(ulong id, IMessage message) { Sent.Add((id, message)); Observe?.Invoke(id, message); }
        public void Inform(ulong id, string text) { }
        public void Travel(ulong id, string zone, string location, ulong run) => Trips.Add((id, run));
        public void Deliver(ulong id, ArenaOutcome outcome) { }
        public ulong NewRunId() => ++_nextRun;
        public string? ZoneOf(ulong id) => Config.HallZone;
        public bool IsAmbient(ulong id) => Players.GetValueOrDefault(id)?.Ambient == true;
        public ArenaPlayer? ReserveAmbient(int level, int preferredSchool) => Reserve(level, preferredSchool, null);
        public ArenaPlayer? ReserveFriendly(int level, int school, ArenaPvpSkill skill) => Reserve(level, school, skill);
        public ArenaPlayer? PreviewFriendly(int level, int school, ArenaPvpSkill skill)
            => AuthoredAmbient(level, school, (int) skill * 2, skill);
        public void ReleaseAmbient(ulong id) => Players.Remove(id);

        private ArenaPlayer? Reserve(int level, int school, ArenaPvpSkill? skill) {
            for (var variant = 0; variant < 8; variant++) {
                var player = AuthoredAmbient(level, school, variant, skill);
                if (Players.ContainsKey(player.CharId)) continue;
                Players[player.CharId] = player;
                return player;
            }
            return null;
        }

        private static ArenaPlayer AuthoredAmbient(int level, int school, int variant, ArenaPvpSkill? skill) {
            var id = ArenaAmbientParticipants.IdentityId(level, school, variant);
            return new ArenaPlayer(id, id, [0x82, 2, 3, 4], "authored friendly", level,
                ((Imlight.Classic.Ambient.AmbientSchool) school).ToString(), 0, true, skill);
        }

        internal IEnumerable<T> Messages<T>(ulong id) => Sent.Where(s => s.Who == id).Select(s => s.Message).OfType<T>();
        internal ulong Create(ArenaKind kind) {
            Arena.Create(Human, kind, ArenaRules.Hash(ArenaRules.MatchName(
                kind == ArenaKind.Practice ? Config.PracticeTournament : Config.RankedTournament, 1)), 0, 0, false);
            return Arena.MatchOf(Human);
        }
    }

    // CLASSIC: native PvPWindow's proven match-row gate, independent of actor/status-window codes.
    private static bool NativeJoinAdmits(PvPMatchInfo row) => row.m_status == 0;
    private static bool NativeWatchAdmits(PvPMatchInfo row) => row.m_status == 4;

    private static PvPMatchInfo[] ReadList(World world, ArenaKind kind, bool watch = false) {
        world.Sent.Clear();
        world.Arena.List(Watcher, world.Arena.TournamentId(kind), requestType: watch ? 5u : 0u);
        var initial = ArenaMessages.ReadBrowser<NewListUpdate>(world.Messages<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>(Watcher).Single().TournamentInfo)!;
        var rows = initial.m_matches.Cast<PvPMatchInfo>().ToList();
        foreach (var message in world.Messages<GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE>(Watcher))
            rows.AddRange(ArenaMessages.Read<TournamentUpdateList>(message.Updates)!.m_updates
                .OfType<AddMatchUpdate>().Select(add => (PvPMatchInfo) add.m_matchInfo));
        return rows.ToArray();
    }

    private static void RefusesWatch(World world, ulong matchId, ulong viewer = Watcher) {
        var trips = world.Trips.Count;
        world.Arena.Watch(viewer, matchId);
        Assert.False(world.Arena.IsSpectator(viewer));
        Assert.Equal(trips, world.Trips.Count);
        Assert.Equal(ArenaRules.Hash(ArenaErrors.MatchStarted), world.Messages<WIZARD_12_PROTOCOL.MSG_ARENA_ERROR>(viewer).Last().Error);
    }

    [Theory]
    [InlineData(ArenaKind.Practice, false)] [InlineData(ArenaKind.Practice, true)]
    [InlineData(ArenaKind.Ranked, false)] [InlineData(ArenaKind.Ranked, true)]
    public void LiveAutonomousWireRowsAreAdmittedByWatchAndRefuseJoin(ArenaKind kind, bool fighting) {
        var world = new World();
        world.Arena.Tick(DateTime.UtcNow);
        var match = world.Arena.Snapshot().Single(m => m.Kind == kind);
        var run = world.Arena.RunOf(match.Id);
        if (fighting) world.Arena.Started(run);
        Assert.Equal(fighting ? "Fighting" : "Travelling", world.Arena.Snapshot().Single(m => m.Id == match.Id).Phase);
        var row = Assert.Single(ReadList(world, kind, watch: true));
        Assert.Equal(match.Id, row.m_matchID.Full);
        Assert.True(NativeWatchAdmits(row));
        Assert.False(NativeJoinAdmits(row));
        Assert.All(row.m_teams, team => Assert.NotEmpty(team.m_actors));

        world.Arena.Join(Watcher, match.Id, world.Arena.TeamIdsOf(match.Id)[0]);
        Assert.Equal(0UL, world.Arena.MatchOf(Watcher));
        Assert.Equal(ArenaRules.Hash(ArenaErrors.MatchStarted), world.Messages<WIZARD_12_PROTOCOL.MSG_ARENA_ERROR>(Watcher).Last().Error);
        world.Arena.Watch(Watcher, match.Id);
        Assert.True(world.Arena.IsSpectator(Watcher, match.Id));
        Assert.Equal(run, world.Trips.Last(t => t.Who == Watcher).Run);
        Assert.Equal(0UL, world.Arena.MatchOf(Watcher));
        Assert.DoesNotContain(Watcher, world.Arena.Run(run)!.Side0.Concat(world.Arena.Run(run)!.Side1));
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void BrowserUpdatesAdmitWatchOnlyAfterTheServerCanAcceptSpectators(ArenaKind kind) {
        var world = new World();
        Assert.Empty(ReadList(world, kind, watch: true));
        var advertisedRows = new List<PvPMatchInfo>();
        world.Observe = (who, message) => {
            if (who != Watcher) return;
            IEnumerable<PvPMatchInfo> rows = message switch {
                GAME_5_PROTOCOL.MSG_PVPUPDATEINFO initial => ArenaMessages.ReadBrowser<NewListUpdate>(initial.TournamentInfo)!.m_matches.Cast<PvPMatchInfo>(),
                GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE update => ArenaMessages.Read<TournamentUpdateList>(update.Updates)!.m_updates
                    .OfType<AddMatchUpdate>().Select(add => (PvPMatchInfo) add.m_matchInfo),
                _ => [],
            };
            foreach (var row in rows) { Assert.True(NativeWatchAdmits(row)); advertisedRows.Add(row); }
        };
        world.Arena.Tick(DateTime.UtcNow);
        world.Observe = null;
        var match = world.Arena.Snapshot().Single(m => m.Kind == kind);
        var advertised = Assert.Single(advertisedRows);
        Assert.Equal(match.Id, advertised.m_matchID.Full);
        Assert.True(NativeWatchAdmits(advertised));
        world.Arena.Watch(Watcher, match.Id);
        Assert.True(world.Arena.IsSpectator(Watcher, match.Id));
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void OpenAndReplaceableHumanRowsRetainNativeJoinStatusUntilConfirmation(ArenaKind kind) {
        var world = new World();
        var match = world.Create(kind);
        Assert.Equal("Open", Assert.Single(world.Arena.Snapshot()).Phase);
        var open = Assert.Single(ReadList(world, kind));
        Assert.True(NativeJoinAdmits(open)); Assert.False(NativeWatchAdmits(open));
        RefusesWatch(world, match);
        world.Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.AmbientWaitSeconds + 1));
        Assert.Equal("Confirming", Assert.Single(world.Arena.Snapshot()).Phase);
        var replaceable = Assert.Single(ReadList(world, kind));
        Assert.True(NativeJoinAdmits(replaceable)); Assert.False(NativeWatchAdmits(replaceable));
        var vacancy = Assert.IsType<MatchActor>(Assert.Single(replaceable.m_teams[1].m_actors));
        Assert.Equal(0UL, vacancy.m_nActorID.Full); Assert.Equal(0, vacancy.m_status);
        RefusesWatch(world, match);
        world.Arena.Join(Opponent, match, world.Arena.TeamIdsOf(match)[1]);
        Assert.Equal(match, world.Arena.MatchOf(Opponent));
        Assert.Equal("Confirming", Assert.Single(world.Arena.Snapshot()).Phase);
        Assert.Empty(ReadList(world, kind)); // existing human-only full-match browser policy is retained
        RefusesWatch(world, match);
        world.Arena.Confirm(Human, true); world.Arena.Confirm(Opponent, true);
        Assert.Equal("Travelling", Assert.Single(world.Arena.Snapshot()).Phase);
        Assert.Empty(ReadList(world, kind));
        Assert.Equal(match, Assert.Single(ReadList(world, kind, watch: true)).m_matchID.Full);
        world.Arena.Watch(Watcher, match);
        Assert.True(world.Arena.IsSpectator(Watcher, match));
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void DormantFriendlyRowsRemainNativeJoinChoicesAndCannotBeWatched(ArenaKind kind) {
        var world = new World { FriendlyEnabled = true };
        var rows = ReadList(world, kind);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => { Assert.True(NativeJoinAdmits(row)); Assert.False(NativeWatchAdmits(row)); });
        Assert.Empty(world.Arena.Snapshot());
        Assert.DoesNotContain(world.Players.Values, player => player.Ambient);
        RefusesWatch(world, rows[0].m_matchID.Full);
        Assert.Empty(world.Arena.Snapshot());
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void WatchableRowDoesNotBypassParticipantOrOnlinePlayerChecks(ArenaKind kind) {
        var world = new World();
        world.Arena.Tick(DateTime.UtcNow);
        var row = Assert.Single(ReadList(world, kind, watch: true));
        Assert.True(NativeWatchAdmits(row));
        var run = world.Arena.Run(world.Arena.RunOf(row.m_matchID.Full))!;
        var participant = run.Side0[0];
        RefusesWatch(world, row.m_matchID.Full, participant);
        Assert.Equal(row.m_matchID.Full, world.Arena.MatchOf(participant));
        RefusesWatch(world, row.m_matchID.Full, Offline);
        Assert.Equal(0UL, world.Arena.MatchOf(Offline));
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void CompletedWatchableMatchIsRemovedAndCannotAcceptAnotherSpectator(ArenaKind kind) {
        var world = new World();
        world.Arena.Tick(DateTime.UtcNow);
        var row = Assert.Single(ReadList(world, kind, watch: true));
        Assert.True(NativeWatchAdmits(row));
        var run = world.Arena.RunOf(row.m_matchID.Full);
        world.Arena.Watch(Watcher, row.m_matchID.Full);
        world.Arena.Started(run);
        world.Arena.Finish(run, 0, []);
        Assert.False(world.Arena.IsSpectator(Watcher));
        Assert.Null(world.Arena.Run(run));
        var cleared = ArenaMessages.ReadBrowser<NewListUpdate>(world.Messages<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>(Watcher).Last().TournamentInfo)!;
        Assert.True(cleared.m_clearData); Assert.Empty(cleared.m_matches); Assert.Equal(0, cleared.m_totalTeams);
        Assert.Empty(ReadList(world, kind, watch: true));
        RefusesWatch(world, row.m_matchID.Full);
    }
}
