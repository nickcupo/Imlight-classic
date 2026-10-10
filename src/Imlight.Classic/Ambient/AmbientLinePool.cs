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
 * AMBIENT LINE POOL
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (rewritten 2026-10-10; owner: "rework the chat completely of
 * the friendly wizards so its more natural", the old lines sounded "too
 * AI ish"): what ambient wizards say, written to read like the kids,
 * teens and parents in 2009-2010 Wizard101 chat, not like a tour guide.
 *   - Short and messy: mostly 1-6 words, lower case, run-ons, "lol"
 *     tacked on, drawn-out words; ChatStyle types each line per persona.
 *   - Self-centred and often off topic: their own quest, being bored,
 *     begging, bragging about a drop, griping about fizzles and bosses,
 *     asking how to get somewhere. Some impatience that stays clean
 *     ("ugh", "nvm", "fine", "no you").
 *   - Who says what depends on the wizard's temperament (ChatPersona
 *     .Kind): shy, chatty, bossy, show-off, new player, parent. Each
 *     temperament has its own pool besides the shared ones.
 *   - Schools and zones each have their own lines about their real
 *     spells, teachers, bosses, mobs and places (classic-data/spells and
 *     the datamine catalog, checked 2026-10-10), never one template
 *     stamped across all seven schools; school lines unlock with the
 *     wizard's level, as the spells did.
 *   - Threads: a wizard may carry one small story through a session
 *     ("anyone done rattlebones" ... "rattlebones beat me again" ...
 *     "finally beat rattlebones"), so later lines refer back to earlier
 *     ones (ChatMemory).
 *   - "a||b" in a template is two chat lines sent one after the other
 *     (people hit enter mid-thought).
 *
 * Chat dictionary (r806919 ChatFilter WhiteListBase, checked 2026-10-10):
 * every word of every line, in every ChatStyle, is a dictionary word, or
 * the client would hide it. Not in it, so never used: u, ur, r, omg, kk,
 * gl, ppl, tho, whoa, cant, ez, lf, wb, number words from two to twenty,
 * and digits (only open-chat grown-ups type a level number). The tests
 * check this with a committed vocabulary (CI) and, given
 * W101C_CHATFILTER_DIR, against the client's own lists.
 *
 * In era (to October 2010, Celestia not yet): Wizard City, Krokotopia,
 * Marleybone, MooShu, Dragonspyre, Grizzleheim, the arena, housing, the
 * Bazaar, mounts, elixirs, the Pet Pavilion. Never: Celestia or later
 * worlds, gardening, fishing, crafting, jewels, critical and block,
 * later slang.
 *
 * Templates are lower case; slots: {zone} {school} {level} {next} {time}
 * {boss} {mob} {spell} {place} {name} {me} {a} {b} {aschool} {bschool}.
 * {level} and {next} are numbers, so only open-chat grown-ups use them.
 *
 * USAGE EXAMPLE:
 * var pool = AmbientLinePool.Solo(ChatMoment.Idle, "WizardCity/WC_Hub", level: 5, AmbientSchool.Fire, hour: 20, persona);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>What a wizard is doing when it speaks up.</summary>
public enum ChatMoment { Idle, Hunting, Shopping, Following, AfterWin, AfterLoss, BossDoor, Arrived, Leaving }

/// <summary>A short scripted talk between two ambient wizards: each turn is "A:" or "B:" then alternatives split by '|'.</summary>
/// <param name="Key">A name for the history (no repeats in a while).</param>
/// <param name="Zones">Zone path fragments it fits, or empty for anywhere.</param>
/// <param name="Turns">
/// The turns. An empty alternative ("A:|ok") means that wizard may say nothing, which ends the talk. "A="/"B=" (CLASSIC
/// 2026-10-10) answers the option the other one just picked: the same position, with '/' between its alternatives.
/// </param>
public sealed record ChatExchange(string Key, string[] Zones, string[] Turns);

/// <summary>
/// CLASSIC (2026-10-10): a small story one wizard tells over a session, a line at a time, so it can refer back to what it
/// said before.
/// </summary>
/// <param name="Key">The thread's name.</param>
/// <param name="Zones">Zone path fragments it fits, or empty for anywhere.</param>
/// <param name="MinLevel">The lowest level that tells it.</param>
/// <param name="MaxLevel">The highest level that tells it.</param>
/// <param name="Steps">The lines, in order (each may hold slots).</param>
public sealed record ChatThread(string Key, string[] Zones, int MinLevel, int MaxLevel, string[] Steps);

/// <summary>The line pools (see the file header).</summary>
public static class AmbientLinePool {

    // ---- menu chat (the client's own QuickChat phrases, r806919 QuickChat table) ----------------

    /// <summary>Menu phrases a menu-only wizard says on its own.</summary>
    public static readonly string[] MenuIdle = [
        "Hello", "Hi!", "Hi everybody!", "How is everyone doing?", "What's up?", "Is anyone new?", "I am new",
        "I just started playing", "I love this game!", "Let's go do some quests", "Let's go to the Arena",
        "Let's go to the Bazaar", "Let's go defeat a boss", "I need to do some shopping", "I need new gear",
        "I need more Treasure Cards", "I need to train", "What school are you?", "Please be my friend",
        "Can you help me please?", "I like your hat", "I like your robes", "I like your pet", "Where should we go?",
        "Wait for me", "I only have a few minutes", "Tired", "Things could be better", "Hurry up",
    ];

    /// <summary>Menu phrases a menu-only wizard answers with.</summary>
    public static readonly string[] MenuReply = [
        "Yes!", "Hello", "Hi!", "Thanks", "You're welcome!", "No problem", "Sorry, I don't know", "Sorry, I'm busy", "Cool",
        "Awesome", "Wow!", "Ha Ha", "Yes, I think so", "I'm doing great!", "I'm okay", "Good luck", "See ya!", "What?",
    ];

