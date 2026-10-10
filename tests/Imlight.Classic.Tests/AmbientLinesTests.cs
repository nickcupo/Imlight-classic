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
 * AMBIENT LINES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the ambient wizards' line pools are clean and varied, and no
 * player hears the same line twice within their last twenty.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientLinesTests {

    private static ChatContext Context(LineHistory? history = null, ulong[]? audience = null, AmbientSchool school = AmbientSchool.Fire,
                                       string zone = "WizardCity/WC_Streets/WC_Unicorn")
        => new("Duncan AshFriend", school, 7, "Unicorn Way", SpeakerName: "Nick Stormblade", ZoneKey: zone, Hour: 9,
            History: history, Audience: audience);

    [Fact]
    public void EveryLineIsCleanOnceFilled() {
        string[][] pools = [
            AmbientLines.Greetings, AmbientLines.FriendGreetings, AmbientLines.Thanks, AmbientLines.Bye, AmbientLines.HowAreYou,
            AmbientLines.Fallback, AmbientLines.Level, AmbientLines.School, AmbientLines.Duel, AmbientLines.Help,
            AmbientLines.Friend, AmbientLines.Doing, AmbientLines.WhereUnknown, AmbientLines.HelpOffer,
            AmbientLines.HelpOfferFriend, AmbientLines.Joining, AmbientLines.NotJoining, AmbientLines.AfterWin,
        ];
        var idle = Enum.GetValues<AmbientSchool>().SelectMany(s => AmbientLines.Idle(Context(school: s)))
            .Concat(new[] { "WC_Hub", "WC_Shop_Area", "Krokotopia/KT_Hub", "Marleybone/MB_Hub", "MooShu/MS_Hub", "Dragonspyre/DS_Hub",
                "Grizzleheim/GH_Hub" }.SelectMany(z => AmbientLines.Idle(Context(zone: z))));

        foreach (var template in pools.SelectMany(p => p).Concat(idle)) {
            var line = AmbientLines.Fill(template, Context());
            Assert.NotNull(line);
            Assert.All(line.Split("||"), part => Assert.True(AmbientChatBrain.IsClean(part), part)); // CLASSIC (2026-10-10): "a||b" is two lines
        }

        Assert.True(AmbientLines.Idle(Context()).Distinct().Count() >= 40);
    }

    [Fact]
    public void APlayerDoesNotHearTheSameLineWithinTwenty() {
        var history = new LineHistory();
        var context = Context(history, [42]);
        var said = new List<string>();
        for (var turn = 0; turn < 30; turn++) {
            if (AmbientChatBrain.Idle(context, turn * 7) is { } line) {
                said.Add(line);
            }
        }

        Assert.Equal(30, said.Count);
        for (var i = 0; i < said.Count; i++) {
            Assert.DoesNotContain(said[i], said.Skip(i + 1).Take(LineHistory.HeardWindow - 1));
        }
    }

    [Fact]
    public void IdleTalkStaysQuietWhenEverythingWasHeardButAnswersStillAnswer() {
        var history = new LineHistory();
        var context = Context(history, [42]);
        foreach (var template in AmbientLines.AfterWin.Take(LineHistory.HeardWindow)) {
            history.Note([42], template);
        }

        Assert.Null(AmbientChatBrain.AfterWin(context, 0));
        Assert.NotNull(AmbientChatBrain.AfterWin(Context(history, [7]), 0)); // someone else has not heard them
        Assert.NotNull(AmbientChatBrain.HelpAnswered(true, 0, context));
    }

    [Fact]
    public void SlotsWithoutAValueSkipTheTemplate() {
        var nameless = Context() with { SpeakerName = null, Hour = -1 };

        Assert.Null(AmbientLines.Fill("hi {name}!", nameless));
        Assert.Null(AmbientLines.Fill("good {time}", nameless));
        Assert.Equal("hi Nick!", AmbientLines.Fill("hi {name}!", Context()));
        Assert.Equal("good morning everyone", AmbientLines.Fill("good {time} everyone", Context()));
        Assert.Equal("almost level 8!", AmbientLines.Fill("almost level {next}!", Context()));
        Assert.Contains("lost souls everywhere", AmbientLines.Idle(Context()));
    }

}
