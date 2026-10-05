/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * ECONOMY AND STATE FIXES (prod-exploits C1, M1, M2, M4, M5, M6, H1, H4, L5, L7, L11)
 * ========================================================================
 *
 * PURPOSE:
 * Races between two sessions (two live copies of one account or wizard) and
 * between actors of one session, against a fake RavenDB that saves what each
 * session loaded: Crowns and gold spends, training points, the backpack, XP,
 * the deck Treasure Card ledger, one session per account, potions in a duel
 * and the walk-away (rejoin expiry) flee penalty.
 *
 * USAGE EXAMPLE:
 * dotnet test tests/Imlight.Classic.Tests --filter EconomyFixesTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.Classic.Collections;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class EconomyFixesTests {

    private const ulong Char = 4242, AccountId = 777;
    private const uint Fire = 11, Ice = 22;
    private const ulong DeckId = 9001;

    public EconomyFixesTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}economy-fixes-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- Crowns (M2, the save-model split)

    [Fact]
    public async Task TwoSessionsCannotSpendOneCrownsBalanceTwice() {
        for (var round = 0; round < 25; round++) {
            var store = new AccountStore(100);
            var first = store.Live();
            var second = store.Live(); // the second session's own copy, also showing 100
            using var start = new ManualResetEventSlim();
            Task<bool> Spend(Account account) => Task.Factory.StartNew(() => {
                start.Wait();
                return ClassicCrowns.TrySpend(account, 60, store.Open, store.Load);
            }, TaskCreationOptions.LongRunning);

            var a = Spend(first);
            var b = Spend(second);
            start.Set();
            var results = await Task.WhenAll(a, b);

            Assert.Single(results, ok => ok);
            Assert.Equal(40, store.Saved.Crowns);
            Assert.Equal(40, (results[0] ? first : second).Crowns);
        }
    }

    [Fact]
    public void AStaleSessionsCreditNoLongerRollsBackASpend() {
        // C1 exploit 1: A buys a 50,000-Crown mount; B (still showing 100,000) earns 1 Crown from a mob.
        var store = new AccountStore(100_000);
        var a = store.Live();
        var b = store.Live();
        Assert.True(ClassicCrowns.TrySpend(a, 50_000, store.Open, store.Load));
        ClassicCrowns.Add(b, 1, store.Open, store.Load);

        Assert.Equal(50_001, store.Saved.Crowns);
        Assert.Equal(50_001, b.Crowns);
    }

    [Fact]
    public void ASpendTheBalanceCannotCoverChangesNothing() {
        var store = new AccountStore(30);
        var live = store.Live();
        Assert.Equal(30, ClassicCrowns.Add(live, -50, store.Open, store.Load)); // no clamping at zero
        Assert.False(ClassicCrowns.TrySpend(live, 31, store.Open, store.Load));
        Assert.Equal(30, store.Saved.Crowns);
        Assert.True(ClassicCrowns.TrySpend(live, 30, store.Open, store.Load));
        Assert.Equal(0, store.Saved.Crowns);
        Assert.False(ClassicCrowns.TrySpend(live, -1, store.Open, store.Load));
    }

    [Fact]
    public void ManyRacingCrownsSpendsNeverGoBelowZero() {
        var store = new AccountStore(100);
        var sessions = Enumerable.Range(0, 4).Select(_ => store.Live()).ToArray();
        var spent = 0;
        Parallel.For(0, 4, s => {
            for (var i = 0; i < 20; i++) {
                if (ClassicCrowns.TrySpend(sessions[s], 7, store.Open, store.Load)) Interlocked.Increment(ref spent);
            }
        });

        Assert.Equal(14, spent);
        Assert.Equal(2, store.Saved.Crowns);
    }

    [Fact]
    public void StartingCrownsAreGivenOnceEvenFromTwoSessionsAtOnce() {
        var store = new AccountStore(0);
        var a = store.Live();
        var b = store.Live();
        var given = 0;
        Parallel.Invoke(
            () => { if (ClassicCrowns.EnsureStartingCrowns(a, store.Open, store.Load)) Interlocked.Increment(ref given); },
            () => { if (ClassicCrowns.EnsureStartingCrowns(b, store.Open, store.Load)) Interlocked.Increment(ref given); });

        Assert.Equal(1, given);
        Assert.Equal(ClassicCrowns.StartingCrowns, store.Saved.Crowns);
        Assert.Equal(ClassicCrowns.StartingCrowns, store.Saved.StartingCrownsGiven);
    }

    // ---------------------------------------------------------------- gold (M1)

    [Fact]
    public async Task TwoSessionsRacingPurchasesCannotBothSpendTheGold() {
        for (var round = 0; round < 25; round++) {
            var store = new WizardStore();
            var live = store.Live();
            var stale = store.Live(); // a second session's copy, or another actor's stale read
            using var start = new ManualResetEventSlim();
            Task<bool> Buy(Wizard wizard) => Task.Factory.StartNew(() => {
                start.Wait();
                return WizardCollection.TrySpendGold(wizard, 600, store.Open, store.Load);
            }, TaskCreationOptions.LongRunning);

            var a = Buy(live);
            var b = Buy(stale);
            start.Set();
            var results = await Task.WhenAll(a, b);

            Assert.Single(results, ok => ok);
            Assert.Equal(400, store.Saved.GameStats.m_currentGold);
        }
    }

    [Fact]
    public void GoldNeverGoesBelowZero() {
        var store = new WizardStore();
        var live = store.Live();
        Assert.False(WizardCollection.ChangeGold(live, -1001, capToPouch: false, store.Open, store.Load));
        Assert.Equal(1000, store.Saved.GameStats.m_currentGold);
        var spent = 0;
        Parallel.For(0, 8, _ => {
            for (var i = 0; i < 10; i++) {
                if (WizardCollection.TrySpendGold(live, 30, store.Open, store.Load)) Interlocked.Increment(ref spent);
            }
        });

        Assert.Equal(33, spent);
        Assert.Equal(10, store.Saved.GameStats.m_currentGold);
        Assert.Equal(10, live.GameStats.m_currentGold);
        Assert.False(WizardCollection.TrySpendGold(live, -5, store.Open, store.Load));
    }

    // ---------------------------------------------------------------- training points (L11)

    [Fact]
    public void LootAndTrainingOnDifferentActorsNoLongerOverwriteEachOthersTrainingPoints() {
        var store = new WizardStore { TrainingPoints = 5 };
        var live = store.Live();
        var spent = 0;
        Parallel.Invoke(
            () => { for (var i = 0; i < 50; i++) WizardCollection.ChangeTrainingPoints(live, +1, store.Open, store.Load); },
            () => {
                for (var i = 0; i < 50; i++) {
                    if (WizardCollection.ChangeTrainingPoints(live, -1, store.Open, store.Load)) Interlocked.Increment(ref spent);
                }
            });

        Assert.Equal(5 + 50 - spent, store.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(store.Saved.MagicSchoolBehavior.TrainingPoints, live.MagicSchoolBehavior.TrainingPoints);
        Assert.True(store.Saved.MagicSchoolBehavior.TrainingPoints >= 0);
        Assert.False(WizardCollection.ChangeTrainingPoints(live, -1000, store.Open, store.Load));
    }

    // ---------------------------------------------------------------- the backpack and quest lists (one live copy)

    [Fact]
    public void OneLiveCopyWithSeveralActorsSavingKeepsEveryItem() {
        var store = new WizardStore();
        var live = store.Live();
        using (store.Scope()) {
            Parallel.For(0, 4, actor => {
                for (var i = 0; i < 4; i++) { // 16 items: within any backpack size
                    var item = new WizClientObjectItem { m_globalID = (ulong) (actor * 1000 + i + 1) };
                    Assert.True(live.InventoryBehavior.AddItem(item));
                    WizardCollection.UpdateCharacterItems(live);
                }
            });
        }

        Assert.Equal(16, store.Saved.InventoryBehavior.InventoryItemIds.Count);
        Assert.Equal(live.InventoryBehavior.InventoryItemIds.OrderBy(id => id), store.Saved.InventoryBehavior.InventoryItemIds.OrderBy(id => id));
    }

    [Fact]
    public void TwoLiveCopiesLoseItemsWhichIsWhyAnAccountHasOneSession() {
        // The premise of C1: the older session's save of its own copy writes the newer session's backpack away.
        var store = new WizardStore();
        var newer = store.Live();
        var older = store.Live();
        using (store.Scope()) {
            Assert.True(newer.InventoryBehavior.AddItem(new WizClientObjectItem { m_globalID = 1 }));
            WizardCollection.UpdateCharacterItems(newer);
            WizardCollection.UpdateCharacterItems(older);
        }

        Assert.Empty(store.Saved.InventoryBehavior.InventoryItemIds);
    }

    [Fact]
    public void TwoActorsSpendingOneItemOnlyOneSucceeds() {
        // H1/L7: the removal is the check (Bazaar sale vs. bank move or vendor sale of the same item).
        for (var round = 0; round < 25; round++) {
            var store = new WizardStore();
            var live = store.Live();
            using (store.Scope()) {
                Assert.True(live.InventoryBehavior.AddItem(new WizClientObjectItem { m_globalID = 55, m_characterId = (Imcodec.Types.GID) Char }));
                var wins = 0;
                Parallel.For(0, 2, _ => { if (live.DestroyInventoryItem(55)) Interlocked.Increment(ref wins); });
                Assert.Equal(1, wins);
            }

            Assert.Empty(store.Saved.InventoryBehavior.InventoryItemIds);
        }
    }

    // ---------------------------------------------------------------- the deck Treasure Card ledger (M5, M6, C2 refunds)

    [Fact]
    public void ATreasureCardOfAKnownSpellStaysATreasureCardAndOnlyItComesBack() {
        var store = new WizardStore();
        store.Saved.SpellbookBehavior.LearnedSpellTemplateIds = [Fire];
        store.Saved.SpellbookBehavior.TreasureCardTemplateIds = [Fire];
        var live = store.Live();

        Assert.True(WizardCollection.MoveTreasureCardToDeck(live, DeckId, Fire, 2, store.Open, store.Load));
        Assert.Empty(store.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(1, store.Saved.SpellbookBehavior.DeckTreasureCount(DeckId, Fire));
        Assert.Equal(1, live.SpellbookBehavior.DeckTreasureCount(DeckId, Fire));
        Assert.False(WizardCollection.MoveTreasureCardToDeck(live, DeckId, Fire, 2, store.Open, store.Load)); // book empty

        Assert.True(WizardCollection.MoveTreasureCardFromDeck(live, DeckId, Fire, destroy: false, store.Open, store.Load));
        Assert.Equal([Fire], store.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        // A regular deck card (or anything the ledger does not hold) never comes out as a book Treasure Card.
        Assert.False(WizardCollection.MoveTreasureCardFromDeck(live, DeckId, Fire, destroy: false, store.Open, store.Load));
        Assert.False(WizardCollection.MoveTreasureCardFromDeck(live, DeckId, Ice, destroy: false, store.Open, store.Load));
        Assert.Equal([Fire], store.Saved.SpellbookBehavior.TreasureCardTemplateIds);
    }

    [Fact]
    public void TheDecksTreasureCardPlacesAreRespectedAndACastCardIsSpent() {
        var store = new WizardStore();
        store.Saved.SpellbookBehavior.TreasureCardTemplateIds = [Fire, Fire, Ice];
        var live = store.Live();
        Assert.True(WizardCollection.MoveTreasureCardToDeck(live, DeckId, Fire, 2, store.Open, store.Load));
        Assert.True(WizardCollection.MoveTreasureCardToDeck(live, DeckId, Ice, 2, store.Open, store.Load));
        Assert.False(WizardCollection.MoveTreasureCardToDeck(live, DeckId, Fire, 2, store.Open, store.Load));
        Assert.Equal(2, store.Saved.SpellbookBehavior.DeckTreasureTotal(DeckId));

        Assert.True(WizardCollection.MoveTreasureCardFromDeck(live, DeckId, Ice, destroy: true, store.Open, store.Load));
        Assert.Equal([Fire], store.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(0, store.Saved.SpellbookBehavior.DeckTreasureCount(DeckId, Ice));
        Assert.Equal(1, store.Saved.SpellbookBehavior.DeckTreasureTotal(DeckId));
    }

    [Fact]
    public void RacingDeckMovesOfOneCardMoveItOnce() {
        for (var round = 0; round < 25; round++) {
            var store = new WizardStore();
            store.Saved.SpellbookBehavior.TreasureCardTemplateIds = [Fire];
            var a = store.Live();
            var b = store.Live();
            var moved = 0;
            Parallel.Invoke(
                () => { if (WizardCollection.MoveTreasureCardToDeck(a, DeckId, Fire, 5, store.Open, store.Load)) Interlocked.Increment(ref moved); },
                () => { if (WizardCollection.MoveTreasureCardToDeck(b, DeckId + 1, Fire, 5, store.Open, store.Load)) Interlocked.Increment(ref moved); });
            Assert.Equal(1, moved);
            Assert.Empty(store.Saved.SpellbookBehavior.TreasureCardTemplateIds);
            Assert.Equal(1, store.Saved.SpellbookBehavior.DeckTreasureTotal(DeckId) + store.Saved.SpellbookBehavior.DeckTreasureTotal(DeckId + 1));

            var back = 0;
            var deck = store.Saved.SpellbookBehavior.DeckTreasureTotal(DeckId) == 1 ? DeckId : DeckId + 1;
            Parallel.Invoke(
                () => { if (WizardCollection.MoveTreasureCardFromDeck(a, deck, Fire, false, store.Open, store.Load)) Interlocked.Increment(ref back); },
                () => { if (WizardCollection.MoveTreasureCardFromDeck(b, deck, Fire, false, store.Open, store.Load)) Interlocked.Increment(ref back); });
            Assert.Equal(1, back);
            Assert.Equal([Fire], store.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        }
    }

    [Fact]
    public void AnotherWizardsDeckEntriesAreNotCastableAndTheLedgerMigratesOnce() {
        // M5: an entry the wizard has not learned is left out of the combat deck (it used to become a Treasure Card).
        var learned = new List<uint> { Fire };
        Assert.True(Wizard.IsLegacyDeckTreasure(new SpellData { m_templateID = Ice, m_quantity = 1 }, learned, NoTemplate));
        Assert.False(Wizard.IsLegacyDeckTreasure(new SpellData { m_templateID = Fire, m_quantity = 1 }, learned, NoTemplate));
        Assert.True(Wizard.IsLegacyDeckTreasure(new SpellData { m_templateID = Fire, m_quantity = 1, m_enchantment = 5 }, learned, NoTemplate));
        Assert.False(Wizard.IsLegacyDeckTreasure(new SpellData { m_templateID = Ice, m_quantity = 1 }, new List<uint>(), NoTemplate));

        var store = new WizardStore();
        var live = store.Live();
        var found = new Dictionary<ulong, Dictionary<uint, int>> { [DeckId] = new() { [Ice] = 2 } };
        Assert.True(WizardCollection.RecordMigratedDeckTreasureCards(live, found, store.Open, store.Load));
        Assert.False(WizardCollection.RecordMigratedDeckTreasureCards(live, found, store.Open, store.Load));
        Assert.Equal(2, store.Saved.SpellbookBehavior.DeckTreasureCount(DeckId, Ice));
        Assert.Equal(1, live.SpellbookBehavior.DeckTreasureLedgerVersion);
    }

    // ---------------------------------------------------------------- one session per account (C1)

    [Fact]
    public void ANewLoginClosesTheOldSessionAndWaitsForItToStop() {
        var (closer, wait) = (AccountSessions.Closer, AccountSessions.StopWait);
        try {
            var closed = new ConcurrentQueue<object>();
            AccountSessions.Closer = session => {
                closed.Enqueue(session);
                Task.Run(async () => { await Task.Delay(150); AccountSessions.Release(session); });
            };
            AccountSessions.StopWait = TimeSpan.FromSeconds(5);
            const ulong account = 90_001;
            var first = new object();
            var second = new object();

            Assert.Equal(AccountClaim.Admitted, AccountSessions.Claim(account, first, DateTime.UtcNow));
            Assert.Equal(AccountClaim.Admitted, AccountSessions.Claim(account, first, DateTime.UtcNow)); // same session again
            var loaded = DateTime.UtcNow;
            Assert.Equal(AccountClaim.AdmittedReload, AccountSessions.Claim(account, second, loaded));
            Assert.Equal([first], closed);
            Assert.Same(second, AccountSessions.HolderOf(account));

            AccountSessions.Release(second);
            Assert.Null(AccountSessions.HolderOf(account));
            Thread.Sleep(5);
            Assert.Equal(AccountClaim.Admitted, AccountSessions.Claim(account, new object(), DateTime.UtcNow)); // stopped before this load
        }
        finally {
            (AccountSessions.Closer, AccountSessions.StopWait) = (closer, wait);
        }
    }

    [Fact]
    public void ANewLoginIsRefusedWhenTheOldSessionWillNotStop() {
        var (closer, wait) = (AccountSessions.Closer, AccountSessions.StopWait);
        try {
            AccountSessions.Closer = _ => { };
            AccountSessions.StopWait = TimeSpan.FromMilliseconds(200);
            const ulong account = 90_002;
            var stuck = new object();
            Assert.Equal(AccountClaim.Admitted, AccountSessions.Claim(account, stuck, DateTime.UtcNow));
            Assert.Equal(AccountClaim.Refused, AccountSessions.Claim(account, new object(), DateTime.UtcNow));
            Assert.Same(stuck, AccountSessions.HolderOf(account));
            Assert.False(AccountSessions.CloseAndWait(account));
            AccountSessions.Release(stuck);
            Assert.True(AccountSessions.CloseAndWait(account));
        }
        finally {
            (AccountSessions.Closer, AccountSessions.StopWait) = (closer, wait);
        }
    }

    [Fact]
    public void TwoLoginsAtOnceEndWithOneHolder() {
        var (closer, wait) = (AccountSessions.Closer, AccountSessions.StopWait);
        try {
            AccountSessions.Closer = session => Task.Run(() => AccountSessions.Release(session));
            AccountSessions.StopWait = TimeSpan.FromSeconds(5);
            const ulong account = 90_003;
            var sessions = Enumerable.Range(0, 6).Select(_ => new object()).ToArray();
            var results = new AccountClaim[sessions.Length];
            Parallel.For(0, sessions.Length, i => results[i] = AccountSessions.Claim(account, sessions[i], DateTime.UtcNow));

            Assert.DoesNotContain(AccountClaim.Refused, results);
            Assert.Contains(AccountSessions.HolderOf(account), sessions);
        }
        finally {
            (AccountSessions.Closer, AccountSessions.StopWait) = (closer, wait);
        }
    }

    // ---------------------------------------------------------------- potions in a duel (H4), walking away (M4)

    [Fact]
    public void NoPotionInADuelOrWhileASeatIsHeld() {
        var wizard = new Wizard { CharId = 81_001 };
        Assert.True(PotionService.MayDrinkNow(wizard, DateTime.UtcNow));
        wizard.IsInDuel = true;
        Assert.False(PotionService.MayDrinkNow(wizard, DateTime.UtcNow));
        wizard.IsInDuel = false;
        ActiveDuels.Hold(new HeldSeat(wizard.CharId, "WizardCity/WC_Streets/WC_Unicorn", 0, DateTime.UtcNow.AddMinutes(1)));
        try {
            Assert.False(PotionService.MayDrinkNow(wizard, DateTime.UtcNow));
        }
        finally {
            ActiveDuels.Release(wizard.CharId);
        }

        Assert.True(PotionService.MayDrinkNow(wizard, DateTime.UtcNow));
    }

    [Fact]
    public void ASeatThatRunsOutCostsTheManaAFleeWould() {
        var store = new WizardStore();
        var held = store.Live();
        held.GameStats.m_currentMana = 40;
        using (store.Scope()) {
            Assert.Same(held, CombatDuelComponent.ApplyWalkAwayPenalty(held));
        }

        Assert.Equal(0, held.GameStats.m_currentMana);
        Assert.Equal(0, store.Saved.GameStats.m_currentMana);
        Assert.Equal(1000, store.Saved.GameStats.m_currentGold); // gold is never written from the live copy
    }

    // ---------------------------------------------------------------- fakes

    private static SpellTemplate? NoTemplate(uint id) => null;

    /// <summary>One saved wizard behind a fake RavenDB session that saves what it loaded (a deep copy).</summary>
    private sealed class WizardStore {

        internal Wizard Saved;
        internal int TrainingPoints { set => Saved.MagicSchoolBehavior.TrainingPoints = value; }
        private readonly object _gate = new();

        internal WizardStore() => Saved = Make();

        internal Wizard Live() {
            lock (_gate) {
                return Clone(Saved);
            }
        }

        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, Load);
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }

        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, TreasureTradeTests.MultiSessionProxy>();
            var proxy = (TreasureTradeTests.MultiSessionProxy) (object) session;
            proxy.Commit = working => {
                lock (_gate) {
                    foreach (var (_, wizard) in working) {
                        Saved = Clone(wizard);
                    }
                }
            };

            return session;
        }

        internal Wizard Load(IDocumentSession session, ulong id) {
            Assert.Equal(Char, id);
            Assert.True(WizardCollection.HoldsWriteLane);
            var proxy = (TreasureTradeTests.MultiSessionProxy) (object) session;
            Wizard working;
            lock (_gate) {
                working = Clone(Saved);
            }

            proxy.Working[id] = working;

            return working;
        }

        private static Wizard Make() => new() {
            CharId = Char,
            GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 1000, m_baseGoldPouch = 2000, m_currentMana = 10 },
            SpellbookBehavior = new ServerWizSpellbookBehavior { TreasureCardTemplateIds = [], LearnedSpellTemplateIds = [] },
            MagicSchoolBehavior = new ServerMagicSchoolBehavior(),
            InventoryBehavior = new ServerWizInventoryBehavior { InventoryItemIds = [], Items = new CopyOnWriteList<WizClientObjectItem>() },
        };

        // What a save writes: the documents' fields, copied when SaveChanges runs.
        internal static Wizard Clone(Wizard wizard) => new() {
            CharId = wizard.CharId,
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            SpellbookBehavior = new ServerWizSpellbookBehavior {
                LearnedSpellTemplateIds = [.. wizard.SpellbookBehavior.LearnedSpellTemplateIds],
                TreasureCardTemplateIds = [.. wizard.SpellbookBehavior.TreasureCardTemplateIds],
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(wizard.SpellbookBehavior.DeckTreasureCards),
                DeckTreasureLedgerVersion = wizard.SpellbookBehavior.DeckTreasureLedgerVersion,
            },
            MagicSchoolBehavior = JsonConvert.DeserializeObject<ServerMagicSchoolBehavior>(JsonConvert.SerializeObject(wizard.MagicSchoolBehavior))!,
            InventoryBehavior = new ServerWizInventoryBehavior {
                InventoryItemIds = [.. wizard.InventoryBehavior.InventoryItemIds],
                Items = new CopyOnWriteList<WizClientObjectItem>(),
            },
        };

    }

    /// <summary>One saved account behind a fake session.</summary>
    private sealed class AccountStore {

        internal Account Saved;
        private readonly object _gate = new();

        internal AccountStore(int crowns) {
            Saved = Make();
            Saved.Crowns = crowns;
            Saved.StartingCrownsGiven = crowns == 0 ? 0 : ClassicCrowns.StartingCrowns;
        }

        internal Account Live() {
            lock (_gate) {
                return Copy(Saved);
            }
        }

        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, AccountSessionProxy>();
            var proxy = (AccountSessionProxy) (object) session;
            proxy.Commit = working => {
                lock (_gate) {
                    Saved = Copy(working);
                }
            };

            return session;
        }

        internal Account Load(IDocumentSession session, ulong id) {
            Assert.Equal(AccountId, id);
            var proxy = (AccountSessionProxy) (object) session;
            lock (_gate) {
                proxy.Working = Copy(Saved);
            }

            return proxy.Working;
        }

        private static Account Make() {
            var account = new Account();
            typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, AccountId);

            return account;
        }

        private static Account Copy(Account account) {
            var copy = Make();
            copy.Crowns = account.Crowns;
            copy.StartingCrownsGiven = account.StartingCrownsGiven;

            return copy;
        }

    }

    public class AccountSessionProxy : DispatchProxy {

        internal Account Working = null!;
        internal Action<Account> Commit = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, MonstrologyConcurrencyTests.AdvancedProxy>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method!.Name switch {
                "get_Advanced" => _advanced,
                "SaveChanges" => Save(),
                "Dispose" => null,
                _ => throw new NotSupportedException(method.Name),
            };

        private object? Save() {
            Commit(Working);

            return null;
        }

    }

    private sealed class Restore(Action restore) : IDisposable {
        public void Dispose() => restore();
    }

}
