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
 * AMBIENT CHAT VOICE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-10, owner: "rework the chat completely of the friendly
 * wizards so its more natural"): the reworked voice holds.
 *   - Vocabulary (runs everywhere, CI included): every word of every line
 *     pool, in every ChatStyle spelling and temperament, is in
 *     AmbientChatVocabulary, our committed list of the dictionary words
 *     the wizards use (our own word list, not KingsIsle's files). With
 *     W101C_CHATFILTER_DIR set, every word of that list is checked
 *     against the client's own dictionary, so a line can only go in when
 *     both agree. W101C_WRITE_VOCAB=<file> writes the list anew.
 *   - Variety: no line is one school's or zone's template with the name
 *     swapped; pools do not lean on one sentence shape.
 *   - Temperament: personas spread over the kinds; shy ones ignore and
 *     answer short more, chatty ones wander off topic; lines a player
 *     waits on are always answered.
 *   - Memory: a wizard tells its thread in order and repeats itself on
 *     "what?".
 *   - Transcripts: SampleTranscripts prints a few minutes of generated
 *     chat per scene (W101C_TRANSCRIPT_OUT=<file> also writes them) for
 *     the owner to judge the feel.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>Every line the ambient wizards can say, for the tests.</summary>
internal static class AmbientChatCorpus {

    private static readonly Type[] s_lineTypes = [typeof(AmbientLinePool), typeof(AmbientLines), typeof(DungeonLines), typeof(GroupLines), typeof(AmbientPets)];

    // Zone keys and other names inside the pools that are lookups, not lines.
    private static readonly HashSet<string> s_notLines = new(StringComparer.Ordinal) { "morning", "afternoon", "evening", "night", "weekend" };

    /// <summary>Slot values for filling templates in tests (all dictionary words).</summary>
    public static Dictionary<string, string> Slots() => new() {
        ["boss"] = "rattlebones", ["mob"] = "lost souls", ["pet"] = "fire cat", ["a"] = "Duncan", ["b"] = "Ryan", ["aschool"] = "fire",
        ["bschool"] = "storm", ["me"] = "Duncan Ashfriend", ["what"] = "Lady Blackhope", ["where"] = "Unicorn Way", ["spell"] = "meteor",
        ["last"] = "need gold", ["quest"] = "Rattlebones", ["ago"] = "earlier", ["stage"] = "adult", ["name"] = "Nick",
    };

    /// <summary>Every template of every line pool (public or private), the exchanges' turns and the threads' steps.</summary>
    public static IEnumerable<string> Templates() {
        foreach (var type in s_lineTypes) {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (field.FieldType == typeof(Regex) || field.FieldType == typeof(Regex[])) {
                    continue;
                }

                if (field.IsLiteral && field.FieldType == typeof(string)) {
                    yield return (string) field.GetRawConstantValue()!;
                    continue;
                }

                foreach (var text in Strings(field.GetValue(null))) {
                    yield return text;
                }
            }
        }

        yield return AmbientChatBrain.MenuHelpOffer;
        foreach (var text in new[] { "noob", "hi", "where is lady blackhope", "how old are you", "thanks", "nice hat", "give me gold",
                     "wanna duel", "can you help me", "add me", "anyone wanna quest", "follow me", "bye", "how are you", "lol", "ok", "what",
                     "brb", "fire is the best", "are you a bot", "whats your name", "what level are you", "what school are you" }) {
            if (AmbientChatBrain.Understand(text, new ChatContext("Duncan Ashfriend", AmbientSchool.Fire, 7, "Unicorn Way")) is { } intent) {
                foreach (var line in intent.Pool.Concat(intent.Menu ?? [])) {
                    yield return line;
                }
            }
        }
    }

    private static IEnumerable<string> Strings(object? value) {
        switch (value) {
            case null:
                yield break;
            case string text:
                if (!s_notLines.Contains(text) && !Regex.IsMatch(text, @"^(WC_|KT_|MB_|MS_|DS_|GH_|AuctionHouse$|Hatchery$|PET_Park$)")) {
                    yield return text;
                }

                break;
            case ChatExchange exchange:
                foreach (var turn in exchange.Turns) {
                    foreach (var option in turn[2..].Split('|', '/').Where(o => o.Length > 0)) {
                        yield return option;
                    }
                }

                break;
            case ChatThread thread:
                foreach (var step in thread.Steps) {
                    yield return step;
                }

                break;
            case ImmutableArray<string> array:
                foreach (var text in array) {
                    yield return text;
                }

                break;
            case IDictionary dictionary:
                foreach (var item in dictionary.Values) {
                    foreach (var text in Strings(item)) {
                        yield return text;
                    }
                }

                break;
            case System.Runtime.CompilerServices.ITuple tuple:
                for (var i = 0; i < tuple.Length; i++) {
                    foreach (var text in Strings(tuple[i])) {
                        yield return text;
                    }
                }

                break;
            case IEnumerable items:
                foreach (var item in items) {
                    foreach (var text in Strings(item)) {
                        yield return text;
                    }
                }

                break;
        }
    }

    /// <summary>A persona of every spelling, temperament, laugh and yes word, on dictionary chat.</summary>
    public static IEnumerable<ChatPersona> Typists() {
        var basis = ChatPersona.For(4, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary, Grownup = false };
        string[] laughs = ["lol", "haha", "hehe", "lolz", "heh", "xd"];
        string[] yeses = ["ya", "yea", "yeah", "yep", "yup", "ok", "yes", "sure"];
        string?[] smileys = [":)", ":D", ":P", null];
        var i = 0;
        foreach (var spelling in Enum.GetValues<ChatSpelling>()) {
            foreach (var kind in Enum.GetValues<ChatTemperament>()) {
                yield return basis with {
                    Spelling = spelling, Kind = kind, Laugh = laughs[i % laughs.Length], YesWord = yeses[i % yeses.Length],
                    Smiley = smileys[i % smileys.Length],
                };
                i++;
            }
        }
    }

}

