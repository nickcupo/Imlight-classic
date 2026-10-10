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
 * AMBIENT LINES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the things ambient wizards say, and the memory that keeps them
 * from saying the same thing to the same player twice in a while (owner,
 * 2026-10-03: "their constant same dialog is annoying"). Rule based only:
 * pools of templates (greetings, school talk, zone tips, reactions, small
 * talk) with {name}, {zone}, {school}, {level}, {next} and {time} slots.
 * 2009 tone: short, friendly, kid-safe, nothing from after 2009.
 * CLASSIC (2026-10-10): rewritten with AmbientLinePool to sound like
 * players rather than a tour guide (owner: "too AI ish"); the idle talk
 * now comes from AmbientLinePool's moods, schools and zones.
 *
 * LineHistory remembers the last HeardWindow templates each player heard
 * from any ambient wizard in the zone; a pick skips those.
 *
 * USAGE EXAMPLE:
 * var line = AmbientLines.Choose(AmbientLines.Idle(context), turn, context);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>
/// The last <see cref="HeardWindow"/> templates each player heard from the zone's ambient wizards.
/// </summary>
public sealed class LineHistory {

    /// <summary>How many recent lines a player will not hear again.</summary>
    public const int HeardWindow = 20;

    private readonly Dictionary<ulong, Queue<string>> _heard = [];

    /// <summary>True when any of <paramref name="audience"/> heard <paramref name="template"/> lately.</summary>
    public bool Recent(IEnumerable<ulong> audience, string template)
        => audience.Any(player => _heard.TryGetValue(player, out var lines) && lines.Contains(template));

    /// <summary>Remembers that <paramref name="audience"/> heard <paramref name="template"/>.</summary>
    public void Note(IEnumerable<ulong> audience, string template) {
        foreach (var player in audience) {
            if (!_heard.TryGetValue(player, out var lines)) {
                if (_heard.Count >= 500) {
                    _heard.Clear();
                }

                _heard[player] = lines = new Queue<string>();
            }

            lines.Enqueue(template);
            while (lines.Count > HeardWindow) {
                lines.Dequeue();
            }
        }
    }

}

/// <summary>Template pools and the no-repeat pick (see the file header).</summary>
public static class AmbientLines {

    // CLASSIC (rewritten 2026-10-10, owner: the wizards sounded "too AI ish"): short, plain, a bit careless, the way kids
    // and parents typed in 2009-2010; no tour-guide lines, no "great teamwork". Every word is in the client's chat
    // dictionary (see AmbientLinePool's header).

    // ---- talk to a player -----------------------------------------------------------------------

    public static readonly string[] Greetings = ["hi {name}", "hey {name}", "hi", "hey", "oh hi", "yo", "hiya {name}", "sup {name}", "hi?"];

    public static readonly string[] FriendGreetings = [
        "{name}!", "hey {name}", "{name} hi", "oh hey {name}", "yo {name}", "{name} wanna quest", "hi {name} where were you",
        "{name}!! finally", "hey {name} what are you doing",
    ];

    public static readonly string[] Thanks = ["np", "yw", "sure", "k", "np lol"];

    public static readonly string[] Bye = ["bye", "cya", "bye {name}", "later", "ttyl"];

    public static readonly string[] HowAreYou = ["good", "bored", "ok you", "tired", "meh", "good just questing"];

    public static readonly string[] Fallback = ["lol", "ok", "what", "huh", "oh", "cool", "hmm", "ya", "idk"];

    public static readonly string[] Level = ["level {level}", "lvl {level}", "{level}", "{level} you?"];

    public static readonly string[] School = ["{school}", "im {school}", "{school} you?"];

    public static readonly string[] Duel = ["maybe later", "lol no", "after this quest", "ok meet at the arena", "ill lose lol"];

    public static readonly string[] Help = ["sure where", "ok start a fight", "what do you need", "k"];

    public static readonly string[] Friend = ["sure", "k", "ok send it"];

