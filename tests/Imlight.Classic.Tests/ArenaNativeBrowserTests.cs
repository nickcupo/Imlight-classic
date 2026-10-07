// CLASSIC: replay the native browser's match/team maps and its PRE-update nested count contract.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaNativeBrowserTests {
    private const ulong Viewer = 9000;

    public ArenaNativeBrowserTests() {
        var root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var path = Path.Combine(root, "arena-native-browser-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={root}/arena-native-browser.log\n");
        ConfigurationManager.Initialize(path);
    }

    private sealed class World : IArenaWorld, IArenaAmbientWorld, IArenaFriendlyWorld {
        internal readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        internal readonly Dictionary<ulong, ArenaPlayer> Players = [];
        internal readonly List<(ulong Who, IMessage Message)> Sent = [];
        internal readonly ArenaMatchmaker Arena;
        internal int Reservations;
        private ulong _runs = 0xA000;
        public bool AmbientEnabled { get; set; }
        public bool FriendlyEnabled { get; set; }
        public IArenaLadderStore Ladder { get; } = new ArenaLadderCollection.Memory();

        internal World(bool friendly = false) {
            AmbientEnabled = FriendlyEnabled = friendly;
            foreach (var id in Enumerable.Range(1000, 10).Select(id => (ulong) id).Append(Viewer))
                Players[id] = new ArenaPlayer(id, id + 77, [0x82, 1, 2, 3], "authored human", 20, "Fire", 0);
            Arena = new ArenaMatchmaker(Config, this);
        }

        public ArenaPlayer? Player(ulong id) => Players.GetValueOrDefault(id);
        public bool AreFriends(ulong first, ulong second) => false;
        public void Send(ulong id, IMessage message) => Sent.Add((id, message));
        public void Inform(ulong id, string text) { }
        public void Travel(ulong id, string zone, string location, ulong run) { }
        public void Deliver(ulong id, ArenaOutcome outcome) { }
        public ulong NewRunId() => ++_runs;
        public string? ZoneOf(ulong id) => Config.HallZone;
        public bool IsAmbient(ulong id) => Players.GetValueOrDefault(id)?.Ambient == true;
        public ArenaPlayer? PreviewFriendly(int level, int school, ArenaPvpSkill skill) => Friendly(level, school, (int) skill * 2, skill);
        public ArenaPlayer? ReserveAmbient(int level, int preferredSchool) => Reserve(level, preferredSchool, null);
        public ArenaPlayer? ReserveFriendly(int level, int school, ArenaPvpSkill skill) => Reserve(level, school, skill);
        public void ReleaseAmbient(ulong id) => Players.Remove(id);

        private ArenaPlayer? Reserve(int level, int school, ArenaPvpSkill? skill) {
            Reservations++;
            for (var variant = 0; variant < 8; variant++) {
                var player = Friendly(level, school, variant, skill);
                if (Players.TryAdd(player.CharId, player)) return player;
            }
            return null;
        }

        private static ArenaPlayer Friendly(int level, int school, int variant, ArenaPvpSkill? skill) {
            var id = ArenaAmbientParticipants.IdentityId(level, school, variant);
            return new ArenaPlayer(id, id, [0x82, 2, 3, 4], "authored friendly", level,
                ((Imlight.Classic.Ambient.AmbientSchool) school).ToString(), 0, true, skill);
        }

        internal ulong Create(ArenaKind kind, int size, ulong creator = 1000, bool friends = true) {
            Arena.Create(creator, kind, ArenaRules.Hash(ArenaRules.MatchName(Arena.Tournament(kind), size)), 0, 0, friends);
            return Arena.MatchOf(creator);
        }

        internal void List(ArenaKind kind, uint mode, int start = 0, int count = 4, bool onlyLevel = true)
            => Arena.List(Viewer, Arena.TournamentId(kind), start, count, qualifiedOnly: onlyLevel,
                qualifiedLevel: 20, requestType: mode);
    }

    private sealed class NativePage {
        internal readonly Dictionary<ulong, PvPMatchInfo> Matches = [];
        internal readonly Dictionary<ulong, MatchTeam> Teams = [];
        internal int Total, Initials, Continuations, RemovedMatches, RemovedTeams;

        internal void Read(World world, int from = 0) {
            foreach (var (_, message) in world.Sent.Skip(from).Where(sent => sent.Who == Viewer)) {
                var writer = new BitWriter(); message.Encode(writer);
                Assert.InRange(writer.GetData().Length + 4, 1, ushort.MaxValue);
                if (message is GAME_5_PROTOCOL.MSG_PVPUPDATEINFO initial) {
                    var list = ArenaMessages.Read<NewListUpdate>(initial.TournamentInfo)!;
                    Assert.True(list.m_clearData);
                    Matches.Clear(); Teams.Clear(); Total = list.m_totalTeams; Initials++;
                    foreach (var match in list.m_matches) AddMatch(Assert.IsType<PvPMatchInfo>(match));
                    foreach (var team in list.m_teams) AddTeam(team);
                } else if (message is GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE continuation) {
                    var update = ArenaMessages.Read<TournamentUpdateList>(continuation.Updates)!;
                    // Native GetCounts compares BEFORE applying children and includes nested plus standalone views.
                    var teams = Matches.Values.SelectMany(match => match.m_teams).Concat(Teams.Values).ToArray();
                    Assert.Equal(Matches.Count, update.m_matchCount);
                    Assert.Equal(teams.Length, update.m_teamCount);
                    Assert.Equal(teams.Sum(team => team.m_actors.Count(actor => actor.m_status != 0)), update.m_actorCount);
                    Continuations++;
                    foreach (var child in update.m_updates) {
                        switch (child) {
                            case AddMatchUpdate add: AddMatch(Assert.IsType<PvPMatchInfo>(add.m_matchInfo)); break;
                            case AddTeamUpdate add: AddTeam(add.m_team); break;
                            case RemoveMatchUpdate remove: Assert.True(Matches.Remove(remove.m_matchID.Full)); RemovedMatches++; break;
                            case RemoveTeamUpdate remove: Assert.True(Teams.Remove(remove.m_teamID.Full)); RemovedTeams++; break;
                            default: Assert.Fail("Unexpected browser update type."); break;
                        }
                    }
                }
                Assert.All(Teams.Values, team => Assert.Contains(team.m_matchId.Full, Matches.Keys));
            }
        }

        private void AddMatch(PvPMatchInfo match) {
            Assert.True(Matches.TryAdd(match.m_matchID.Full, match));
            Assert.Equal(2, match.m_teams.Count);
            Assert.All(match.m_teams, team => {
                Assert.Equal(match.m_matchID.Full, team.m_matchId.Full);
                Assert.Equal((int) match.m_teamSize, team.m_actors.Count);
                Assert.All(team.m_actors.Where(actor => actor.m_status == 0), actor => {
                    Assert.IsType<MatchActor>(actor); Assert.Equal(0UL, actor.m_nActorID.Full);
                    Assert.Equal(match.m_matchID.Full, actor.m_nMatchID.Full);
                    Assert.Equal(team.m_nTeamID.Full, actor.m_nTeamID.Full);
                });
            });
        }

        private void AddTeam(MatchTeam team) {
            Assert.True(Teams.TryAdd(team.m_nTeamID.Full, team));
            Assert.Contains(team.m_matchId.Full, Matches.Keys);
            Assert.Contains(Matches[team.m_matchId.Full].m_teams, nested => nested.m_nTeamID.Full == team.m_nTeamID.Full);
        }
    }

    // CLASSIC: native PrePvPKiosk clears TournamentInfo before echoing; SendUpdateRequest refuses
    // name id zero. Exercise guard entry without the old fixture's direct, preinitialized List call.
    [Theory]
    [InlineData(ArenaKind.Practice, 1)] [InlineData(ArenaKind.Practice, 20)] [InlineData(ArenaKind.Practice, 50)]
    [InlineData(ArenaKind.Ranked, 1)] [InlineData(ArenaKind.Ranked, 20)] [InlineData(ArenaKind.Ranked, 50)]
    public void ReadyGuardEchoInitializesNativeTournamentAndFriendlyRowsBeforeAnyListRequest(ArenaKind kind, int level) {
        var world = new World(friendly: true);
        world.Players[Viewer] = world.Players[Viewer] with { Level = level };
        world.Arena.OpenKiosk(Viewer, kind, 123);
        var prep = Assert.IsType<WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK>(Assert.Single(world.Sent).Message);
        Assert.Equal(world.Arena.TournamentId(kind), prep.TournamentNameID);
        Assert.True(world.Arena.CompleteKiosk(Viewer, prep));
        var initial = Assert.IsType<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>(world.Sent[1].Message);
        var list = ArenaMessages.Read<NewListUpdate>(initial.TournamentInfo)!;
        Assert.NotEqual(0u, list.m_tournamentNameID);
        Assert.Equal(prep.TournamentNameID, list.m_tournamentNameID);
        Assert.Equal(world.Arena.Tournament(kind), list.m_tournamentName);
        Assert.Equal(world.Players[Viewer].ActorId, initial.CharacterID);
        Assert.True(list.m_totalTeams > 0);
        var page = new NativePage(); page.Read(world);
        Assert.NotEmpty(page.Matches);
        Assert.Equal(kind == ArenaKind.Ranked ? 4 : 0, page.Teams.Count);
        Assert.All(page.Matches.Values, row => Assert.Equal(0, row.m_status));
        Assert.Equal(0, world.Reservations); // Browsing must not allocate NPC actors or matches.
        var count = world.Sent.Count;
        Assert.False(world.Arena.CompleteKiosk(Viewer, prep));
        Assert.Equal(count, world.Sent.Count);
        // The initialized native name id now permits an ordinary filtered browser request.
        world.Arena.List(Viewer, list.m_tournamentNameID, numberOfElements: 4, requestType: 1);
        page = new NativePage(); page.Read(world, count);
        Assert.NotEmpty(page.Matches);
        Assert.All(page.Matches.Values, row => Assert.Equal(1u, row.m_teamSize));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void MismatchedOrStillPatchingEchoCannotInitializeOrConsumePendingGuard(int defect) {
        var world = new World(friendly: true);
        world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123);
        var valid = Assert.IsType<WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK>(Assert.Single(world.Sent).Message);
        var bad = ArenaMessages.Kiosk(valid.TournamentNameID, valid.MobileID);
        switch (defect) {
            case 0: bad.TournamentNameID = world.Arena.TournamentId(ArenaKind.Ranked); break;
            case 1: bad.MobileID++; break;
            case 2: bad.Patching = 1; break;
            case 3: bad.LeagueID = 1; break;
            case 4: bad.SeasonID = 1; break;
        }
        Assert.False(world.Arena.CompleteKiosk(Viewer, bad)); Assert.Single(world.Sent);
        Assert.True(world.Arena.CompleteKiosk(Viewer, valid));
    }

    [Fact]
    public void FormerGuardEchoCannotOverwriteTheNewNativeTournament() {
        var world = new World(friendly: true);
        world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123);
        var former = (WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK)world.Sent[0].Message;
        world.Arena.OpenKiosk(Viewer, ArenaKind.Ranked, 124);
        var current = (WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK)world.Sent[1].Message;
        Assert.False(world.Arena.CompleteKiosk(Viewer, former)); Assert.Equal(2, world.Sent.Count);
        Assert.True(world.Arena.CompleteKiosk(Viewer, current));
        var initial = Assert.IsType<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>(world.Sent[2].Message);
        Assert.Equal(current.TournamentNameID, ArenaMessages.Read<NewListUpdate>(initial.TournamentInfo)!.m_tournamentNameID);
    }

    [Fact]
    public void EchoCannotCrossArenaServiceSessionsEvenForTheSameCharacterAndGuard() {
        var world = new World(friendly: true); var former = new object(); var current = new object();
        world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123, former);
        var echo = ArenaMessages.Kiosk(world.Arena.TournamentId(ArenaKind.Practice), 123);
        world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123, current);
        Assert.False(world.Arena.CompleteKiosk(Viewer, echo, former)); Assert.Equal(2, world.Sent.Count);
        Assert.False(world.Arena.CompleteKiosk(Viewer, echo)); Assert.Equal(2, world.Sent.Count);
        Assert.True(world.Arena.CompleteKiosk(Viewer, echo, current));
    }

    [Fact]
    public void FormerSessionDisposalCannotRemoveItsReplacementSessionsPendingGuard() {
        var world = new World(friendly: true); var former = new object(); var current = new object();
        world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123, former);
        world.Arena.OpenKiosk(Viewer, ArenaKind.Ranked, 124, current);
        world.Arena.RetireKiosk(Viewer, former);
        var echo = ArenaMessages.Kiosk(world.Arena.TournamentId(ArenaKind.Ranked), 124);
        Assert.True(world.Arena.CompleteKiosk(Viewer, echo, current));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ProductionArenaServiceRetiresItsPendingGuardOnGracefulDisposeOrStop(bool graceful) {
        using var system = ActorSystem.Create("arena-kiosk-lifecycle", "akka.actor.provider = local");
        var actor = system.ActorOf(Props.Create(() => new ArenaService(null!)), "arena");
        var world = new World(friendly: true);
        var identity = await actor.Ask<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY>(
            new SERVICE_101_PROTOCOL.MSG_QUERYMESSAGESERVICEIDENTITY(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var service = Assert.IsType<ArenaService>(identity.Service);
        typeof(ArenaService).GetField("_charId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, Viewer);
        typeof(ArenaService).GetField("_kioskArena", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, world.Arena);
        world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123, service);
        var echo = ArenaMessages.Kiosk(world.Arena.TournamentId(ArenaKind.Practice), 123);
        try {
            if (graceful) {
                await actor.Ask<SERVICE_101_PROTOCOL.MSG_PREDISPOSE>(new SERVICE_101_PROTOCOL.MSG_PREDISPOSE(),
                    TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.False(world.Arena.CompleteKiosk(Viewer, echo, service));
            }
        }
        finally { await system.Terminate(); }
        Assert.False(world.Arena.CompleteKiosk(Viewer, echo, service)); Assert.Single(world.Sent);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void UnsolicitedOfflineMissingViewerAndStoppedEchoesCannotPublishABrowser(int reason) {
        var world = new World(friendly: true);
        var echo = ArenaMessages.Kiosk(world.Arena.TournamentId(ArenaKind.Practice), 123);
        if (reason != 0) world.Arena.OpenKiosk(Viewer, ArenaKind.Practice, 123);
        if (reason == 1) world.Arena.Offline(Viewer);
        if (reason == 2) world.Players.Remove(Viewer);
        if (reason == 3) world.Arena.QuiesceAsync().GetAwaiter().GetResult();
        var count = world.Sent.Count;
        Assert.False(world.Arena.CompleteKiosk(Viewer, echo)); Assert.Equal(count, world.Sent.Count);
        if (reason == 1) {
            // Even a still-visible player record cannot revive a previous session's pending kiosk.
            Assert.False(world.Arena.CompleteKiosk(Viewer, echo));
        }
    }

    [Theory]
    [InlineData(ArenaKind.Practice, 1)] [InlineData(ArenaKind.Practice, 2)] [InlineData(ArenaKind.Practice, 3)] [InlineData(ArenaKind.Practice, 4)]
    [InlineData(ArenaKind.Ranked, 1)] [InlineData(ArenaKind.Ranked, 2)] [InlineData(ArenaKind.Ranked, 3)] [InlineData(ArenaKind.Ranked, 4)]
    public void NativeFourRowPagesFilterSizeBeforePagingAndReachTheLastRow(ArenaKind kind, int size) {
        var world = new World(friendly: true);
        world.List(kind, (uint) size);
        var first = new NativePage(); first.Read(world);
        var teamRows = kind == ArenaKind.Ranked;
        var total = 21 * (teamRows ? 2 : 1);
        Assert.Equal(total, first.Total);
        Assert.Equal(teamRows ? 2 : 4, first.Matches.Count); Assert.Equal(teamRows ? 4 : 0, first.Teams.Count);
        Assert.All(first.Matches.Values, match => { Assert.Equal((uint) size, match.m_teamSize); Assert.Equal(0, match.m_status); });
        var offset = world.Sent.Count;
        world.List(kind, (uint) size, total - 1);
        var last = new NativePage(); last.Read(world, offset);
        Assert.Equal(total, last.Total); Assert.Single(last.Matches); Assert.Equal(teamRows ? 1 : 0, last.Teams.Count);
        Assert.Empty(first.Matches.Keys.Intersect(last.Matches.Keys));
        Assert.Equal(0, world.Reservations); Assert.Empty(world.Arena.Snapshot());
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void ContinuationsUsePreviousNestedMapCountsAndKeepFullDomainPagination(ArenaKind kind) {
        var world = new World(friendly: true);
        world.List(kind, 0, count: 84);
        var page = new NativePage(); page.Read(world);
        Assert.Equal(kind == ArenaKind.Ranked ? 168 : 84, page.Total);
        Assert.Equal(kind == ArenaKind.Ranked ? 42 : 84, page.Matches.Count);
        Assert.Equal(kind == ArenaKind.Ranked ? 84 : 0, page.Teams.Count);
        Assert.True(page.Continuations > 0);
        Assert.All(page.Matches.Values, match => Assert.Equal(0, match.m_status));
    }

    [Theory]
    [InlineData(ArenaKind.Practice, 1)] [InlineData(ArenaKind.Practice, 2)] [InlineData(ArenaKind.Practice, 3)] [InlineData(ArenaKind.Practice, 4)]
    [InlineData(ArenaKind.Ranked, 1)] [InlineData(ArenaKind.Ranked, 2)] [InlineData(ArenaKind.Ranked, 3)] [InlineData(ArenaKind.Ranked, 4)]
    public void WatchSizeModesContainOnlyLiveMatchesWithoutDormantOrStandaloneTeams(ArenaKind kind, int size) {
        var world = new World(friendly: true);
        var live = world.Create(kind, size, friends: false);
        world.Arena.Confirm(1000, true);
        Assert.Equal("Travelling", world.Arena.Snapshot().Single(match => match.Id == live).Phase);
        world.Create(kind, size, creator: 1001);
        world.List(kind, (uint) (size + 5));
        var page = new NativePage(); page.Read(world);
        Assert.Equal(1, page.Total); Assert.Empty(page.Teams);
        var row = Assert.Single(page.Matches.Values);
        Assert.Equal(live, row.m_matchID.Full); Assert.Equal(4, row.m_status); Assert.Equal((uint) size, row.m_teamSize);
        var offset = world.Sent.Count;
        world.List(kind, (uint) ((size % 4 + 1) + 5));
        var different = new NativePage(); different.Read(world, offset);
        Assert.Equal(0, different.Total); Assert.Empty(different.Matches); Assert.Empty(different.Teams);
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void StableDomainReplacementRemovesBothMapsAndUnavailableMatchClearsCurrentPage(ArenaKind kind) {
        var world = new World();
        var match = world.Create(kind, 2, friends: false);
        world.List(kind, 2);
        var page = new NativePage(); page.Read(world);
        var offset = world.Sent.Count;
        world.Arena.Join(1001, match, world.Arena.TeamIdsOf(match)[1]);
        page.Read(world, offset);
        Assert.Equal(1, page.RemovedMatches); Assert.Equal(kind == ArenaKind.Ranked ? 2 : 0, page.RemovedTeams);
        Assert.Equal(2, page.Matches.Values.Single().m_teams.Sum(team => team.m_actors.Count(actor => actor.m_status != 0)));
        offset = world.Sent.Count;
        world.Arena.Join(1002, match, world.Arena.TeamIdsOf(match)[0]);
        world.Arena.Join(1003, match, world.Arena.TeamIdsOf(match)[1]);
        page.Read(world, offset);
        Assert.Equal(0, page.Total); Assert.Empty(page.Matches); Assert.Empty(page.Teams);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SwitchingGuardsStopsFormerTournamentUpdates(bool kiosk) {
        var world = new World();
        world.List(ArenaKind.Practice, 0);
        if (kiosk) world.Arena.OpenKiosk(Viewer, ArenaKind.Ranked, 123);
        else world.List(ArenaKind.Ranked, 0);
        var offset = world.Sent.Count;
        world.Create(ArenaKind.Practice, 1);
        Assert.DoesNotContain(world.Sent.Skip(offset), sent => sent.Who == Viewer);
    }

    [Fact]
    public void SizeFilteredMutationDoesNotLeakIntoTheCurrentPage() {
        var world = new World(); world.List(ArenaKind.Ranked, 1);
        var offset = world.Sent.Count;
        world.Create(ArenaKind.Ranked, 2);
        Assert.DoesNotContain(world.Sent.Skip(offset), sent => sent.Who == Viewer);
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void DormantTeamOneTargetJoinsTheRequestedSide(ArenaKind kind) {
        var world = new World(friendly: true); world.List(kind, 1);
        var page = new NativePage(); page.Read(world);
        var row = page.Matches.Values.First();
        world.Arena.Join(1000, row.m_matchID.Full, row.m_teams[1].m_nTeamID.Full);
        var made = Assert.Single(world.Arena.Snapshot());
        var run = world.Arena.RunOf(made.Id);
        Assert.Equal("Confirming", made.Phase); Assert.Equal(0UL, run);
        world.Arena.Confirm(1000, true);
        Assert.Contains(1000UL, world.Arena.Run(world.Arena.RunOf(made.Id))!.Side1);
        Assert.DoesNotContain(1000UL, world.Arena.Run(world.Arena.RunOf(made.Id))!.Side0);
    }

    [Fact]
    public void OfflineAndStoppedBrowsersCannotReceiveLaterMatchUpdates() {
        var world = new World(); world.List(ArenaKind.Practice, 0);
        var viewer = world.Players[Viewer]; world.Players.Remove(Viewer);
        world.Create(ArenaKind.Practice, 1);
        world.Players[Viewer] = viewer;
        var offset = world.Sent.Count; world.Create(ArenaKind.Practice, 2, creator: 1001);
        Assert.DoesNotContain(world.Sent.Skip(offset), sent => sent.Who == Viewer);
        world.List(ArenaKind.Practice, 0);
        world.Arena.QuiesceAsync().GetAwaiter().GetResult();
        offset = world.Sent.Count; world.List(ArenaKind.Practice, 0); world.Create(ArenaKind.Practice, 3, creator: 1002);
        Assert.Equal(offset, world.Sent.Count);
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void StaleFilteredCursorReturnsTheLastNonemptyNativePage(ArenaKind kind) {
        var world = new World(friendly: true); world.List(kind, 3, start: 4000);
        var page = new NativePage(); page.Read(world);
        var total = kind == ArenaKind.Ranked ? 42 : 21;
        Assert.Equal(total, page.Total); Assert.Single(page.Matches);
        Assert.Equal(kind == ArenaKind.Ranked ? 2 : 0, page.Teams.Count);
        var last = ArenaFriendlyRoster.Challenges(kind, 20, 20).Last(challenge => challenge.TeamSize == 3);
        Assert.Equal(last.Id, page.Matches.Values.Single().m_matchID.Full);
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void RemovingTheLastWatchPageReturnsTheRemainingPreviousPage(ArenaKind kind) {
        var world = new World(friendly: true);
        var matches = new List<ulong>();
        for (ulong creator = 1000; creator < 1005; creator++) {
            matches.Add(world.Create(kind, 1, creator, friends: false)); world.Arena.Confirm(creator, true);
        }
        world.List(kind, 6, start: 4);
        var page = new NativePage(); page.Read(world);
        Assert.Equal(5, page.Total); Assert.Equal(matches[4], Assert.Single(page.Matches.Values).m_matchID.Full);
        var offset = world.Sent.Count;
        world.Arena.Finish(world.Arena.RunOf(matches[4]), 0, []);
        page.Read(world, offset);
        Assert.Equal(4, page.Total); Assert.Equal(4, page.Matches.Count); Assert.Empty(page.Teams);
        Assert.Equal(matches.Take(4).Order(), page.Matches.Keys.Order());
    }

    [Fact]
    public void OddRankedFourPlayerPageFitsNativeFramesAndSharesExactPackedNames() {
        var world = new World(friendly: true); world.List(ArenaKind.Ranked, 4, start: 1, count: 84);
        var page = new NativePage(); page.Read(world);
        Assert.Equal(42, page.Total); Assert.Equal(21, page.Matches.Count); Assert.Equal(41, page.Teams.Count);
        Assert.True(page.Continuations > 0);
        var first = ArenaMessages.Read<NewListUpdate>(world.Sent.Select(sent => sent.Message)
            .OfType<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>().Single().TournamentInfo)!;
        Assert.Equal(5, first.m_matches.Count); Assert.Equal(8, first.m_teams.Count);
        var actors = page.Matches.Values.SelectMany(match => match.m_teams).Concat(page.Teams.Values)
            .SelectMany(team => team.m_actors).OfType<PvPActor>();
        Assert.All(actors, actor => {
            Assert.Equal(4, actor.m_nameBlob.Length);
        });
        foreach (var (_, message) in world.Sent.Where(sent => sent.Who == Viewer)) {
            byte[] bytes;
            IEnumerable<MatchTeam> serializedTeams;
            if (message is GAME_5_PROTOCOL.MSG_PVPUPDATEINFO initial) {
                bytes = initial.TournamentInfo;
                var list = ArenaMessages.Read<NewListUpdate>(bytes)!;
                serializedTeams = list.m_matches.SelectMany(match => match.m_teams).Concat(list.m_teams);
            } else if (message is GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE continuation) {
                bytes = continuation.Updates;
                serializedTeams = ArenaMessages.Read<TournamentUpdateList>(bytes)!.m_updates.SelectMany(child => child switch {
                    AddMatchUpdate add => add.m_matchInfo.m_teams,
                    AddTeamUpdate add => new List<MatchTeam> { add.m_team },
                    _ => new List<MatchTeam>(),
                });
            } else continue;
            // Generated m_nameBlob is a string; check its high-bit packed bytes before that UTF-8 conversion.
            Assert.Equal(serializedTeams.Sum(team => team.m_actors.OfType<PvPActor>().Count()), PackedNameCount(bytes));
            Assert.False(Enumerable.Range(0, Math.Max(0, bytes.Length - 3)).Any(at => bytes[at] == 1 && bytes[at + 1] == 2
                && bytes[at + 2] is >= (byte) 'A' and <= (byte) 'Z' && bytes[at + 3] is >= (byte) 'A' and <= (byte) 'Z'));
        }
    }

    private static int PackedNameCount(byte[] bytes) {
        var pattern = new byte[] { 0x82, 2, 3, 4 };
        var count = 0;
        for (var at = 0; at <= bytes.Length - pattern.Length; at++)
            if (bytes.AsSpan(at, pattern.Length).SequenceEqual(pattern)) count++;
        return count;
    }

    [Fact]
    public void UnknownBrowserModePreservesTheLastValidSubscription() {
        var world = new World(); world.List(ArenaKind.Practice, 1);
        var offset = world.Sent.Count;
        world.List(ArenaKind.Practice, 10); world.Create(ArenaKind.Practice, 2);
        Assert.DoesNotContain(world.Sent.Skip(offset), sent => sent.Who == Viewer);
        world.Create(ArenaKind.Practice, 1, creator: 1001);
        var page = new NativePage(); page.Read(world, offset);
        Assert.Equal(1, page.Total); Assert.Equal(1u, Assert.Single(page.Matches.Values).m_teamSize);
    }

    [Theory]
    [InlineData(ArenaKind.Practice)] [InlineData(ArenaKind.Ranked)]
    public void SubscribedWatchBrowserReceivesHumanOnlyTravelThroughTheJoinRemovalPath(ArenaKind kind) {
        var world = new World(); world.List(kind, 6);
        var page = new NativePage(); page.Read(world);
        var offset = world.Sent.Count;
        var match = world.Create(kind, 1, friends: false);
        world.Arena.Join(1001, match, world.Arena.TeamIdsOf(match)[1]);
        world.Arena.Confirm(1000, true); world.Arena.Confirm(1001, true);
        page.Read(world, offset);
        Assert.Equal("Travelling", Assert.Single(world.Arena.Snapshot()).Phase);
        Assert.Equal(1, page.Total); Assert.Empty(page.Teams);
        var row = Assert.Single(page.Matches.Values);
        Assert.Equal(match, row.m_matchID.Full); Assert.Equal(4, row.m_status);
        world.Arena.Watch(Viewer, match); Assert.True(world.Arena.IsSpectator(Viewer, match));
    }
}
