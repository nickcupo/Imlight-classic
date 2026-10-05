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
 * ARENA MATCHMAKER (2009 PRACTICE AND RANKED)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: every arena match on the server (owner ruling 2026-10-05). The
 * Practice and Ranked guards list the matches waiting for players; a wizard
 * creates one (Practice: 1v1 to 4v4, friends only, levels), joins a side,
 * or Quick Joins any open spot. A full match asks everyone "Go to Arena";
 * when all have said yes the wizards go to a random arena and fight there
 * (CombatDuelComponent.Arena). The fight's end comes back here: Ranked
 * moves ratings and gives Arena Tickets, Practice changes nothing, a
 * wizard who fled or never came back loses, and everyone returns to the
 * arena hall.
 *
 * USAGE EXAMPLE:
 * ArenaMatchmaker.Instance.Create(charId, ArenaKind.Practice, request);
 * ArenaMatchmaker.Instance.Finish(runId, winningSide: 1, fled: [charId]);
 *
 * NOTE:
 * One lock holds the state (quick in-memory steps; messages are actor
 * Tells). The 2009 rules and their sources are in Imlight.Classic.Pvp
 * .ArenaRules; the client protocol in ArenaMessages.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic.Arena;

/// <summary>What one wizard got from a finished match.</summary>
internal sealed record ArenaOutcome(ulong MatchId, ArenaKind Kind, bool Won, bool Fled, int RatingBefore, int RatingAfter,
    string Rank, int Tickets, IMessage Result, string HallZone, string HallLocation, int ReturnSeconds);

/// <summary>What the matchmaker needs from the server (fakes in tests).</summary>
internal interface IArenaWorld {

    /// <summary>The online wizard, or null when offline.</summary>
    ArenaPlayer? Player(ulong charId);

    bool AreFriends(ulong charId, ulong otherCharId);

    /// <summary>Sends a client message to an online wizard.</summary>
    void Send(ulong charId, IMessage message);

    /// <summary>A chat line to an online wizard.</summary>
    void Inform(ulong charId, string text);

    /// <summary>Sends an online wizard into a match's arena instance.</summary>
    void Travel(ulong charId, string zone, string location, ulong runId);

    /// <summary>Hands a finished match to a wizard (tickets in their stats, the result window, the trip back).</summary>
    void Deliver(ulong charId, ArenaOutcome outcome);

    /// <summary>A new instance key for a match's arena.</summary>
    ulong NewRunId();

    /// <summary>The zone an online wizard is in, or null.</summary>
    string? ZoneOf(ulong charId);

    IArenaLadderStore Ladder { get; }

}

/// <summary>A match's arena trip, as the arena's duel circle sees it.</summary>
internal sealed record ArenaRun(ulong RunId, ulong MatchId, ArenaKind Kind, string Zone, IReadOnlyList<ulong> Side0,
    IReadOnlyList<ulong> Side1, DateTime ArrivalEndsUtc);

/// <summary>CLASSIC: the 2009 arena's matches.</summary>
internal sealed class ArenaMatchmaker {

    private enum Phase { Open, Confirming, Travelling, Fighting }

    private sealed class Match {

        public ulong Id;
        public ArenaKind Kind;
        public string Tournament = "";
        public uint TournamentId;
        public int TeamSize;
        public uint MatchNameId;
        public string MatchName = "";
        public readonly ulong[] TeamIds = new ulong[2];
        public readonly List<ulong>[] Sides = [[], []];
        public ulong Creator;
        public bool FriendsOnly;
        public int MinLevel;
        public int MaxLevel;
        public Phase Phase;
        public DateTime CreatedUtc;
        public DateTime ConfirmEndsUtc;
        public readonly HashSet<ulong> Confirmed = [];
        public ulong RunId;
        public string Zone = "";
        public DateTime TravelledUtc;

        public IEnumerable<ulong> Members => Sides[0].Concat(Sides[1]);

        public int SideOf(ulong charId) => Sides[0].Contains(charId) ? 0 : Sides[1].Contains(charId) ? 1 : -1;

        public bool IsFull(int teamSize) => Sides[0].Count >= teamSize && Sides[1].Count >= teamSize;

    }

    private static ArenaMatchmaker? s_instance;

    /// <summary>The server's matchmaker (null until the arena is on).</summary>
    public static ArenaMatchmaker? Instance => s_instance;