    public static readonly string[] Doing = ["nothing", "questing", "bored", "hunting stuff", "waiting for my friend"];

    public static readonly string[] WhereUnknown = ["idk", "no idea", "ask someone else", "check your map", "dunno sorry"];

    public static readonly string[] WhereKnown = ["{where}", "{where} i think", "its in {where}", "{where} lol"];

    // ---- friends --------------------------------------------------------------------------------

    /// <summary>To a friend, about the quest they are on ({quest} is the quest's title).</summary>
    public static readonly string[] FriendQuest = ["still on {quest}?", "did you finish {quest}", "need help with {quest}?", "{quest} took me forever"];

    /// <summary>To a friend, about where they are ({where} is the zone players know).</summary>
    public static readonly string[] FriendZone = ["{where}? cool", "how is {where}", "ill be in {where} later", "still in {where}?"];

    /// <summary>A friend back after a while: did they finish last time's quest.</summary>
    public static readonly string[] RecallQuest = ["{name} did you ever finish {quest}", "{name}! did you beat {quest}"];

    /// <summary>A friend back after a while: last time together in a zone ({ago} is "earlier" or "the other day").</summary>
    public static readonly string[] RecallZone = ["{name} hi again", "{name}!! that was fun {ago}", "hey {name} back in {where}?"];

    /// <summary>A friend it fought beside before.</summary>
    public static readonly string[] RecallFight = ["{name}! wanna fight stuff again", "{name} hi, need help again?"];

    /// <summary>Its answer when a player's friend request comes in.</summary>
    public static readonly string[] FriendAdded = ["ty for the add", "added you", "ok added", "ty {name}"];

    // ---- battles --------------------------------------------------------------------------------

    public static readonly string[] HelpOffer = ["need help?", "want help", "need a hand", "want me to join", "can i join", "i can help if you want", "help?"];

    public static readonly string[] HelpOfferFriend = ["{name} need help", "want help {name}", "{name} want me to join"];

    public static readonly string[] Joining = ["k coming", "ok", "coming", "joining", "here i come", "k"];

    public static readonly string[] NotJoining = ["k", "ok", "np", "ok then", "fine lol", "k good luck"];

    /// <summary>It went to join but the battle had filled.</summary>
    public static readonly string[] Full = ["aw its full", "oh its full", "nvm its full"];

    public static readonly string[] AfterWin = ["gg", "nice", "phew", "that was close", "ty", "easy", "woot", "yay"];

    // ---- away and menu stand-ins ----------------------------------------------------------------

    /// <summary>Going quiet for a few minutes.</summary>
    public static readonly string[] Away = ["brb", "brb", "brb dinner", "brb my mom is calling me", "afk"];

    /// <summary>Back from <see cref="Away"/>.</summary>
    public static readonly string[] BackFromAway = ["back", "im back", "ok back", "back sorry"];

    /// <summary>Menu-chat stand-ins (r806919 QuickChat phrases) where a typed wizard would type a line.</summary>
    public const string MenuJoining = "On my way!", MenuNotJoining = "Good luck", MenuHi = "Hi!", MenuThanks = "Thanks", MenuFull = "Sorry, I can't";

    // ---- on their own ---------------------------------------------------------------------------

    /// <summary>
    /// What an idle wizard might say when its persona is not at hand (the group companions): the shared kid moods, its
    /// school at its level, its level band, the time of day and its zone (twice: the place is what a passer-by cares about).
    /// No numbers (the persona is not known).
    /// </summary>
    public static IReadOnlyList<string> Idle(ChatContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var pool = new List<string>();
        foreach (var mood in AmbientLinePool.MoodsFor(ChatTemperament.Chatty)) {
            pool.AddRange(mood);
        }

        pool.AddRange(AmbientLinePool.SchoolFor(context.School, context.Level));
        pool.AddRange(AmbientLinePool.ForLevel(context.Level));
        if (context.Hour >= 0) {
            pool.AddRange(AmbientLinePool.ForTime(context.Hour, null));
        }

        if (ZoneTips(context.ZoneKey) is { Length: > 0 } tips) {
            pool.AddRange(tips);
            pool.AddRange(tips);
        }

        return pool.Where(t => !t.Contains("{level}", StringComparison.Ordinal) && !t.Contains("{next}", StringComparison.Ordinal)
                               && !t.Contains("||", StringComparison.Ordinal)).ToList(); // one line at a time here
    }

