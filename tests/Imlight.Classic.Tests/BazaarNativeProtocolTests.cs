// CLASSIC: native quote/confirmation commands, strict template envelopes and ACK-only publication.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class BazaarNativeProtocolTests {
    private const uint TreasureId = 1698635320;
    private const ulong BuyWireId = (9UL << 40) | TreasureId;
    private const ulong SellWireId = (0x60UL << 40) | TreasureId;

    private static WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST Request(int command, uint quantity = 1,
        int kind = 2, ulong? wire = null, ulong instance = 0, uint key = 1) => new() {
        Command = (sbyte) command, quantity = quantity, itemType = (sbyte) kind,
        itemTemplateID = wire ?? (command is 2 or 4 or 8 ? SellWireId : BuyWireId),
        itemGlobalID = instance, key = key,
    };

    private static BazaarTransactionReceipt Receipt(bool sell = false, int quantity = 2, int kind = 2,
        IReadOnlyList<BazaarPreparedItem>? added = null, IReadOnlyList<ulong>? removed = null,
        ClientReagentItem? reagent = null) => new(80, quantity, 420, 10000, kind, TreasureId,
        new AuctionHouseEntry { m_templateID = TreasureId, m_numForSale = 7, m_buyPrice = 40, m_sellPrice = 20 },
        added ?? [], removed ?? [], reagent!, sell, new ByteString(new byte[] { 1, 2, 3 }));

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 3, false)]
    [InlineData(4, 4, false)]
    [InlineData(7, 3, true)]
    [InlineData(8, 4, true)]
    public void TracedNativeCommandMapSeparatesQuotesAndCommit(int command, int action, bool bulk) {
        Assert.True(ClassicBazaarProtocol.TryDecode(Request(command), out var decoded));
        Assert.Equal((BazaarNativeAction) action, decoded.Action);
        Assert.Equal(bulk, decoded.Bulk);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SingleCommandAcceptsZeroDefaultOrOneAndRejectsLargerRawAmounts(int command) {
        foreach (var raw in new uint[] { 0, 1 }) {
            Assert.True(ClassicBazaarProtocol.TryDecode(Request(command, raw), out var decoded));
            Assert.Equal(1, decoded.Quantity);
        }
        foreach (var raw in new uint[] { 2, uint.MaxValue })
            Assert.False(ClassicBazaarProtocol.TryDecode(Request(command, raw), out _));
    }

    [Theory]
    [InlineData(7)] [InlineData(8)]
    public void BulkCommandsRequirePositiveRepresentableQuantity(int command) {
        Assert.False(ClassicBazaarProtocol.TryDecode(Request(command, 0), out _));
        Assert.False(ClassicBazaarProtocol.TryDecode(Request(command, uint.MaxValue), out _));
        Assert.True(ClassicBazaarProtocol.TryDecode(Request(command, 17), out var decoded));
        Assert.Equal(17, decoded.Quantity);
    }

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)]
    public void TreasureSaleUsesSavedBookIdentityWithoutAnItemInstance(int command) {
        Assert.True(ClassicBazaarProtocol.TryDecode(Request(command, instance: 0), out var decoded));
        Assert.Equal(TreasureId, decoded.TemplateId);
        Assert.Equal(SellWireId, decoded.WireTemplateId);
        Assert.Equal(0UL, decoded.OwnedGlobalId);
        Assert.True(decoded.IsSell);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void GearAndHousingCannotSellSeveralCopiesUsingOneInstanceId(int kind) {
        Assert.True(ClassicBazaarProtocol.TryDecode(Request(8, kind: kind, wire: 123, instance: 999), out _));
        Assert.False(ClassicBazaarProtocol.TryDecode(Request(8, 2, kind, 123, 999), out _));
        Assert.False(ClassicBazaarProtocol.TryDecode(Request(4, kind: kind, wire: 123, instance: 0), out _));
    }

    [Fact]
    public void OnlyObservedTemplateEnvelopesNormalizeAndTheWireCorrelationStaysIntact() {
        Assert.True(ClassicBazaarProtocol.TryTemplateId(BuyWireId, false, 2, out var canonical));
        Assert.Equal(TreasureId, canonical);
        Assert.True(ClassicBazaarProtocol.TryTemplateId(SellWireId, true, 2, out canonical));
        Assert.True(ClassicBazaarProtocol.TryTemplateId(TreasureId, true, 2, out canonical));
        foreach (var wire in new[] { 0UL, BuyWireId | (1UL << 32), BuyWireId | (1UL << 48),
            (10UL << 40) | TreasureId, SellWireId | (1UL << 32), SellWireId | (1UL << 56) }) {
            Assert.False(ClassicBazaarProtocol.TryTemplateId(wire, false, 2, out _));
            Assert.False(ClassicBazaarProtocol.TryTemplateId(wire, true, 2, out _));
        }
        Assert.False(ClassicBazaarProtocol.TryTemplateId(BuyWireId, true, 2, out _));
        Assert.False(ClassicBazaarProtocol.TryTemplateId(SellWireId, false, 2, out _));
        Assert.False(ClassicBazaarProtocol.TryTemplateId(SellWireId, true, 0, out _));
        Assert.True(ClassicBazaarProtocol.TryDecode(Request(1), out var request));
        var response = ClassicBazaarProtocol.QuoteResponse(request, new(40, 7, TreasureId, 2));
        Assert.Equal(BuyWireId, response.ItemTemplateID);
        Assert.Equal(40, response.Cost);
        Assert.Equal(0, response.ReturnCode);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)] [InlineData(8)]
    public void LaterNativeKindsAreClosedOnEveryQuoteAndConfirmation(int command) {
        foreach (var kind in new[] { -1, 4, 5, 127 })
            Assert.False(ClassicBazaarProtocol.TryDecode(Request(command, kind: kind), out _));
    }

    [Fact]
    public void NativeKindMustMatchTheActualTemplateAndCannotBypassNoAuction() {
        ClassicBazaarProtocol.TryDecode(Request(3), out var request);
        Assert.True(ClassicBazaarProtocol.MatchesTemplateKind(request, new SpellTemplate { m_name = "Fire Cat TC" }));
        Assert.False(ClassicBazaarProtocol.MatchesTemplateKind(request with { Kind = 0 }, new SpellTemplate { m_name = "Fire Cat TC" }));
        Assert.False(ClassicBazaarProtocol.MatchesTemplateKind(request, new ReagentItemTemplate()));
        Assert.False(ClassicBazaarProtocol.MatchesTemplateKind(request, new SpellTemplate { m_name = "" }));
        Assert.False(ClassicBazaarProtocol.MatchesTemplateKind(request with { Kind = 0 }, new WizItemTemplate { m_adjectiveList = ["FLAG_NoAuction"] }));
        Assert.False(ClassicBazaarProtocol.MatchesTemplateKind(request with { Kind = 0 }, new WizItemTemplate { m_adjectiveList = ["FLAG_CrownsOnly"] }));
        Assert.False(ClassicBazaarProtocol.MatchesTemplateKind(request with { Kind = 0 }, new WizItemTemplate { m_adjectiveList = ["Furniture"] }));
        Assert.True(ClassicBazaarProtocol.MatchesTemplateKind(request with { Kind = 1 }, new WizItemTemplate { m_adjectiveList = ["Furniture"] }));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void FailedPriceLockUsesItsMatchingQuoteCommandAndOriginalWireCorrelation(int command) {
        var request = Request(command);
        var response = Assert.IsType<WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE>(ClassicBazaarProtocol.FailureMessage(request));
        Assert.Equal(request.Command, response.Command);
        Assert.Equal(request.itemTemplateID, response.ItemTemplateID);
        Assert.Equal(0, response.Cost);
        Assert.Equal(1, response.ReturnCode);
    }

    [Fact]
    public void FailedSingleBuyUsesTheProvedBuyCompletionFamily() {
        var response = Assert.IsType<WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM>(ClassicBazaarProtocol.FailureMessage(Request(3)));
        Assert.Equal(1, response.Failure);
    }

    [Fact]
    public void FailedSingleSaleUsesTheProvedSellCompletionFamilyWithoutOpeningAnotherQuote() {
        var response = Assert.IsType<WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM>(ClassicBazaarProtocol.FailureMessage(Request(4)));
        Assert.Equal(1, response.Failure);
    }

    private sealed class Probe {
        internal bool Allowed = true, Ack = true, Uncertain;
        internal int GuardCalls, QuoteCalls, CommitCalls, FailureCalls, CloseCalls, ContentsCalls;
        internal int Gold = 500, Stock = 9, Cards = 4;
        internal readonly List<IMessage> Sent = [];
        internal readonly List<string> Events = [];

        internal void Handle(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
            ClassicBazaarProtocol.Dispatch(message,
                () => { GuardCalls++; return Allowed; },
                _ => new SpellTemplate { m_name = "Fire Cat TC" },
                (BazaarNativeRequest request, out BazaarTransactionQuote quote) => {
                    QuoteCalls++; quote = new(40, Stock, request.TemplateId, request.Kind); return true;
                },
                (BazaarNativeRequest request, out BazaarTransactionReceipt receipt) => {
                    CommitCalls++; Events.Add("save requested");
                    Assert.Empty(Sent); // No item/book/gold/stock/confirmation packets while the save is pending.
                    receipt = null!;
                    if (!Ack) return false;
                    Events.Add("save acknowledged");
                    Gold = 420; Stock = 7; Cards += request.IsSell ? -2 : 2;
                    receipt = Receipt(request.IsSell); return true;
                },
                () => ContentsCalls++,
                (request, quote) => Sent.Add(ClassicBazaarProtocol.QuoteResponse(request, quote)),
                (request, receipt) => {
                    Events.Add("publish");
                    Sent.AddRange(ClassicBazaarProtocol.ReceiptMessages(request, receipt, 102, 100, request.TreasureNameHash));
                },
                () => FailureCalls++, () => Uncertain, () => CloseCalls++);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)] [InlineData(8)]
    public void EveryNativeOperationRequiresTheSharedFeatureAuthenticationAndProximityGate(int command) {
        var denied = new Probe { Allowed = false };
        denied.Handle(Request(command));
        Assert.Equal(1, denied.GuardCalls);
        Assert.Equal(0, denied.QuoteCalls + denied.CommitCalls + denied.ContentsCalls);
        Assert.Equal(1, denied.FailureCalls);
        Assert.Empty(denied.Sent);
        Assert.Equal((500, 9, 4), (denied.Gold, denied.Stock, denied.Cards));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void NativePriceLockAndCancelDoNotSellSpendOrRemoveAnything(int command) {
        var probe = new Probe();
        probe.Handle(Request(command));
        var quote = Assert.IsType<WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE>(Assert.Single(probe.Sent));
        Assert.Equal(command, quote.Command);
        Assert.Equal(command == 2 ? SellWireId : BuyWireId, quote.ItemTemplateID);
        Assert.Equal(1, probe.QuoteCalls);
        Assert.Equal(0, probe.CommitCalls);
        Assert.Equal((500, 9, 4), (probe.Gold, probe.Stock, probe.Cards));
        Assert.Empty(probe.Events); // Cancellation is simply absence of a later confirmation.
    }

    [Fact]
    public void AConfirmationWithADifferentNativeKeyStillCommitsOnlyAfterAcknowledgement() {
        var probe = new Probe();
        probe.Handle(Request(1, key: 100));
        probe.Sent.Clear();
        probe.Handle(Request(7, 2, key: 101));
        Assert.Equal(1, probe.QuoteCalls);
        Assert.Equal(1, probe.CommitCalls);
        Assert.Equal(new[] { "save requested", "save acknowledged", "publish" }, probe.Events);
        Assert.Equal((420, 7, 6), (probe.Gold, probe.Stock, probe.Cards));
        var ack = Assert.Single(probe.Sent.OfType<WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEMOREACKNOWLEDGEMENT>());
        Assert.Equal(BuyWireId, ack.ItemTemplateID);
        Assert.Equal(80, ack.TotalPrice);
        Assert.Equal(2, ack.QuantityPurchased);
    }

    [Fact]
    public void LostSaveAcknowledgementClosesOnlyTheAffectedSessionWithoutPublishingRefundingOrRetrying() {
        var probe = new Probe { Ack = false, Uncertain = true };
        probe.Handle(Request(8, 2));
        Assert.Equal(1, probe.CommitCalls);
        Assert.Equal(1, probe.CloseCalls);
        Assert.Equal(0, probe.FailureCalls);
        Assert.Empty(probe.Sent);
        Assert.Equal(new[] { "save requested" }, probe.Events);
        Assert.Equal((500, 9, 4), (probe.Gold, probe.Stock, probe.Cards));
    }

    [Fact]
    public void ARefusedCommitPublishesNoSuccessfulDeliveryOrBalance() {
        var probe = new Probe { Ack = false };
        probe.Handle(Request(7, 2));
        Assert.Equal(1, probe.FailureCalls);
        Assert.Equal(0, probe.CloseCalls);
        Assert.Empty(probe.Sent);
    }

    [Fact]
    public void TreasureBuyAndSellPacketsUseTheSpellNameHashAndTheActualCommittedQuantity() {
        var hash = StringHash.Compute("Fire Cat TC");
        ClassicBazaarProtocol.TryDecode(Request(7, 8), out var buy);
        var packets = ClassicBazaarProtocol.ReceiptMessages(buy, Receipt(quantity: 2), 102, 100, hash);
        var additions = packets.OfType<WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK>().ToList();
        Assert.Equal(2, additions.Count);
        Assert.All(additions, packet => { Assert.Equal(unchecked((int) hash), packet.SpellID); Assert.Equal(0, packet.EnchantmentID); });
        Assert.DoesNotContain(packets, packet => packet is GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM);
        var buyAck = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEMOREACKNOWLEDGEMENT>());
        Assert.Equal(2, buyAck.QuantityPurchased); // Receipt, never the uncommitted request's eight copies.
        Assert.Equal(0, buyAck.IsSell);
        ClassicBazaarProtocol.TryDecode(Request(8, 8), out var sell);
        packets = ClassicBazaarProtocol.ReceiptMessages(sell, Receipt(sell: true, quantity: 2), 102, 100, hash);
        var removal = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK>());
        Assert.Equal(unchecked((int) hash), removal.SpellID);
        Assert.Equal(0, removal.EnchantmentID);
        Assert.Equal(2, removal.Quantity);
        var sellAck = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEMOREACKNOWLEDGEMENT>());
        Assert.Equal(SellWireId, sellAck.ItemTemplateID);
        Assert.Equal(1, sellAck.IsSell);
        Assert.DoesNotContain(packets, packet => packet is WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK);
    }

    [Fact]
    public void ReagentPacketsUseTheCommittedStackIdentityAndCountIncludingLastCopyRemoval() {
        var row = new ClientReagentItem { m_globalID = 987, m_templateID = TreasureId, m_quantity = 17 };
        ClassicBazaarProtocol.TryDecode(Request(7, 2, 3), out var buy);
        var receipt = Receipt(kind: 3, reagent: row);
        var packets = ClassicBazaarProtocol.ReceiptMessages(buy, receipt, 102, 100, 0);
        var add = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_REAGENTADD>());
        Assert.Equal((byte[]) receipt.ReagentData, (byte[]) add.Data);
        var update = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE>());
        Assert.Equal(987UL, update.ItemID);
        Assert.Equal(17, update.Quantity);
        Assert.Equal(987UL, Assert.Single(packets.OfType<WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION>()).ItemGlobalID);
        ClassicBazaarProtocol.TryDecode(Request(8, 2, 3, wire: TreasureId, instance: 987), out var sell);
        row.m_quantity = 0;
        packets = ClassicBazaarProtocol.ReceiptMessages(sell, Receipt(sell: true, kind: 3, reagent: row), 102, 100, 0);
        Assert.Equal(987UL, Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE>()).ItemID);
        Assert.DoesNotContain(packets, packet => packet is WIZARD_12_PROTOCOL.MSG_REAGENTADD or WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE);
    }

    [Fact]
    public void PreparedGearBytesAndStockAreEchoedWithoutCreatingOrSerializingAnObjectAfterPayment() {
        var item = new WizClientObjectItem { m_globalID = 456, m_templateID = TreasureId };
        var data = new ByteString(new byte[] { 7, 8, 9 });
        ClassicBazaarProtocol.TryDecode(Request(3, kind: 0), out var request);
        var receipt = Receipt(quantity: 1, kind: 0, added: [new(item, data)]);
        var packets = ClassicBazaarProtocol.ReceiptMessages(request, receipt, 102, 100, 0);
        Assert.Equal((byte[]) data, (byte[]) Assert.Single(packets.OfType<GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM>()).SerializedItem);
        Assert.Equal(456UL, Assert.Single(packets.OfType<WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION>()).ItemGlobalID);
        var balance = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>());
        Assert.Equal(420, balance.Gold);
        Assert.Equal(10000, balance.MaxGold);
        var stock = new BitReader(Assert.Single(packets.OfType<GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE>()).UpdateInfo);
        Assert.Equal(0U, stock.ReadUInt32());
        Assert.Equal(1U, stock.ReadUInt32());
        Assert.Equal(BuyWireId, stock.ReadUInt64());
        Assert.Equal(7, stock.ReadInt32());
        Assert.Equal(40, stock.ReadInt32());
        Assert.Equal(20, stock.ReadInt32());
    }
}
