using System;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaTicketShopCommandTests {
    public ArenaTicketShopCommandTests() => EquipmentAttachConcurrencyTests.Configure();

    [Fact]
    public void PurchaseUsesSavedTicketsEvenWhenAttachedBalanceLooksTooSmall() {
        var store = new TicketStore(100);
        var attached = store.Live(5);
        var grants = 0;
        using (store.Scope()) {
            var result = ShopService.TryPurchaseWithTickets(attached, 70, 500, 500, () => {
                Assert.Equal(30, store.Saved.GameStats.m_currentArenaPoints);
                Assert.Equal(30, attached.GameStats.m_currentArenaPoints);
                Assert.Equal(30, attached.GameStats.m_currentPvPCurrency);
                Assert.Equal(1, store.Saves);
                grants++;
                return true;
            });

            Assert.Equal(ShopService.TicketPurchaseResult.Purchased, result);
        }

        Assert.Equal(1, grants);
        Assert.Equal(30, store.Saved.GameStats.m_currentArenaPoints);
    }

    [Fact]
    public void StaleHighBalanceCannotGrantAnUnaffordableItem() {
        var store = new TicketStore(50);
        var attached = store.Live(999);
        var grants = 0;
        using (store.Scope()) {
            Assert.Equal(ShopService.TicketPurchaseResult.TicketsUnavailable,
                ShopService.TryPurchaseWithTickets(attached, 70, 500, 0, () => { grants++; return true; }));
        }

        Assert.Equal(0, grants);
        Assert.Equal(0, store.Saves);
        Assert.Equal(50, store.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(999, attached.GameStats.m_currentArenaPoints);
    }

    [Fact]
    public async Task ConcurrentPurchasesCannotSpendTheSameSavedTickets() {
        var store = new TicketStore(100);
        var grants = 0;
        var purchases = new Task<ShopService.TicketPurchaseResult>[20];
        using (store.Scope()) {
            for (var i = 0; i < purchases.Length; i++) {
                var attached = store.Live(999);
                purchases[i] = Task.Run(() => ShopService.TryPurchaseWithTickets(attached, 40, 500, 0, () => {
                    Interlocked.Increment(ref grants);
                    return true;
                }), TestContext.Current.CancellationToken);
            }

            var results = await Task.WhenAll(purchases);
            Assert.Equal(2, Array.FindAll(results, result => result == ShopService.TicketPurchaseResult.Purchased).Length);
            Assert.Equal(18, Array.FindAll(results, result => result == ShopService.TicketPurchaseResult.TicketsUnavailable).Length);
        }

        Assert.Equal(2, grants);
        Assert.Equal(2, store.Saves);
        Assert.Equal(20, store.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(20, store.Saved.GameStats.m_currentPvPCurrency);
    }

    [Fact]
    public void RefusedInventoryRefundRetainsAnAwardSavedAfterTheDebit() {
        var store = new TicketStore(100);
        var attached = store.Live(999);
        var grants = 0;
        using (store.Scope()) {
            Assert.Equal(ShopService.TicketPurchaseResult.InventoryRefused,
                ShopService.TryPurchaseWithTickets(attached, 60, 500, 0, () => {
                    Assert.Equal(40, store.Saved.GameStats.m_currentArenaPoints);
                    Assert.True(WizardCollection.ChangeArenaTickets(store.Live(900), 10));
                    grants++;
                    return false;
                }));
        }

        Assert.Equal(1, grants);
        Assert.Equal(3, store.Saves);
        Assert.Equal(110, store.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(110, attached.GameStats.m_currentArenaPoints);
        Assert.Equal(110, attached.GameStats.m_currentPvPCurrency);
    }

    [Fact]
    public void ARefusedRefundFailsInsteadOfReportingAnOrdinaryPurchaseRefusal() {
        var store = new TicketStore(100);
        var attached = store.Live();
        using (store.Scope()) {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ShopService.TryPurchaseWithTickets(attached, 60, 500, 0, () => {
                    store.Missing = true;
                    return false;
                }));
            Assert.Contains("could not be refunded", error.Message);
        }

        Assert.Equal(1, store.Saves);
        Assert.Equal(40, store.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(40, attached.GameStats.m_currentArenaPoints);
    }

    [Fact]
    public void AnUncertainItemSaveIsNotAutomaticallyRefundedOrRetried() {
        var store = new TicketStore(100);
        var attached = store.Live();
        var grantAttempts = 0;
        using (store.Scope()) {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ShopService.TryPurchaseWithTickets(attached, 60, 500, 0, () => {
                    grantAttempts++;
                    throw new InvalidOperationException("Item save acknowledgement lost");
                }));
            Assert.Equal("Item save acknowledgement lost", error.Message);
        }

        Assert.Equal(1, grantAttempts);
        Assert.Equal(1, store.Opens);
        Assert.Equal(1, store.Saves);
        Assert.Equal(40, store.Saved.GameStats.m_currentArenaPoints);
    }

    [Fact]
    public void FailedTicketSaveCannotGrantOrPublishADebit() {
        var store = new TicketStore(100) { FailSave = true };
        var attached = store.Live(900);
        var grants = 0;
        using (store.Scope()) {
            Assert.Throws<InvalidOperationException>(() => ShopService.TryPurchaseWithTickets(attached, 60, 500, 0,
                () => { grants++; return true; }));
        }

        Assert.Equal(0, grants);
        Assert.Equal(0, store.Saves);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(100, store.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(900, attached.GameStats.m_currentArenaPoints);
        Assert.Equal(900, attached.GameStats.m_currentPvPCurrency);
    }

    [Theory]
    [InlineData(-1, 500, 0, "InvalidPrice")]
    [InlineData(50, 499, 500, "RankRequired")]
    public void InvalidPricesAndInsufficientRankCannotDebitOrGrant(int price, int rating, int minimumRating,
        string expected) {
        var store = new TicketStore(100);
        var grants = 0;
        using (store.Scope()) {
            Assert.Equal(expected, ShopService.TryPurchaseWithTickets(store.Live(), price, rating, minimumRating,
                () => { grants++; return true; }).ToString());
        }

        Assert.Equal(0, grants);
        Assert.Equal(0, store.Opens);
        Assert.Equal(100, store.Saved.GameStats.m_currentArenaPoints);
    }

    [Fact]
    public void AddTicketsRetainsTheExactQualityAssuranceAuthorizationContract() {
        var method = typeof(CommandModifyProtocol).GetMethod("AddTicketsCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal("addtickets", method.GetCustomAttribute<CommandAttribute>()!.Name);
        Assert.Equal(AuthLevel.QualityAssurance, method.GetCustomAttribute<AuthRequiredAttribute>()!.Level);
    }

    [Theory]
    [InlineData("10", 20, 900, 30)]
    [InlineData("-30", 20, 900, 0)]
    [InlineData("-2147483648", 20, 900, 0)]
    public async Task AuthorizedCommandChangesSavedTicketsAndPublishesCommittedBalance(string delta, int saved,
        int stale, int expected) {
        var store = new TicketStore(saved);
        var attached = store.Live(stale);
        using var system = ActorSystem.Create("ticket-command-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var messages = Channel.CreateUnbounded<object>();
        try {
            var sink = system.ActorOf(Props.Create(() => new MessageSink(messages.Writer)));
            using (store.Scope()) {
                Assert.True(new CommandModifyProtocol().Execute("addtickets", Context(attached, sink), delta));
            }

            Assert.Equal(expected, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEARENAPOINTS>(await Next(messages)).Points);
            Assert.Equal(expected, Assert.IsType<WIZARD3_56_PROTOCOL.MSG_UPDATEPVPCURRENCY>(await Next(messages)).PvPCurrency);
            Assert.Equal($"Arena Tickets: {expected}.", Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(await Next(messages)).Message);
            Assert.Equal(1, store.Saves);
            Assert.Equal(expected, store.Saved.GameStats.m_currentArenaPoints);
            Assert.Equal(expected, store.Saved.GameStats.m_currentPvPCurrency);
            Assert.Equal(expected, attached.GameStats.m_currentArenaPoints);
            Assert.Equal(expected, attached.GameStats.m_currentPvPCurrency);
        }
        finally {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData("1", int.MaxValue, false)]
    [InlineData("10", 20, true)]
    public async Task ARefusedCommandMutationCannotPublishBalanceOrSuccess(string delta, int saved, bool missing) {
        var store = new TicketStore(saved) { Missing = missing };
        var attached = store.Live(900);
        using var system = ActorSystem.Create("ticket-command-refused-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var messages = Channel.CreateUnbounded<object>();
        try {
            var sink = system.ActorOf(Props.Create(() => new MessageSink(messages.Writer)));
            using (store.Scope()) {
                Assert.True(new CommandModifyProtocol().Execute("addtickets", Context(attached, sink), delta));
            }

            Assert.Equal("Arena Tickets could not be saved.", Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(await Next(messages)).Message);
            Assert.False(messages.Reader.TryRead(out _));
            Assert.Equal(0, store.Saves);
            Assert.Equal(saved, store.Saved.GameStats.m_currentArenaPoints);
            Assert.Equal(900, attached.GameStats.m_currentArenaPoints);
            Assert.Equal(900, attached.GameStats.m_currentPvPCurrency);
        }
        finally {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AFailedCommandSaveCannotSendATicketBalanceOrSuccess() {
        var store = new TicketStore(20) { FailSave = true };
        var attached = store.Live(900);
        using var system = ActorSystem.Create("ticket-command-save-failed-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var messages = Channel.CreateUnbounded<object>();
        try {
            var sink = system.ActorOf(Props.Create(() => new MessageSink(messages.Writer)));
            using (store.Scope()) {
                var error = Assert.Throws<TargetInvocationException>(() =>
                    new CommandModifyProtocol().Execute("addtickets", Context(attached, sink), "10"));
                Assert.IsType<InvalidOperationException>(error.InnerException);
            }

            // A mailbox marker observes every packet the command could have sent before failing.
            var marker = new object();
            sink.Tell(marker);
            Assert.Same(marker, await Next(messages));
            Assert.False(messages.Reader.TryRead(out _));
            Assert.Equal(1, store.SaveAttempts);
            Assert.Equal(0, store.Saves);
            Assert.Equal(20, store.Saved.GameStats.m_currentArenaPoints);
            Assert.Equal(900, attached.GameStats.m_currentArenaPoints);
            Assert.Equal(900, attached.GameStats.m_currentPvPCurrency);
        }
        finally {
            await system.Terminate();
        }
    }

    private static CommandContext Context(Wizard wizard, IActorRef sink) => new() {
        Character = wizard,
        SessionActor = sink,
        Account = new Account("", "", "") { AuthLevel = AuthLevel.QualityAssurance },
    };

    private static async Task<object> Next(Channel<object> channel)
        => await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed class MessageSink : ReceiveActor {
        public MessageSink(ChannelWriter<object> writer) => Receive<object>(message => writer.TryWrite(message));
    }

    private sealed class TicketStore {
        private readonly TicketWalletFixture _wallet;
        internal Wizard Saved => _wallet.Live();
        internal bool Missing { set => _wallet.RefuseLoad = value; }
        internal bool FailSave { set => _wallet.FailSave = value; }
        internal int Opens;
        internal int Saves => _wallet.Saves;
        internal int SaveAttempts;

        internal TicketStore(int tickets) {
            _wallet = new TicketWalletFixture(tickets, tickets);
            _wallet.BeforeSave = () => Interlocked.Increment(ref SaveAttempts);
        }

        internal Wizard Live(int? tickets = null) {
            var wizard = _wallet.Live();
            if (tickets is { } stale) {
                wizard.GameStats.m_currentArenaPoints = stale;
                wizard.GameStats.m_currentPvPCurrency = stale;
            }
            return wizard;
        }

        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, _wallet.Load);
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }

        private IDocumentSession Open() {
            Interlocked.Increment(ref Opens);
            return _wallet.Open();
        }
    }

    private sealed class Restore(Action action) : IDisposable {
        public void Dispose() => action();
    }
}
