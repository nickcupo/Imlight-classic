// CLASSIC: a paid chest roll owns one account/character/use/inventory acknowledgement.
using System;
using Akka.Actor;
using Action = System.Action;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Game.SecondChance;

internal enum SecondChanceStatus { Opened, Committed, Refused, PreparationFailed, ContextLost }
internal sealed record SecondChanceQuote(ulong CharId, ulong ChestId, SecondChanceChest Chest,
    string Zone, ulong Instance, DateOnly Day, int Used, int Cost, int Uses, ulong BossTemplate) {
    internal IActorRef Owner { get; init; }
}
internal sealed record SecondChanceReceipt(SecondChanceQuote Quote, int NewUsed, int Balance,
    int Gold, int AppliedGold, StackRewardReceipt Rewards, IReadOnlyList<IMessage> Messages) {
    internal int NextCost { get; init; }
}
internal sealed record SecondChanceResult(SecondChanceStatus Status, ChestRefusal Refusal = ChestRefusal.None,
    SecondChanceQuote Quote = null, SecondChanceReceipt Receipt = null);

// CLASSIC: fixtures may author a roll/native preparation/fault, never replace the saved authority queries.
internal sealed class SecondChanceDependencies {
    internal Func<ulong, MobRewardRules, DropTableResult> Roll;
    internal Func<LootInfoList, uint, ByteString> SerializeLoot;
    internal Func<IMessage, bool> Prepare;
    internal Action BeforePublish;
}

internal static class ClassicSecondChanceTransactions {
    internal static readonly AsyncLocal<SecondChanceDependencies> TestScope = new();
    private static readonly TimeSpan QueryWait = TimeSpan.FromSeconds(5);

    internal static SecondChanceResult TryOpen(Wizard live, ulong chestId, SecondChanceChest chest,
        string zone, ulong instance, SecondChanceRules rules, Action<IReadOnlyList<IMessage>> publish = null,
        Func<bool> isCurrent = null, SecondChanceChests state = null, IActorRef owner = null) {
        state ??= SecondChanceChests.Instance;
        if (!IdentityReady(live, live?.CharId ?? 0, live?.Account?.AccountId ?? 0)
            || chestId == 0 || chest is null || rules?.ChestByTemplate(chest.Template) != chest)
            return new(SecondChanceStatus.PreparationFailed);
        var charId = live.CharId; var accountId = live.Account.AccountId;
        if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
        return AccountCollection.WithAccountWriteLane<SecondChanceResult>(accountId, () => WizardCollection.WithCharacterLock<SecondChanceResult>(charId,
            () => state.WithGate<SecondChanceResult>(() => {
                if (WizardCollection.IsInventorySnapshotUncertain(live)) return new(SecondChanceStatus.PreparationFailed);
                if (!IdentityReady(live, charId, accountId)) return ContextLost(state, charId, owner);
                if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
                state.Close(charId); // a refused/failed open cannot leave a previously displayed quote spendable
                if (!state.HasDefeated(charId, chest, zone, instance))
                    return new(SecondChanceStatus.Refused, ChestRefusal.BossNotDefeated);
                var day = state.CaptureDay();
                using var session = OpenSession();
                var saved = ReadWizard(session, charId);
                var account = ReadAccount(session, accountId);
                if (!Owned(saved, account, charId, accountId)
                    || !SecondChanceUses.TryRead(session, charId, day, out _, out var uses))
                    return new(SecondChanceStatus.PreparationFailed);
                if (!SameZone(saved, zone)) return ContextLost(state, charId, owner);
                var used = uses.GetValueOrDefault(Key(chest.Template));
                if (!TryCost(rules, used, out var cost)) return new(SecondChanceStatus.PreparationFailed);
                var quote = new SecondChanceQuote(charId, chestId, chest, zone, instance, day, used, cost,
                    Math.Max(0, rules.DailyUses - used), state.WonBoss(charId, chest)) { Owner = owner };
                IMessage[] messages = [new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_CROWNS_BALANCE { Balance = account.Crowns },
                    new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_PROMPT { Id = chestId, Cost = cost, Uses = quote.Uses }];
                if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
                if (quote.BossTemplate == 0 || !PrepareMessages(messages)) return new(SecondChanceStatus.PreparationFailed);
                if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
                // No save/reward mutation: only this prepared, freshly priced prompt is remembered.
                publish?.Invoke(messages);
                if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
                state.Remember(quote);
                return new(SecondChanceStatus.Opened, Quote: quote);
            })));
    }

