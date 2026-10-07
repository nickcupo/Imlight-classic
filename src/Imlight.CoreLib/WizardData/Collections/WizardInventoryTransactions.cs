// CLASSIC: ordinary item rows and backpack references share one fresh, acknowledged character write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal static class WizardInventoryTransactions {
    // CLASSIC: fixtures replace row loading/template initialization; production always uses tracked Raven rows.
    internal static readonly AsyncLocal<Func<IDocumentSession, List<WizClientObjectItem>>> TestRowsScope = new();
    internal static readonly AsyncLocal<Action<WizClientObjectItem>> TestInitializeScope = new();

    internal static IQueryable<WizClientObjectItem> ItemQuery(IDocumentSession session)
        => session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(
                ConfigurationManager.Settings["Database.DatabaseWaitForNonStaleResultsTimeout"].AsByte(5))))
            .Take(int.MaxValue);

    private static List<WizClientObjectItem> ReadRows(IDocumentSession session)
        => TestRowsScope.Value is { } read ? read(session) : ItemQuery(session).ToList();

    internal static bool TryReadOwnedBackpack(IDocumentSession session, Wizard saved,
        out List<WizClientObjectItem> owned) {
        owned = [];
        return session is not null && TryValidateBackpack(saved, ReadRows(session), out owned);
    }

    // CLASSIC: hatch parents may be worn or in the backpack, but never banked or unreferenced/orphaned.
    internal static bool TryReadOwnedItem(IDocumentSession session, Wizard saved, ulong id, out WizClientObjectItem item) {
        item = null;
        if (session is null || id == 0) return false;
        var rows = ReadRows(session);
        if (!TryValidateBackpack(saved, rows, out _)
            || !(saved.InventoryBehavior.InventoryItemIds.Contains(id) || (saved.EquipmentBehavior?.EquippedItemIds ?? []).Contains(id))
            || (saved.StorageBehavior?.BankItemIds ?? []).Contains(id)) return false;
        var matches = rows.Where(row => row is not null && row.m_globalID.Full == id).ToArray();
        if (matches.Length != 1 || matches[0].m_characterId.Full != saved.CharId || matches[0].m_templateID.Full == 0) return false;
        item = matches[0];
        return true;
    }

    internal static bool TryValidateBackpack(Wizard saved, IReadOnlyList<WizClientObjectItem> rows,
        out List<WizClientObjectItem> owned) {
        owned = [];
        var ids = saved?.InventoryBehavior?.InventoryItemIds;
        if (saved is null || saved.CharId == 0 || ids is null || rows is null || !ValidIds(ids)) return false;
        var equipment = saved.EquipmentBehavior?.EquippedItemIds ?? [];
        var bank = saved.StorageBehavior?.BankItemIds ?? [];
        if (!ValidIds(equipment) || !ValidIds(bank) || equipment.Intersect(bank).Any()
            || ids.Intersect(equipment.Concat(bank)).Any()) return false;
        foreach (var id in ids) {
            var matches = rows.Where(row => row is not null && row.m_globalID.Full == id).ToArray();
            if (matches.Length != 1 || matches[0].m_characterId.Full != saved.CharId
                || matches[0].m_templateID.Full == 0) return false;
            owned.Add(matches[0]);
        }
        return true;
    }

    // CLASSIC: validate every candidate before accepting the fitting prefix. Full bags are valid no-ops;
    // an orphan/foreign/global-ID collision is a refusal, never an opportunity to adopt an existing row.
    internal static bool TryStageGrants(IDocumentSession session, Wizard saved,
        IReadOnlyList<WizClientObjectItem> candidates, out IReadOnlyList<WizClientObjectItem> admitted,
        out bool validated, out List<WizClientObjectItem> backpack) {
        admitted = []; backpack = []; validated = false;
        if (session is null || saved is null || candidates is null || candidates.Count == 0
            || candidates.Any(item => item is null || item is ClientReagentItem || item.m_globalID.Full == 0 || item.m_templateID.Full == 0
                || item.m_characterId.Full != saved.CharId)
            || candidates.Select(item => item.m_globalID.Full).Distinct().Count() != candidates.Count) return false;
        var rows = ReadRows(session);
        if (!TryValidateBackpack(saved, rows, out backpack)) return false;
        var elsewhere = (saved.EquipmentBehavior?.EquippedItemIds ?? []).Concat(saved.StorageBehavior?.BankItemIds ?? []).ToHashSet();
        if (candidates.Any(item => elsewhere.Contains(item.m_globalID.Full)
            || rows.Any(row => row is not null && row.m_globalID.Full == item.m_globalID.Full))) return false;
        validated = true;
        var available = Math.Max(0, ServerWizInventoryBehavior.MaxItemsAllowed - backpack.Count);
        var accepted = candidates.Take(available).ToArray();
        if (accepted.Length == 0) return false;
        foreach (var item in accepted) {
            session.Store(item);
            session.Advanced.GetMetadataFor(item)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardItemCollection.CollectionName;
        }
        saved.InventoryBehavior.InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds, ..accepted.Select(item => item.m_globalID.Full)];
        backpack.AddRange(accepted);
        admitted = accepted;
        return true;
    }

    // CLASSIC: reference-only removal supports moves. Destruction deletes the exact tracked original row in
    // the same commit, and cannot retire a house deed or a deck still holding Treasure Cards.
    internal static bool TryStageRemove(IDocumentSession session, Wizard saved, ulong id, bool destroy,
        out WizClientObjectItem removed, out List<WizClientObjectItem> backpack,
        IReadOnlyList<WizClientObjectItem> trackedRows = null) {
        removed = null; backpack = [];
        if (session is null || id == 0) return false;
        // CLASSIC: compound sales reuse one fresh tracked snapshot. Querying again after Delete can otherwise
        // re-track a staged deletion; the saved reference subset is revalidated before each removal.
        if (trackedRows is null ? !TryReadOwnedBackpack(session, saved, out backpack)
            : !TryValidateBackpack(saved, trackedRows, out backpack)) return false;
        var item = backpack.SingleOrDefault(row => row.m_globalID.Full == id);
        if (item is null) return false;
        if (destroy && ((Imlight.Classic.ClassicRuntime.IsInitialized && Imlight.Classic.ClassicRuntime.IsActive && HouseCollection.IsDeed(item))
            || saved.SpellbookBehavior?.DeckTreasureCards is { } ledger && ledger.TryGetValue(id, out var cards)
                && (cards is null || cards.Values.Any(count => count != 0)))) return false;
        saved.InventoryBehavior.InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds.Where(ownedId => ownedId != id)];
        backpack.Remove(item);
        if (destroy) session.Delete(item);
        removed = item;
        return true;
    }

    internal static void PublishCommittedBackpack(Wizard live, Wizard saved, IReadOnlyList<WizClientObjectItem> backpack) {
        if (live is null || saved is null || live.CharId == 0 || live.CharId != saved.CharId || saved.InventoryBehavior?.InventoryItemIds is not { } ids
            || !ValidIds(ids) || backpack is null || backpack.Count != ids.Count || backpack.Any(item => item is null
                || item.m_characterId.Full != live.CharId || !ids.Contains(item.m_globalID.Full))
            || backpack.Select(item => item.m_globalID.Full).Distinct().Count() != backpack.Count)
            throw new InvalidOperationException("Cannot publish an invalid saved backpack.");
        live.InventoryBehavior ??= new();
        var previous = live.InventoryBehavior.Items?.ToArray() ?? [];
        var published = new List<WizClientObjectItem>();
        foreach (var snapshot in backpack) {
            var alias = previous.FirstOrDefault(item => item is not null && item.GetType() == snapshot.GetType()
                && item.m_globalID.Full == snapshot.m_globalID.Full && item.m_templateID.Full == snapshot.m_templateID.Full
                && item.m_characterId.Full == live.CharId);
            if (alias is not null && !ReferenceEquals(alias, snapshot)) {
                // CLASSIC: generated native item types expose their saved fields/properties. Copy every one,
                // rather than losing dye, timer or pet metadata by rebuilding a template or copying a subset.
                CopySnapshot(snapshot, alias);
                published.Add(alias);
            }
            else published.Add(snapshot);
        }
        live.InventoryBehavior.InventoryItemIds = [..ids];
        live.InventoryBehavior.Items = [..published];
    }

    // CLASSIC: detached preparation leaves the caller's object untouched on refusal or a lost ACK. Pets skip
    // template initialization so their names, egg timers, talents and growth remain exactly as prepared.
    internal static WizClientObjectItem Prepare(Wizard live, WizClientObjectItem candidate, bool initializeBehaviors) {
        if (live is null || candidate is null || candidate is ClientReagentItem || candidate.m_globalID.Full == 0 || candidate.m_templateID.Full == 0
            || (candidate.m_characterId.Full != 0 && candidate.m_characterId.Full != live.CharId)) return null;
        var prepared = candidate with { m_characterId = live.CharId,
            m_inactiveBehaviors = candidate.m_inactiveBehaviors is null ? [] : [..candidate.m_inactiveBehaviors] };
        if (initializeBehaviors) {
            if (TestInitializeScope.Value is { } initialize) initialize(prepared);
            else CoreObjectFactory.InitializeCoreObjectBehaviors(prepared, prepared.m_templateID);
        }
        return prepared;
    }

    internal static bool Add(Wizard live, WizClientObjectItem candidate, bool initializeBehaviors) {
        if (live is null || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        var prepared = Prepare(live, candidate, initializeBehaviors);
        if (prepared is null) return false;
        List<WizClientObjectItem> backpack = [];
        return WizardCollection.CommitCharacterMutation(live.CharId,
            (session, saved) => !WizardCollection.IsInventorySnapshotUncertain(live)
                && TryStageGrants(session, saved, [prepared], out var admitted, out _, out backpack) && admitted.Count == 1,
            saved => {
                candidate.m_characterId = prepared.m_characterId;
                candidate.m_inactiveBehaviors = prepared.m_inactiveBehaviors;
                PublishCommittedBackpack(live, saved, backpack.Select(item => item.m_globalID.Full == prepared.m_globalID.Full ? candidate : item).ToArray());
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
    }

    internal static bool Remove(Wizard live, ulong id, bool destroy) {
        if (live is null || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        List<WizClientObjectItem> backpack = [];
        return WizardCollection.CommitCharacterMutation(live.CharId,
            (session, saved) => !WizardCollection.IsInventorySnapshotUncertain(live)
                && TryStageRemove(session, saved, id, destroy, out _, out backpack),
            saved => PublishCommittedBackpack(live, saved, backpack),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
    }

    private static bool ValidIds(IReadOnlyList<ulong> ids)
        => ids is not null && ids.All(id => id != 0) && ids.Distinct().Count() == ids.Count;

    private static void CopySnapshot(WizClientObjectItem snapshot, WizClientObjectItem alias) {
        foreach (var field in snapshot.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!field.IsInitOnly) field.SetValue(alias, field.GetValue(snapshot));
        foreach (var property in snapshot.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetMethod is not null && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
                property.SetValue(alias, property.GetValue(snapshot));
    }
}
