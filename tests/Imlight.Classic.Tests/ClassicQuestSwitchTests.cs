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
 * CLASSIC QUEST ENGINE TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * Which profiles turn on the KingsIsle quest reading and the classic start,
 * so dev-unrestricted and stock stay stock Imlight.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicQuestSwitchTests {

    [Fact]
    public void CanonicalProfilesUseBoth() {
        foreach (var id in new[] { "late-2009", "arc1-2009h1" }) {
            var rules = ClassicDataFixture.RealRules(id);

            Assert.Equal(ClassicSchema.ClassicTutorial, rules.Profile.Rules.Tutorial);
            Assert.True(rules.UsesKingsIsleQuestRules, id);
            Assert.True(rules.UsesClassicStart, id);
        }
    }

    [Fact]
    public void DevUnrestrictedAndStockUseNeither() {
        var dev = new ClassicRules(ClassicDataFixture.LoadProfile("dev-unrestricted"), ZoneWorldMap.Empty);

        foreach (var rules in new[] { dev, ClassicRules.Stock }) {
            Assert.False(rules.UsesKingsIsleQuestRules);
            Assert.False(rules.UsesClassicStart);
        }
    }

    [Fact]
    public void ClassicStartNeedsTheClassicTutorial() {
        var modern = Restricted(tutorial: "modern-2019");
        var unset = Restricted(tutorial: null);

        Assert.True(modern.UsesKingsIsleQuestRules);
        Assert.False(modern.UsesClassicStart);
        Assert.False(unset.UsesClassicStart);
        Assert.True(Restricted(ClassicSchema.ClassicTutorial).UsesClassicStart);
    }

    private static ClassicRules Restricted(string? tutorial) {
        var profile = ZoneFixture.Profile(levelCap: 50);

        return new ClassicRules(new ClassicProfile {
            Id = profile.Id,
            Title = profile.Title,
            Status = profile.Status,
            LevelCap = profile.LevelCap,
            Features = profile.Features,
            Rules = new ProfileRules { Tutorial = tutorial },
            SourceFiles = profile.SourceFiles,
        }, ZoneFixture.MinimalMap());
    }

}
