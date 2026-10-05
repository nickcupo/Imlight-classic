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
 * HELP MANNERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): when an ambient wizard may offer help with a real
 * player's duel, judged the way a 2009 player would (owner: "they are
 * doing things like offering to help when not in view of my combat"):
 *   - it can see the duel: within ViewDistance (1200 units, two seconds
 *     at a run; a duel circle is about 300 across, so at 1200 it is still
 *     plain on screen) and nothing solid in between (SightGrid);
 *   - it noticed it a moment ago: 2 to 5 seconds after the duel came into
 *     view, not at the instant it started;
 *   - the duel wants help: not PvP, a player slot free, and the player is
 *     not plainly winning (the last enemy nearly dead, or every enemy low
 *     while the player is healthy);
 *   - the player has not just said no: a no (or an offer left unanswered)
 *     quiets the whole zone's ambient wizards for that player, not only the
 *     one that asked, so a second wizard does not ask the same thing a
 *     minute later.
 * Wizards farther than ApproachDistance walk over first and ask from
 * there (a player walks up before asking "need help?").
 *
 * USAGE EXAMPLE:
 * var verdict = HelpManners.Judge(new OfferFacts(distance, canSee, seenFor, reaction, odds, free, pvp, quiet));
 * if (verdict == OfferVerdict.Offer) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>How a duel is going for its players, as an onlooker can tell from the health bars.</summary>
/// <param name="EnemiesStanding">Enemies still up (-1: not known yet).</param>
/// <param name="EnemyHealth">The enemies' health left, as a share of their total (0..1).</param>
/// <param name="PlayerHealth">The lowest real player's health share (0..1).</param>
public readonly record struct DuelOdds(int EnemiesStanding, double EnemyHealth, double PlayerHealth) {

    /// <summary>Nothing known yet (the duel has just begun): treated as a fair fight.</summary>
    public static DuelOdds Unknown { get; } = new(-1, 1, 1);

}

/// <summary>What an ambient wizard knows when it thinks of offering help.</summary>
/// <param name="Distance">Ground distance from the wizard to the duel's centre.</param>
/// <param name="CanSee">Nothing solid between them.</param>
/// <param name="SeenFor">How long the duel has been under way where the wizard could notice it.</param>
/// <param name="Reaction">This wizard's reaction time.</param>
/// <param name="Odds">How the duel is going.</param>
/// <param name="FreePlayerSlots">Player slots still open.</param>
/// <param name="Pvp">A PvP duel.</param>
/// <param name="PlayerQuiet">The player said no (or did not answer) lately.</param>
public readonly record struct OfferFacts(float Distance, bool CanSee, TimeSpan SeenFor, TimeSpan Reaction, DuelOdds Odds,
                                         int FreePlayerSlots, bool Pvp, bool PlayerQuiet);

/// <summary>The outcome of <see cref="HelpManners.Judge"/>.</summary>
public enum OfferVerdict { Offer, TooFar, OutOfSight, NotYet, Full, Pvp, WinningEasily, PlayerQuiet }

/// <summary>The rules for help offers (see the file header).</summary>
public static class HelpManners {

    /// <summary>How far an ambient wizard notices a duel (with a clear line to it).</summary>
    public const float ViewDistance = 1200f;

    /// <summary>A wizard farther than this walks over before it asks; it asks from about <see cref="AskFrom"/>.</summary>
    public const float ApproachDistance = 500f;

    /// <summary>How far from the duel's centre a wizard that walked over stands to ask (outside its circle).</summary>
    public const float AskFrom = 380f;

    /// <summary>How far an ambient wizard hears a player's open chat (it does not answer talk across the zone).</summary>
    public const float HearingDistance = 1500f;

    /// <summary>After a no, no ambient wizard in the zone asks that player again for this long.</summary>
    public static readonly TimeSpan QuietAfterNo = TimeSpan.FromMinutes(10);

    /// <summary>After an offer nobody answered, this long.</summary>
    public static readonly TimeSpan QuietAfterSilence = TimeSpan.FromMinutes(5);

