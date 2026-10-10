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
 * CHAT PERSONA, STYLE AND TIMING
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner, 2026-10-04: "make the friendly wizards feel more natural
 * with their language and timing"): each ambient wizard types like one
 * 2009 player, the same way every time.
 *   - Persona: grown-up (a parent playing with a kid, about one in five)
 *     or kid/teen; its chat level as in 2009 (menu chat only, the
 *     dictionary text chat of 13+ subscribers, or the 18+ open chat of
 *     June 2009, the only one where numbers were shown); spelling (neat,
 *     casual, sloppy, excited); typing speed (kids 2-4.5 characters a
 *     second, grown-ups 4.5-7); its laugh and smiley; how much it talks.
 *   - Style: a template is written once in plain casual form ("i'm
 *     gonna...") and each persona types it its own way: Neat capitalises
 *     and ends with a full stop; Sloppy drops apostrophes and commas and
 *     uses lol/plz/ty/wanna/idk; Excited doubles "!" and adds ":D". Every
 *     swap is a word the client's chat dictionary has (r806919 lists,
 *     checked 2026-10-04: "u", "r", "ur", "omg", "kk", "lag" and nearly all
 *     misspellings are NOT in it, so the wizards never use them). A styled
 *     line the filter refuses falls back to the plain template.
 *   - CLASSIC (2026-10-10, owner: "rework the chat completely of the
 *     friendly wizards so its more natural"): a temperament (Kind: shy,
 *     chatty, bossy, show-off, new player, parent) that sets what it
 *     talks about and how it answers (AmbientChatBrain.Plan), its own
 *     laugh for every "lol" and its own yes word; shy ones trail off
 *     ("...", "um"), bossy ones drop smileys, show-offs add a laugh;
 *     neat typists end questions with "?"; kids drop a leading "i" and
 *     type "your" for "you're". Number words from two to twenty, "bed",
 *     "cant" and "ugly" are not in the dictionary either.
 *   - Timing: a reply waits for reading the line, a thinking pause and the
 *     typing (length / speed), 1.5 to 25 seconds; quick words ("lol",
 *     "ty") come fast.
 *
 * USAGE EXAMPLE:
 * var persona = ChatPersona.For(identity);
 * var line = ChatStyle.Apply("i'm gonna go hunt some ghosts", persona, rng);
 * var wait = ChatTiming.Typing(line, persona, rng);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Imlight.Classic.Ambient;

/// <summary>The 2009 chat levels: menu phrases, dictionary words, or 18+ open chat.</summary>
public enum ChatChannel { Menu, Dictionary, Open }

/// <summary>How a wizard spells.</summary>
public enum ChatSpelling { Neat, Casual, Sloppy, Excited }

/// <summary>
/// CLASSIC (2026-10-10, owner: "rework the chat completely of the friendly wizards so its more natural"): the kind of
/// player a wizard is, which sets what it talks about and how it answers. Shy ones say little and miss lines; chatty ones
/// talk about anything and wander off topic; bossy ones tell people what to do and answer short; show-offs brag; new
/// players ask; parents talk about their kids and get lost.
/// </summary>
public enum ChatTemperament { Shy, Chatty, Bossy, ShowOff, Newbie, Parent }

