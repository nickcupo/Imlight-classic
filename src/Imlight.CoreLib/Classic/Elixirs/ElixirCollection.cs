using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic.Elixirs;

internal readonly record struct ElixirResult(string Error, ElixirLedger Ledger = null,
    WizClientObjectItem Item = null, ElixirEntry[] Removed = null, WizClientObjectItem[] ActiveItems = null) {
    internal bool Saved => Error is null && Ledger is not null;
    internal bool HasActive => Ledger?.Active.Count > 0;
    internal bool NoWork => Error is null && Ledger is null;
}

// CLASSIC: original item, inventory/equipment membership and remaining seconds commit together
// under the existing character lane. Network/stat publication occurs only after SaveChanges.
internal static class ElixirCollection {
    // CLASSIC: trusted attach validates original stored ownership and both equipment views before
    // any timer/effect can run. Orphan force-equipped objects are never approval evidence.
    internal static bool LoadValidated(Wizard live, Func<uint, ElixirDefinition> definitions = null) {
        if (live is null) return false;
        ElixirRuntime.Invalidate(live);
        ElixirLedger validated = null;
        try {
            return WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<ElixirLedger>(ElixirLedger.DocumentId(live.CharId))
                    ?? new ElixirLedger { OwnerId = live.CharId };
                if (ledger.OwnerId != live.CharId || !EquipmentMatches(wizard, ledger)
                    || !EquipmentMatches(live, ledger) || ledger.Active.Any(e => !ApprovedEntry(e, definitions))) return false;
                foreach (var entry in ledger.Active) {
                    var stored = session.Load<WizClientObjectItem>(entry.ItemDocumentId);
                    if (!OwnedEntry(stored, entry, live.CharId) || !OwnedEntry(live.EquipmentBehavior.GetItem(entry.ItemId), entry, live.CharId)) return false;
                }
                // Capture immutable validated state only after the read session completes successfully.
                validated = ledger.Copy();
                return true;
            }, _ => ElixirRuntime.PublishValidated(live, validated));
        }
        catch (Exception ex) { Failed(live, ex); return false; }
    }

    internal static ElixirResult Activate(Wizard live, ulong itemId,
        Func<uint, ElixirDefinition> definitions = null,
        Func<IDocumentSession, ulong, ulong, WizClientObjectItem> findItem = null) {
        var item = live?.InventoryBehavior?.GetItem(itemId);
        if (live is null || item is null || item.m_characterId != live.CharId
            || item.m_templateID.Full >= (1UL << 28) || !ElixirRules.IsElixir(item)) return Refused();
        var definition = (definitions ?? ElixirRules.Approved)((uint)item.m_templateID.Full);
        if (definition?.Valid != true || definition.TemplateId != item.m_templateID.Full) return Refused();
        ElixirResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (!InBackpack(wizard, itemId)) return false;
                var stored = Find(session, live.CharId, itemId, findItem);
                if (stored is null || stored.m_characterId != live.CharId || stored.m_globalID != itemId
                    || stored.m_templateID != item.m_templateID || !ElixirRules.IsElixir(stored)) return false;
                var timed = stored.m_inactiveBehaviors.OfType<ClientElixirBehavior>().SingleOrDefault();
                if (timed is null) return false;
                var ledger = session.Load<ElixirLedger>(ElixirLedger.DocumentId(live.CharId))
                    ?? new ElixirLedger { OwnerId = live.CharId };
                if (ledger.OwnerId != live.CharId || !EquipmentMatches(wizard, ledger)
                    || ledger.Active.Any(e => !ApprovedEntry(e, definitions))) return false;
                var entry = new ElixirEntry {
                    ItemId = itemId, ItemDocumentId = session.Advanced.GetDocumentId(stored),
                    TemplateId = definition.TemplateId, RemainingSeconds = definition.DurationSeconds,
                    Families = [.. definition.Families],
                };
                if (!ledger.TryActivate(entry, replaceMatching: false, out var removed)) return false;
                var activeItems = ledger.Active.Select(e => e.ItemId == itemId ? stored
                    : session.Load<WizClientObjectItem>(e.ItemDocumentId)).ToArray();
                timed.m_expireTime = entry.RemainingSeconds;
                timed.m_statsApplied = false; // effects require the independent approved canonical runtime path.
                if (ledger.Active.Any(e => !OwnedEntry(activeItems.SingleOrDefault(i => i?.m_globalID == e.ItemId), e, live.CharId))) return false;
                wizard.InventoryBehavior.InventoryItemIds = wizard.InventoryBehavior.InventoryItemIds.Where(id => id != itemId).ToList();
                wizard.EquipmentBehavior.EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds, itemId];
                wizard.EquipmentBehavior.SlotList = [.. wizard.EquipmentBehavior.SlotList, new EquipmentSlot {
                    SlotType = EquipmentSlotType.Elixir, ItemId = itemId, ItemName = stored.m_debugName,
                    EquippedSince = DateTime.UtcNow,
                }];
                session.Store(ledger, ElixirLedger.DocumentId(live.CharId));
                result = new(null, ledger.Copy(), stored, removed, activeItems);
                return true;
            }, committed => {
                live.InventoryBehavior.RemoveItem(itemId, out var removedItem);
                live.EquipmentBehavior.PublishElixirItems(result.ActiveItems);
                ElixirRuntime.PublishValidated(live, result.Ledger);
            });
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, ex); }
    }

    internal static ElixirResult AdvanceOnline(Wizard live, uint wholeSeconds,
        Func<uint, ElixirDefinition> definitions = null)
        => Advance(live, wholeSeconds, null, definitions);

    internal static ElixirResult AdvanceOnline(Wizard live, IReadOnlyDictionary<ulong, uint> elapsedByItem,
        Func<uint, ElixirDefinition> definitions = null)
        => Advance(live, null, elapsedByItem, definitions);

    private static ElixirResult Advance(Wizard live, uint? wholeSeconds,
        IReadOnlyDictionary<ulong, uint> elapsedByItem, Func<uint, ElixirDefinition> definitions) {
        if (live is null) return Refused();
        if (wholeSeconds == 0 || wholeSeconds is null && (elapsedByItem is null || elapsedByItem.Count == 0)) return new(null);
        ElixirResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<ElixirLedger>(ElixirLedger.DocumentId(live.CharId));
                if (ledger is null || ledger.OwnerId != live.CharId || ledger.Active.Count == 0
                    || ledger.Version == uint.MaxValue || !EquipmentMatches(wizard, ledger)) return false;
                // A removed approval cannot keep granting effects or consume uncertain item definitions.
                if (ledger.Active.Any(e => !ApprovedEntry(e, definitions))) return false;
                var elapsed = elapsedByItem ?? ledger.Active.ToDictionary(e => e.ItemId, _ => wholeSeconds.Value);
                if (elapsed.Keys.Any(id => !ledger.Active.Any(e => e.ItemId == id))) return false;
                if (!elapsed.Values.Any(s => s > 0)) { result = new(null); return false; }
                var items = ledger.Active.Select(entry => {
                    var stored = session.Load<WizClientObjectItem>(entry.ItemDocumentId);
                    return (entry, stored, timed: stored?.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault());
                }).ToArray();
                if (items.Any(i => !OwnedEntry(i.stored, i.entry, live.CharId))) return false;
                var expired = ledger.AdvanceOnline(elapsed);
                foreach (var (entry, _, timed) in items) {
                    timed.m_expireTime = entry.RemainingSeconds;
                    if (entry.RemainingSeconds == 0) timed.m_statsApplied = false;
                }
                var expiredIds = expired.Select(e => e.ItemId).ToHashSet();
                wizard.EquipmentBehavior.EquippedItemIds = wizard.EquipmentBehavior.EquippedItemIds.Where(id => !expiredIds.Contains(id)).ToList();
                wizard.EquipmentBehavior.SlotList = wizard.EquipmentBehavior.SlotList.Where(s => !expiredIds.Contains(s.ItemId)).ToList();
                session.Store(ledger, ElixirLedger.DocumentId(live.CharId));
                result = new(null, ledger.Copy(), Removed: expired,
                    ActiveItems: items.Where(i => !expiredIds.Contains(i.entry.ItemId)).Select(i => i.stored).ToArray());
                return true;
            }, _ => {
                live.EquipmentBehavior.PublishElixirItems(result.ActiveItems);
                ElixirRuntime.PublishValidated(live, result.Ledger);
            });
            return saved || result.NoWork ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, ex); }
    }

    private static WizClientObjectItem Find(IDocumentSession session, ulong owner, ulong id,
        Func<IDocumentSession, ulong, ulong, WizClientObjectItem> findItem)
        => findItem is not null ? findItem(session, owner, id) : session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
            .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .FirstOrDefault(i => i.m_characterId == owner && i.m_globalID == id);

    private static bool InBackpack(Wizard wizard, ulong id)
        => wizard.InventoryBehavior?.InventoryItemIds?.Contains(id) == true
            && wizard.EquipmentBehavior?.EquippedItemIds?.Contains(id) == false
            && wizard.StorageBehavior?.BankItemIds?.Contains(id) != true;

    private static bool EquipmentMatches(Wizard wizard, ElixirLedger ledger)
        => wizard.EquipmentBehavior?.EquippedItemIds is not null && wizard.EquipmentBehavior.SlotList is not null
            && ledger.Active.Count <= ElixirRules.MaximumActive
            && ledger.Active.Select(e => e.ItemId).Distinct().Count() == ledger.Active.Count
            && ledger.Active.All(e => wizard.EquipmentBehavior.EquippedItemIds.Contains(e.ItemId)
                && wizard.EquipmentBehavior.SlotList.Count(s => s.SlotType == EquipmentSlotType.Elixir && s.ItemId == e.ItemId) == 1)
            && wizard.EquipmentBehavior.SlotList.Where(s => s.SlotType == EquipmentSlotType.Elixir)
                .All(s => ledger.Active.Any(e => e.ItemId == s.ItemId));

    private static bool ApprovedEntry(ElixirEntry entry, Func<uint, ElixirDefinition> definitions) {
        var definition = (definitions ?? ElixirRules.Approved)(entry.TemplateId);
        return definition?.Valid == true && definition.TemplateId == entry.TemplateId
            && entry.ItemId != 0 && !string.IsNullOrEmpty(entry.ItemDocumentId)
            && entry.RemainingSeconds > 0 && entry.RemainingSeconds <= definition.DurationSeconds
            && entry.Families is not null && entry.Families.ToHashSet(StringComparer.Ordinal).SetEquals(definition.Families);
    }

    private static bool OwnedEntry(WizClientObjectItem item, ElixirEntry entry, ulong owner)
        => item is not null && item.m_characterId == owner && item.m_globalID == entry.ItemId
            && item.m_templateID.Full == entry.TemplateId
            && item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault()?.m_expireTime == entry.RemainingSeconds;

    private static ElixirResult Refused() => new("That elixir is not available in this historical profile yet.");
    private static ElixirResult Failed(Wizard live, Exception ex) {
        Logger.Error("Elixir transaction for {0} was not completed: {1}", Logger.Args(live.CharId, ex.Message));
        return new("Your elixir could not be saved. Please try again.");
    }
}