    /// <summary>The lines for the zone with internal name <paramref name="zoneKey"/>, or null.</summary>
    public static string[]? ZoneTips(string? zoneKey)
        => AmbientLinePool.ForZone(zoneKey) is { Length: > 0 } lines ? lines : null;

    /// <summary>"morning", "afternoon", "evening" or "night" for a local hour.</summary>
    public static string TimeOfDay(int hour) => hour switch {
        >= 5 and < 12 => "morning",
        >= 12 and < 17 => "afternoon",
        >= 17 and < 21 => "evening",
        _ => "night",
    };

    /// <summary>
    /// A filled line from <paramref name="templates"/>, starting at <paramref name="turn"/> and skipping templates the
    /// audience heard lately or that need a slot the context cannot fill. When every template was heard lately, an answer
    /// repeats the first that fits and an unprompted line (<paramref name="quietIfHeard"/>) is null. The choice is remembered.
    /// </summary>
    public static string? Choose(IReadOnlyList<string> templates, int turn, ChatContext context,
                                 IReadOnlyDictionary<string, string>? extra = null, bool quietIfHeard = false) {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(context);
        var audience = context.Audience ?? [];
        var start = (int) ((uint) turn % (uint) Math.Max(1, templates.Count));
        string? fallback = null;
        string? fallbackTemplate = null;
        for (var i = 0; i < templates.Count; i++) {
            var template = templates[(start + i) % templates.Count];
            if (Fill(template, context, extra) is not { } line || !AmbientChatBrain.IsClean(line)) {
                continue;
            }

            if (context.History?.Recent(audience, template) == true) {
                (fallback, fallbackTemplate) = (fallback ?? line, fallbackTemplate ?? template);
                continue;
            }

            context.History?.Note(audience, template);
            return line;
        }

        // Everything was heard lately: an answer still answers; an unprompted line stays quiet.
        if (fallbackTemplate is not null && !quietIfHeard) {
            context.History?.Note(audience, fallbackTemplate);
            return fallback;
        }

        return null;
    }

    /// <summary>The template with its slots filled, or null when a slot has no value.</summary>
    public static string? Fill(string template, ChatContext context, IReadOnlyDictionary<string, string>? extra = null) {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        var text = template;
        foreach (var (slot, value) in Slots(context, extra)) {
            var token = "{" + slot + "}";
            if (!text.Contains(token, StringComparison.Ordinal)) {
                continue;
            }

            if (string.IsNullOrEmpty(value)) {
                return null;
            }

            text = text.Replace(token, value, StringComparison.Ordinal);
        }

        return text.Contains('{') ? null : text.Replace("  ", " ").Trim();
    }

    private static IEnumerable<(string, string?)> Slots(ChatContext c, IReadOnlyDictionary<string, string>? extra) {
        yield return ("name", c.SpeakerFirstName ?? (c.Friend?.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()));
        yield return ("zone", c.ZoneName);
        yield return ("school", AmbientChatBrain.SchoolName(c.School));
        yield return ("level", c.Level.ToString(CultureInfo.InvariantCulture));
        yield return ("next", (c.Level + 1).ToString(CultureInfo.InvariantCulture));
        yield return ("time", c.Hour >= 0 ? TimeOfDay(c.Hour) : null);
        if (extra is not null) {
            foreach (var (slot, value) in extra) {
                yield return (slot, value);
            }
        }
    }

}
