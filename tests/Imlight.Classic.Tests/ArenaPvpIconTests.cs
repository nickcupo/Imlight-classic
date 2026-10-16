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
 * ARENA PVP ICON TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner ruling 2026-10-15, "option A, do it like 2009"): the HUD
 * PvP icon shows only while the wizard is queued or in a match. The
 * r806919 client decides it from the last MatchActor (predicate
 * 0x142112910: no actor -> shown; m_pLadder unset -> shown iff state 1-11).
 * Each attach of an idle wizard gets one state-0 actor; every queue and
 * match status stays in 1-11; Leave and the end of a match go back to 0.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
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
public sealed class ArenaPvpIconTests {

    private const ulong A = 1000, B = 2000, C = 3000, Viewer = 4000;

    public ArenaPvpIconTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}arena-pvp-icon-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    private sealed class World : IArenaWorld {

        public readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        public readonly Dictionary<ulong, ArenaPlayer> Online = [];
        public readonly List<(ulong To, IMessage Message)> Sent = [];
        public readonly List<(ulong Who, ulong Run)> Trips = [];
        public ulong Runs = 0xA000;
        public ArenaMatchmaker Arena { get; }

        public World() {
            foreach (var id in new[] { A, B, C, Viewer }) {
                Online[id] = new ArenaPlayer(id, id + 7, [0x82, 0x01, 0x02, (byte) (id / 1000)], $"wiz{id}", 20, "Fire", 0);
            }

            Arena = new ArenaMatchmaker(Config, this);
        }

        public IArenaLadderStore Ladder { get; } = new ArenaLadderCollection.Memory();
        public ArenaPlayer? Player(ulong charId) => Online.GetValueOrDefault(charId);
        public bool AreFriends(ulong a, ulong b) => false;
        public void Send(ulong charId, IMessage message) => Sent.Add((charId, message));
        public void Inform(ulong charId, string text) { }
        public void Travel(ulong charId, string zone, string location, ulong runId) => Trips.Add((charId, runId));
        public void Deliver(ulong charId, ArenaOutcome outcome) { }
        public ulong NewRunId() => ++Runs;
        public string? ZoneOf(ulong charId) => Config.HallZone;

        public IEnumerable<GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE> Updates(ulong to)
            => Sent.Where(s => s.To == to).Select(s => s.Message).OfType<GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE>();

        public uint Size(ArenaKind kind, int n)
            => ArenaRules.Hash(ArenaRules.MatchName(kind == ArenaKind.Ranked ? "PvPSanctioned" : "PvPPractice", n));

    }

    private static PvPActor Actor(GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE update)
        => Assert.IsType<PvPActor>(ArenaMessages.Read<MatchActor>(update.MatchActor));

    // r806919 0x142112910 over the client's stored actor (m_pLadder is the field at +0x98).
    private static bool IconShown(PvPActor? actor)
        => actor is null || (actor.m_pLadder is not null ? actor.m_status is not (0 or 12) : actor.m_status is >= 1 and <= 11);

    private static void AssertIdle(GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE? update, ulong charId) {
        Assert.NotNull(update);
        var actor = Actor(update!);
        Assert.Equal(0, actor.m_status);
        Assert.Equal(0, (int) update!.Status);
        Assert.Equal(charId + 7, actor.m_nActorID.Full);
        Assert.Equal(charId + 7, (ulong) update.CharacterID);
        Assert.Equal(0ul, actor.m_nMatchID.Full);
        Assert.True(((byte[]) update.MatchTeam)?.Length is null or 0);  // no team: the client keeps no match
        Assert.Equal("", update.UpdateMessage.ToString());              // no penalty or tournament text
        Assert.False(IconShown(actor));
    }

    [Fact]
    public void AnIdleWizardGetsOneStateZeroActorPerAttach() {
        var w = new World();
        Assert.True(IconShown(null));                                    // without it, r806919 would show the icon
        AssertIdle(w.Arena.AttachedStatus(A, w.Online[A]), A);
        Assert.Empty(w.Sent);                                            // returned for the session's own socket only
        AssertIdle(w.Arena.AttachedStatus(A, w.Online[A]), A);           // the next attach (zone change) gets it again
        Assert.Empty(w.Sent);
    }

    [Fact]
    public void AmbientWizardsGetNothing() {
        var w = new World();
        var ambient = w.Online[B] with { Ambient = true };
        Assert.Null(w.Arena.AttachedStatus(B, ambient));
    }

    [Theory]
    [InlineData(ArenaKind.Practice)]
    [InlineData(ArenaKind.Ranked)]
    public void EveryQueueAndMatchStatusShowsTheIconAndAQueuedAttachKeepsIt(ArenaKind kind) {
        var w = new World();
        w.Arena.Create(A, kind, w.Size(kind, 1), 0, 0, friendsOnly: false);
        var match = w.Arena.MatchOf(A);
        Assert.Equal(4, Actor(w.Updates(A).Last()).m_status);           // 1v1: own side full, finding an opponent
        Assert.Null(w.Arena.AttachedStatus(A, w.Online[A]));             // a zone change while queued: no state 0

        w.Arena.Join(B, match, w.Arena.TeamIdsOf(match)[1]);             // full: Go to Arena (8)
        Assert.Null(w.Arena.AttachedStatus(B, w.Online[B]));
        w.Arena.Confirm(A, true);                                        // 9/10 while the others answer
        w.Arena.Confirm(B, true);                                        // travelling (11)
        Assert.Equal(2, w.Trips.Count);
        var run = w.Trips[0].Run;
        Assert.Null(w.Arena.AttachedStatus(A, w.Online[A]));             // arriving in the arena: the match status stays
        w.Arena.Started(run);
        Assert.Null(w.Arena.AttachedStatus(B, w.Online[B]));

        foreach (var who in new[] { A, B }) {
            var statuses = w.Updates(who).Select(u => Actor(u).m_status).ToList();
            Assert.NotEmpty(statuses);
            Assert.All(statuses, s => Assert.InRange(s, 1, 11));
            Assert.All(w.Updates(who), u => Assert.True(IconShown(Actor(u))));
            Assert.Contains(8, statuses);
            Assert.Equal(11, statuses.Last());
        }

        // The match ends: the client clears its own status on MSG_MATCHRESULT; the next attach (the trip back to the
        // hall) gets state 0 from the server too.
        w.Arena.Finish(run, winningSide: 0, fled: []);
        AssertIdle(w.Arena.AttachedStatus(A, w.Online[A]), A);
        AssertIdle(w.Arena.AttachedStatus(B, w.Online[B]), B);
    }

    [Fact]
    public void LeavingTheQueueHidesTheIconAgain() {
        var w = new World();
        w.Arena.QuickJoin(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 2));
        Assert.True(IconShown(Actor(w.Updates(A).Last())));
        w.Arena.Leave(A);
        Assert.Equal(0ul, w.Arena.MatchOf(A));
        Assert.False(IconShown(Actor(w.Updates(A).Last())));
        Assert.Equal(0, Actor(w.Updates(A).Last()).m_status);
        AssertIdle(w.Arena.AttachedStatus(A, w.Online[A]), A);
    }

    [Fact]
    public void WatchingAMatchShowsTheIconUntilLeave() {
        var w = new World();
        w.Arena.QuickJoin(A, ArenaKind.Practice, w.Size(ArenaKind.Practice, 1));
        var match = w.Arena.MatchOf(A);
        w.Arena.Join(B, match, w.Arena.TeamIdsOf(match)[1]);
        w.Arena.Confirm(A, true);
        w.Arena.Confirm(B, true);
        w.Arena.Started(w.Trips[0].Run);

        w.Arena.Watch(Viewer, match);
        Assert.True(w.Arena.IsSpectator(Viewer, match));
        Assert.Equal(11, Actor(w.Updates(Viewer).Last()).m_status);
        Assert.Null(w.Arena.AttachedStatus(Viewer, w.Online[Viewer]));   // arriving to watch keeps the status

        w.Arena.Leave(Viewer);
        Assert.Equal(0, Actor(w.Updates(Viewer).Last()).m_status);
        AssertIdle(w.Arena.AttachedStatus(Viewer, w.Online[Viewer]), Viewer);
    }

    [Fact]
    public void NothingIsSentWhileTheArenaStops() {
        var w = new World();
        w.Arena.QuiesceAsync().GetAwaiter().GetResult();
        Assert.Null(w.Arena.AttachedStatus(C, w.Online[C]));
    }

}
