// CLASSIC: r806919 Bazaar command routing and its distinct quote/commit boundary.
// Template tags are protocol envelopes, never permission to truncate an arbitrary GID.
using System;
using System.Collections.Generic;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Bazaar;

namespace Imlight.CoreLib.Classic;

internal enum BazaarNativeAction { Contents, QuoteBuy, QuoteSell, Buy, Sell }

internal readonly record struct BazaarNativeRequest(BazaarNativeAction Action, ulong WireTemplateId,
    uint TemplateId, ulong OwnedGlobalId, int Kind, int Quantity, bool Bulk, int Texture, int Decal) {
    internal bool IsSell => Action is BazaarNativeAction.QuoteSell or BazaarNativeAction.Sell;
    internal bool IsQuote => Action is BazaarNativeAction.QuoteBuy or BazaarNativeAction.QuoteSell;
    internal uint TreasureNameHash { get; init; }
}

internal delegate bool BazaarNativeQuote(BazaarNativeRequest request, out BazaarTransactionQuote quote);
internal delegate bool BazaarNativeCommit(BazaarNativeRequest request, out BazaarTransactionReceipt receipt);

internal static class ClassicBazaarProtocol {
    private const ulong BuyRowTag = 9UL << 40;
    private const ulong TreasureSellTag = 0x60UL << 40;

    internal static bool IsSupportedCommand(int command) => command is 0 or 1 or 2 or 3 or 4 or 7 or 8;

    internal static bool TryDecode(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message, out BazaarNativeRequest request) {
        request = default;
        if (!IsSupportedCommand(message.Command)) return false;
        if (message.Command == 0) {
            request = new(BazaarNativeAction.Contents, 0, 0, 0, 0, 0, false, 0, 0);
            return true;
        }

        if (message.itemType is < 0 or > 3) return false; // Jewels and later noncombat categories stay closed.
        var action = message.Command switch {
            1 => BazaarNativeAction.QuoteBuy,
            2 => BazaarNativeAction.QuoteSell,
            3 or 7 => BazaarNativeAction.Buy,
            _ => BazaarNativeAction.Sell,
        };
        var isSell = action is BazaarNativeAction.QuoteSell or BazaarNativeAction.Sell;
        var bulk = message.Command is 7 or 8;
        // The traced single commands buy/sell one, and their builders never assign quantity. Serialized
        // zero remains an inference pending GUI acceptance; accepting raw 0/1 never permits a bulk quantity.
        if (bulk ? message.quantity == 0 || message.quantity > int.MaxValue : message.quantity > 1) return false;
        var quantity = bulk ? (int) message.quantity : 1;
        if (isSell && message.itemType is 0 or 1 && (message.itemGlobalID == 0 || quantity != 1)) return false;
        if (isSell && message.itemType == 3 && message.itemGlobalID == 0) return false;
        if (!TryTemplateId(message.itemTemplateID, isSell, message.itemType, out var templateId)) return false;

        request = new(action, message.itemTemplateID, templateId, message.itemGlobalID,
            message.itemType, quantity, bulk, message.texture, message.decal);
        return true;
    }

    internal static bool TryTemplateId(ulong wireId, bool isSell, int nativeKind, out uint templateId) {
        templateId = 0;
        if (nativeKind is < 0 or > 3) return false;
        var upper = wireId & ~((ulong) uint.MaxValue);
        // Our contents row's Type=9 is echoed for a buy. The native treasure seller uses Type=0x60.
        // Block/unknown bits, other tags, and Type=0x60 outside treasure sales are refused intact.
        if (upper != 0 && !(upper == BuyRowTag && !isSell)
            && !(upper == TreasureSellTag && isSell && nativeKind == 2)) return false;
        templateId = (uint) wireId;
        return templateId != 0;
    }

    internal static bool MatchesTemplateKind(BazaarNativeRequest request, CoreTemplate template) {
        var expected = request.Kind switch {
            0 => BazaarKind.Gear,
            1 => BazaarKind.Housing,
            2 => BazaarKind.TreasureCard,
            3 => BazaarKind.Reagent,
            _ => (BazaarKind?) null,
        };
        return expected is not null && ClassicBazaar.KindOf(template) == expected
            && (template is not SpellTemplate spell || !string.IsNullOrEmpty(spell.m_name));
    }

    // Both quote commands are read-only. The sole success-publication path follows a committed receipt.
    // No quote key is remembered: r806919 intentionally sends a different key on confirmation.
    internal static void Dispatch(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message,
        Func<bool> authorize, Func<uint, CoreTemplate> template,
        BazaarNativeQuote quote, BazaarNativeCommit commit,
        System.Action contents, System.Action<BazaarNativeRequest, BazaarTransactionQuote> sendQuote,
        System.Action<BazaarNativeRequest, BazaarTransactionReceipt> publish,
        System.Action failure, Func<bool> isQuarantined, System.Action close) {
        if (!authorize()) { if (isQuarantined()) close(); else failure(); return; }
        if (!TryDecode(message, out var request)) { failure(); return; }
        if (request.Action == BazaarNativeAction.Contents) { contents(); return; }
        var actualTemplate = template(request.TemplateId);
        if (!MatchesTemplateKind(request, actualTemplate)) { failure(); return; }
        if (actualTemplate is SpellTemplate spell) request = request with { TreasureNameHash = StringHash.Compute(spell.m_name) };
        if (request.IsQuote) {
            if (quote(request, out var price)) sendQuote(request, price);
            else if (isQuarantined()) close();
            else failure();
            return;
        }

        if (commit(request, out var receipt)) publish(request, receipt);
        else if (isQuarantined()) close(); // A lost save acknowledgement cannot be refunded or blindly retried.
        else failure();
    }

