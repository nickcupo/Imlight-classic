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
 * CLASSIC (2026-10-04): what ambient wizards say on their own, by
 * context, written to sound like the players of 2009 to May 2010.
 * Sources (dated 2008-2010 blogs and forum threads; notes in
 * ~/w101c-private/build/playbot-reports/ambient-chat.md): most players
 * were kids and their parents; text chat was a dictionary (any other word,
 * and every number, showed as "..."), so talk was short and plain: "hi",
 * "lol", "brb", "can i join", "add me", "need help with rattlebones",
 * "what school are you", "nice hat"; menu chat (fixed phrases) was all
 * many kids had; grown-ups got 18+ open chat in June 2009; people stood
 * around the Commons, begged for gold (Oct 2009), asked before joining a
 * fight, and said "gg" or "ty" after.
 *
 * In era (2008-09 to 2010-05): Wizard City, Krokotopia, Marleybone,
 * MooShu, Dragonspyre (Jan 2009), Grizzleheim (Jul 2009), the arena (Dec
 * 2008), housing, the Bazaar and grouping (Jul 2009), mounts, gifting and
 * elixirs (Oct 2009), the Pet Pavilion and hatching (26 May 2010). Not in
 * era, never said: Celestia and every later world, gardening, fishing,
 * jewels, shadow or astral magic, the Pet Derby, later slang. Crafting and
 * henchmen existed in 2009 but are off in this server, so nobody brings
 * them up.
 *
 * Templates are plain lower case; ChatStyle types them per persona. Slots:
 * {zone} {school} {level} {next} {time} {boss} {place} {name} {a} {b}.
 * {level} and {next} are numbers, so only open-chat grown-ups use them
 * (Fill returns null for others). Every filled line must pass IsClean and
 * the client's chat dictionary (AmbientLinePoolTests).
 *
 * USAGE EXAMPLE:
 * var pool = AmbientLinePool.Solo(ChatMoment.Idle, "WizardCity/WC_Hub", level: 5, AmbientSchool.Fire, hour: 20, grownup: false);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
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
/// <param name="Turns">The turns.</param>
public sealed record ChatExchange(string Key, string[] Zones, string[] Turns);

/// <summary>The line pools (see the file header).</summary>
public static class AmbientLinePool {

    // ---- menu chat ------------------------------------------------------------------------------

    /// <summary>Menu-chat phrases a menu-only wizard picks (kept to ones the 2009 menu had in spirit; sent as quick chat when the client's menu has the exact phrase).</summary>
    public static readonly string[] MenuIdle = [
        "Hello!", "Hi!", "Hi everyone!", "Is anyone here?", "Does anyone want to go questing?", "Let's go questing!",
        "I like your outfit!", "Nice hat!", "I like your wand!", "Want to be friends?", "Where are you going?", "Follow me!",
        "Wait for me!", "I'll be right back.", "I'm back!", "This is fun!", "Let's team up!", "Does anyone need help?",
        "Good luck!", "Have fun!", "Goodbye!", "See you later!", "I have to go.", "Let's go!",
    ];

    public static readonly string[] MenuReply = [
        "Hi!", "Hello!", "Yes!", "No, thank you.", "Thank you!", "You're welcome!", "Okay!", "Sure!", "I don't know.",
        "Sorry!", "Good luck!", "Goodbye!", "See you later!", "That's great!", "Cool!", "Me too!",
    ];

    public static readonly string[] MenuAfterWin = ["Good job!", "Great fight!", "We did it!", "Thank you!", "That was fun!", "Good game!"];

    // ---- on their own: any zone -----------------------------------------------------------------

