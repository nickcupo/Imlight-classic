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
 * AMBIENT CHAT VOCABULARY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-10): the words the ambient wizards' lines use, every
 * one checked to be in the r806919 client's chat dictionary
 * (AmbientChatVoiceTests.TheCommittedVocabularyIsInTheClientDictionary,
 * with W101C_CHATFILTER_DIR). This is our own list of the words our lines
 * use, not KingsIsle's files, so CI can check every line without them.
 * Regenerate with W101C_WRITE_VOCAB=<this file> after changing lines,
 * then run the dictionary test with the client lists.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Imlight.Classic.Tests;

internal static class AmbientChatVocabulary {

    /// <summary>The words (lower case).</summary>
    public static readonly IReadOnlySet<string> Words = new HashSet<string>(StringComparer.Ordinal) {
        "a", "above", "actually", "add", "added", "adult", "afford", "afk", "after", "again", "ages", "ago", "ahead",
        "akilles", "all", "alley", "almost", "already", "always", "am", "an", "and", "annoying", "another", "any",
        "anyone", "anything", "anyway", "are", "arena", "arent", "armor", "around", "arrow", "arthur", "ashfriend",
        "ashthorn", "ask", "asleep", "at", "ate", "atheneum", "attack", "avenue", "aw", "awesome", "aww", "awww",
        "back", "backpack", "bad", "balance", "balanceblade", "balestrom", "banshee", "barking", "bartleby", "basilica",
        "bat", "bats", "bazaar", "be", "bears", "beat", "beating", "bedtime", "beetle", "before", "beg", "behind",
        "being", "ben", "best", "better", "big", "blackhope", "blade", "blast", "blizzard", "blood", "boots", "bored",
        "boring", "boss", "bosses", "boulevard", "brb", "breakfast", "bring", "broken", "brother", "btw", "burns",
        "busy", "but", "button", "buy", "by", "bye", "calling", "can", "can't", "cannot", "card", "cards", "care",
        "cat", "cats", "cause", "cave", "centaur", "cheated", "cheats", "check", "chelsea", "city", "clean", "clicking",
        "clockworks", "close", "cold", "colossus", "come", "coming", "commons", "computer", "confusing", "cool", "cost",
        "costs", "could", "couple", "court", "cows", "creepy", "crowns", "curse", "cursed", "cute", "cuz", "cya",
        "cyclops", "cyrus", "dads", "dalia", "damage", "dark", "daughter", "day", "death", "deck", "defeat", "did",
        "didnt", "dinner", "district", "do", "does", "doesnt", "dog", "dogs", "doing", "don't", "done", "dont", "door",
        "dorm", "down", "dragon", "dragonspyre", "drake", "drakes", "drawing", "drop", "dropped", "duel", "duncan",
        "dunno", "earlier", "earthquake", "easy", "eat", "eels", "egg", "elemental", "elf", "else", "elves", "emperor",
        "end", "enough", "even", "ever", "every", "everybody", "everyone", "everything", "everywhere", "evil", "ew",
        "expensive", "extra", "fair", "fairies", "falmea", "fancy", "far", "favorite", "few", "fight", "fights",
        "figuring", "finally", "fine", "finish", "fire", "firecat", "first", "fixing", "fizzle", "fizzled", "fizzling",
        "fodder", "foggy", "follow", "for", "forever", "forth", "foulgaze", "found", "fountain", "friend", "friends",
        "frog", "from", "frost", "full", "fun", "funny", "game", "gamma", "gave", "gear", "general", "get", "gets",
        "getting", "gg", "ghosts", "ghoul", "giant", "give", "go", "gobblers", "gobblestone", "going", "gold", "golem",
        "gonna", "good", "goodbye", "got", "gotta", "great", "grettir", "greyrose", "grizzleheim", "gross", "group",
        "grr", "grumpy", "gtg", "guess", "guy", "guys", "ha", "had", "haha", "hai", "half", "hall", "halston", "hand",
        "hard", "has", "hat", "hatch", "hatched", "hatching", "hats", "haunted", "have", "head", "headmaster", "heal",
        "heals", "health", "heh", "hehe", "helephant", "hello", "help", "helped", "here", "hey", "heya", "hi", "high",
        "higher", "highest", "him", "his", "hit", "hits", "hiya", "hmm", "hold", "home", "homework", "hooray", "hour",
        "how", "huge", "huh", "humongofrog", "hundred", "hungry", "hunting", "hurry", "hydra", "i", "i'll", "i'm",
        "ice", "idea", "idk", "if", "ill", "im", "imp", "in", "into", "is", "isnt", "it", "its", "itself", "jade", "jk",
        "join", "joining", "jotun", "judgement", "jumped", "junk", "just", "k", "kagemoosha", "katzenstein",
        "katzenstein's", "keep", "keeps", "kewl", "kid", "kids", "kinda", "kings", "know", "knows", "kraken", "krok",
        "krokopatra", "krokosphinx", "krokotopia", "kroks", "lab", "lady", "lane", "late", "later", "lead", "leading",
        "leave", "lemme", "leprechaun", "let", "let's", "lets", "level", "leveled", "levels", "lfg", "library",
        "lightning", "like", "link", "listen", "little", "locust", "log", "lol", "lolz", "long", "look", "looks",
        "lord", "lose", "lost", "loud", "love", "low", "luck", "lvl", "lydia", "made", "magma", "makes", "making",
        "malistaire", "malorn", "mana", "manders", "many", "map", "marleybone", "match", "matter", "maybe", "me",
        "mean", "meet", "meh", "men", "meowiarty", "meteor", "middle", "million", "mind", "mine", "minion", "minotaur",
        "minutes", "misses", "mob", "mobs", "mom", "moolinda", "mooshu", "more", "mount", "mounts", "much", "mummies",
        "music", "my", "myself", "nah", "name", "need", "needs", "never", "new", "next", "nice", "nicely", "nicest",
        "nick", "night", "nightshade", "nightside", "no", "nobody", "nooo", "noooo", "nope", "normal", "northguard",
        "not", "nothing", "nova", "now", "np", "nvm", "oasis", "obviously", "of", "off", "office", "oh", "ok", "okay",
        "old", "olde", "on", "one", "oni", "only", "ooh", "oops", "open", "or", "oriel", "orthrus", "ouch", "out",
        "over", "owl", "own", "palace", "past", "pause", "pavilion", "people", "pet", "phew", "phoenix", "picked",
        "pips", "pirate", "plague", "play", "playing", "plays", "please", "pls", "plz", "point", "pointing", "points",
        "port", "potions", "power", "practice", "pretty", "prince", "prob", "problem", "prolly", "pull", "pulled",
        "pulling", "puppy", "put", "pvp", "pyramid", "quest", "questing", "quests", "quick", "race", "raining", "ran",
        "ranked", "rare", "rats", "rattlebones", "ravens", "ravenwood", "ready", "real", "really", "rebirth",
        "remember", "resist", "right", "rip", "robe", "robes", "rofl", "room", "rotting", "rough", "rude", "said",
        "same", "samoorai", "sand", "sandstorm", "saturday", "satyr", "saved", "saves", "saving", "say", "says",
        "scarab", "scarecrow", "scary", "school", "scorpion", "screamers", "sec", "second", "secret", "see", "sell",
        "sells", "send", "sending", "sent", "seraph", "serpent", "set", "shark", "shield", "shields", "shop",
        "shopping", "should", "sick", "sidewalk", "sigil", "sister", "sit", "skeletal", "skeletons", "sleep", "slow",
        "snake", "snow", "snowman", "snowmen", "so", "sold", "some", "somebody", "someone", "something", "sometimes",
        "somewhere", "son", "soon", "soooo", "sorry", "souls", "spare", "spectral", "spell", "spellbook", "spells",
        "spots", "sprite", "spyre", "srsly", "sry", "stand", "standing", "stands", "start", "started", "starting",
        "stay", "still", "stillson", "stop", "stopped", "stops", "storm", "storms", "stormzilla", "street", "stuck",
        "stuff", "sun", "sunbird", "sunken", "sup", "sure", "swarm", "take", "takes", "taking", "talk", "talking",
        "talks", "tall", "teacher", "team", "teamwork", "tell", "telling", "tempest", "terrible", "than", "thank",
        "thanks", "thanx", "that", "thats", "the", "them", "then", "there", "these", "they", "thier", "things", "think",
        "this", "thousand", "thunder", "thx", "time", "times", "tiny", "tired", "to", "today", "together", "tomb",
        "tomorrow", "ton", "tonight", "too", "took", "tougher", "tower", "town", "trade", "trading", "train", "trained",
        "training", "treasure", "triton", "troll", "trolls", "true", "try", "trying", "ttyl", "turn", "ty", "type",
        "ugh", "um", "unicorn", "unlucky", "up", "us", "use", "vampire", "village", "wait", "waiting", "walked", "wall",
        "wand", "wands", "wanna", "want", "wants", "warhorns", "was", "wasn't", "wasnt", "watching", "way", "we",
        "weak", "weird", "welcome", "went", "were", "wethersfield", "what", "what's", "whatever", "whats", "when",
        "where", "wheres", "which", "who", "whole", "why", "wife", "will", "wings", "wish", "with", "without", "wiz",
        "wizard", "wolves", "won", "wonder", "wont", "woot", "work", "works", "worst", "worth", "would", "wow",
        "wraith", "wrong", "wu", "wyvern", "xd", "ya", "yay", "yea", "yeah", "yep", "yes", "yet", "yo", "you", "you're",
        "young", "your", "yours", "yup", "yw",
    };

    /// <summary>Writes this file anew with <paramref name="words"/> (W101C_WRITE_VOCAB).</summary>
    public static void Write(string path, IEnumerable<string> words) {
        var text = File.ReadAllText(path);
        var start = text.IndexOf("StringComparer.Ordinal) {", StringComparison.Ordinal) + "StringComparer.Ordinal) {".Length;
        var end = text.IndexOf("    };", start, StringComparison.Ordinal);
        var body = new StringBuilder("\n");
        var line = new StringBuilder("       ");
        foreach (var word in words.Select(w => w.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)) {
            var item = $" \"{word}\",";
            if (line.Length + item.Length > 120) {
                body.Append(line).Append('\n');
                line.Clear().Append("       ");
            }

            line.Append(item);
        }

        body.Append(line).Append('\n');
        File.WriteAllText(path, text[..start] + body + text[end..]);
    }

}
