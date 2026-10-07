// CLASSIC: r806919 browser cells distinguish real actors from explicit zero-ID vacancies.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Arena;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaNativeVacancyTests {
    private static readonly byte[] PackedName = [0x82, 0x31, 0x32, 0x33];

    private static ArenaMatchView View(int size) {
        var name = ArenaRules.MatchName("PvPSanctioned", size);
        return new(400, ArenaRules.Hash("PvPSanctioned"), ArenaRules.Hash(name), name, size,
            [401, 402], 500, false, 20, 20, 0, true);
    }

    private static PvPActor Wizard(ArenaMatchView view, int side = 0) {
        var player = new ArenaPlayer(700, 500, PackedName, "authored wizard", 20, "Fire", 0);
        var actor = ArenaMessages.Actor(player, view, side, 4, 500, 0);
        actor.m_nameBlob = "original packed-name source";
        return actor;
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(2, 0)] [InlineData(2, 1)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(4, 0)] [InlineData(4, 1)]
    public void EveryListingSeatRoundTripsAsANativeVacancyWithItsCorrectTeamAndMatch(int size, int side) {
        var view = View(size);
        var team = ArenaMessages.ListingTeam(view, side, []);
        var wire = ArenaMessages.Blob(team);
        var decoded = Assert.IsType<MatchTeam>(ArenaMessages.Read<MatchTeam>(wire));
        Assert.Equal(size, decoded.m_actors.Count);
        Assert.Equal(view.TeamIds[side], decoded.m_nTeamID.Full);
        Assert.Equal(view.MatchId, decoded.m_matchId.Full);
        Assert.All(decoded.m_actors, actor => {
            var vacancy = Assert.IsType<MatchActor>(actor);
            Assert.Equal(1689401516U, vacancy.GetHash());
            // Native be4610 sees actor ID zero and enables Join without casting to PvPActor.
            Assert.Equal(0UL, vacancy.m_nActorID.Full);
            Assert.Equal(0, vacancy.m_status);
            Assert.Equal(view.TournamentId, vacancy.m_nTournamentNameID);
            Assert.Equal(view.MatchNameId, vacancy.m_nMatchNameID);
            Assert.Equal(view.MatchId, vacancy.m_nMatchID.Full);
            Assert.Equal(view.TeamIds[side], vacancy.m_nTeamID.Full);
            Assert.Equal(0UL, vacancy.m_userID.Full);
            Assert.Equal(ADJUSTMENT_TYPE.UNSET_MAX, vacancy.m_costAdj.m_matchAdjustmentType);
            Assert.Null(vacancy.m_pLadder);
            Assert.False(vacancy is PvPActor);
        });
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ListingPreservesRealActorAliasesAndPadsOnlyTheRemainingSeats(int size) {
        var view = View(size);
        var actor = Wizard(view);
        var supplied = new List<PvPActor> { actor };
        var team = ArenaMessages.ListingTeam(view, 0, supplied);
        Assert.Equal(size, team.m_actors.Count);
        Assert.Same(actor, team.m_actors[0]);
        Assert.Single(supplied);
        Assert.All(team.m_actors.Skip(1), vacancy => {
            Assert.IsType<MatchActor>(vacancy);
            Assert.Equal(0UL, vacancy.m_nActorID.Full);
            Assert.Equal(0, vacancy.m_status);
        });
        Assert.Single(ArenaMessages.Team(view, 0, supplied).m_actors);
        Assert.Empty(ArenaMessages.Team(view, 1, []).m_actors);
    }

    [Fact]
    public void SharedActorInMatchAndTeamViewsGetsExactPackedBytesInBothAndRestoresItsSource() {
        var view = View(2);
        var actor = Wizard(view);
        var original = actor.m_nameBlob;
        var team = ArenaMessages.ListingTeam(view, 0, [actor]);
        var list = new NewListUpdate {
            m_tournamentNameID = view.TournamentId,
            m_clearData = true,
            m_matches = [ArenaMessages.MatchInfo(view, [team, ArenaMessages.ListingTeam(view, 1, [])])],
            m_teams = [team],
            m_totalTeams = 1,
        };
        var wire = ArenaMessages.BrowserBlob(list, new Dictionary<ulong, byte[]> { [500] = PackedName });
        AssertWireNameCopies(wire, 2);
        var decoded = Assert.IsType<NewListUpdate>(ArenaMessages.ReadBrowser<NewListUpdate>(wire));
        var matchActor = Assert.IsType<PvPActor>(Assert.Single(decoded.m_matches).m_teams[0].m_actors[0]);
        var teamActor = Assert.IsType<PvPActor>(Assert.Single(decoded.m_teams).m_actors[0]);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(PackedName), matchActor.m_nameBlob);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(PackedName), teamActor.m_nameBlob);
        Assert.Equal(original, actor.m_nameBlob);
        Assert.IsType<MatchActor>(decoded.m_teams[0].m_actors[1]);
    }

    [Fact]
    public void SeparateActorObjectsWithTheSameIdentityBothReceiveTheirPackedNames() {
        var view = View(1);
        var actor = Wizard(view);
        var second = actor with { };
        var original = actor.m_nameBlob;
        Assert.NotSame(actor, second);
        var list = new NewListUpdate {
            m_clearData = true,
            m_matches = [ArenaMessages.MatchInfo(view, [ArenaMessages.ListingTeam(view, 0, [actor])])],
            m_teams = [ArenaMessages.ListingTeam(view, 0, [second])],
            m_totalTeams = 1,
        };
        var wire = ArenaMessages.BrowserBlob(list, new Dictionary<ulong, byte[]> { [500] = PackedName });
        AssertWireNameCopies(wire, 2);
        var decoded = Assert.IsType<NewListUpdate>(ArenaMessages.ReadBrowser<NewListUpdate>(wire));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(PackedName), Assert.IsType<PvPActor>(decoded.m_matches[0].m_teams[0].m_actors[0]).m_nameBlob);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(PackedName), Assert.IsType<PvPActor>(decoded.m_teams[0].m_actors[0]).m_nameBlob);
        Assert.Equal(original, actor.m_nameBlob);
        Assert.Equal(original, second.m_nameBlob);
    }

    [Fact]
    public void IncrementalTeamUpdatesPackRealNamesAndKeepVacanciesAsBaseActors() {
        var view = View(2);
        var actor = Wizard(view);
        var original = actor.m_nameBlob;
        var updates = new TournamentUpdateList {
            m_updates = [new AddTeamUpdate { m_team = ArenaMessages.ListingTeam(view, 0, [actor]) }],
            m_teamCount = 1,
            m_actorCount = 1,
        };
        var wire = ArenaMessages.Blob(updates, new Dictionary<ulong, byte[]> { [500] = PackedName });
        AssertWireNameCopies(wire, 1);
        var decoded = Assert.IsType<TournamentUpdateList>(ArenaMessages.Read<TournamentUpdateList>(wire));
        var team = Assert.IsType<AddTeamUpdate>(Assert.Single(decoded.m_updates)).m_team;
        Assert.Equal(System.Text.Encoding.UTF8.GetString(PackedName), Assert.IsType<PvPActor>(team.m_actors[0]).m_nameBlob);
        Assert.Equal(0UL, Assert.IsType<MatchActor>(team.m_actors[1]).m_nActorID.Full);
        Assert.Equal(original, actor.m_nameBlob);
    }

    [Fact]
    public void FailedSerializationRestoresTheOriginalActorName() {
        var actor = new ThrowingActor { m_nActorID = 500, m_nameBlob = "retained naïve 名" };
        var original = actor.m_nameBlob;
        Assert.Throws<InvalidOperationException>(() => ArenaMessages.Blob(actor,
            new Dictionary<ulong, byte[]> { [500] = PackedName }));
        Assert.Equal(original, actor.m_nameBlob);
    }

    private static void AssertWireNameCopies(ByteString wire, int copies) {
        byte[] bytes = wire;
        Assert.Equal(copies, Occurrences(bytes, PackedName));
        for (var i = 0; i < copies; i++)
            Assert.Equal(0, Occurrences(bytes, [0x01, 0x02, (byte) 'A', (byte) ('A' + i)]));
    }

    private static int Occurrences(byte[] bytes, byte[] value) {
        var count = 0;
        for (var offset = 0; offset + value.Length <= bytes.Length; offset++)
            if (bytes.AsSpan(offset, value.Length).SequenceEqual(value)) count++;
        return count;
    }

    private sealed record ThrowingActor : PvPActor {
        public override void OnPreEncode() => throw new InvalidOperationException("authored serialization failure");
    }
}
