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
 * AMBIENT CHAT TIMING TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (owner, 2026-10-05: "it seems like friendly wizards are answering
 * before i actually hit enter"): an ambient wizard never answers at once;
 * a menu phrase only counts when no typed line from the same player comes
 * with it; personas are stable and type at kid or grown-up speed.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Linq;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientChatTimingTests {

    private static readonly DateTime T0 = new(2026, 10, 5, 1, 55, 14, DateTimeKind.Utc);

    [Fact]
    public void AnswersAlwaysTakeReadingAndTypingTime() {
        var rng = new Random(1);
        foreach (var seed in Enumerable.Range(0, 200)) {
            var persona = ChatPersona.For(seed, (AmbientTemper) (seed % 3));
            foreach (var line in new[] { "ok!", "lol", "coming!", "sure, where are you?", "i need to finish all the streets first lol" }) {
                var wait = ChatTiming.Reading("help", rng) + ChatTiming.Typing(line, persona, rng);
                Assert.True(wait >= TimeSpan.FromSeconds(1.9), $"{line}: {wait.TotalSeconds} s");
                Assert.True(wait <= TimeSpan.FromSeconds(30), $"{line}: {wait.TotalSeconds} s");
            }
        }
    }

    [Fact]
    public void TwoWizardsNeverAnswerInTheSameInstant() {
        var rng = new Random(3);
        var last = DateTime.MinValue;
        var answers = new System.Collections.Generic.List<DateTime>();
        for (var i = 0; i < 6; i++) {
            last = ChatTiming.Stagger(T0.AddSeconds(2), last, rng); // six wizards whose typing would finish together
            answers.Add(last);
        }

        for (var i = 1; i < answers.Count; i++) {
            Assert.True(answers[i] - answers[i - 1] >= ChatTiming.ApartAtLeast);
        }
    }

    [Fact]
    public void AWizardInAFightAnswersSlowerAndSomeLinesGoUnanswered() {
        var persona = ChatPersona.For(9, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary };
        var calm = Enumerable.Range(0, 100).Average(i => ChatTiming.Answer("hi", "hey whats up", persona, false, new Random(i)).TotalSeconds);
        var fighting = Enumerable.Range(0, 100).Average(i => ChatTiming.Answer("hi", "hey whats up", persona, true, new Random(i)).TotalSeconds);
        var rng = new Random(11);
        var ignored = Enumerable.Range(0, 1000).Count(_ => ChatTiming.Ignores(persona, rng));

        Assert.True(calm >= 3, $"calm {calm}");
        Assert.True(fighting > calm * 1.4, $"{calm} vs {fighting}");
        Assert.InRange(ignored, 60, 200);
    }

    [Fact]
    public void LongerLinesTakeLonger() {
        var persona = ChatPersona.For(5, AmbientTemper.Friendly) with { Channel = ChatChannel.Dictionary };
        var shortWait = Enumerable.Range(0, 50).Average(i => ChatTiming.Typing("ok cool", persona, new Random(i)).TotalSeconds);
        var longWait = Enumerable.Range(0, 50).Average(i => ChatTiming.Typing("i need to finish all the streets before krokotopia", persona, new Random(i)).TotalSeconds);

        Assert.True(longWait > shortWait + 4, $"{shortWait} vs {longWait}");
    }

    [Fact]
    public void AMenuPhraseWithATypedLineAroundItDoesNotCount() {
        Assert.True(ChatTiming.MenuPhraseStands(T0, null));
        Assert.True(ChatTiming.MenuPhraseStands(T0, T0.AddSeconds(-30)));        // an older line
        Assert.False(ChatTiming.MenuPhraseStands(T0, T0.AddSeconds(-1)));        // typed just before
        Assert.False(ChatTiming.MenuPhraseStands(T0, T0.AddSeconds(2)));         // typed while it settled
        Assert.True(ChatTiming.MenuSettle >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ShortAnswersCountAsYes() {
        foreach (var yes in new[] { "ye", "yes", "help", "sure!", "ok" }) {
            Assert.Equal(HelpAnswerKind.Yes, HelpOffers.Classify(yes));
        }
    }

    [Fact]
    public void PersonasAreStableAndVaried() {
        Assert.Equal(ChatPersona.For(42, AmbientTemper.Chatty), ChatPersona.For(42, AmbientTemper.Chatty));
        var all = Enumerable.Range(0, 400).Select(s => ChatPersona.For(s, (AmbientTemper) (s % 3))).ToList();

        foreach (var spelling in Enum.GetValues<ChatSpelling>()) {
            Assert.Contains(all, p => p.Spelling == spelling);
        }

        Assert.InRange(all.Count(p => p.Grownup), 40, 130);
        Assert.InRange(all.Count(p => p.Channel == ChatChannel.Menu), 40, 200);
        Assert.All(all.Where(p => !p.Grownup), p => Assert.InRange(p.CharsPerSecond, 2.2, 4.5));
        Assert.All(all.Where(p => p.Channel == ChatChannel.Open), p => Assert.True(p.Grownup));
    }

}