    public static readonly string[] Kid = [
        // hanging around
        "hi everyone", "hi all", "hey guys", "hello anyone here", "anyone wanna quest", "anyone want to team up",
        "who wants to go questing", "anyone wanna be friends", "bored", "so bored lol", "this is fun", "i love this game",
        "this game is the best", "whats everyone doing", "anyone here", "hello", "brb", "back", "brb dinner",
        "brb my mom is calling me", "back sorry", "ok im back", "lol", "lolz", "hehe",
        // gear and gold
        "i need more gold", "how do you get gold fast", "anyone have extra gold", "can someone give me gold plz",
        "i wish i had more gold", "i only have like no gold lol", "im saving up for a new robe", "i need a new hat so bad",
        "does this hat look ok", "i love my new boots", "where do you get cool wands", "my wand is so old lol",
        "i want a better deck", "my deck is a mess", "i keep getting the wrong cards", "i fizzled like a million times lol",
        "why do i always fizzle", "i need more treasure cards", "anyone know a good spell to get",
        // friends
        "add me", "anyone wanna be my friend", "i need more friends", "friend me if you want", "who wants to be friends",
        "my friend is supposed to be on soon", "waiting for my friend", "my friend never comes on anymore",
        "my best friend plays too", "my sister plays too", "my brother is ice lol", "me and my brother are questing",
        // school and home
        "i have school tomorrow", "i finished my homework so i can play", "my mom says one more hour", "only got like an hour left",
        "my dad lets me play on weekends", "i should be doing homework lol", "dinner soon", "i have to go soon",
        // quests
        "so many quests", "i have like a million quests", "this quest is so long", "i keep getting lost",
        "where do i go now", "which way is the next quest", "the quest arrow is pointing at a wall lol", "i love the quest arrow",
        "i just finished a really hard quest", "anyone know where to go for this quest", "i need to talk to the headmaster",
        "where is the headmaster", "where do i turn in quests", "i need to train my spells", "i have training points to spend",
        // pets and houses
        "i love my pet", "my pet follows me everywhere", "i want a pet so bad", "whats the best pet", "i want a unicorn",
        "i want a house", "my dorm room is so small", "anyone wanna see my dorm", "i decorated my dorm",
        // fights
        "anyone wanna duel", "i want to go to the arena", "i lost so bad in the arena lol", "who wants to practice duel",
        "i almost died last fight", "that last fight was so hard", "i need a heal lol", "im almost out of mana",
        "i need potions", "anyone wanna help me fight",
        // little things
        "this place is so pretty", "i love the music here", "wizard city is so cool", "i like the colors here",
        "i wish i could fly", "i want a broom", "nice hats everyone lol", "everyone has cool clothes",
        "i like your name", "what does everyone want for christmas", "i want crowns for my birthday",
        // more hanging around
        "whats the best school", "who here is the highest level", "anyone wanna go to my dorm", "i just got a new spell",
        "i love my new wand", "my hat is so big lol", "anyone else love the commons music", "this is my favorite spot",
        "lets all dance lol", "i wish i could have more pets", "anyone ever beat the headmaster lol", "i want to be a teacher here",
        "i got a rare drop", "yay i leveled up", "ding", "i just leveled", "i finally got enough gold", "i need a mount",
        "i want a broom so bad", "anyone have a mount", "mounts are so cool", "i saw someone with a dragon mount",
        "my friend has a horse mount", "i got an elixir", "how do elixirs work", "the crown shop has cool stuff",
        "my mom said i can get crowns", "i used all my mana", "i keep forgetting to heal", "my pet is so cute",
        "i named my pet after my dog lol", "anyone else have a fire cat pet", "i wanna try the arena", "pvp is so hard",
        "i won my first duel", "i lost my first duel lol", "anyone know a good deck for my school", "i need better boots",
        "this robe makes me look cool", "everyone go to the shopping district", "i like everyone's outfits",
        "lets have a party lol", "who wants to race", "first one to ravenwood wins", "i got here first lol",
    ];

    public static readonly string[] Grownup = [
        "good {time} everyone", "my son is around here somewhere", "my daughter is waiting for me in Ravenwood",
        "questing with my kids tonight", "my kids got me into this game", "my son picked {school} too",
        "the kids are asleep, finally some questing time", "my daughter wants a pet so badly", "anyone else playing with their kids",
        "my son is a higher level than me", "my daughter says my hat is silly", "just a few more quests tonight",
        "nice to see so many people on", "this game is a lot of fun", "my wife plays too", "my husband plays too",
        "my kids think I am terrible at this", "i like that the chat is safe for the kids",
        "trying to catch up to my son", "my daughter has more gold than me",
        "anyone need a hand", "happy to help if anyone needs it", "just let me know if you need help with a fight",
        "anyone know where the next quest is", "my son says I need a better wand", "I keep forgetting which spell does what",
        "is there a good place to get gold", "my daughter told me to wait here", "the music in this game is great",
        "my kids want me to buy them a pet", "still learning how to play", "my son set up my deck for me",
        "anyone else here with family", "we have a family night on here every friday", "I think I bought the wrong hat",
        "my kids are faster at this than me", "finally finished that quest", "where do you all buy your gear",
        "good game everyone", "just waiting for my kids to log on", "I like how friendly everyone is here",
        "anyone know how to get to the next street", "my daughter wants to see the pet shop", "I need more training points",
        "thanks to everyone who helped me earlier", "trying not to fizzle tonight", "my son is a {school} wizard too",
    ];

