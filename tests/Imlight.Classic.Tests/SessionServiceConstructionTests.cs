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
    /// <summary>
    /// CLASSIC (2026-10-17): every session service gets each message it handles, so two services handling one
    /// message type both act on it. The tutorial-tip log was answered twice at login after two branches each added a
    /// handler. These are the message types several services share on purpose, as of 2026-10-17; a new shared type
    /// fails here until it is listed with its reason. A handler a base class declares counts once.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> s_shared = new() {
        // Client protocol
        ["GAME_5_PROTOCOL+MSG_ATTACH"] = "attach plus the tutorial's start-of-session hook",
        ["GAME_5_PROTOCOL+MSG_BUDDYREQUESTLIST"] = "friends list, plus ambient wizards answering as friends",
        ["GAME_5_PROTOCOL+MSG_BUDDYSTATS"] = "friend stats feed chat, command replies and the friends list",
        ["GAME_5_PROTOCOL+MSG_CLIENTMOVE"] = "every movement: position, zone triggers, duel range, wizard state, idle control",
        ["GAME_5_PROTOCOL+MSG_CLIENT_DISCONNECT"] = "leaving: session, duel and friends-online cleanup",
        ["GAME_5_PROTOCOL+MSG_QUERY_LOGOUT"] = "logging out: session, duel and friends-online cleanup",
        ["GAME_5_PROTOCOL+MSG_REQUESTDIRECTEDCHAT"] = "whispers to players, and to ambient wizards in a group",
        ["PATCH_8_PROTOCOL+MSG_LATEST_FILE_LIST_V2"] = "the game client's and the launcher's patch services",
        ["WIZARDHOUSING_50_PROTOCOL+MSG_REQUEST_BLOBS"] = "house blobs: furniture and garden",
        ["WIZARD_12_PROTOCOL+MSG_DONESHOPPING"] = "closing any shop window: bank, shop, treasure shop",
        // Server-internal (each service reacts to the event)
        ["CLASSIC_FEATURES_PROTOCOL+MSG_PVPRELEASE"] = "end of a PvP match: combat, crown shop, elixirs, gear",
        ["COMBAT_106_PROTOCOL+MSG_ACTORADDEDTODUEL"] = "joining a duel: combat, crown shop, elixirs, gear",
        ["COMBAT_106_PROTOCOL+MSG_COMBATDEFEAT"] = "a lost duel: combat, crown shop, elixirs, gear",
        ["COMBAT_106_PROTOCOL+MSG_COMBATWIN"] = "a won duel: combat, crown shop, elixirs, gear, quest credit",
        ["SERVICE_101_PROTOCOL+MSG_ATTACHCOMPLETE"] = "each service sends its state once the client is attached",
        ["SERVICE_101_PROTOCOL+MSG_OPCODE_HALT"] = "control and AFK timers pause",
        ["SERVICE_101_PROTOCOL+MSG_OPCODE_RESUME"] = "control and AFK timers resume",
        ["ZONE_102_PROTOCOL+MSG_ADDPLAYERRSP"] = "zone entry: the wizard and the zone service",
        ["ZONE_102_PROTOCOL+MSG_PRELOGIN"] = "before login: attach, control, held quests",
        ["ZONE_102_PROTOCOL+MSG_ZONETRANSFER"] = "zone change: zone service and the cantrip teleport",
    };

    [Fact]
    public void NoNewMessageTypeIsHandledByTwoSessionServices() {
        var services = typeof(MessageService).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(MessageService).IsAssignableFrom(t));
        var shared = services
            .SelectMany(service => service.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(method => method.GetCustomAttributes<MessageHandlerAttribute>()
                    .Select(attribute => (Message: attribute.MessageType, Declarer: method.DeclaringType!))))
            .Distinct()
            .GroupBy(entry => entry.Message)
            .Where(group => group.Count() > 1)
            .ToArray();
        Assert.NotEmpty(shared); // the scan sees the known shared types

        var unlisted = shared
            .Where(group => !s_shared.ContainsKey(group.Key.FullName![(group.Key.FullName!.LastIndexOf('.') + 1)..]))
            .Select(group => $"{group.Key.FullName}: {string.Join(", ", group.Select(entry => entry.Declarer.Name).OrderBy(n => n))}")
            .ToArray();
        Assert.True(unlisted.Length == 0, "Handled by more than one session service (list it with a reason only if both"
            + " must act on it):\n" + string.Join("\n", unlisted));
    }
}
