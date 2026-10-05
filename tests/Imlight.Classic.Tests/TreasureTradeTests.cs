using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.Math;
using Imlight.Common;
using Imlight.CoreLib.Game.Trading;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// CLASSIC: treasure card trading between friends (2009), against the r806919 client's trade protocol.
/// </summary>
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class TreasureTradeTests {

    private const ulong A = 1000, B = 2000, C = 3000;
    private const uint Fire = 11, Ice = 22, Storm = 33, Myth = 44, Life = 55; // spell ids = templates in the fake

    public TreasureTradeTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}treasure-trade-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    private static ulong Gid(ulong charId) => Wizard.GetGameObjectId(charId);

    [Fact]
    public void FriendsTradeCardsAndBothBooksSwapAtOnce() {
        var w = new World();
        w.Book(A, Fire, Fire, Ice);
        w.Book(B, Storm);
        var m = w.Manager;

        m.Create(A, Gid(B));
        var request = Assert.IsType<WIZARD_12_PROTOCOL.MSG_TRADE_REQUEST>(w.Last(B));
        Assert.Equal(Gid(A), request.TargetGID);

        m.Join(B, Gid(A), 1);
        var joinA = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS>(A).Last();
        Assert.Equal((Gid(B), 1, 1), (joinA.TargetGID, joinA.PlayerStatus, joinA.TargetStatus));
        var joinB = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS>(B).Last();
        Assert.Equal((Gid(A), 1, 1), (joinB.TargetGID, joinB.PlayerStatus, joinB.TargetStatus));

        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        var relayed = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM>(B).Last();
        Assert.Equal((Gid(A), Gid(A), Fire, 1), (relayed.TargetGID, relayed.ChangeGID, relayed.ItemTemplate, (int) relayed.Action));
        m.ChangeItem(B, Gid(A), Storm, 0, 1);

        m.Ready(A, Gid(B), 1);
        m.Ready(B, Gid(A), 1);
        m.Ready(A, Gid(B), 2);
        Assert.Equal(0, w.Commits);
        m.Ready(B, Gid(A), 2);

        Assert.Equal(1, w.Commits);
        var resultA = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single();
        Assert.Equal((Gid(B), 0u, 1u, 2u), (resultA.TargetGID, resultA.Status, resultA.ItemsGained, resultA.ItemsLost));
        var resultB = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(B).Single();
        Assert.Equal((Gid(A), 0u, 2u, 1u), (resultB.TargetGID, resultB.Status, resultB.ItemsGained, resultB.ItemsLost));

        Assert.Equal(new uint[] { Ice, Storm }, w.Saved(A).OrderBy(x => x));
        Assert.Equal(new uint[] { Fire, Fire }, w.Saved(B).OrderBy(x => x));
        Assert.Equal(w.Saved(A).OrderBy(x => x), w.Live[A].SpellbookBehavior.TreasureCardTemplateIds.OrderBy(x => x));
        Assert.Equal(w.Saved(B).OrderBy(x => x), w.Live[B].SpellbookBehavior.TreasureCardTemplateIds.OrderBy(x => x));
        Assert.False(m.IsTrading(A));
        Assert.False(m.IsTrading(B));
    }

    [Fact]
    public void OnlyFriendsInTheSameZoneAndStandingCloseMayTrade() {
        var w = new World();
        w.Friends.Clear();
        w.Manager.Create(A, Gid(B));
        Assert.Equal(TradeStatus.CannotTrade, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);
        Assert.Empty(w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_REQUEST>(B));

        w = new World();
        w.Zones[B] = "WizardCity/WC_Hub";
        w.Manager.Create(A, Gid(B));
        Assert.Equal(TradeStatus.NotInZone, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);

        w = new World();
        w.Instances[B] = 77;
        w.Manager.Create(A, Gid(B));
        Assert.Equal(TradeStatus.NotInZone, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);

        // Too far when the friend answers: both see the refusal and nothing is open.
        w = new World();
        w.Manager.Create(A, Gid(B));
        w.Live[B].Location = new Vector3(TreasureTradeManager.MaxDistance + 50, 0, 0);
        w.Manager.Join(B, Gid(A), 1);
        Assert.Equal(TradeStatus.CannotTrade, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);
        Assert.Equal(TradeStatus.CannotTrade, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(B).Single().Status);
        Assert.False(w.Manager.IsTrading(A));

        // Treasure cards switched off by the profile.
        w = new World { Enabled = false };
        w.Manager.Create(A, Gid(B));
        Assert.Equal(TradeStatus.CannotTrade, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);
    }

    [Fact]
    public void AFriendAlreadyTradingIsBusy() {
        var w = new World();
        w.Friends.Add((C, B));
        w.Manager.Create(A, Gid(B));
        w.Manager.Create(C, Gid(B));
        Assert.Equal(TradeStatus.AlreadyTrading, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(C).Single().Status);
        Assert.Single(w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_REQUEST>(B));
    }

    [Fact]
    public void CardsNotOwnedEnchantedOrBeyondFourKindsFailTheTrade() {
        // More copies than the book holds.
        var w = Opened(out var m);
        w.Book(A, Fire);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(B).Single().Status);
        Assert.False(m.IsTrading(A));

        // An enchanted card (none existed in 2009).
        w = Opened(out m);
        w.Book(A, Fire);
        m.ChangeItem(A, Gid(B), Fire, 7, 1);
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);

        // A fifth kind of card.
        w = Opened(out m);
        w.Book(A, Fire, Ice, Storm, Myth, Life);
        foreach (var card in new[] { Fire, Ice, Storm, Myth }) {
            m.ChangeItem(A, Gid(B), card, 0, 1);
        }

        Assert.True(m.IsTrading(A));
        m.ChangeItem(A, Gid(B), Life, 0, 1);
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);

        // Not a treasure card at all.
        w = Opened(out m);
        m.ChangeItem(A, Gid(B), 999, 0, 1);
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);
        Assert.Equal(0, w.Commits);
    }

    [Fact]
    public void ChangingTheOfferClearsBothReadyBoxes() {
        var w = Opened(out var m);
        w.Book(A, Fire, Ice);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.Ready(A, Gid(B), 1);
        m.Ready(B, Gid(A), 1);
        m.ChangeItem(A, Gid(B), Ice, 0, 1);
        var readyB = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS>(B).Last();
        Assert.Equal((0, 0), (readyB.PlayerStatus, readyB.TargetStatus));

        // "Trade" before the partner is ready counts only as ready.
        m.Ready(B, Gid(A), 2);
        Assert.Equal(1, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS>(A).Last().TargetStatus);
        m.Ready(A, Gid(B), 2);
        Assert.Equal(0, w.Commits);

        // Removing a card relays the removal.
        m.ChangeItem(A, Gid(B), Ice, 0, 0);
        Assert.Equal(0, (int) w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM>(B).Last().Action);
    }

    [Fact]
    public void CancelDeclineAndLogoutTellThePartner() {
        // The target declines: the requester's window shows "has cancelled".
        var w = new World();
        w.Manager.Create(A, Gid(B));
        w.Manager.Join(B, Gid(A), 0);
        var j = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS>(A).Single();
        Assert.Equal((Gid(B), 1, 0), (j.TargetGID, j.PlayerStatus, j.TargetStatus));
        Assert.False(w.Manager.IsTrading(A));

        // The requester gives up first: the request window closes (TargetStatus 0).
        w = new World();
        w.Manager.Create(A, Gid(B));
        w.Manager.Join(A, Gid(B), 0);
        j = w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS>(B).Single();
        Assert.Equal((Gid(A), 0), (j.TargetGID, j.TargetStatus));

        // A logout mid-trade cancels it; a late "Trade" from the partner does nothing.
        w = Opened(out var m);
        w.Book(A, Fire);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.Leave(A);
        Assert.Equal(0, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS>(B).Last().TargetStatus);
        m.Ready(B, Gid(A), 2);
        Assert.Equal(0, w.Commits);
        Assert.Equal(new uint[] { Fire }, w.Saved(A));

        // Gold cannot be traded.
        w = Opened(out m);
        m.ChangeMoney(A, Gid(B));
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(B).Single().Status);
    }

    [Fact]
    public void ACardSpentAfterTheOfferFailsTheWholeTrade() {
        var w = Opened(out var m);
        w.Book(A, Fire);
        w.Book(B, Ice);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.ChangeItem(B, Gid(A), Ice, 0, 1);
        w.SavedBooks[A].Clear(); // deleted or put in a deck in another session after it was offered
        m.Ready(A, Gid(B), 1); m.Ready(B, Gid(A), 1); m.Ready(A, Gid(B), 2); m.Ready(B, Gid(A), 2);
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);
        Assert.Equal(new uint[] { Ice }, w.Saved(B));
        Assert.Equal(0, w.Saves);
    }

    [Fact]
    public void AFailedSaveChangesNeitherBook() {
        var w = Opened(out var m);
        w.Book(A, Fire);
        w.Book(B, Ice);
        w.FailSave = true;
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.ChangeItem(B, Gid(A), Ice, 0, 1);
        m.Ready(A, Gid(B), 1); m.Ready(B, Gid(A), 1); m.Ready(A, Gid(B), 2); m.Ready(B, Gid(A), 2);
        Assert.Equal(TradeStatus.Failed, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(B).Single().Status);
        Assert.Equal(new uint[] { Fire }, w.Saved(A));
        Assert.Equal(new uint[] { Fire }, w.Live[A].SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(new uint[] { Ice }, w.Live[B].SpellbookBehavior.TreasureCardTemplateIds);
    }

    [Fact]
    public async Task BothConfirmingAtOnceSwapsExactlyOnce() {
        for (var round = 0; round < 200; round++) {
            var w = Opened(out var m);
            w.Book(A, Fire);
            w.Book(B, Ice);
            m.ChangeItem(A, Gid(B), Fire, 0, 1);
            m.ChangeItem(B, Gid(A), Ice, 0, 1);
            m.Ready(A, Gid(B), 1);
            m.Ready(B, Gid(A), 1);
            using var start = new ManualResetEventSlim();
            var token = TestContext.Current.CancellationToken;
            var t1 = Task.Run(() => { start.Wait(token); m.Ready(A, Gid(B), 2); m.Ready(A, Gid(B), 2); }, token);
            var t2 = Task.Run(() => { start.Wait(token); m.Ready(B, Gid(A), 2); m.Ready(B, Gid(A), 2); }, token);
            start.Set();
            await Task.WhenAll(t1, t2);
            Assert.Equal(1, w.Commits);
            Assert.Equal(new uint[] { Ice }, w.Saved(A));
            Assert.Equal(new uint[] { Fire }, w.Saved(B));
        }
    }

    [Fact]
    public async Task CancelRacingConfirmEitherTradesWholeOrNotAtAll() {
        for (var round = 0; round < 200; round++) {
            var w = Opened(out var m);
            w.Book(A, Fire);
            w.Book(B, Ice);
            m.ChangeItem(A, Gid(B), Fire, 0, 1);
            m.ChangeItem(B, Gid(A), Ice, 0, 1);
            m.Ready(A, Gid(B), 1);
            m.Ready(B, Gid(A), 1);
            m.Ready(A, Gid(B), 2);
            using var start = new ManualResetEventSlim();
            var token = TestContext.Current.CancellationToken;
            var confirm = Task.Run(() => { start.Wait(token); m.Ready(B, Gid(A), 2); }, token);
            var cancel = Task.Run(() => { start.Wait(token); if (round % 2 == 0) m.Join(A, Gid(B), 0); else m.Leave(A); }, token);
            start.Set();
            await Task.WhenAll(confirm, cancel);
            var all = w.Saved(A).Concat(w.Saved(B)).OrderBy(x => x).ToArray();
            Assert.Equal(new uint[] { Fire, Ice }, all); // never duplicated, never lost
            if (w.Commits == 1) {
                Assert.Equal(new uint[] { Ice }, w.Saved(A));
            }
            else {
                Assert.Equal(new uint[] { Fire }, w.Saved(A));
            }

            Assert.False(m.IsTrading(A));
            Assert.False(m.IsTrading(B));
        }
    }

    [Fact]
    public async Task TwoTradesOverCrossingLanesNeverDeadlock() {
        // Characters whose write lanes cross (lane = id & 255): trades A<->B and B'<->A' in parallel, in opposite order.
        var token = TestContext.Current.CancellationToken;
        for (var round = 0; round < 50; round++) {
            var store = new Store();
            var a = store.Add(5, Fire);
            var b = store.Add(9, Ice);
            var c = store.Add(9 + 256, Storm);
            var d = store.Add(5 + 256, Myth);
            var t1 = Task.Run(() => WizardCollection.CommitTreasureCardTrade(a, [Fire], b, [Ice], store.Open, store.Load), token);
            var t2 = Task.Run(() => WizardCollection.CommitTreasureCardTrade(c, [Storm], d, [Myth], store.Open, store.Load), token);
            var done = await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.All(done, Assert.True);
            Assert.Equal(new uint[] { Ice }, store.Saved[5]);
            Assert.Equal(new uint[] { Storm }, store.Saved[5 + 256]);
        }

        // Two characters on one lane trade too.
        var same = new Store();
        var x = same.Add(7, Fire);
        var y = same.Add(7 + 256, Ice);
        Assert.True(WizardCollection.CommitTreasureCardTrade(x, [Fire], y, [Ice], same.Open, same.Load));
        Assert.Equal(new uint[] { Ice }, same.Saved[7]);
        Assert.False(WizardCollection.HoldsWriteLane);
    }

    [Fact]
    public async Task ATradeBeingSavedHoldsUpNoOtherTradeAndCannotBeCancelledHalfway() {
        // L8: the save used to run inside the one lock every trade on the server shares.
        var w = Opened(out var m);
        w.Friends.Add((C, A));
        w.Book(A, Fire);
        w.Book(B, Storm);
        m.ChangeItem(A, Gid(B), Fire, 0, 1);
        m.ChangeItem(B, Gid(A), Storm, 0, 1);
        m.Ready(A, Gid(B), 1);
        m.Ready(B, Gid(A), 1);
        m.Ready(A, Gid(B), 2);

        using var inCommit = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        w.DuringCommit = () => { inCommit.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var saving = Task.Factory.StartNew(() => m.Ready(B, Gid(A), 2), TaskCreationOptions.LongRunning);
        Assert.True(inCommit.Wait(TimeSpan.FromSeconds(10)));

        // Another player's trade request is handled while the save runs, and the saving pair cannot leave or restart.
        var other = Task.Run(() => { m.Create(C, Gid(A)); m.Leave(A); m.Create(A, Gid(B)); return m.IsTrading(C); });
        Assert.Same(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(TradeStatus.AlreadyTrading, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(C).Single().Status);
        Assert.True(m.IsTrading(A));

        release.Set();
        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, w.Commits);
        Assert.Equal(TradeStatus.Done, w.Of<WIZARD_12_PROTOCOL.MSG_TRADE_RESULT>(A).Single().Status);
        Assert.Equal([Storm], w.Saved(A));
        Assert.Equal([Fire], w.Saved(B));
        Assert.False(m.IsTrading(A));
        Assert.False(m.IsTrading(B));
    }

    private static World Opened(out TreasureTradeManager manager) {
        var w = new World();
        manager = w.Manager;
        manager.Create(A, Gid(B));
        manager.Join(B, Gid(A), 1);
        w.Sent.Clear();

        return w;
    }

    /// <summary>An in-memory server: live wizards, saved books, sent messages.</summary>
    private sealed class World : ITradeWorld {

        internal readonly Store Store = new();
        internal readonly Dictionary<ulong, Wizard> Live = [];
        internal readonly Dictionary<ulong, string> Zones = [];
        internal readonly Dictionary<ulong, ulong> Instances = [];
        internal readonly HashSet<(ulong, ulong)> Friends = [(A, B)];
        internal readonly ConcurrentQueue<(ulong To, IMessage Message)> Sent = new();
        internal bool Enabled = true;
        internal bool FailSave { get => Store.FailSave; set => Store.FailSave = value; }
        internal int Commits;
        internal int Saves => Store.Saves;
        internal Dictionary<ulong, List<uint>> SavedBooks => Store.Saved;
        internal readonly TreasureTradeManager Manager;

        internal World() {
            foreach (var id in new[] { A, B, C }) {
                Live[id] = Store.Add(id);
                Live[id].Location = new Vector3(id == B ? 100 : 0, 0, 0);
                Zones[id] = "WizardCity/WC_Ravenwood";
                Instances[id] = 0;
            }

            Manager = new TreasureTradeManager(this);
        }

        internal void Book(ulong id, params uint[] cards) {
            foreach (var card in cards) {
                Live[id].SpellbookBehavior.AddTreasureCard(card);
                Store.Saved[id].Add(card);
            }
        }

        internal List<uint> Saved(ulong id) => Store.Saved[id];

        internal IMessage Last(ulong to) => Sent.Where(s => s.To == to).Select(s => s.Message).Last();

        internal List<T> Of<T>(ulong to) => Sent.Where(s => s.To == to).Select(s => s.Message).OfType<T>().ToList();

        public bool TradingEnabled => Enabled;

        public TradeParty Party(ulong charId)
            => Live.TryGetValue(charId, out var w) ? new TradeParty(charId, w, Zones[charId], Instances[charId], w.Location) : null;

        public bool AreFriends(ulong charId, ulong otherCharId) => Friends.Contains((charId, otherCharId)) || Friends.Contains((otherCharId, charId));

        public uint TreasureTemplateOf(uint spellId) => spellId is Fire or Ice or Storm or Myth or Life ? spellId : 0;

        public void Send(ulong charId, IMessage message) => Sent.Enqueue((charId, message));

        internal Action? DuringCommit;

        public bool Commit(Wizard first, IReadOnlyList<uint> firstGives, Wizard second, IReadOnlyList<uint> secondGives) {
            DuringCommit?.Invoke();
            var ok = WizardCollection.CommitTreasureCardTrade(first, firstGives, second, secondGives, Store.Open, Store.Load);
            if (ok) {
                Interlocked.Increment(ref Commits);
            }

            return ok;
        }

    }

    /// <summary>Saved wizards behind a fake RavenDB session; SaveChanges writes every loaded wizard at once.</summary>
    internal sealed class Store {

        internal readonly Dictionary<ulong, List<uint>> Saved = [];
        internal bool FailSave;
        internal int Saves;
        private readonly object _gate = new();

        internal Wizard Add(ulong id, params uint[] cards) {
            Saved[id] = [.. cards];
            var live = Make(id);
            foreach (var card in cards) {
                live.SpellbookBehavior.AddTreasureCard(card);
            }

            return live;
        }

        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, MultiSessionProxy>();
            var proxy = (MultiSessionProxy) (object) session;
            proxy.Commit = working => {
                if (FailSave) {
                    throw new InvalidOperationException("Injected SaveChanges failure");
                }

                lock (_gate) {
                    foreach (var (id, wizard) in working) {
                        Saved[id] = [.. wizard.SpellbookBehavior.TreasureCardTemplateIds];
                    }

                    Saves++;
                }
            };

            return session;
        }

        internal Wizard Load(IDocumentSession session, ulong id) {
            Assert.True(WizardCollection.HoldsWriteLane);
            var proxy = (MultiSessionProxy) (object) session;
            var wizard = Make(id);
            lock (_gate) {
                wizard.SpellbookBehavior.TreasureCardTemplateIds = [.. Saved[id]];
            }

            proxy.Working[id] = wizard;

            return wizard;
        }

        private static Wizard Make(ulong id) => new() {
            CharId = id,
            GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 1000, m_baseGoldPouch = 2000 },
            SpellbookBehavior = new ServerWizSpellbookBehavior { TreasureCardTemplateIds = [] },
        };

    }

    public class MultiSessionProxy : DispatchProxy {

        internal readonly Dictionary<ulong, Wizard> Working = [];
        internal Action<Dictionary<ulong, Wizard>> Commit = null!;
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

}