    public static readonly string[] GrownupLevel = [
        "level {level} now", "almost level {next}", "just made level {level}", "level {level} and still lost half the time",
    ];

    public static readonly string[] Level1To5 = [
        "im new", "im new here", "how do i play", "i just started", "first day lol", "is this the right way",
        "how do i get to unicorn way", "what do i do first", "where is ravenwood", "im so lost", "how do i use my cards",
        "i barely have any spells", "im a new wizard", "where do i buy stuff", "who is gamma",
        "the headmaster sent me here", "what do i do with training points",
    ];

    public static readonly string[] Level6To14 = [
        "almost done with wizard city", "i need to finish all the streets", "unicorn way was easy", "triton avenue is so annoying",
        "those ghosts on unicorn way are so annoying", "the cyclops are so hard", "firecat alley is so hot lol", "so many street fights",
        "i got lost in the haunted cave", "almost ready for krokotopia", "i need to do the sunken city", "whats after wizard city",
    ];

    public static readonly string[] Level15To25 = [
        "krokotopia is so big", "i keep getting lost in the pyramid", "the sand here is so annoying", "kroks are so hard",
        "manders hit so hard", "i need to get to marleybone", "almost done with krokotopia", "the tomb is so creepy",
    ];

    public static readonly string[] Level26To40 = [
        "marleybone was so long", "i love the dogs in marleybone", "mooshu is so pretty", "the oni are so hard",
        "i need to get to mooshu", "i finally got to mooshu", "my deck finally works", "anyone done the jade palace",
    ];

    public static readonly string[] Level41To50 = [
        "dragonspyre is so hard", "almost to malistaire", "who wants to help with malistaire", "the basilica is huge",
        "i need better gear for dragonspyre", "this fight took forever", "i think im done with arc one lol",
        "anyone else waiting for the next world",
    ];

    public static readonly Dictionary<AmbientSchool, string[]> School = new() {
        [AmbientSchool.Fire] = [
            "fire is the best school", "fire wizards rule", "fire is hot lol", "i love my fire cat", "fire elf go",
            "anyone else fire", "fire beats ice any day", "my fire spells keep missing", "i love my helephant",
            "fire wizards are so cool", "my robe is all red lol",
        ],
        [AmbientSchool.Ice] = [
            "ice is the best school", "ice wizards are so tough", "i never lose cuz im ice", "frost beetle go",
            "anyone else ice", "ice beats fire", "my ice shields save me every time", "ice is so slow but so strong",
            "my robe is all blue lol", "i love my snow serpent",
        ],
        [AmbientSchool.Storm] = [
            "storm is the best school", "storm hits so hard", "thunder snake go", "storm wizards rule", "i fizzle so much lol",
            "anyone else storm", "storm is great when it hits lol", "i love my lightning bats", "storm power",
            "my robe is all purple lol",
        ],
        [AmbientSchool.Myth] = [
            "myth is the best school", "blood bat go", "i love my minion", "myth wizards are so cool", "anyone else myth",
            "my minion is my best friend", "i love my troll", "cyclops is my favorite spell", "myth is so fun",
            "my robe is all yellow lol",
        ],
        [AmbientSchool.Life] = [
            "life is the best school", "i heal everyone lol", "need a heal", "imp go", "anyone need a heal",
            "life wizards are so nice", "anyone else life", "i love my unicorn spell", "fairy for everyone",
            "my robe is all green lol",
        ],
        [AmbientSchool.Death] = [
            "death is the best school", "death is so cool", "dark sprite go", "ghoul is my favorite spell", "anyone else death",
            "death wizards are not evil lol", "i love the death school", "vampire is so cool", "my robe is all black lol",
            "spooky", "death school rocks",
        ],
        [AmbientSchool.Balance] = [
            "balance is the best school", "balance is so hard to play", "scarab go", "anyone else balance", "i love sandstorm",
            "balance has the best spells", "balance wizards rule", "nobody picks balance lol", "my robe is all orange lol",
            "balance is so cool",
        ],
    };

