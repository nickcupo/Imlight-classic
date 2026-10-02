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
 * AMBIENT CHAT BRAIN
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what an ambient wizard says. No language model of any kind
 * (owner, 2026-10-01): an ELIZA-style list of patterns and reply
 * templates with some game awareness. It greets, makes small talk, reacts
 * to its friend's zone or quest, offers help, and answers simple questions
 * from server data ("what level are you?", "what school?", "want to
 * duel?", "where is Lady Blackhope?" through the WhereIs lookup the server
 * passes in). Friends are greeted by name and now and then reminded of
 * the last time they played together (FriendMemory).
 *
 * Replies are deterministic: the template is picked from the line, the
 * speaker and a turn counter, never from a clock or a random generator.
 * Every reply passes IsClean (2009 tone: short, plain words, no links,
 * no numbers beyond levels) and AmbientChatLimiter keeps the wizard from
 * spamming.
 *
 * USAGE EXAMPLE:
 * var reply = AmbientChatBrain.Reply("hi Ryan, what level are you?", context, direct: false, turn: 3);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Imlight.Classic.Ambient;

/// <summary>What an ambient wizard remembers about a friend (kept in the AmbientWizards collection).</summary>
/// <param name="CharId">The friend's character id.</param>
/// <param name="Name">The friend's wizard name.</param>
/// <param name="LastZone">The zone display name where they were last seen together.</param>
/// <param name="LastQuest">The quest the friend was on then, as the player sees its title.</param>
/// <param name="LastPlayedTogether">When they last fought or talked together (UTC).</param>
/// <param name="TimesHelped">Battles fought together.</param>
public sealed record FriendMemory(ulong CharId, string Name, string? LastZone = null, string? LastQuest = null,
                                  DateTime? LastPlayedTogether = null, int TimesHelped = 0);

/// <summary>What the brain knows when it answers.</summary>
/// <param name="MyName">The ambient wizard's full name.</param>
/// <param name="School">Its school.</param>
/// <param name="Level">Its level.</param>
/// <param name="ZoneName">Where it is (display name).</param>
/// <param name="SpeakerName">Who spoke (full name), when known.</param>
/// <param name="SpeakerZone">The speaker's zone display name, when known.</param>
/// <param name="SpeakerQuest">The speaker's current quest title, when known.</param>
/// <param name="Friend">What it remembers of the speaker, when they are friends.</param>
/// <param name="WhereIs">Server data lookup: a place or character name to where it is, or null.</param>
/// <param name="Now">The current time (UTC), for "last time" remarks.</param>
public sealed record ChatContext(string MyName, AmbientSchool School, int Level, string ZoneName,
                                 string? SpeakerName = null, string? SpeakerZone = null, string? SpeakerQuest = null,
                                 FriendMemory? Friend = null, Func<string, string?>? WhereIs = null,
                                 DateTime Now = default) {

    public string MyFirstName => MyName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? MyName;

    public string? SpeakerFirstName => SpeakerName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

}

/// <summary>
/// Pattern and template replies (see the file header).
/// </summary>
public static class AmbientChatBrain {

    private sealed record Rule(string Name, Regex Pattern, Func<Match, ChatContext, int, string?> Reply);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly string[] s_greetings = ["hi {0}!", "hey {0}", "hello {0}!", "hiya {0}", "oh hi {0}!"];
    private static readonly string[] s_friendGreetings = ["{0}! good to see you again", "hey {0}! :)", "hi {0}! whats up?"];
    private static readonly string[] s_thanks = ["np!", "no problem", "anytime!", "you're welcome"];
    private static readonly string[] s_bye = ["bye!", "cya later", "see you around!", "bye {0}!"];
    private static readonly string[] s_fallbackDirect = ["lol", "cool", "hmm idk", "oh nice", "haha", "yeah", "i see"];
    private static readonly string[] s_howAreYou = ["good! just questing", "pretty good, you?", "great! leveling up", "tired lol, been fighting all day"];

