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
 * CLASSIC ARENA MATCHES (2009 PRACTICE AND RANKED)
 * ========================================================================
 *
 * PURPOSE:
 * The data (classic-data/pvp/arena-*.yaml) and the pure rules of the
 * January 2009 arena (owner ruling 2026-10-05): the Practice and Ranked
 * guards, matches of 1v1 up to 4v4 that a wizard creates (size, friends
 * only, levels) or joins, "Go to Arena" for everyone, a random arena, and
 * for Ranked a rating, a rank and Arena Tickets.
 *
 * EVIDENCE (dated; KingsIsle sites not used):
 *   - Arena oldid 3991 (2009-01-20), 6408 (2009-01-29): "As of January 19, 2009, PVP Arena now has Ranked Matches
 *     and Practice Matches"; up to 4 players on each team; two figures "Ranked" and "Practice".
 *   - Duels oldid 4913 (2009-01-23) .. 66643 (2010-04-13): Create Match (how many people, friends only, levels),
 *     Join in any circle, Quick Join, "Go to Arena" for everyone, "a random arena", flee = automatic loss.
 *   - News Archive oldid 69111 (2010-05-18): ranks reset "to the default PvP rank of 500" (July 2009).
 *   - youngwizard.wordpress.com 2009-08-31: 10 Arena Tickets for a ranked win, 3 for a loss; Practice gives nothing.
 *   - Engadget 2009-01-20: Ranked matches "similarly ranked" wizards.
 *   - Client r806919: Tournaments/PvPPractice.xml, PvPSanctioned.xml (Root.wad), MatchActor/PvPActor status values,
 *     the error strings (Error_ErrorSideIsFull, Error_MatchStarted, Error_ErrorNoSlotsAvailable, ...).
 * The rank thresholds and the rating formula have no dated source: they are placeholders in the data, marked
 * unverified (see the data file).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Rules;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Pvp;

/// <summary>An arena a match can go to, and where its duel circle sits (the zone's own PvP sigil spot).</summary>
public sealed record ArenaZone(string Zone, float X, float Y, float Z, float Yaw);

/// <summary>
/// An item an Arena Ticket vendor sells: its template, the rank it needs (null: none) and the tickets it costs (null: the
/// template's own m_arenaPointCost, which the client's shop window shows).
/// </summary>
public sealed record ArenaVendorItem(uint Template, string? Rank, int? Price);

/// <summary>An Arena Ticket vendor (Diego the Duelmaster, Roland Silverheart) and the 2009 stock.</summary>
public sealed record ArenaVendor(uint Npc, string Title, ImmutableArray<ArenaVendorItem> Items);

/// <summary>One rank of the Ranked ladder: its name and the lowest rating that holds it.</summary>
public sealed record ArenaRank(string Name, int MinRating);

/// <summary>The 2009 arena: guards, arenas, timers, rating, ranks and tickets.</summary>
public sealed class ArenaConfig {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>The arena hall (the guards' zone) and where a wizard lands there.</summary>
    public required string HallZone { get; init; }
    public required string HallLocation { get; init; }

    /// <summary>The Practice guard's and the Ranked guard's templates.</summary>
    public required uint PracticeKiosk { get; init; }
    public required uint RankedKiosk { get; init; }

    /// <summary>The client's tournament names (Root.wad Tournaments/*.xml).</summary>
    public required string PracticeTournament { get; init; }
    public required string RankedTournament { get; init; }

    /// <summary>The arenas a started match goes to (one at random) and the landing location there.</summary>
    public required ImmutableArray<ArenaZone> Arenas { get; init; }
    public required string ArenaLocation { get; init; }

    /// <summary>The duel circle the server places in each arena (template, sigil type, radius).</summary>
    public required uint CircleTemplate { get; init; }
    public required string CircleSigilType { get; init; }
    public required float CircleRadius { get; init; }

    /// <summary>Seconds to press Go to Arena; seconds the arena waits for everyone; seconds before the trip back.</summary>
    public required int ConfirmSeconds { get; init; }
    public required int ArrivalSeconds { get; init; }
    public required int ReturnSeconds { get; init; }

    public required int StartRating { get; init; }
    public required int KFactor { get; init; }
    public required int MinRating { get; init; }
    public required ImmutableArray<ArenaRank> Ranks { get; init; }

    public required int TicketsWin { get; init; }
    public required int TicketsLoss { get; init; }

    /// <summary>Ranked for every account (2009: subscribers, or 80 Crowns a match; this server has no subscriptions).</summary>
    public required bool RankedForAll { get; init; }

    /// <summary>The Arena Ticket vendors and their 2009 stock.</summary>
    public ImmutableArray<ArenaVendor> TicketVendors { get; init; } = [];

    public required string SourceFile { get; init; }

}

/// <summary>The kind of match a guard offers.</summary>
public enum ArenaKind { Practice, Ranked }

/// <summary>Why a wizard cannot join (each is the client's own string key, MSG_ARENA_ERROR shows it).</summary>
public static class ArenaErrors {

