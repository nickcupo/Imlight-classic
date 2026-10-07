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
 * ARENA MESSAGES (r806919 PvP PROTOCOL)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the client messages of the arena guards, built as the official
 * client r806919 reads them (WizardGraphicalClient.exe read with capstone;
 * the session report is playbot-reports/arena-2009.md):
 *   - MSG_PREPVPKIOSK(TournamentNameID, MobileID): the client echoes it and
 *     fires PVP_DisplayWindow, which opens PvPWindow.gui for the tournament.
 *   - MSG_PVP5THAGECANJOINMATCHRESPONSE.CanJoinQueue: Quick Join and Create work.
 *   - MSG_PVPUPDATEINFO.TournamentInfo: a NewListUpdate (matches, teams).
 *   - MSG_TOURNAMENTUPDATE.Updates: a TournamentUpdateList (add/remove match).
 *   - MSG_MATCHMAKERUPDATE: MatchTeam + MatchActor (PvPActor) blobs; the
 *     actor's m_status drives PvPMatchStatusWindow (0 none, 2 looking for a
 *     team, 3 for teammates, 4 for a match, 8 Go to Arena, 9 waiting for
 *     teammates, 10 for opponents, 11 waiting to teleport).
 *   - MSG_PVPCONFIRM (server to client): status 8, the Go to Arena buttons.
 *   - MSG_MATCHRESULT.ResultData: an ArenaMatchResults (PvPMatchResults.gui).
 *   - MSG_ARENA_ERROR.Error: the KingsIsle hash of a string-table key.
 * Every blob is a non-versioned binary object with property mask 0x18, as the
 * client's SerializerBinary writes and reads them.
 *
 * NOTE:
 * PvPActor.m_nameBlob is a std::string carrying the packed name bytes
 * (0x80/0x82 first byte), which the UTF-8 string property cannot carry: the
 * actor is serialized with a 4-byte ASCII marker and the marker's bytes are
 * replaced by the name's in the output (same length).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;

namespace Imlight.CoreLib.Classic.Arena;

/// <summary>A wizard as the arena knows them.</summary>
/// <param name="CharId">Character id (the server's key).</param>
/// <param name="ActorId">The id the client knows its wizard by (its CharacterID: the character list's id).</param>
/// <param name="NameBlob">The packed name (4 bytes) the client unpacks.</param>
/// <param name="Name">The name, for logs and chat.</param>
/// <param name="Level">Magic level.</param>
/// <param name="School">School name as the client's art uses it ("Fire", "Ice", ...).</param>
/// <param name="Gender">0 male, 1 female.</param>
internal sealed record ArenaPlayer(ulong CharId, ulong ActorId, byte[] NameBlob, string Name, int Level, string School, short Gender,
    bool Ambient = false, ArenaPvpSkill? FriendlySkill = null); // CLASSIC: retained through actor cleanup, so an NPC is never treated as an offline saved player.

/// <summary>Builds the arena's client messages.</summary>
internal static class ArenaMessages {

    /// <summary>The property mask of every arena blob (client SerializerBinary, mask 0x18).</summary>
    public const PropertyFlags Mask = (PropertyFlags) 0x18;

    /// <summary>A serializer as the client's: not versioned, no flags.</summary>
    public static ObjectSerializer NewSerializer() => new(Versionable: false, Behaviors: SerializerFlags.None);

    /// <summary>The kiosk opening (the client then opens PvPWindow).</summary>
    public static WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK Kiosk(uint tournamentId, ulong kioskGid) => new() {
        TournamentNameID = tournamentId, MobileID = kioskGid, LeagueID = 0, SeasonID = 0, TransactionID = 0, Patching = 0,
    };

    public static WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCHRESPONSE CanJoin(bool canJoin)
        => new() { CanJoinQueue = (byte) (canJoin ? 1 : 0) };

    public static WIZARD_12_PROTOCOL.MSG_ARENA_ERROR Error(string key) => new() { Error = ArenaRules.Hash(key) };

    public static WIZARD_12_PROTOCOL.MSG_UPDATEARENAPOINTS ArenaPoints(int total) => new() { Points = total };

    public static WIZARD3_56_PROTOCOL.MSG_UPDATEPVPCURRENCY PvpCurrency(int total)
        => new() { PvPCurrency = total, MaxPvPCurrency = Math.Max(total, 0) };