    private static readonly Rule[] s_rules = [
        new("level", new(@"\b(what|wat|wut)\s*(level|lvl|lv)\b|\b(level|lvl)\s*\?|\bhow high\b|\bur level\b|\byour level\b", Options),
            (_, c, t) => Pick(["i'm level {1}", "level {1}!", "{1}, almost {2}"], t, c.SpeakerFirstName ?? "", c.Level, c.Level + 1)),
        new("school", new(@"\b(what|wat|which)\s+school\b|\bur school\b|\byour school\b", Options),
            (_, c, t) => Pick(["{1}! best school", "i'm a {1} wizard", "{1} :)"], t, "", SchoolName(c.School))),
        new("duel", new(@"\b(duel|pvp|fight me|1v1|arena)\b", Options),
            (_, c, t) => Pick(["maybe later, i'm questing", "sure, ask me for a practice match at the arena", "lol i'd lose", "after this quest!"], t)),
        new("where", new(@"\bwhere\s+(is|are|r|can i find|do i find)\s+(?:the\s+)?(?<what>[a-z' ]{3,40})", Options),
            (m, c, t) => WhereReply(m.Groups["what"].Value, c, t)),
        new("help", new(@"\b(can|could|will)\s+(you|u)\s+help\b|\bneed\s+help\b|\bhelp\s+me\b|\bhelp\s+pls\b|\bhelp\s+plz\b", Options),
            (_, c, t) => Pick(["sure, where are you?", "i can help! start a fight and i'll ask to join", "ok! what do you need?"], t)),
        new("friend", new(@"\b(friend me|add me|be my friend|be friends|friend request)\b", Options),
            (_, c, t) => Pick(["sure! send me a request", "ok! add me :)", "yeah sure"], t)),
        new("thanks", new(@"\b(thanks|thank you|thx|ty|tyvm)\b", Options),
            (_, c, t) => Pick(s_thanks, t, c.SpeakerFirstName ?? "")),
        new("bye", new(@"\b(bye|cya|see ya|gtg|got to go|g2g|later)\b", Options),
            (_, c, t) => Pick(s_bye, t, c.SpeakerFirstName ?? "")),
        new("how", new(@"\bhow\s+(are|r)\s+(you|u)\b|\bhow'?s it going\b|\bsup\b|\bwhat'?s up\b", Options),
            (_, c, t) => Pick(s_howAreYou, t)),
        new("quest", new(@"\b(what|which)\s+quest\b|\bwhat are you doing\b|\bwhat r u doing\b|\bwhatcha doing\b", Options),
            (_, c, t) => Pick(["just {1} stuff", "helping out in {2}", "hunting in {2}", "working on my {1} spells"], t, "",
                SchoolName(c.School).ToLowerInvariant(), c.ZoneName)),
        new("greet", new(@"^\s*(hi|hello|hey|hiya|yo|heya|howdy|greetings)\b", Options),
            (_, c, t) => Greeting(c, t)),
    ];

    /// <summary>
    /// The reply to <paramref name="text"/>, or null to stay quiet. A Say that does not use the wizard's first name gets
    /// no reply; a whisper (<paramref name="direct"/>) always gets one.
    /// </summary>
    /// <param name="text">What the player said.</param>
    /// <param name="context">The wizard and the speaker.</param>
    /// <param name="direct">A whisper (Text), not a Say.</param>
    /// <param name="turn">A counter the caller increments per reply, to vary the templates.</param>
    public static string? Reply(string? text, ChatContext context, bool direct, int turn) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var mentioned = Regex.IsMatch(text, $@"\b{Regex.Escape(context.MyFirstName)}\b", RegexOptions.IgnoreCase);
        if (!direct && !mentioned) {
            return null;
        }