    /// <summary>"The side you requested to join is full"</summary>
    public const string SideIsFull = "Error_ErrorSideIsFull";

    /// <summary>"The match has already begun. Please select a different match"</summary>
    public const string MatchStarted = "Error_MatchStarted";

    /// <summary>"Cannot join match because no slots are available"</summary>
    public const string NoSlots = "Error_ErrorNoSlotsAvailable";

    /// <summary>"This team is full. Please try another one."</summary>
    public const string TeamFull = "Error_NoSlotsAvailable";

    /// <summary>"Your listed side is not allowed in this match."</summary>
    public const string SideNotAllowed = "Error_ErrorSideNotAllowed";

    /// <summary>A wizard declined or did not answer Go to Arena.</summary>
    public const string PlayerDeclined = "Error_PlayerDeclined";

    /// <summary>"You have created the maximum number of matches you are allowed to have outstanding"</summary>
    public const string MaxedMatches = "Error_ErrorMaxedMatches";

}

/// <summary>A wizard's standing on the Ranked ladder.</summary>
public readonly record struct ArenaStanding(int Rating, int Wins, int Losses);

/// <summary>
/// The 2009 arena rules (pure).
/// </summary>
public static class ArenaRules {

    /// <summary>Wizards per side at most (Arena oldid 3696: "Up To 4 Players On Each Team").</summary>
    public const int MaxTeamSize = 4;

    /// <summary>
    /// KingsIsle's string hash (the client's 0x13dbb90, the same as Imcodec's StringHash): tournament, match and
    /// string-table ids are this hash of their names.
    /// </summary>
    public static uint Hash(string? text) {
        if (string.IsNullOrEmpty(text)) {
            return 0;
        }

        var result = 0;
        var shift1 = 0;
        var shift2 = 32;
        foreach (var c in text) {
            var cb = (byte) c;
            result ^= (cb - 32) << shift1;
            if (shift1 > 24) {
                result ^= (cb - 32) >> shift2;
                if (shift1 >= 27) {
                    shift1 -= 32;
                    shift2 += 32;
                }
            }

            shift1 += 5;
            shift2 -= 5;
        }

        return (uint) (result < 0 ? -result : result);
    }

    /// <summary>The client's match template name for a tournament and a team size ("PvPPractice2v2Match").</summary>
    public static string MatchName(string tournament, int teamSize) => $"{tournament}{teamSize}v{teamSize}Match";

    /// <summary>The team size of a match name id of <paramref name="tournament"/>, or 0 when it is none of 1v1..4v4.</summary>
    public static int TeamSizeOf(string tournament, uint matchNameId) {
        for (var size = 1; size <= MaxTeamSize; size++) {
            if (Hash(MatchName(tournament, size)) == matchNameId) {
                return size;
            }
        }

        return 0;
    }

    /// <summary>
    /// The rating change of one side after a ranked match: Elo with the data's K factor against the average rating of
    /// each side. UNVERIFIED for 2009 (no dated formula); the r806919 tournament templates carry m_matchKFactor 32.
    /// </summary>
    public static int RatingChange(double ownAverage, double opponentAverage, bool won, int kFactor) {
        var expected = 1.0 / (1.0 + Math.Pow(10, (opponentAverage - ownAverage) / 400.0));
        var change = (int) Math.Round(kFactor * ((won ? 1.0 : 0.0) - expected), MidpointRounding.AwayFromZero);

        // A win always gains and a loss always costs at least a point.
        return won ? Math.Max(1, change) : Math.Min(-1, change);
    }

