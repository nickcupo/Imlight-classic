using System;
using System.Reflection;
using System.Text.Json;
using Raven.Client.Documents.Session;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;
using System.Runtime.CompilerServices;
namespace Imlight.Classic.Tests;

public sealed class MonstrologyTransactionTests {
    public class SessionStub : DispatchProxy {
        internal MonstrologyLedger Ledger = null!;
        internal int Stores;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name == "Load") return Ledger;
            if (method.Name == "Store") { Ledger = (MonstrologyLedger)args![0]!; Stores++; return null; }
            throw new NotSupportedException(method.Name);
        }
    }
    private sealed class Fixture {
        internal MonstrologyLedger Persisted = new() { OwnerId=42, Animus = new() { [123]=5 } };
        internal int Gold=20;
        internal int Cards;
        internal int Saves;
        internal bool RejectSave;
        internal bool ThrowSave;
        internal bool Transact(ulong owner, Func<IDocumentSession,Wizard,bool> apply, Action<Wizard> afterCommit) {
            var session = DispatchProxy.Create<IDocumentSession,SessionStub>();
            var stub = (SessionStub)(object)session;
            stub.Ledger = JsonSerializer.Deserialize<MonstrologyLedger>(JsonSerializer.Serialize(Persisted))!;
            var stats = (ServerWizGameStats)RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
            stats.m_currentGold = Gold;
            var wizard = new Wizard { CharId=owner, GameStats=stats, SpellbookBehavior=new ServerWizSpellbookBehavior() };
            if (!apply(session,wizard)) return false;
            if (ThrowSave) throw new InvalidOperationException("Simulated commit failure");
            if (RejectSave) return false;
            Persisted = stub.Ledger; Gold=wizard.GameStats.m_currentGold;
            Cards += wizard.SpellbookBehavior.TreasureCardTemplateIds.Count; Saves++;
            Assert.Equal(1,stub.Stores);
            afterCommit?.Invoke(wizard);
            return true;
        }
    }
    private static AnimusCreation Request(string id="one") => new(id,42,123,321,3,10,true);
    [Fact] public void AtomicCallbackDeliversAndDebitsTogetherAndReplayDoesNotDuplicate() {
        var f=new Fixture();
        var published = 0;
        Assert.Equal(MonstrologyResult.Applied,MonstrologyRepository.CreateCard(42,Request(),out var gold,f.Transact,
            afterCommit: wizard => { Assert.Equal(1,f.Saves); published++; Assert.Equal(10,wizard.GameStats.m_currentGold); }));
        Assert.Equal(1,published);
        Assert.Equal(10,gold); Assert.Equal(10,f.Gold); Assert.Equal(2,f.Persisted.Animus[123]); Assert.Equal(1,f.Cards); Assert.Equal(1,f.Saves);
        Assert.Equal(MonstrologyResult.Replay,MonstrologyRepository.CreateCard(42,Request(),out _,f.Transact));
        Assert.Equal(1,f.Cards); Assert.Equal(1,f.Saves);
        Assert.Equal(MonstrologyResult.InsufficientAnimus,MonstrologyRepository.CreateCard(42,Request("two"),out _,f.Transact));
        Assert.Equal(1,f.Cards); Assert.Equal(10,f.Gold);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FailedCommitDoesNotPublishBalancesOrPersistAnyPart(bool throws) {
        var f=new Fixture { RejectSave=!throws, ThrowSave=throws };
        Action<Wizard> publish = _ => throw new Xunit.Sdk.XunitException("Failed commit published attached state");
        if (throws) {
            var error = Assert.Throws<InvalidOperationException>(() => MonstrologyRepository.CreateCard(42,Request(),out _,f.Transact, afterCommit: publish));
            Assert.Equal("Simulated commit failure", error.Message);
        }
        else { Assert.Equal(MonstrologyResult.CommitFailed,MonstrologyRepository.CreateCard(42,Request(),out var gold,f.Transact, afterCommit: publish)); Assert.Equal(0,gold); }
        Assert.Equal(20,f.Gold); Assert.Equal(5,f.Persisted.Animus[123]); Assert.Empty(f.Persisted.Creations); Assert.Equal(0,f.Cards); Assert.Equal(0,f.Saves);
    }
    [Fact] public void WrongOwnerAndInsufficientGoldDoNotRequestSave() {
        var f=new Fixture { Gold=9 };
        Assert.Equal(MonstrologyResult.InsufficientGold,MonstrologyRepository.CreateCard(42,Request(),out _,f.Transact));
        Assert.Equal(MonstrologyResult.Rejected,MonstrologyRepository.CreateCard(43,Request(),out _,f.Transact));
        Assert.Equal(0,f.Saves); Assert.Equal(0,f.Cards); Assert.Equal(5,f.Persisted.Animus[123]);
    }
}
