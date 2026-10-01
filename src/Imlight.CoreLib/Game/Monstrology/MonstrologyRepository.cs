using System;
using Raven.Client.Documents;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using System.Linq;

namespace Imlight.CoreLib.Game.Monstrology;

internal sealed class MonstrologyRepository(IDocumentStore store) {
    internal static MonstrologyRepository ForPlayers() => new(PlayerDatabase.Instance.Store);
    internal static string DocumentId(ulong owner) {
        if (owner == 0) throw new ArgumentOutOfRangeException(nameof(owner));
        return "MonstrologyLedgers/" + owner.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    internal MonstrologyLedger Read(ulong owner) {
        using var session = store.OpenSession();
        var state = session.Load<MonstrologyLedger>(DocumentId(owner)) ?? new MonstrologyLedger { OwnerId = owner };
        if (state.OwnerId != owner) throw new InvalidOperationException("Monstrology owner mismatch");
        return state;
    }
    internal static MonstrologyResult CreateCard(ulong owner, AnimusCreation request, out int gold,
        Func<ulong, Func<IDocumentSession, Wizard, bool>, Action<Wizard>, bool> transact = null, WizClientObjectItem guest = null,
        Action<Wizard> afterCommit = null) {
        var resultingGold = 0;
        var result = MonstrologyResult.CommitFailed;
        var execute = transact ?? WizardCollection.TransactMonstrology;
        var committed = execute(owner, (session, wizard) => {
            var id = DocumentId(owner);
            var state = session.Load<MonstrologyLedger>(id) ?? new MonstrologyLedger { OwnerId = owner };
            if (state.OwnerId != owner || wizard.CharId != owner) { result = MonstrologyResult.Rejected; return false; }
            if (request.Kind == MonstrologyCreationKind.HouseGuest) {
                var limit = ConfigurationManager.Settings["Character.MaxInventoryItems"].AsInt(20);
                if (limit <= 0) limit = 20;
                if (guest == null || guest.m_globalID == 0 || guest.m_templateID != request.OutputTemplate
                    || guest.m_characterId.Full != owner || wizard.InventoryBehavior.InventoryItemIds == null
                    || wizard.InventoryBehavior.InventoryItemIds.Count >= limit
                    || wizard.InventoryBehavior.InventoryItemIds.Contains(guest.m_globalID)) {
                    result = MonstrologyResult.Rejected; return false;
                }
            } else if (guest != null) { result = MonstrologyResult.Rejected; return false; }
            result = MonstrologyRules.DeliverCard(state, request, wizard.GameStats.m_currentGold);
            if (result != MonstrologyResult.Applied) return false;
            wizard.GameStats.m_currentGold -= request.GoldCost;
            if (guest == null) wizard.SpellbookBehavior.AddTreasureCard(request.OutputTemplate);
            else {
                wizard.InventoryBehavior.InventoryItemIds = [.. wizard.InventoryBehavior.InventoryItemIds, guest.m_globalID];
                session.Store(guest);
                session.Advanced.GetMetadataFor(guest)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardItemCollection.CollectionName;
                state.Creations[request.OperationId] = state.Creations[request.OperationId] with { ItemId = guest.m_globalID };
            }
            session.Store(state, id);
            resultingGold = wizard.GameStats.m_currentGold;
            return true; // Parent commits ledger, Gold and card together, once, while holding shared wizard lane.
        }, afterCommit);
        gold = committed ? resultingGold : 0;
        return committed ? result : result == MonstrologyResult.Applied ? MonstrologyResult.CommitFailed : result;
    }
    internal MonstrologyResult Transact(ulong owner, Func<MonstrologyLedger, MonstrologyResult> operation) {
        using var session = store.OpenSession();
        // Conflicting extraction/spend transactions fail instead of overwriting or double spending.
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var id = DocumentId(owner);
        var state = session.Load<MonstrologyLedger>(id);
        var fresh = state == null;
        state ??= new MonstrologyLedger { OwnerId = owner };
        if (state.OwnerId != owner) throw new InvalidOperationException("Monstrology owner mismatch");
        var result = operation(state);
        if (result != MonstrologyResult.Applied) return result;
        if (fresh) session.Store(state, id);
        session.SaveChanges(); // Ledger, debit and pending entitlement commit together.
        return result;
    }
}
