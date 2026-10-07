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
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic.Arena;

/// <summary>What one wizard got from a finished match.</summary>
internal sealed record ArenaOutcome(ulong MatchId, ArenaKind Kind, bool Won, bool Fled, int RatingBefore, int RatingAfter,
    string Rank, int Tickets, IMessage Result, string HallZone, string HallLocation, int ReturnSeconds, bool NoContest = false);
internal sealed record ArenaAmbientFailed(ulong RunId); // CLASSIC: internal actor-only cleanup, never a client message.

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
// CLASSIC: optional server-side participants; worlds with ambient wizards off retain human-only play.
// CLASSIC: a dormant friendly roster costs no actor or reservation until a human joins.
internal interface IArenaFriendlyWorld {
    bool FriendlyEnabled { get; }
    ArenaPlayer? PreviewFriendly(int level, int school, ArenaPvpSkill skill);
    ArenaPlayer? ReserveFriendly(int level, int school, ArenaPvpSkill skill);
}

internal interface IArenaAmbientWorld {
    bool AmbientEnabled { get; }
    bool IsAmbient(ulong charId);
    ArenaPlayer? ReserveAmbient(int level, int preferredSchool);
    void ReleaseAmbient(ulong charId);
}

internal sealed record ArenaRun(ulong RunId, ulong MatchId, ArenaKind Kind, string Zone, IReadOnlyList<ulong> Side0,
    IReadOnlyList<ulong> Side1, DateTime ArrivalEndsUtc);

/// <summary>CLASSIC: the 2009 arena's matches.</summary>
internal sealed class ArenaMatchmaker {

    private enum Phase { Open, Confirming, Travelling, Fighting }

    // CLASSIC: the native window changes both its row domain and its size gate with this request.
    private sealed class BrowserWatch(uint requestType, int startingIndex, int count, int? level) {
        internal readonly bool Watch = requestType >= 5;
        internal readonly int Size = (int) (requestType >= 5 ? requestType - 5 : requestType);
        internal int StartingIndex = startingIndex;
        internal readonly int Count = count;
        internal readonly int? Level = level;
        internal readonly Dictionary<ulong, PvPMatchInfo> Matches = [];
        internal readonly Dictionary<ulong, MatchTeam> Teams = [];
        internal int Total;
    }

    private sealed record BrowserPage(List<PvPMatchInfo> Matches, List<MatchTeam> Teams, int Total,
        bool TeamRows, Dictionary<ulong, byte[]> Names);

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
        public bool Autonomous;
        public ArenaPvpSkill? FriendlySkill;
        public int FriendlySchool;
        public DateTime NextAmbientUtc;
        public DateTime? AmbientEndsUtc;

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
    private readonly object _resultsGate = new(); // CLASSIC: serialize complete ranked result batches.
    // CLASSIC: accepted results own their saves/cleanup even after shutdown closes new admissions.
    private bool _stopping;
    private int _finishesInFlight;
    private TaskCompletionSource? _quiesceCompletion;
    private bool _quiesceCleanupFinished;
    private readonly List<Exception> _quiesceFailures = [];
    private readonly ArenaConfig _config;
    private readonly IArenaWorld _world;
    private readonly Dictionary<ulong, Match> _matches = [];
    private readonly Dictionary<ulong, ulong> _matchOf = [];           // charId -> match id
    private readonly Dictionary<ulong, ArenaPlayer> _players = [];      // the wizards in matches (as they joined)
    private readonly Dictionary<ArenaKind, Dictionary<ulong, BrowserWatch>> _watchers = new() {
        [ArenaKind.Practice] = [], [ArenaKind.Ranked] = [],
    };
    // CLASSIC: PrePvPKiosk resets the native tournament; its matching ready echo must bootstrap the first list.
    private readonly Dictionary<ulong, (ArenaKind Kind, ulong KioskGid, object? Owner)> _pendingKiosks = [];
    private readonly ConcurrentDictionary<ulong, ArenaRun> _runs = new();
    private readonly Random _random = new();
    private ulong _nextId = (ulong) DateTime.UtcNow.Ticks & 0x0000_FFFF_FFFF_FFFF;
    internal const int AmbientWaitSeconds = 30;
    internal static readonly TimeSpan AmbientFightLimit = TimeSpan.FromMinutes(20); // CLASSIC: operational no-contest cleanup, not a historical duel rule.
    private readonly Dictionary<ulong, ulong> _spectators = [];
    private int _ambientSchool;
    private readonly Dictionary<ArenaKind, int> _autonomousRounds = [];
    private readonly Dictionary<ArenaKind, int> _autonomousSizes = [];
    private readonly Dictionary<ArenaKind, DateTime> _nextAutonomous = [];
    private IArenaAmbientWorld? Ambient => _world is IArenaAmbientWorld { AmbientEnabled: true } enabled ? enabled : null;
    private IArenaFriendlyWorld? Friendly => Ambient is not null && _world is IArenaFriendlyWorld { FriendlyEnabled: true } enabled ? enabled : null;
    private bool IsAmbient(ulong member) => _players.GetValueOrDefault(member)?.Ambient == true || _world is IArenaAmbientWorld world && world.IsAmbient(member);
    private bool Replaceable(Match match) => !match.Autonomous && (match.Phase == Phase.Open
        || match.Phase == Phase.Confirming && match.Members.Any(IsAmbient));
    private bool Listed(Match match) => match.Phase == Phase.Open || match.Autonomous || Replaceable(match) && match.Members.Any(IsAmbient);

    public ArenaMatchmaker(ArenaConfig config, IArenaWorld world) {
        _config = config;
        _world = world;
    }

    public ArenaConfig Config => _config;