    /// <summary>Menu phrases after a won battle.</summary>
    public static readonly string[] MenuAfterWin = ["Good teamwork", "That was fun", "That was easy", "Thanks for the help", "Hooray!", "Phew"];

    /// <summary>Menu phrases after a lost battle.</summary>
    public static readonly string[] MenuAfterLoss = ["Oh no!", "Why me?", "Rough time to fizzle", "Ouch", "Things could be better"];

    // ---- kids and teens, by what is on their mind ------------------------------------------------

    /// <summary>Nothing to do.</summary>
    public static readonly string[] Bored = [
        "bored", "soooo bored", "nothing to do", "what now", "ugh", "hmm", "hello??", "is anyone even here",
        "everyone just stands here lol", "why is nobody talking", "im just gonna stand here", "still waiting",
        "this music is stuck in my head", "ok what do i do now", "boring", "anyone", "i should do my quests but meh",
        "walked around for like an hour lol", "brb||back", "hi||anyone?",
    ];

    /// <summary>Asking strangers for things.</summary>
    public static readonly string[] Begging = [
        "can someone give me gold", "anyone have extra gold", "plz gold", "i need gold plz", "can i have your hat lol",
        "anyone wanna give me crowns lol", "will someone buy me a pet", "i need like a thousand gold", "spare gold?",
        "anyone have treasure cards they dont want", "can someone port me to krokotopia", "need someone to help with my quest plz",
        "can somebody give me a mount", "i only have like no gold", "if anyone has extra gold im right here lol",
    ];

    /// <summary>Showing off.</summary>
    public static readonly string[] Bragging = [
        "just got a new wand", "look at my new robe", "my deck is so good now", "easy", "first try lol",
        "i didnt even get hit", "i have so much gold now", "got a level", "i won every duel today", "i have a mount now",
        "check out my hat", "i got a rare drop", "beat that boss by myself", "my pet is an adult already",
        "im the highest level here lol", "nobody can beat me in pvp", "i won my first ranked match", "new boots!!",
    ];

    /// <summary>Complaining.</summary>
    public static readonly string[] Griping = [
        "i fizzled like a million times", "why do i always fizzle", "ugh fizzled again", "no more mana", "out of potions",
        "that boss cheated", "never get the drop i want", "street fights everywhere", "got pulled into another fight",
        "this quest makes me go back and forth so much", "why is the next quest so far", "the quest arrow is pointing at a wall",
        "my deck is all wrong", "i keep drawing shields lol", "no attack cards in my hand at all", "lost all my health to one mob",
        "why does everything resist me", "i walked all the way here for nothing", "my hat looks so weird lol",
    ];

    /// <summary>Asking how things work and where things are.</summary>
    public static readonly string[] Asking = [
        "where do i go now", "how do i get to krokotopia", "whats a good deck", "what spell should i train",
        "where do you buy potions", "is the wand in the shop worth it", "mounts cost crowns right?", "whats the best school for pvp",
        "wheres the arena", "who sells hats", "can someone tell me how to heal", "where is the library",
        "training points come from levels?", "anyone know where golem court is", "whats a treasure card", "need more mana how",
        "does anyone know how to hatch",
    ];

    /// <summary>Looking for company.</summary>
    public static readonly string[] Social = [
        "anyone wanna quest", "wanna be friends", "add me", "anyone wanna duel", "who wants to team up", "lets all go to the arena",
        "my friend is coming on soon", "where did everyone go", "hi guys", "hey", "yo", "hiya", "who here is the highest level",
        "who wants to race", "anyone wanna come see my dorm", "i need more friends lol", "lfg", "group anyone",
    ];

    /// <summary>Life away from the game.</summary>
    public static readonly string[] Home = [
        "brb dinner", "my mom says i have to get off soon", "i have school tomorrow", "homework done finally",
        "my little brother wants a turn", "my dog keeps barking lol", "its raining here", "snow day no school",
        "my sister is watching me play lol", "brb my mom is calling me", "on my dads computer", "i have practice soon",
        "my mom is making me clean my room brb",
    ];

    /// <summary>A bossy kid telling everyone what to do.</summary>
    public static readonly string[] Bossy = [
        "everyone follow me", "stop standing on the sigil", "guys get out of the street", "go do your quests lol",
        "dont pull the street fights", "just use a shield", "put a blade on before you hit", "everyone go to unicorn way",
        "ok listen", "hurry up guys", "ok im leading", "nobody start the boss yet", "stand over here", "no thats wrong",
        "you have to train your spells first", "come on",
    ];

    /// <summary>A shy kid.</summary>
    public static readonly string[] Shy = [
        "um hi", "hi...", "can i ask something", "nvm", "oh", "sorry", "nobody talks to me lol", "...", "hi i guess",
        "is it ok if i follow you", "i dont really know anyone here", "um where is the library", "ok", "never mind",
    ];

    /// <summary>A new player.</summary>
    public static readonly string[] Newbie = [
        "im new", "how do i play", "i just started", "first day lol", "is this the right way", "how do i get to unicorn way",
        "what do i do first", "where is ravenwood", "im so lost", "how do i use my cards", "i only have like no spells",
        "where do i buy stuff", "who is gamma", "what are training points", "what do the pips do", "how do i open my spellbook",
        "the headmaster sent me here i think", "why did the fight start by itself", "how do i get out of a fight",
        "what does the arrow mean", "which school is the best",
    ];

    /// <summary>Every kid line on its own (tests).</summary>
    public static IEnumerable<string> Kid => Bored.Concat(Begging).Concat(Bragging).Concat(Griping).Concat(Asking).Concat(Social)
        .Concat(Home).Concat(Bossy).Concat(Shy).Concat(Newbie);

    // ---- grown-ups -------------------------------------------------------------------------------

