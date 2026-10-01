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
 * HELP OFFERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: an ambient wizard always asks before it joins a real player's
 * battle, friends included (owner, 2026-10-01). It asks ("Need a hand?"),
 * and joins only if that player answers yes in chat within about 20
 * seconds ("yes", "y", "sure", "ok" and the like, any case). No answer or
 * a no: it does not join and does not ask that player again for a few
 * minutes. Whether a slot is still free when the yes comes is the
 * caller's check. One wizard tracks its offers here.
 *
 * USAGE EXAMPLE:
 * if (offers.MayOffer(charId, now)) { offers.Offered(charId, duelId, now); Say("Need a hand?"); }
 * var answer = offers.Hear(charId, text, now); // Yes: join duel answer.DuelId if a slot is free.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>A player's answer to an offer.</summary>
public enum HelpAnswerKind { None, Yes, No }

/// <summary>The answer and the duel the offer was about.</summary>
public readonly record struct HelpAnswer(HelpAnswerKind Kind, ulong DuelId);

/// <summary>
/// One ambient wizard's open offers and cool-downs, by player character id.
/// </summary>
public sealed class HelpOffers {

    /// <summary>How long an offer waits for an answer.</summary>
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(20);

    /// <summary>How long a player is not asked again after a no or no answer.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);

    private static readonly HashSet<string> s_yes = new(StringComparer.OrdinalIgnoreCase) {
        "yes", "y", "yeah", "yea", "yep", "yup", "sure", "ok", "okay", "k", "kk", "please", "pls", "plz", "yes please",
        "sure thing", "ok thanks", "ok thx", "yes plz", "help", "help me", "come", "come help", "of course", "definitely", "ya",
    };

    private static readonly HashSet<string> s_no = new(StringComparer.OrdinalIgnoreCase) {
        "no", "n", "nope", "nah", "no thanks", "no thx", "im good", "i'm good", "i got it", "no ty", "naw", "go away",
    };

    private readonly Dictionary<ulong, (ulong DuelId, DateTime At)> _open = [];
    private readonly Dictionary<ulong, DateTime> _quietUntil = [];

    /// <summary>True when the wizard may ask this player now: nothing open and no cool-down.</summary>
    public bool MayOffer(ulong player, DateTime now) {
        Expire(now);
        return !_open.ContainsKey(player) && (!_quietUntil.TryGetValue(player, out var until) || now >= until);
    }

    /// <summary>True while an offer to this player waits for an answer.</summary>
    public bool IsOpen(ulong player, DateTime now) {
        Expire(now);
        return _open.ContainsKey(player);
    }

    /// <summary>The wizard asked <paramref name="player"/> about <paramref name="duelId"/>.</summary>
    public void Offered(ulong player, ulong duelId, DateTime now) => _open[player] = (duelId, now);

    /// <summary>
    /// A chat line from <paramref name="player"/>: a yes or no closes the offer (a no starts the cool-down).
    /// </summary>
    public HelpAnswer Hear(ulong player, string? text, DateTime now) {
        Expire(now);
        if (!_open.TryGetValue(player, out var offer)) {
            return default;
        }

        var kind = Classify(text);
        if (kind == HelpAnswerKind.None) {
            return default;
        }

        _open.Remove(player);
        _quietUntil[player] = now + Cooldown; // Asked once; a yes is used now, so do not ask again at once either.

        return new HelpAnswer(kind, offer.DuelId);
    }

    /// <summary>Offers older than <see cref="AnswerWindow"/> lapse into a cool-down.</summary>
    public void Expire(DateTime now) {
        foreach (var (player, offer) in _open.Where(kv => now - kv.Value.At > AnswerWindow).ToList()) {
            _open.Remove(player);
            _quietUntil[player] = offer.At + AnswerWindow + Cooldown;
        }
    }

    /// <summary>Yes, no or neither, from a chat line (case, punctuation and a trailing "!" or "?" ignored).</summary>
    public static HelpAnswerKind Classify(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return HelpAnswerKind.None;
        }

        var cleaned = new string([.. text.Trim().Where(c => char.IsLetter(c) || c == ' ' || c == '\'')]).Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal)) {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (s_yes.Contains(cleaned)) {
            return HelpAnswerKind.Yes;
        }

        if (s_no.Contains(cleaned)) {
            return HelpAnswerKind.No;
        }

        // "yes!!", "sure thing friend": the first word decides, when it is a clear yes or no.
        var first = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.ToLowerInvariant() switch {
            "yes" or "yeah" or "sure" or "ok" or "okay" or "yep" or "yup" or "please" => HelpAnswerKind.Yes,
            "no" or "nope" or "nah" => HelpAnswerKind.No,
            _ => HelpAnswerKind.None,
        };
    }

}