    public static readonly Dictionary<string, string[]> Time = new() {
        ["morning"] = ["good morning everyone", "morning", "up early lol", "anyone else playing before school", "i should eat breakfast lol"],
        ["afternoon"] = ["just got home from school", "good afternoon", "school was so long today", "anyone just get home"],
        ["evening"] = ["good evening", "dinner is soon", "just ate dinner", "almost bedtime lol", "my mom says i can play one more hour"],
        ["night"] = ["its so late lol", "i should be asleep lol", "my mom thinks im asleep lol", "anyone else up late", "so tired",
            "one more quest then sleep", "good night everyone"],
        ["weekend"] = ["yay weekend", "no school tomorrow", "i get to play all day", "best weekend ever", "saturday questing"],
    };

    // ---- on their own: by zone (zone path fragment) ---------------------------------------------

    public static readonly (string Key, string[] Lines)[] Zone = [
        ("WC_Hub", [
            "the commons is so busy", "so many people here lol", "everyone stands around the commons", "the headmaster is right here",
            "gamma is so cool", "anyone wanna go to ravenwood", "lets go to unicorn way", "anyone heading to the shopping district",
            "why is everyone standing here lol", "the commons is my favorite place", "anyone wanna go to the arena",
            "i love the music in the commons", "i always come back here lol", "this fountain is so pretty",
            "anyone wanna race to ravenwood", "i like seeing everyone here",
        ]),
        ("WC_Unicorn", [
            "watch out for the ghosts", "these ghosts are everywhere", "stay on the sidewalk", "unicorn way is spooky",
            "i need more lost souls", "anyone know where rattlebones is", "who wants to do lady blackhope",
            "i need to find the haunted cave", "so many street fights here", "lady oriel is at the end of the street",
            "private stillson told me to come here", "anyone killed rattlebones yet", "i got jumped by a ghost again lol",
            "these skeletons are so annoying", "anyone need lost souls",
        ]),
        ("WC_Triton", [
            "triton avenue is so confusing", "anyone seen foulgaze", "i need help with foulgaze", "these fish things are everywhere",
            "where is the sunken city", "anyone wanna do the kraken", "i keep getting lost here", "triton is so pretty though",
        ]),
        ("WC_Firecat", [
            "firecat alley is so hot", "these fire elementals are so annoying", "anyone need help in firecat alley",
            "who wants to do lord nightshade", "so many fire things here", "this street is so creepy at night",
        ]),
        ("WC_Cyclops", [
            "cyclops lane is so hard", "the cyclops hit so hard", "anyone wanna team up here", "i keep dying here lol",
            "these golems are tough", "anyone know where to go on cyclops lane",
        ]),
        ("WC_Colossus", [
            "colossus boulevard is new right", "this street is huge", "anyone wanna team up on colossus", "these golems are big",
        ]),
        ("WC_OldeTown", [
            "olde town is so cool", "anyone going to the bazaar", "the bazaar has cool stuff", "i need to sell my stuff",
            "the arena is right here", "anyone wanna duel at the arena", "olde town is so pretty", "my backpack is so full",
        ]),
        ("AuctionHouse", [
            "anyone know whats good to buy here", "i sold so much junk lol", "how much is that hat", "its so expensive",
            "i need to sell my stuff", "anyone selling a good wand", "everything is so expensive here", "found a cool robe",
            "the bazaar has everything", "my backpack was so full", "i just sold a bunch of hats", "check the treasure cards",
            "i need gold for that", "anyone know when new stuff comes in", "i come here every day lol", "so many people here",
        ]),
        ("WC_Shop_Area", [
            "saving up for a new hat", "this robe costs so much", "i need a better wand", "shopping is fun", "new boots",
            "which hat should i get", "everything here costs too much lol", "i only have enough for boots", "anyone know a good shop",
            "the pet shop is cool", "i want everything here lol", "i need to sell stuff first",
        ]),
        ("WC_Ravenwood", [
            "ravenwood is so pretty", "the tree is so cool", "i have to go see my teacher", "which school is yours",
            "the {school} school is over there", "i love the big tree", "anyone know where the library is",
            "time to train some spells", "my teacher gave me a new spell", "anyone wanna go back to the commons",
        ]),
        ("WC_HauntedCave", [
            "lady blackhope is in here", "this cave is so creepy", "anyone wanna help with lady blackhope", "this cave is so dark",
            "stay together guys", "she hits so hard", "who wants to fight lady blackhope",
        ]),
        ("Hatchery", [
            "anyone wanna hatch", "my pet is an adult now", "whats your pet", "i want to hatch my pet",
            "anyone have a cool pet to hatch with", "hatching is so cool", "i wonder what pet i will get", "my pet is so cute",
            "can i hatch with you", "how does hatching work", "i trained my pet so much", "i love the pet pavilion",
        ]),
        ("PET_Park", [
            "anyone wanna hatch", "my pet is an adult now", "whats your pet", "hatching is so cool", "my pet is so cute",
            "i trained my pet so much", "i love the pet pavilion", "can i hatch with you",
        ]),
        ("KT_Hub", [
            "krokotopia is so hot", "the sand gets everywhere", "watch out for the kroks", "the pyramid of the sun is huge",
            "manders hit so hard", "the krokosphinx is so big", "anyone wanna do the pyramid", "i love krokotopia",
            "i keep getting lost in krokotopia", "who wants to team up for the tomb",
        ]),
        ("MB_Hub", [
            "marleybone is so foggy", "the dogs here are so funny", "regents square is busy", "meowiarty is up to no good",
            "jolly good lol", "i love the hats in marleybone", "anyone wanna team up in marleybone", "i love the music here",
            "the cats are so mean", "everyone here talks funny lol",
        ]),
        ("MS_Hub", [
            "mooshu is so pretty", "the jade palace is beautiful", "watch out for the oni", "i love mooshu",
            "the emperor is sick", "anyone wanna team up in mooshu", "these samoorai are so cool", "mooshu music is the best",
        ]),
        ("DS_Hub", [
            "dragonspyre is so hot", "the basilica is huge", "watch out for the drakes", "malistaire went this way",
            "dragonspyre is so hard", "anyone wanna team up", "its so dark here", "almost done with dragonspyre",
        ]),
        ("GH_Hub", [
            "grizzleheim is so cold", "the bears are so cool", "watch out for the ravens", "northguard is so pretty",
            "i love the bears here", "anyone wanna team up in grizzleheim",
        ]),
    ];

