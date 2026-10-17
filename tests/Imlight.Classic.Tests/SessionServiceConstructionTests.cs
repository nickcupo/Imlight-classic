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
    /// CLASSIC (2026-10-17): every session service gets each client message it handles. Two services handling the same
    /// message type both answer it: the tutorial-tip log was answered twice at login after two branches each added a
    /// handler. One message type, one session service.
    /// </summary>
    // Client messages several services read on purpose (movement, logout, attach, shop close ...), as of 2026-10-17.
    // A new entry here needs a reason: each of these services must act on the message without answering it twice.
    private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> s_fanOut = new() {
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_CLIENTMOVE"] = ["ControlService", "CombatService", "MoveService", "WizardService", "ZoneService"],
        ["Imcodec.MessageLayer.Generated.PATCH_8_PROTOCOL+MSG_LATEST_FILE_LIST_V2"] = ["PatchService", "LauncherPatchService"],
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_REQUESTDIRECTEDCHAT"] = ["AmbientGroupService", "ChatService"],
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_BUDDYREQUESTLIST"] = ["AmbientGroupService", "FriendsService"],
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_ATTACH"] = ["AttachService", "TutorialService"],
        ["Imcodec.MessageLayer.Generated.WIZARD_12_PROTOCOL+MSG_DONESHOPPING"] = ["BankService", "ShopService", "TreasureShopService"],
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_BUDDYSTATS"] = ["ChatService", "CommandService", "FriendsService"],
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_CLIENT_DISCONNECT"] = ["ClientService", "CombatService", "FriendsService"],
        ["Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL+MSG_QUERY_LOGOUT"] = ["ClientService", "CombatService", "FriendsService"],
        ["Imcodec.MessageLayer.Generated.WIZARDHOUSING_50_PROTOCOL+MSG_REQUEST_BLOBS"] = ["GardeningService", "HousingService"],
    };

    [Fact]
    public void NoTwoSessionServicesHandleTheSameMessage() {
        var services = typeof(MessageService).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(MessageService).IsAssignableFrom(t));
        var handled = services
            .SelectMany(service => service.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(method => method.GetCustomAttributes<MessageHandlerAttribute>())
                .Select(attribute => (Message: attribute.MessageType, Service: service)))
            .Distinct()
            // Client protocol only: the server's own messages (attach, combat results, dispose ...) fan out on purpose.
            .Where(entry => entry.Message.Namespace == "Imcodec.MessageLayer.Generated")
            .GroupBy(entry => entry.Message)
            .Where(group => group.Count() > 1 && !(s_fanOut.TryGetValue(group.Key.FullName!, out var allowed)
                && allowed.SetEquals(group.Select(entry => entry.Service.Name))))
            .Select(group => $"{group.Key.FullName}: {string.Join(", ", group.Select(entry => entry.Service.Name))}")
            .ToArray();
        Assert.True(handled.Length == 0, "Handled by more than one session service:\n" + string.Join("\n", handled));
    }
}
