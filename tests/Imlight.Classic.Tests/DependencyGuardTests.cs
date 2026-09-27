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
 * CLASSIC RULES TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * Keeps Imlight.Classic free of Imlight, Imcodec and Akka references, so CI
 * can build and test it without the private generator inputs.
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
 * Last Updated: 09/26/2026
 */

using System;
using System.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DependencyGuardTests {

    [Fact]
    public void ReferencesNoServerAssemblies() {
        var references = typeof(ClassicRules).Assembly.GetReferencedAssemblies().Select(name => name.Name ?? "").ToList();
        var forbidden = references.Where(name =>
            name.StartsWith("Imlight.", StringComparison.Ordinal)
            || name.StartsWith("Imcodec", StringComparison.Ordinal)
            || name.StartsWith("Akka", StringComparison.Ordinal)).ToList();

        Assert.Contains("YamlDotNet", references);
        Assert.Empty(forbidden);
    }

}