    /// <summary>Starts the server's matchmaker once.</summary>
    public static ArenaMatchmaker Start(ArenaConfig config, IArenaWorld world) {
        var made = new ArenaMatchmaker(config, world);

        return System.Threading.Interlocked.CompareExchange(ref s_instance, made, null) ?? made;
    }

    private readonly object _gate = new();
    private readonly ArenaConfig _config;
    private readonly IArenaWorld _world;
    private readonly Dictionary<ulong, Match> _matches = [];
    private readonly Dictionary<ulong, ulong> _matchOf = [];           // charId -> match id
    private readonly Dictionary<ulong, ArenaPlayer> _players = [];      // the wizards in matches (as they joined)
    private readonly Dictionary<ArenaKind, HashSet<ulong>> _watchers = new() { [ArenaKind.Practice] = [], [ArenaKind.Ranked] = [] };
    private readonly ConcurrentDictionary<ulong, ArenaRun> _runs = new();
    private readonly Random _random = new();
    private ulong _nextId = (ulong) DateTime.UtcNow.Ticks & 0x0000_FFFF_FFFF_FFFF;

    public ArenaMatchmaker(ArenaConfig config, IArenaWorld world) {
        _config = config;
        _world = world;
    }

    public ArenaConfig Config => _config;

    /// <summary>The tournament id the client uses for a kind (KingsIsle hash of the tournament name).</summary>
    public uint TournamentId(ArenaKind kind) => ArenaRules.Hash(Tournament(kind));

    public string Tournament(ArenaKind kind) => kind == ArenaKind.Ranked ? _config.RankedTournament : _config.PracticeTournament;

    /// <summary>The kind of a tournament id, or null when it is not one of the guards'.</summary>
    public ArenaKind? KindOf(uint tournamentId)
        => tournamentId == TournamentId(ArenaKind.Practice) ? ArenaKind.Practice
            : tournamentId == TournamentId(ArenaKind.Ranked) ? ArenaKind.Ranked : null;

    /// <summary>The arena trip of an instance key, for the arena's duel circle.</summary>
    public ArenaRun? Run(ulong runId) => runId != 0 && _runs.TryGetValue(runId, out var run) ? run : null;

    /// <summary>The arena trip a wizard is on now (gone to their match's arena), or null.</summary>
    public ArenaRun? RunFor(ulong charId) => _runs.Values.FirstOrDefault(r => r.Side0.Contains(charId) || r.Side1.Contains(charId));

    /// <summary>True when <paramref name="runId"/> keys a match's arena instance.</summary>
    public bool IsRun(ulong runId) => runId != 0 && _runs.ContainsKey(runId);

    // ---------------------------------------------------------------- the guards

    /// <summary>A wizard used a guard: open the client's PvP window for its tournament.</summary>
    public void OpenKiosk(ulong charId, ArenaKind kind, ulong kioskGid) {
        _world.Send(charId, ArenaMessages.Kiosk(TournamentId(kind), kioskGid));
        Logger.Information("Arena: {0} opened the {1} guard.", Logger.Args(charId, kind));
    }

    /// <summary>The window asks whether Quick Join and Create work for this wizard.</summary>
    public void CanJoin(ulong charId) => _world.Send(charId, ArenaMessages.CanJoin(true));

    /// <summary>The window asks for the matches of a tournament.</summary>
    public void List(ulong charId, uint tournamentId) {
        if (KindOf(tournamentId) is not { } kind) {
            Logger.Debug("Arena: {0} asked for tournament {1}, not a guard's.", Logger.Args(charId, tournamentId));

            return;
        }

        lock (_gate) {
            _watchers[kind].Add(charId);
            var open = _matches.Values.Where(m => m.Kind == kind && m.Phase == Phase.Open).OrderBy(m => m.CreatedUtc).Take(40).ToList();
            var names = new Dictionary<ulong, byte[]>();
            var list = new NewListUpdate {
                m_tournamentID = 0,
                m_tournamentName = Tournament(kind),
                m_tournamentNameID = TournamentId(kind),
                m_clearData = true,
                m_matches = [.. open.Select(m => (ArenaMatchInfo) Info(m, names))],
                m_teams = [],
                m_brackets = [],
                m_totalTeams = open.Count,
            };
            _world.Send(charId, new GAME_5_PROTOCOL.MSG_PVPUPDATEINFO {
                TournamentInfo = ArenaMessages.Blob(list, names),
                CharacterID = _world.Player(charId)?.ActorId ?? 0,
                PromptMsg = 0,
                DiffType = 0,
                IsPvPQueue = 0,
                IsPlayerAccountAlreadyHosting = 0,
            });
        }
    }

