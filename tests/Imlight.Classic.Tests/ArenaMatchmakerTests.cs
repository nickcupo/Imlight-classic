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
 * ARENA MATCHMAKER TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 arena's matches against the r806919 client's messages: Create
 * Match, Join, Quick Join, the errors (side full, match started, not
 * allowed), Go to Arena (confirm, decline, time out), the trip to a random
 * arena, and the results (Ranked rating and tickets, Practice nothing,
 * flee = loss, no contest), with a fake world.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

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
public sealed class ArenaMatchmakerTests {

    private const ulong A = 1000, B = 2000, C = 3000, D = 4000, E = 5000;

    public ArenaMatchmakerTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}arena-matchmaker-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    private sealed class World : IArenaWorld {

        public readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        public readonly Dictionary<ulong, ArenaPlayer> Online = [];
        public readonly HashSet<(ulong, ulong)> Friends = [];
        public readonly List<(ulong To, IMessage Message)> Sent = [];
        public readonly List<(ulong Who, string Zone, string Location, ulong Run)> Trips = [];
        public readonly Dictionary<ulong, ArenaOutcome> Outcomes = [];
        public ulong Runs = 0xA000;
        public ArenaMatchmaker Arena { get; }

        public World() {
            foreach (var (id, level) in new[] { (A, 10), (B, 12), (C, 30), (D, 5), (E, 20) }) {
                Online[id] = new ArenaPlayer(id, id + 7, [0x82, 0x01, 0x02, (byte) (id / 1000)], $"wiz{id}", level, "Fire", 0);
            }

            Arena = new ArenaMatchmaker(Config, this);
        }

        public IArenaLadderStore Ladder { get; } = new ArenaLadderCollection.Memory();

        public ArenaPlayer? Player(ulong charId) => Online.GetValueOrDefault(charId);

        public bool AreFriends(ulong a, ulong b) => Friends.Contains((a, b)) || Friends.Contains((b, a));

        public void Send(ulong charId, IMessage message) => Sent.Add((charId, message));

        public void Inform(ulong charId, string text) { }

        public void Travel(ulong charId, string zone, string location, ulong runId) => Trips.Add((charId, zone, location, runId));

        public void Deliver(ulong charId, ArenaOutcome outcome) => Outcomes[charId] = outcome;

        public ulong NewRunId() => ++Runs;

        public readonly Dictionary<ulong, string> Zones = [];

        public string? ZoneOf(ulong charId) => Zones.GetValueOrDefault(charId, "WizardCity/WC_Duel_Arena");

        public IEnumerable<T> Of<T>(ulong to) => Sent.Where(s => s.To == to).Select(s => s.Message).OfType<T>();

        public int Status(ulong to) {
            var last = Of<GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE>(to).LastOrDefault();

            return last is null ? -1 : ArenaMessages.Read<MatchActor>(last.MatchActor)!.m_status;
        }

        public string? LastError(ulong to) {
            var e = Of<WIZARD_12_PROTOCOL.MSG_ARENA_ERROR>(to).LastOrDefault();

            return e is null ? null : new[] {
                ArenaErrors.SideIsFull, ArenaErrors.MatchStarted, ArenaErrors.NoSlots, ArenaErrors.TeamFull, ArenaErrors.SideNotAllowed,
                ArenaErrors.PlayerDeclined, ArenaErrors.MaxedMatches,
            }.First(k => ArenaRules.Hash(k) == e.Error);
        }

        public uint Size(ArenaKind kind, int n) => ArenaRules.Hash(ArenaRules.MatchName(kind == ArenaKind.Ranked ? "PvPSanctioned" : "PvPPractice", n));

    }

    [Fact]
    public void KioskOpensTheClientsWindowForItsTournament() {
        var w = new World();
        w.Arena.OpenKiosk(A, ArenaKind.Practice, 777);
        var kiosk = w.Of<WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK>(A).Single();
        Assert.Equal((ArenaRules.Hash("PvPPractice"), 777ul), (kiosk.TournamentNameID, kiosk.MobileID));
        w.Arena.CanJoin(A);
        Assert.Equal(1, w.Of<WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCHRESPONSE>(A).Single().CanJoinQueue);
        Assert.Equal(ArenaKind.Ranked, w.Arena.KindOf(ArenaRules.Hash("PvPSanctioned")));
        Assert.Null(w.Arena.KindOf(ArenaRules.Hash("PvPSanctioned4")));
    }

    [Fact]
    public void CreateJoinConfirmAndGoToARandomArena() {
        var w = new World();
        w.Arena.List(E, ArenaRules.Hash("PvPPractice"));
        Assert.Empty(ArenaMessages.ReadBrowser<NewListUpdate>(w.Of<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>(E).Single().TournamentInfo)!.m_matches);

        w.Arena.Create(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 2), 1, 50, friendsOnly: false);
        var match = w.Arena.MatchOf(A);
        Assert.NotEqual(0ul, match);
        Assert.Equal(3, w.Status(A));   // looking for teammates

        // The watcher's list gets the new match, with the creator's packed name in the actor.
        var update = w.Of<GAME_5_PROTOCOL.MSG_PVPUPDATEINFO>(E).Last();
        var info = (PvPMatchInfo) ArenaMessages.ReadBrowser<NewListUpdate>(update.TournamentInfo)!.m_matches.Single();
        Assert.Equal((2u, ArenaRules.Hash("PvPPractice2v2Match")), (info.m_teamSize, info.m_matchNameID));
        Assert.Equal(50, info.m_joinQueueRequirements.m_maxLevel);
        var actor = info.m_teams[0].m_actors.OfType<PvPActor>().Single();
        Assert.Equal((A + 7, 10, "Fire"), (actor.m_nActorID.Full, actor.m_level, actor.m_sSchool));
        byte[] blob = update.TournamentInfo;
        Assert.True(Contains(blob, [0x82, 0x01, 0x02, 0x01]));   // the 4 name bytes reach the client as they are

        var teams = w.Arena.TeamIdsOf(match);
        w.Arena.Join(B, match, teams[0]);
        Assert.Equal(4, w.Status(A));   // own side full: finding a match
        w.Arena.Join(C, match, teams[1]);
        Assert.Equal(3, w.Status(C));
        w.Arena.Join(E, match, teams[1]);

        // Full: everyone is asked to Go to Arena (status 8).
        foreach (var who in new[] { A, B, C, E }) {
            Assert.Single(w.Of<WIZARD_12_PROTOCOL.MSG_PVPCONFIRM>(who));
            Assert.Equal(8, w.Status(who));
        }

        w.Arena.Confirm(A, true);
        Assert.Equal(9, w.Status(A));   // waiting for teammates
        w.Arena.Confirm(B, true);
        Assert.Equal(10, w.Status(A));  // waiting for the opponents
        w.Arena.Confirm(C, true);
        Assert.Empty(w.Trips);
        w.Arena.Confirm(E, true);

        Assert.Equal(4, w.Trips.Count);
        Assert.Single(w.Trips.Select(t => t.Run).Distinct());
        Assert.Single(w.Trips.Select(t => t.Zone).Distinct());
        Assert.Contains(w.Trips[0].Zone, w.Config.Arenas.Select(a => a.Zone));
        var run = w.Arena.Run(w.Trips[0].Run)!;
        Assert.Equal(new[] { A, B }, run.Side0.Order());
        Assert.Equal(new[] { C, E }, run.Side1.Order());
        Assert.Equal(11, w.Status(A));  // waiting to teleport
    }

    [Fact]
    public void SideFullMatchStartedAndNotAllowed() {
        var w = new World();
        w.Arena.Create(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 1), 0, 0, friendsOnly: false);
        var match = w.Arena.MatchOf(A);
        var teams = w.Arena.TeamIdsOf(match);

        w.Arena.Join(B, match, teams[0]);
        Assert.Equal(ArenaErrors.SideIsFull, w.LastError(B));   // "The side you requested to join is full"
        Assert.Equal(0ul, w.Arena.MatchOf(B));

        w.Arena.Join(B, match, teams[1]);                        // the match is now full and asking Go to Arena
        w.Arena.Join(C, match, teams[1]);
        Assert.Equal(ArenaErrors.MatchStarted, w.LastError(C));  // "The match has already begun"

        w.Arena.Create(C, ArenaKind.Practice, w.Size(ArenaKind.Practice, 1), 15, 40, friendsOnly: true);
        var limited = w.Arena.MatchOf(C);
        w.Arena.Join(D, limited, w.Arena.TeamIdsOf(limited)[1]);   // level 5, below 15
        Assert.Equal(ArenaErrors.SideNotAllowed, w.LastError(D));
        w.Arena.Join(E, limited, w.Arena.TeamIdsOf(limited)[1]);   // level 20 but not a friend
        Assert.Equal(ArenaErrors.SideNotAllowed, w.LastError(E));
        w.Friends.Add((C, E));
        w.Arena.Join(E, limited, w.Arena.TeamIdsOf(limited)[1]);
        Assert.Equal(limited, w.Arena.MatchOf(E));
    }

    [Fact]
    public void DeclineOrNoAnswerReopensTheMatch() {
        var w = new World();
        w.Arena.Create(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 1), 0, 0, false);
        var match = w.Arena.MatchOf(A);
        w.Arena.Join(B, match, w.Arena.TeamIdsOf(match)[1]);
        w.Arena.Confirm(A, true);
        w.Arena.Confirm(B, false);
        Assert.Equal(ArenaErrors.PlayerDeclined, w.LastError(A));
        Assert.Equal(0ul, w.Arena.MatchOf(B));
        Assert.Equal(0, w.Status(B));
        Assert.Equal(4, w.Status(A));   // back to waiting for an opponent
        Assert.Equal("Open", w.Arena.Snapshot().Single().Phase);

        w.Arena.Join(C, match, w.Arena.TeamIdsOf(match)[1]);
        w.Arena.Confirm(C, true);
        w.Arena.Tick(DateTime.UtcNow.AddSeconds(w.Config.ConfirmSeconds + 5));   // A never answers this time
        Assert.Equal(0ul, w.Arena.MatchOf(A));
        Assert.Equal(match, w.Arena.MatchOf(C));
        Assert.Empty(w.Trips);
    }

    [Fact]
    public void QuickJoinFillsAnOpenSpotOrMakesAMatch() {
        var w = new World();
        w.Arena.QuickJoin(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 2));
        var match = w.Arena.MatchOf(A);
        Assert.NotEqual(0ul, match);
        w.Arena.QuickJoin(B, ArenaKind.Practice, w.Size(ArenaKind.Practice, 2));
        Assert.Equal(match, w.Arena.MatchOf(B));
        Assert.Equal((1, 1), (w.Arena.Snapshot().Single().Side0, w.Arena.Snapshot().Single().Side1));   // the emptier side

        // Ranked: the open match with the nearest rating.
        w.Ladder.Save(new ArenaLadderEntry { CharId = C, Rating = 900 });
        w.Ladder.Save(new ArenaLadderEntry { CharId = D, Rating = 520 });
        w.Arena.QuickJoin(C, ArenaKind.Ranked, w.Size(ArenaKind.Ranked, 1));
        w.Arena.QuickJoin(D, ArenaKind.Ranked, w.Size(ArenaKind.Ranked, 2));
        w.Arena.QuickJoin(E, ArenaKind.Ranked, w.Size(ArenaKind.Ranked, 1));   // 500: joins C's 1v1 (only 1v1 open)
        Assert.Equal(w.Arena.MatchOf(C), w.Arena.MatchOf(E));
    }

    [Fact]
    public void RankedResultsMoveRatingsAndGiveTicketsAndFleeingLoses() {
        var w = new World();
        var run = StartedMatch(w, ArenaKind.Ranked, 2, [A, B], [C, E]);

        // Side 1 (A, B) wins, but B fled: B loses.
        w.Arena.Finish(run, winningSide: 0, fled: [B]);
        var a = w.Outcomes[A];
        Assert.True(a.Won);
        Assert.Equal((500, 10), (a.RatingBefore, a.Tickets));
        Assert.True(a.RatingAfter > 500);
        Assert.False(w.Outcomes[B].Won);
        Assert.True(w.Outcomes[B].Fled);
        Assert.Equal(3, w.Outcomes[B].Tickets);
        Assert.True(w.Outcomes[B].RatingAfter < 500);
        Assert.Equal((false, 3), (w.Outcomes[C].Won, w.Outcomes[C].Tickets));
        Assert.Equal(new ArenaStanding(a.RatingAfter, 1, 0), w.Arena.Standing(A));
        Assert.Equal(1, w.Arena.Standing(C).Losses);
        Assert.Equal("WizardCity/WC_Duel_Arena", a.HallZone);

        var result = ArenaMessages.Read<ArenaMatchResults>(Assert.IsType<GAME_5_PROTOCOL.MSG_MATCHRESULT>(a.Result).ResultData)!;
        Assert.Equal(4, result.m_actorList.Count);
        Assert.Equal(10, result.m_actorList.Single(r => r.m_pActor.m_nActorID.Full == A + 7).m_arenaPoints);
        Assert.Equal(0ul, w.Arena.MatchOf(A));   // free to queue again
        Assert.Null(w.Arena.Run(run));
    }

    [Fact]
    public void PracticeChangesNothingAndNoContestGivesNothing() {
        var w = new World();
        var run = StartedMatch(w, ArenaKind.Practice, 1, [A], [C]);
        w.Arena.Finish(run, 1, []);
        Assert.Equal((true, 0, 500, 500), (w.Outcomes[C].Won, w.Outcomes[C].Tickets, w.Outcomes[C].RatingBefore, w.Outcomes[C].RatingAfter));
        Assert.Equal(new ArenaStanding(500, 0, 0), w.Arena.Standing(C));

        var w2 = new World();
        var run2 = StartedMatch(w2, ArenaKind.Ranked, 1, [A], [C]);
        w2.Arena.Finish(run2, -1, [A, C]);
        Assert.Equal((false, 0), (w2.Outcomes[A].Won, w2.Outcomes[A].Tickets));
        Assert.Equal(new ArenaStanding(500, 0, 0), w2.Arena.Standing(A));
    }

    [Fact]
    public void GoingOfflineLeavesAWaitingMatchButNotAStartedOne() {
        var w = new World();
        w.Arena.Create(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 2), 0, 0, false);
        var player = w.Online[A];
        w.Online.Remove(A);   // a zone change: the session is gone for a moment
        w.Arena.Tick(DateTime.UtcNow);
        w.Online[A] = player;
        w.Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.OfflineGraceSeconds + 5));
        Assert.Single(w.Arena.Snapshot());   // back in time: still in the match
        w.Online.Remove(A);   // logged out for good
        w.Arena.Tick(DateTime.UtcNow);
        w.Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.OfflineGraceSeconds + 5));
        Assert.Empty(w.Arena.Snapshot());

        var run = StartedMatch(w, ArenaKind.Ranked, 1, [B], [C], fight: false);
        w.Online.Remove(B);
        w.Arena.Tick(DateTime.UtcNow);
        w.Arena.Tick(DateTime.UtcNow.AddSeconds(ArenaMatchmaker.OfflineGraceSeconds + 5));
        Assert.NotEqual(0ul, w.Arena.MatchOf(B));   // gone to the arena: the fight decides
        w.Sent.Clear();
        w.Arena.Leave(B);   // the window's leave cannot pull a wizard out of a fight
        Assert.NotEqual(0ul, w.Arena.MatchOf(B));
        Assert.Equal(11, w.Status(B));   // the client cleared its status on Leave: the match status comes back (HUD PvP button)

        // Nobody's arena fight started: no contest a minute after the arrival time.
        w.Arena.Tick(DateTime.UtcNow.AddSeconds(w.Config.ArrivalSeconds + 61));
        Assert.Null(w.Arena.Run(run));
        Assert.False(w.Outcomes[B].Won);
        Assert.Equal(0, w.Outcomes[B].Tickets);
    }

    [Fact]
    public void AMissedTripIsSentAgain() {
        var w = new World();
        StartedMatch(w, ArenaKind.Practice, 1, [A], [B], fight: false);
        var zone = w.Trips[0].Zone;
        Assert.Equal(2, w.Trips.Count);
        w.Zones[A] = zone;   // A arrived; B's trip was lost in a zone change
        w.Arena.Tick(DateTime.UtcNow.AddSeconds(11));
        Assert.Equal(3, w.Trips.Count);
        Assert.Equal(B, w.Trips[2].Who);
    }

    private static ulong StartedMatch(World w, ArenaKind kind, int size, ulong[] side0, ulong[] side1, bool fight = true) {
        w.Arena.QuickJoin(side0[0], kind, w.Size(kind, size));
        var match = w.Arena.MatchOf(side0[0]);
        var teams = w.Arena.TeamIdsOf(match);
        foreach (var who in side0.Skip(1)) {
            w.Arena.Join(who, match, teams[0]);
        }

        foreach (var who in side1) {
            w.Arena.Join(who, match, teams[1]);
        }

        foreach (var who in side0.Concat(side1)) {
            w.Arena.Confirm(who, true);
        }

        var run = w.Trips.Last().Run;
        if (fight) {
            w.Arena.Started(run);
        }

        return run;
    }

    private static bool Contains(byte[] haystack, byte[] needle) {
        for (var i = 0; i + needle.Length <= haystack.Length; i++) {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) {
                return true;
            }
        }

        return false;
    }

}