    /// <summary>The standing after one ranked match.</summary>
    public static ArenaStanding After(ArenaStanding before, int change, bool won, int minRating)
        => new(Math.Max(minRating, before.Rating + change), before.Wins + (won ? 1 : 0), before.Losses + (won ? 0 : 1));

    /// <summary>The rank a rating holds (the highest whose minimum it reaches).</summary>
    public static string RankOf(int rating, IReadOnlyList<ArenaRank> ranks) {
        var name = ranks.Count > 0 ? ranks[0].Name : "";
        foreach (var rank in ranks.OrderBy(r => r.MinRating)) {
            if (rating >= rank.MinRating) {
                name = rank.Name;
            }
        }

        return name;
    }

    /// <summary>The lowest rating that holds <paramref name="rank"/> (null: no rank needed, 0).</summary>
    public static int MinRatingOf(string? rank, IReadOnlyList<ArenaRank> ranks)
        => rank is null ? 0 : ranks.FirstOrDefault(r => r.Name == rank)?.MinRating ?? int.MaxValue;

    /// <summary>
    /// Whether a wizard may buy a ticket item, or why not: "rank" (their rating is below the item's rank) or "tickets"
    /// (not enough Arena Tickets).
    /// </summary>
    public static string? TicketPurchaseError(int tickets, int price, int rating, int minRating)
        => rating < minRating ? "rank" : tickets < price ? "tickets" : null;

    /// <summary>Arena Tickets a match gives: Ranked only, more for a win (youngwizard 2009-08-31: 10 and 3).</summary>
    public static int Tickets(ArenaKind kind, bool won, ArenaConfig config)
        => kind == ArenaKind.Ranked ? (won ? config.TicketsWin : config.TicketsLoss) : 0;

    /// <summary>
    /// Whether a wizard may take a seat on a side of a match, or the client's error key why not.
    /// </summary>
    /// <param name="started">The match has gone to its arena (or is confirming).</param>
    /// <param name="sideCount">Wizards already on the side.</param>
    /// <param name="teamSize">The match's team size.</param>
    /// <param name="level">The wizard's level.</param>
    /// <param name="minLevel">The creator's minimum level (0: none).</param>
    /// <param name="maxLevel">The creator's maximum level (0: none).</param>
    /// <param name="friendsOnly">The match is friends only.</param>
    /// <param name="friendOfCreator">The wizard is the creator's friend (or the creator).</param>
    public static string? JoinError(bool started, int sideCount, int teamSize, int level, int minLevel, int maxLevel,
        bool friendsOnly, bool friendOfCreator) {
        if (started) {
            return ArenaErrors.MatchStarted;
        }

        if (sideCount >= teamSize) {
            return ArenaErrors.SideIsFull;
        }

        if ((minLevel > 0 && level < minLevel) || (maxLevel > 0 && level > maxLevel) || (friendsOnly && !friendOfCreator)) {
            return ArenaErrors.SideNotAllowed;
        }

        return null;
    }

    /// <summary>
    /// The side a Quick Join takes in a match (the emptier side, the first on a tie), or -1 when both are full.
    /// Duels oldid 4913: Quick Join "will place you in any open spot".
    /// </summary>
    public static int QuickJoinSide(int side0, int side1, int teamSize) {
        if (side0 >= teamSize && side1 >= teamSize) {
            return -1;
        }

        if (side0 >= teamSize) {
            return 1;
        }

        if (side1 >= teamSize) {
            return 0;
        }

        return side1 < side0 ? 1 : 0;
    }

    /// <summary>
    /// How a finished match counts for one wizard: a loss when they fled or left before the end, whatever their side
    /// did (Duels oldid 4913: "If you flee, you automatically lose, even if you were already defeated").
    /// </summary>
    public static bool CountsAsWin(bool sideWon, bool fled) => sideWon && !fled;

}

/// <summary>
/// Loads and validates the arena file.
/// </summary>
public static class ArenaLoader {