    internal static WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE QuoteResponse(BazaarNativeRequest request,
        BazaarTransactionQuote quote) => new() {
        Command = request.IsSell ? (sbyte) 2 : (sbyte) 1,
        ItemTemplateID = request.WireTemplateId,
        Cost = quote.Cost,
        ReturnCode = 0,
    };

    internal static WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEMOREACKNOWLEDGEMENT BulkResponse(
        BazaarNativeRequest request, BazaarTransactionReceipt receipt) => new() {
        ItemTemplateID = request.WireTemplateId,
        IsSell = receipt.IsSell ? (sbyte) 1 : (sbyte) 0,
        TotalPrice = receipt.Cost,
        QuantityPurchased = receipt.Quantity,
    };

    internal static IMessage FailureMessage(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        if (message.Command is 1 or 2) return new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = message.Command, ItemTemplateID = message.itemTemplateID, Cost = 0, ReturnCode = 1,
        }; // Existing generic quote refusal pattern; precise ReturnCode meanings remain unverified.
        if (message.Command == 3) return new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1 };
        if (message.Command == 4) return new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { Failure = 1 };
        // Bulk refusal completion is not proved by the native trace. Preserve the existing refusal patterns;
        // do not invent a zero-quantity MOREACK success or a new ReturnCode meaning.
        if (message.Command == 7) return new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1 };
        if (message.Command == 8) return new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 2, ItemTemplateID = message.itemTemplateID, Cost = 0, ReturnCode = 1,
        };
        return null;
    }

    internal static ByteString WriteAuctionBlob(uint categoryHash, IReadOnlyCollection<AuctionHouseEntry> entries) {
        var writer = new BitWriter();
        writer.WriteUInt32(categoryHash);
        writer.WriteUInt32((uint) entries.Count);
        foreach (var entry in entries) {
            // The persisted template is canonical. Preserve the known native Type=9 envelope on the wire.
            writer.WriteUInt64(entry.m_templateID.Full | BuyRowTag);
            writer.WriteInt32(entry.m_numForSale);
            writer.WriteInt32(entry.m_buyPrice);
            writer.WriteInt32(entry.m_sellPrice);
        }
        return writer.GetData();
    }

    // Packet construction consumes only acknowledged state and prepared object bytes. It performs no save,
    // wallet mutation, item creation or serialization which could fail after payment has been committed.
    internal static IReadOnlyList<IMessage> ReceiptMessages(BazaarNativeRequest request,
        BazaarTransactionReceipt receipt, ulong gameObjectId, ulong characterId, uint treasureNameHash) {
        var messages = new List<IMessage> {
            new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
                CharacterID = characterId,
                UpdateInfo = WriteAuctionBlob(0, [receipt.Stock]),
            }
        };
        foreach (var added in receipt.AddedItems) {
            messages.Add(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = gameObjectId, SerializedItem = added.Data,
            });
            messages.Add(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                ItemGlobalID = added.Item.m_globalID, ItemTemplateID = (uint) receipt.TemplateId, ItemLocation = 1,
            });
        }
        foreach (var removedId in receipt.RemovedItemIds) messages.Add(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = gameObjectId, ItemID = removedId,
        });
        if (receipt.Kind == 2) {
            if (receipt.IsSell) messages.Add(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK {
                SpellID = unchecked((int) treasureNameHash), EnchantmentID = 0, Quantity = receipt.Quantity,
            });
            else for (var i = 0; i < receipt.Quantity; i++) messages.Add(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                SpellID = unchecked((int) treasureNameHash), EnchantmentID = 0,
            });
        }
        if (receipt.ReagentUpdate is { } reagent) {
            if (!receipt.IsSell) {
                messages.Add(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = gameObjectId, Data = receipt.ReagentData });
                messages.Add(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                    ItemGlobalID = reagent.m_globalID, ItemTemplateID = (uint) receipt.TemplateId, ItemLocation = 1,
                });
            }
            if (reagent.m_quantity > 0) messages.Add(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE {
                GlobalID = gameObjectId, ItemID = reagent.m_globalID, Quantity = reagent.m_quantity,
            });
            else messages.Add(new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE {
                GlobalID = gameObjectId, ItemID = reagent.m_globalID,
            });
        }
        messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = receipt.Gold, MaxGold = receipt.MaxGold });
        if (request.Bulk) messages.Add(BulkResponse(request, receipt));
        // Native single confirmation handlers are proved to consume these shop completion messages.
        else if (receipt.IsSell) messages.Add(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM {
            ClientRequestID = 0, GlobalID = 0, Failure = 0,
        });
        else messages.Add(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 0, WebFailure = 0, Credits = 0 });
        return messages;
    }
}
