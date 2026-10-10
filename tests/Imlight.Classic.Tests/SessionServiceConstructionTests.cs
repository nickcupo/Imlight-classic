using System;
using System.Linq;
using System.Reflection;
using Imlight.CoreLib.Shared.Networking;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// CLASSIC (2026-10-10): SessionActor.SetServices builds every session service with Props.Create(type, session),
/// which looks up a constructor taking exactly (SessionActor). A service whose only constructor had an extra optional
/// parameter failed to start, and the session closed on every login (live 16:44-19:5x UTC). Tests that construct a
/// service directly never notice, so this checks the shape the server actually uses.
/// </summary>
public class SessionServiceConstructionTests {

    [Fact]
    public void EveryMessageServiceHasTheConstructorTheSessionUses() {
        var services = typeof(MessageService).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(MessageService).IsAssignableFrom(t)).ToArray();
        Assert.NotEmpty(services);
        var missing = services.Where(t => t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, [typeof(SessionActor)], modifiers: null) is null)
            .Select(t => t.FullName).ToArray();
        Assert.True(missing.Length == 0, "No (SessionActor) constructor: " + string.Join(", ", missing));
    }
}