    /// <summary>A parent, typed neater and slower; lost, tired, talking about the kids.</summary>
    public static readonly string[] Grownup = [
        "where did my son go", "my daughter ran off again", "kids are asleep, finally my turn", "how do I get back to the commons",
        "is it normal to fizzle this much", "my son says I'm doing it wrong", "which school is the one that heals",
        "ok which way is ravenwood", "sorry, still figuring this out", "my kid leveled past me again",
        "are the boots in the shop worth it", "just a couple quests tonight", "my wife plays fire, I went ice",
        "wish this game had a pause button", "the street fights keep pulling me in", "sorry, I type slow",
        "these ghosts are tougher than they look", "lost my kids somewhere on unicorn way", "my son set up my deck",
        "I have no idea what half these cards do", "my daughter wants the pet with the wings", "waiting on my son to log in",
        "my kids think I'm terrible at this", "too many quests, not enough time", "anyone else on after the kids are asleep",
        "how do you get the arrow to point somewhere else", "my daughter beat that boss without me, now I'm stuck",
        "the music in the commons is stuck in my head", "I keep clicking the wrong card", "finally out of wizard city",
        "they want me to buy crowns again", "my son is in krokotopia already", "where do I sell all this stuff",
    ];

    /// <summary>A grown-up with open chat may type a level.</summary>
    public static readonly string[] GrownupLevel = [
        "level {level} now", "made {level} tonight", "{level} and still lost half the time", "my son is ahead of me and I'm {level}",
    ];

    // ---- level bands ----------------------------------------------------------------------------

    public static readonly string[] Level1To5 = [
        "is unicorn way the one with the ghosts", "what do i do with lost souls", "stillson keeps sending me back lol",
        "how do you beat rattlebones", "lady oriel needs me i think", "i just got my second spell", "the first quests take forever",
    ];

    public static readonly string[] Level6To14 = [
        "almost done with wizard city", "triton avenue is so annoying", "i need to do the sunken city",
        "whats after wizard city", "nightside is creepy", "cyclops lane is so hard", "colossus boulevard is so long",
        "i got lost in the haunted cave", "golem tower anyone", "almost ready for krokotopia",
    ];

    public static readonly string[] Level15To25 = [
        "krokotopia is so big", "lost in the pyramid again", "manders hit so hard", "the tomb of storms is so long",
        "krokopatra beat me", "almost done with krokotopia", "the oasis is so far", "marleybone next",
    ];

    public static readonly string[] Level26To40 = [
        "marleybone took forever", "big ben is so long", "katzenstein's lab is creepy", "the oni hit so hard",
        "finally in mooshu", "my deck finally works", "the jade palace is huge", "kagemoosha is so hard",
    ];

    public static readonly string[] Level41To50 = [
        "dragonspyre is so hard", "almost to malistaire", "who wants to help with malistaire", "the basilica is huge",
        "my gear is bad for dragonspyre", "this fight took forever", "im done with malistaire now what lol",
        "the great spyre is so long", "drakes everywhere ugh",
    ];

    // ---- schools: (level the spell or remark fits from, line) -----------------------------------

    /// <summary>Each school's own talk, by the level it fits from (teachers and spells from classic-data/spells).</summary>
    public static readonly Dictionary<AmbientSchool, (int Min, string Line)[]> SchoolLines = new() {
        [AmbientSchool.Fire] = [
            (1, "everything i hit is on fire lol"), (1, "dalia falmea gave me a new spell"), (1, "why do ice mobs take so long"),
            (5, "fire elf burns so slow"), (10, "got sunbird finally"), (12, "link is kinda weak but i use it anyway"),
            (15, "the krok mobs resist fire ugh"), (22, "meteor hits everything i love it"), (28, "fire elemental!!"),
            (33, "phoenix costs so many pips"), (42, "helephant is worth it"), (48, "fire dragon looks so cool"),
        ],
        [AmbientSchool.Ice] = [
            (1, "frost beetle is so weak lol"), (1, "lydia greyrose talks so slow"), (1, "i have so much health lol"),
            (5, "snow serpent is cute"), (10, "evil snowman is my favorite"), (16, "tower shield saves me every fight"),
            (18, "ice armor is the best"), (22, "ice wyvern"), (26, "blizzard hits everyone but not much"),
            (42, "colossus hits so hard"), (48, "frost giant!!"), (5, "everyone hits so hard and i just sit here lol"),
        ],
        [AmbientSchool.Storm] = [
            (1, "thunder snake is so cute"), (1, "halston balestrom is so weird lol"), (1, "i have no health at all"),
            (5, "lightning bats are so loud"), (10, "storm shark fizzled again"), (22, "kraken hits for so much when it hits"),
            (28, "tempest hits all of them"), (38, "triton!!"), (42, "stormzilla is so funny"), (48, "storm lord"),
            (1, "storm misses so much lol"), (10, "one hit and im out lol"),
        ],
        [AmbientSchool.Myth] = [
            (1, "blood bat"), (1, "cyrus drake is so mean"), (2, "my golem minion just stands there"), (5, "troll!!"),
            (7, "my minion keeps getting beat up"), (10, "cyclops is my favorite"), (18, "cyclops minion is the best minion"),
            (22, "humongofrog lol"), (33, "minotaur"), (42, "earthquake hits all of them"), (48, "orthrus"),
        ],
        [AmbientSchool.Life] = [
            (1, "imp is so tiny"), (1, "moolinda wu is the nicest"), (1, "everyone always wants me to heal them"),
            (5, "leprechaun lol"), (7, "unicorn heals everyone"), (10, "sprite"), (22, "seraph is so pretty"),
            (26, "satyr heals a ton"), (33, "centaur"), (48, "rebirth saved us"), (1, "i do no damage lol"),
        ],
        [AmbientSchool.Death] = [
            (1, "dark sprite"), (1, "malorn ashthorn is so grumpy lol"), (1, "im not evil i just picked death"),
            (5, "ghoul heals me"), (10, "banshee"), (16, "vampire is the best spell"), (18, "curse then hit"),
            (22, "skeletal pirate"), (42, "wraith"), (48, "scarecrow hits all of them"),
        ],
        [AmbientSchool.Balance] = [
            (1, "scarab"), (1, "arthur wethersfield talks so much"), (1, "nobody knows what balance does lol"),
            (5, "scorpion"), (10, "locust swarm"), (12, "balanceblade on everyone"), (16, "sandstorm hits all of them"),
            (26, "spectral blast"), (28, "judgement takes all my pips"), (42, "hydra"), (48, "power nova"),
        ],
    };