    // ---------------------------------------------------------------- joining

    /// <summary>Create Match (Practice): a new match with the creator's size, levels and friends-only choice.</summary>
    public void Create(ulong charId, ArenaKind kind, uint matchNameId, int minLevel, int maxLevel, bool friendsOnly) {
        var size = ArenaRules.TeamSizeOf(Tournament(kind), matchNameId);
        if (_world.Player(charId) is not { } player || size == 0) {
            Logger.Information("Arena: create refused for {0} (match id {1} is not 1v1..4v4 of {2}).",
                Logger.Args(charId, matchNameId, Tournament(kind)));
            _world.Send(charId, ArenaMessages.Error(ArenaErrors.NoSlots));

            return;
        }

        lock (_gate) {
            if (!LeaveLocked(charId, quiet: true)) {
                _world.Send(charId, ArenaMessages.Error(ArenaErrors.MaxedMatches));

                return;
            }

            if (minLevel > 0 && maxLevel > 0 && minLevel > maxLevel) {
                (minLevel, maxLevel) = (maxLevel, minLevel);
            }

            var match = NewMatch(kind, size, charId, friendsOnly, Math.Max(0, minLevel), Math.Max(0, maxLevel));
            Logger.Information("Arena: {0} created {1} match {2} ({3}, levels {4}-{5}{6}).",
                Logger.Args(player.Name, kind, match.Id, match.MatchName, minLevel, maxLevel, friendsOnly ? ", friends only" : ""));
            SeatLocked(match, player, 0);
        }
    }

    /// <summary>Join in a match's circle: a seat on the side whose team id is <paramref name="teamId"/>.</summary>
    public void Join(ulong charId, ulong matchId, ulong teamId) {
        if (_world.Player(charId) is not { } player) {
            return;
        }

        lock (_gate) {
            var match = _matches.GetValueOrDefault(matchId)
                ?? _matches.Values.FirstOrDefault(m => m.TeamIds.Contains(teamId) || m.TeamIds.Contains(matchId));
            if (match is null) {
                _world.Send(charId, ArenaMessages.Error(ArenaErrors.NoSlots));

                return;
            }

            if (match.SideOf(charId) >= 0) {
                return; // already there
            }

            var side = match.TeamIds[1] == teamId ? 1 : match.TeamIds[0] == teamId ? 0
                : ArenaRules.QuickJoinSide(match.Sides[0].Count, match.Sides[1].Count, match.TeamSize);
            var error = side < 0 ? ArenaErrors.NoSlots : JoinError(match, side, player);
            if (error is not null) {
                Logger.Information("Arena: {0} could not join match {1} side {2}: {3}.", Logger.Args(player.Name, match.Id, side + 1, error));
                _world.Send(charId, ArenaMessages.Error(error));

                return;
            }

            if (!LeaveLocked(charId, quiet: true)) {
                return;
            }

            SeatLocked(match, player, side);
        }
    }

    /// <summary>Quick Join: any open spot in a match of this size; a new match when there is none.</summary>
    public void QuickJoin(ulong charId, ArenaKind kind, uint matchNameId) {
        var size = ArenaRules.TeamSizeOf(Tournament(kind), matchNameId);
        if (_world.Player(charId) is not { } player) {
            return;
        }

        lock (_gate) {
            if (_matchOf.TryGetValue(charId, out var current) && _matches.TryGetValue(current, out var already)
                    && already.Phase != Phase.Open) {
                return;
            }

            LeaveLocked(charId, quiet: true);
            var rating = Standing(charId).Rating;
            var candidates = _matches.Values
                .Where(m => m.Kind == kind && m.Phase == Phase.Open && (size == 0 || m.TeamSize == size))
                .Select(m => (Match: m, Side: ArenaRules.QuickJoinSide(m.Sides[0].Count, m.Sides[1].Count, m.TeamSize)))
                .Where(c => c.Side >= 0 && JoinError(c.Match, c.Side, player) is null);

            // Ranked: "matches Wizard101 subscribers with similarly ranked Wizards" (Engadget 2009-01-20): the open
            // match whose wizards' rating is nearest. Practice: the oldest open match.
            var pick = kind == ArenaKind.Ranked
                ? candidates.OrderBy(c => Math.Abs(AverageRating(c.Match) - rating)).ThenBy(c => c.Match.CreatedUtc).FirstOrDefault()
                : candidates.OrderBy(c => c.Match.CreatedUtc).FirstOrDefault();
            if (pick.Match is null) {
                var newSize = size == 0 ? 1 : size;
                var made = NewMatch(kind, newSize, charId, friendsOnly: false, 0, 0);
                Logger.Information("Arena: {0} quick joined {1} {2}v{2}: no open match, made {3}.", Logger.Args(player.Name, kind, newSize, made.Id));
                SeatLocked(made, player, 0);

                return;
            }

            Logger.Information("Arena: {0} quick joined match {1} side {2}.", Logger.Args(player.Name, pick.Match.Id, pick.Side + 1));
            SeatLocked(pick.Match, player, pick.Side);
        }
    }