    /// <summary>The client's PvPActor for a wizard.</summary>
    public static PvPActor Actor(ArenaPlayer player, ArenaMatchView? match, int side, int status, int rating, int rank) => new() {
        m_nActorID = player.ActorId,
        m_nLadderContainerID = 0,
        m_nTournamentNameID = match?.TournamentId ?? 0,
        m_nTournamentID = 0,
        m_leagueID = 0,
        m_seasonID = 0,
        m_nMatchNameID = match?.MatchNameId ?? 0,
        m_nMatchID = match?.MatchId ?? 0,
        m_nTeamID = match is not null && side is 0 or 1 ? match.TeamIds[side] : 0,
        m_status = status,
        m_costAdj = new MatchCostAdjustment { m_matchAdjustmentType = ADJUSTMENT_TYPE.UNSET_MAX },
        m_pLadder = null,
        m_bracketID = 0,
        m_overridingELO = -1,
        m_matchKFactor = 32,
        m_userID = 0,
        m_allowRangeToExceedMaxForMatches = true,
        m_matchCrownsCost = -1,
        m_rank = rank,
        m_rating = rating,
        m_isSubscriber = true,
        m_meetBracketBadgeRequirnments = true,
        m_1v1 = match?.TeamSize == 1,
        m_PVPHistoryStr = "",
        m_optInOut = "0",
        m_sIgnoreListData = "",
        m_pvpStatus = 0,
        m_gender = player.Gender,
        m_level = player.Level,
        m_nameBlob = "",
        m_sSchool = player.School,
        m_timeLeft = 0,
    };

    /// <summary>The client's MatchTeam for one side of a match.</summary>
    public static MatchTeam Team(ArenaMatchView match, int side, IReadOnlyList<PvPActor> actors) => new() {
        m_nTeamID = match.TeamIds[side],
        m_matchId = match.MatchId,
        m_matchNameID = match.MatchNameId,
        m_creatorId = match.CreatorActorId,
        m_friendsOnly = match.FriendsOnly,
        m_actors = [.. actors.Cast<MatchActor>()],
        m_bracketID = 0,
        m_leagueID = 0,
        m_seasonID = 0,
        m_maxActorLevel = match.MaxLevel,
    };

    // CLASSIC: r806919 draws a Join cell only for an explicit zero-ID actor. A vacancy is not a wizard or a reservation.
    public static MatchActor Vacancy(ArenaMatchView match, int side) => new() {
        m_nActorID = 0,
        m_nLadderContainerID = 0,
        m_nTournamentNameID = match.TournamentId,
        m_nTournamentID = 0,
        m_nMatchNameID = match.MatchNameId,
        m_nMatchID = match.MatchId,
        m_nTeamID = match.TeamIds[side],
        m_status = 0,
        m_costAdj = new MatchCostAdjustment { m_matchAdjustmentType = ADJUSTMENT_TYPE.UNSET_MAX },
        m_pLadder = null,
        m_bracketID = 0,
        m_leagueID = 0,
        m_seasonID = 0,
        m_overridingELO = -1,
        m_matchCrownsCost = -1,
        m_userID = 0,
        m_1v1 = match.TeamSize == 1,
        m_PVPHistoryStr = "",
        m_optInOut = "0",
        m_sIgnoreListData = "",
    };

    /// <summary>A detached browser team with explicit native cells for every available human seat.</summary>
    public static MatchTeam ListingTeam(ArenaMatchView match, int side, IReadOnlyList<PvPActor> actors) {
        var team = Team(match, side, actors);
        while (team.m_actors.Count < match.TeamSize) team.m_actors.Add(Vacancy(match, side));
        return team;
    }

    /// <summary>The client's PvPMatchInfo for a match (the rows of PvPWindow).</summary>
    public static PvPMatchInfo MatchInfo(ArenaMatchView match, IReadOnlyList<MatchTeam> teams) => new() {
        m_matchID = match.MatchId,
        m_matchNameID = match.MatchNameId,
        m_teams = [.. teams],
        m_matchName = match.MatchName,
        m_matchTitle = "",
        m_matchZoneID = 0,
        m_creatorID = match.CreatorActorId,
        m_matchZone = "",
        m_startTime = 0,
        m_status = match.Status,
        m_friendsOnly = match.FriendsOnly,
        m_tournamentNameID = match.TournamentId,
        m_leagueID = 0,
        m_seasonID = 0,
        m_bUpdateLadder = match.Ranked,
        m_maxPointsPerMatch = 32,
        m_estimatedWeight = 0,
        m_maxELOError = 5000,
        m_uniqueMatchID = match.MatchId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        m_pvpDuelModifiersTemplates = [],
        m_teamSize = (uint) match.TeamSize,
        m_joinQueueRequirements = new PvPMatchJoinQueueRequirements {
            m_minLevel = match.MinLevel,
            m_maxLevel = match.MaxLevel,
            m_friendsOnly = match.FriendsOnly,
            m_requiredBadge = "",
            m_explicitBadgeRequirements = null,
            m_explicitBadgeReqDesc = "",
        },
        m_timeLimitSec = 0,
        m_useHistoricDiego = true,
        m_ignoredList = [],
        m_matchTimer = 0,
        m_minTurnTime = 5,
        m_effects = [],
    };