    /// <summary>Each school's lines, every level (tests).</summary>
    public static Dictionary<AmbientSchool, string[]> School { get; } =
        SchoolLines.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => v.Line).ToArray());

    /// <summary>The school lines a wizard of <paramref name="level"/> says.</summary>
    public static string[] SchoolFor(AmbientSchool school, int level)
        => SchoolLines[school].Where(s => s.Min <= Math.Max(1, level)).Select(s => s.Line).ToArray();

    // The spells a wizard has by school and level, for {spell} (classic-data/spells level_learned).
    private static readonly Dictionary<AmbientSchool, (int Min, string Spell)[]> s_spells = new() {
        [AmbientSchool.Fire] = [(1, "fire cat"), (5, "fire elf"), (10, "sunbird"), (22, "meteor"), (33, "phoenix"), (42, "helephant"), (48, "fire dragon")],
        [AmbientSchool.Ice] = [(1, "frost beetle"), (5, "snow serpent"), (10, "evil snowman"), (22, "ice wyvern"), (42, "colossus"), (48, "frost giant")],
        [AmbientSchool.Storm] = [(1, "thunder snake"), (5, "lightning bats"), (10, "storm shark"), (22, "kraken"), (38, "triton"), (42, "stormzilla")],
        [AmbientSchool.Myth] = [(1, "blood bat"), (5, "troll"), (10, "cyclops"), (22, "humongofrog"), (33, "minotaur"), (42, "earthquake")],
        [AmbientSchool.Life] = [(1, "imp"), (5, "leprechaun"), (7, "unicorn"), (22, "seraph"), (26, "satyr"), (33, "centaur")],
        [AmbientSchool.Death] = [(1, "dark sprite"), (5, "ghoul"), (10, "banshee"), (16, "vampire"), (22, "skeletal pirate"), (42, "wraith")],
        [AmbientSchool.Balance] = [(1, "scarab"), (5, "scorpion"), (10, "locust swarm"), (16, "sandstorm"), (26, "spectral blast"), (42, "hydra")],
    };

    /// <summary>The newest attack spell a wizard of <paramref name="school"/> and <paramref name="level"/> has.</summary>
    public static string SpellFor(AmbientSchool school, int level)
        => s_spells[school].Where(s => s.Min <= Math.Max(1, level)).Select(s => s.Spell).LastOrDefault() ?? s_spells[school][0].Spell;

    // ---- time of day ----------------------------------------------------------------------------

    public static readonly Dictionary<string, string[]> Time = new() {
        ["morning"] = ["playing before school lol", "morning", "have to leave for school soon", "i should eat breakfast"],
        ["afternoon"] = ["just got home", "school was so long", "homework later lol", "finally home"],
        ["evening"] = ["just ate", "one more hour then sleep", "my mom says i can play after dinner", "dinner soon"],
        ["night"] = ["its so late", "i should be asleep lol", "one more quest then sleep", "night guys", "so tired"],
        ["weekend"] = ["no school tomorrow", "saturday!!", "i get to stay up late", "playing all day"],
    };

    // ---- zones (zone path fragment) -------------------------------------------------------------

    public static readonly (string Key, string[] Lines)[] Zone = [
        ("WC_Hub", [
            "why is everyone standing by the fountain", "anyone going to unicorn way", "gamma talks too much lol",
            "wheres golem court", "ravenwood is that way right", "olde town is past the arena right",
            "race to the shopping district", "i always end up back here", "this music never stops lol",
            "the commons is so full today", "anyone going to the arena", "everybody in the commons say hi lol",
        ]),
        ("WC_Unicorn", [
            "lost souls everywhere", "these dark fairies hit hard", "rotting fodder lol what a name", "where is lady oriel",
            "rattlebones keeps beating me", "the haunted cave is at the end right", "anyone need lost souls",
            "ok who keeps starting fights", "the street fights here never end", "help with rattlebones plz",
            "rattlebones dropped nothing again", "stay on the sidewalk", "stillson says the street is still bad",
        ]),
        ("WC_Triton", [
            "foulgaze is so gross", "the screamers are so annoying", "anyone done the sunken city", "where is the sunken city door",
            "the kraken took forever", "triton avenue is so confusing", "eels lol",
        ]),
        ("WC_Firecat", [
            "nightside is creepy", "magma men hit so hard", "fire elves everywhere", "lord nightshade is so hard",
            "firecat alley is so dark", "who wants to do nightside",
        ]),
        ("WC_Cyclops", [
            "cyclops lane is the worst", "the cyclops hit so hard", "general akilles beat me", "these trolls are huge",
            "i keep getting jumped here", "warhorns lol",
        ]),
        ("WC_Colossus", [
            "gobblers lol", "prince gobblestone is so weird", "why are there snowmen here", "this street is so long",
            "the gobblers eat everything",
        ]),
        ("WC_OldeTown", [
            "anyone going to the bazaar", "my backpack is full again", "i need to sell stuff", "the arena is right there",
            "olde town is so pretty at night",
        ]),
        ("AuctionHouse", [
            "how is this so expensive", "sold all my junk", "the bazaar never has the hat i want", "anyone know what sells good",
            "sold my old robe for like nothing", "i need gold for that wand", "why is everything a hundred gold more today",
            "treasure cards are so expensive here", "come on just one good wand", "i check here every day lol",
        ]),
        ("WC_Shop_Area", [
            "these hats cost too much", "i can only afford boots", "which robe is better", "the pet shop is cool",
            "i want everything here", "need to sell first", "is the wand worth it", "so many hats", "ugh not enough gold",
        ]),
        ("WC_Ravenwood", [
            "bartleby is huge", "my teacher gave me a new spell", "where is the library", "i got lost in ravenwood lol",
            "gotta train", "is the headmaster in his office", "which way is my school",
        ]),
        ("WC_HauntedCave", [
            "blackhope is so hard", "stay together", "this cave is so dark", "who wants to do blackhope", "i got lost in here",
            "blackhope has so much health",
        ]),
        ("Hatchery", [
            "anyone wanna hatch", "my pet is an adult now", "whats your pet", "hatching costs so much",
            "i wonder what i will get", "my pet is so cute", "can i hatch with you", "how does hatching work",
            "trained my pet all day", "the egg takes forever",
        ]),
        ("PET_Park", [
            "anyone wanna hatch", "my pet is an adult now", "whats your pet", "hatching costs so much", "trained my pet all day",
        ]),
        ("KT_", [
            "the sand gets in everything lol", "manders hit so hard", "where is the pyramid of the sun", "the tomb of storms is so long",
            "krokopatra cheats", "the oasis is so far", "i need to get to the krokosphinx", "kroks everywhere",
            "the mummies are so slow lol",
        ]),
        ("MB_", [
            "marleybone is so foggy", "the dogs talk so fancy lol", "big ben is so tall", "meowiarty is so hard",
            "katzenstein's lab is creepy", "the cats are so mean", "chelsea court", "rats everywhere ugh",
        ]),
        ("MS_", [
            "the oni hit so hard", "the jade palace is so pretty", "samoorai lol", "the emperor is sick", "kagemoosha",
            "the cows here are funny", "the plague village is gross",
        ]),
        ("DS_", [
            "malistaire", "the basilica is huge", "drakes everywhere", "dragonspyre is so dark", "the great spyre",
            "the atheneum is so big", "everything here hits so hard",
        ]),
        ("GH_", [
            "jotun needs a full group", "the bears talk lol", "ravens are creepy", "northguard is so cold",
            "anyone wanna do the hall of kings", "the wolves are so annoying", "grizzleheim is so pretty",
        ]),
    ];

    // ---- what they are doing --------------------------------------------------------------------

    public static readonly string[] Hunting = [
        "need more {mob}", "where are all the {mob}", "{mob} again", "why are there no {mob}", "ok one more", "almost done",
        "this is taking forever", "anyone else hunting {mob}", "stop taking my {mob} lol", "got pulled in again",
        "so many {mob}", "one more and im done", "ugh another fight",
    ];

    public static readonly string[] Shopping = [
        "hmm", "too expensive", "i can't afford anything lol", "ooh", "want this hat", "maybe next time", "need more gold",
        "is this good for my level", "boots or hat", "nope", "so much gold",
    ];

    public static readonly string[] Following = ["wait for me", "where are we going", "right behind you", "slow down lol", "coming", "wait"];

    public static readonly string[] AfterWin = [
        "gg", "gg!", "nice", "ty", "that was close", "phew", "easy", "woot", "yay", "finally", "ok next", "that took forever lol",
        "nice hit", "yes", "lol that was quick",
    ];

    public static readonly string[] AfterWinGrownup = ["Good fight.", "Thanks, that helped.", "Nicely done.", "Phew, close one.", "That was a long one."];

    public static readonly string[] AfterLoss = [
        "ugh", "nooo", "fizzled the whole fight", "that was not fair", "back to the commons lol", "lost again", "i had it too",
        "why", "i need more health", "out of mana again", "rip", "that boss cheats", "fizzling is the worst",
    ];

    public static readonly string[] BossDoor = [
        "anyone wanna do {boss}", "need help with {boss} plz", "{boss}??", "lfg {boss}", "who wants {boss}",
        "can someone help me beat {boss}", "{boss} beat me again", "doing {boss} who wants to come", "anyone done {boss} yet",
    ];

    public static readonly string[] Arrived = ["hi", "hey", "back", "yo", "hi guys", "sup", "hello?", "im here", "hiya"];

    public static readonly string[] Leaving = [
        "gtg", "bye", "gtg dinner", "bye guys", "ok bye", "my mom says off", "ttyl", "cya", "bedtime bye", "gtg bye",
    ];

    /// <summary>Its own open call went unanswered for a while.</summary>
    public static readonly string[] NobodyAnswered = ["guess not", "nvm", "ok nobody lol", "fine ill do it myself", "anyone?", "hello??", "ok then"];

    // ---- answers to people ----------------------------------------------------------------------

    public static readonly string[] Greet = [
        "hi", "hey", "hiya", "heya", "yo", "sup", "hi {name}", "hey {name}", "oh hi", "hello", "hai", "hi?", "hey whats up",
    ];

    public static readonly string[] GreetNear = [
        "hi", "hey", "hi {name}", "yo", "nice hat", "what school are you", "hi wanna quest", "i like your robe", "sup",
        "hey {name} wanna team up", "hello", "hi?",
    ];

    public static readonly string[] HowAreYou = [
        "good", "good you", "bored", "tired", "ok", "meh", "good just questing", "fine i guess", "hungry lol",
        "great i just leveled", "not bad", "ugh fizzling all day",
    ];

    public static readonly string[] LevelNoNumber = [
        "low lol", "not that high", "higher than you prob", "why", "almost done with wizard city", "secret lol",
        "high enough", "dunno like the middle", "why does it matter",
    ];

    public static readonly string[] LevelNumber = ["{level}", "lvl {level}", "{level} you?", "{level} almost {next}", "{level} why"];

    public static readonly string[] SchoolAnswer = ["{school}", "im {school}", "{school} you", "{school} why", "{school} lol", "{school} obviously"];

    public static readonly string[] AgeAnswer = ["not telling", "why", "old enough lol", "secret", "my mom says not to say", "lol no"];

    public static readonly string[] NameAnswer = ["{me}", "its {me}", "{me} lol", "its right there above my head lol"];

    public static readonly string[] ComplimentThanks = ["ty", "thx", "ty i got it at the shop", "ty yours is cool too", "thanks lol", "ty its new", "i know right"];

    public static readonly string[] GoldBeg = [
        "no", "lol no", "i need gold too", "get your own lol", "no sorry", "i only have like a hundred", "sell stuff", "no im saving",
        "you can't even give gold lol", "beg somewhere else lol",
    ];

    public static readonly string[] NotABot = ["what", "lol what", "no?", "huh", "um no", "?", "are you?"];

    public static readonly string[] Laugh = ["lol", "haha", "lolz", "hehe", "xd", "rofl", "lol ya"];

    public static readonly string[] Agree = ["ya", "yea", "yeah", "same", "true", "i know right", "me too", "k"];

    public static readonly string[] Busy = ["busy", "not now", "maybe later", "doing a quest", "after this", "no im busy"];

    public static readonly string[] Unsure = ["idk", "dunno", "no idea", "idk ask someone else", "um", "not sure"];

    public static readonly string[] Come = ["k", "where", "ok where", "coming", "on my way", "wait im in a fight"];

    public static readonly string[] Teleport = ["port to me", "add me then port", "it wont let me", "wait im in a fight", "where are you"];

    public static readonly string[] HowToHatch = [
        "you need an adult pet", "go to the pet pavilion", "it costs a ton of gold", "talk to the guy in the pavilion",
    ];

    public static readonly string[] HowToGold = ["sell stuff", "street fights", "bosses give more", "quests", "sell at the bazaar"];

    public static readonly string[] Trade = ["no trading", "sell it at the bazaar", "trade what", "no"];

    public static readonly string[] Duel = [
        "sure meet at the arena", "lol no", "ill lose", "after this quest", "maybe", "practice or ranked", "my deck is bad for pvp",
        "ok", "no ranked is scary lol",
    ];

    public static readonly string[] Friend = ["sure", "k", "ok send it", "sure add me", "ya", "ok"];

    public static readonly string[] Thanks = ["np", "yw", "sure", "np lol", "no prob", "k"];

    public static readonly string[] Bye = ["bye", "cya", "bye {name}", "ttyl", "later", "k bye"];

    public static readonly string[] Fallback = [
        "lol", "ok", "k", "what", "?", "huh", "oh", "cool", "hmm", "ya", "lol ok", "um ok", "idk", "same", "oh ok", "nice",
    ];

    public static readonly string[] HelpSure = ["sure where", "ok what quest", "ya", "k start the fight", "sure", "what do you need", "only if its quick"];

    public static readonly string[] QuestTogether = [
        "sure", "what quest", "where", "maybe", "after this", "what level are you", "i have to finish mine", "no", "ok come to {zone}",
    ];

    public static readonly string[] Doing = [
        "nothing", "questing", "being bored", "hunting {mob}", "waiting for my friend", "shopping", "nothing much",
        "trying to level", "standing here lol", "dunno",
    ];

    /// <summary>CLASSIC (2026-10-10): someone was rude ("noob", "you're bad"): a short shrug, never rude back.</summary>
    public static readonly string[] Rude = ["?", "ok", "k", "lol ok", "whatever", "rude", "um ok", "no you", "ok then"];

    /// <summary>CLASSIC (2026-10-10): someone said "what?" or "huh?" to it: it repeats itself ({last}) or lets it go.</summary>
    public static readonly string[] What = ["nvm", "nothing", "{last}", "i said {last}", "never mind lol"];

    /// <summary>CLASSIC (2026-10-10): someone said their school is the best.</summary>
    public static readonly string[] SchoolOpinion = ["no {school} is", "lol no", "ew", "ya right", "{school} is better", "whatever", "ok"];

    /// <summary>CLASSIC (2026-10-10): someone says they will be right back or are back.</summary>
    public static readonly string[] Brb = ["k", "ok", "hi again", "finally lol"];

    /// <summary>CLASSIC (2026-10-10): "anyone done rattlebones?", "is jotun hard": what players said about bosses.</summary>
    public static readonly string[] BossTalk = [
        "me", "not yet", "ya", "ya its easy", "it beat me", "i did lol", "use a shield", "bring a heal", "its hard", "with a group yes",
    ];

    /// <summary>A short answer from a wizard that does not feel like talking.</summary>
    public static readonly string[] Curt = ["k", "ok", "ya", "idk", "maybe", "no", "nah", "?", "sure", "meh"];

    // ---- two wizards talking --------------------------------------------------------------------

    // "A:"/"B:" pick freely among '|' options; "A="/"B=" answer the option the other just picked (same position, '/'
    // between its alternatives). An empty alternative is silence, which ends the talk.
    public static readonly ChatExchange[] Exchanges = [
        new("hi", [], ["A:hi|hey|yo", "B:hi|hey|sup|hiya", "A:wanna quest|what level are you|bored|what school are you",
            "B=sure/no/maybe later/|low lol/why/higher than you|same/lol same/go quest|{bschool}/{bschool} you", "A:|ok|lol"]),
        new("school", [], ["A:what school are you", "B:{bschool}|{bschool} you", "A:{aschool}|ew jk|cool|same", "B=|lol/ok|ya/|"]),
        new("school-fight", [], ["A:{aschool} is the best", "B:no|lol no|{bschool} is better|ok sure", "A=no you/lol|whatever/no|nope/ew|", "B:|no you|ok"]),
        new("gold", [], ["A:can i have gold|anyone have gold", "B:no|lol no|get your own", "A:plz|aw|fine", "B=no/still no|lol|"]),
        new("help-boss", ["WC_Unicorn", "WC_HauntedCave", "WC_Triton", "WC_Firecat", "KT_", "MB_", "MS_", "DS_", "GH_"],
            ["A:anyone wanna help with {boss}|need help with {boss}", "B:what level are you|sure|maybe|already did it",
             "A=low lol/why|ok come/where are you|ok/plz|aw/ok", "B=lol/ok|on my way/k|/k|"]),
        new("lost", [], ["A:where do i go for this quest|im lost", "B:follow the arrow|idk|what quest", "A=ok/that arrow is broken|ok/nvm found it|never mind/nvm"]),
        new("hat", [], ["A:nice hat|cool hat", "B:ty|thx", "A:|where did you get it", "B=|shopping district/a boss dropped it/dont remember"]),
        new("brag", [], ["A:i just got {spell}|got {spell} finally", "B:cool|so|i got that ages ago|nice", "A=|lol ok/whatever|ok/so|lol"]),
        new("fizzle", [], ["A:i keep fizzling|fizzled again", "B:same|lol same|its your deck|unlucky", "A=ugh/|grr/lol|no its not/maybe|ya"]),
        new("bored", [], ["A:bored", "B:same|go quest|lol|me too", "A=lets do something/meh|no/later|/what|lets duel/meh"]),
        new("night", [], ["A:gtg soon|my mom says i have to go soon", "B:same|aw|k", "A:bye|cya", "B:|bye"]),
        new("new", ["WC_Hub", "WC_Ravenwood", "WC_Unicorn"], ["A:im new", "B:cool|hi|what school", "A=ty/|hi/|{aschool}"]),
        new("race", ["WC_Hub", "WC_Shop_Area", "WC_Ravenwood"], ["A:race you to ravenwood|race to the shopping district", "B:go|no lol|ok", "A=|fine/lol|"]),
        new("arena", ["WC_Hub", "WC_OldeTown"], ["A:anyone wanna duel", "B:sure|lol no|practice?", "A=meet at the arena/ok|fine/aw|ya/ok"]),
        new("pet", ["Hatchery", "PET_Park"], ["A:anyone wanna hatch", "B:me|what pet", "A:my {pet}", "B:ok|ooh|nah"]),
        new("bazaar", ["AuctionHouse", "WC_OldeTown"], ["A:does the bazaar have good wands", "B:sometimes|idk|no", "A:ok|ugh"]),
        new("mana", [], ["A:out of mana", "B:buy potions|go to the commons|same", "A=no gold lol/ok|ok/k|"]),
        new("jotun", ["GH_"], ["A:anyone for jotun", "B:sure|what level|already did him", "A=ok come/|low lol/why|aw/ok"]),
        new("malistaire", ["DS_"], ["A:anyone wanna do malistaire", "B:sure|maybe later|i need to level", "A=ok/k|k/aw|ok/same"]),
        new("quest", [], ["A:what quest are you on", "B:{boss}|dunno|a long one", "A=same/nice|lol/|lol same/ugh"]),
    ];

    // ---- threads (a story told over a session) --------------------------------------------------

    public static readonly ChatThread[] Threads = [
        new("rattlebones", ["WC_Unicorn"], 1, 10, ["anyone done rattlebones", "rattlebones beat me", "ok trying rattlebones again",
            "finally beat rattlebones", "rattlebones didnt even drop anything good"]),
        new("blackhope", ["WC_Unicorn", "WC_HauntedCave"], 3, 12, ["lady blackhope next", "blackhope beat me", "anyone wanna help with blackhope",
            "beat blackhope!!"]),
        new("fizzle", [], 1, 50, ["fizzled every turn lol", "fizzled again", "ok who cursed my deck lol", "finally stopped fizzling"]),
        new("hat", ["WC_Hub", "WC_Shop_Area", "WC_OldeTown", "AuctionHouse"], 1, 50, ["saving up for a hat", "how much is the hat in the shopping district",
            "still saving lol", "got the hat!!"]),
        new("friend", [], 1, 50, ["waiting for my friend", "my friend still isnt on", "my friend is on now"]),
        new("lost", ["WC_Hub", "WC_Ravenwood", "WC_Unicorn"], 1, 6, ["where is ravenwood", "nvm found it"]),
        new("deck", [], 5, 50, ["fixing my deck brb", "ok deck is better now", "nope deck is still bad lol"]),
        new("gold", [], 1, 50, ["need gold", "still need gold", "sold some stuff, have gold now"]),
        new("pet", ["Hatchery", "PET_Park"], 10, 50, ["my pet is almost an adult", "my pet is an adult now!!", "who wants to hatch"]),
        new("storms", ["KT_"], 15, 25, ["the tomb of storms is so long", "still in the tomb of storms", "done with the tomb finally"]),
        new("meowiarty", ["MB_"], 20, 30, ["anyone done meowiarty", "meowiarty cheated", "beat meowiarty!!"]),
        new("oni", ["MS_"], 30, 40, ["jade oni next", "the oni beat me", "ok the jade oni is done"]),
        new("malistaire", ["DS_"], 40, 50, ["almost to malistaire", "malistaire beat us", "we beat malistaire!!"]),
        new("jotun", ["GH_"], 12, 30, ["anyone for jotun", "we still need people for jotun", "jotun done finally"]),
    ];

    /// <summary>The threads a wizard here at this level may start.</summary>
    public static IEnumerable<ChatThread> ThreadsFor(string? zone, int level)
        => Threads.Where(t => level >= t.MinLevel && level <= t.MaxLevel
                              && (t.Zones.Length == 0 || t.Zones.Any(z => (zone ?? "").Contains(z, StringComparison.OrdinalIgnoreCase))));

    // ---- lookups --------------------------------------------------------------------------------

    private static readonly (string Key, string[] Bosses)[] s_bosses = [
        ("WC_Unicorn", ["rattlebones", "lady blackhope"]), ("WC_HauntedCave", ["lady blackhope"]),
        ("WC_Triton", ["foulgaze", "the kraken"]), ("WC_Firecat", ["lord nightshade"]), ("WC_Cyclops", ["general akilles"]),
        ("WC_Colossus", ["prince gobblestone"]),
        ("KT_", ["krokopatra", "the krokosphinx"]), ("MB_", ["meowiarty", "katzenstein"]), ("MS_", ["the jade oni", "kagemoosha"]),
        ("DS_", ["malistaire"]), ("GH_", ["jotun", "grettir"]),
    ];

    private static readonly (string Key, string[] Mobs)[] s_mobs = [
        ("WC_Unicorn", ["lost souls", "dark fairies", "rotting fodder"]), ("WC_HauntedCave", ["rotting fodder"]),
        ("WC_Triton", ["screamers", "rotting fodder", "eels"]), ("WC_Firecat", ["fire elves", "magma men", "skeletons"]),
        ("WC_Cyclops", ["cyclops", "trolls"]), ("WC_Colossus", ["gobblers", "snowmen"]), ("KT_", ["kroks", "manders", "mummies"]),
        ("MB_", ["rats", "clockworks"]), ("MS_", ["oni", "samoorai"]), ("DS_", ["drakes", "skeletons"]), ("GH_", ["ravens", "wolves"]),
    ];

    private static readonly string[] s_pets = ["fire cat", "unicorn", "snow serpent", "imp", "frog", "dragon", "owl", "puppy"];

    /// <summary>The bosses players talk about in a zone, or empty.</summary>
    public static string[] BossesFor(string? zone)
        => zone is null ? [] : s_bosses.Where(b => zone.Contains(b.Key, StringComparison.OrdinalIgnoreCase)).SelectMany(b => b.Bosses).ToArray();

    /// <summary>The street mobs a zone has, or null.</summary>
    public static string? MobFor(string? zone, int pick) {
        var mobs = zone is null ? null : s_mobs.FirstOrDefault(m => zone.Contains(m.Key, StringComparison.OrdinalIgnoreCase)).Mobs;
        return mobs is { Length: > 0 } ? mobs[(int) ((uint) pick % (uint) mobs.Length)] : null;
    }

    /// <summary>A pet name for the hatching talk.</summary>
    public static string Pet(int pick) => s_pets[(int) ((uint) pick % (uint) s_pets.Length)];

    /// <summary>The lines for <paramref name="zone"/>, or empty.</summary>
    public static string[] ForZone(string? zone)
        => zone is null ? [] : Zone.Where(z => zone.Contains(z.Key, StringComparison.OrdinalIgnoreCase)).SelectMany(z => z.Lines).ToArray();

    /// <summary>The level-band lines for a level.</summary>
    public static string[] ForLevel(int level) => level switch {
        <= 5 => Level1To5,
        <= 14 => Level6To14,
        <= 25 => Level15To25,
        <= 40 => Level26To40,
        _ => Level41To50,
    };

    /// <summary>The time-of-day lines for a local hour and day.</summary>
    public static IEnumerable<string> ForTime(int hour, DayOfWeek? day) {
        if (hour >= 0 && Time.TryGetValue(AmbientLines.TimeOfDay(hour), out var lines)) {
            foreach (var line in lines) {
                yield return line;
            }
        }

        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday) {
            foreach (var line in Time["weekend"]) {
                yield return line;
            }
        }
    }

    /// <summary>
    /// The kid pools a temperament draws its idle talk from, with weights (a pool listed twice comes up twice as often).
    /// </summary>
    public static IEnumerable<string[]> MoodsFor(ChatTemperament kind) => kind switch {
        ChatTemperament.Shy => [Shy, Shy, Bored, Asking, Home],
        ChatTemperament.Bossy => [Bossy, Bossy, Griping, Social, Bored, Bragging],
        ChatTemperament.ShowOff => [Bragging, Bragging, Social, Begging, Griping, Bored],
        ChatTemperament.Newbie => [Newbie, Newbie, Asking, Begging, Social, Bored],
        _ => [Bored, Begging, Griping, Asking, Social, Social, Home, Bragging],
    };

    /// <summary>
    /// The templates a wizard may say on its own at <paramref name="moment"/>, most fitting first in weight (zone and moment
    /// lines are listed twice). Menu-chat wizards get menu phrases only.
    /// </summary>
    public static List<string> Solo(ChatMoment moment, string? zone, int level, AmbientSchool school, int hour, ChatPersona persona,
                                    DayOfWeek? day = null) {
        ArgumentNullException.ThrowIfNull(persona);
        if (persona.Channel == ChatChannel.Menu) {
            return moment switch {
                ChatMoment.AfterWin => [.. MenuAfterWin],
                ChatMoment.AfterLoss => [.. MenuAfterLoss],
                _ => [.. MenuIdle],
            };
        }

        var pool = new List<string>();
        switch (moment) {
            case ChatMoment.Hunting:
                pool.AddRange(Hunting);
                pool.AddRange(Hunting);
                pool.AddRange(ForZone(zone));
                pool.AddRange(Griping);
                break;
            case ChatMoment.Shopping:
                pool.AddRange(Shopping);
                pool.AddRange(ForZone(zone));
                break;
            case ChatMoment.Following:
                pool.AddRange(Following);
                break;
            case ChatMoment.AfterWin:
                pool.AddRange(persona.Grownup ? AfterWinGrownup : AfterWin);
                pool.AddRange(AfterWin);
                break;
            case ChatMoment.AfterLoss:
                pool.AddRange(AfterLoss);
                break;
            case ChatMoment.BossDoor:
                pool.AddRange(BossDoor);
                break;
            case ChatMoment.Arrived:
                pool.AddRange(Arrived);
                break;
            case ChatMoment.Leaving:
                pool.AddRange(Leaving);
                break;
            default:
                if (persona.Grownup) {
                    pool.AddRange(Grownup);
                    if (persona.Numbers) {
                        pool.AddRange(GrownupLevel);
                    }
                }
                else {
                    foreach (var mood in MoodsFor(persona.Kind)) {
                        pool.AddRange(mood);
                    }

                    pool.AddRange(ForTime(hour, day));
                }

                pool.AddRange(ForZone(zone));
                pool.AddRange(ForZone(zone));
                pool.AddRange(ForLevel(level));
                pool.AddRange(SchoolFor(school, level));
                if (BossesFor(zone).Length > 0) {
                    pool.AddRange(BossDoor.Take(4));
                }

                break;
        }

        return pool;
    }

}
