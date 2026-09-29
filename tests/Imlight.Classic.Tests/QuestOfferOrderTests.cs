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
 * CLASSIC QUEST OFFER ORDER TESTS
 * ========================================================================
 *
 * PURPOSE:
 * An NPC offers its main-story quest before side quests with lower title keys.
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
 * Last Updated: 09/29/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public class QuestOfferOrderTests {

    private sealed record Q(string Name, bool Main, string Title);

    [Fact]
    public void MainStoryComesBeforeSideQuestsWithLowerTitleKeys() {
        // Mayor Pimsbury: two classic side quests (QuestTitle_14xxx) and A Cat-tastrophy (QuestTitle_9B3C).
        var quests = new List<Q> {
            new("MB-CLASSIC-SIDE-018", false, "QuestTitle_1414A"),
            new("MB-AIRHub-C01-001", true, "QuestTitle_9B3C"),
            new("MB-CLASSIC-SIDE-014", false, "QuestTitle_14142"),
        };

        quests.Sort(QuestOfferOrder.For<Q>(q => q.Main, q => q.Title));

        Assert.Equal(["MB-AIRHub-C01-001", "MB-CLASSIC-SIDE-014", "MB-CLASSIC-SIDE-018"], quests.Select(q => q.Name));
    }

    [Fact]
    public void WithinAGroupTheTitleKeyOrderIsKept() {
        var quests = new List<Q> { new("b", true, "QuestTitle_9B3D"), new("a", true, "QuestTitle_9B3C") };

        quests.Sort(QuestOfferOrder.For<Q>(q => q.Main, q => q.Title));

        Assert.Equal(["a", "b"], quests.Select(q => q.Name));
    }

}