/// <summary>How one ambient wizard chats (see the file header).</summary>
/// <param name="Seed">The identity's seed.</param>
/// <param name="Temper">How much it talks.</param>
/// <param name="Grownup">A parent or other adult rather than a kid or teen.</param>
/// <param name="Channel">Its chat level.</param>
/// <param name="Spelling">Its spelling.</param>
/// <param name="CharsPerSecond">Typing speed.</param>
/// <param name="ThinkSeconds">The usual pause before it starts typing.</param>
/// <param name="Laugh">"lol", "haha", "hehe", "lolz", "heh" or "xd".</param>
/// <param name="Smiley">":)", ":D", ":P" or null.</param>
/// <param name="Kind">Its temperament (CLASSIC 2026-10-10).</param>
/// <param name="YesWord">How it types a yes ("ya", "yea", "yeah", "yep", "yup", "ok").</param>
public sealed record ChatPersona(int Seed, AmbientTemper Temper, bool Grownup, ChatChannel Channel, ChatSpelling Spelling,
                                 double CharsPerSecond, double ThinkSeconds, string Laugh, string? Smiley,
                                 ChatTemperament Kind = ChatTemperament.Chatty, string YesWord = "yeah") {

    /// <summary>True when the wizard may type a level as a number (open chat; see ChatWordFilter).</summary>
    public bool Numbers => Channel == ChatChannel.Open;

    /// <summary>The persona of the wizard with <paramref name="identity"/>; the same identity gives the same persona.</summary>
    public static ChatPersona For(AmbientIdentity identity) {
        ArgumentNullException.ThrowIfNull(identity);
        return For(identity.Seed, identity.Temper, identity.Level);
    }

    /// <summary>The persona for a seed and temper (<paramref name="level"/> 0: not known).</summary>
    public static ChatPersona For(int seed, AmbientTemper temper, int level = 0) {
        var rng = new Random(unchecked(seed * 7919 + 0x5EED));
        var grownup = rng.NextDouble() < 0.2;
        ChatChannel channel;
        ChatSpelling spelling;
        double speed;
        if (grownup) {
            channel = rng.NextDouble() < 0.6 ? ChatChannel.Open : ChatChannel.Dictionary;
            spelling = rng.NextDouble() < 0.6 ? ChatSpelling.Neat : ChatSpelling.Casual;
            speed = 4.5 + rng.NextDouble() * 2.5;
        }
        else {
            // Menu chat was all an under-13 or free player had; most chatty kids typed in the dictionary.
            var roll = rng.NextDouble();
            channel = roll < (temper == AmbientTemper.Quiet ? 0.45 : 0.25) ? ChatChannel.Menu : ChatChannel.Dictionary;
            roll = rng.NextDouble();
            spelling = roll < 0.1 ? ChatSpelling.Neat : roll < 0.45 ? ChatSpelling.Casual : roll < 0.8 ? ChatSpelling.Sloppy : ChatSpelling.Excited;
            speed = 2.2 + rng.NextDouble() * 2.3;
        }

        string[] laughs = ["lol", "lol", "lol", "haha", "hehe", "lolz", "heh", "xd"];
        string?[] smileys = [":)", ":)", ":D", ":P", null, null];
        var think = 0.8 + rng.NextDouble() * 1.6;
        var laugh = laughs[rng.Next(laughs.Length)];
        if (spelling == ChatSpelling.Neat && laugh is "xd" or "lolz") {
            laugh = "haha"; // a neat typist does not type "Xd"
        }

        var smiley = smileys[rng.Next(smileys.Length)];

        // CLASSIC (2026-10-10): temperament and habits, drawn after the older draws so a wizard keeps its old voice.
        var kind = grownup ? ChatTemperament.Parent : Temperament(temper, level, rng.NextDouble(), rng.NextDouble());
        string[] yes = grownup ? ["yes", "sure", "yeah", "ok"] : ["ya", "yea", "yeah", "yeah", "yep", "yup", "ok"];
        var yesWord = yes[rng.Next(yes.Length)];
        if (kind == ChatTemperament.Shy) {
            smiley = rng.NextDouble() < 0.5 ? smiley : null;
            think += 0.8; // shy ones wait before they type
        }

        return new ChatPersona(seed, temper, grownup, channel, spelling, Math.Round(speed, 2), think, laugh, smiley, kind, yesWord);
    }

    /// <summary>A kid's temperament from its temper, its level (new players are low) and two rolls in [0, 1).</summary>
    public static ChatTemperament Temperament(AmbientTemper temper, int level, double newRoll, double roll) {
        if (level is > 0 and <= 5 && newRoll < 0.55) {
            return ChatTemperament.Newbie;
        }

        return temper switch {
            AmbientTemper.Quiet => roll < 0.7 ? ChatTemperament.Shy : roll < 0.85 ? ChatTemperament.Chatty : ChatTemperament.ShowOff,
            AmbientTemper.Chatty => roll < 0.45 ? ChatTemperament.Chatty : roll < 0.7 ? ChatTemperament.ShowOff : ChatTemperament.Bossy,
            _ => roll < 0.35 ? ChatTemperament.Chatty : roll < 0.6 ? ChatTemperament.Shy : roll < 0.8 ? ChatTemperament.ShowOff : ChatTemperament.Bossy,
        };
    }

}

