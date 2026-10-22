using System;
using System.IO;
using System.Linq;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// CLASSIC (2026-10-22, ios-qa): a friend request the recipient left unanswered at logout is kept on the recipient and
/// shown again at their next login (BuddyListManager::MSG_BuddyRequestAdd 0x141741590), unless it can no longer stand.
/// </summary>
public sealed class FriendRequestPendingTests {

    private static Wizard WizardWith(ulong charId) => new() { CharId = charId, FriendsBehavior = new ServerFriendBehavior() };

    [Fact]
    public void AReceivedRequestIsRecordedOnceAndAnswered() {
        var behavior = new ServerFriendBehavior();
        Assert.True(behavior.AddIncomingFriendRequest(42));
        Assert.False(behavior.AddIncomingFriendRequest(42));
        Assert.False(behavior.AddIncomingFriendRequest(0));
        Assert.True(behavior.HasIncomingFriendRequest(42));
        Assert.True(behavior.RemoveIncomingFriendRequest(42));
        Assert.False(behavior.HasIncomingFriendRequest(42));
        Assert.False(behavior.RemoveIncomingFriendRequest(42));
    }

    [Fact]
    public void TheRecordSurvivesSavingAndOlderDocumentsReadAsEmpty() {
        var behavior = new ServerFriendBehavior();
        behavior.AddIncomingFriendRequest(42);
        var copy = JsonConvert.DeserializeObject<ServerFriendBehavior>(JsonConvert.SerializeObject(behavior))!;
        Assert.Equal([42UL], copy.IncomingFriendRequestsFromCharId);

        var old = JsonConvert.DeserializeObject<ServerFriendBehavior>("{\"PendingFriendRequestsFromCharId\":[42]}")!;
        Assert.Empty(old.IncomingFriendRequestsFromCharId);
        Assert.False(old.HasIncomingFriendRequest(42));
    }

    [Fact]
    public void ARequestIsShownAgainUnlessItCanNoLongerStand() {
        var recipient = WizardWith(1);
        var sender = WizardWith(2);
        Assert.Equal(FriendsService.IncomingRequestFate.Show, FriendsService.FateOfIncoming(recipient, 2, sender, ignored: false));
        Assert.Equal(FriendsService.IncomingRequestFate.Drop, FriendsService.FateOfIncoming(recipient, 2, null, ignored: false)); // deleted
        Assert.Equal(FriendsService.IncomingRequestFate.Drop, FriendsService.FateOfIncoming(recipient, 2, sender, ignored: true));
        Assert.Equal(FriendsService.IncomingRequestFate.Drop, FriendsService.FateOfIncoming(recipient, 1, recipient, ignored: false));

        // Already friends (they added each other another way): the client would deny it by itself; drop it instead.
        recipient.FriendsBehavior.AddOrUpdateRelationship(new Relationship { FirstPlayerId = 1, SecondPlayerId = 2 });
        Assert.Equal(FriendsService.IncomingRequestFate.Drop, FriendsService.FateOfIncoming(recipient, 2, sender, ignored: false));

        // A friendship that was broken up does not stop a new request.
        recipient.FriendsBehavior.Relationships.Single().IsBrokenUp = true;
        Assert.Equal(FriendsService.IncomingRequestFate.Show, FriendsService.FateOfIncoming(recipient, 2, sender, ignored: false));
    }

    [Fact]
    public void TheLoginSendsSavedRequestsAfterTheBuddyListAndASenderMayAskAgain() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Imlight.CoreLib"))) {
            dir = dir.Parent;
        }

        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Imlight.CoreLib", "Game", "Services", "FriendsService.cs"));
        var listEnd = source.IndexOf("        SendBuddyListEnd(wizard);\n        ResendIncomingRequests(wizard);", StringComparison.Ordinal);
        Assert.True(listEnd > 0, "saved requests go out right after MSG_BUDDYLISTCOMPLETE");
        Assert.DoesNotContain("if (!wizard.AddPendingFriendRequest(buddyCharID)) {\n            return;", source);
        Assert.Equal(2, source.Split("!TakeIncoming(wizard, buddyCharID)").Length - 1); // accept and deny use the saved record
    }

}