    /// <summary>A wizard's reaction time, 2.0 to 5.0 seconds, fixed per wizard (<paramref name="seed"/>).</summary>
    public static TimeSpan ReactionTime(int seed) => TimeSpan.FromSeconds(2.0 + (uint) seed % 31 / 10.0);

    /// <summary>
    /// True when the players are plainly winning: the last enemy is nearly dead, or every enemy is low while the
    /// player is healthy. Nobody offers help then.
    /// </summary>
    public static bool WinningEasily(DuelOdds odds) {
        if (odds.EnemiesStanding < 0) {
            return false;
        }

        if (odds.EnemiesStanding == 0) {
            return true;
        }

        return (odds.EnemiesStanding == 1 && odds.EnemyHealth <= 0.30)
               || (odds.PlayerHealth >= 0.70 && odds.EnemyHealth <= 0.40);
    }

    /// <summary>Whether to offer now; anything but <see cref="OfferVerdict.Offer"/> means not now.</summary>
    public static OfferVerdict Judge(OfferFacts facts) {
        if (facts.Pvp) {
            return OfferVerdict.Pvp;
        }

        if (facts.FreePlayerSlots <= 0) {
            return OfferVerdict.Full;
        }

        if (facts.PlayerQuiet) {
            return OfferVerdict.PlayerQuiet;
        }

        if (facts.Distance > ViewDistance) {
            return OfferVerdict.TooFar;
        }

        if (!facts.CanSee) {
            return OfferVerdict.OutOfSight;
        }

        if (facts.SeenFor < facts.Reaction) {
            return OfferVerdict.NotYet;
        }

        return WinningEasily(facts.Odds) ? OfferVerdict.WinningEasily : OfferVerdict.Offer;
    }

    /// <summary>
    /// Where a wizard <paramref name="distance"/> away walks to before it asks: <see cref="AskFrom"/> from the duel on
    /// the line toward the wizard; null when it is close enough to ask from where it stands.
    /// </summary>
    public static (float X, float Y)? ApproachSpot(float wizardX, float wizardY, float duelX, float duelY) {
        var dx = wizardX - duelX;
        var dy = wizardY - duelY;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance <= ApproachDistance) {
            return null;
        }

        return (duelX + dx / distance * AskFrom, duelY + dy / distance * AskFrom);
    }

}

/// <summary>
/// A zone's memory of who said no to help: the players its ambient wizards leave alone for a while (see
/// <see cref="HelpManners"/>), and when each duel first came into view.
/// </summary>
public sealed class ZoneHelpMemory {

    private readonly Dictionary<ulong, DateTime> _quietUntil = [];
    private readonly Dictionary<ulong, DateTime> _duelSeen = [];

    /// <summary>The player said no: nobody here asks again for <see cref="HelpManners.QuietAfterNo"/>.</summary>
    public void SaidNo(ulong player, DateTime now) => Quiet(player, now + HelpManners.QuietAfterNo);

    /// <summary>The player let an offer lapse: <see cref="HelpManners.QuietAfterSilence"/>.</summary>
    public void Ignored(ulong player, DateTime now) => Quiet(player, now + HelpManners.QuietAfterSilence);

    /// <summary>True while the zone leaves <paramref name="player"/> alone.</summary>
    public bool IsQuiet(ulong player, DateTime now) => _quietUntil.TryGetValue(player, out var until) && now < until;

    /// <summary>True while any of <paramref name="players"/> is left alone.</summary>
    public bool AnyQuiet(IEnumerable<ulong> players, DateTime now) => players.Any(p => IsQuiet(p, now));

    /// <summary>When duel <paramref name="sigil"/> was first noticed (now, the first time it is asked).</summary>
    public DateTime Seen(ulong sigil, DateTime now) {
        if (!_duelSeen.TryGetValue(sigil, out var at)) {
            _duelSeen[sigil] = at = now;
        }

        return at;
    }

    /// <summary>The duel is over.</summary>
    public void Forget(ulong sigil) => _duelSeen.Remove(sigil);

    private void Quiet(ulong player, DateTime until) {
        if (!_quietUntil.TryGetValue(player, out var current) || current < until) {
            _quietUntil[player] = until;
        }
    }

}
