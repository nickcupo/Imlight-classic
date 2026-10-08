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
    WizClientObjectItem Item = null, ByteString ItemData = default, int Gold = 0, int MaxGold = 0, int Crowns = 0) {
    internal bool Saved => Error is null && Record is not null;
}
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
    internal static HouseRecord Owned(IDocumentSession session, Wizard wizard, ulong owner, ulong deed,
        bool protectReadOnly = false) {
        if (wizard?.CharId != owner || wizard.InventoryBehavior?.InventoryItemIds is not { } backpack
            || wizard.EquipmentBehavior?.EquippedItemIds is not { } equipment || !backpack.Contains(deed) && !equipment.Contains(deed)) return null;
        var portfolio = session.Load<HousePortfolio>(HousePortfolio.DocumentId(owner));
        var record = session.Load<HouseRecord>(HouseRecord.DocumentId(owner, deed));
        // CLASSIC: selection and furniture validation read ownership without modifying it.
        // Sales keep the default tracked originals because they legitimately change both documents.
        if (protectReadOnly) {
            if (portfolio is not null) session.Advanced.IgnoreChangesFor(portfolio);
            if (record is not null) session.Advanced.IgnoreChangesFor(record);
        }
        if (portfolio?.OwnerId != owner || !portfolio.DeedIds.Contains(deed) || record?.OwnerId != owner
            || record.DeedId != deed || record.Sold || record.LotInstanceId == 0
            || !HouseCatalog.TryGet(record.TemplateId, out var definition)
            || !HouseCatalog.Same(record.ExteriorZone, definition.ExteriorZone)
            || !HouseCatalog.Same(record.InteriorZone, definition.InteriorZone)) return null;
        var item = session.Load<WizClientObjectItem>(record.ItemDocumentId);
        if (item is not null) WizardInventoryTransactions.CaptureReadRows(session, [item]);
        return item is not null && item.m_characterId == owner && item.m_globalID == deed
            && item.m_templateID == record.TemplateId ? record : null;
    }
    internal static Wizard LoadWizard(IDocumentSession session, ulong owner)
        => WizardCollection.TestStoreScope.Value?.Load is { } load ? load(session, owner)
            : session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
                .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).FirstOrDefault(w => w.CharId == owner);

    internal static HousePurchaseResult Purchase(Wizard live, WizClientObjectItem freshDeed, HouseCurrency currency,
        Func<IDocumentSession, ulong, Account> loadAccount = null, Func<HousePurchaseResult, bool> preparePublication = null,
        Action<HousePurchaseResult> afterCommit = null) {
        if (live is null || WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel || freshDeed is null || !IsDeed(freshDeed)
            || freshDeed.m_characterId.Full != 0 && freshDeed.m_characterId.Full != live.CharId
            || freshDeed.m_templateID.Full > uint.MaxValue || !HouseCatalog.TryGet((uint)freshDeed.m_templateID.Full, out var definition))
            return new("This house is not available in this Classic profile.");
        var price = currency == HouseCurrency.Gold ? definition.Gold : definition.Crowns;
        if (price <= 0 || currency is not (HouseCurrency.Gold or HouseCurrency.Crowns)) return new("That purchase currency is unavailable.");
        var result = new HousePurchaseResult("Your house could not be saved.");
        var resultBalance = 0;
        List<WizClientObjectItem> backpack = [];
        try {
            // CLASSIC: native preparation must not mutate the caller's candidate before an acknowledged save.
            var preparedDeed = freshDeed with {
                m_globalID = freshDeed.m_globalID.Full == 0 ? RandomGen.GenerateGUID() : freshDeed.m_globalID,
                m_characterId = live.CharId,
                m_inactiveBehaviors = freshDeed.m_inactiveBehaviors?.Select(behavior => behavior is DeedBehavior deed
                    ? deed with { } : behavior).ToList() ?? [],
            };
            if (!CoreObjectFactory.FindBehaviorInstance(preparedDeed, out DeedBehavior _))
                CoreObjectFactory.InitializeCoreObjectBehaviors(preparedDeed, preparedDeed.m_templateID);
            if (!CoreObjectFactory.FindBehaviorInstance(preparedDeed, out DeedBehavior preparedBehavior))
                return new("This house deed could not be prepared.");
            var lot = RandomGen.GenerateGUID();
            if (preparedDeed.m_globalID.Full == 0 || lot == 0) return new("This house deed could not be prepared.");
            preparedBehavior.m_lotInstanceGID = lot;
            if (!Serialize(preparedDeed, out var data)) return new("This house deed could not be prepared.");
            bool Commit() => WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel || wizard.CharId != live.CharId
                    || wizard.GameStats is null || wizard.MagicSchoolBehavior is null || wizard.MagicSchoolBehavior.Level < definition.MinimumLevel || wizard.InventoryBehavior?.InventoryItemIds is not { } ids
                    || wizard.EquipmentBehavior?.EquippedItemIds is null || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed
                    || !TryReadHoldings(session, wizard, out var rows, out backpack)
                    || rows.Any(item => item.m_globalID.Full == preparedDeed.m_globalID.Full)) return false;
                var portfolio = session.Load<HousePortfolio>(HousePortfolio.DocumentId(live.CharId));
                var isNew = portfolio is null; portfolio ??= new() { OwnerId = live.CharId };
                if (portfolio.OwnerId != live.CharId || portfolio.DeedIds.Count != portfolio.DeedIds.Distinct().Count()
                    || portfolio.DeedIds.Count >= HouseCatalog.MaximumOwned) return false;
                // Native CountIslands counts inventory plus equipped deeds. Also count original
                // legacy deeds without a new portfolio entry; absence of our record is not permission for a fourth.
                var heldIds = ids.Concat(wizard.EquipmentBehavior.EquippedItemIds).ToHashSet();
                var oldDeeds = rows.Where(i => heldIds.Contains(i.m_globalID) && IsDeed(i)).Select(i => (ulong)i.m_globalID);
                if (portfolio.DeedIds.Concat(oldDeeds).Distinct().Count() >= HouseCatalog.MaximumOwned) return false;
                Account account = null;
                if (live.Account is { } liveAccount) {
                    if (wizard.AccountId != liveAccount.AccountId) return false;
                    account = loadAccount is null ? session.Query<Account>(collectionName: AccountCollection.CollectionName)
                        .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                        .FirstOrDefault(candidate => candidate.AccountId == wizard.AccountId) : loadAccount(session, wizard.AccountId);
                    if (account?.AccountId != wizard.AccountId || account.CharacterIds?.Contains(live.CharId) != true
                        || account.Crowns < 0) return false;
                    resultBalance = account.Crowns;
                }
                if (currency == HouseCurrency.Gold) {
                    if (wizard.GameStats.m_currentGold < price) return false;
                    wizard.GameStats.m_currentGold -= price;
                }
                else {
                    if (account is null || account.Crowns < price) return false;
                    account.Crowns -= price;
                    resultBalance = account.Crowns;
                }
                var record = new HouseRecord { OwnerId = live.CharId, DeedId = preparedDeed.m_globalID,
                    // GID does not override parameterless ToString: interpolate the numeric
                    // value so every original deed has its own document, including identical templates.
                    TemplateId = definition.TemplateId, ItemDocumentId = $"ClassicHouseItems/{live.CharId}/{preparedDeed.m_globalID.Full}",
                    LotInstanceId = lot, ExteriorZone = definition.ExteriorZone, InteriorZone = definition.InteriorZone,
                    PreviewZone = definition.PreviewZone };
                if (session.Load<HouseRecord>(HouseRecord.DocumentId(live.CharId, record.DeedId)) is not null
                    || session.Load<WizClientObjectItem>(record.ItemDocumentId) is not null) return false;
                session.Store(preparedDeed, record.ItemDocumentId);
                session.Advanced.GetMetadataFor(preparedDeed)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardItemCollection.CollectionName;
                session.Store(record, HouseRecord.DocumentId(live.CharId, record.DeedId));
                portfolio.DeedIds = [.. portfolio.DeedIds, record.DeedId];
                if (isNew) session.Store(portfolio, HousePortfolio.DocumentId(live.CharId));
                wizard.InventoryBehavior.InventoryItemIds = [.. ids, record.DeedId];
                backpack.Add(preparedDeed);
                result = new(null, record.Copy(), preparedDeed, data, wizard.GameStats.m_currentGold,
                    wizard.GameStats.m_baseGoldPouch, resultBalance);
                if (!PreparePublication(result, preparePublication)) return false;
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                if (currency == HouseCurrency.Gold && account is not null) session.Advanced.IgnoreChangesFor(account);
                return true;
            }, wizard => {
                live.GameStats.m_currentGold = wizard.GameStats.m_currentGold;
                if (live.Account is { } account) {
                    // The same transaction's tracked account is published below through saved balance.
                    account.Crowns = resultBalance;
                }
                WizardInventoryTransactions.PublishCommittedBackpack(live, wizard, backpack);
                afterCommit?.Invoke(result);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            // Account before character is the shared lock order. Optimistic concurrency also
            // prevents another account session from spending the same saved Crowns balance.
            var saved = live.Account is not null
                ? AccountCollection.WithAccountWriteLane(live.Account.AccountId, Commit) : Commit();
            return saved ? result : new("You cannot buy this house: check your level, balance, backpack space and three-house limit.");
        }
        catch (Exception ex) { Log(live.CharId, "purchase", ex); return new(WizardCollection.IsInventorySnapshotUncertain(live)
            ? "Your house save needs an authoritative reload. Please reconnect."
            : "Your house could not be saved. Please try again."); }

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
                var balance = (long)wizard.GameStats.m_currentGold + WizardCollection.CappedGoldDelta(wizard.GameStats, serverCalculatedGold); // keeps an over-full wallet
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

    internal static HouseEquipResult SetEquipped(Wizard live, ulong deedId, bool equip,
        Func<HouseEquipResult, bool> preparePublication = null, Action<HouseEquipResult> afterCommit = null) {
        if (live is null || WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel) return new("You cannot select a house now.");
        var result = new HouseEquipResult("You cannot select that house.");
        List<WizClientObjectItem> backpack = [], originals = [];
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel || wizard.CharId != live.CharId
                    || !TryReadHoldings(session, wizard, out originals, out backpack)) return false;
                var record = Owned(session, wizard, live.CharId, deedId, protectReadOnly: true);
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
                        var oldRecord = Owned(session, wizard, live.CharId, oldId, protectReadOnly: true);
                        if (oldRecord is null || ids.Contains(oldId)) return false;
                        old = session.Load<WizClientObjectItem>(oldRecord.ItemDocumentId);
                        if (!Serialize(old, out oldData)) return false;
                    }
                    wizard.InventoryBehavior.InventoryItemIds = [.. ids.Where(id => id != deedId), .. (oldId == 0 ? Array.Empty<ulong>() : new[] { oldId })];
                    wizard.EquipmentBehavior.EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds.Where(id => id != oldId), deedId];
                    wizard.EquipmentBehavior.SlotList = [.. slots.Where(s => s.SlotType != EquipmentSlotType.Islands),
                        new EquipmentSlot { SlotType = EquipmentSlotType.Islands, ItemId = deedId, ItemName = item.m_debugName, EquippedSince = DateTime.UtcNow }];
                    backpack.RemoveAll(candidate => candidate.m_globalID.Full == deedId);
                    if (old is not null) backpack.Add(old);
                }
                else {
                    if (oldId != deedId || ids.Contains(deedId) || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed) return false;
                    wizard.InventoryBehavior.InventoryItemIds = [.. ids, deedId];
                    wizard.EquipmentBehavior.EquippedItemIds = wizard.EquipmentBehavior.EquippedItemIds.Where(id => id != deedId).ToList();
                    wizard.EquipmentBehavior.SlotList = slots.Where(s => s.SlotType != EquipmentSlotType.Islands).ToList();
                    backpack.Add(item);
                }
                if (!Serialize(item, out var data)) return false;
                result = new(null, item, old, data, oldData);
                if (!CanPublishSelectedHouse(live, wizard, result, originals)) return false;
                if (!PreparePublication(result, preparePublication)) return false;
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                return true;
            }, savedWizard => {
                result = PublishSelectedHouse(live, savedWizard, result, backpack, originals);
                afterCommit?.Invoke(result);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            return saved ? result : new("You cannot select that house: check ownership and backpack space.");
        }
        catch (Exception ex) { Log(live.CharId, "selection", ex); return new(WizardCollection.IsInventorySnapshotUncertain(live)
            ? "Your house selection needs an authoritative reload. Please reconnect."
            : "Your house selection could not be saved. Please try again."); }
    }

    internal static bool TryReadHoldings(IDocumentSession session, Wizard saved, out List<WizClientObjectItem> rows,
        out List<WizClientObjectItem> backpack) {
        rows = WizardInventoryTransactions.CaptureReadRows(session,
            WizardInventoryTransactions.TestRowsScope.Value?.Invoke(session) ?? WizardInventoryTransactions.ItemQuery(session).ToList());
        backpack = [];
        if (rows is null || rows.Any(item => item is null)
            || !WizardInventoryTransactions.TryValidateBackpack(saved, rows, out backpack)
            || saved.EquipmentBehavior?.EquippedItemIds is not { } equipped || saved.EquipmentBehavior.SlotList is not { } slots
            || slots.Any(slot => slot is null || !equipped.Contains(slot.ItemId))
            || slots.Select(slot => slot.ItemId.Full).Distinct().Count() != slots.Count) return false;
        foreach (var id in equipped) {
            var originals = rows.Where(item => item is not null && item.m_globalID.Full == id).ToArray();
            if (originals.Length != 1 || originals[0].m_characterId.Full != saved.CharId || originals[0].m_templateID.Full == 0) return false;
        }
        return true;
    }

    private static bool CanPublishSelectedHouse(Wizard live, Wizard saved, HouseEquipResult result, List<WizClientObjectItem> originals) {
        if (live?.CharId != saved?.CharId || live.InventoryBehavior?.Items is null || live.EquipmentBehavior?.EquippedItems is null
            || originals is null) return false;
        var previousBag = live.InventoryBehavior.Items.ToArray();
        var previousWorn = live.EquipmentBehavior.EquippedItems.ToArray();
        var moved = new[] { result.Item, result.Replaced }.Where(item => item is not null).ToArray();
        foreach (var snapshot in moved) {
            var matches = previousBag.Concat(previousWorn).Where(item => item is not null
                && item.m_globalID.Full == snapshot.m_globalID.Full).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !MatchesOriginal(matches[0], snapshot)) return false;
        }
        var movedIds = moved.Select(item => item.m_globalID.Full).ToHashSet();
        foreach (var id in saved.EquipmentBehavior.EquippedItemIds.Where(id => !movedIds.Contains(id))) {
            var aliases = previousWorn.Where(item => item is not null && item.m_globalID.Full == id).ToArray();
            var snapshots = originals.Where(item => item is not null && item.m_globalID.Full == id).ToArray();
            if (aliases.Length != 1 || snapshots.Length != 1 || !MatchesOriginal(aliases[0], snapshots[0])) return false;
        }
        return true;
    }

    private static bool MatchesOriginal(WizClientObjectItem alias, WizClientObjectItem snapshot)
        => alias is not null && snapshot is not null && alias.GetType() == snapshot.GetType()
            && alias.m_globalID.Full != 0 && alias.m_globalID == snapshot.m_globalID
            && alias.m_templateID.Full != 0 && alias.m_templateID == snapshot.m_templateID
            && alias.m_characterId.Full != 0 && alias.m_characterId == snapshot.m_characterId;

    private static HouseEquipResult PublishSelectedHouse(Wizard live, Wizard saved, HouseEquipResult result,
        List<WizClientObjectItem> backpack, List<WizClientObjectItem> originals) {
        if (!CanPublishSelectedHouse(live, saved, result, originals))
            throw new InvalidOperationException("House selection needs an authoritative equipment reload.");
        var previousBag = live.InventoryBehavior?.Items?.ToArray() ?? [];
        var previousWorn = live.EquipmentBehavior?.EquippedItems?.ToArray() ?? [];
        var snapshots = new[] { result.Item, result.Replaced }.Where(item => item is not null).ToArray();
        var moved = new Dictionary<ulong, WizClientObjectItem>();
        foreach (var snapshot in snapshots) {
            var matches = previousBag.Concat(previousWorn).Where(item => item is not null
                && item.m_globalID.Full == snapshot.m_globalID.Full).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("House selection needs authoritative item aliases.");
            var alias = matches.SingleOrDefault() ?? snapshot;
            WizardInventoryTransactions.PublishCommittedItemSnapshot(snapshot, alias);
            moved.Add(snapshot.m_globalID.Full, alias);
        }
        // Only Islands move here; other worn runtime objects (decks and active elixirs) keep their exact aliases/state.
        var movedIds = moved.Keys.ToHashSet();
        var remainingIds = saved.EquipmentBehavior.EquippedItemIds.Where(id => !movedIds.Contains(id)).ToArray();
        if (remainingIds.Any(id => previousWorn.Count(item => item?.m_globalID.Full == id
                && item.m_characterId.Full == saved.CharId) != 1))
            throw new InvalidOperationException("House selection needs an authoritative equipment reload.");
        var worn = remainingIds.Select(id => previousWorn.Single(item => item?.m_globalID.Full == id)).ToList();
        foreach (var id in saved.EquipmentBehavior.EquippedItemIds.Where(movedIds.Contains)) worn.Add(moved[id]);
        // Publish the moved worn alias into the backpack using the same full-snapshot publisher as ordinary items.
        var committedBag = backpack.Select(item => moved.TryGetValue(item.m_globalID.Full, out var alias) ? alias : item).ToList();
        WizardInventoryTransactions.PublishCommittedBackpack(live, saved, committedBag);
        live.EquipmentBehavior.EquippedItemIds = [.. saved.EquipmentBehavior.EquippedItemIds];
        live.EquipmentBehavior.SlotList = [.. saved.EquipmentBehavior.SlotList];
        live.EquipmentBehavior.EquippedItems = [.. worn];
        return result with { Item = moved[result.Item.m_globalID.Full],
            Replaced = result.Replaced is null ? null : moved[result.Replaced.m_globalID.Full] };
    }

    private static bool PreparePublication<T>(T result, Func<T, bool> prepare) {
        try { return prepare?.Invoke(result) ?? true; }
        catch { return false; }
    }

    private static bool Serialize(WizClientObjectItem item, out ByteString data)
        => new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None).Serialize(item, (PropertyFlags)24, out data);
    private static void Log(ulong owner, string action, Exception ex)
        => Logger.Error("House {0} for {1} was not completed: {2}", Logger.Args(action, owner, ex.Message));
}