    /// <summary>Leave (the status window's Leave, or the window's own leave): out of a match that has not started.</summary>
    public void Leave(ulong charId) {
        lock (_gate) {
            LeaveLocked(charId, quiet: false);
        }
    }

    /// <summary>Seconds a wizard's session may be gone (a zone change is a new session) before they leave their match.</summary>
    public const int OfflineGraceSeconds = 30;

    private readonly Dictionary<ulong, DateTime> _goneSince = [];

    /// <summary>
    /// The wizard's session closed. A zone change does that too (the client moves to a new session), so they leave a
    /// match that has not gone to its arena only if they are still offline <see cref="OfflineGraceSeconds"/> later.
    /// </summary>
    public void Offline(ulong charId, DateTime? nowUtc = null) {
        lock (_gate) {
            _goneSince[charId] = nowUtc ?? DateTime.UtcNow;
        }
    }

    private void SweepOffline(DateTime nowUtc) {
        // Every wizard waiting in a match is checked: one whose session ended (logged out, dropped) for good leaves it.
        foreach (var member in _matches.Values.Where(m => m.Phase is Phase.Open or Phase.Confirming).SelectMany(m => m.Members)) {
            if (_world.Player(member) is null) {
                _goneSince.TryAdd(member, nowUtc);
            }
        }

        foreach (var (charId, since) in _goneSince.ToList()) {
            if (_world.Player(charId) is not null) {
                _goneSince.Remove(charId);
            }
            else if ((nowUtc - since).TotalSeconds >= OfflineGraceSeconds) {
                _goneSince.Remove(charId);
                Logger.Information("Arena: {0} went offline; out of their waiting match.", Logger.Args(charId));
                LeaveLocked(charId, quiet: true);
                foreach (var watchers in _watchers.Values) {
                    watchers.Remove(charId);
                }
            }
        }
    }

    // ---------------------------------------------------------------- Go to Arena

    /// <summary>The Go to Arena (yes) or Decline (no) answer.</summary>
    public void Confirm(ulong charId, bool yes) {
        lock (_gate) {
            if (!_matchOf.TryGetValue(charId, out var id) || !_matches.TryGetValue(id, out var match) || match.Phase != Phase.Confirming) {
                return;
            }

            if (!yes) {
                Decline(match, charId, "declined");

                return;
            }

            match.Confirmed.Add(charId);
            Logger.Information("Arena: {0} pressed Go to Arena for match {1} ({2}/{3}).",
                Logger.Args(charId, match.Id, match.Confirmed.Count, match.Members.Count()));
            if (match.Members.All(match.Confirmed.Contains)) {
                Travel(match);
            }
            else {
                UpdateMembers(match);
            }
        }
    }

