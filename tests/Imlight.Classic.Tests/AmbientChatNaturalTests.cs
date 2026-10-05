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
 * AMBIENT CHAT NATURAL TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-05): the 2009-style line pools are clean, in era and
 * (when the client's word lists are at hand: W101C_CHATFILTER_DIR, a
 * folder with ChatFilter_WhiteListBase.txt etc. from Root.wad) pass the
 * game's chat dictionary in every persona's spelling; the planner keeps
 * quiet stretches, varies speakers and never repeats a wizard's line; the
 * brain understands open calls; the optional LLM client is local-only,
 * filters its output and never makes the game wait.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientChatNaturalTests {

    private static readonly DateTime T0 = new(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc);

    private static ChatContext Ctx(string zone = "WizardCity/WC_Streets/WC_Unicorn", int level = 7, AmbientSchool school = AmbientSchool.Fire,
                                   LineHistory? history = null, ulong[]? audience = null, string? speaker = "Nick Stormblade")
        => new("Duncan AshFriend", school, level, "Unicorn Way", SpeakerName: speaker, ZoneKey: zone, Hour: 20, History: history,
            Audience: audience ?? [42]);

    private static readonly string[] Zones = [
        "WizardCity/WC_Hub", "WizardCity/WC_Streets/WC_Unicorn", "WizardCity/WC_Streets/WC_Triton", "WizardCity/WC_Streets/WC_Firecat",
        "WizardCity/WC_Streets/WC_Cyclops", "WizardCity/WC_Streets/WC_Colossus", "WizardCity/WC_Streets/WC_OldeTown",
        "WizardCity/WC_Streets/Interiors/WC_OldeTown_AuctionHouse", "WizardCity/WC_Shop_Area", "WizardCity/WC_Ravenwood",
        "WizardCity/WC_Streets/WC_HauntedCave", "WizardCity/Interiors/WC_Hatchery", "Krokotopia/KT_Hub", "Marleybone/MB_Hub",
        "MooShu/MS_Hub", "DragonSpire/DS_Hub", "Grizzleheim/GH_Hub",
    ];

    private static IEnumerable<string> EveryTemplate() {
        var pools = new List<IEnumerable<string>> {
            AmbientLinePool.MenuIdle, AmbientLinePool.MenuReply, AmbientLinePool.MenuAfterWin, AmbientLinePool.Kid,
            AmbientLinePool.Grownup, AmbientLinePool.GrownupLevel, AmbientLinePool.Level1To5, AmbientLinePool.Level6To14,
            AmbientLinePool.Level15To25, AmbientLinePool.Level26To40, AmbientLinePool.Level41To50, AmbientLinePool.Hunting,
            AmbientLinePool.Shopping, AmbientLinePool.Following, AmbientLinePool.AfterWin, AmbientLinePool.AfterWinGrownup,
            AmbientLinePool.AfterLoss, AmbientLinePool.BossDoor, AmbientLinePool.Arrived, AmbientLinePool.Leaving,
            AmbientLinePool.Greet, AmbientLinePool.GreetNear, AmbientLinePool.HowAreYou, AmbientLinePool.LevelNoNumber,
            AmbientLinePool.LevelNumber, AmbientLinePool.SchoolAnswer, AmbientLinePool.AgeAnswer, AmbientLinePool.NameAnswer,
            AmbientLinePool.ComplimentThanks, AmbientLinePool.GoldBeg, AmbientLinePool.NotABot, AmbientLinePool.Laugh,
            AmbientLinePool.Agree, AmbientLinePool.Busy, AmbientLinePool.Unsure, AmbientLinePool.Come, AmbientLinePool.Teleport,
            AmbientLinePool.HowToHatch, AmbientLinePool.HowToGold, AmbientLinePool.Trade, AmbientLinePool.Duel, AmbientLinePool.Friend,
            AmbientLinePool.Thanks, AmbientLinePool.Bye, AmbientLinePool.Fallback, AmbientLinePool.HelpSure, AmbientLinePool.Doing,
            AmbientLinePool.QuestTogether,
        };
        pools.AddRange(AmbientLinePool.School.Values);
        pools.AddRange(AmbientLinePool.Time.Values);
        pools.AddRange(AmbientLinePool.Zone.Select(z => z.Lines));
        pools.Add(AmbientLinePool.Exchanges.SelectMany(x => x.Turns.SelectMany(t => t[2..].Split('|'))));
        return pools.SelectMany(p => p);
    }

    private static Dictionary<string, string> Slots() => new() {
        ["boss"] = "Rattlebones", ["mob"] = "ghosts", ["pet"] = "fire cat", ["a"] = "Duncan", ["b"] = "Ryan", ["aschool"] = "fire",
        ["bschool"] = "storm", ["me"] = "Duncan AshFriend", ["what"] = "Lady Blackhope", ["where"] = "Unicorn Way",
    };

    private static readonly ChatPersona Open = ChatPersona.For(1, AmbientTemper.Friendly) with { Grownup = true, Channel = ChatChannel.Open };

    [Fact]
    public void EveryTemplateFillsCleanAndInEra() {
        var speaker = new ChatSpeaker(Open, Ctx(), ChatMoment.Idle, new ChatMemory());
        var templates = EveryTemplate().Distinct().ToList();
        Assert.True(templates.Count >= 600, $"only {templates.Count} templates");
        foreach (var template in templates) {
            var line = AmbientChatPlanner.Fill(template, speaker, Slots());
            Assert.True(line is not null, template);
            Assert.True(AmbientChatBrain.IsClean(line), line);
            Assert.True(AmbientLlmPrompt.InEra(line), line);
        }
    }

    [Fact]
    public void TheSoloPoolsAreBigForEveryZoneAndPersona() {
        foreach (var zone in Zones) {
            foreach (var seed in Enumerable.Range(0, 30)) {
                var persona = ChatPersona.For(seed, (AmbientTemper) (seed % 3));
                var pool = AmbientLinePool.Solo(ChatMoment.Idle, zone, 1 + seed, (AmbientSchool) (seed % 7), 20, persona, DayOfWeek.Saturday);
                Assert.True(pool.Distinct().Count() >= (persona.Channel == ChatChannel.Menu ? 20 : persona.Grownup ? 70 : 150), $"{zone} {persona}");
            }
        }
    }

    [Fact]
    public void NumbersOnlyInOpenChat() {
        var kid = ChatPersona.For(3, AmbientTemper.Chatty) with { Grownup = false, Channel = ChatChannel.Dictionary };
        var speaker = new ChatSpeaker(kid, Ctx(), ChatMoment.Idle, new ChatMemory());

        Assert.Null(AmbientChatPlanner.Fill("level {level}", speaker));
        Assert.Equal("level 7", AmbientChatPlanner.Fill("level {level}", speaker with { Persona = Open }));
        var intent = AmbientChatBrain.Understand("what lvl are you", Ctx(), kid)!;
        Assert.All(intent.Pool, t => Assert.DoesNotContain("{level}", t));
    }

    [Fact]
    public void StylesDifferButStayClean() {
        var rng = new Random(5);
        var seen = new HashSet<string>();
        foreach (var spelling in Enum.GetValues<ChatSpelling>()) {
            var persona = ChatPersona.For(2, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary, Spelling = spelling };
            for (var i = 0; i < 20; i++) {
                var line = ChatStyle.Apply("i don't know, i'm going to ask the headmaster. thanks!", persona, rng);
                Assert.True(AmbientChatBrain.IsClean(line), line);
                seen.Add(line);
            }
        }

        Assert.Contains(seen, l => l.StartsWith("I don't know", StringComparison.Ordinal));
        Assert.Contains(seen, l => l.Contains("idk") || l.Contains("dunno") || l.Contains("gonna"));
        Assert.True(seen.Count >= 6);
    }

    [Fact]
    public void ThirtyMinutesOfAZoneSoundLikeAZone() {
        var rng = new Random(2009);
        var history = new LineHistory();
        var wizards = Enumerable.Range(0, 4).Select(i => (Persona: ChatPersona.For(100 + i, (AmbientTemper) (i % 3)), Memory: new ChatMemory(),
            School: (AmbientSchool) i, Said: new List<string>(), Last: DateTime.MinValue)).ToArray();
        var rhythm = new ChatRhythm(rng, T0);
        var times = new List<DateTime>();
        var exchanges = 0;
        for (var now = T0; now < T0.AddMinutes(30); now = now.AddSeconds(0.3)) {
            if (!rhythm.Due(now) || !rhythm.Spent(now)) {
                continue;
            }

            var pick = AmbientChatPlanner.PickSpeaker(wizards.Select(w => (w.Persona.Temper, w.Last, true)).ToList(), now, rng);
            if (pick < 0) {
                continue;
            }

            var me = wizards[pick];
            var speaker = new ChatSpeaker(me.Persona, Ctx(school: me.School, history: history), ChatMoment.Idle, me.Memory);
            if (rng.NextDouble() < 0.3) {
                var other = wizards[(pick + 1) % 4];
                var plan = AmbientChatPlanner.Exchange(speaker, new ChatSpeaker(other.Persona, Ctx(school: other.School, history: history),
                    ChatMoment.Idle, other.Memory), rng, history: history);
                if (plan.Count > 0) {
                    exchanges++;
                    Assert.All(plan, l => Assert.True(l.After >= TimeSpan.FromSeconds(1.5)));
                    Assert.True(plan.Zip(plan.Skip(1)).All(p => p.First.Speaker != p.Second.Speaker || p.First.Text != p.Second.Text));
                    times.Add(now);
                    wizards[pick].Last = now;
                    continue;
                }
            }

            if (AmbientChatPlanner.Solo(speaker, rng) is { } line) {
                Assert.DoesNotContain(line.Template, me.Said.TakeLast(ChatMemory.Window));
                me.Said.Add(line.Template);
                wizards[pick].Last = now;
                times.Add(now);
            }
        }

        // Between a handful and a few dozen moments in half an hour, at least one quiet stretch of three minutes.
        Assert.InRange(times.Count, 6, 45);
        Assert.True(times.Zip(times.Skip(1)).Any(p => p.Second - p.First >= TimeSpan.FromMinutes(3)) || times[0] - T0 >= TimeSpan.FromMinutes(3));
        Assert.True(wizards.Count(w => w.Said.Count > 0) >= 2);
    }

    [Theory]
    [InlineData("anyone wanna help me with rattlebones", true)]
    [InlineData("hi all", true)]
    [InlineData("hello", true)]
    [InlineData("where is lady blackhope", true)]
    [InlineData("how r you", true)]
    [InlineData("i love this game", false)]
    [InlineData("brb", false)]
    public void OpenCalls(string text, bool open) => Assert.Equal(open, AmbientChatBrain.IsOpenCall(text));

    [Theory]
    [InlineData("anyone wanna help me with rattlebones", "help")]
    [InlineData("where is lady blackhope", "where")]
    [InlineData("nice hat", "compliment")]
    [InlineData("can i have some gold plz", "gold")]
    [InlineData("are you a bot", "bot")]
    [InlineData("how old are you", "age")]
    [InlineData("anyone wanna quest", "quest")]
    [InlineData("lol", "laugh")]
    [InlineData("ty", "thanks")]
    public void TheBrainUnderstandsTwoThousandNineQuestions(string text, string intent)
        => Assert.Equal(intent, AmbientChatBrain.Understand(text, Ctx())?.Name);

    // ---- the client's own word lists (private; skipped without them) ----------------------------

    private static ChatWordFilter? ClientLists() {
        var dir = Environment.GetEnvironmentVariable("W101C_CHATFILTER_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
            return null;
        }

        return ChatWordFilter.Load(name => {
            var path = Path.Combine(dir, name.Replace('/', '_'));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        });
    }

    [Fact]
    public void EveryLinePassesTheClientDictionaryInEveryStyle() {
        if (ClientLists() is not { HasDictionary: true } filter) {
            return; // no lists here (CI): IsClean alone was checked above
        }

        var rng = new Random(9);
        var kid = ChatPersona.For(4, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary };
        var failures = new List<string>();
        foreach (var template in EveryTemplate().Distinct()) {
            var line = AmbientChatPlanner.Fill(template, new ChatSpeaker(Open, Ctx(), ChatMoment.Idle, new ChatMemory()), Slots())!;
            if (!filter.Passes(line, allowNumbers: true)) {
                failures.Add($"{line} [{string.Join(",", filter.Refused(line, true))}]");
            }

            foreach (var spelling in Enum.GetValues<ChatSpelling>()) {
                var styled = ChatStyle.Apply(line, kid with { Spelling = spelling }, rng, filter);
                Assert.True(filter.Passes(styled, allowNumbers: true) || styled == line, styled);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---- the optional language model ------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:8088", true)]
    [InlineData("http://localhost:8088", true)]
    [InlineData("http://192.168.1.251:8088", true)]
    [InlineData("http://10.0.0.5:8088", true)]
    [InlineData("http://example.com:8088", false)]
    [InlineData("http://8.8.8.8:8088", false)]
    [InlineData("https://api.someone.ai/v1", false)]
    public void TheModelMustBeLocal(string url, bool on) {
        Assert.Equal(on, AmbientLlmSettings.Parse("true", url, "", "", "").Enabled);
        Assert.False(AmbientLlmSettings.Parse("", url, "", "", "").Enabled); // off by default
    }

    [Theory]
    [InlineData("hey wanna go questing", true)]
    [InlineData("\"lol nice hat\"", true)]
    [InlineData("Ryan: anyone going to krokotopia", true)]
    [InlineData("i just got back from celestia", false)]
    [InlineData("as an ai language model i cannot", false)]
    [InlineData("my level is 23", false)]
    [InlineData("check out www.example.com", false)]
    [InlineData("i love fishing in the commons", false)]
    [InlineData("Hi there! How may I help you today?", false)]
    [InlineData("ok your answer is lol", false)]
    [InlineData("this is a very long line that goes on and on and on about everything in the whole game", false)]
    public void TheModelsLinesAreFiltered(string raw, bool kept) {
        var persona = ChatPersona.For(1, AmbientTemper.Chatty) with { Channel = ChatChannel.Dictionary, Spelling = ChatSpelling.Casual };
        Assert.Equal(kept, AmbientLlmPrompt.Clean(raw, persona, null, new Random(1)) is not null);
    }

    private sealed class FakeServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => answer(request, cancellationToken);
    }

    private static HttpResponseMessage Completion(string text) => new(HttpStatusCode.OK) {
        Content = new StringContent($"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{text}\"}}}}]}}", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task TheModelNeverMakesTheGameWait() {
        var settings = AmbientLlmSettings.Parse("true", "http://127.0.0.1:8088", "600", "60", "24");
        var persona = ChatPersona.For(1, AmbientTemper.Chatty) with { Channel = ChatChannel.Dictionary, Spelling = ChatSpelling.Casual };
        using var slow = new AmbientLlmClient(settings, new FakeServer(async (_, cancel) => {
            await Task.Delay(TimeSpan.FromSeconds(10), cancel);
            return Completion("too late");
        }));

        var watch = Stopwatch.StartNew();
        var reply = slow.Reply("system", "user", persona, null);
        Assert.True(watch.ElapsedMilliseconds < 100, "Reply must return a task at once");
        Assert.Null(await reply);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"timeout took {watch.Elapsed}");
        Assert.False(slow.TryTake("k", out _));
    }

    [Fact]
    public async Task TheModelsGoodLinesAreKeptAndBadOnesDropped() {
        var settings = AmbientLlmSettings.Parse("true", "http://127.0.0.1:8088", "2000", "60", "24");
        var persona = ChatPersona.For(1, AmbientTemper.Chatty) with { Channel = ChatChannel.Dictionary, Spelling = ChatSpelling.Casual };
        var answers = new Queue<string>(["anyone wanna go to the arena", "i love celestia"]);
        using var client = new AmbientLlmClient(settings, new FakeServer((_, _) => Task.FromResult(Completion(answers.Dequeue()))));

        Assert.Equal("anyone wanna go to the arena", await client.Reply("s", "u", persona, null));
        Assert.Null(await client.Reply("s", "u", persona, null));
        Assert.Equal(1, client.Kept);
        Assert.Equal(1, client.Rejected);
    }

}