        var seed = turn + StableHash(text);
        foreach (var rule in s_rules) {
            var match = rule.Pattern.Match(text);
            if (match.Success && rule.Reply(match, context, seed) is { } reply && IsClean(reply)) {
                return reply;
            }
        }

        var fallback = Pick(s_fallbackDirect, seed);
        return IsClean(fallback) ? fallback : null;
    }

    /// <summary>
    /// A line an idle wizard says on its own now and then: about where it is, its friend nearby, or nothing.
    /// </summary>
    public static string? Idle(ChatContext context, int turn) {
        string[] lines = [
            "anyone want to quest?", "lf group", "this place is busy today", "need anything from the shops?",
            "almost level {1}!", "{2} is so pretty", "anyone seen my pet?", "brb", "hi everyone",
            "{3} spells are the best", "lol", "where's a good place to level?",
        ];
        var line = Pick(lines, turn, "", context.Level + 1, context.ZoneName, SchoolName(context.School));

        return IsClean(line) ? line : null;
    }

    /// <summary>A greeting for a friend who just showed up, now and then recalling last time.</summary>
    public static string? GreetFriend(ChatContext context, int turn) {
        if (context.Friend is not { } friend) {
            return null;
        }

        var first = friend.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? friend.Name;
        var recall = Recall(friend, context, turn);
        var line = recall ?? Pick(s_friendGreetings, turn, first);

        return IsClean(line) ? line : null;
    }

    /// <summary>A remark on the friend's zone or quest, as a buddy would make on seeing them.</summary>
    public static string? AboutFriend(ChatContext context, int turn) {
        if (context.Friend is null) {
            return null;
        }

        if (!string.IsNullOrEmpty(context.SpeakerQuest) && turn % 2 == 0) {
            return Clean(Pick(["how's {1} going?", "still on {1}?", "need help with {1}?"], turn, "", context.SpeakerQuest));
        }

        if (!string.IsNullOrEmpty(context.SpeakerZone)) {
            return Clean(Pick(["you're in {1}? cool", "how is {1}?", "i'll be in {1} later"], turn, "", context.SpeakerZone));
        }

        return null;
    }

    /// <summary>"Need a hand?" and friends: the help offer before joining a battle.</summary>
    public static string HelpOffer(ChatContext context, int turn)
        => Pick(context.Friend is not null
            ? ["need a hand {0}?", "{0}, want help?", "want me to join {0}?"]
            : ["need a hand?", "want some help?", "need help with that fight?"], turn, context.SpeakerFirstName ?? "");

    /// <summary>The reply to a yes (joining) or no.</summary>
    public static string HelpAnswered(bool yes, int turn)
        => yes ? Pick(["on my way!", "coming!", "ok!"], turn) : Pick(["ok, good luck!", "np, have fun", "ok!"], turn);

    /// <summary>
    /// True for a line fit to send: short, letters, digits, spaces and plain punctuation only, no links, no long digit
    /// runs (2009 chat dropped numbers that could be personal), and none of the blocked words.
    /// </summary>
    public static bool IsClean(string? line) {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 80) {
            return false;
        }

        if (line.Any(c => !(char.IsLetterOrDigit(c) || " .,!?'-:)(".Contains(c)))) {
            return false;
        }

        if (Regex.IsMatch(line, @"\d{3,}|www|http|\.com", RegexOptions.IgnoreCase)) {
            return false;
        }

        var words = line.ToLowerInvariant().Split([' ', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        return !words.Any(s_blocked.Contains);
    }

    private static readonly HashSet<string> s_blocked = new(StringComparer.OrdinalIgnoreCase) {
        "damn", "hell", "stupid", "idiot", "dumb", "hate", "kill", "die", "dead", "shut", "crap", "sucks", "noob",
        "password", "address", "phone", "email",
    };

    private static string? Recall(FriendMemory friend, ChatContext context, int turn) {
        if (turn % 3 != 0) {
            return null;
        }

        var first = friend.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? friend.Name;
        if (friend.LastQuest is { Length: > 0 } quest && turn % 2 == 0) {
            return Clean($"hi {first}! did you finish {quest}?");
        }

        if (friend.LastZone is { Length: > 0 } zone) {
            var ago = context.Now == default || friend.LastPlayedTogether is null
                ? "last time"
                : (context.Now - friend.LastPlayedTogether.Value).TotalDays >= 1 ? "the other day" : "earlier";
            return Clean($"hey {first}! that was fun in {zone} {ago}");
        }

        return friend.TimesHelped > 0 ? Clean($"{first}! ready for another fight?") : null;
    }

    private static string? WhereReply(string what, ChatContext context, int turn) {
        var subject = what.Trim().TrimEnd('?', '.', '!');
        if (subject.Length == 0) {
            return null;
        }

        if (context.WhereIs?.Invoke(subject) is { Length: > 0 } where) {
            return Clean(Pick(["{1} is in {2}", "try {2}", "{2}, i think"], turn, "", Title(subject), where));
        }

        return Clean(Pick(["hmm not sure, ask an npc", "idk, sorry", "i forget where that is"], turn));
    }

    private static string? Greeting(ChatContext context, int turn) {
        var who = context.SpeakerFirstName ?? "";
        if (context.Friend is not null) {
            return GreetFriend(context, turn);
        }

        return Clean(Pick(s_greetings, turn, who).Replace("  ", " ").Replace(" !", "!"));
    }

    private static string? Clean(string line) => IsClean(line) ? line : null;

    private static string Pick(IReadOnlyList<string> templates, int turn, params object[] args) {
        var template = templates[(int) ((uint) turn % (uint) templates.Count)];
        var text = args.Length == 0 ? template : string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);

        return Regex.Replace(text, @"\s+([!?,])", "$1").Replace("  ", " ").Trim();
    }

    private static string Title(string text)
        => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

    /// <summary>The school's name as players say it.</summary>
    public static string SchoolName(AmbientSchool school) => school switch {
        AmbientSchool.Fire => "Fire", AmbientSchool.Ice => "Ice", AmbientSchool.Storm => "Storm", AmbientSchool.Myth => "Myth",
        AmbientSchool.Life => "Life", AmbientSchool.Death => "Death", _ => "Balance",
    };

    /// <summary>A hash that is the same in every run (string.GetHashCode is not).</summary>
    internal static int StableHash(string text) {
        var hash = 17;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToLowerInvariant())) {
            hash = unchecked(hash * 31 + b);
        }

        return hash & 0x7FFFFFFF;
    }

}

