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
 *
 * LineHistory remembers the last HeardWindow templates each player heard
 * from any ambient wizard in the zone; a pick skips those.
 *
 * USAGE EXAMPLE:
 * var line = AmbientLines.Choose(AmbientLines.Idle(context), turn, context);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
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

    // ---- talk to a player -----------------------------------------------------------------------

    public static readonly string[] Greetings = [
        "hi {name}!", "hey {name}", "hello {name}!", "hiya {name}", "oh hi {name}!", "hey there {name}!",
        "hi! :)", "heya {name}", "hello!", "hi {name}, nice hat", "hey {name}, whats up?", "good {time} {name}!",
        "oh hey, hi!", "hi {name}! welcome to {zone}", "howdy {name}",
    ];

    public static readonly string[] FriendGreetings = [
        "{name}! good to see you again", "hey {name}! :)", "hi {name}! whats up?", "{name}!! hi!",
        "there you are {name}!", "hey {name}, buddy!", "hi {name}! ready to quest?", "yay {name} is here!",
        "{name}! i was just thinking about you", "oh hi {name}, long time no see", "good {time} {name}!",
        "hey {name}, want to team up?",
    ];

    public static readonly string[] Thanks = [
        "np!", "no problem", "anytime!", "you're welcome", "sure thing", "glad to help!", "no prob :)", "any time {name}",
    ];

    public static readonly string[] Bye = [
        "bye!", "cya later", "see you around!", "bye {name}!", "take care!", "good luck out there!", "cya {name}!",
        "have fun!", "see you in the Spiral!", "bye bye",
    ];

    public static readonly string[] HowAreYou = [
        "good! just questing", "pretty good, you?", "great! leveling up", "tired lol, been fighting all day",
        "good, just got a new hat", "awesome! almost level {next}", "good! {zone} is busy today", "not bad, you?",
        "great, i finally finished a hard quest", "pretty good! just practicing my {school} spells",
        "good! saving up gold", "happy {time}! i'm good",
    ];

    public static readonly string[] Fallback = [
        "lol", "cool", "hmm idk", "oh nice", "haha", "yeah", "i see", "really?", "wow", "nice!", "oh ok",
        "same here", "hehe", "true", "maybe!", "ooh", "neat",
    ];

    public static readonly string[] Level = [
        "i'm level {level}", "level {level}!", "{level}, almost {next}", "level {level}, you?", "just hit {level}!",
        "{level}! slowly getting there", "level {level}, trying for {next}",
    ];

    public static readonly string[] School = [
        "{school}! best school", "i'm a {school} wizard", "{school} :)", "{school}, of course!", "{school}! you?",
        "{school} all the way", "i picked {school} on my first day",
    ];

    public static readonly string[] Duel = [
        "maybe later, i'm questing", "sure, ask me for a practice match at the arena", "lol i'd lose",
        "after this quest!", "hmm not right now", "i need more practice first", "only a friendly match :)",
    ];

    public static readonly string[] Help = [
        "sure, where are you?", "i can help! start a fight and i'll ask to join", "ok! what do you need?",
        "sure! fight something and i'll come", "yep, just say yes when i ask", "of course!",
    ];

    public static readonly string[] Friend = [
        "sure! send me a request", "ok! add me :)", "yeah sure", "yes! friends!", "ok, i'll accept", "sure, add me",
    ];

    public static readonly string[] Doing = [
        "just {school} stuff", "helping out in {zone}", "hunting in {zone}", "working on my {school} spells",
        "trying to level up", "looking for people to quest with", "just walking around", "waiting for a friend",
        "saving up gold for gear", "doing my quests", "exploring {zone}",
    ];

    public static readonly string[] WhereUnknown = [
        "hmm not sure, ask an npc", "idk, sorry", "i forget where that is", "maybe check your map?",
        "not sure, sorry!", "try asking in the Commons",
    ];

    public static readonly string[] WhereKnown = ["{what} is in {where}", "try {where}", "{where}, i think", "i think it's in {where}"];

    // ---- battles --------------------------------------------------------------------------------

    public static readonly string[] HelpOffer = [
        "need a hand?", "want some help?", "need help with that fight?", "want me to jump in?", "need a hand there?",
        "i can help if you want", "want a {school} wizard on your team?", "need backup?",
    ];

    public static readonly string[] HelpOfferFriend = [
        "need a hand {name}?", "{name}, want help?", "want me to join {name}?", "i got your back {name}, want help?",
        "{name}! need backup?",
    ];

    public static readonly string[] Joining = ["on my way!", "coming!", "ok!", "here i come!", "be right there!", "ok, joining!"];

    public static readonly string[] NotJoining = ["ok, good luck!", "np, have fun", "ok!", "ok, you got this!", "alright, good luck {name}"];

    public static readonly string[] AfterWin = [
        "gg!", "nice one", "that was fun", "good fight!", "we did it!", "great teamwork", "yay!", "phew, that was close",
        "good game!", "nice spells!", "thanks for the fight", "woo!",
    ];

    // ---- on their own ---------------------------------------------------------------------------

    private static readonly string[] s_smallTalk = [
        "anyone want to quest?", "lfg", "this place is busy today", "anyone seen my pet?", "hi everyone",
        "where's a good place to level?", "i love this hat", "need more gold lol", "i wish i had more treasure cards",
        "my deck needs work", "anyone know a good shield spell?", "i keep fizzling today", "brb, getting a snack",
        "back!", "so many quests", "i almost have enough training points", "who else is questing?",
        "Gamma says hi lol", "i should visit my dorm room", "my backpack is so full", "anyone want to trade tips?",
        "being a wizard is the best", "i need a new robe", "wizard city is so pretty",
    ];

    private static readonly string[] s_level = [
        "almost level {next}!", "one more level to {next}", "level {level} and counting", "ding! oh wait, not yet lol",
        "need more experience for {next}",
    ];

    private static readonly string[] s_time = [
        "good {time} everyone", "nice {time} for questing", "happy {time}!", "anyone else playing this {time}?",
    ];

    private static readonly Dictionary<AmbientSchool, string[]> s_school = new() {
        [AmbientSchool.Fire] = ["fire spells are so warm", "fire elf, go!", "{school} wizards are the hottest lol", "i love my fire cat"],
        [AmbientSchool.Ice] = ["ice wizards are tough", "my ice shields are the best", "ice magic is so cold", "frost beetle time"],
        [AmbientSchool.Storm] = ["storm spells hit so hard", "thunder snake!", "storm wizards never miss lol, jk", "zap!"],
        [AmbientSchool.Myth] = ["myth minions are so helpful", "blood bat go!", "myth is the coolest", "my minion is my best friend"],
        [AmbientSchool.Life] = ["need a heal? i'm life", "life wizards heal everyone", "imp, go!", "fairy heals for everyone :)"],
        [AmbientSchool.Death] = ["death school is spooky fun", "dark sprite, go!", "death wizards are cool", "ghoul time"],
        [AmbientSchool.Balance] = ["balance is the best of everything", "scarab, go!", "balance wizards are so balanced lol", "i love my sandstorm"],
    };

    private static readonly (string Key, string[] Lines)[] s_zone = [
        ("WC_Hub", [
            "Ravenwood has all the teachers", "the Headmaster lives right here in the Commons", "Gamma is with the Headmaster",
            "Private Stillson guards the gate to Unicorn Way", "the Commons is so busy", "the Shopping District has nice gear",
            "first quests start with the Headmaster", "go see your school teacher in Ravenwood",
        ]),
        ("WC_Unicorn", [
            "Lady Blackhope is down in the Haunted Cave", "Lady Oriel is at the end of the street", "stay on the sidewalks, the undead walk the street",
            "Private Stillson asked me to help here", "so many lost souls on this street", "Unicorn Way is spooky", "the Haunted Cave is scary",
        ]),
        ("WC_Shop_Area", [
            "saving up for a new hat", "this robe costs so much gold", "i need a better wand", "shopping is fun",
            "check the shops for your level", "new boots!", "which hat should i get?",
        ]),
        ("Krokotopia", [
            "the sand gets everywhere lol", "watch out for the kroks", "the Pyramid of the Sun is huge", "manders are tough",
            "Krokotopia is so hot", "the Krokosphinx is amazing",
        ]),
        ("Marleybone", [
            "Marleybone is so foggy", "the dogs here are so polite", "Regent's Square is busy today", "Meowiarty is up to no good",
            "Digmoore Station is creepy", "jolly good! lol",
        ]),
        ("MooShu", [
            "the Emperor is sick, we have to help", "MooShu is so pretty", "the Jade Palace is beautiful", "watch out for the oni",
            "Hametsu Village needs help",
        ]),
        ("Dragonspyre", [
            "Malistaire went this way", "it's so hot in Dragonspyre", "the Basilica is huge", "careful, drakes everywhere",
            "Dragonspyre is so old",
        ]),
        ("Grizzleheim", [
            "the bears here are so wise", "Northguard is cold", "watch out for the ravens", "Grizzleheim is so snowy",
        ]),
    ];

    /// <summary>What an idle wizard might say: small talk, its school, its level, the time of day, tips for its zone.</summary>
    public static IReadOnlyList<string> Idle(ChatContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var pool = new List<string>(s_smallTalk);
        pool.AddRange(s_school[context.School]);
        pool.AddRange(s_level);
        if (context.Hour >= 0) {
            pool.AddRange(s_time);
        }

        if (ZoneTips(context.ZoneKey) is { } tips) {
            pool.AddRange(tips); // tips twice: the place is what a passer-by cares about
            pool.AddRange(tips);
        }

        return pool;
    }

    /// <summary>The tips for the zone with internal name <paramref name="zoneKey"/>, or null.</summary>
    public static string[]? ZoneTips(string? zoneKey)
        => zoneKey is null ? null : s_zone.FirstOrDefault(z => zoneKey.Contains(z.Key, StringComparison.OrdinalIgnoreCase)).Lines;

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