    /// <summary>
    /// The clock: Go to Arena answers that ran out count as a decline; a match whose arena never started its fight (nobody
    /// arrived, the zone failed) ends without a contest a minute after its arrival time.
    /// </summary>
    public void Tick(DateTime nowUtc) {
        foreach (var stale in _runs.Values.Where(r => nowUtc >= r.ArrivalEndsUtc.AddSeconds(60)).ToList()) {
            bool travelling;
            lock (_gate) {
                travelling = _matches.TryGetValue(stale.MatchId, out var m) && m.Phase == Phase.Travelling;
            }

            if (travelling) {
                Logger.Warning("Arena: match {0} never started in {1}; no contest.", Logger.Args(stale.MatchId, stale.Zone));
                Finish(stale.RunId, -1, [.. stale.Side0, .. stale.Side1]);
            }
        }

        lock (_gate) {
            SweepOffline(nowUtc);

            // A wizard whose trip did not happen (they were changing zones when it was sent) is sent again, every 10 s
            // until the arena's arrival time is over.
            foreach (var match in _matches.Values.Where(m => m.Phase == Phase.Travelling && (nowUtc - m.TravelledUtc).TotalSeconds >= 10).ToList()) {
                match.TravelledUtc = nowUtc;
                if (!_runs.TryGetValue(match.RunId, out var run) || nowUtc >= run.ArrivalEndsUtc) {
                    continue;
                }

                foreach (var member in match.Members.Where(m => _world.Player(m) is not null
                             && !string.Equals(_world.ZoneOf(m), match.Zone, StringComparison.OrdinalIgnoreCase))) {
                    Logger.Information("Arena: {0} is not in {1} yet; sending them again.", Logger.Args(member, match.Zone));
                    _world.Travel(member, match.Zone, _config.ArenaLocation, match.RunId);
                }
            }

            foreach (var match in _matches.Values.Where(m => m.Phase == Phase.Confirming && nowUtc >= m.ConfirmEndsUtc).ToList()) {
                foreach (var late in match.Members.Where(m => !match.Confirmed.Contains(m)).ToList()) {
                    Decline(match, late, "did not answer");
                }
            }
        }
    }

    // ---------------------------------------------------------------- the fight's end

    /// <summary>
    /// The arena's fight is over. <paramref name="winningSide"/> is 0 or 1 (-1: nobody fought, everyone goes back with
    /// nothing). <paramref name="fled"/> lists the wizards who fled, dropped or never arrived: they lose.
    /// </summary>
    public void Finish(ulong runId, int winningSide, IReadOnlyCollection<ulong> fled) {
        Match? match;
        lock (_gate) {
            if (!_runs.TryRemove(runId, out var run) || !_matches.TryGetValue(run.MatchId, out match)) {
                return;
            }

            _matches.Remove(match.Id);
            foreach (var member in match.Members) {
                _matchOf.Remove(member);
            }
        }

        var members = match.Members.ToList();
        var standings = members.ToDictionary(m => m, Standing);
        double Average(int side) => match.Sides[side].Count == 0 ? _config.StartRating
            : match.Sides[side].Average(m => standings[m].Rating);
        var names = new Dictionary<ulong, byte[]>();
        var actorResults = new List<(ulong CharId, MatchActorResult Result, bool Won, bool Fled, ArenaStanding Before, ArenaStanding After, int Tickets)>();
        foreach (var member in members) {
            var side = match.SideOf(member);
            var fledHere = fled.Contains(member);
            var won = winningSide >= 0 && ArenaRules.CountsAsWin(side == winningSide, fledHere);
            var before = standings[member];
            var after = before;
            var tickets = 0;
            if (winningSide >= 0 && match.Kind == ArenaKind.Ranked) {
                var change = ArenaRules.RatingChange(Average(side), Average(1 - side), won, _config.KFactor);
                after = ArenaRules.After(before, change, won, _config.MinRating);
                tickets = ArenaRules.Tickets(match.Kind, won, _config);
                _world.Ladder.Save(new ArenaLadderEntry {
                    CharId = member, Rating = after.Rating, Wins = after.Wins, Losses = after.Losses, LastMatchUtc = DateTime.UtcNow,
                });
            }

            var player = _players.GetValueOrDefault(member) ?? _world.Player(member);
            var actor = player is null ? null : ArenaMessages.Actor(player, View(match), side, 0, after.Rating, RankIndex(after.Rating));
            if (player is not null) {
                names[player.ActorId] = player.NameBlob;
            }

            actorResults.Add((member, new MatchActorResult {
                m_pActor = actor,
                m_place = won ? 1 : 2,
                m_ratingGained = after.Rating - before.Rating,
                m_arenaPoints = tickets,
                m_pvpCurrency = 0,
                m_pvpTourneyCurrency = 0,
                m_gold = 0,
                m_gameResult = (byte) (winningSide < 0 ? 2 : won ? 0 : 1),
            }, won, fledHere, before, after, tickets));
        }

        var results = new ArenaMatchResults {
            m_matchID = match.Id,
            m_matchNameID = match.MatchNameId,
            m_matchResolution = winningSide,
            m_actorList = [.. actorResults.Select(r => r.Result)],
            m_teamResults = [.. Enumerable.Range(0, 2).Select(side => new MatchTeamResult {
                m_teamID = match.TeamIds[side], m_teamResolution = (byte) (winningSide < 0 ? 2 : side == winningSide ? 0 : 1),
            })],
            m_timeOutDraw = false,
        };
        var blob = ArenaMessages.Blob(results, names);
        foreach (var r in actorResults) {
            var message = new GAME_5_PROTOCOL.MSG_MATCHRESULT {
                CharacterID = _players.GetValueOrDefault(r.CharId)?.ActorId ?? 0, ResultData = blob, AwardData = "",
            };
            _world.Deliver(r.CharId, new ArenaOutcome(match.Id, match.Kind, r.Won, r.Fled, r.Before.Rating, r.After.Rating,
                ArenaRules.RankOf(r.After.Rating, _config.Ranks), r.Tickets, message, _config.HallZone, _config.HallLocation,
                _config.ReturnSeconds));
            Logger.Information("Arena: match {0} ({1}) result for {2}: {3}{4}, rating {5} -> {6}, tickets +{7}.",
                Logger.Args(match.Id, match.Kind, r.CharId, winningSide < 0 ? "no contest" : r.Won ? "win" : "loss",
                    r.Fled ? " (fled)" : "", r.Before.Rating, r.After.Rating, r.Tickets));
        }

        lock (_gate) {
            foreach (var member in members) {
                if (!_matchOf.ContainsKey(member)) {
                    _players.Remove(member);
                }
            }
        }
    }

