using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic.Housing;

internal enum HouseCurrency { Gold, Crowns }
internal sealed class HouseRecord {
    public ulong OwnerId { get; set; }
    public ulong DeedId { get; set; }
    public uint TemplateId { get; set; }
    public string ItemDocumentId { get; set; } = "";
    public ulong LotInstanceId { get; set; }
    public string ExteriorZone { get; set; } = "";
    public string InteriorZone { get; set; } = "";
    public string PreviewZone { get; set; } = "";
    public bool Sold { get; set; }
    public DateTime? SoldAt { get; set; }
    internal static string DocumentId(ulong owner, ulong deed) => $"ClassicHouses/{owner}/{deed}";
    internal HouseRecord Copy() => (HouseRecord)MemberwiseClone();
}
internal sealed class HousePortfolio {
    public ulong OwnerId { get; set; }
    public List<ulong> DeedIds { get; set; } = [];
    internal static string DocumentId(ulong owner) => $"ClassicHouses/{owner}/portfolio";
    internal HousePortfolio Copy() => new() { OwnerId = OwnerId, DeedIds = [.. DeedIds] };
}
internal sealed class HouseLocation {
    public ulong CharacterId { get; set; }
    public ulong OwnerId { get; set; }
    public ulong DeedId { get; set; }
    public string Zone { get; set; } = "";
    internal static string DocumentId(ulong character) => $"ClassicHouseLocations/{character}";
    internal HouseLocation Copy() => (HouseLocation)MemberwiseClone();
}
internal sealed record HousePurchaseResult(string Error, HouseRecord Record = null,
    WizClientObjectItem Item = null, ByteString ItemData = default) { internal bool Saved => Error is null && Record is not null; }
internal sealed record HouseSaleResult(string Error, HouseRecord Record = null, AtticLedger Attic = null,
    IReadOnlyList<AtticPatch> Added = null, int Gold = 0) { internal bool Saved => Error is null && Record is not null; }
internal sealed record HouseEquipResult(string Error, WizClientObjectItem Item = null,
    WizClientObjectItem Replaced = null, ByteString ItemData = default, ByteString ReplacedData = default) {
    internal bool Saved => Error is null && Item is not null;
}

// CLASSIC: deed identity is the original owned item, not its template (two identical houses
// are independent). Purchases, selection and approved empty-house sales each use a single Raven save.
internal static class HouseCollection {
    internal static bool MayEnter(ulong character, ulong owner, ulong deed, string zone)
        => character != 0 && TryGetOwned(owner, deed, out var house) && HouseCatalog.TryRoom(house.TemplateId, zone, out _);

    internal static bool RecordLocation(ulong character, ulong owner, ulong deed, string zone) {
        if (character == 0 || zone is null || deed != 0 && !MayEnter(character, owner, deed, zone)) return false;
        try {
            return WizardCollection.CommitCharacterMutation(character, (session, wizard) => {
                if (wizard.CharId != character) return false;
                // Called only after validated attach. Public/dorm entry explicitly clears the old lot.
                // CLASSIC: load before updating so Raven retains the existing change vector. Storing
                // an untracked replacement under optimistic concurrency is a create-only write.
                var id = HouseLocation.DocumentId(character);
                var location = session.Load<HouseLocation>(id);
                if (location is null) {
                    location = new HouseLocation { CharacterId = character };
                    session.Store(location, id);
                }
                else if (location.CharacterId != character) return false;
                location.OwnerId = deed == 0 ? 0 : owner;
                location.DeedId = deed;
                location.Zone = zone;
                return true;
            }, null);
        }
        catch (Exception ex) { Log(character, "location save", ex); return false; }
    }
    internal static bool HasSavedLocation(ulong character, ulong owner, string zone) {
        using var session = HousingCollection.Open();
        var location = session.Load<HouseLocation>(HouseLocation.DocumentId(character));
        return location?.CharacterId == character && location.OwnerId == owner && location.DeedId != 0
            && HouseCatalog.Same(location.Zone, zone);
    }
    internal static bool TryGetSavedLocation(ulong character, ulong owner, string zone, out ulong deed) {
        deed = 0;
        if (character == 0 || character != owner) return false; // visitors always require fresh pending transport proof
        using var session = HousingCollection.Open();
        var location = session.Load<HouseLocation>(HouseLocation.DocumentId(character));
        if (location?.CharacterId != character || location.OwnerId != owner || location.DeedId == 0 || !HouseCatalog.Same(location.Zone, zone)
            || !MayEnter(character, owner, location.DeedId, zone)) return false;
        deed = location.DeedId; return true;
    }
    internal static bool IsDeed(WizClientObjectItem item) => item is not null
        && CoreObjectFactory.GetCoreTemplate(item.m_templateID) is WizItemTemplate template
        && HouseCatalog.IsDeed(template);