    // ---- what they are doing --------------------------------------------------------------------

    public static readonly string[] Hunting = [
        "need more {mob} lol", "where are all the {mob}", "ugh another fight", "these street fights are so annoying",
        "almost done with this quest", "one more to go", "anyone know where to find {mob}", "why do i keep getting pulled in",
        "so many fights lol", "i just need one more", "this is taking forever", "i keep running into fights",
        "where do the {mob} spawn", "ok one more", "hunting {mob} lol", "anyone else hunting {mob}",
    ];

    public static readonly string[] Shopping = [
        "hmm what should i buy", "too expensive", "i can't afford anything lol", "this looks cool", "i want this hat",
        "maybe next time", "need more gold", "ooh nice robe", "is this good for my level", "should i get the boots or the hat",
    ];

    public static readonly string[] Following = ["wait for me", "where are we going", "im right behind you", "slow down lol", "coming"];

    public static readonly string[] AfterWin = [
        "gg", "gg everyone", "yay", "we did it", "that was fun", "good fight", "nice one", "woot", "easy lol", "phew",
        "that was close", "yes finally", "ty for the help", "great teamwork", "good game", "that was awesome",
    ];

    public static readonly string[] AfterWinGrownup = ["Good fight, everyone!", "Nicely done.", "Thanks for the help!", "Great teamwork!"];

    public static readonly string[] AfterLoss = [
        "aw man", "that was so hard", "ugh i lost", "i need to heal", "why do i always fizzle", "i almost had it",
        "nooo", "that mob was too strong lol", "i need a better deck", "i need potions", "that was not fair lol",
        "i ran out of mana", "back to the commons lol",
    ];