    // ---------------------------------------------------------------- state helpers (under _gate)

    private Match NewMatch(ArenaKind kind, int teamSize, ulong creator, bool friendsOnly, int minLevel, int maxLevel) {
        var tournament = Tournament(kind);
        var match = new Match {
            Id = NextId(), Kind = kind, Tournament = tournament, TournamentId = ArenaRules.Hash(tournament), TeamSize = teamSize,
            MatchName = ArenaRules.MatchName(tournament, teamSize), Creator = creator, FriendsOnly = friendsOnly,
            MinLevel = minLevel, MaxLevel = maxLevel, Phase = Phase.Open, CreatedUtc = DateTime.UtcNow,
        };
        match.MatchNameId = ArenaRules.Hash(match.MatchName);
        match.TeamIds[0] = NextId();
        match.TeamIds[1] = NextId();
        _matches[match.Id] = match;

        return match;
    }

    private ulong NextId() => ++_nextId;

    private string? JoinError(Match match, int side, ArenaPlayer player)
        => ArenaRules.JoinError(match.Phase != Phase.Open, match.Sides[side].Count, match.TeamSize, player.Level, match.MinLevel,
            match.MaxLevel, match.FriendsOnly, player.CharId == match.Creator || _world.AreFriends(match.Creator, player.CharId));

    private void SeatLocked(Match match, ArenaPlayer player, int side) {
        match.Sides[side].Add(player.CharId);
        _matchOf[player.CharId] = match.Id;
        _players[player.CharId] = player;
        Logger.Information("Arena: {0} sits on side {1} of match {2} ({3} v {4} of {5}v{5}).",
            Logger.Args(player.Name, side + 1, match.Id, match.Sides[0].Count, match.Sides[1].Count, match.TeamSize));
        if (match.IsFull(match.TeamSize)) {
            StartConfirm(match);
        }
        else {
            UpdateMembers(match);
            BroadcastMatch(match);
        }
    }

    /// <summary>Takes a wizard out of the match they wait in. False when they are in one that has gone to its arena.</summary>
    private bool LeaveLocked(ulong charId, bool quiet) {
        if (!_matchOf.TryGetValue(charId, out var id) || !_matches.TryGetValue(id, out var match)) {
            _matchOf.Remove(charId);

            return true;
        }

        if (match.Phase is Phase.Travelling or Phase.Fighting) {
            return false;
        }

        match.Sides[0].Remove(charId);
        match.Sides[1].Remove(charId);
        match.Confirmed.Remove(charId);
        _matchOf.Remove(charId);
        var player = _players.GetValueOrDefault(charId);
        _players.Remove(charId);
        if (player is not null) {
            SendStatus(charId, player, null, -1, 0);
        }

        if (!quiet) {
            Logger.Information("Arena: {0} left match {1}.", Logger.Args(charId, match.Id));
        }

        if (match.Phase == Phase.Confirming) {
            match.Phase = Phase.Open;
            match.Confirmed.Clear();
        }

        if (!match.Members.Any()) {
            _matches.Remove(match.Id);
            BroadcastRemoved(match);
        }
        else {
            if (match.Creator == charId) {
                match.Creator = match.Members.First();
            }

            UpdateMembers(match);
            BroadcastMatch(match);
        }

        return true;
    }