    internal static bool TryGetOwned(ulong owner, ulong deed, out HouseRecord record) {
        record = null;
        if (owner == 0 || deed == 0) return false;
        using var session = HousingCollection.Open();
        var wizard = LoadWizard(session, owner);
        record = Owned(session, wizard, owner, deed);
        return record is not null;
    }
    internal static bool TryGetEquipped(Wizard live, out HouseRecord record) {
        record = null;
        if (live is null) return false;
        using var session = HousingCollection.Open();
        var wizard = LoadWizard(session, live.CharId);
        if (wizard?.EquipmentBehavior?.SlotList is not { } slots) return false;
        var selected = slots.Where(s => s.SlotType == EquipmentSlotType.Islands).ToArray();
        if (selected.Length != 1 || !wizard.EquipmentBehavior.EquippedItemIds.Contains(selected[0].ItemId)) return false;
        record = Owned(session, wizard, live.CharId, selected[0].ItemId);
        return record is not null;
    }
    internal static HouseRecord Owned(IDocumentSession session, Wizard wizard, ulong owner, ulong deed) {
        if (wizard?.CharId != owner || wizard.InventoryBehavior?.InventoryItemIds is not { } backpack
            || wizard.EquipmentBehavior?.EquippedItemIds is not { } equipment || !backpack.Contains(deed) && !equipment.Contains(deed)) return null;
        var portfolio = session.Load<HousePortfolio>(HousePortfolio.DocumentId(owner));
        var record = session.Load<HouseRecord>(HouseRecord.DocumentId(owner, deed));
        if (portfolio?.OwnerId != owner || !portfolio.DeedIds.Contains(deed) || record?.OwnerId != owner
            || record.DeedId != deed || record.Sold || record.LotInstanceId == 0
            || !HouseCatalog.TryGet(record.TemplateId, out var definition)
            || !HouseCatalog.Same(record.ExteriorZone, definition.ExteriorZone)
            || !HouseCatalog.Same(record.InteriorZone, definition.InteriorZone)) return null;
        var item = session.Load<WizClientObjectItem>(record.ItemDocumentId);
        return item is not null && item.m_characterId == owner && item.m_globalID == deed
            && item.m_templateID == record.TemplateId ? record : null;
    }
    internal static Wizard LoadWizard(IDocumentSession session, ulong owner)
        => WizardCollection.TestStoreScope.Value is { } test ? test.Load(session, owner)
            : session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
                .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).FirstOrDefault(w => w.CharId == owner);

    internal static HousePurchaseResult Purchase(Wizard live, WizClientObjectItem freshDeed, HouseCurrency currency,
        Func<IDocumentSession, ulong, Account> loadAccount = null) {
        if (live is null || live.IsInDuel || freshDeed is null || !IsDeed(freshDeed)
            || freshDeed.m_templateID.Full > uint.MaxValue || !HouseCatalog.TryGet((uint)freshDeed.m_templateID.Full, out var definition))
            return new("This house is not available in this Classic profile.");
        var price = currency == HouseCurrency.Gold ? definition.Gold : definition.Crowns;
        if (price <= 0 || currency is not (HouseCurrency.Gold or HouseCurrency.Crowns)) return new("That purchase currency is unavailable.");
        var result = new HousePurchaseResult("Your house could not be saved.");
        var resultBalance = 0;
        try {
            if (freshDeed.m_globalID == 0) freshDeed.m_globalID = RandomGen.GenerateGUID();
            if (freshDeed.m_inactiveBehaviors is null || !CoreObjectFactory.FindBehaviorInstance(freshDeed, out DeedBehavior _))
                CoreObjectFactory.InitializeCoreObjectBehaviors(freshDeed, freshDeed.m_templateID);
            bool Commit() => WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (wizard.MagicSchoolBehavior is null || wizard.MagicSchoolBehavior.Level < definition.MinimumLevel || wizard.InventoryBehavior?.InventoryItemIds is not { } ids
                    || wizard.EquipmentBehavior?.EquippedItemIds is null || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed
                    || ids.Contains(freshDeed.m_globalID) || HousingCollection.OutsideBackpack(wizard, freshDeed.m_globalID)) return false;
                var portfolio = session.Load<HousePortfolio>(HousePortfolio.DocumentId(live.CharId));
                var isNew = portfolio is null; portfolio ??= new() { OwnerId = live.CharId };
                if (portfolio.OwnerId != live.CharId || portfolio.DeedIds.Count != portfolio.DeedIds.Distinct().Count()
                    || portfolio.DeedIds.Count >= HouseCatalog.MaximumOwned) return false;
                // Native CountIslands counts inventory plus equipped deeds. Also count original
                // legacy deeds without a new portfolio entry; absence of our record is not permission for a fourth.
                var heldIds = ids.Concat(wizard.EquipmentBehavior.EquippedItemIds).ToHashSet();
                var held = (live.InventoryBehavior?.Items?.ToArray() ?? []).Concat(live.EquipmentBehavior?.EquippedItems?.ToArray() ?? []);
                var oldDeeds = held.Where(i => heldIds.Contains(i.m_globalID) && IsDeed(i)).Select(i => (ulong)i.m_globalID);
                if (portfolio.DeedIds.Concat(oldDeeds).Distinct().Count() >= HouseCatalog.MaximumOwned) return false;
                Account account = null;
                if (currency == HouseCurrency.Gold) {
                    if (wizard.GameStats?.m_currentGold < price) return false;
                    wizard.GameStats.m_currentGold -= price;
                }
                else {
                    if (live.Account is null || wizard.AccountId != live.Account.AccountId) return false;
                    account = loadAccount is null ? session.Query<Account>(collectionName: AccountCollection.CollectionName)
                        .FirstOrDefault(a => a.AccountId == wizard.AccountId) : loadAccount(session, wizard.AccountId);
                    if (account?.AccountId != wizard.AccountId || !account.CharacterIds.Contains(live.CharId) || account.Crowns < price) return false;
                    account.Crowns -= price;
                    resultBalance = account.Crowns;
                }
                freshDeed.m_characterId = live.CharId;
                if (!CoreObjectFactory.FindBehaviorInstance(freshDeed, out DeedBehavior behavior)) return false;
                var lot = RandomGen.GenerateGUID(); behavior.m_lotInstanceGID = lot;
                var record = new HouseRecord { OwnerId = live.CharId, DeedId = freshDeed.m_globalID,
                    // GID does not override parameterless ToString: interpolate the numeric
                    // value so every original deed has its own document, including identical templates.
                    TemplateId = definition.TemplateId, ItemDocumentId = $"ClassicHouseItems/{live.CharId}/{freshDeed.m_globalID.Full}",
                    LotInstanceId = lot, ExteriorZone = definition.ExteriorZone, InteriorZone = definition.InteriorZone,
                    PreviewZone = definition.PreviewZone };
                // No save occurs if native full-behavior serialization fails.
                if (!Serialize(freshDeed, out var data) || session.Load<HouseRecord>(HouseRecord.DocumentId(live.CharId, record.DeedId)) is not null
                    || session.Load<WizClientObjectItem>(record.ItemDocumentId) is not null) return false;
                session.Store(freshDeed, record.ItemDocumentId);
                session.Advanced.GetMetadataFor(freshDeed)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardItemCollection.CollectionName;
                session.Store(record, HouseRecord.DocumentId(live.CharId, record.DeedId));
                portfolio.DeedIds = [.. portfolio.DeedIds, record.DeedId];
                if (isNew) session.Store(portfolio, HousePortfolio.DocumentId(live.CharId));
                wizard.InventoryBehavior.InventoryItemIds = [.. ids, record.DeedId];
                result = new(null, record.Copy(), freshDeed, data);
                return true;
            }, wizard => {
                live.GameStats.m_currentGold = wizard.GameStats.m_currentGold;
                if (currency == HouseCurrency.Crowns && live.Account is { } account) {
                    // The same transaction's tracked account is published below through saved balance.
                    account.Crowns = resultBalance;
                }
                if (!live.InventoryBehavior.AddItem(result.Item)) throw new InvalidOperationException("Saved house needs inventory resynchronization.");
            });
            // Account before character is the shared lock order. Optimistic concurrency also
            // prevents another account session from spending the same saved Crowns balance.
            var saved = currency == HouseCurrency.Crowns && live.Account is not null
                ? AccountCollection.WithAccountWriteLane(live.Account.AccountId, Commit) : Commit();
            return saved ? result : new("You cannot buy this house: check your level, balance, backpack space and three-house limit.");
        }
        catch (Exception ex) { Log(live.CharId, "purchase", ex); return new("Your house could not be saved. Please try again."); }

    }

    internal static HouseSaleResult Sell(Wizard live, ulong deedId, int serverCalculatedGold, int atticCapacity = HousingRules.ApprovedAtticCapacity) {
        if (live is null || live.IsInDuel || serverCalculatedGold < 0) return new("This house cannot be sold now.");
        var result = new HouseSaleResult("This house cannot be sold now.");
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var record = Owned(session, wizard, live.CharId, deedId);
                if (record is null || !HouseCatalog.TryGet(record.TemplateId, out var definition) || !definition.ResaleAllowed
                    || !HousingCollection.InBackpack(wizard, deedId)) return false;
                var rooms = new[] { record.ExteriorZone, record.InteriorZone }
                    .Select(zone => session.Load<HousingLedger>(HousingLedger.DocumentId(new HousingRoomIdentity(live.CharId, deedId, zone)))).Where(r => r is not null).ToArray();
                if (rooms.Any(r => r.OwnerId != live.CharId || r.DeedId != deedId)) return false;
                // CLASSIC: May 2010 live notes require an empty house before sale; Pick Up All is separate for
                // inside/outside. https://web.archive.org/web/20140122055808/https://www.wizard101.com/game/community/updatenotes/may2010
                // Period corroboration: Gamma's 2010-04-29 Housing Updates post (thread17854), and the 2010-08-22
                // Pick Up All question/reply in https://www.wizard101.com/forum/ravenwood-commons/housing-updates-17854
                // (dated search-index evidence, no live-host fetch). The older furnished-sale-to-attic rule is not October's.
                // Check both saved rooms under the character write lane before changing any document or balance.
                // The retained atticCapacity argument does not permit sale to bypass this rule.
                if (rooms.Any(r => r.Count > 0)) {
                    result = new("Please empty both the inside and outside of your house before selling it.");
                    return false;
                }
                var portfolio = session.Load<HousePortfolio>(HousePortfolio.DocumentId(live.CharId));
                portfolio.DeedIds = portfolio.DeedIds.Where(id => id != deedId).ToList();
                record.Sold = true; record.SoldAt = DateTime.UtcNow; // Keep deed/item/room documents as inaccessible archives.
                wizard.InventoryBehavior.InventoryItemIds = wizard.InventoryBehavior.InventoryItemIds.Where(id => id != deedId).ToList();
                var balance = Math.Min((long)wizard.GameStats.m_baseGoldPouch, (long)wizard.GameStats.m_currentGold + serverCalculatedGold);
                if (balance < 0 || balance > int.MaxValue) return false;
                wizard.GameStats.m_currentGold = (int)balance;
                result = new(null, record.Copy(), null, [], serverCalculatedGold);
                return true;
            }, wizard => {
                live.GameStats.m_currentGold = wizard.GameStats.m_currentGold;
                if (!live.InventoryBehavior.RemoveItem(deedId, out _)) throw new InvalidOperationException("Saved house sale needs inventory resynchronization.");
            });
            return saved ? result : result.Saved ? new("This house cannot be sold now.") : result;
        }
        catch (Exception ex) { Log(live.CharId, "sale", ex); return new("Your house sale could not be saved. Please try again."); }
    }

    internal static HouseEquipResult SetEquipped(Wizard live, ulong deedId, bool equip) {
        if (live is null || live.IsInDuel) return new("You cannot select a house now.");
        var result = new HouseEquipResult("You cannot select that house.");
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var record = Owned(session, wizard, live.CharId, deedId);
                if (record is null || wizard.EquipmentBehavior.SlotList is not { } slots) return false;
                var selected = slots.Where(s => s.SlotType == EquipmentSlotType.Islands).ToArray();
                if (selected.Length > 1) return false;
                ulong oldId = selected.SingleOrDefault()?.ItemId ?? 0;
                var item = session.Load<WizClientObjectItem>(record.ItemDocumentId);
                WizClientObjectItem old = null; ByteString oldData = default;
                var ids = wizard.InventoryBehavior.InventoryItemIds;
                if (equip) {
                    if (!HousingCollection.InBackpack(wizard, deedId) || oldId == deedId) return false;
                    if (oldId != 0) {
                        var oldRecord = Owned(session, wizard, live.CharId, oldId);
                        if (oldRecord is null || ids.Contains(oldId)) return false;
                        old = session.Load<WizClientObjectItem>(oldRecord.ItemDocumentId);
                        if (!Serialize(old, out oldData)) return false;
                    }
                    wizard.InventoryBehavior.InventoryItemIds = [.. ids.Where(id => id != deedId), .. (oldId == 0 ? Array.Empty<ulong>() : new[] { oldId })];
                    wizard.EquipmentBehavior.EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds.Where(id => id != oldId), deedId];
                    wizard.EquipmentBehavior.SlotList = [.. slots.Where(s => s.SlotType != EquipmentSlotType.Islands),
                        new EquipmentSlot { SlotType = EquipmentSlotType.Islands, ItemId = deedId, ItemName = item.m_debugName, EquippedSince = DateTime.UtcNow }];
                }
                else {
                    if (oldId != deedId || ids.Contains(deedId) || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed) return false;
                    wizard.InventoryBehavior.InventoryItemIds = [.. ids, deedId];
                    wizard.EquipmentBehavior.EquippedItemIds = wizard.EquipmentBehavior.EquippedItemIds.Where(id => id != deedId).ToList();
                    wizard.EquipmentBehavior.SlotList = slots.Where(s => s.SlotType != EquipmentSlotType.Islands).ToList();
                }
                if (!Serialize(item, out var data)) return false;
                result = new(null, item, old, data, oldData); return true;
            }, savedWizard => {
                if (equip) {
                    if (!live.InventoryBehavior.RemoveItem(deedId, out _)) throw new InvalidOperationException("House selection needs inventory resynchronization.");
                    if (result.Replaced is not null) {
                        if (!live.EquipmentBehavior.UnequipItem(result.Replaced.m_globalID) || !live.InventoryBehavior.AddItem(result.Replaced))
                            throw new InvalidOperationException("House swap needs inventory resynchronization.");
                    }
                    if (!live.EquipmentBehavior.EquipItem(result.Item, EquipmentSlotType.Islands)) throw new InvalidOperationException("House selection needs equipment resynchronization.");
                }
                else if (!live.EquipmentBehavior.UnequipItem(deedId) || !live.InventoryBehavior.AddItem(result.Item))
                    throw new InvalidOperationException("House deselection needs inventory resynchronization.");
            });
            return saved ? result : new("You cannot select that house: check ownership and backpack space.");
        }
        catch (Exception ex) { Log(live.CharId, "selection", ex); return new("Your house selection could not be saved. Please try again."); }
    }

    private static bool Serialize(WizClientObjectItem item, out ByteString data)
        => new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None).Serialize(item, (PropertyFlags)24, out data);
    private static void Log(ulong owner, string action, Exception ex)
        => Logger.Error("House {0} for {1} was not completed: {2}", Logger.Args(action, owner, ex.Message));
}
