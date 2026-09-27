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
 * The ReqMagicLevel comparison at its boundaries: Enrollment (level 2),
 * Olde News (level 5) and Gamma's teleport tips (level 15 or lower).
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

using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class NumericRequirementTests {

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void EnrollmentNeedsLevelTwo(int level, bool met)
        => Assert.Equal(met, NumericRequirement.Meets(level, "OPERATOR_GREATER_THAN_EQ", 2f));

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void OldeNewsNeedsLevelFive(int level, bool met)
        => Assert.Equal(met, NumericRequirement.Meets(level, "OPERATOR_GREATER_THAN_EQ", 5f));

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void GammaTipsStopAfterLevelFifteen(int level, bool met)
        => Assert.Equal(met, NumericRequirement.Meets(level, "OPERATOR_LESS_THAN_EQ", 15f));

    [Theory]
    [InlineData("OPERATOR_EQUALS", 4, false)]
    [InlineData("OPERATOR_EQUALS", 5, true)]
    [InlineData("OPERATOR_EQUALS", 6, false)]
    [InlineData("OPERATOR_GREATER_THAN", 5, false)]
    [InlineData("OPERATOR_GREATER_THAN", 6, true)]
    [InlineData("OPERATOR_LESS_THAN", 4, true)]
    [InlineData("OPERATOR_LESS_THAN", 5, false)]
    public void StrictAndEqualOperatorsSplitAtTheValue(string operatorType, int level, bool met)
        => Assert.Equal(met, NumericRequirement.Meets(level, operatorType, 5f));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OPERATOR_NOT_EQUALS")]
    [InlineData("operator_greater_than_eq")]
    public void AnUnknownOperatorIsNeverMet(string? operatorType) {
        Assert.False(NumericRequirement.Meets(1, operatorType, 5f));
        Assert.False(NumericRequirement.Meets(5, operatorType, 5f));
        Assert.False(NumericRequirement.Meets(9, operatorType, 5f));
    }

}