    /// <summary>
    /// CLASSIC: cancel unclaimed matches without database access or awards, then drain results already accepted by Finish.
    /// The caller must retain the database until this task completes and separately stop the arena clock/actor system.
    /// </summary>
    internal Task QuiesceAsync() {
        List<ulong> ambientToRelease;
        Task completion;
        lock (_gate) {
            if (_stopping) return _quiesceCompletion!.Task;
            _stopping = true;
            _quiesceCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _quiesceCompletion.Task;

            // A claimed Finish has removed its match already; its participants/reservations remain its own responsibility.
            var cancelledMatches = _matches.Keys.ToHashSet();
            var cancelledMembers = _matches.Values.SelectMany(m => m.Members).Distinct().ToList();
            ambientToRelease = cancelledMembers.Where(IsAmbient).ToList();
            _matches.Clear();
            _runs.Clear();
            _matchOf.Clear();
            foreach (var member in cancelledMembers) _players.Remove(member);
            foreach (var spectator in _spectators.Where(pair => cancelledMatches.Contains(pair.Value)).Select(pair => pair.Key).ToList())
                _spectators.Remove(spectator);
            foreach (var watchers in _watchers.Values) watchers.Clear();
            _pendingKiosks.Clear();
            _goneSince.Clear();
            _nextAutonomous.Clear();
        }

        var failures = new List<Exception>();
        try {
            foreach (var member in ambientToRelease) {
                try { (_world as IArenaAmbientWorld)?.ReleaseAmbient(member); }
                catch (Exception ex) {
                    failures.Add(ex);
                    Logger.Error("Arena: shutdown cleanup for {0} failed: {1}", Logger.Args(member, ex.Message));
                }
            }
        }
        finally {
            lock (_gate) {
                _quiesceFailures.AddRange(failures);
                _quiesceCleanupFinished = true;
                CompleteQuiesceLocked();
            }
        }
        return completion;
    }

    private void CompleteQuiesceLocked() {
        if (!_stopping || !_quiesceCleanupFinished || _finishesInFlight != 0) return;
        if (_quiesceFailures.Count > 0)
            _quiesceCompletion!.TrySetException(new AggregateException("Arena shutdown could not complete every accepted result or cleanup.", _quiesceFailures));
        else _quiesceCompletion!.TrySetResult();
    }

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
    public void OpenKiosk(ulong charId, ArenaKind kind, ulong kioskGid, object? owner = null) {
        lock (_gate) {
            if (_stopping) return;
            // CLASSIC: TournamentUpdateList has no tournament id; the native manager has only one active guard.
            foreach (var watchers in _watchers.Values) watchers.Remove(charId);
            _pendingKiosks[charId] = (kind, kioskGid, owner);
            _world.Send(charId, ArenaMessages.Kiosk(TournamentId(kind), kioskGid));
            Logger.Information("Arena: {0} opened the {1} guard.", Logger.Args(charId, kind));
        }
    }

    // CLASSIC: r806919 creates an empty TournamentInfo before echoing Prep. Until NewListUpdate fills
    // its name id, SendUpdateRequest refuses to send anything. Never bootstrap before that reset,
    // or on an unsolicited, stale, still-patching or duplicate echo.
    public bool CompleteKiosk(ulong charId, WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK echo, object? owner = null) {
        lock (_gate) {
            if (_stopping || echo.Patching != 0 || echo.LeagueID != 0 || echo.SeasonID != 0
                || !_pendingKiosks.TryGetValue(charId, out var pending)
                || !ReferenceEquals(pending.Owner, owner)
                || echo.TournamentNameID != TournamentId(pending.Kind) || echo.MobileID != pending.KioskGid
                || _world.Player(charId) is null) return false;
            _pendingKiosks.Remove(charId);
            // The native browser requests four visible rows; it can then request/filter subsequent pages normally.
            List(charId, echo.TournamentNameID, numberOfElements: 4);
            Logger.Information("Arena: {0} initialized the {1} browser after the ready kiosk echo.",
                Logger.Args(charId, pending.Kind));
            return true;
        }
    }

    // CLASSIC: disposing an old arena service must not retire a replacement session's pending guard.
    public void RetireKiosk(ulong charId, object owner) {
        lock (_gate)
            if (_pendingKiosks.TryGetValue(charId, out var pending) && ReferenceEquals(pending.Owner, owner))
                _pendingKiosks.Remove(charId);
    }

    /// <summary>The window asks whether Quick Join and Create work for this wizard.</summary>
    public void CanJoin(ulong charId) {
        lock (_gate) _world.Send(charId, ArenaMessages.CanJoin(!_stopping));
    }

    /// <summary>The window asks for the matches of a tournament.</summary>
    public void List(ulong charId, uint tournamentId, int startingIndex = 0, int numberOfElements = 0,
        bool qualifiedOnly = false, uint qualifiedLevel = 0, int qualifiedRank = -1, uint requestType = 0) {
        if (KindOf(tournamentId) is not { } kind) {
            Logger.Debug("Arena: {0} asked for tournament {1}, not a guard's.", Logger.Args(charId, tournamentId));

            return;
        }

        lock (_gate) {
            if (_stopping || requestType > 9) return;
            // The request's qualifiedRank is the viewer's qualification. Classic has no lobby rank restriction;
            // it must not become an invented exact-opponent-rank filter that leaves some ranks with no choices.
            var count = Math.Clamp(numberOfElements > 0 ? numberOfElements : ArenaFriendlyRoster.DefaultPageSize,
                1, ArenaFriendlyRoster.DefaultPageSize);
            var browser = new BrowserWatch(requestType, Math.Max(0, startingIndex), count,
                qualifiedOnly && qualifiedLevel is >= 1 and <= 50 ? (int) qualifiedLevel : null);
            foreach (var watchers in _watchers.Values) watchers.Remove(charId);
            _watchers[kind][charId] = browser;
            SendBrowserPage(charId, kind, browser, BrowserRows(charId, kind, browser));
        }
    }

