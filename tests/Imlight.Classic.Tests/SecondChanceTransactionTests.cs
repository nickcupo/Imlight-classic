// CLASSIC: the actual chest callers cannot publish/payment-compensate around a split reward write.
using System;
using Akka.Actor;
using Action = System.Action;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.SecondChance;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SecondChanceTransactionTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void InteractionQuotesFreshAccountAndDurableUseCountWithoutSavingOrChangingLiveBalances() {
        using var f = new Fixture(); f.Live.Account.Crowns = 1; f.SetUses(2);
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status);
        Assert.Equal(0, f.Saves); Assert.Equal(1, f.Opened); Assert.Equal(1, f.Live.Account.Crowns);
        Assert.Equal(new[] { typeof(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_CROWNS_BALANCE), typeof(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_PROMPT) }, f.Sent.Select(packet => packet.GetType()));
        Assert.Equal(1000, ((WIZARD_12_PROTOCOL.MSG_PAID_LOOT_CROWNS_BALANCE)f.Sent[0]).Balance);
        var prompt = (WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_PROMPT)f.Sent[1];
        Assert.Equal(f.Rules.CostOfUse(2), prompt.Cost); Assert.Equal(f.Rules.DailyUses - 2, prompt.Uses);
        Assert.Equal(Fixture.ChestId, prompt.Id); Assert.Equal(0, f.Rolls); Assert.False(PlayerDatabase.IsCreated);
    }

    [Fact]
    public void ResponseCommitsFreshPaymentUseGoldAndAcceptedMixedRewardsOnceBeforeOriginalOrderedPackets() {
        using var f = new Fixture(); f.Reagents.Add(Fixture.ReagentRow(998)); f.Saved.AlchemyBehavior.ReagentItemIds = [Fixture.ReagentId];
        f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(Fixture.Card, 998).ToList();
        f.Reload(); var stats = f.Live.GameStats; var account = f.Live.Account; var book = f.Live.SpellbookBehavior;
        var reagent = Assert.Single(f.Live.AlchemyBehavior.Reagents); var deck = book.DeckTreasureCards;
        var deckCards = deck[Fixture.Deck]; deckCards[Fixture.Card] = 41;
        deck[Fixture.Deck + 1] = new() { [Fixture.Card] = 7 }; // stale attached ledger must be replaced from saved authority
        f.Live.Account.Crowns = 1; f.Live.GameStats.m_currentGold = 3;
        f.Roll = new() { GoldAmount = 17, Items = [Fixture.Drop(Fixture.Gear, 9)], TreasureCards = [Fixture.Card, Fixture.Card],
            Reagents = [Fixture.Drop(Fixture.Reagent, 3)] };
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear(); f.Prepared.Clear(); var before = f.LiveSnapshot();
        f.OnSave = () => { Assert.Equal(before, f.LiveSnapshot()); Assert.Empty(f.Sent); Assert.NotEmpty(f.Prepared);
            Assert.Same(deck, book.DeckTreasureCards); Assert.Equal(41, book.DeckTreasureCount(Fixture.Deck, Fixture.Card)); Assert.Equal(2, deck.Count); };
        f.OnSend = _ => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); Assert.Equal(950, f.Live.Account.Crowns); };
        var result = f.Use(); Assert.Equal(SecondChanceStatus.Committed, result.Status); var receipt = result.Receipt!;
        var scaled = ClassicSettings.Scale(17, ClassicSettings.GoldMultiplier); var expectedGold = Math.Min(1000, 980 + scaled);
        Assert.Equal(expectedGold, f.Saved.GameStats.m_currentGold); Assert.Equal(expectedGold, receipt.Gold);
        Assert.Equal(Math.Max(0, expectedGold - 980), receipt.AppliedGold);
        Assert.Equal(950, f.Account.Crowns); Assert.Equal(1, f.Used); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls);
        Assert.Single(f.Items); Assert.Equal(Fixture.Owner, Assert.Single(receipt.Rewards.Items).Item.m_characterId.Full);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count); Assert.Single(receipt.Rewards.Cards);
        Assert.Equal(999, Assert.Single(f.Reagents).m_quantity); Assert.Equal(1, Assert.Single(receipt.Rewards.Reagents).Acquired);
        Assert.Same(stats, f.Live.GameStats); Assert.Same(account, f.Live.Account); Assert.Same(book, f.Live.SpellbookBehavior);
        Assert.Same(reagent, Assert.Single(f.Live.AlchemyBehavior.Reagents)); Assert.Equal(999, book.TreasureCardTemplateIds.Count);
        Assert.NotSame(deck, book.DeckTreasureCards); Assert.NotSame(deckCards, book.DeckTreasureCards[Fixture.Deck]);
        Assert.NotSame(f.Saved.SpellbookBehavior.DeckTreasureCards, book.DeckTreasureCards);
        Assert.NotSame(f.Saved.SpellbookBehavior.DeckTreasureCards[Fixture.Deck], book.DeckTreasureCards[Fixture.Deck]);
        Assert.Equal(Fixture.Deck, Assert.Single(book.DeckTreasureCards).Key);
        var publishedCard = Assert.Single(book.DeckTreasureCards[Fixture.Deck]);
        Assert.Equal(Fixture.Card, publishedCard.Key); Assert.Equal(3, publishedCard.Value);
        Assert.Equal(3, f.Saved.SpellbookBehavior.DeckTreasureCards[Fixture.Deck][Fixture.Card]);
        Assert.Equal(41, deckCards[Fixture.Card]); Assert.Equal(2, deck.Count);
        Assert.Equal(77, f.Saved.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(4, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(1.25f, f.Saved.GameStats.m_potionCharge);
        Assert.Equal(new[] { typeof(WIZARD_12_PROTOCOL.MSG_UPDATEGOLD), typeof(GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM),
            typeof(WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK), typeof(WIZARD_12_PROTOCOL.MSG_REAGENTADD),
            typeof(WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION), typeof(WIZARD_12_PROTOCOL.MSG_CROWNBALANCE),
            typeof(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT) }, f.Sent.Select(packet => packet.GetType()));
        Assert.Equal(Fixture.Owner + 2, ((GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM)f.Sent[1]).GlobalID);
        var crown = Assert.Single(f.Sent.OfType<WIZARD_12_PROTOCOL.MSG_CROWNBALANCE>()); Assert.Equal(Fixture.Owner, crown.CharacterID); Assert.Equal(950, crown.TotalCrowns);
        var native = Assert.Single(f.Sent.OfType<WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT>());
        Assert.Equal(100, native.Cost); Assert.Equal(f.Rules.DailyUses - 1, native.Uses); Assert.NotEqual(0, native.Loot.Length);
        var loot = Assert.Single(f.Loot); Assert.Equal(5u, loot.Flags); Assert.Equal(receipt.AppliedGold, loot.Value.m_goldInfo?.m_goldAmount ?? 0);
        Assert.Equal(new[] { 1, 1 }, loot.Value.m_loot.OfType<ItemLootInfo>().Select(item => item.m_numItems));
        Assert.Single(loot.Value.m_loot.OfType<TreasureCardLootInfo>());
        Assert.Equal(f.Prepared.Count, f.Sent.Count);
        for (var index = 0; index < f.Sent.Count; index++) {
            Assert.Same(f.Prepared[index], f.Sent[index]);
            Assert.NotEmpty(MessageEncoder.Encode(f.Sent[index]));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyAndAllFullRollsStillPayCountAndReturnOnlyAdmittedRewards(bool full) {
        using var f = new Fixture();
        if (full) {
            f.FillBag(); f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(Fixture.Card, 999).ToList();
            f.Reagents = [Fixture.ReagentRow(999)]; f.Saved.AlchemyBehavior.ReagentItemIds = [Fixture.ReagentId]; f.Reload();
            f.Roll = new() { Items = [Fixture.Drop(Fixture.Gear)], TreasureCards = [Fixture.Card], Reagents = [Fixture.Drop(Fixture.Reagent)] };
        }
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear(); var beforeItems = f.Items.Count;
        var result = f.Use(); Assert.Equal(SecondChanceStatus.Committed, result.Status);
        Assert.Equal(1, f.Saves); Assert.Equal(950, f.Account.Crowns); Assert.Equal(1, f.Used); Assert.Equal(980, f.Saved.GameStats.m_currentGold);
        Assert.Empty(result.Receipt!.Rewards.Items); Assert.Empty(result.Receipt.Rewards.Cards); Assert.Empty(result.Receipt.Rewards.Reagents);
        Assert.Equal(beforeItems, f.Items.Count); Assert.Empty(Assert.Single(f.Loot).Value.m_loot);
        Assert.Null(Assert.Single(f.Loot).Value.m_goldInfo); Assert.Equal(2, f.Sent.Count(packet => packet is WIZARD_12_PROTOCOL.MSG_CROWNBALANCE or WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT));
        Assert.Equal(full, result.Receipt.Rewards.BackpackCapacityExceeded);
    }

    [Theory]
    [InlineData("loot-empty")] [InlineData("loot-throw")] [InlineData("item-empty")]
    [InlineData("reagent-empty")] [InlineData("encoder-hook")] [InlineData("roll-throw")]
    [InlineData("xp")] [InlineData("potion")] [InlineData("training")]
    public void PreSavePreparationFailuresCloseWithoutDebitUseRewardOrSuccess(string failure) {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.Roll = new() { GoldAmount = 17, Items = [Fixture.Drop(Fixture.Gear)], TreasureCards = [Fixture.Card], Reagents = [Fixture.Drop(Fixture.Reagent)] };
        switch (failure) {
            case "loot-empty": f.Dependencies.SerializeLoot = (_, _) => default; break;
            case "loot-throw": f.Dependencies.SerializeLoot = (_, _) => throw new InvalidOperationException("authored native refusal"); break;
            case "item-empty": f.Stack.SerializeItem = _ => default; break;
            case "reagent-empty": f.Stack.SerializeReagent = _ => default; break;
            case "encoder-hook": f.Dependencies.Prepare = _ => false; break;
            case "roll-throw": f.Dependencies.Roll = (_, _) => throw new InvalidOperationException("authored roll failure"); break;
            case "xp": f.Roll.ExperienceAmount = 1; break;
            case "potion": f.Roll.GrantsPotionSlot = true; break;
            case "training": f.Roll.TrainingPoints = 1; break;
        }
        var before = f.LiveSnapshot(); Assert.Equal(SecondChanceStatus.PreparationFailed, f.Use().Status);
        Assert.Equal(1, f.Closes); Assert.Empty(f.Sent); Assert.Equal(0, f.Saves); Assert.Equal(1000, f.Account.Crowns);
        Assert.Null(f.UseRecord); Assert.Empty(f.Items); Assert.Empty(f.Reagents); Assert.Equal(before, f.LiveSnapshot());
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Null(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
    }

    [Theory]
    [InlineData("before")] [InlineData("lost")] [InlineData("publication")] [InlineData("send")]
    public void UnknownSaveOrPublicationClosesAndQuarantinesInLaneWithoutRefundRetryOrReroll(string failure) {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.Roll = new() { GoldAmount = 17, TreasureCards = [Fixture.Card] }; var before = f.LiveSnapshot();
        f.FailSave = failure is "before" or "lost"; f.Durable = failure == "lost";
        if (failure == "publication") f.Dependencies.BeforePublish = () => throw new InvalidOperationException("authored publication failure");
        if (failure == "send") f.OnSend = _ => throw new InvalidOperationException("authored socket queue failure");
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); };
        Assert.Equal(SecondChanceStatus.PreparationFailed, f.Use().Status); Assert.Equal(1, f.Closes); Assert.Empty(f.Sent);
        Assert.Equal(1, f.Saves); Assert.Equal(failure == "before" ? 1000 : 950, f.Account.Crowns); Assert.Equal(failure == "before" ? 0 : 1, f.Used);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        if (failure != "send") Assert.Equal(before, f.LiveSnapshot()); // send failure may follow selected ACK publication
        var opened = f.Opened; Assert.Equal(SecondChanceStatus.PreparationFailed, f.Use().Status);
        Assert.Equal(opened, f.Opened); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Equal(2, f.Closes);
        Assert.Null(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
    }

    [Theory]
    [InlineData("foreign-account")] [InlineData("missing-membership")] [InlineData("duplicate-account")]
    [InlineData("duplicate-wizard")] [InlineData("foreign-use")] [InlineData("negative-use")]
    [InlineData("future-day")] [InlineData("bad-day")] [InlineData("bad-key")] [InlineData("load-failure")]
    public void FreshAuthorityFailuresCannotShowAnUnreliableQuoteOrPay(string failure) {
        using var f = new Fixture();
        switch (failure) {
            case "foreign-account": f.Saved.AccountId++; break;
            case "missing-membership": f.Account.CharacterIds.Clear(); break;
            case "duplicate-account": f.DuplicateAccount = true; break;
            case "duplicate-wizard": f.DuplicateWizard = true; break;
            case "foreign-use": f.SetUses(0); f.UseRecord!.CharId++; break;
            case "negative-use": f.SetUses(-1); break;
            case "future-day": f.SetUses(0); f.UseRecord!.Day = SecondChanceUseRecord.DayText(DateOnly.FromDateTime(f.Now.AddDays(1))); break;
            case "bad-day": f.SetUses(0); f.UseRecord!.Day = "not-a-day"; break;
            case "bad-key": f.SetUses(0); f.UseRecord!.Uses["0001"] = 1; break;
            case "load-failure": f.FailLoad = true; break;
        }
        Assert.Equal(SecondChanceStatus.PreparationFailed, f.Open().Status); Assert.Empty(f.Sent); Assert.Equal(1, f.Closes);
        Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls); Assert.Equal(1000, f.Account.Crowns); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("foreign-account")] [InlineData("missing-membership")]
    [InlineData("duplicate-account")] [InlineData("duplicate-wizard")]
    [InlineData("foreign-use")] [InlineData("load-failure")]
    public void APreviouslyDisplayedPromptCannotBypassChangedFreshAuthority(string reason) {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        switch (reason) {
            case "foreign-account": f.Saved.AccountId++; break;
            case "missing-membership": f.Account.CharacterIds.Clear(); break;
            case "duplicate-account": f.DuplicateAccount = true; break;
            case "duplicate-wizard": f.DuplicateWizard = true; break;
            case "foreign-use": f.SetUses(0); f.UseRecord!.CharId++; break;
            case "load-failure": f.FailLoad = true; break;
        }
        Assert.Equal(SecondChanceStatus.PreparationFailed, f.Use().Status);
        Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls); Assert.Equal(1000, f.Account.Crowns);
        Assert.Empty(f.Sent); Assert.Equal(1, f.Closes); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("foreign-item-id")] [InlineData("missing-bag-row")] [InlineData("foreign-reagent-id")]
    public void ARewardIdentityCollisionOrCorruptReferenceRefusesTheEntirePaymentAndMixedRoll(string reason) {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.Roll = new() { GoldAmount = 17, Items = [Fixture.Drop(Fixture.Gear)], TreasureCards = [Fixture.Card], Reagents = [Fixture.Drop(Fixture.Reagent)] };
        switch (reason) {
            case "foreign-item-id":
                f.Items.Add(new() { m_globalID = 0x4C00000000000000UL + 786021, m_templateID = Fixture.Gear, m_characterId = Fixture.Owner + 1 }); break;
            case "missing-bag-row": f.Saved.InventoryBehavior.InventoryItemIds = [999999]; break;
            case "foreign-reagent-id": f.Reagents = [Fixture.ReagentRow(5) with { m_characterId = Fixture.Owner + 1 }]; break;
        }
        Assert.Equal(SecondChanceStatus.PreparationFailed, f.Use().Status);
        Assert.Equal(1000, f.Account.Crowns); Assert.Null(f.UseRecord); Assert.Equal(980, f.Saved.GameStats.m_currentGold);
        Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(0, f.Saves); Assert.Empty(f.Sent); Assert.Equal(1, f.Closes);
    }

    [Theory]
    [InlineData("poor")] [InlineData("wrong-id")] [InlineData("wrong-instance")] [InlineData("expired-win")]
    [InlineData("day-changed")] [InlineData("count-changed")]
    public void NormalRefusalsKeepPaymentAndCountsUntouchedAndUseExistingErrorReply(string reason) {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        var chestId = Fixture.ChestId; ulong instance = Fixture.Instance;
        switch (reason) {
            case "poor": f.Account.Crowns = 49; break;
            case "wrong-id": chestId++; break;
            case "wrong-instance": instance++; break;
            case "expired-win": f.Now = f.Now.AddHours(3); break;
            case "day-changed": f.Now = f.Now.AddDays(1); f.Win(); break;
            case "count-changed": f.SetUses(1); break;
        }
        var crowns = f.Account.Crowns; var used = f.Used;
        var result = f.Use(chestId, instance); Assert.Equal(SecondChanceStatus.Refused, result.Status);
        Assert.Equal(crowns, f.Account.Crowns); Assert.Equal(used, f.Used); Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls);
        Assert.Single(f.Sent.OfType<WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_ERROR>()); Assert.Equal(0, f.Closes);
        if (reason is "day-changed" or "count-changed") Assert.Equal(ChestRefusal.QuoteChanged, result.Refusal);
        if (reason == "poor") Assert.Equal(ChestRefusal.NotEnoughCrowns, result.Refusal);
    }

    [Fact]
    public void ReopenAfterDayChangeUsesOneNewDayAndResetsDurableCountsWithoutOldPriceCharge() {
        using var f = new Fixture(); f.SetUses(2); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status);
        f.Now = f.Now.AddDays(1); f.Win(); f.Sent.Clear();
        Assert.Equal(ChestRefusal.QuoteChanged, f.Use().Refusal); Assert.Equal(0, f.Saves);
        Assert.Equal(50, f.Open().Quote!.Cost); f.Sent.Clear();
        f.OnSave = () => f.Now = f.Now.AddDays(1); // the captured transaction day remains authoritative
        Assert.Equal(SecondChanceStatus.Committed, f.Use().Status);
        Assert.Equal("2009-10-18", f.UseRecord!.Day); Assert.Equal(1, f.Used); Assert.Equal(950, f.Account.Crowns);
        Assert.Equal("2009-10-18", SecondChanceUseRecord.DayText(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner))!.Day));
    }

    [Fact]
    public void CloseAndRefusedOpenCannotLeaveAnOlderPromptSpendable() {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        var close = SecondChanceService.UseAcknowledged(f.Live, new() { Id = Fixture.ChestId, Response = 0 }, f.Chest.Zone,
            Fixture.Instance, f.Rules, f.RewardRules, f.Send, () => f.Closes++, state: f.State);
        Assert.Equal(SecondChanceStatus.Refused, close.Status); Assert.Empty(f.Sent); Assert.Equal(0, f.Saves);
        Assert.Equal(ChestRefusal.NoPrompt, f.Use().Refusal); f.Sent.Clear(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status);
        f.State.RecordWin(Fixture.Owner, "QA/Elsewhere", 0, [1]); f.Sent.Clear();
        Assert.Equal(ChestRefusal.BossNotDefeated, f.Open().Refusal); Assert.Single(f.Sent);
        f.Sent.Clear(); Assert.Equal(ChestRefusal.NoPrompt, f.Use().Refusal); Assert.Equal(0, f.Saves);
    }

    [Fact]
    public async Task QueuedSameAccountRollsObserveFreshCountAndStopAtTheDailyLimit() {
        using var f = new Fixture(); f.Account.Crowns = 10000; f.Reload(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        f.OnSave = () => { if (f.Saves == 1) { entered.Set(); Assert.True(release.Wait(Timeout)); } };
        var first = Task.Run(() => f.Use()); Task<SecondChanceResult>? second = null;
        try {
            Assert.True(entered.Wait(Timeout));
            second = Task.Run(() => { attempted.Set(); return f.Use(); });
            Assert.True(attempted.Wait(Timeout)); Assert.False(second.IsCompleted); Assert.Equal(1, f.Saves);
        }
        finally { release.Set(); }
        Assert.Equal(SecondChanceStatus.Committed, (await first).Status); Assert.Equal(SecondChanceStatus.Committed, (await second!).Status);
        Assert.Equal(2, f.Saves); Assert.Equal(2, f.Used); Assert.Equal(9850, f.Account.Crowns);
        f.OnSave = null;
        for (var i = 2; i < f.Rules.DailyUses; i++) Assert.Equal(SecondChanceStatus.Committed, f.Use().Status);
        var spent = Enumerable.Range(0, f.Rules.DailyUses).Sum(f.Rules.CostOfUse);
        Assert.Equal(10000 - spent, f.Account.Crowns); Assert.Equal(ChestRefusal.NoUsesLeft, f.Use().Refusal);
        Assert.Equal(f.Rules.DailyUses, f.Saves); Assert.Equal(f.Rules.DailyUses, f.Rolls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AQueuedRollCannotReopenOrSaveAnUncertainSnapshot(bool durable) {
        using var f = new Fixture { FailSave = true, Durable = durable };
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        f.OnSave = () => { entered.Set(); Assert.True(release.Wait(Timeout)); };
        var first = Task.Run(() => f.Use()); Task<SecondChanceResult>? second = null;
        try {
            Assert.True(entered.Wait(Timeout)); second = Task.Run(() => { attempted.Set(); return f.Use(); });
            Assert.True(attempted.Wait(Timeout)); Assert.False(second.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(SecondChanceStatus.PreparationFailed, (await first).Status); Assert.Equal(SecondChanceStatus.PreparationFailed, (await second!).Status);
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Equal(2, f.Opened); Assert.Empty(f.Sent);
        Assert.Equal(durable ? 1 : 0, f.Used); Assert.Equal(durable ? 950 : 1000, f.Account.Crowns);
    }

    [Fact]
    public void FreshOpenReplacementAndFailedPromptPreparationDoNotSaveOrRoll() {
        using var f = new Fixture(); var current = true;
        f.BeforeOpen = () => current = false;
        Assert.Equal(SecondChanceStatus.ContextLost, f.Open(() => current).Status);
        Assert.Equal(0, f.Closes); Assert.Empty(f.Sent); Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls);
        f.BeforeOpen = null; f.Dependencies.Prepare = _ => false;
        Assert.Equal(SecondChanceStatus.PreparationFailed, f.Open().Status); Assert.Equal(1, f.Closes); Assert.Empty(f.Sent);
    }

    [Theory]
    [InlineData(0)] [InlineData(17)]
    public void ExistingOverfullGoldIsNeverErasedByAnEmptyOrPositiveRoll(int rolledGold) {
        using var f = new Fixture(); f.Saved.GameStats.m_currentGold = 1100; f.Reload();
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear(); f.Roll = new() { GoldAmount = rolledGold };
        var result = f.Use(); Assert.Equal(SecondChanceStatus.Committed, result.Status);
        Assert.Equal(1100, f.Saved.GameStats.m_currentGold); Assert.Equal(1100, f.Live.GameStats.m_currentGold);
        Assert.Equal(0, result.Receipt!.AppliedGold); Assert.Null(Assert.Single(f.Loot).Value.m_goldInfo);
        Assert.Equal(950, f.Account.Crowns); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void UnrepresentableNextPriceRefusesBeforeSaveWithoutClaimingPaymentOrResult() {
        using var f = new Fixture(); var old = f.Rules;
        f.Rules = new() { Id = old.Id, Profiles = old.Profiles, SourceFile = old.SourceFile, Chests = old.Chests,
            DailyUses = 2, FirstCost = 1000, CostStep = int.MaxValue };
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        Assert.Equal(SecondChanceStatus.PreparationFailed, f.Use().Status);
        Assert.Equal(1000, f.Account.Crowns); Assert.Null(f.UseRecord); Assert.Equal(0, f.Saves); Assert.Empty(f.Sent);
        Assert.Equal(1, f.Closes); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("before-open")] [InlineData("native-prepare")] [InlineData("ack")]
    [InlineData("send")]
    public void OrdinaryTravelCancelsContextWithoutClosingAndRetainsAnyAcknowledgedState(string timing) {
        using var f = new Fixture(); var current = true;
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.Roll = new() { GoldAmount = 17, TreasureCards = [Fixture.Card] };
        switch (timing) {
            case "before-open": f.BeforeOpen = () => current = false; break;
            case "native-prepare": f.Dependencies.Prepare = _ => { current = false; return true; }; break;
            case "ack": f.OnSave = () => current = false; break;
            case "send": f.OnSend = _ => current = false; break;
        }
        var result = f.Use(current: () => current); Assert.Equal(SecondChanceStatus.ContextLost, result.Status);
        Assert.Equal(0, f.Closes); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Null(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
        var acknowledged = timing is "ack" or "send";
        Assert.Equal(acknowledged ? 1 : 0, f.Saves); Assert.Equal(acknowledged ? 950 : 1000, f.Account.Crowns);
        Assert.Equal(acknowledged ? 1 : 0, f.Used); Assert.Equal(acknowledged ? 1 : 0, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(f.Account.Crowns, f.Live.Account.Crowns);
        Assert.Equal(f.Saved.SpellbookBehavior.TreasureCardTemplateIds, f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        if (timing != "send") Assert.Empty(f.Sent);
        else { Assert.Single(f.Sent); Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(f.Sent[0]); }
        Assert.Equal(acknowledged, result.Receipt is not null);
    }

    [Fact]
    public void SameAliasCharacterReplacementBeforeFreshReadCancelsWithoutClosingOrSaving() {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.BeforeOpen = () => f.Live.CharId++;
        Assert.Equal(SecondChanceStatus.ContextLost, f.Use().Status);
        Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls); Assert.Equal(0, f.Closes); Assert.Empty(f.Sent);
        Assert.Equal(1000, f.Account.Crowns); Assert.Null(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void FreshSavedSceneChangeCancelsAnOldOpenQuoteWithoutClosingTheTravelSocket() {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.Saved.Zone = "Authored/NewScene";
        Assert.Equal(SecondChanceStatus.ContextLost, f.Use().Status);
        Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls); Assert.Empty(f.Sent); Assert.Equal(0, f.Closes);
        Assert.Equal(1000, f.Account.Crowns); Assert.Null(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
    }

    [Fact]
    public void IdentityReplacementDuringAcknowledgedSaveNeverPublishesOldRewardsToTheNewCharacter() {
        using var f = new Fixture(); Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Sent.Clear();
        f.Roll = new() { TreasureCards = [Fixture.Card] }; f.OnSave = () => f.Live.CharId++;
        var result = f.Use(); Assert.Equal(SecondChanceStatus.ContextLost, result.Status);
        Assert.Equal(1, f.Saves); Assert.Equal(950, f.Account.Crowns); Assert.Equal(1, f.Used);
        Assert.Equal(Fixture.Owner, f.Saved.CharId); Assert.Single(f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(Fixture.Owner + 1, f.Live.CharId); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(1000, f.Live.Account.Crowns); Assert.Equal(0, f.Closes); Assert.Empty(f.Sent);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public async Task OpenWithoutResponseIsRetiredByItsOwnerAndWrongOwnerCannotSpendOrCloseIt() {
        using var f = new Fixture(); var system = ActorSystem.Create("AuthoredSecondChanceOwners");
        try {
            var first = system.ActorOf(Props.Create(() => new OwnerActor()), "first");
            var other = system.ActorOf(Props.Create(() => new OwnerActor()), "other");
            Assert.Equal(SecondChanceStatus.Opened, f.Open(owner: first).Status); f.Sent.Clear();
            Assert.Equal(ChestRefusal.NoPrompt, f.Use(owner: other).Refusal); Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls);
            Assert.Same(first, f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner))!.Owner);
            SecondChanceService.UseAcknowledged(f.Live, new() { Id = Fixture.ChestId, Response = 0 }, f.Chest.Zone,
                Fixture.Instance, f.Rules, f.RewardRules, f.Send, () => f.Closes++, state: f.State, owner: other);
            Assert.Same(first, f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner))!.Owner);
            f.State.ForgetOwner(other); Assert.NotNull(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
            f.State.ForgetOwner(first); Assert.Null(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
            Assert.Equal(0, f.Saves); Assert.Equal(0, f.Closes); Assert.Equal(ChestRefusal.NoPrompt, f.Use(owner: first).Refusal);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task ReplacementActorWithTheSamePathAndNewUidCannotUseAnOldPromptOrBeClearedByOldDisposal() {
        using var f = new Fixture(); var system = ActorSystem.Create("AuthoredSecondChanceIncarnations");
        try {
            var old = system.ActorOf(Props.Create(() => new OwnerActor()), "connection");
            Assert.Equal(SecondChanceStatus.Opened, f.Open(owner: old).Status); f.Sent.Clear();
            Assert.True(await old.GracefulStop(Timeout));
            IActorRef? replacement = null;
            var deadline = DateTime.UtcNow + Timeout;
            while (replacement is null) {
                try { replacement = system.ActorOf(Props.Create(() => new OwnerActor()), "connection"); }
                catch (InvalidActorNameException) when (DateTime.UtcNow < deadline) { await Task.Delay(10); }
            }
            Assert.Equal(old.Path.ToStringWithoutAddress(), replacement.Path.ToStringWithoutAddress());
            Assert.NotEqual(old, replacement);
            Assert.Equal(ChestRefusal.NoPrompt, f.Use(owner: replacement).Refusal); Assert.Equal(0, f.Saves); Assert.Equal(0, f.Rolls);
            Assert.Equal(SecondChanceStatus.Opened, f.Open(owner: replacement).Status); f.Sent.Clear();
            f.State.ForgetOwner(old); Assert.Same(replacement, f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner))!.Owner);
            Assert.Equal(SecondChanceStatus.ContextLost, f.Use(current: () => false, owner: old).Status);
            Assert.Same(replacement, f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner))!.Owner);
            Assert.Equal(SecondChanceStatus.Committed, f.Use(owner: replacement).Status);
            Assert.Same(replacement, f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner))!.Owner);
            f.State.ForgetOwner(old); Assert.NotNull(f.State.WithGate(() => f.State.QuoteFor(Fixture.Owner)));
            Assert.Equal(SecondChanceStatus.Committed, f.Use(owner: replacement).Status); Assert.Equal(2, f.Saves);
        }
        finally { await system.Terminate(); }
    }

    public sealed class OwnerActor : ReceiveActor {
        public OwnerActor() { ReceiveAny(_ => { }); }
    }

    internal sealed class Fixture : IDisposable {
        private readonly ClassicConfigurationSnapshot _configuration = new();
        internal const ulong Owner = 786001, AccountId = 786002, ChestId = 0x4C00000000786003, Instance = 786004;
        internal const uint Gear = 786010, Card = 786011, Reagent = 786012;
        internal const ulong ReagentId = 0x4C00000000786013, Deck = 786014;
        internal DateTime Now = new(2009, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        internal SecondChanceRules Rules = SecondChanceRulesLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "rules", "second-chance-2009.yaml"));
        internal readonly MobRewardRules RewardRules = MobRewardRulesLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "progression", "mob-rewards-2009.yaml"));
        internal SecondChanceChest Chest => Rules.ChestByName("KT_MonsterChest_Krokopatra")!;
        internal readonly SecondChanceChests State;
        internal Wizard Saved, Live;
        internal Account Account;
        internal List<WizClientObjectItem> Items = [];
        internal List<ClientReagentItem> Reagents = [];
        internal SecondChanceUseRecord? UseRecord;
        internal DropTableResult Roll = new();
        internal readonly SecondChanceDependencies Dependencies;
        internal readonly StackRewardDependencies Stack;
        internal readonly List<IMessage> Sent = [], Prepared = [];
        internal readonly List<(LootInfoList Value, uint Flags)> Loot = [];
        internal Action? OnSave, OnDispose, BeforeOpen;
        internal Action<IMessage>? OnSend;
        internal bool FailSave, Durable, FailLoad, DuplicateAccount, DuplicateWizard;
        internal int Saves, Opened, Rolls, Closes;
        private long _itemNumber = 786020;
        private readonly WizardCollection.TestStore? _store;
        private readonly SecondChanceDependencies? _dependencies;
        private readonly StackRewardDependencies? _stack;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _items;
        private readonly Func<IDocumentSession, List<ClientReagentItem>>? _reagents;
        private bool _scopesCaptured, _disposed;
        internal int Used => UseRecord?.Uses.GetValueOrDefault(Chest.Template.ToString(CultureInfo.InvariantCulture)) ?? 0;

        internal Fixture() {
            try {
                EquipmentAttachConcurrencyTests.Configure("[Logging]\nLogLevel=FATAL\n[Character]\nMaxInventoryItems=150\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
                _store = WizardCollection.TestStoreScope.Value; _dependencies = ClassicSecondChanceTransactions.TestScope.Value;
                _stack = ClassicStackRewards.TestScope.Value; _items = WizardInventoryTransactions.TestRowsScope.Value; _reagents = WizardReagentCollection.TestRowsScope.Value;
                _scopesCaptured = true;
                Account = NewAccount(1000);
                State = new(() => Now); Win();
                Saved = NewWizard(); Live = NewWizard(); Reload();
                Dependencies = new() {
                    Roll = (boss, _) => { Assert.Equal(35433UL, boss); Rolls++; return Roll; },
                    SerializeLoot = (loot, flags) => { Loot.Add((loot, flags)); return (ByteString)BitConverter.GetBytes(786099); },
                    Prepare = packet => { Prepared.Add(packet); return true; },
                };
                Stack = new() {
                    Template = id => id switch { Gear => new WizItemTemplate { m_templateID = Gear, m_behaviors = [] },
                        Card => new SpellTemplate { m_name = "Authored paid chest card" }, Reagent => new ReagentItemTemplate { m_templateID = Reagent }, _ => null! },
                    Create = id => id == Reagent ? ReagentRow(0) : new WizClientObjectItem { m_globalID = 0x4C00000000000000UL + (ulong)Interlocked.Increment(ref _itemNumber),
                        m_templateID = (uint)id, m_characterId = Owner, m_inactiveBehaviors = [] },
                    SerializeItem = item => (ByteString)BitConverter.GetBytes(item.m_globalID.Full),
                    SerializeReagent = row => (ByteString)BitConverter.GetBytes(row.m_quantity),
                };
                WizardCollection.TestStoreScope.Value = new(OpenSession, null);
                ClassicSecondChanceTransactions.TestScope.Value = Dependencies; ClassicStackRewards.TestScope.Value = Stack;
                WizardInventoryTransactions.TestRowsScope.Value = null; WizardReagentCollection.TestRowsScope.Value = null;
            } catch {
                Dispose();
                throw;
            }
        }
        internal void Win() => State.RecordWin(Owner, Chest.Zone, Instance, [35433]);
        internal SecondChanceResult Open(Func<bool>? current = null, IActorRef? owner = null)
            => InteractSecondChanceComponent.OpenAcknowledged(Live, ChestId, Chest, Chest.Zone, Instance, Rules, Send, () => Interlocked.Increment(ref Closes), current, State, owner);
        internal SecondChanceResult Use(ulong chestId = ChestId, ulong instance = Instance, Func<bool>? current = null, IActorRef? owner = null)
            => SecondChanceService.UseAcknowledged(Live, new() { Id = chestId, Response = 1 }, Chest.Zone, instance, Rules, RewardRules,
                Send, () => Interlocked.Increment(ref Closes), current, State, owner);
        internal void Send(IMessage packet) { OnSend?.Invoke(packet); lock (Sent) Sent.Add(packet); }
        internal void SetUses(int used) => UseRecord = new() { CharId = Owner, Day = SecondChanceUseRecord.DayText(DateOnly.FromDateTime(Now)),
            Uses = new() { [Chest.Template.ToString(CultureInfo.InvariantCulture)] = used } };
        internal void FillBag() {
            for (ulong id = 1; Saved.InventoryBehavior.InventoryItemIds.Count < ServerWizInventoryBehavior.MaxItemsAllowed; id++) {
                var item = new WizClientObjectItem { m_globalID = id, m_templateID = Gear, m_characterId = Owner, m_inactiveBehaviors = [] };
                Items.Add(item); Saved.InventoryBehavior.InventoryItemIds.Add(id);
            }
        }
        internal void Reload() {
            Live = CloneWizard(Saved); Live.Account = CloneAccount(Account);
            Live.InventoryBehavior.Items = [..Items.Select(CloneItem)]; Live.AlchemyBehavior.Reagents = Reagents.Select(row => row with { }).ToList();
        }
        internal string LiveSnapshot() => $"{Live.Account.Crowns}/{Live.GameStats.m_currentGold}/{string.Join(',', Live.InventoryBehavior.InventoryItemIds)}/{string.Join(',', Live.SpellbookBehavior.TreasureCardTemplateIds)}/{string.Join(',', Live.AlchemyBehavior.Reagents.Select(row => row.m_quantity))}";
        private IDocumentSession OpenSession() {
            Assert.True(WizardCollection.HoldsWriteLane); Opened++; BeforeOpen?.Invoke();
            var session = DispatchProxy.Create<IDocumentSession, ChestSession>(); var proxy = (ChestSession)(object)session;
            proxy.Wizard = CloneWizard(Saved); proxy.Account = CloneAccount(Account); proxy.Items = Items.Select(CloneItem).ToList();
            proxy.Reagents = Reagents.Select(row => row with { }).ToList(); proxy.UseRecord = CloneUse(UseRecord);
            proxy.DuplicateAccount = DuplicateAccount; proxy.DuplicateWizard = DuplicateWizard; proxy.FailLoad = FailLoad;
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Saves++; OnSave?.Invoke();
                if (FailSave && !Durable) throw new InvalidOperationException("authored save refused");
                Saved = CloneWizard(proxy.Wizard); Account = CloneAccount(proxy.Account); Items = proxy.Items.Select(CloneItem).ToList();
                Reagents = proxy.Reagents.Select(row => row with { }).ToList(); UseRecord = CloneUse(proxy.UseRecord);
                if (FailSave) throw new InvalidOperationException("authored durable save lost ACK");
            };
            proxy.DisposeSession = () => OnDispose?.Invoke(); return session;
        }
        private Wizard NewWizard() => new() { CharId = Owner, AccountId = AccountId, Zone = Chest.Zone,
            GameStats = new(MagicSchool.None, 0) { m_currentGold = 980, m_baseGoldPouch = 1000, m_potionCharge = 1.25f, m_potionMax = 3 },
            MagicSchoolBehavior = new() { Level = 2, MagicSchool = MagicSchool.Fire, ExperiencePoints = 77, TrainingPoints = 4 },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] }, EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [], Items = [] }, SpellbookBehavior = new() { LearnedSpellTemplateIds = [], TreasureCardTemplateIds = [],
                DeckTreasureCards = new() { [Deck] = new() { [Card] = 3 } } }, AlchemyBehavior = new() { ReagentItemIds = [], Reagents = [] } };
        internal static ClientReagentItem ReagentRow(int count) => new() { m_globalID = ReagentId, m_templateID = Reagent, m_characterId = Owner, m_quantity = count };
        internal static DropItemResult Drop(ulong id, int quantity = 1) => new() { ItemId = id.ToString(CultureInfo.InvariantCulture), ItemName = "Authored", Quantity = quantity };
        private static Account NewAccount(int crowns) {
            var account = new Account { Crowns = crowns }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, AccountId);
            account.CharacterIds.Add(Owner); return account;
        }
        private static Account CloneAccount(Account source) {
            var account = NewAccount(source.Crowns); typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, source.AccountId);
            account.CharacterIds.Clear(); account.CharacterIds.AddRange(source.CharacterIds); return account;
        }
        private static Wizard CloneWizard(Wizard source) => new() { CharId = source.CharId, AccountId = source.AccountId, Zone = source.Zone,
            GameStats = source.GameStats.CloneSnapshotWithGold(source.GameStats.m_currentGold),
            MagicSchoolBehavior = new() { Level = source.MagicSchoolBehavior.Level, MagicSchool = source.MagicSchoolBehavior.MagicSchool,
                ExperiencePoints = source.MagicSchoolBehavior.ExperiencePoints, TrainingPoints = source.MagicSchoolBehavior.TrainingPoints },
            InventoryBehavior = new() { InventoryItemIds = [..source.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..source.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [..source.StorageBehavior.BankItemIds], Items = [] },
            SpellbookBehavior = new() { LearnedSpellTemplateIds = [..source.SpellbookBehavior.LearnedSpellTemplateIds],
                TreasureCardTemplateIds = [..source.SpellbookBehavior.TreasureCardTemplateIds],
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(source.SpellbookBehavior.DeckTreasureCards) },
            AlchemyBehavior = new() { ReagentItemIds = [..source.AlchemyBehavior.ReagentItemIds], Reagents = [] } };
        private static WizClientObjectItem CloneItem(WizClientObjectItem source) => source with { m_inactiveBehaviors = source.m_inactiveBehaviors is null ? [] : [..source.m_inactiveBehaviors] };
        private static SecondChanceUseRecord? CloneUse(SecondChanceUseRecord? source) => source is null ? null : new() { CharId = source.CharId, Day = source.Day, Uses = new(source.Uses) };
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try {
                if (_scopesCaptured) {
                    WizardCollection.TestStoreScope.Value = _store; ClassicSecondChanceTransactions.TestScope.Value = _dependencies;
                    ClassicStackRewards.TestScope.Value = _stack; WizardInventoryTransactions.TestRowsScope.Value = _items; WizardReagentCollection.TestRowsScope.Value = _reagents;
                }
            } finally {
                _configuration.Restore();
            }
        }
    }

    public class ChestSession : DispatchProxy {
        internal Wizard Wizard = null!; internal Account Account = null!;
        internal List<WizClientObjectItem> Items = []; internal List<ClientReagentItem> Reagents = [];
        internal SecondChanceUseRecord? UseRecord;
        internal bool DuplicateAccount, DuplicateWizard, FailLoad;
        internal Action Save = null!, DisposeSession = null!;
        internal readonly HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance);
        private IAdvancedSessionOperations? _advanced;
        private bool _storedUse;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ChestAdvanced>(); ((ChestAdvanced)(object)_advanced).Owner = this; }
                    return _advanced;
                case "Query": Assert.False(_storedUse, "all fresh ownership/global queries must precede new use-row storage");
                    return typeof(ChestSession).GetMethod(nameof(Query), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(method.GetGenericArguments()[0]).Invoke(this, [args]);
                case "Load":
                    Assert.Equal(typeof(SecondChanceUseRecord), method.GetGenericArguments()[0]); Assert.Equal(SecondChanceUseRecord.DocumentId(Fixture.Owner), args![0]);
                    if (FailLoad) throw new InvalidOperationException("authored use document read failure"); return UseRecord;
                case "Store":
                    switch (args![0]) {
                        case SecondChanceUseRecord use: Assert.Null(UseRecord); Assert.Equal(SecondChanceUseRecord.DocumentId(Fixture.Owner), args.OfType<string>().Single()); UseRecord = use; _storedUse = true; break;
                        case ClientReagentItem row: Reagents.Add(row); break;
                        case WizClientObjectItem item: Items.Add(item); break;
                        default: throw new NotSupportedException(args[0]?.GetType().Name);
                    }
                    return null;
                case "SaveChanges": Save(); return null;
                case "Dispose": DisposeSession(); return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        private IRavenQueryable<T> Query<T>(object?[] args) {
            var collection = args.OfType<string>().Single(); IEnumerable<T> rows;
            if (typeof(T) == typeof(Wizard)) { Assert.Equal(WizardCollection.CollectionName, collection); rows = (DuplicateWizard ? new[] { Wizard, Wizard } : [Wizard]).Cast<T>(); }
            else if (typeof(T) == typeof(Account)) { Assert.Equal(AccountCollection.CollectionName, collection); rows = (DuplicateAccount ? new[] { Account, Account } : [Account]).Cast<T>(); }
            else if (typeof(T) == typeof(WizClientObjectItem)) { Assert.Equal(WizardItemCollection.CollectionName, collection); rows = Items.Cast<T>(); }
            else if (typeof(T) == typeof(ClientReagentItem)) { Assert.Equal(WizardReagentCollection.CollectionName, collection); rows = Reagents.Cast<T>(); }
            else throw new NotSupportedException(typeof(T).Name);
            var query = DispatchProxy.Create<IRavenQueryable<T>, ChestQuery<T>>(); var proxy = (ChestQuery<T>)(object)query;
            proxy.Rows = rows.AsQueryable(); proxy.Self = query; return query;
        }
    }
    public class ChestQuery<T> : DispatchProxy {
        internal IQueryable<T> Rows = null!; internal IRavenQueryable<T> Self = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "Customize" => Self, "get_Provider" => Rows.Provider, "get_Expression" => Rows.Expression,
            "get_ElementType" => typeof(T), "GetEnumerator" => Rows.GetEnumerator(), _ => throw new NotSupportedException(method.Name),
        };
    }
    public class ChestAdvanced : DispatchProxy {
        internal ChestSession Owner = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, ChestMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "GetMetadataFor" => _metadata,
            "IgnoreChangesFor" => Ignore(args![0]!), _ => throw new NotSupportedException(method.Name),
        };
        private object? Ignore(object item) { Assert.True(Owner.Ignored.Add(item)); return null; }
    }
    public class ChestMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Contains(args[1], new[] { SecondChanceUseRecord.CollectionName, WizardItemCollection.CollectionName, WizardReagentCollection.CollectionName }); return null;
        }
    }
}