    internal static SecondChanceResult TryUse(Wizard live, ulong chestId, string zone, ulong instance,
        SecondChanceRules rules, MobRewardRules rewards, Action<IReadOnlyList<IMessage>> publish = null,
        Func<bool> isCurrent = null, SecondChanceChests state = null, IActorRef owner = null) {
        state ??= SecondChanceChests.Instance;
        if (!IdentityReady(live, live?.CharId ?? 0, live?.Account?.AccountId ?? 0)
            || chestId == 0 || rules is null || rewards is null) return new(SecondChanceStatus.PreparationFailed);
        var charId = live.CharId; var accountId = live.Account.AccountId;
        if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
        return AccountCollection.WithAccountWriteLane<SecondChanceResult>(accountId, () => WizardCollection.WithCharacterLock<SecondChanceResult>(charId,
            () => state.WithGate<SecondChanceResult>(() => {
                if (WizardCollection.IsInventorySnapshotUncertain(live)) return new(SecondChanceStatus.PreparationFailed);
                if (!IdentityReady(live, charId, accountId)) return ContextLost(state, charId, owner);
                if (!ContextMatches(live, charId, accountId, zone, isCurrent)) return ContextLost(state, charId, owner);
                var quote = state.QuoteFor(charId);
                if (quote is null || quote.ChestId != chestId || !Equals(quote.Owner, owner)) return new(SecondChanceStatus.Refused, ChestRefusal.NoPrompt);
                if (!state.HasDefeated(charId, quote.Chest, zone, instance) || quote.Instance != instance
                    || !string.Equals(quote.Zone, zone, StringComparison.OrdinalIgnoreCase))
                    return new(SecondChanceStatus.Refused, ChestRefusal.BossNotDefeated, quote);
                var day = state.CaptureDay(); // one day owns the price, stored count and returned next quote
                var outcome = new SecondChanceResult(SecondChanceStatus.PreparationFailed, Quote: quote);
                SecondChanceReceipt receipt = null;
                StagedStackRewards staged = null;
                Account account = null;
                var committed = WizardCollection.CommitCharacterMutation(charId, (session, saved) => {
                    if (WizardCollection.IsInventorySnapshotUncertain(live)) return false;
                    if (!IdentityReady(live, charId, accountId)) { outcome = ContextLost(state, charId, owner); return false; }
                    if (!ContextMatches(live, charId, accountId, zone, isCurrent)) { outcome = ContextLost(state, charId, owner); return false; }
                    // CommitCharacterMutation's loader and this complete uniqueness query must be the same original.
                    if (!ReferenceEquals(saved, ReadWizard(session, charId))) return false;
                    account = ReadAccount(session, accountId);
                    if (!Owned(saved, account, charId, accountId)
                        || saved.GameStats is null || live.GameStats is null
                        || saved.GameStats.m_currentGold < 0 || saved.GameStats.m_baseGoldPouch < 0
                        || !SecondChanceUses.TryRead(session, charId, day, out var record, out var uses)) return false;
                    if (!SameZone(saved, zone)) { outcome = ContextLost(state, charId, owner); return false; }
                    var used = uses.GetValueOrDefault(Key(quote.Chest.Template));
                    if (!TryCost(rules, used, out var cost)) return false;
                    if (quote.Day != day || quote.Used != used || quote.Cost != cost
                        || quote.Uses != Math.Max(0, rules.DailyUses - used)
                        || rules.ChestByTemplate(quote.Chest.Template) != quote.Chest) {
                        state.CloseOwned(charId, owner);
                        outcome = new(SecondChanceStatus.Refused, ChestRefusal.QuoteChanged, quote);
                        return false;
                    }
                    if (used >= rules.DailyUses) {
                        outcome = new(SecondChanceStatus.Refused, ChestRefusal.NoUsesLeft, quote); return false;
                    }
                    if (account.Crowns < cost) {
                        outcome = new(SecondChanceStatus.Refused, ChestRefusal.NotEnoughCrowns, quote); return false;
                    }
                    var dependencies = TestScope.Value ?? new();
                    DropTableResult rolled;
                    try {
                        rolled = dependencies.Roll is { } roll ? roll(quote.BossTemplate, rewards) : Roll(quote, rewards);
                    }
                    catch (Exception error) { LogPreparation(charId, chestId, error); return false; }
                    // Only the boss reward family is supported here; no general LootGranter side effects.
                    if (rolled is null || rolled.GoldAmount < 0 || rolled.ExperienceAmount != 0
                        || rolled.TrainingPoints != 0 || rolled.GrantsPotionSlot || rolled.Items is null
                        || rolled.TreasureCards is null || rolled.Reagents is null) return false;
                    var scaledGold = ClassicSettings.Scale(rolled.GoldAmount, ClassicSettings.GoldMultiplier);
                    if (scaledGold < 0) return false;
                    var oldGold = saved.GameStats.m_currentGold;
                    // Capacity limits the positive award, never erases already owned over-cap gold.
                    var appliedGold = Math.Min(scaledGold, Math.Max(0, saved.GameStats.m_baseGoldPouch - oldGold));
                    var gold = oldGold + appliedGold;
                    if (!TryCost(rules, used + 1, out var nextCost)) return false;
                    if (!ClassicStackRewards.TryPrepare(live, rolled.Items, rolled.TreasureCards, rolled.Reagents, out var prepared)
                        || !ClassicStackRewards.TryStage(session, saved, prepared, out staged)) return false;
                    IReadOnlyList<IMessage> messages;
                    try {
                        messages = PrepareResult(live, quote, rules, used + 1, nextCost, account.Crowns - cost,
                            gold, saved.GameStats.m_baseGoldPouch, scaledGold > 0, appliedGold, staged.Receipt, dependencies);
                    }
                    catch (Exception error) { LogPreparation(charId, chestId, error); return false; }
                    if (!ContextMatches(live, charId, accountId, zone, isCurrent)) { outcome = ContextLost(state, charId, owner); return false; }
                    if (messages is null) return false;
                    account.Crowns -= cost;
                    saved.GameStats.m_currentGold = gold;
                    uses[Key(quote.Chest.Template)] = used + 1;
                    // All complete original/item/global queries precede the new use document store.
                    SecondChanceUses.Stage(session, charId, day, record, uses);
                    WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                    // An empty/capacity-refused roll changes only account/use documents. Raven can
                    // rewrite an unchanged deserialized wizard; preserve its original change vector.
                    if (appliedGold == 0 && !staged.HasRewards) session.Advanced.IgnoreChangesFor(saved);
                    if (!ContextMatches(live, charId, accountId, zone, isCurrent)) { outcome = ContextLost(state, charId, owner); return false; }
                    receipt = new(quote, used + 1, account.Crowns, gold, appliedGold, staged.Receipt, messages) { NextCost = nextCost };
                    return true; // an admitted empty/full-capacity roll is still a valid paid use
                }, saved => {
                    // A scene change cannot undo an acknowledged save. Publish only still-bound aliases;
                    // travelling/replaced contexts receive no old-scene packets and no synthetic rollback.
                    TestScope.Value?.BeforePublish?.Invoke();
                    if (!IdentityReady(live, charId, accountId)) {
                        outcome = ContextLost(state, charId, owner, receipt); return;
                    }
                    live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
                    live.Account.Crowns = account.Crowns;
                    var published = ClassicStackRewards.Publish(live, saved, staged);
                    receipt = receipt with { Rewards = published };
                    if (!ContextMatches(live, charId, accountId, zone, isCurrent)) {
                        outcome = ContextLost(state, charId, owner, receipt); return;
                    }
                    state.Remember(quote with { Day = day, Used = receipt.NewUsed,
                        Cost = receipt.NextCost, Uses = Math.Max(0, rules.DailyUses - receipt.NewUsed) });
                    // Current context is checked again for each contextual send by the production caller.
                    publish?.Invoke(receipt.Messages);
                    if (!ContextMatches(live, charId, accountId, zone, isCurrent)) outcome = ContextLost(state, charId, owner, receipt);

                }, onSaveFailure: _ => {
                    WizardCollection.MarkInventorySnapshotUncertain(live);
                    state.CloseOwned(charId, owner);
                });
                return committed && outcome.Status != SecondChanceStatus.ContextLost
                    ? new(SecondChanceStatus.Committed, Quote: quote, Receipt: receipt) : outcome;
            })));
    }