    private void StartConfirm(Match match) {
        match.Phase = Phase.Confirming;
        match.Confirmed.Clear();
        match.ConfirmEndsUtc = DateTime.UtcNow.AddSeconds(_config.ConfirmSeconds);
        Logger.Information("Arena: match {0} is full ({1}v{1}); asking Go to Arena ({2} s).",
            Logger.Args(match.Id, match.TeamSize, _config.ConfirmSeconds));
        BroadcastRemoved(match);
        foreach (var member in match.Members) {
            var player = _players[member];
            var side = match.SideOf(member);
            var actor = ArenaMessages.Actor(player, View(match), side, 8, Standing(member).Rating, RankIndex(Standing(member).Rating));
            SendStatus(member, player, match, side, 8);
            _world.Send(member, new WIZARD_12_PROTOCOL.MSG_PVPCONFIRM {
                TournamentNameID = match.TournamentId,
                Confirm = 1,
                MatchActor = ArenaMessages.Blob(actor, new Dictionary<ulong, byte[]> { [player.ActorId] = player.NameBlob }),
            });
        }
    }

    private void Decline(Match match, ulong charId, string why) {
        Logger.Information("Arena: {0} {1} Go to Arena for match {2}; it waits for another wizard.", Logger.Args(charId, why, match.Id));
        var others = match.Members.Where(m => m != charId).ToList();
        LeaveLocked(charId, quiet: true);
        foreach (var other in others) {
            _world.Send(other, ArenaMessages.Error(ArenaErrors.PlayerDeclined));
        }
    }

    private void Travel(Match match) {
        match.Phase = Phase.Travelling;
        match.TravelledUtc = DateTime.UtcNow;
        match.RunId = _world.NewRunId();
        match.Zone = _config.Arenas[_random.Next(_config.Arenas.Length)].Zone;
        var run = new ArenaRun(match.RunId, match.Id, match.Kind, match.Zone, [.. match.Sides[0]], [.. match.Sides[1]],
            DateTime.UtcNow.AddSeconds(_config.ArrivalSeconds));
        _runs[match.RunId] = run;
        Logger.Information("Arena: match {0} goes to {1} (instance {2}): {3} v {4}.",
            Logger.Args(match.Id, match.Zone, match.RunId, string.Join(",", match.Sides[0]), string.Join(",", match.Sides[1])));
        foreach (var member in match.Members) {
            SendStatus(member, _players[member], match, match.SideOf(member), 11);
            _world.Travel(member, match.Zone, _config.ArenaLocation, match.RunId);
        }
    }

    /// <summary>The fight in the arena has begun (no more leaving through the window).</summary>
    public void Started(ulong runId) {
        lock (_gate) {
            if (_runs.TryGetValue(runId, out var run) && _matches.TryGetValue(run.MatchId, out var match)) {
                match.Phase = Phase.Fighting;
            }
        }
    }

    private void UpdateMembers(Match match) {
        foreach (var member in match.Members) {
            var side = match.SideOf(member);
            var status = match.Phase switch {
                Phase.Confirming => !match.Confirmed.Contains(member) ? 8
                    : match.Sides[side].All(match.Confirmed.Contains) ? 10 : 9,
                Phase.Travelling or Phase.Fighting => 11,
                _ => match.Sides[side].Count < match.TeamSize ? 3 : 4,
            };
            SendStatus(member, _players[member], match, side, status);
        }
    }

    private void SendStatus(ulong charId, ArenaPlayer player, Match? match, int side, int status) {
        var standing = Standing(charId);
        var names = new Dictionary<ulong, byte[]>();
        var actor = ArenaMessages.Actor(player, match is null ? null : View(match), side, status, standing.Rating, RankIndex(standing.Rating));
        names[player.ActorId] = player.NameBlob;
        var team = match is null || side < 0 ? null : Team(match, side, names);
        _world.Send(charId, new GAME_5_PROTOCOL.MSG_MATCHMAKERUPDATE {
            CharacterID = player.ActorId,
            MatchTeam = team is null ? new Imcodec.IO.ByteString() : ArenaMessages.Blob(team, names),
            MatchActor = ArenaMessages.Blob(actor, names),
            BracketInfo = "",
            RegistrationInfo = "",
            UpdateMessage = "",
            Status = (byte) status,
        });
    }