public sealed class AmbientChatVoiceTests(ITestOutputHelper output) {

    private static readonly string[] s_smileys = [":)", ":D", ":P"];

    private static ChatWordFilter Vocabulary() => ChatWordFilter.FromLists(AmbientChatVocabulary.Words, smileys: s_smileys);

    private static ChatContext Ctx(string zone = "WizardCity/WC_Streets/WC_Unicorn", int level = 7, AmbientSchool school = AmbientSchool.Fire,
                                   LineHistory? history = null, string? speaker = "Nick Stormblade", string me = "Duncan Ashfriend")
        => new(me, school, level, AmbientKnowledgeName(zone), SpeakerName: speaker, ZoneKey: zone, Hour: 20, History: history, Audience: [42]);

    private static string AmbientKnowledgeName(string zone) => zone.Split('/').Last().Replace("WC_", "").Replace("_", " ");

    // Every line, filled, split at "||", styled every way (several rolls each).
    private static IEnumerable<(string Line, string From)> EveryStyledLine() {
        var open = ChatPersona.For(1, AmbientTemper.Friendly) with { Grownup = true, Channel = ChatChannel.Open };
        var speaker = new ChatSpeaker(open, Ctx(), ChatMoment.Idle, new ChatMemory());
        var rng = new Random(2010);
        foreach (var template in AmbientChatCorpus.Templates().Distinct()) {
            var filled = AmbientChatPlanner.Fill(template, speaker, AmbientChatCorpus.Slots()) ?? template;
            foreach (var line in filled.Split("||")) {
                yield return (line, template);
                foreach (var persona in AmbientChatCorpus.Typists()) {
                    for (var roll = 0; roll < 4; roll++) {
                        yield return (ChatStyle.Apply(line, persona, rng), $"{template} ({persona.Spelling}, {persona.Kind})");
                    }
                }
            }
        }
    }

    // ---- the dictionary -----------------------------------------------------------------------

    [Fact]
    public void EveryLineInEveryStyleUsesOnlyTheCommittedVocabulary() {
        var vocabulary = Vocabulary();
        var words = new SortedSet<string>(StringComparer.Ordinal);
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (line, from) in EveryStyledLine()) {
            foreach (var word in vocabulary.Refused(line, allowNumbers: true)) {
                failures.Add($"{word}: \"{line}\" from {from}");
            }

            var plain = string.Join(' ', line.Split(' ').Where(chunk => !s_smileys.Contains(chunk)));
            foreach (Match match in Regex.Matches(plain.ToLowerInvariant(), @"[a-z][a-z'\-]*[a-z]|[a-z]")) {
                words.Add(match.Value);
            }
        }