/// <summary>
/// Keeps one ambient wizard from spamming: at least <see cref="MinGap"/> between lines, at most
/// <see cref="PerMinute"/> lines a minute, and one reply per speaker per <see cref="PerSpeakerGap"/>.
/// </summary>
public sealed class AmbientChatLimiter {

    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan PerSpeakerGap = TimeSpan.FromSeconds(4);
    public const int PerMinute = 5;

    private readonly Queue<DateTime> _recent = new();
    private readonly Dictionary<ulong, DateTime> _bySpeaker = [];

    /// <summary>True (and counted) when a line may go out now; <paramref name="speaker"/> 0 for unprompted lines.</summary>
    public bool TryTake(DateTime now, ulong speaker = 0) {
        while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1)) {
            _recent.Dequeue();
        }

        if (_recent.Count >= PerMinute || (_recent.Count > 0 && now - _recent.Last() < MinGap)) {
            return false;
        }

        if (speaker != 0 && _bySpeaker.TryGetValue(speaker, out var last) && now - last < PerSpeakerGap) {
            return false;
        }

        _recent.Enqueue(now);
        if (speaker != 0) {
            _bySpeaker[speaker] = now;
            if (_bySpeaker.Count > 200) {
                _bySpeaker.Clear();
            }
        }

        return true;
    }

}