    private void BroadcastMatch(Match match) {
        var names = new Dictionary<ulong, byte[]>();
        var update = new TournamentUpdateList {
            m_updates = [new RemoveMatchUpdate { m_matchID = match.Id }, new AddMatchUpdate { m_matchInfo = Info(match, names) }],
            m_matchCount = _matches.Values.Count(m => m.Kind == match.Kind && m.Phase == Phase.Open),
            m_teamCount = 0,
            m_actorCount = match.Members.Count(),
        };
        Broadcast(match.Kind, ArenaMessages.Blob(update, names));
    }

    private void BroadcastRemoved(Match match) {
        var update = new TournamentUpdateList {
            m_updates = [new RemoveMatchUpdate { m_matchID = match.Id }],
            m_matchCount = _matches.Values.Count(m => m.Kind == match.Kind && m.Phase == Phase.Open),
            m_teamCount = 0,
            m_actorCount = 0,
        };
        Broadcast(match.Kind, ArenaMessages.Blob(update));
    }

    private void Broadcast(ArenaKind kind, Imcodec.IO.ByteString updates) {
        foreach (var watcher in _watchers[kind].ToList()) {
            if (_world.Player(watcher) is null) {
                _watchers[kind].Remove(watcher);

                continue;
            }

            _world.Send(watcher, new GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE { Updates = updates, CharacterID = 0 });
        }
    }

    private PvPMatchInfo Info(Match match, Dictionary<ulong, byte[]> names)
        => ArenaMessages.MatchInfo(View(match), [Team(match, 0, names), Team(match, 1, names)]);

    private MatchTeam Team(Match match, int side, Dictionary<ulong, byte[]> names) {
        var actors = new List<PvPActor>();
        foreach (var member in match.Sides[side]) {
            if (_players.GetValueOrDefault(member) is not { } p) {
                continue;
            }

            var standing = Standing(member);
            names[p.ActorId] = p.NameBlob;
            actors.Add(ArenaMessages.Actor(p, View(match), side, match.Phase == Phase.Open ? 4 : 8, standing.Rating, RankIndex(standing.Rating)));
        }

        return ArenaMessages.Team(View(match), side, actors);
    }

    private ArenaMatchView View(Match match) => new(match.Id, match.TournamentId, match.MatchNameId, match.MatchName, match.TeamSize,
        match.TeamIds, _players.GetValueOrDefault(match.Creator)?.ActorId ?? 0, match.FriendsOnly, match.MinLevel, match.MaxLevel,
        match.Phase == Phase.Open ? 0 : 1, match.Kind == ArenaKind.Ranked);

    private double AverageRating(Match match) {
        var members = match.Members.ToList();

        return members.Count == 0 ? _config.StartRating : members.Average(m => Standing(m).Rating);
    }

    /// <summary>A wizard's Ranked standing (the start rating when they have none yet).</summary>
    public ArenaStanding Standing(ulong charId) {
        var entry = _world.Ladder.Load(charId);

        return entry is null ? new ArenaStanding(_config.StartRating, 0, 0) : new ArenaStanding(entry.Rating, entry.Wins, entry.Losses);
    }

    private int RankIndex(int rating) {
        var index = 0;
        for (var i = 0; i < _config.Ranks.Length; i++) {
            if (rating >= _config.Ranks[i].MinRating) {
                index = i;
            }
        }

        return index;
    }

    /// <summary>For the dashboard and tests: the matches now.</summary>
    public IReadOnlyList<(ulong Id, ArenaKind Kind, string Phase, int TeamSize, int Side0, int Side1)> Snapshot() {
        lock (_gate) {
            return [.. _matches.Values.Select(m => (m.Id, m.Kind, m.Phase.ToString(), m.TeamSize, m.Sides[0].Count, m.Sides[1].Count))];
        }
    }

    /// <summary>For tests: the match a wizard is in (0 when none).</summary>
    public ulong MatchOf(ulong charId) {
        lock (_gate) {
            return _matchOf.GetValueOrDefault(charId);
        }
    }

    /// <summary>For tests: a match's team ids.</summary>
    public ulong[] TeamIdsOf(ulong matchId) {
        lock (_gate) {
            return _matches.TryGetValue(matchId, out var m) ? [.. m.TeamIds] : [];
        }
    }

    /// <summary>For tests: the run id of a match that went to its arena.</summary>
    public ulong RunOf(ulong matchId) {
        lock (_gate) {
            return _matches.TryGetValue(matchId, out var m) ? m.RunId : 0;
        }
    }

}