        foreach (var word in ChatStyle.StyleWords.Select(w => w.ToLowerInvariant())) {
            words.Add(word);
            if (!AmbientChatVocabulary.Words.Contains(word)) {
                failures.Add($"{word}: a ChatStyle word");
            }
        }

        if (Environment.GetEnvironmentVariable("W101C_WRITE_VOCAB") is { Length: > 0 } path) {
            AmbientChatVocabulary.Write(path, words);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} word(s) not in AmbientChatVocabulary (check them against the client dictionary, "
                                         + "then add them):\n" + string.Join("\n", failures.Take(80)));
    }

    [Fact]
    public void TheCommittedVocabularyIsInTheClientDictionary() {
        var dir = Environment.GetEnvironmentVariable("W101C_CHATFILTER_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
            return; // no client lists here (CI): the committed vocabulary was checked when it was written
        }

        var client = ChatWordFilter.Load(name => {
            var path = Path.Combine(dir, name.Replace('/', '_'));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        });
        Assert.True(client.HasDictionary);
        var refused = AmbientChatVocabulary.Words.Where(w => !client.Passes(w)).ToList();
        Assert.True(refused.Count == 0, "not in the client's chat dictionary: " + string.Join(", ", refused));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("ur")]
    [InlineData("omg")]
    [InlineData("kk")]
    [InlineData("gl")]
    [InlineData("ppl")]
    [InlineData("tho")]
    [InlineData("whoa")]
    [InlineData("cant")]
    [InlineData("bed")]
    [InlineData("four")]
    public void WordsTheClientHidesAreNotInTheVocabulary(string word) => Assert.DoesNotContain(word, AmbientChatVocabulary.Words);

    [Fact]
    public void ALineTheDictionaryRefusesIsNotSendable() {
        var kid = ChatPersona.For(4, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary };
        var vocabulary = Vocabulary();

        Assert.True(AmbientChatPlanner.Sendable("anyone wanna quest", kid, vocabulary));
        Assert.False(AmbientChatPlanner.Sendable("u there", kid, vocabulary));
        Assert.False(AmbientChatPlanner.Sendable("lvl 12", kid, vocabulary));
        Assert.True(AmbientChatPlanner.Sendable("lvl 12", kid with { Channel = ChatChannel.Open }, vocabulary));
        Assert.True(AmbientChatPlanner.Sendable("u there", kid, null)); // no lists loaded: IsClean alone
    }

    // ---- variety --------------------------------------------------------------------------------

    private static readonly string[] s_schoolWords = [
        "fire", "ice", "storm", "myth", "life", "death", "balance", "red", "blue", "purple", "yellow", "green", "black", "orange",
    ];

    private static string Shape(string line, IEnumerable<string> names) {
        var shape = " " + line.ToLowerInvariant() + " ";
        foreach (var name in names.OrderByDescending(n => n.Length)) {
            shape = shape.Replace(" " + name + " ", " X ");
        }

        return shape.Trim();
    }

    [Fact]
    public void NoLineIsOneSchoolsTemplateWithTheNameSwapped() {
        var spells = AmbientLinePool.SchoolLines.Values.SelectMany(v => v).Select(v => v.Line)
            .SelectMany(l => l.Split(' ')).Distinct().ToList();
        var shapes = AmbientLinePool.SchoolLines.SelectMany(kv => kv.Value.Select(v => (kv.Key, Shape: Shape(v.Line, s_schoolWords))))
            .GroupBy(x => x.Shape).Where(g => g.Select(x => x.Key).Distinct().Count() > 1).Select(g => g.Key).ToList();
        Assert.True(shapes.Count == 0, "the same line for several schools: " + string.Join("; ", shapes));
        _ = spells;
    }

    [Fact]
    public void NoLineIsOneZonesTemplateWithThePlaceSwapped() {
        var places = AmbientLinePool.Zone.SelectMany(z => z.Lines).SelectMany(l => l.Split(' ')).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var shared = AmbientLinePool.Zone.SelectMany(z => z.Lines.Select(l => (z.Key, Line: l)))
            .GroupBy(x => x.Line).Where(g => g.Select(x => x.Key).Distinct().Count() > 1 && !g.All(x => x.Key is "Hatchery" or "PET_Park"))
            .Select(g => g.Key).ToList();
        Assert.True(shared.Count == 0, "the same line in several zones: " + string.Join("; ", shared));
        _ = places;
    }

    [Fact]
    public void PoolsDoNotLeanOnOneSentenceShape() {
        var pools = new Dictionary<string, IReadOnlyList<string>> {
            ["Bored"] = AmbientLinePool.Bored, ["Begging"] = AmbientLinePool.Begging, ["Bragging"] = AmbientLinePool.Bragging,
            ["Griping"] = AmbientLinePool.Griping, ["Asking"] = AmbientLinePool.Asking, ["Social"] = AmbientLinePool.Social,
            ["Home"] = AmbientLinePool.Home, ["Bossy"] = AmbientLinePool.Bossy, ["Shy"] = AmbientLinePool.Shy, ["Newbie"] = AmbientLinePool.Newbie,
            ["Grownup"] = AmbientLinePool.Grownup, ["Hunting"] = AmbientLinePool.Hunting, ["AfterWin"] = AmbientLinePool.AfterWin,
            ["AfterLoss"] = AmbientLinePool.AfterLoss,
        };
        foreach (var zone in AmbientLinePool.Zone) {
            pools["zone " + zone.Key] = zone.Lines;
        }

        foreach (var (name, pool) in pools) {
            // "X is so Y", the old lines' favourite: a few at most.
            var isSo = pool.Count(l => Regex.IsMatch(l, @"\bis (so|soooo) \w+$"));
            Assert.True(isSo <= 3, $"{name}: {isSo} lines are \"X is so Y\"");

            // No two-word opening carries more than a third of a pool.
            if (pool.Count >= 8) {
                var top = pool.GroupBy(l => string.Join(' ', l.Split(' ').Take(2))).MaxBy(g => g.Count())!;
                Assert.True(top.Count() * 3 <= pool.Count, $"{name}: {top.Count()} of {pool.Count} lines start \"{top.Key}\"");
            }

            // Mostly short: at least half the lines are six words or fewer (a third for parents, who typed whole sentences).
            Assert.True(pool.Count(l => l.Split(' ').Length <= 6) * (name == "Grownup" ? 3 : 2) >= pool.Count, $"{name}: too many long lines");
        }
    }

    [Fact]
    public void NoWholesomeTourGuideLinesAreLeft() {
        string[] banned = [
            "being a wizard is the best", "great teamwork", "glad to help", "i was just thinking about you", "see you in the spiral",
            "good luck out there", "welcome to", "balance is the best of everything", "spooky fun", "safe for the kids",
            "how friendly everyone is", "happy to help", "let me know if you need help", "lives right here", "wizards rule",
            "my robe is all",
        ];
        foreach (var template in AmbientChatCorpus.Templates()) {
            foreach (var phrase in banned) {
                Assert.DoesNotContain(phrase, template.ToLowerInvariant());
            }
        }
    }

    // ---- temperament and replies ----------------------------------------------------------------

    [Fact]
    public void PersonasSpreadOverTheTemperaments() {
        var kinds = Enumerable.Range(0, 600).Select(seed => ChatPersona.For(seed, (AmbientTemper) (seed % 3), level: 1 + seed % 50).Kind)
            .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        foreach (var kind in Enum.GetValues<ChatTemperament>()) {
            Assert.True(kinds.GetValueOrDefault(kind) >= 20, $"{kind}: {kinds.GetValueOrDefault(kind)}");
        }

        Assert.All(Enumerable.Range(0, 200).Select(seed => ChatPersona.For(seed, AmbientTemper.Chatty)),
            p => Assert.Equal(p.Grownup, p.Kind == ChatTemperament.Parent));
        Assert.Equal(ChatPersona.For(77, AmbientTemper.Quiet, 3), ChatPersona.For(77, AmbientTemper.Quiet, 3));
    }

    [Fact]
    public void RepliesAreNotAlwaysStraightAnswers() {
        var small = AmbientChatBrain.Understand("lol", Ctx())!;
        var help = AmbientChatBrain.Understand("can you help me", Ctx())!;
        foreach (var kind in Enum.GetValues<ChatTemperament>()) {
            var persona = ChatPersona.For(5, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary, Kind = kind };
            var turns = Enumerable.Range(0, 100).Select(i => AmbientChatBrain.Plan(small, persona, addressed: true, i / 100.0)).ToList();
            var straight = turns.Count(t => t == AmbientChatBrain.ReplyTurn.Answer);
            Assert.InRange(straight, 40, 95);

            // A player waiting on a yes or no always gets one (help is never brushed off).
            Assert.All(Enumerable.Range(0, 100), i => Assert.Equal(AmbientChatBrain.ReplyTurn.Answer, AmbientChatBrain.Plan(help, persona, true, i / 100.0)));
        }

        var shy = ChatPersona.For(5, AmbientTemper.Quiet) with { Channel = ChatChannel.Dictionary, Kind = ChatTemperament.Shy };
        var chatty = shy with { Kind = ChatTemperament.Chatty };
        int Count(ChatPersona p, AmbientChatBrain.ReplyTurn t) => Enumerable.Range(0, 100).Count(i => AmbientChatBrain.Plan(small, p, true, i / 100.0) == t);
        Assert.True(Count(shy, AmbientChatBrain.ReplyTurn.Curt) > Count(chatty, AmbientChatBrain.ReplyTurn.Curt));
        Assert.True(Count(chatty, AmbientChatBrain.ReplyTurn.OffTopic) > Count(shy, AmbientChatBrain.ReplyTurn.OffTopic));
        Assert.True(Count(shy, AmbientChatBrain.ReplyTurn.Ignore) > 0);
    }

    [Theory]
    [InlineData("you noob", "rude")]
    [InlineData("ur bad lol", "rude")]
    [InlineData("what?", "what")]
    [InlineData("huh", "what")]
    [InlineData("??", "what")]
    [InlineData("brb", "brb")]
    [InlineData("fire is the best school", "schoolopinion")]
    [InlineData("how do i get to the haunted cave", "where")]
    public void TheBrainHearsMoreThanQuestions(string text, string intent)
        => Assert.Equal(intent, AmbientChatBrain.Understand(text, Ctx())?.Name);

    [Fact]
    public void RudenessGetsAShrugNeverRudenessBack() {
        var intent = AmbientChatBrain.Understand("you noob", Ctx())!;
        Assert.All(intent.Pool, line => Assert.True(AmbientChatBrain.IsClean(line), line));
        Assert.All(intent.Pool, line => Assert.True(line.Split(' ').Length <= 3, line));
    }

    [Fact]
    public void WhatGetsItsOwnLastLineBack() {
        var persona = ChatPersona.For(4, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary, Spelling = ChatSpelling.Casual };
        var memory = new ChatMemory { LastLine = "anyone wanna do rattlebones" };
        var speaker = new ChatSpeaker(persona, Ctx(), ChatMoment.Idle, memory);
        var answers = Enumerable.Range(0, 40).Select(i => AmbientChatPlanner.Answer(AmbientLinePool.What, speaker with { Memory = new ChatMemory { LastLine = memory.LastLine } },
            new Random(i))).ToList();
        Assert.Contains(answers, a => a!.Contains("rattlebones"));
        Assert.Contains(answers, a => !a!.Contains("rattlebones"));
    }

    // ---- memory ---------------------------------------------------------------------------------

    [Fact]
    public void AWizardTellsItsThreadInOrderAndNeverTwice() {
        var persona = ChatPersona.For(4, AmbientTemper.Chatty) with { Channel = ChatChannel.Dictionary, Spelling = ChatSpelling.Casual, Grownup = false };
        var memory = new ChatMemory();
        var speaker = new ChatSpeaker(persona, Ctx(level: 5), ChatMoment.Idle, memory);
        var rng = new Random(7);
        var steps = new List<string>();
        for (var i = 0; i < 400; i++) {
            if (AmbientChatPlanner.Solo(speaker, rng) is { Template: var t } && t.StartsWith("t:", StringComparison.Ordinal)) {
                steps.Add(t);
            }
        }

        Assert.NotEmpty(steps);
        foreach (var thread in steps.GroupBy(t => t.Split(':')[1])) {
            var numbers = thread.Select(t => int.Parse(t.Split(':')[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(Enumerable.Range(0, numbers.Count), numbers); // in order, each step once
        }
    }

    [Fact]
    public void ADoubleLineGoesOutAsTwoLines() {
        var persona = ChatPersona.For(4, AmbientTemper.Chatty) with { Channel = ChatChannel.Dictionary, Spelling = ChatSpelling.Casual, Kind = ChatTemperament.Chatty };
        var rng = new Random(3);
        PlannedLine? twice = null;
        for (var i = 0; i < 2000 && twice is null; i++) {
            var line = AmbientChatPlanner.Solo(new ChatSpeaker(persona, Ctx(zone: "WizardCity/WC_Hub"), ChatMoment.Idle, new ChatMemory()), rng);
            twice = line is { Then.Count: > 0 } ? line : null;
        }

        Assert.NotNull(twice);
        Assert.DoesNotContain("||", twice!.Text);
        Assert.All(twice.Then!, more => Assert.DoesNotContain("|", more));
    }

    // ---- sample transcripts ---------------------------------------------------------------------

    private sealed record Wiz(string Name, ChatPersona Persona, AmbientSchool School, int Level, ChatMemory Memory);

    // A wizard whose own persona (ChatPersona.For, as the server makes it) has the temperament wanted and types (not menu chat).
    private static Wiz MakeWiz(string name, int seed, AmbientTemper temper, AmbientSchool school, int level, ChatTemperament kind) {
        for (var s = seed; ; s++) {
            var persona = ChatPersona.For(s, temper, level);
            if (persona.Kind == kind && persona.Channel != ChatChannel.Menu) {
                return new Wiz(name, persona, school, level, new ChatMemory());
            }
        }
    }

    private static ChatSpeaker Speaker(Wiz w, string zone, LineHistory history, string? player = null, ChatMoment moment = ChatMoment.Idle)
        => new(w.Persona, new ChatContext(w.Name, w.School, w.Level, AmbientKnowledgeName(zone), SpeakerName: player, ZoneKey: zone, Hour: 19,
            History: history, Audience: [42]), moment, w.Memory);

    private static string Clock(TimeSpan t) => $"[{(int) t.TotalMinutes:00}:{t.Seconds:00}]";

    /// <summary>
    /// A few minutes of a street with four wizards and one player who talks to them: lines on their own (the zone's
    /// rhythm, talks between two), and the answers to the player's lines (Plan, the pools, ChatStyle), with their timing.
    /// </summary>
    private static List<string> Street(string zone, Wiz[] wizards, (double At, string Text, string? To)[] player, int seed, double minutes) {
        var rng = new Random(seed);
        var history = new LineHistory();
        var log = new List<(TimeSpan At, string Line)>();
        var t0 = new DateTime(2010, 10, 9, 19, 0, 0, DateTimeKind.Utc);
        var rhythm = new ChatRhythm(rng, t0);
        var last = wizards.Select(_ => DateTime.MinValue).ToArray();
        var playerLines = new Queue<(double At, string Text, string? To)>(player);
        for (var now = t0; now < t0.AddMinutes(minutes); now = now.AddSeconds(1)) {
            while (playerLines.Count > 0 && playerLines.Peek().At * 60 <= (now - t0).TotalSeconds) {
                var (_, text, to) = playerLines.Dequeue();
                log.Add((now - t0, $"Nick (player): {text}"));
                rhythm.Bump(now);
                var named = wizards.FirstOrDefault(w => to is not null && w.Name.StartsWith(to, StringComparison.Ordinal));
                var answerer = named ?? (AmbientChatBrain.IsOpenCall(text) ? wizards[rng.Next(wizards.Length)] : null);
                if (answerer is null) {
                    continue;
                }

                var speaker = Speaker(answerer, zone, history, "Nick Stormblade");
                var intent = AmbientChatBrain.Understand(text, speaker.Context, answerer.Persona);
                if (intent is null && named is null) {
                    continue;
                }

                intent ??= AmbientChatBrain.Fallback;
                var plan = AmbientChatBrain.Plan(intent, answerer.Persona, named is not null, rng.NextDouble());
                if (plan == AmbientChatBrain.ReplyTurn.Ignore) {
                    log.Add((now - t0, $"   ({answerer.Name} lets it go by)"));
                    continue;
                }

                var me = new Dictionary<string, string> { ["me"] = answerer.Name };
                var pool = plan switch {
                    AmbientChatBrain.ReplyTurn.Curt => AmbientLinePool.Curt,
                    AmbientChatBrain.ReplyTurn.OffTopic => AmbientLinePool.Solo(ChatMoment.Idle, zone, answerer.Level, answerer.School, 19, answerer.Persona),
                    _ => (IReadOnlyList<string>) intent.Pool,
                };
                var reply = answerer.Persona.Channel == ChatChannel.Menu ? (intent.Menu ?? AmbientLinePool.MenuReply)[0]
                    : AmbientChatPlanner.Answer(pool, speaker, rng, null, me) ?? AmbientChatPlanner.Answer(intent.Pool, speaker, rng, null, me);
                if (reply is null) {
                    continue;
                }

                var at = now - t0 + ChatTiming.Answer(text, reply, answerer.Persona, false, rng);
                foreach (var piece in reply.Split("||")) {
                    log.Add((at, $"{answerer.Name}: {piece}"));
                    at += ChatTiming.FollowUp(piece, answerer.Persona, rng);
                }

                if (plan == AmbientChatBrain.ReplyTurn.AnswerAndAside && AmbientChatPlanner.Solo(speaker, rng) is { } aside) {
                    log.Add((at, $"{answerer.Name}: {aside.Text}"));
                }
            }

            if (!rhythm.Due(now) || !rhythm.Spent(now)) {
                continue;
            }

            var pick = AmbientChatPlanner.PickSpeaker(wizards.Select((w, i) => (w.Persona.Temper, last[i], true)).ToList(), now, rng);
            if (pick < 0) {
                continue;
            }

            var who = wizards[pick];
            last[pick] = now;
            if (rng.NextDouble() < 0.3 && who.Persona.Channel != ChatChannel.Menu) {
                var other = wizards.Where(w => w != who && w.Persona.Channel != ChatChannel.Menu).OrderBy(_ => rng.Next()).FirstOrDefault();
                if (other is not null) {
                    var talk = AmbientChatPlanner.Exchange(Speaker(who, zone, history), Speaker(other, zone, history), rng, null, history);
                    var at = now - t0;
                    foreach (var line in talk) {
                        at += line.After;
                        log.Add((at, $"{(line.Speaker == 0 ? who : other).Name}: {line.Text}"));
                    }

                    if (talk.Count > 0) {
                        continue;
                    }
                }
            }

            if (AmbientChatPlanner.Solo(Speaker(who, zone, history), rng) is { } solo) {
                var at = now - t0 + solo.After;
                log.Add((at, $"{who.Name}: {solo.Text}"));
                foreach (var more in solo.Then ?? []) {
                    at += ChatTiming.FollowUp(more, who.Persona, rng);
                    log.Add((at, $"{who.Name}: {more}"));
                }
            }
        }

        return log.OrderBy(l => l.At).Select(l => $"{Clock(l.At)} {l.Line}").ToList();
    }

    [Fact]
    public void SampleTranscripts() {
        var report = new StringBuilder();
        void Scene(string title, IEnumerable<string> lines) {
            report.AppendLine($"=== {title} ===");
            foreach (var line in lines) {
                report.AppendLine(line);
            }

            report.AppendLine();
        }

        var commons = new[] {
            MakeWiz("Valdus Frostflame", 11, AmbientTemper.Chatty, AmbientSchool.Ice, 9, ChatTemperament.Chatty),
            MakeWiz("Sierra Dawnbringer", 12, AmbientTemper.Quiet, AmbientSchool.Life, 3, ChatTemperament.Newbie),
            MakeWiz("Alex Stormblade", 13, AmbientTemper.Chatty, AmbientSchool.Storm, 14, ChatTemperament.ShowOff),
            MakeWiz("Jessica Nightbreeze", 14, AmbientTemper.Friendly, AmbientSchool.Death, 12, ChatTemperament.Parent),
        };
        Scene("The Commons, 6 minutes, the player says a few things", Street("WizardCity/WC_Hub", commons,
            [(0.5, "hi", null), (1.2, "anyone wanna quest", null), (2.5, "valdus what school are you", "Valdus"), (3.1, "lol", "Valdus"),
             (4.0, "where is the library", null), (5.0, "you noob", "Alex")], seed: 21, minutes: 6));

        var unicorn = new[] {
            MakeWiz("Duncan Ashfriend", 21, AmbientTemper.Chatty, AmbientSchool.Fire, 6, ChatTemperament.Bossy),
            MakeWiz("Ryan Wildheart", 22, AmbientTemper.Quiet, AmbientSchool.Myth, 5, ChatTemperament.Shy),
            MakeWiz("Nick Lifeweaver", 23, AmbientTemper.Friendly, AmbientSchool.Balance, 8, ChatTemperament.Chatty),
        };
        Scene("Unicorn Way, 8 minutes, the player mostly listens", Street("WizardCity/WC_Streets/WC_Unicorn", unicorn,
            [(2.0, "anyone done rattlebones", null), (2.4, "ryan want to help", "Ryan"), (6.0, "duncan how old are you", "Duncan")],
            seed: 5, minutes: 8));

        // Help offers: what a few wizards say before joining a fight, and to the yes or no.
        var help = new List<string>();
        var rng = new Random(9);
        foreach (var (w, i) in unicorn.Concat(commons).Select((w, i) => (w, i))) {
            var context = new ChatContext(w.Name, w.School, w.Level, "Unicorn Way", SpeakerName: "Nick Stormblade", ZoneKey: "WizardCity/WC_Streets/WC_Unicorn");
            var offer = w.Persona.Channel == ChatChannel.Menu ? AmbientChatBrain.MenuHelpOffer : ChatStyle.Apply(AmbientChatBrain.HelpOffer(context, i), w.Persona, rng);
            var yes = i % 2 == 0;
            var answer = ChatStyle.Apply(AmbientChatBrain.HelpAnswered(yes, i, context), w.Persona, rng);
            help.Add($"{w.Name} ({w.Persona.Kind}): {offer}");
            help.Add($"Nick (player): {(yes ? "sure" : "no thanks")}");
            help.Add($"{w.Name}: {answer}");
            help.Add("");
        }

        Scene("Help offers", help);

        // Group recruiting: a call answered by GroupLines, then the group's lines.
        var group = new List<string>();
        var recruits = new[] { unicorn[0], unicorn[2], commons[2] };
        group.Add("Nick (player): anyone wanna do rattlebones");
        foreach (var (w, i) in recruits.Select((w, i) => (w, i))) {
            var pool = i switch { 0 => GroupLines.Yes, 1 => GroupLines.Brb, _ => GroupLines.Done };
            group.Add($"{w.Name}: {ChatStyle.Apply(GroupLines.For(pool, w.Persona, 31 + i) ?? "", w.Persona, rng)}");
        }

        group.Add($"{recruits[1].Name}: {ChatStyle.Apply(GroupLines.For(GroupLines.Back, recruits[1].Persona, 4)!, recruits[1].Persona, rng)}");
        group.Add($"{recruits[0].Name}: {ChatStyle.Apply(GroupLines.For(GroupLines.NewZone, recruits[0].Persona, 5)!, recruits[0].Persona, rng)}");
        group.Add("(fight won)");
        group.Add($"{recruits[1].Name}: {ChatStyle.Apply(GroupLines.For(GroupLines.AfterWin, recruits[1].Persona, 6)!, recruits[1].Persona, rng)}");
        group.Add("Nick (player): ty guys bye");
        foreach (var (w, i) in recruits.Take(2).Select((w, i) => (w, i))) {
            group.Add($"{w.Name}: {ChatStyle.Apply(GroupLines.For(GroupLines.ByeBack, w.Persona, 7 + i)!, w.Persona, rng)}");
        }

        Scene("Group recruiting", group);

        // A dungeon sigil and the run's end.
        var dungeon = new List<string> {
            $"{unicorn[2].Name}: {DungeonLines.Pick(DungeonLines.Join, 3)}",
            "Nick (player): sure",
            "(the run)",
            $"{unicorn[0].Name}: {DungeonLines.Pick(DungeonLines.Defeated, 2)}",
            "(the run is over)",
            $"{unicorn[2].Name}: {DungeonLines.Pick(DungeonLines.Thanks, 1)}",
            $"{unicorn[0].Name}: {DungeonLines.Pick(DungeonLines.Leave, 4)}",
            "",
            $"{commons[1].Name}: {DungeonLines.Pick(DungeonLines.Join, 6)}",
            "Nick (player): no",
            $"{commons[1].Name}: {DungeonLines.Pick(DungeonLines.Declined, 2)}",
        };
        Scene("Dungeon sigil", dungeon);

        var text = report.ToString();
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("W101C_TRANSCRIPT_OUT") is { Length: > 0 } path) {
            File.WriteAllText(path, text);
        }

        Assert.Contains("Nick (player)", text);
    }

}