    public static readonly string[] BossDoor = [
        "anyone wanna do {boss} with me", "need help with {boss}", "who wants to do {boss}", "can someone help me beat {boss}",
        "anyone done {boss} yet", "is {boss} hard", "i keep losing to {boss}", "team up for {boss} plz",
        "{boss} is so hard", "anyone here for {boss}",
    ];

    public static readonly string[] Arrived = ["hi everyone", "hey", "hi", "hello", "im back", "hi all", "hey guys"];

    public static readonly string[] Leaving = [
        "bye everyone", "gtg", "gtg bye", "i have to go", "bye guys", "ttyl", "my mom says i have to go", "brb", "dinner bye",
        "bedtime bye", "cya", "see you tomorrow",
    ];

    // ---- answers to people ----------------------------------------------------------------------

    public static readonly string[] Greet = [
        "hi", "hey", "hello", "hi {name}", "hey {name}", "hiya", "heya", "hi there", "hey whats up", "hi {name} :)", "hello {name}",
        "oh hi", "sup", "howdy", "hi {name} whats up",
    ];

    public static readonly string[] GreetNear = [
        "hi", "hey", "hi {name}", "hey {name}", "hello", "nice hat", "hi wanna quest", "hey wanna team up", "cool robe",
        "hi {name} :)", "whats up", "hey {name} what school are you", "hello {name}",
    ];

    public static readonly string[] HowAreYou = [
        "good you", "good", "pretty good", "im good", "good just questing", "bored lol", "tired", "great", "ok i guess",
        "good how about you", "awesome", "fine", "good just got a new hat",
    ];

    public static readonly string[] LevelNoNumber = [
        "not very high lol", "pretty low", "almost done with wizard city", "high enough lol", "im still kind of new",
        "a little higher than you i think", "why", "almost to the next world", "higher than my brother lol",
    ];

    public static readonly string[] LevelNumber = ["{level}", "level {level}", "{level} you", "im {level}", "{level} almost {next}"];

    public static readonly string[] SchoolAnswer = [
        "{school}", "im {school}", "{school} you", "{school} of course", "{school} lol", "im a {school} wizard", "{school} what about you",
    ];

    public static readonly string[] AgeAnswer = [
        "im not supposed to say lol", "my mom says not to tell", "secret lol", "why", "old enough lol", "im not telling",
    ];

    public static readonly string[] NameAnswer = ["{me}", "its {me}", "im {me}", "{me} lol", "my name is {me}"];

    public static readonly string[] ComplimentThanks = ["ty", "thanks", "thx", "ty i like yours too", "ty :)", "aw thanks", "thanks i got it today"];

    public static readonly string[] GoldBeg = [
        "i dont have much gold either lol", "sorry im saving up", "no sorry", "i need gold too lol", "nope sorry",
        "sell stuff at the bazaar", "beat monsters for gold",
    ];

    public static readonly string[] NotABot = ["lol what", "what", "lol no", "huh", "lol"];

    public static readonly string[] Laugh = ["lol", "haha", "lolz", "hehe", "lol yeah", "haha yeah", ":D"];

    public static readonly string[] Agree = ["yeah", "ya", "yep", "true", "same", "me too", "i know right", "totally", "yes"];

    public static readonly string[] Busy = [
        "can't right now sorry", "maybe later", "im doing a quest right now", "sorry im busy", "after this quest ok",
        "in a little bit", "not right now",
    ];

    public static readonly string[] Unsure = ["idk", "i dont know", "not sure", "hmm", "no idea lol", "dunno", "idk sorry"];

    public static readonly string[] Come = ["ok coming", "where", "ok where are you", "on my way", "sure", "ok"];

    public static readonly string[] Teleport = [
        "use teleport to friend", "add me and port to me", "i can't port right now", "port to me", "you have to be my friend to port",
    ];

    public static readonly string[] HowToHatch = [
        "go to the pet pavilion", "your pet has to be an adult", "you need an adult pet", "ask someone at the pet pavilion",
    ];

    public static readonly string[] HowToGold = [
        "beat monsters", "sell stuff at the bazaar", "do quests", "street fights give gold", "sell your old gear",
    ];

