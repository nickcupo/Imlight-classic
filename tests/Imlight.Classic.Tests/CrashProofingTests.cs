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
 * CRASH PROOFING TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Crash and hang review of the live server (2026-10-28): each test pins one failure that could take down live play
 * (the process, a zone or the game world) or leak for good, found by reading the code, not reported in game.
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~CrashProofingTests
 *
 * NOTE:
 * Real actors in a local ActorSystem; no database or network server.
 *
 * Created by: Nick with Claude Code
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CrashProofingTests {

    /// <summary>
    /// An exception in an async void method is rethrown on the thread pool and ends the process (.NET has no way to
    /// catch it there). AttachService.AddPlayerToOnlineCollection was one: a RavenDB error while it stored the online
    /// row (the embedded database restarting, a timeout) took the server down for everyone.
    /// </summary>
    [Fact]
    public void NoServerMethodIsAsyncVoid() {
        var asyncVoid = typeof(SessionActor).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                          | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(void) && m.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToList();

        Assert.Empty(asyncVoid);
    }

}
