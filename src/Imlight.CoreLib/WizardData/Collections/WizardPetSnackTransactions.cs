// CLASSIC: snack originals and their fresh saved references are changed in the caller's one write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal sealed record SnackPurchaseReceipt(ClientPetSnackItem Snack, bool IsNew, ByteString Data);

internal static class WizardPetSnackTransactions {
    internal static readonly AsyncLocal<Func<IDocumentSession, List<ClientPetSnackItem>>> TestRowsScope = new();

    internal static bool TryReadOwnedBag(IDocumentSession session, Wizard saved, out List<ClientPetSnackItem> bag) {
        bag = [];
        if (session is null || saved?.CharId is not > 0 || saved.PetSnackBehavior is null) return false;
        var rows = TestRowsScope.Value is { } read ? read(session) : session.Query<ClientPetSnackItem>(
            collectionName: WizardPetSnackCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).Take(int.MaxValue).ToList();
        return TryValidate(saved, rows, out bag);
    }

    private static bool TryValidate(Wizard saved, IReadOnlyList<ClientPetSnackItem> rows, out List<ClientPetSnackItem> bag) {
        bag = [];
        var ids = saved.PetSnackBehavior.SnackItemIds ?? [];
        var otherIds = (saved.InventoryBehavior?.InventoryItemIds ?? []).Concat(saved.EquipmentBehavior?.EquippedItemIds ?? [])
            .Concat(saved.StorageBehavior?.BankItemIds ?? []);
        if (rows is null || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count || ids.Intersect(otherIds).Any()) return false;
        foreach (var id in ids) {
            var matches = rows.Where(row => row is not null && row.m_globalID.Full == id).ToArray();
            if (matches.Length != 1 || matches[0].m_characterId.Full != saved.CharId
                || matches[0].m_templateID.Full == 0 || matches[0].m_quantity <= 0) return false;
            bag.Add(matches[0]);
        }
        return bag.Select(snack => snack.m_templateID.Full).Distinct().Count() == bag.Count;
    }

    internal static bool TryStageConsume(IDocumentSession session, Wizard saved, ulong id,
        out ClientPetSnackItem snack, out List<ClientPetSnackItem> bag) {
        snack = null;
        if (id == 0 || !TryReadOwnedBag(session, saved, out bag)) { bag = []; return false; }
        snack = bag.SingleOrDefault(row => row.m_globalID.Full == id);
        if (snack is null) return false;
        snack.m_quantity--;
        if (snack.m_quantity == 0) {
            session.Delete(snack);
            saved.PetSnackBehavior.SnackItemIds = [..saved.PetSnackBehavior.SnackItemIds.Where(owned => owned != id)];
            bag.Remove(snack);
        }
        return true;
    }

    private static bool TryStageAdd(IDocumentSession session, Wizard saved, ulong templateId,
        Func<ClientPetSnackItem> create, out ClientPetSnackItem snack, out bool isNew, out List<ClientPetSnackItem> bag) {
        snack = null; isNew = false; bag = [];
        if (templateId == 0 || !TryReadOwnedBag(session, saved, out bag)) return false;
        snack = bag.SingleOrDefault(row => row.m_templateID.Full == templateId);
        if (snack is not null) {
            if (snack.m_quantity >= ServerPetSnackBehavior.MaxSnackStackAllowed) return false;
            snack.m_quantity++;
            return true;
        }
        snack = create?.Invoke();
        if (snack is null || snack.m_globalID.Full == 0 || snack.m_templateID.Full != templateId
            || snack.m_characterId.Full != saved.CharId || snack.m_quantity is <= 0 or > ServerPetSnackBehavior.MaxSnackStackAllowed) return false;
        // CLASSIC: a new original cannot adopt an orphan or another wizard's native identity.
        var rows = TestRowsScope.Value is { } read ? read(session) : session.Query<ClientPetSnackItem>(
            collectionName: WizardPetSnackCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).Take(int.MaxValue).ToList();
        var id = snack.m_globalID.Full;
        if (rows.Any(row => row is not null && row.m_globalID.Full == id)
            || (saved.InventoryBehavior?.InventoryItemIds ?? []).Contains(id)
            || (saved.EquipmentBehavior?.EquippedItemIds ?? []).Contains(id)
            || (saved.StorageBehavior?.BankItemIds ?? []).Contains(id)) return false;
        session.Store(snack);
        session.Advanced.GetMetadataFor(snack)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardPetSnackCollection.CollectionName;
        saved.PetSnackBehavior.SnackItemIds = [..(saved.PetSnackBehavior.SnackItemIds ?? []), id];
        bag.Add(snack); isNew = true;
        return true;
    }

    internal static void PublishCommittedBag(Wizard live, Wizard saved, IReadOnlyList<ClientPetSnackItem> bag) {
        var ids = saved?.PetSnackBehavior?.SnackItemIds ?? [];
        if (live is null || saved is null || live.CharId != saved.CharId || bag is null || ids.Count != bag.Count
            || !TryValidate(saved, bag, out _)) throw new InvalidOperationException("Cannot publish an invalid snack bag.");
        live.PetSnackBehavior ??= new();
        var previous = live.PetSnackBehavior.Snacks?.ToArray() ?? [];
        var published = new List<ClientPetSnackItem>();
        foreach (var snapshot in bag) {
            var alias = previous.FirstOrDefault(row => row is not null && row.GetType() == snapshot.GetType()
                && row.m_globalID == snapshot.m_globalID && row.m_templateID == snapshot.m_templateID
                && row.m_characterId == snapshot.m_characterId);
            if (alias is not null) { alias.m_quantity = snapshot.m_quantity; published.Add(alias); }
            else published.Add(snapshot);
        }
        live.PetSnackBehavior.SnackItemIds = [..ids];
        live.PetSnackBehavior.Snacks = published;
    }

    internal static bool Purchase(Wizard live, ulong templateId, int price, out SnackPurchaseReceipt receipt,
        Func<ClientPetSnackItem> create = null, Func<ClientPetSnackItem, ByteString> serialize = null) {
        receipt = null;
        if (live is null || templateId == 0 || price < 0 || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        SnackPurchaseReceipt acknowledged = null; List<ClientPetSnackItem> bag = [];
        var success = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.GameStats is null
                || saved.GameStats.m_currentGold < price) return false;
            if (!TryStageAdd(session, saved, templateId, create ?? (() => Create(live, templateId)),
                out var snack, out var isNew, out bag)) return false;
            ByteString data = default;
            if (isNew) {
                try { data = serialize is null ? Serialize(snack) : serialize(snack); }
                catch (Exception) { return false; }
                if (data.Length == 0) return false;
            }
            saved.GameStats.m_currentGold -= price;
            acknowledged = new(snack, isNew, data);
            return true;
        }, saved => {
            PublishCommittedBag(live, saved, bag);
            live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (success) receipt = acknowledged;
        return success;
    }

    private static ClientPetSnackItem Create(Wizard live, ulong templateId) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not PetSnackItemTemplate) return null;
        if (CoreObjectFactory.FinalizeCoreObject(templateId) is not ClientPetSnackItem snack) return null;
        CoreObjectFactory.InitializeCoreObjectBehaviors(snack, snack.m_templateID);
        snack.m_characterId = live.CharId; snack.m_quantity = 1;
        return snack;
    }

    private static ByteString Serialize(ClientPetSnackItem snack) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        return serializer.Serialize(snack, (PropertyFlags)24, out var data) ? data : default;
    }
}
