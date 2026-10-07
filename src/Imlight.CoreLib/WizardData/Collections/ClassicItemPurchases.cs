// CLASSIC: a paid ordinary item and its original backpack reference share one acknowledged save.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal sealed record ItemPurchaseReceipt(WizClientObjectItem Item, ByteString Data);

internal static class ClassicItemPurchases {
    internal static bool Purchase(Wizard live, WizClientObjectItem prepared, int price, bool crowns,
        out ItemPurchaseReceipt receipt, int minimumLevel = 0,
        Func<WizClientObjectItem, ByteString> serialize = null,
        Func<IDocumentSession, ulong, Account> loadAccount = null) {
        receipt = null;
        if (live is null || prepared is null || price < 0 || minimumLevel < 0
            || WizardCollection.IsInventorySnapshotUncertain(live)
            || crowns && live.Account is null) return false;
        ItemPurchaseReceipt acknowledged = null;
        Account account = null;
        List<WizClientObjectItem> backpack = [];
        bool Commit() => WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.GameStats is null
                || saved.MagicSchoolBehavior is null || saved.MagicSchoolBehavior.Level < minimumLevel) return false;
            if (crowns) {
                if (saved.AccountId != live.Account.AccountId) return false;
                account = loadAccount is null ? ReadAccount(session, saved.AccountId) : loadAccount(session, saved.AccountId);
                if (account?.AccountId != saved.AccountId || account.CharacterIds?.Contains(saved.CharId) != true
                    || account.Crowns < price) return false;
            }
            else if (saved.GameStats.m_currentGold < price) return false;
            if (!WizardInventoryTransactions.TryStageGrants(session, saved, [prepared], out var admitted,
                out _, out backpack) || admitted.Count != 1) return false;
            ByteString data;
            try { data = serialize is null ? Serialize(prepared) : serialize(prepared); }
            catch (Exception) { return false; }
            if (data.Length == 0) return false;
            if (crowns) account.Crowns -= price;
            else saved.GameStats.m_currentGold -= price;
            acknowledged = new(prepared, data);
            return true;
        }, saved => {
            WizardInventoryTransactions.PublishCommittedBackpack(live, saved, backpack);
            if (crowns) live.Account.Crowns = account.Crowns;
            else live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        // CLASSIC: every account-and-character writer takes account first, including housing.
        var success = crowns ? AccountCollection.WithAccountWriteLane(live.Account.AccountId, Commit) : Commit();
        if (success) receipt = acknowledged;
        return success;
    }

    private static Account ReadAccount(IDocumentSession session, ulong id) {
        var accounts = session.Query<Account>(collectionName: AccountCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(account => account.AccountId == id).Take(int.MaxValue).ToList();
        return accounts.Count == 1 ? accounts[0] : null;
    }

    private static ByteString Serialize(WizClientObjectItem item) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        return serializer.Serialize(item, 24, out var data) ? data : default;
    }
}
