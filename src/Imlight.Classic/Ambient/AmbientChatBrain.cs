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
 * The lines live in AmbientLines, which also skips any template the
 * listeners heard in their last twenty (2026-10-03).
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
/// <param name="ZoneKey">The zone's internal name (WizardCity/WC_Hub), for zone tips.</param>
/// <param name="Hour">The server's local hour (0..23) for "good morning", or -1.</param>
/// <param name="History">What the listeners heard lately (AmbientLines), or null for no memory.</param>
/// <param name="Audience">The players who will hear the line.</param>
public sealed record ChatContext(string MyName, AmbientSchool School, int Level, string ZoneName,
                                 string? SpeakerName = null, string? SpeakerZone = null, string? SpeakerQuest = null,
                                 FriendMemory? Friend = null, Func<string, string?>? WhereIs = null,
                                 DateTime Now = default, string? ZoneKey = null, int Hour = -1,
                                 LineHistory? History = null, IReadOnlyList<ulong>? Audience = null) {

    public string MyFirstName => MyName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? MyName;

    public string? SpeakerFirstName => SpeakerName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

}

/// <summary>
/// Pattern and template replies (see the file header).
/// </summary>
public static class AmbientChatBrain {

    /// <summary>What a line asks for: the reply pool (templates) and any extra slots.</summary>
    /// <param name="Name">The rule's name ("level", "greet", ...).</param>
    /// <param name="Pool">Reply templates.</param>
    /// <param name="Extra">Extra slot values.</param>
    /// <param name="Menu">The menu-chat phrases that fit, for a wizard that only has menu chat.</param>
    public sealed record ChatIntent(string Name, IReadOnlyList<string> Pool, IReadOnlyDictionary<string, string>? Extra = null,
                                    IReadOnlyList<string>? Menu = null);

    private sealed record Rule(string Name, Regex Pattern, Func<Match, ChatContext, ChatPersona?, ChatIntent?> Intent);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly string[] s_menuYes = ["Sure!", "Okay!", "Yes!"];

    private static ChatIntent Pool(string name, IReadOnlyList<string> pool, IReadOnlyList<string>? menu = null) => new(name, pool, null, menu);