    private static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "unverified", "hall", "kiosks", "tournaments",
        "arenas", "arena_location", "circle", "confirm_seconds", "arrival_seconds", "return_seconds", "rating", "ranks", "tickets",
        "ranked_for_all", "ticket_vendors");
    private static readonly Regex s_id = new(@"^arena-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>Loads the arena file at <paramref name="path"/>.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static ArenaConfig Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the arena file does not exist"));
        }

        var d = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, d);
        if (root is not YMap map) {
            if (root is not null) {
                d.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw d.ToException();
        }

        d.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "hall", "kiosks", "tournaments",
            "arenas", "arena_location", "circle", "confirm_seconds", "arrival_seconds", "return_seconds", "rating", "ranks",
            "tickets"]);
        var id = map.Find("id") is { } idEntry ? d.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            d.At(map.Find("id")!.Value, "id", $"id '{id}' must be arena-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, d);
        YMap? Sub(string key) => map.Find(key) is { } e ? d.ReadMap(e.Value, key) : null;
        string? Str(YMap? m, string parent, string key)
            => m?.Find(key) is { } e ? d.ReadString(e.Value, YamlTree.Join(parent, key)) : Missing(m, parent, key);
        string? Missing(YMap? m, string parent, string key) {
            if (m is not null) {
                d.At(m, parent, $"'{key}' is required");
            }

            return null;
        }

        int? Int(YMap? m, string parent, string key, int min, int max) {
            if (m?.Find(key) is { } e) {
                return d.ReadInt(e.Value, YamlTree.Join(parent, key), min, max);
            }

            Missing(m, parent, key);

            return null;
        }

        var hall = Sub("hall");
        var hallZone = Str(hall, "hall", "zone");
        var hallLocation = Str(hall, "hall", "location");
        var kiosks = Sub("kiosks");
        var practiceKiosk = Int(kiosks, "kiosks", "practice", 1, int.MaxValue);
        var rankedKiosk = Int(kiosks, "kiosks", "ranked", 1, int.MaxValue);
        var tournaments = Sub("tournaments");
        var practice = Str(tournaments, "tournaments", "practice");
        var ranked = Str(tournaments, "tournaments", "ranked");

        float? Float(YMap? m, string parent, string key) {
            if (m?.Find(key) is not { } e) {
                Missing(m, parent, key);

                return null;
            }

            if (e.Value is YScalar s && float.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)) {
                return v;
            }

            d.At(e.Value, YamlTree.Join(parent, key), "must be a number");

            return null;
        }

        var arenas = ImmutableArray.CreateBuilder<ArenaZone>();
        if (map.Find("arenas") is { } arenasEntry && d.ReadList(arenasEntry.Value, "arenas") is { } arenaList) {
            for (var i = 0; i < arenaList.Items.Length; i++) {
                var keyPath = YamlTree.Index("arenas", i);
                if (d.ReadMap(arenaList.Items[i], keyPath) is not { } arena) {
                    continue;
                }

                var zone = Str(arena, keyPath, "zone");
                var (x, y, z, yaw) = (Float(arena, keyPath, "x"), Float(arena, keyPath, "y"), Float(arena, keyPath, "z"), Float(arena, keyPath, "yaw"));
                if (zone is not null && x is not null && y is not null && z is not null && yaw is not null) {
                    arenas.Add(new ArenaZone(zone, x.Value, y.Value, z.Value, yaw.Value));
                }
            }

            if (arenas.Count == 0) {
                d.At(arenasEntry.Value, "arenas", "at least one arena is required");
            }
        }

        var arenaLocation = map.Find("arena_location") is { } al ? d.ReadString(al.Value, "arena_location") : null;
        var circle = Sub("circle");
        var circleTemplate = Int(circle, "circle", "template", 1, int.MaxValue);
        var circleSigil = Str(circle, "circle", "sigil_type");
        var circleRadius = Float(circle, "circle", "radius");
        int? Top(string key, int min, int max) => map.Find(key) is { } e ? d.ReadInt(e.Value, key, min, max) : null;
        var confirm = Top("confirm_seconds", 5, 600);
        var arrival = Top("arrival_seconds", 10, 600);
        var back = Top("return_seconds", 0, 600);

        var rating = Sub("rating");
        var start = Int(rating, "rating", "start", 0, 10000);
        var k = Int(rating, "rating", "k_factor", 1, 400);
        var floor = Int(rating, "rating", "min", 0, 10000);

        var ranks = ImmutableArray.CreateBuilder<ArenaRank>();
        if (map.Find("ranks") is { } ranksEntry && d.ReadList(ranksEntry.Value, "ranks") is { } rankList) {
            var last = -1;
            for (var i = 0; i < rankList.Items.Length; i++) {
                var keyPath = YamlTree.Index("ranks", i);
                if (d.ReadMap(rankList.Items[i], keyPath) is not { } rank) {
                    continue;
                }

                var name = Str(rank, keyPath, "name");
                var min = Int(rank, keyPath, "min_rating", 0, 10000);
                if (min is not null && min <= last) {
                    d.At(rank, keyPath, "ranks must be listed from the lowest min_rating up");
                }

                last = min ?? last;
                if (name is not null && min is not null) {
                    ranks.Add(new ArenaRank(name, min.Value));
                }
            }
        }

        var tickets = Sub("tickets");
        var win = Int(tickets, "tickets", "ranked_win", 0, 10000);
        var loss = Int(tickets, "tickets", "ranked_loss", 0, 10000);
        var forAll = map.Find("ranked_for_all") is { } fa ? d.ReadBool(fa.Value, "ranked_for_all") : true;

        var vendors = ImmutableArray.CreateBuilder<ArenaVendor>();
        var rankNames = ranks.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        if (map.Find("ticket_vendors") is { } vendorsEntry && d.ReadList(vendorsEntry.Value, "ticket_vendors") is { } vendorList) {
            for (var i = 0; i < vendorList.Items.Length; i++) {
                var keyPath = YamlTree.Index("ticket_vendors", i);
                if (d.ReadMap(vendorList.Items[i], keyPath) is not { } vendor) {
                    continue;
                }

                var npc = Int(vendor, keyPath, "npc", 1, int.MaxValue);
                var title = Str(vendor, keyPath, "title");
                var items = ImmutableArray.CreateBuilder<ArenaVendorItem>();
                if (vendor.Find("items") is { } itemsEntry && d.ReadList(itemsEntry.Value, YamlTree.Join(keyPath, "items")) is { } itemList) {
                    for (var j = 0; j < itemList.Items.Length; j++) {
                        var itemPath = YamlTree.Index(YamlTree.Join(keyPath, "items"), j);
                        if (d.ReadMap(itemList.Items[j], itemPath) is not { } item) {
                            continue;
                        }

                        var template = Int(item, itemPath, "template", 1, int.MaxValue);
                        var rank = item.Find("rank") is { } rk ? d.ReadString(rk.Value, YamlTree.Join(itemPath, "rank")) : null;
                        if (rank is not null && !rankNames.Contains(rank)) {
                            d.At(item, itemPath, $"rank '{rank}' is not one of the ranks");
                        }

                        int? price = item.Find("price") is { } pr ? d.ReadInt(pr.Value, YamlTree.Join(itemPath, "price"), 0) : null;
                        if (template is not null) {
                            items.Add(new ArenaVendorItem((uint) template.Value, rank, price));
                        }
                    }
                }

                if (npc is not null && title is not null) {
                    vendors.Add(new ArenaVendor((uint) npc.Value, title, items.ToImmutable()));
                }
            }
        }

        if (d.HasErrors) {
            throw d.ToException();
        }

        return new ArenaConfig {
            Id = id!, Profiles = profiles, HallZone = hallZone!, HallLocation = hallLocation!,
            PracticeKiosk = (uint) practiceKiosk!.Value, RankedKiosk = (uint) rankedKiosk!.Value,
            PracticeTournament = practice!, RankedTournament = ranked!, Arenas = arenas.ToImmutable(),
            ArenaLocation = arenaLocation ?? "Start", CircleTemplate = (uint) circleTemplate!.Value, CircleSigilType = circleSigil!,
            CircleRadius = circleRadius!.Value, ConfirmSeconds = confirm!.Value, ArrivalSeconds = arrival!.Value,
            ReturnSeconds = back!.Value, StartRating = start!.Value, KFactor = k!.Value, MinRating = floor!.Value,
            Ranks = ranks.ToImmutable(), TicketsWin = win!.Value, TicketsLoss = loss!.Value, RankedForAll = forAll ?? true,
            TicketVendors = vendors.ToImmutable(),
            SourceFile = display,
        };
    }

    /// <summary>For tests and tools: a number as the data writes it.</summary>
    internal static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

}