    public static readonly string[] Trade = [
        "you can sell at the bazaar", "i dont have anything good lol", "what do you want", "sorry nothing to trade",
    ];

    public static readonly string[] Duel = [
        "sure lets go to the arena", "maybe later", "lol i would lose", "after this quest", "ok practice match", "not right now",
        "i need a better deck first", "sure meet me at the arena",
    ];

    public static readonly string[] Friend = ["sure", "ok add me", "yeah sure", "ok", "sure send it", "sure ill accept", "yes"];

    public static readonly string[] Thanks = ["np", "no problem", "anytime", "you're welcome", "yw", "np :)", "sure", "no prob"];

    public static readonly string[] Bye = ["bye", "cya", "bye {name}", "see ya", "ttyl", "bye have fun", "later", "cya {name}"];

    public static readonly string[] Fallback = [
        "lol", "cool", "oh", "ok", "hmm", "haha", "yeah", "really", "nice", "oh ok", "same", "true", "ooh", "wow", "idk",
        "lol what", "huh",
    ];

    public static readonly string[] HelpSure = [
        "sure", "ok what do you need", "sure where are you", "ok start the fight and ill join", "yeah i can help",
        "sure what quest", "ok just say yes when i ask",
    ];

    public static readonly string[] QuestTogether = [
        "sure", "ok where are you", "sure what quest", "can't right now sorry", "maybe later", "ok", "sure lets team up",
        "what level are you", "i have to finish mine first", "sure im in {zone}",
    ];

    public static readonly string[] Doing = [
        "questing", "just questing", "nothing much", "hunting", "trying to level", "waiting for my friend", "shopping",
        "just walking around", "doing quests", "bored lol", "looking for people to quest with",
    ];

    // ---- two wizards talking --------------------------------------------------------------------

    public static readonly ChatExchange[] Exchanges = [
        new("hi", [], ["A:hi|hey|hello", "B:hi|hey|heya|hi {a}", "A:whats up|wanna quest|what school are you",
            "B:nothing|sure|{bschool}|not much lol"]),
        new("school", [], ["A:what school are you|whats your school", "B:{bschool}|im {bschool}", "A:cool im {aschool}|nice|{aschool} here",
            "B:cool|nice|{aschool} is cool too"]),
        new("help-boss", ["WC_Unicorn", "WC_HauntedCave", "WC_Triton", "WC_Firecat", "KT_", "MB_", "MS_", "DS_"],
            ["A:anyone wanna help me with {boss}|can someone help me with {boss}|need help with {boss} plz",
             "B:sure|i can help|ok", "A:yay ty|thanks|ok meet me there", "B:np|on my way|ok coming"]),
        new("help-boss-no", ["WC_Unicorn", "WC_HauntedCave", "WC_Triton", "KT_", "MB_"],
            ["A:anyone wanna do {boss}|who wants to do {boss}", "B:i already did it sorry|can't right now|maybe later",
             "A:ok|aw ok|ok np"]),
        new("gold", [], ["A:can someone give me gold|anyone have extra gold", "B:no|sorry i need it too|beat monsters lol",
            "A:aw|ok|lol ok"]),
        new("friend", [], ["A:wanna be friends|add me", "B:sure|ok", "A:sent|ok i sent it|yay", "B:ok|got it|:)"]),
        new("hat", [], ["A:nice hat|i like your hat|cool hat", "B:ty|thanks|thx i just got it", "A:where did you get it",
            "B:the shopping district|it dropped from a boss|i dont remember lol"]),
        new("lost", [], ["A:where do i go now|im lost", "B:follow your quest arrow|check your map|what quest",
            "A:ok ty|oh ok thanks|lol ok"]),
        new("brb", [], ["A:brb", "B:k|ok", "A:back", "B:welcome back|ok"]),
        new("duel", ["WC_Hub", "WC_OldeTown"], ["A:anyone wanna duel|wanna practice duel", "B:sure|ok|lol i would lose",
            "A:meet me at the arena|ok come on", "B:ok|on my way"]),
        new("level", [], ["A:what level are you", "B:not very high|almost done with wizard city|pretty low lol",
            "A:same|me too|cool"]),
        new("pet", [], ["A:i love your pet|cute pet", "B:ty|thanks|thx its my favorite", "A:i want one",
            "B:you can get them at the pet shop|check the crown shop|some drop from bosses"]),
        new("hatch", ["Hatchery", "PET_Park"], ["A:anyone wanna hatch", "B:sure whats your pet|me", "A:my {pet}|a {pet}",
            "B:cool lets do it|ok send it|ooh nice"]),
        new("bazaar", ["AuctionHouse", "WC_OldeTown"], ["A:anyone know if the bazaar has good wands", "B:sometimes|check every day|idk",
            "A:ok ty"]),
        new("school-fight", [], ["A:{aschool} is the best school", "B:no {bschool} is|nah {bschool} is better|lol no",
            "A:lol no way|whatever lol|haha"]),
        new("night", [], ["A:i have to go soon|my mom says i have to go soon", "B:aw|me too|same",
            "A:see you tomorrow|bye|cya tomorrow", "B:bye|cya"]),
        new("unicorn", ["WC_Unicorn"], ["A:where is the haunted cave", "B:down the street|follow the arrow|near lady oriel",
            "A:ty|ok thanks"]),
        new("krok", ["KT_"], ["A:the pyramid is so long", "B:yeah|i know right|it took me forever", "A:lol"]),
        new("new", ["WC_Hub", "WC_Ravenwood", "WC_Unicorn"], ["A:im new", "B:welcome|hi welcome|cool what school",
            "A:{aschool}|ty|thanks"]),
    ];