    // CLASSIC (2026-10-04): the first rule that matches answers. Order matters: "where is" before "what", thanks before help.
    private static readonly Rule[] s_rules = [
        new("bot", new(@"\b(are|r)\s+(you|u)\s+(a\s+)?(bot|robot|npc|real|computer|fake)\b|\bnpc\b", Options),
            (_, _, _) => Pool("bot", AmbientLinePool.NotABot, ["What?"])),
        new("age", new(@"\bhow old\b|\bwhat grade\b|\bur age\b|\byour age\b|\bage\s*\?", Options),
            (_, _, _) => Pool("age", AmbientLinePool.AgeAnswer, ["Sorry!"])),
        new("level", new(@"\b(what|wat|wut)\s*(level|lvl|lv)\b|\b(level|lvl)\s*\?|\bhow high\b|\bur (level|lvl)\b|\byour (level|lvl)\b", Options),
            (_, _, p) => Pool("level", p is null || p.Numbers ? AmbientLinePool.LevelNumber : AmbientLinePool.LevelNoNumber, ["I don't know."])),
        new("school", new(@"\b(what|wat|which)\s+school\b|\bur school\b|\byour school\b", Options),
            (_, _, _) => Pool("school", AmbientLinePool.SchoolAnswer)),
        new("name", new(@"\bwhat'?s\s+(your|ur)\s+name\b|\bwhat is your name\b|\bwho are (you|u)\b", Options),
            (_, _, _) => Pool("name", AmbientLinePool.NameAnswer)),
        new("where", new(@"\bwhere\s+(is|are|r|can i find|do i find)\s+(?:the\s+)?(?<what>[a-z' ]{3,40})", Options),
            (m, c, _) => WhereIntent(m.Groups["what"].Value, c)),
        new("thanks", new(@"\b(thanks|thank you|thx|ty|tyvm|thanx)\b", Options),
            (_, _, _) => Pool("thanks", AmbientLinePool.Thanks, ["You're welcome!"])),
        new("compliment", new(@"\b(nice|cool|love|like|awesome|pretty)\s+(your\s+|ur\s+)?(hat|robe|wand|outfit|clothes|pet|name|boots|mount|broom|deck)\b", Options),
            (_, _, _) => Pool("compliment", AmbientLinePool.ComplimentThanks, ["Thank you!"])),
        new("gold", new(@"\b(give|gimme|spare|can i have|need)\b.*\bgold\b|\bfree gold\b", Options),
            (_, _, _) => Pool("gold", AmbientLinePool.GoldBeg, ["Sorry!"])),
        new("howgold", new(@"\bhow\s+(do|can)\s+(i|you|u)\s+(get|make)\s+(more\s+)?gold\b", Options),
            (_, _, _) => Pool("howgold", AmbientLinePool.HowToGold)),
        new("hatch", new(@"\bhatch", Options),
            (_, c, _) => (c.ZoneKey ?? "").Contains("Hatchery", StringComparison.OrdinalIgnoreCase)
                         || (c.ZoneKey ?? "").Contains("PET_Park", StringComparison.OrdinalIgnoreCase)
                ? Pool("hatch", ["sure what pet", "ok whats your pet", "maybe my pet isnt an adult yet", "sure"], s_menuYes)
                : Pool("hatch", AmbientLinePool.HowToHatch)),
        new("teleport", new(@"\b(teleport|port|tp)\b", Options),
            (_, _, _) => Pool("teleport", AmbientLinePool.Teleport)),
        new("trade", new(@"\btrade\b", Options),
            (_, _, _) => Pool("trade", AmbientLinePool.Trade)),
        new("duel", new(@"\b(duel|pvp|fight me|1v1|arena)\b", Options),
            (_, _, _) => Pool("duel", AmbientLinePool.Duel, s_menuYes)),
        new("help", new(@"\b(can|could|will)\s+(you|u|someone|anyone|any1)\s+help\b|\bneed\s+help\b|\bhelp\s+me\b|\bhelp\s+(pls|plz|please)\b|\bhelp\s+with\b|\b(wanna|want to)\s+help\b", Options),
            (_, _, _) => Pool("help", AmbientLinePool.HelpSure, s_menuYes)),
        new("friend", new(@"\b(friend me|add me|be my friend|be friends|friend request|wanna be friends|want to be friends)\b", Options),
            (_, _, _) => Pool("friend", AmbientLinePool.Friend, ["Sure!", "Okay!"])),
        new("quest", new(@"\b(anyone|anybody|who|wanna|want to|lets|let's)\b.*\b(quest|questing|team up|group|lfg)\b|\blfg\b", Options),
            (_, _, _) => Pool("quest", AmbientLinePool.QuestTogether, s_menuYes)),
        new("come", new(@"\b(follow me|come here|come with me|come on|lets go|let's go)\b", Options),
            (_, _, _) => Pool("come", AmbientLinePool.Come, ["Okay!", "Let's go!"])),
        new("bye", new(@"\b(bye|cya|see ya|gtg|got to go|g2g|later|ttyl|good night|goodnight)\b", Options),
            (_, _, _) => Pool("bye", AmbientLinePool.Bye, ["Goodbye!", "See you later!"])),
        new("how", new(@"\bhow\s+(are|r)\s+(you|u)\b|\bhow'?s it going\b|\bsup\b|\bwhat'?s up\b|\bwassup\b", Options),
            (_, _, _) => Pool("how", AmbientLinePool.HowAreYou, ["Good!"])),
        new("doing", new(@"\b(what|which)\s+quest\b|\bwhat are (you|u) doing\b|\bwhat r u doing\b|\bwhatcha doing\b|\bwyd\b", Options),
            (_, _, _) => Pool("doing", AmbientLinePool.Doing)),
        new("greet", new(@"^\s*(hi+|hello|hey+|hiya|yo|heya|howdy|greetings|sup)\b", Options),
            (_, c, _) => Pool("greet", c.Friend is not null ? AmbientLines.FriendGreetings : AmbientLinePool.Greet, ["Hi!", "Hello!"])),
        new("laugh", new(@"^\s*(lol+|lolz|haha+|hehe+|rofl|xd)\s*!*$", Options),
            (_, _, _) => Pool("laugh", AmbientLinePool.Laugh)),
        new("agree", new(@"^\s*(yes|yeah|ya|yep|ok|okay|k|sure|true|cool|nice)\s*!*$", Options),
            (_, _, _) => Pool("agree", AmbientLinePool.Agree)),
    ];

    private static readonly Regex s_openCall = new(
        @"\b(anyone|anybody|any1|everyone|everybody|someone|somebody|who wants|who wanna|lfg|lfm|guys|all)\b|^\s*(hi+|hello|hey+|hiya|heya)\s*!*$",
        Options);

    /// <summary>True for a Say to everyone around ("anyone wanna help", "hi all", a bare "hello").</summary>
    public static bool IsOpenCall(string? text) => !string.IsNullOrWhiteSpace(text) && s_openCall.IsMatch(text);