    private static IDocumentSession OpenSession()
        => WizardCollection.TestStoreScope.Value is { } test ? test.Open() : PlayerDatabase.Instance.Store.OpenSession();
    private static Wizard ReadWizard(IDocumentSession session, ulong id) {
        var rows = session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(QueryWait)).Where(wizard => wizard.CharId == id).Take(2).ToList();
        return rows.Count == 1 ? rows[0] : null;
    }
    private static Account ReadAccount(IDocumentSession session, ulong id) {
        var rows = session.Query<Account>(collectionName: AccountCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(QueryWait)).Where(account => account.AccountId == id).Take(2).ToList();
        return rows.Count == 1 ? rows[0] : null;
    }
    private static bool Owned(Wizard saved, Account account, ulong charId, ulong accountId)
        => saved?.CharId == charId && saved.AccountId == accountId && account?.AccountId == accountId
            && account.CharacterIds?.Count(id => id == charId) == 1 && account.Crowns >= 0;
    private static bool SameZone(Wizard saved, string zone)
        => string.Equals(saved?.Zone, zone, StringComparison.OrdinalIgnoreCase);
    private static bool IdentityReady(Wizard live, ulong charId, ulong accountId)
        => live is not null && charId != 0 && accountId != 0 && live.CharId == charId
            && live.AccountId == accountId && live.Account?.AccountId == accountId
            && !WizardCollection.IsInventorySnapshotUncertain(live);
    private static bool ContextMatches(Wizard live, ulong charId, ulong accountId, string zone, Func<bool> current)
        => IdentityReady(live, charId, accountId) && SameZone(live, zone) && (current?.Invoke() ?? true);
    private static SecondChanceResult ContextLost(SecondChanceChests state, ulong charId, IActorRef owner, SecondChanceReceipt receipt = null) {
        state.CloseOwned(charId, owner); return new(SecondChanceStatus.ContextLost, Quote: receipt?.Quote, Receipt: receipt);
    }
    private static void LogPreparation(ulong charId, ulong chestId, Exception error)
        => Logger.Warning("Second Chance: wizard {0}, chest {1}, preparation failed ({2}).", Logger.Args(charId, chestId, error.GetType().Name));
    private static string Key(ulong id) => id.ToString(CultureInfo.InvariantCulture);
    private static bool TryCost(SecondChanceRules rules, int used, out int cost) {
        var value = (long)rules.FirstCost + (long)rules.CostStep * used;
        cost = 0;
        if (used < 0 || rules.DailyUses <= 0 || rules.FirstCost < 0 || rules.CostStep < 0 || value < 0 || value > int.MaxValue) return false;
        cost = (int)value; return true;
    }
    private static DropTableResult Roll(SecondChanceQuote quote, MobRewardRules rewards) {
        var result = new DropTableResult { DropTableId = "second_chance:" + quote.Chest.Chest };
        CombatService.AddClassicMobLoot(rewards, quote.BossTemplate, result, Random.Shared);
        return result;
    }

    private static IReadOnlyList<IMessage> PrepareResult(Wizard live, SecondChanceQuote quote, SecondChanceRules rules,
        int used, int nextCost, int balance, int gold, int maxGold, bool updateGold, int appliedGold,
        StackRewardReceipt rewards, SecondChanceDependencies dependencies) {
        var accepted = new DropTableResult {
            GoldAmount = appliedGold,
            Items = rewards.Items.Select(item => new DropItemResult { ItemId = Key(item.Item.m_templateID.Full), Quantity = 1 }).ToList(),
            TreasureCards = rewards.Cards.Select(card => card.TemplateId).ToList(),
            TreasureCardSpellIds = rewards.Cards.Select(card => card.SpellHash).ToList(),
            Reagents = rewards.Reagents.Select(reagent => new DropItemResult {
                ItemId = Key(reagent.Reagent.m_templateID.Full), Quantity = reagent.Acquired }).ToList(),
        };
        var loot = DropTableConverter.ToLootInfoList(accepted);
        var lootData = dependencies.SerializeLoot is { } serialize ? serialize(loot, 5)
            : new ObjectSerializer(Behaviors: SerializerFlags.None).Serialize(loot, 5, out var data) ? data : default;
        if (lootData.Length == 0) return null;
        List<IMessage> messages = [];
        if (updateGold) messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = gold, MaxGold = maxGold });
        foreach (var item in rewards.Items) messages.Add(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = live.GameObjectID, SerializedItem = item.Data });
        foreach (var card in rewards.Cards) messages.Add(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
            SpellID = unchecked((int)card.SpellHash), EnchantmentID = 0 });
        foreach (var reagent in rewards.Reagents) {
            messages.Add(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = live.GameObjectID, Data = reagent.Data });
            messages.Add(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION { ItemGlobalID = reagent.Reagent.m_globalID,
                ItemTemplateID = (uint)reagent.Reagent.m_templateID, ItemLocation = 1 });
        }
        if (rewards.BackpackCapacityExceeded) messages.Add(ClassicChat.Line(
            "Your backpack is full, so a reward item could not be added. Make room and try again later."));
        messages.Add(ClassicCrowns.BalanceMessage(new Account { Crowns = balance }, live.CharId));
        messages.Add(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT { Id = quote.ChestId,
            Cost = nextCost, Balance = balance, Uses = Math.Max(0, rules.DailyUses - used), Loot = lootData });
        return PrepareMessages(messages) ? messages.ToArray() : null;
    }

    // CLASSIC: encode every owned final message before the save. Ordinary transport re-encodes the
    // unchanged objects after ACK; this proves preparation, not socket delivery or native acceptance.
    private static bool PrepareMessages(IEnumerable<IMessage> messages) {
        try {
            foreach (var message in messages)
                if (message is null || MessageEncoder.Encode(message).Length == 0 || TestScope.Value?.Prepare?.Invoke(message) == false) return false;
            return true;
        }
        catch { return false; }
    }
}