    // ---- lookups --------------------------------------------------------------------------------

    private static readonly (string Key, string[] Bosses)[] s_bosses = [
        ("WC_Unicorn", ["Rattlebones", "Lady Blackhope"]), ("WC_HauntedCave", ["Lady Blackhope"]),
        ("WC_Triton", ["Foulgaze", "the Kraken"]), ("WC_Firecat", ["Lord Nightshade"]),
        ("KT_", ["Krokopatra", "the Krokosphinx boss"]), ("MB_", ["Meowiarty"]), ("MS_", ["the Jade Oni"]), ("DS_", ["Malistaire"]),
    ];

    private static readonly (string Key, string[] Mobs)[] s_mobs = [
        ("WC_Unicorn", ["ghosts", "skeletons", "lost souls"]), ("WC_HauntedCave", ["ghosts", "skeletons"]),
        ("WC_Triton", ["fish things", "golems"]), ("WC_Firecat", ["fire elves", "fire elementals"]),
        ("WC_Cyclops", ["cyclops", "golems"]), ("WC_Colossus", ["golems", "trolls"]), ("KT_", ["kroks", "manders", "mummies"]),
        ("MB_", ["cats", "rats"]), ("MS_", ["oni", "samoorai"]), ("DS_", ["drakes", "skeletons"]), ("GH_", ["ravens", "trolls"]),
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
    /// The templates a wizard may say on its own at <paramref name="moment"/>, most fitting first in weight (zone and moment
    /// lines are listed twice). Menu-chat wizards get menu phrases only.
    /// </summary>
    public static List<string> Solo(ChatMoment moment, string? zone, int level, AmbientSchool school, int hour, ChatPersona persona,
                                    DayOfWeek? day = null) {
        ArgumentNullException.ThrowIfNull(persona);
        if (persona.Channel == ChatChannel.Menu) {
            return moment == ChatMoment.AfterWin ? [.. MenuAfterWin] : [.. MenuIdle];
        }

        var pool = new List<string>();
        switch (moment) {
            case ChatMoment.Hunting:
                pool.AddRange(Hunting);
                pool.AddRange(Hunting);
                pool.AddRange(ForZone(zone));
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
                pool.AddRange(persona.Grownup ? Grownup : Kid);
                if (persona.Grownup && persona.Numbers) {
                    pool.AddRange(GrownupLevel);
                }

                pool.AddRange(ForZone(zone));
                pool.AddRange(ForZone(zone));
                pool.AddRange(ForLevel(level));
                pool.AddRange(School[school]);
                pool.AddRange(ForTime(hour, day));
                if (BossesFor(zone).Length > 0) {
                    pool.AddRange(BossDoor.Take(4));
                }

                break;
        }

        return pool;
    }

}