    /// <summary>True when <paramref name="text"/> uses <paramref name="firstName"/>.</summary>
    public static bool Mentions(string? text, string firstName)
        => !string.IsNullOrWhiteSpace(text) && firstName.Length > 0
           && Regex.IsMatch(text, $@"\b{Regex.Escape(firstName)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// What <paramref name="text"/> asks for, or null when no rule fits (then only a line to the wizard by name gets a
    /// fallback). <paramref name="persona"/> null answers levels with numbers.
    /// </summary>
    public static ChatIntent? Understand(string? text, ChatContext context, ChatPersona? persona = null) {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        foreach (var rule in s_rules) {
            var match = rule.Pattern.Match(text);
            if (match.Success && rule.Intent(match, context, persona) is { } intent) {
                return intent;
            }
        }

        return null;
    }

    /// <summary>The fallback intent: a short "lol" or "hmm" for a line to the wizard that no rule understood.</summary>
    public static ChatIntent Fallback { get; } = new("fallback", AmbientLinePool.Fallback, null, ["Okay!", "Cool!"]);

    /// <summary>
    /// The reply to <paramref name="text"/>, or null to stay quiet. A Say that does not use the wizard's first name gets
    /// no reply; a whisper (<paramref name="direct"/>) always gets one. (The zone's AmbientChatter also answers open
    /// calls and players it is already talking with, typed in the wizard's own style.)
    /// </summary>
    /// <param name="text">What the player said.</param>
    /// <param name="context">The wizard and the speaker.</param>
    /// <param name="direct">A whisper (Text), not a Say.</param>
    /// <param name="turn">A counter the caller increments per reply, to vary the templates.</param>
    public static string? Reply(string? text, ChatContext context, bool direct, int turn) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        if (!direct && !Mentions(text, context.MyFirstName)) {
            return null;
        }

        var seed = turn + StableHash(text);
        var intent = Understand(text, context) ?? Fallback;
        var me = new Dictionary<string, string>(intent.Extra ?? new Dictionary<string, string>()) { ["me"] = context.MyName };
        return AmbientLines.Choose(intent.Pool, seed, context, me) ?? AmbientLines.Choose(AmbientLinePool.Fallback, seed, context);
    }

    /// <summary>
    /// A line an idle wizard says on its own now and then (AmbientLines.Idle), or null when the listeners heard every
    /// fitting line lately.
    /// </summary>
    public static string? Idle(ChatContext context, int turn)
        => AmbientLines.Choose(AmbientLines.Idle(context), turn, context, quietIfHeard: true);

    /// <summary>What a wizard says after a battle won with a player.</summary>
    public static string? AfterWin(ChatContext context, int turn)
        => AmbientLines.Choose(AmbientLines.AfterWin, turn, context, quietIfHeard: true);

    /// <summary>A greeting for a friend who just showed up, now and then recalling last time.</summary>
    public static string? GreetFriend(ChatContext context, int turn) {
        if (context.Friend is not { } friend) {
            return null;
        }

        var first = friend.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? friend.Name;
        var recall = Recall(friend, context, turn);
        return recall ?? AmbientLines.Choose(AmbientLines.FriendGreetings, turn, context);
    }

    /// <summary>A remark on the friend's zone or quest, as a buddy would make on seeing them.</summary>
    public static string? AboutFriend(ChatContext context, int turn) {
        if (context.Friend is null) {
            return null;
        }

        if (!string.IsNullOrEmpty(context.SpeakerQuest) && turn % 2 == 0) {
            return AmbientLines.Choose(["how's {quest} going?", "still on {quest}?", "need help with {quest}?", "{quest} is a fun one",
                "almost done with {quest}?"], turn, context, new Dictionary<string, string> { ["quest"] = context.SpeakerQuest });
        }

        if (!string.IsNullOrEmpty(context.SpeakerZone)) {
            return AmbientLines.Choose(["you're in {where}? cool", "how is {where}?", "i'll be in {where} later", "ooh, {where}!",
                "say hi to {where} for me lol"], turn, context, new Dictionary<string, string> { ["where"] = context.SpeakerZone });
        }

        return null;
    }

    /// <summary>"Need a hand?" and friends: the help offer before joining a battle.</summary>
    public static string HelpOffer(ChatContext context, int turn)
        => (context.Friend is not null ? AmbientLines.Choose(AmbientLines.HelpOfferFriend, turn, context) : null)
           ?? AmbientLines.Choose(AmbientLines.HelpOffer, turn, context) ?? "need a hand?";

    /// <summary>The reply to a yes (joining) or no.</summary>
    public static string HelpAnswered(bool yes, int turn, ChatContext? context = null) {
        context ??= new ChatContext("", AmbientSchool.Balance, 1, "");
        return AmbientLines.Choose(yes ? AmbientLines.Joining : AmbientLines.NotJoining, turn, context) ?? "ok!";
    }

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

    private static ChatIntent? WhereIntent(string what, ChatContext context) {
        var subject = what.Trim().TrimEnd('?', '.', '!');
        if (subject.Length == 0) {
            return null;
        }

        if (context.WhereIs?.Invoke(subject) is { Length: > 0 } where) {
            return new ChatIntent("where", AmbientLines.WhereKnown,
                new Dictionary<string, string> { ["what"] = Title(subject), ["where"] = where }, ["I don't know."]);
        }

        return new ChatIntent("where", AmbientLines.WhereUnknown, null, ["I don't know."]);
    }

    private static string? Clean(string line) => IsClean(line) ? line : null;

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
