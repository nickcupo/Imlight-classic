// CLASSIC: private house identity is server state, not a new native login/attach field.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Trading;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HouseTransportTests {
    private const string Room = "Housing/TestHouse/Exterior";
    private static readonly DateTime Now = new(2026, 10, 6, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PendingEntryRequiresExactCharacterOwnerAndRoomAndCannotReplay() {
        var entries = new HouseTransferRegistry();
        Assert.True(entries.Queue(1, 2, 300, Room, Now));
        Assert.False(entries.TryConsume(3, 2, Room, Now, out _));
        Assert.False(entries.TryConsume(1, 9, Room, Now, out _));
        Assert.False(entries.TryConsume(1, 2, "Housing/TestHouse/Interior", Now, out _));
        Assert.True(entries.TryConsume(1, 2, Room.ToLowerInvariant(), Now, out var deed));
        Assert.Equal(300UL, deed);
        Assert.False(entries.TryConsume(1, 2, Room, Now, out _));
    }

    [Fact]
    public void PendingEntriesExpireAndBoundStateWhileAllowingReplacementAndCancellation() {
        var entries = new HouseTransferRegistry(2, TimeSpan.FromMinutes(1));
        Assert.False(entries.Queue(0, 2, 3, Room, Now));
        Assert.False(entries.Queue(1, 0, 3, Room, Now));
        Assert.False(entries.Queue(1, 2, 0, Room, Now));
        Assert.False(entries.Queue(1, 2, 3, "", Now));
        Assert.True(entries.Queue(1, 2, 3, Room, Now));
        Assert.True(entries.Queue(4, 2, 5, Room, Now));
        Assert.False(entries.Queue(6, 2, 7, Room, Now));
        Assert.True(entries.Queue(1, 2, 8, Room, Now));
        Assert.True(entries.TryConsume(1, 2, Room, Now, out var replacement));
        Assert.Equal(8UL, replacement);
        entries.Cancel(4);
        Assert.False(entries.TryConsume(4, 2, Room, Now, out _));
        Assert.True(entries.Queue(4, 2, 5, Room, Now));
        Assert.False(entries.TryConsume(4, 2, Room, Now.AddMinutes(1), out _));
        Assert.True(entries.Queue(6, 2, 7, Room, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task ConcurrentAttachAttemptsConsumeOneProofOnly() {
        var entries = new HouseTransferRegistry();
        Assert.True(entries.Queue(1, 2, 3, Room, Now));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(
            () => entries.TryConsume(1, 2, Room, Now, out _), TestContext.Current.CancellationToken)));
        Assert.Single(attempts.Where(consumed => consumed));
    }

    [Fact]
    public void VisitorPresenceAndInstanceComparisonsUseTheOriginalDeed() {
        OnlinePlayer[] visitors = [new() { CharacterId = 1, InstanceOwnerId = 2, HousingDeedId = 300 }];
        Assert.False(HouseTransferEntries.OwnerPresent(visitors, 2, 300));
        OnlinePlayer[] owner = [new() { CharacterId = 2, InstanceOwnerId = 2, HousingDeedId = 301 }];
        Assert.False(HouseTransferEntries.OwnerPresent(owner, 2, 300));
        Assert.True(HouseTransferEntries.OwnerPresent(owner, 2, 301));
        Assert.False(HouseTransferEntries.OwnerPresent(owner, 2, 0));
        Assert.False(HouseTransferEntries.SameInstance(2, 300, 2, 301));
        Assert.True(HouseTransferEntries.SameInstance(2, 300, 2, 300));
        Assert.True(HouseTransferEntries.SameInstance(2, 0, 2, 0));
        Assert.False(HouseTransferEntries.SameInstance(2, 300, 3, 300));
    }

    [Fact]
    public async Task ActualContainersKeepTwoSameOwnerLotsDistinctAndRejectMisdirectedTransfers() {
        EquipmentAttachConcurrencyTests.Configure();
        using var system = ActorSystem.Create("house-transport", "akka.actor.provider = local");
        var first = system.ActorOf(InstanceContainer.Props(2, 300), "first-lot");
        var second = system.ActorOf(InstanceContainer.Props(2, 301), "second-lot");
        var ordinary = system.ActorOf(InstanceContainer.Props(2), "ordinary-instance");
        try {
            foreach (var (actor, deed) in new[] { (first, 300UL), (second, 301UL), (ordinary, 0UL) }) {
                var reply = await actor.Ask<ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONERSP>(
                    new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE {
                        OwnerCharId = 2, HousingDeedId = deed, ZoneName = Room,
                    }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(2UL, reply.OwnerCharId);
                Assert.Equal(deed, reply.HousingDeedId);
                Assert.False(reply.HasZone);
            }
            // This reaches the production handler before it can look up a zone, and must fail without a restart.
            var refusal = await first.Ask<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(
                new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                    OwnerCharId = 2, HousingDeedId = 301, DestinationZone = Room,
                }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotEqual(0U, refusal.ErrorCode);
            var stillAlive = await first.Ask<ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONERSP>(
                new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE { ZoneName = Room },
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(300UL, stillAlive.HousingDeedId);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public void LocalChatCannotCrossIdenticalHouseTemplates() {
        OnlinePlayer[] players = [
            new() { CharacterId = 1, InstanceOwnerId = 2, HousingDeedId = 300 },
            new() { CharacterId = 3, InstanceOwnerId = 2, HousingDeedId = 300 },
            new() { CharacterId = 4, InstanceOwnerId = 2, HousingDeedId = 301 },
            new() { CharacterId = 5, InstanceOwnerId = 2, HousingDeedId = 300 },
        ];
        Assert.Equal(new ulong[] { 3 }, ChatService.ChatAudience(players, 1, 2, new ulong[] { 5 }, 300)
            .Select(p => p.CharacterId));
    }

    [Fact]
    public void TradeIsRefusedAcrossTwoLotsWithTheSameOwnerAndZone() {
        EquipmentAttachConcurrencyTests.Configure();
        var world = new TradeWorld();
        var manager = new TreasureTradeManager(world);
        manager.Create(1, Wizard.GetGameObjectId(2));
        var refusal = Assert.IsType<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(Assert.Single(world.Sent).Message);
        Assert.Equal(TradeStatus.NotInZone, refusal.Status);
        Assert.False(manager.IsTrading(1));
        world.SecondDeed = 300;
        world.Sent.Clear();
        manager.Create(1, Wizard.GetGameObjectId(2));
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_TRADE_REQUEST>(Assert.Single(world.Sent).Message);
        Assert.True(manager.IsTrading(1));
    }

    private sealed class TradeWorld : ITradeWorld {
        internal ulong SecondDeed = 301;
        internal readonly List<(ulong To, IMessage Message)> Sent = [];
        public TradeParty Party(ulong character) => new(character, null, Room, 2, null,
            character == 1 ? 300 : SecondDeed);
        public bool AreFriends(ulong character, ulong other) => true;
        public bool TradingEnabled => true;
        public uint TreasureTemplateOf(uint spell) => spell;
        public void Send(ulong character, IMessage message) => Sent.Add((character, message));
        public bool Commit(Wizard first, IReadOnlyList<uint> gives, Wizard second, IReadOnlyList<uint> takes)
            => throw new InvalidOperationException("This location regression never commits a trade.");
    }
}