/// <summary>Types a template the persona's way (see the file header).</summary>
public static class ChatStyle {

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Swaps a sloppy typist makes, every result a dictionary word (checked against r806919's WhiteListBase; CLASSIC
    // 2026-10-10: "your" for "you're" and "its" for "it's" are the misspellings kids really made and the filter shows).
    private static readonly (Regex From, string[] To)[] s_swaps = [
        (new(@"\bi don'?t know\b", Options), ["idk", "dunno"]),
        (new(@"\bdon'?t know\b", Options), ["dunno"]),
        (new(@"\bwant to\b", Options), ["wanna"]),
        (new(@"\bgoing to\b", Options), ["gonna"]),
        (new(@"\bgot to go\b", Options), ["gtg"]),
        (new(@"\bgot to\b", Options), ["gotta"]),
        (new(@"\bkind of\b", Options), ["kinda"]),
        (new(@"\bbe right back\b", Options), ["brb"]),
        (new(@"\btalk to you later\b", Options), ["ttyl"]),
        (new(@"\bno problem\b", Options), ["np"]),
        (new(@"\bnever mind\b", Options), ["nvm"]),
        (new(@"\bthank you\b", Options), ["ty", "thx"]),
        (new(@"\bthanks\b", Options), ["thx", "ty", "thanx"]),
        (new(@"\bplease\b", Options), ["plz", "pls"]),
        (new(@"\bokay\b", Options), ["ok", "k"]),
        (new(@"\bprobably\b", Options), ["prob", "prolly"]),
        (new(@"\bbecause\b", Options), ["cuz", "cause"]),
        (new(@"\blet me\b", Options), ["lemme"]),
        (new(@"\bseriously\b", Options), ["srsly"]),
        (new(@"\blevel\b", Options), ["lvl"]),
        (new(@"\bwizard\b", Options), ["wiz"]),
        (new(@"\byes\b", Options), ["yeah", "yep", "ya"]),
        (new(@"\bcool\b", Options), ["cool", "kewl"]),
        (new(@"\btheir\b", Options), ["thier"]),
        (new(@"\byou're\b", Options), ["your"]),
        (new(@"\bsorry\b", Options), ["sry"]),
        (new(@"\bby the way\b", Options), ["btw"]),
        (new(@"\bjust kidding\b", Options), ["jk"]),
    ];

    // Drawn-out words, each a dictionary word ("soooo", "nooo", "awww" are in the list; "sooo", "hiii" are not).
    private static readonly (Regex From, string[] To)[] s_stretch = [
        (new(@"\bso\b", Options), ["soooo"]),
        (new(@"\bno\b", Options), ["nooo", "noooo"]),
        (new(@"\baw\b", Options), ["aww", "awww"]),
    ];

    private static readonly Regex s_apostrophe = new(@"\b(i'm|don't|that's|what's|it's|let's|i'll|didn't|doesn't|isn't|won't|wasn't|aren't)\b", Options);

    private static readonly Regex s_lol = new(@"\blol\b", Options);

    private static readonly Regex s_leadingYes = new(@"^(yeah|ya|yea|yep|yup)\b", Options);

    /// <summary>
    /// Every word a style can put into a line that the template did not have (swaps, stretched words, laughs, yes words,
    /// fillers), for the vocabulary tests: each must be a client dictionary word.
    /// </summary>
    public static IReadOnlyCollection<string> StyleWords { get; } = new SortedSet<string>(StringComparer.Ordinal) {
        "idk", "dunno", "wanna", "gonna", "gtg", "gotta", "kinda", "brb", "ttyl", "np", "nvm", "ty", "thx", "thanx", "plz", "pls",
        "ok", "k", "prob", "prolly", "cuz", "cause", "lemme", "srsly", "lvl", "wiz", "yeah", "yep", "ya", "cool", "kewl", "thier",
        "your", "sry", "btw", "jk", "soooo", "nooo", "noooo", "aww", "awww", "lol", "haha", "hehe", "lolz", "heh", "xd", "yea",
        "yup", "yes", "sure", "um", "dont", "thats", "whats", "its", "lets", "ill", "didnt", "doesnt", "isnt", "wont", "wasnt",
        "arent", "im", "i", "I",
    };

    /// <summary>
    /// <paramref name="template"/> (already filled) as the persona types it, or the template itself when the styled line
    /// would not pass <paramref name="filter"/>. Menu-chat personas get the template unchanged.
    /// </summary>
    public static string Apply(string template, ChatPersona persona, Random rng, ChatWordFilter? filter = null) {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(rng);
        if (persona.Channel == ChatChannel.Menu || template.Length == 0) {
            return template;
        }

        var line = persona.Spelling switch {
            ChatSpelling.Neat => Neat(template),
            ChatSpelling.Casual => Casual(template, rng),
            ChatSpelling.Sloppy => Sloppy(template, persona, rng),
            _ => Excited(template, persona, rng),
        };
        line = Habits(line, persona, rng);
        line = Regex.Replace(line, " {2,}", " ").Trim();
        if (line.Length == 0 || line.Length > 80 || !AmbientChatBrain.IsClean(line)) {
            return template;
        }

        return filter is null || filter.Passes(line, persona.Numbers) ? line : template;
    }

    // CLASSIC (2026-10-10): the same person's habits every time: its own laugh for "lol", its own yes, and its temperament.
    private static string Habits(string line, ChatPersona persona, Random rng) {
        if (persona.Spelling != ChatSpelling.Neat) {
            if (persona.Laugh != "lol") {
                line = s_lol.Replace(line, persona.Laugh);
            }

            line = s_leadingYes.Replace(line, persona.YesWord);
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        switch (persona.Kind) {
            case ChatTemperament.Shy:
                line = line.Replace("!!", "").TrimEnd('!');
                if (words >= 3 && rng.NextDouble() < 0.2 && !line.StartsWith("um", StringComparison.OrdinalIgnoreCase)) {
                    line = (persona.Spelling == ChatSpelling.Neat ? "Um, " + char.ToLowerInvariant(line[0]) + line[1..] : "um " + line);
                }
                else if (words >= 2 && rng.NextDouble() < 0.15 && !line.EndsWith('?') && !Regex.IsMatch(line, @"(:\)|:D|:P)$")) {
                    line = line.TrimEnd('.') + "...";
                }

                break;
            case ChatTemperament.Bossy:
                line = Regex.Replace(line, @"\s*(:\)|:D|:P)$", "");
                break;
            case ChatTemperament.ShowOff:
                if (persona.Spelling != ChatSpelling.Neat && words >= 3 && rng.NextDouble() < 0.12
                    && !line.Contains(persona.Laugh, StringComparison.Ordinal)) {
                    line += " " + persona.Laugh;
                }

                break;
        }

        return line;
    }

    // CLASSIC (2026-10-10): a line that asks something, for its "?" ("what school are you." read like a robot).
    private static readonly Regex s_question = new(
        @"^((ok|okay|k|so|um|and|but|hey|sure)\s+)?(what|whats|what's|where|wheres|where's|how|who|which|why|is|are|can|could|does|do|did|will|anyone|anybody|wanna|want|need|you|r)\b",
        Options);

    private static readonly HashSet<string> s_interjections = new(StringComparer.OrdinalIgnoreCase) {
        "lol", "lolz", "haha", "hehe", "heh", "xd", "rofl", "ok", "k", "gg", "ty", "thx", "np", "yw", "hmm", "um", "meh", "ugh", "brb",
        "afk", "gtg", "ya", "yea", "yep", "yup", "nah", "oh", "ooh", "aw", "aww", "awww", "nvm", "sup", "yo", "idk", "same",
    };

    /// <summary>True when <paramref name="line"/> reads as a question.</summary>
    public static bool Asks(string line) => s_question.IsMatch(line ?? "");

    private static string Neat(string text) {
        var line = Regex.Replace(text, @"\bi\b", "I");
        line = Regex.Replace(line, @"\bi'(m|ll|ve|d)\b", "I'$1");
        line = char.ToUpperInvariant(line[0]) + line[1..];
        var bare = line.TrimEnd('.', '!', '?');
        if (s_interjections.Contains(bare) || bare.Split(' ').All(w => s_interjections.Contains(w))) {
            return line; // "Lol", "Ok": nobody punctuates those
        }

        if (!".!?)".Contains(line[^1]) && !line.EndsWith(":D", StringComparison.Ordinal) && !line.EndsWith(":P", StringComparison.Ordinal)) {
            line += Asks(text) ? "?" : line.Split(' ').Length <= 2 ? "!" : ".";
        }

        return line;
    }

    private static string Casual(string text, Random rng) {
        var line = text.ToLowerInvariant();
        if (rng.NextDouble() < 0.5) {
            line = s_apostrophe.Replace(line, m => m.Value.Replace("'", ""));
        }

        if (line.EndsWith('.') && rng.NextDouble() < 0.7) {
            line = line[..^1];
        }

        return line;
    }

    private static string Sloppy(string text, ChatPersona persona, Random rng) {
        var line = s_apostrophe.Replace(text.ToLowerInvariant(), m => m.Value.Replace("'", ""));
        foreach (var (from, to) in s_swaps) {
            if (rng.NextDouble() < 0.6) {
                line = from.Replace(line, _ => to[rng.Next(to.Length)], 1);
            }
        }

        foreach (var (from, to) in s_stretch) {
            if (rng.NextDouble() < 0.15) {
                line = from.Replace(line, _ => to[rng.Next(to.Length)], 1);
            }
        }

        line = line.Replace(",", "").TrimEnd('.');
        if (rng.NextDouble() < 0.4) {
            line = line.TrimEnd('!', '?').TrimEnd();
        }

        // Kids dropped the "i" at the start: "need gold", "got a new hat".
        if (rng.NextDouble() < 0.2 && Regex.IsMatch(line, @"^i (need|got|have|want|keep|just|finally|cant|can't|wish|lost|beat|hate|love)\b")) {
            line = line[2..];
        }

        if (rng.NextDouble() < 0.12 && !line.Contains(persona.Laugh, StringComparison.Ordinal) && line.Length < 50) {
            line += " " + persona.Laugh;
        }

        return line;
    }

    private static string Excited(string text, ChatPersona persona, Random rng) {
        var line = text.ToLowerInvariant();
        if (rng.NextDouble() < 0.3) {
            line = s_apostrophe.Replace(line, m => m.Value.Replace("'", ""));
        }

        line = line.TrimEnd('.');
        if (line.EndsWith('!')) {
            line += rng.NextDouble() < 0.5 ? "!" : "!!";
        }
        else if (Asks(line) && !line.EndsWith('?')) {
            line += rng.NextDouble() < 0.5 ? "?" : "??";
        }
        else if (!line.EndsWith('?') && rng.NextDouble() < 0.5 && !s_interjections.Contains(line)) {
            line += "!!";
        }

        foreach (var (from, to) in s_stretch) {
            if (rng.NextDouble() < 0.3) {
                line = from.Replace(line, _ => to[rng.Next(to.Length)], 1);
            }
        }

        if (persona.Smiley is { } smiley && rng.NextDouble() < 0.3 && !line.Contains(':')) {
            line += " " + smiley;
        }

        return line;
    }

}

/// <summary>How long a wizard takes to answer (see the file header).</summary>
public static class ChatTiming {

    /// <summary>Lines a person fires off without thinking.</summary>
    private static readonly HashSet<string> s_quick = new(StringComparer.OrdinalIgnoreCase) {
        "lol", "lolz", "haha", "hehe", "heh", "ty", "thx", "np", "k", "ok", "yep", "yeah", "ya", "brb", "gg", "hi", "hey", "yay",
        "kk", "back", "nope", "sure", "wow", "cool", ":)", ":D", ":P", "hmm",
    };

    /// <summary>The time to read <paramref name="heard"/> (a line someone said), from when it arrived.</summary>
    public static TimeSpan Reading(string? heard, Random rng) {
        ArgumentNullException.ThrowIfNull(rng);
        var length = heard?.Length ?? 0;
        return TimeSpan.FromSeconds(0.4 + length / 30.0 + rng.NextDouble() * 0.8);
    }

    /// <summary>The thinking pause and typing time for <paramref name="line"/>, 1.5 to 25 seconds.</summary>
    public static TimeSpan Typing(string line, ChatPersona persona, Random rng) {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(rng);
        var quick = s_quick.Contains(line.Trim().TrimEnd('!', '?', '.'));
        var think = quick ? 0.3 + rng.NextDouble() * 0.7 : persona.ThinkSeconds * (0.6 + rng.NextDouble());
        var typing = line.Length / persona.CharsPerSecond * (0.85 + rng.NextDouble() * 0.4);
        if (persona.Channel == ChatChannel.Menu) {
            typing = 1.2 + rng.NextDouble() * 1.5; // a few clicks through the menu
        }

        return TimeSpan.FromSeconds(Math.Clamp(think + typing, 1.5, 25));
    }

    /// <summary>
    /// The whole wait before an answer to <paramref name="heard"/>: noticing and reading it (1 to 3 s), thinking, and
    /// typing <paramref name="reply"/> at the persona's speed, all with jitter. A wizard in a fight (<paramref name="busy"/>)
    /// is picking cards and answers 1.5 to 2.5 times slower.
    /// </summary>
    public static TimeSpan Answer(string? heard, string reply, ChatPersona persona, bool busy, Random rng) {
        ArgumentNullException.ThrowIfNull(rng);
        var notice = TimeSpan.FromSeconds(1 + rng.NextDouble() * 2) + Reading(heard, rng) - TimeSpan.FromSeconds(0.4);
        var wait = notice + Typing(reply, persona, rng);
        return busy ? TimeSpan.FromSeconds(wait.TotalSeconds * (1.5 + rng.NextDouble())) : wait;
    }

    /// <summary>
    /// When an answer planned for <paramref name="due"/> goes out so that no two wizards of a zone answer within
    /// <see cref="ApartAtLeast"/> of each other; <paramref name="lastAnswer"/> is the zone's latest planned answer.
    /// </summary>
    public static DateTime Stagger(DateTime due, DateTime lastAnswer, Random rng) {
        ArgumentNullException.ThrowIfNull(rng);
        var earliest = lastAnswer + ApartAtLeast + TimeSpan.FromSeconds(rng.NextDouble() * 1.5);
        return due < earliest ? earliest : due;
    }

    /// <summary>The least time between two ambient answers in one zone.</summary>
    public static readonly TimeSpan ApartAtLeast = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when a wizard lets a Say to it go unanswered this time (people miss lines: 12%, a quiet one 25%). Whispers
    /// and yes/no answers to its own offer are always answered.
    /// </summary>
    public static bool Ignores(ChatPersona persona, Random rng) {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(rng);
        return rng.NextDouble() < (persona.Temper == AmbientTemper.Quiet ? 0.25 : 0.12);
    }

    /// <summary>
    /// How long a menu phrase waits before an ambient wizard acts on it (owner, 2026-10-05: wizards answered "before i
    /// actually hit enter"; the client sends menu phrases on its own as well as when picked).
    /// </summary>
    public static readonly TimeSpan MenuSettle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True when a menu phrase heard at <paramref name="heardAt"/> still counts: no typed line from the same player came
    /// from 1.5 seconds before it up to when it settles (<paramref name="typedAt"/> is that player's last typed line).
    /// </summary>
    public static bool MenuPhraseStands(DateTime heardAt, DateTime? typedAt)
        => typedAt is not { } typed || typed < heardAt - TimeSpan.FromSeconds(1.5);

    /// <summary>A short pause between two lines one wizard sends in a row (a second thought, a correction).</summary>
    public static TimeSpan FollowUp(string line, ChatPersona persona, Random rng)
        => TimeSpan.FromSeconds(Math.Clamp(0.5 + line.Length / persona.CharsPerSecond * (0.9 + rng.NextDouble() * 0.3), 1.2, 15));

}