    /// <summary>Serializes <paramref name="value"/>, putting each actor's packed name into its m_nameBlob.</summary>
    public static ByteString Blob(PropertyClass value, IReadOnlyDictionary<ulong, byte[]>? names = null) {
        var markers = new List<(byte[] Marker, byte[] Name)>();
        var originals = new List<(PvPActor Actor, string Name)>();
        try {
            if (names is { Count: > 0 }) {
                // CLASSIC: a Ranked reply can include the same actor in both the match and team views.
                var seen = new HashSet<PvPActor>(ReferenceEqualityComparer.Instance);
                foreach (var actor in ActorsIn(value)) {
                    if (!seen.Add(actor) || !names.TryGetValue(actor.m_nActorID, out var name) || name is not { Length: 4 }) {
                        continue;
                    }

                    var i = markers.Count;
                    var marker = new byte[] { 0x01, 0x02, (byte) ('A' + (i / 26 % 26)), (byte) ('A' + (i % 26)) };
                    originals.Add((actor, actor.m_nameBlob));
                    actor.m_nameBlob = System.Text.Encoding.ASCII.GetString(marker);
                    markers.Add((marker, name));
                }
            }

            if (!NewSerializer().Serialize(value, Mask, out var output)) {
                throw new InvalidOperationException($"Cannot serialize {value.GetType().Name} for the arena.");
            }

            byte[] bytes = output;
            var replacements = new List<(int Offset, byte[] Name)>();
            foreach (var (marker, name) in markers) {
                var start = 0;
                while (IndexOf(bytes, marker, start) is var at && at >= 0) {
                    replacements.Add((at, name));
                    start = at + marker.Length;
                }
            }
            foreach (var (at, name) in replacements) Buffer.BlockCopy(name, 0, bytes, at, name.Length);
            return new ByteString(bytes);
        }
        finally {
            foreach (var (actor, original) in originals) actor.m_nameBlob = original;
        }
    }

    /// <summary>Reads a client blob (PvPMatchRequest, TournamentInfoRequest); null when it does not decode.</summary>
    public static T? Read<T>(byte[]? data) where T : PropertyClass {
        if (data is not { Length: > 0 }) {
            return null;
        }

        try {
            return NewSerializer().Deserialize<T>(data, Mask, out var value) ? value : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static IEnumerable<PvPActor> ActorsIn(PropertyClass value) => value switch {
        PvPActor actor => [actor],
        MatchTeam team => team.m_actors?.OfType<PvPActor>() ?? [],
        ArenaMatchInfo info => info.m_teams?.SelectMany(t => ActorsIn(t)) ?? [],
        NewListUpdate list => (list.m_matches?.SelectMany(m => ActorsIn(m)) ?? []).Concat(list.m_teams?.SelectMany(t => ActorsIn(t)) ?? []),
        AddMatchUpdate add when add.m_matchInfo is not null => ActorsIn(add.m_matchInfo),
        AddTeamUpdate add when add.m_team is not null => ActorsIn(add.m_team),
        TournamentUpdateList updates => updates.m_updates?.SelectMany(u => ActorsIn(u)) ?? [],
        ArenaMatchResults results => results.m_actorList?.Select(r => r.m_pActor).OfType<PvPActor>() ?? [],
        _ => [],
    };

    private static int IndexOf(byte[] haystack, byte[] needle, int start) {
        for (var i = start; i + needle.Length <= haystack.Length; i++) {
            var ok = true;
            for (var j = 0; j < needle.Length && ok; j++) {
                ok = haystack[i + j] == needle[j];
            }

            if (ok) {
                return i;
            }
        }

        return -1;
    }

}

/// <summary>What a message about a match needs to know of it.</summary>
internal sealed record ArenaMatchView(ulong MatchId, uint TournamentId, uint MatchNameId, string MatchName, int TeamSize,
    ulong[] TeamIds, ulong CreatorActorId, bool FriendsOnly, int MinLevel, int MaxLevel, int Status, bool Ranked);