    private BrowserPage BrowserRows(ulong charId, ArenaKind kind, BrowserWatch browser) {
        var viewer = _world.Player(charId);
        var matches = _matches.Values.Where(match => match.Kind == kind
            && (browser.Size == 0 || match.TeamSize == browser.Size)
            && (browser.Watch ? match.Phase is Phase.Travelling or Phase.Fighting
                : Listed(match) && View(match).Status == 0))
            .OrderBy(match => match.Autonomous).ThenBy(match => match.CreatedUtc).Cast<object>().ToList();
        if (!browser.Watch && Friendly is not null && viewer is not null)
            matches.AddRange(ArenaFriendlyRoster.Challenges(kind, Math.Clamp(viewer.Level, 1, 50), browser.Level)
                .Where(challenge => browser.Size == 0 || challenge.TeamSize == browser.Size));
        var teamRows = kind == ArenaKind.Ranked && !browser.Watch;
        var total = matches.Count * (teamRows ? 2 : 1);
        var names = new Dictionary<ulong, byte[]>();
        var standings = new Dictionary<ulong, ArenaStanding>();
        // CLASSIC: the native frame clamps its page number after a smaller filtered result and does not request again.
        if (browser.StartingIndex >= total)
            browser.StartingIndex = total == 0 ? 0 : (total - 1) / browser.Count * browser.Count;
        var selected = matches.SelectMany(row => teamRows ? new[] { (Row: row, Side: 0), (Row: row, Side: 1) }
                : new[] { (Row: row, Side: -1) })
            .Skip(browser.StartingIndex).Take(browser.Count).ToList();
        var prepared = new Dictionary<object, PvPMatchInfo>();
        foreach (var (row, _) in selected)
            if (!prepared.ContainsKey(row)) prepared[row] = row is Match match ? Info(match, names)
                : FriendlyInfo((ArenaFriendlyChallenge) row, names, standings);
        var teams = teamRows ? selected.Select(selection => prepared[selection.Row].m_teams[selection.Side]).ToList() : [];
        return new BrowserPage([.. prepared.Values], teams, total, teamRows, names);
    }

    // CLASSIC: m_totalTeams is the complete selected domain; continuation counts describe only the materialized page maps.
    private void SendBrowserPage(ulong charId, ArenaKind kind, BrowserWatch browser, BrowserPage page) {
        browser.Matches.Clear(); browser.Teams.Clear(); browser.Total = page.Total;
        var chunks = BrowserChunks(page).ToList();
        var first = chunks.Count > 0 ? chunks[0] : (Matches: new List<PvPMatchInfo>(), Teams: new List<MatchTeam>());
        var list = new NewListUpdate {
            m_tournamentID = 0, m_tournamentName = Tournament(kind), m_tournamentNameID = TournamentId(kind),
            m_clearData = true, m_matches = [.. first.Matches], m_teams = first.Teams, m_brackets = [], m_totalTeams = page.Total,
        };
        _world.Send(charId, new GAME_5_PROTOCOL.MSG_PVPUPDATEINFO {
            TournamentInfo = ArenaMessages.Blob(list, page.Names), CharacterID = _world.Player(charId)?.ActorId ?? 0,
            PromptMsg = 0, DiffType = 0, IsPvPQueue = 0, IsPlayerAccountAlreadyHosting = 0,
        });
        RememberBrowserRows(browser, first.Matches, first.Teams);
        foreach (var chunk in chunks.Skip(1)) {
            SendBrowserUpdates(charId, browser, [.. chunk.Matches.Select(row => (TournamentUpdate) new AddMatchUpdate { m_matchInfo = row }),
                .. chunk.Teams.Select(team => (TournamentUpdate) new AddTeamUpdate { m_team = team })], page.Names);
            RememberBrowserRows(browser, chunk.Matches, chunk.Teams);
        }
    }

    private static IEnumerable<(List<PvPMatchInfo> Matches, List<MatchTeam> Teams)> BrowserChunks(BrowserPage page) {
        if (!page.TeamRows) {
            foreach (var chunk in page.Matches.Chunk(ArenaFriendlyRoster.RowsPerMessage)) yield return ([.. chunk], []);
            yield break;
        }
        var emitted = new HashSet<ulong>();
        foreach (var chunk in page.Teams.Chunk(ArenaFriendlyRoster.RowsPerMessage)) {
            var parents = chunk.Select(team => team.m_matchId.Full).ToHashSet();
            yield return (page.Matches.Where(match => parents.Contains(match.m_matchID.Full) && emitted.Add(match.m_matchID.Full)).ToList(), [.. chunk]);
        }
    }

    private static void RememberBrowserRows(BrowserWatch browser, IEnumerable<PvPMatchInfo> matches, IEnumerable<MatchTeam> teams) {
        foreach (var match in matches) browser.Matches[match.m_matchID.Full] = match;
        foreach (var team in teams) browser.Teams[team.m_nTeamID.Full] = team;
    }

    private void SendBrowserUpdates(ulong charId, BrowserWatch browser, List<TournamentUpdate> updates,
        IReadOnlyDictionary<ulong, byte[]>? names = null) {
        // CLASSIC: native GetCounts includes parent-nested teams plus the standalone team map, including both views twice.
        var teams = browser.Matches.Values.SelectMany(match => match.m_teams).Concat(browser.Teams.Values).ToList();
        _world.Send(charId, new GAME_5_PROTOCOL.MSG_TOURNAMENTUPDATE {
            Updates = ArenaMessages.Blob(new TournamentUpdateList {
                m_updates = updates, m_matchCount = browser.Matches.Count, m_teamCount = teams.Count,
                m_actorCount = teams.Sum(team => team.m_actors.Count(actor => actor.m_status != 0)),
            }, names), CharacterID = 0,
        });
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
            if (_stopping) return;
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
            FillFriendlyNow(match);
        }
    }

    /// <summary>Join in a match's circle: a seat on the side whose team id is <paramref name="teamId"/>.</summary>
    public void Join(ulong charId, ulong matchId, ulong teamId) {
        if (_world.Player(charId) is not { } player) {
            return;
        }

        lock (_gate) {
            if (_stopping) return;
            if (Friendly is not null && (ArenaFriendlyRoster.Read(matchId) ?? ArenaFriendlyRoster.Read(teamId)) is { } challenge) {
                JoinFriendly(player, challenge, teamId);
                return;
            }
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
                : JoinSide(match);
            var error = side < 0 ? ArenaErrors.NoSlots : JoinError(match, side, player);
            if (error is not null) {
                Logger.Information("Arena: {0} could not join match {1} side {2}: {3}.", Logger.Args(player.Name, match.Id, side + 1, error));
                _world.Send(charId, ArenaMessages.Error(error));

                return;
            }

            if (!LeaveLocked(charId, quiet: true)) {
                return;
            }

            MakeRoomForHuman(match, side);
            SeatLocked(match, player, side);
            FillFriendlyNow(match);
        }
    }

    /// <summary>Quick Join: any open spot in a match of this size; a new match when there is none.</summary>
    public void QuickJoin(ulong charId, ArenaKind kind, uint matchNameId) {
        var size = ArenaRules.TeamSizeOf(Tournament(kind), matchNameId);
        if (_world.Player(charId) is not { } player) {
            return;
        }

        lock (_gate) {
            if (_stopping) return;
            if (_matchOf.TryGetValue(charId, out var current) && _matches.TryGetValue(current, out var already)
                    && already.Phase != Phase.Open) {
                return;
            }

            LeaveLocked(charId, quiet: true);
            var rating = Standing(charId).Rating;
            var candidates = _matches.Values
                .Where(m => m.Kind == kind && Replaceable(m) && (size == 0 || m.TeamSize == size))
                .Select(m => (Match: m, Side: JoinSide(m)))
                .Where(c => c.Side >= 0 && JoinError(c.Match, c.Side, player) is null);

            // Ranked: "matches Wizard101 subscribers with similarly ranked Wizards" (Engadget 2009-01-20): the open
            // match whose wizards' rating is nearest. Practice: the oldest open match.
            var pick = kind == ArenaKind.Ranked
                ? candidates.OrderBy(c => Math.Abs(AverageRating(c.Match) - rating)).ThenBy(c => c.Match.CreatedUtc).FirstOrDefault()
                : candidates.OrderBy(c => c.Match.CreatedUtc).FirstOrDefault();
            if (pick.Match is null) {
                var newSize = size == 0 ? 1 : size;
                if (Friendly is not null) {
                    var skill = ArenaFriendlyRoster.NearestSkill(rating);
                    JoinFriendly(player, new ArenaFriendlyChallenge(kind, Math.Clamp(player.Level, 1, 50),
                        _ambientSchool++ % 7, skill, newSize), 0);
                    return;
                }
                var made = NewMatch(kind, newSize, charId, friendsOnly: false, 0, 0);
                Logger.Information("Arena: {0} quick joined {1} {2}v{2}: no open match, made {3}.", Logger.Args(player.Name, kind, newSize, made.Id));
                SeatLocked(made, player, 0);

                return;
            }

            Logger.Information("Arena: {0} quick joined match {1} side {2}.", Logger.Args(player.Name, pick.Match.Id, pick.Side + 1));
            MakeRoomForHuman(pick.Match, pick.Side);
            SeatLocked(pick.Match, player, pick.Side);
            FillFriendlyNow(pick.Match);
        }
    }

    /// <summary>Leave (the status window's Leave, or the window's own leave): out of a match that has not started.</summary>
    public void Leave(ulong charId) {
        lock (_gate) {
            if (_stopping) return;
            if (_spectators.Remove(charId)) {
                ReturnSpectator(charId);
                return;
            }
            if (LeaveLocked(charId, quiet: false)) {
                return;
            }

            // The match has gone to its arena: the fight decides (a flee is a loss). The client cleared its PvP status when
            // it sent the leave (PvPClientManager::ClearPvPStatus), so it gets the match status back: the HUD PvP button
            // still shows this match until the result comes.
            if (_matchOf.TryGetValue(charId, out var id) && _matches.TryGetValue(id, out var match)
                && _players.GetValueOrDefault(charId) is { } player) {
                SendStatus(charId, player, match, match.SideOf(charId), 11);
            }
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
            if (_stopping) return;
            _pendingKiosks.Remove(charId); // CLASSIC: a previous session cannot open a new session's browser.
            _goneSince[charId] = nowUtc ?? DateTime.UtcNow;
        }
    }

    private void SweepOffline(DateTime nowUtc) {
        foreach (var spectator in _spectators.Keys.Where(c => _world.Player(c) is null).ToList())
            _goneSince.TryAdd(spectator, nowUtc);
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
                _spectators.Remove(charId);
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
            if (_stopping) return;
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
        List<ulong> expiredAmbient;
        lock (_gate) {
            if (_stopping) return;
            expiredAmbient = _matches.Values.Where(m => m.Phase == Phase.Fighting && m.AmbientEndsUtc is { } ends && ends <= nowUtc)
                .Select(m => m.RunId).ToList();
        }
        foreach (var runId in expiredAmbient) {
            Logger.Warning("Arena: ambient match instance {0} reached its operational time limit; no contest.", Logger.Args(runId));
            Finish(runId, -1, []);
        }
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
            if (_stopping) return;
            SweepOffline(nowUtc);
            FillWaitingMatches(nowUtc);
            ScheduleAutonomous(nowUtc);

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
        var accepted = false;
        var failures = new List<Exception>();
        Match? match = null;
        List<ulong> members = [];
        Dictionary<ulong, ArenaPlayer?> playerSnapshot = [];
        HashSet<ulong> ambientMembers = [];
        var delivered = new HashSet<ulong>();
        try {
            ArenaMatchView matchView;
            lock (_gate) {
                if (_stopping || !_runs.TryRemove(runId, out var run) || !_matches.TryGetValue(run.MatchId, out match)) {
                    return;
                }

                _finishesInFlight++;
                accepted = true;
                // CLASSIC: acquire cleanup ownership from our state before any world callback can throw.
                members = match.Members.ToList();
                playerSnapshot = members.ToDictionary(member => member, member => _players.GetValueOrDefault(member));
                ambientMembers = playerSnapshot.Where(pair => pair.Value?.Ambient == true).Select(pair => pair.Key).ToHashSet();
                _matches.Remove(match.Id);
                foreach (var member in members) {
                    _matchOf.Remove(member);
                }
                if (match.Autonomous) _nextAutonomous[match.Kind] = DateTime.UtcNow.AddSeconds(10);
                BroadcastRemoved(match);
                foreach (var member in members) if (playerSnapshot[member] is null) playerSnapshot[member] = _world.Player(member);
                ambientMembers.UnionWith(members.Where(IsAmbient));
                matchView = View(match);
            }

            try {
                lock (_resultsGate) {
                    var standings = members.ToDictionary(m => m, Standing);
                    var ladderUpdates = new List<ArenaLadderEntry>();
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
                            ladderUpdates.Add(new ArenaLadderEntry {
                                CharId = member, Rating = after.Rating, Wins = after.Wins, Losses = after.Losses, LastMatchUtc = DateTime.UtcNow,
                            });
                        }

                        var player = playerSnapshot[member];
                        var actor = player is null ? null : ArenaMessages.Actor(player, matchView, side, 0, after.Rating, RankIndex(after.Rating));
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

                    if (ladderUpdates.Count > 0) _world.Ladder.SaveMany(ladderUpdates);

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
                            CharacterID = playerSnapshot[r.CharId]?.ActorId ?? 0, ResultData = blob, AwardData = "",
                        };
                        try {
                            _world.Deliver(r.CharId, new ArenaOutcome(match.Id, match.Kind, r.Won, r.Fled, r.Before.Rating, r.After.Rating,
                                ArenaRules.RankOf(r.After.Rating, _config.Ranks), r.Tickets, message, _config.HallZone, _config.HallLocation,
                                _config.ReturnSeconds, winningSide < 0));
                            delivered.Add(r.CharId);
                        }
                        catch (Exception ex) {
                            failures.Add(ex);
                            Logger.Error("Arena: match {0} delivery to {1} failed: {2}", Logger.Args(match.Id, r.CharId, ex.Message));
                        }
                        Logger.Information("Arena: match {0} ({1}) result for {2}: {3}{4}, rating {5} -> {6}, tickets +{7}.",
                            Logger.Args(match.Id, match.Kind, r.CharId, winningSide < 0 ? "no contest" : r.Won ? "win" : "loss",
                                r.Fled ? " (fled)" : "", r.Before.Rating, r.After.Rating, r.Tickets));
                    }
                }
            }
            catch (Exception ex) {
                failures.Add(ex);
                // CLASSIC: failure to save a result must never strand an arena participant or leak NPC reservations.
                Logger.Error("Arena: match {0} result failed; returning remaining participants: {1}", Logger.Args(match.Id, ex.Message));
            }
        }
        catch (Exception ex) {
            failures.Add(ex);
            throw;
        }
        finally {
            if (accepted) {
                try {
                    var finishedMatch = match!;
                    foreach (var member in members) {
                        try {
                            if (ambientMembers.Contains(member)) (_world as IArenaAmbientWorld)?.ReleaseAmbient(member);
                            else if (!delivered.Contains(member)) {
                                if (playerSnapshot[member] is { } player) SendStatus(member, player, null, -1, 0);
                                _world.Inform(member, "The arena result could not be completed. Returning to the arena hall; please tell the server owner.");
                                _world.Travel(member, _config.HallZone, _config.HallLocation, 0);
                            }
                        }
                        catch (Exception ex) {
                            failures.Add(ex);
                            Logger.Error("Arena: match {0} cleanup for {1} failed: {2}", Logger.Args(finishedMatch.Id, member, ex.Message));
                        }
                    }
                    List<ulong> spectators;
                    lock (_gate) {
                        spectators = _spectators.Where(pair => pair.Value == finishedMatch.Id).Select(pair => pair.Key).ToList();
                        foreach (var spectator in spectators) _spectators.Remove(spectator);
                        foreach (var member in members) if (!_matchOf.ContainsKey(member)) _players.Remove(member);
                    }
                    foreach (var spectator in spectators) {
                        try { ReturnSpectator(spectator); }
                        catch (Exception ex) {
                            failures.Add(ex);
                            Logger.Error("Arena: spectator {0} return failed: {1}", Logger.Args(spectator, ex.Message));
                        }
                    }
                }
                catch (Exception ex) {
                    failures.Add(ex);
                    throw;
                }
                finally {
                    lock (_gate) {
                        if (_stopping) _quiesceFailures.AddRange(failures);
                        _finishesInFlight--;
                        CompleteQuiesceLocked();
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- state helpers (under _gate)

    // CLASSIC: a real wizard can replace a waiting NPC until the existing human confirmations start the trip.
    private int JoinSide(Match match) => ArenaRules.QuickJoinSide(
        Replaceable(match) ? match.Sides[0].Count(m => !IsAmbient(m)) : match.Sides[0].Count,
        Replaceable(match) ? match.Sides[1].Count(m => !IsAmbient(m)) : match.Sides[1].Count, match.TeamSize);

    private void RemoveAmbient(Match match, ulong member) {
        match.Sides[0].Remove(member); match.Sides[1].Remove(member); match.Confirmed.Remove(member);
        _matchOf.Remove(member); _players.Remove(member);
        (_world as IArenaAmbientWorld)?.ReleaseAmbient(member);
    }

    private void MakeRoomForHuman(Match match, int side) {
        if (!Replaceable(match)) return;
        if (match.Sides[side].Count >= match.TeamSize && match.Sides[side].FirstOrDefault(IsAmbient) is var ambient && ambient != 0)
            RemoveAmbient(match, ambient);
        if (match.Phase == Phase.Confirming) {
            match.Phase = Phase.Open;
            match.Confirmed.Clear(); // changed teams require every human to confirm the new lineup themselves
        }
        match.NextAmbientUtc = DateTime.UtcNow.AddSeconds(AmbientWaitSeconds);
    }

    private bool ReserveSeats(Match match, int level) {
        if (Ambient is not { } world) return false;
        var reserved = new List<(ArenaPlayer Player, int Side)>();
        try {
            var sides = match.FriendlySkill is not null ? new[] { 1, 0 } : new[] { 0, 1 };
            var schoolOffset = 0;
            foreach (var side in sides) {
                for (var vacancy = match.Sides[side].Count; vacancy < match.TeamSize; vacancy++) {
                    var player = match.FriendlySkill is { } skill && Friendly is { } roster
                        ? roster.ReserveFriendly(level, (match.FriendlySchool + schoolOffset++) % 7, skill)
                        : world.ReserveAmbient(level, _ambientSchool++ % 7);
                    if (player is null) { foreach (var seat in reserved) world.ReleaseAmbient(seat.Player.CharId); return false; }
                    reserved.Add((player, side));
                }
            }
        } catch (Exception ex) {
            foreach (var seat in reserved) world.ReleaseAmbient(seat.Player.CharId);
            Logger.Warning("Arena: NPC reservation failed: {0}", Logger.Args(ex.Message));
            return false;
        }
        foreach (var seat in reserved) SeatLocked(match, seat.Player, seat.Side);
        return true;
    }

    // CLASSIC: reserve only after a human commits to this match. Listing thousands of dormant choices spawns nothing.
    private void JoinFriendly(ArenaPlayer player, ArenaFriendlyChallenge challenge, ulong teamId) {
        if (Friendly is null || player.Level != challenge.Level) {
            _world.Send(player.CharId, ArenaMessages.Error(ArenaErrors.NoSlots)); return;
        }
        if (!LeaveLocked(player.CharId, quiet: true)) return;
        var made = NewMatch(challenge.Kind, challenge.TeamSize, player.CharId, false, challenge.Level, challenge.Level);
        made.FriendlySkill = challenge.Skill; made.FriendlySchool = challenge.School;
        var side = teamId == challenge.TeamIds[1] ? 1 : 0;
        SeatLocked(made, player, side);
        FillFriendlyNow(made);
    }

    private void FillFriendlyNow(Match match) {
        if (Friendly is null || match.FriendsOnly || match.Autonomous || match.Phase != Phase.Open
            || !match.Members.Any(c => !IsAmbient(c))) return;
        if (match.FriendlySkill is null) {
            var humans = match.Members.Where(c => !IsAmbient(c)).ToArray();
            match.FriendlySkill = ArenaFriendlyRoster.NearestSkill((int) humans.Average(c => Standing(c).Rating));
            match.FriendlySchool = _ambientSchool++ % 7;
        }
        var humanLevel = (int) Math.Round(match.Members.Where(c => !IsAmbient(c)).Average(c => _players[c].Level));
        var minimum = Math.Clamp(match.MinLevel > 0 ? match.MinLevel : 1, 1, 50);
        var maximum = Math.Clamp(match.MaxLevel > 0 ? match.MaxLevel : 50, minimum, 50);
        match.NextAmbientUtc = DateTime.UtcNow.AddSeconds(AmbientWaitSeconds);
        if (!ReserveSeats(match, Math.Clamp(humanLevel, minimum, maximum)))
            foreach (var human in match.Members.Where(c => !IsAmbient(c)))
                _world.Inform(human, "Friendly arena seats are busy. Your match will retry while you wait.");
    }

    private PvPMatchInfo FriendlyInfo(ArenaFriendlyChallenge challenge, Dictionary<ulong, byte[]> names,
        Dictionary<ulong, ArenaStanding> standings) {
        var preview = Friendly!.PreviewFriendly(challenge.Level, challenge.School, challenge.Skill);
        var view = new ArenaMatchView(challenge.Id, TournamentId(challenge.Kind),
            ArenaRules.Hash(ArenaRules.MatchName(Tournament(challenge.Kind), challenge.TeamSize)),
            ArenaRules.MatchName(Tournament(challenge.Kind), challenge.TeamSize), challenge.TeamSize,
            challenge.TeamIds, preview?.ActorId ?? 0, false, challenge.Level, challenge.Level, 0, challenge.Kind == ArenaKind.Ranked);
        var actors = new List<PvPActor>();
        if (preview is not null) {
            if (!standings.TryGetValue(preview.CharId, out var standing)) {
                var saved = _world.Ladder.Load(preview.CharId);
                standings[preview.CharId] = standing = saved is null
                    ? new ArenaStanding(ArenaFriendlyRoster.InitialRating(challenge.Skill), 0, 0)
                    : new ArenaStanding(saved.Rating, saved.Wins, saved.Losses);
            }
            names[preview.ActorId] = preview.NameBlob;
            actors.Add(ArenaMessages.Actor(preview, view, 1, 4, standing.Rating, RankIndex(standing.Rating)));
        }
        var row = ArenaMessages.MatchInfo(view, [ArenaMessages.ListingTeam(view, 0, []), ArenaMessages.ListingTeam(view, 1, actors)]);
        row.m_matchTitle = "Friendly: " + challenge.Skill;
        return row;
    }

    private void FillWaitingMatches(DateTime now) {
        if (Ambient is null) return;
        foreach (var match in _matches.Values.Where(m => !m.Autonomous && !m.FriendsOnly && m.Phase == Phase.Open
                     && now >= m.NextAmbientUtc && m.Members.Any(c => !IsAmbient(c))).OrderBy(m => m.CreatedUtc).ToList()) {
            match.NextAmbientUtc = now.AddSeconds(AmbientWaitSeconds);
            var humanLevel = (int) Math.Round(match.Members.Where(c => !IsAmbient(c)).Average(c => _players[c].Level));
            var minimum = Math.Clamp(match.MinLevel > 0 ? match.MinLevel : 1, 1, 50);
            var maximum = Math.Clamp(match.MaxLevel > 0 ? match.MaxLevel : 50, minimum, 50);
            if (ReserveSeats(match, Math.Clamp(humanLevel, minimum, maximum))) {
                if (match.Phase == Phase.Confirming) match.ConfirmEndsUtc = now.AddSeconds(_config.ConfirmSeconds);
                foreach (var human in match.Members.Where(c => !IsAmbient(c)))
                    _world.Inform(human, "Friendly wizards joined the empty arena seats. Confirm Go to Arena when your team is ready.");
            }
        }
    }

    private void ScheduleAutonomous(DateTime now) {
        if (Ambient is null || _matches.Values.Any(m => !m.Autonomous && m.Phase is Phase.Open or Phase.Confirming)) return;
        foreach (var kind in new[] { ArenaKind.Practice, ArenaKind.Ranked }) {
            if (_matches.Values.Any(m => m.Autonomous && m.Kind == kind) || _nextAutonomous.GetValueOrDefault(kind) > now) continue;
            var round = _autonomousRounds.GetValueOrDefault(kind);
            _autonomousRounds[kind] = (round + 1) % 50;
            var size = 1 + _autonomousSizes.GetValueOrDefault(kind) % 4;
            var level = 1 + (round % 50) * 11 % 50;
            var match = NewMatch(kind, size, 0, false, level, level);
            match.Autonomous = true;
            if (!ReserveSeats(match, level)) {
                _matches.Remove(match.Id); BroadcastRemoved(match);
                _nextAutonomous[kind] = now.AddSeconds(30);
            } else {
                _autonomousSizes[kind] = _autonomousSizes.GetValueOrDefault(kind) + 1;
            }
        }
    }

    // CLASSIC: onlookers use the existing arena trip, never the combat seating or ladder paths.
    public void Watch(ulong charId, ulong matchId) {
        lock (_gate) {
            if (_stopping) return;
            if (_world.Player(charId) is not { } player || _matchOf.ContainsKey(charId) || _spectators.Count >= 64 || !_matches.TryGetValue(matchId, out var match)
                || match.Phase is not (Phase.Travelling or Phase.Fighting)) {
                _world.Send(charId, ArenaMessages.Error(ArenaErrors.MatchStarted)); return;
            }
            _spectators[charId] = match.Id;
            SendStatus(charId, player, match, -1, 11); // retain the existing PvP status window/Leave path without assigning a team
            _world.Travel(charId, match.Zone, _config.ArenaLocation, match.RunId);
            _world.Inform(charId, "Watching this arena match. Use the PvP window's Leave button to return to the arena hall.");
        }
    }

    public bool IsSpectator(ulong charId, ulong matchId = 0) {
        lock (_gate) return _spectators.TryGetValue(charId, out var watched) && (matchId == 0 || watched == matchId);
    }

    private void ReturnSpectator(ulong charId) {
        if (_world.Player(charId) is { } player) SendStatus(charId, player, null, -1, 0);
        _world.Inform(charId, "The arena match is over. Returning to the arena hall.");
        _world.Travel(charId, _config.HallZone, _config.HallLocation, 0);
    }

    // A failed NPC actor/transfer aborts a travelling or active match without awards, rather than wedging a round.
    internal void AmbientLost(ulong charId) {
        ulong run = 0;
        lock (_gate) {
            if (_stopping) return;
            if (!_matchOf.TryGetValue(charId, out var id) || !_matches.TryGetValue(id, out var match)) return;
            if (match.Phase is Phase.Travelling or Phase.Fighting) run = match.RunId;
            else {
                RemoveAmbient(match, charId);
                match.Phase = Phase.Open; match.Confirmed.Clear();
                match.NextAmbientUtc = DateTime.UtcNow.AddSeconds(AmbientWaitSeconds);
                if (match.Autonomous || !match.Members.Any(c => !IsAmbient(c))) {
                    foreach (var remaining in match.Members.ToList()) RemoveAmbient(match, remaining);
                    _matches.Remove(match.Id); BroadcastRemoved(match);
                } else { UpdateMembers(match); BroadcastMatch(match); }
            }
        }
        if (run != 0) Finish(run, -1, []);
    }

    private Match NewMatch(ArenaKind kind, int teamSize, ulong creator, bool friendsOnly, int minLevel, int maxLevel) {
        var tournament = Tournament(kind);
        var match = new Match {
            Id = NextId(), Kind = kind, Tournament = tournament, TournamentId = ArenaRules.Hash(tournament), TeamSize = teamSize,
            MatchName = ArenaRules.MatchName(tournament, teamSize), Creator = creator, FriendsOnly = friendsOnly,
            MinLevel = minLevel, MaxLevel = maxLevel, Phase = Phase.Open, CreatedUtc = DateTime.UtcNow,
            NextAmbientUtc = DateTime.UtcNow.AddSeconds(AmbientWaitSeconds),
        };
        match.MatchNameId = ArenaRules.Hash(match.MatchName);
        match.TeamIds[0] = NextId();
        match.TeamIds[1] = NextId();
        _matches[match.Id] = match;

        return match;
    }

    private ulong NextId() => ++_nextId;

    private string? JoinError(Match match, int side, ArenaPlayer player)
        => ArenaRules.JoinError(!Replaceable(match), Replaceable(match) ? match.Sides[side].Count(m => !IsAmbient(m)) : match.Sides[side].Count, match.TeamSize, player.Level, match.MinLevel,
            match.MaxLevel, match.FriendsOnly, player.CharId == match.Creator || _world.AreFriends(match.Creator, player.CharId));

    private void SeatLocked(Match match, ArenaPlayer player, int side) {
        if (match.Autonomous && match.Creator == 0) match.Creator = player.CharId;
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
        if (_spectators.Remove(charId)) ReturnSpectator(charId);
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
        var wasAmbient = IsAmbient(charId);
        _players.Remove(charId);
        if (wasAmbient) (_world as IArenaAmbientWorld)!.ReleaseAmbient(charId);
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

        if (!match.Members.Any() || !match.Autonomous && match.Members.All(IsAmbient)) {
            foreach (var ambient in match.Members.ToList()) RemoveAmbient(match, ambient);
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
        if (match.Autonomous || match.Members.Any(IsAmbient)) BroadcastMatch(match);
        else BroadcastRemoved(match);
        foreach (var member in match.Members) {
            if (IsAmbient(member)) { match.Confirmed.Add(member); continue; }
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
        if (match.Members.All(match.Confirmed.Contains)) Travel(match);
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
        if (match.Autonomous) BroadcastMatch(match);
        else BroadcastRemoved(match);
        Logger.Information("Arena: match {0} goes to {1} (instance {2}): {3} v {4}.",
            Logger.Args(match.Id, match.Zone, match.RunId, string.Join(",", match.Sides[0]), string.Join(",", match.Sides[1])));
        foreach (var member in match.Members) {
            SendStatus(member, _players[member], match, match.SideOf(member), 11);
            _world.Travel(member, match.Zone, _config.ArenaLocation, match.RunId);
        }
    }

    /// <summary>The fight in the arena has begun (no more leaving through the window).</summary>
    public void Started(ulong runId, DateTime? nowUtc = null) {
        lock (_gate) {
            if (_stopping) return;
            if (_runs.TryGetValue(runId, out var run) && _matches.TryGetValue(run.MatchId, out var match)) {
                match.Phase = Phase.Fighting;
                if (match.Members.Any(IsAmbient)) match.AmbientEndsUtc ??= (nowUtc ?? DateTime.UtcNow) + AmbientFightLimit;
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
        BroadcastBrowser(match.Kind);
    }

    private void BroadcastRemoved(Match match) {
        // CLASSIC: a Join removal can simultaneously become a Watch addition when the match starts travelling.
        BroadcastBrowser(match.Kind);
    }

    private void BroadcastBrowser(ArenaKind kind) {
        foreach (var (charId, browser) in _watchers[kind].ToList()) {
            if (_world.Player(charId) is null) {
                _watchers[kind].Remove(charId);
                continue;
            }
            var page = BrowserRows(charId, kind, browser);
            if (page.Total != browser.Total) {
                SendBrowserPage(charId, kind, browser, page);
                continue;
            }
            // Counts are native PRE-update map counts. Remove both maps before publishing the rebuilt bounded page.
            if (browser.Matches.Count > 0 || browser.Teams.Count > 0) {
                SendBrowserUpdates(charId, browser, [.. browser.Teams.Keys.Select(id => (TournamentUpdate) new RemoveTeamUpdate { m_teamID = id }),
                    .. browser.Matches.Keys.Select(id => (TournamentUpdate) new RemoveMatchUpdate { m_matchID = id })]);
                browser.Teams.Clear(); browser.Matches.Clear();
            }
            foreach (var chunk in BrowserChunks(page)) {
                SendBrowserUpdates(charId, browser, [.. chunk.Matches.Select(row => (TournamentUpdate) new AddMatchUpdate { m_matchInfo = row }),
                    .. chunk.Teams.Select(team => (TournamentUpdate) new AddTeamUpdate { m_team = team })], page.Names);
                RememberBrowserRows(browser, chunk.Matches, chunk.Teams);
            }
        }
    }

    private PvPMatchInfo Info(Match match, Dictionary<ulong, byte[]> names)
        => ArenaMessages.MatchInfo(View(match), [Team(match, 0, names, true), Team(match, 1, names, true)]);

    private MatchTeam Team(Match match, int side, Dictionary<ulong, byte[]> names, bool listing = false) {
        var actors = new List<PvPActor>();
        foreach (var member in match.Sides[side]) {
            // CLASSIC: list rows expose every human-available seat; NPC reservations never grey out a human's Join.
            if (listing && Replaceable(match) && IsAmbient(member)) continue;
            if (_players.GetValueOrDefault(member) is not { } p) {
                continue;
            }

            var standing = Standing(member);
            names[p.ActorId] = p.NameBlob;
            actors.Add(ArenaMessages.Actor(p, View(match), side, match.Phase == Phase.Open ? 4 : 8, standing.Rating, RankIndex(standing.Rating)));
        }

        return listing ? ArenaMessages.ListingTeam(View(match), side, actors) : ArenaMessages.Team(View(match), side, actors);
    }

    // CLASSIC: r806919 admits match status 0 in Join/Friends and 4 in Watch. Only Watch's live phases advertise 4.
    private ArenaMatchView View(Match match) => new(match.Id, match.TournamentId, match.MatchNameId, match.MatchName, match.TeamSize,
        match.TeamIds, _players.GetValueOrDefault(match.Creator)?.ActorId ?? 0, match.FriendsOnly, match.MinLevel, match.MaxLevel,
        Replaceable(match) ? 0 : match.Phase is Phase.Travelling or Phase.Fighting ? 4 : 1, match.Kind == ArenaKind.Ranked);

    private double AverageRating(Match match) {
        var members = match.Members.ToList();

        return members.Count == 0 ? _config.StartRating : members.Average(m => Standing(m).Rating);
    }

    /// <summary>A wizard's Ranked standing (the start rating when they have none yet).</summary>
    public ArenaStanding Standing(ulong charId) {
        var entry = _world.Ladder.Load(charId);

        return entry is null ? new ArenaStanding(_players.GetValueOrDefault(charId)?.FriendlySkill is { } skill
            ? ArenaFriendlyRoster.InitialRating(skill) : _config.StartRating, 0, 0)
            : new ArenaStanding(entry.Rating, entry.Wins, entry.Losses);
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
