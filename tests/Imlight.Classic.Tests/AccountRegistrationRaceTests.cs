// CLASSIC: registration uniqueness must span different account write lanes and include committed prior writes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class AccountRegistrationRaceTests {
    public AccountRegistrationRaceTests() {
        var config = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "w101c-registration-tests-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(config, "[Login Server]\nMaxAllowedCharactersPerAccount=6\n[Logging]\nLogLevel=FATAL\nLogPath=/private/tmp/account-registration-tests.log\n");
        ConfigurationManager.Initialize(config);
    }

    [Fact]
    public async Task DuplicateRegistrationsOnDifferentLanesCannotBothPassTheUniquenessCheck() {
        var fixture = new Fixture();
        var first = Account("friend", 1); var second = Account("friend", 2);
        using var checkedFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var startedSecond = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var checks = 0;
        bool Exists(IDocumentSession session, string username) {
            var exists = fixture.Exists(session, username);
            if (Interlocked.Increment(ref checks) == 1) {
                checkedFirst.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("Registration test did not release the first check.");
            }
            return exists;
        }
        var one = Task.Factory.StartNew(() => AccountCollection.CreateAccount(first, fixture.Open, Exists), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(checkedFirst.Wait(TimeSpan.FromSeconds(10), token));
        var two = Task.Factory.StartNew(() => {
            startedSecond.Set(); return AccountCollection.CreateAccount(second, fixture.Open, Exists);
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try {
            Assert.True(startedSecond.Wait(TimeSpan.FromSeconds(10), token));
            Assert.NotSame(two, await Task.WhenAny(two, Task.Delay(100, token)));
        } finally { releaseFirst.Set(); }
        var results = await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(new[] { true, false }, results);
        Assert.Single(fixture.Persisted); Assert.Equal(first.AccountId, fixture.Persisted["friend"].AccountId);
        Assert.Equal(1, fixture.Saves); Assert.Equal(2, checks);
    }

    [Fact]
    public void ExistingCaseVariantIsReservedWithoutOverwritingTheSavedAccount() {
        var fixture = new Fixture(); var original = Account("Friend", 5);
        fixture.Persisted.Add(original.Username, original);
        Assert.False(AccountCollection.CreateAccount(Account("friend", 6), fixture.Open, fixture.Exists));
        Assert.Same(original, Assert.Single(fixture.Persisted).Value); Assert.Equal(0, fixture.Saves);
    }

    [Fact]
    public void FailedSaveReleasesRegistrationAndAccountLocksWithoutPublishingAnAccount() {
        var fixture = new Fixture { FailSave = true };
        Assert.Throws<IOException>(() => AccountCollection.CreateAccount(Account("friend", 7), fixture.Open, fixture.Exists));
        Assert.Empty(fixture.Persisted);
        fixture.FailSave = false;
        Assert.True(AccountCollection.CreateAccount(Account("friend", 8), fixture.Open, fixture.Exists));
        Assert.Single(fixture.Persisted); Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public void RegistrationCannotInvertAccountWriteLaneLockOrder() {
        var fixture = new Fixture(); var held = Account("held", 9);
        Assert.False(AccountCollection.CommitAccountMutation(held.AccountId, _ => {
            Assert.Throws<InvalidOperationException>(() => AccountCollection.CreateAccount(Account("friend", 10), fixture.Open, fixture.Exists));
            return false;
        }, null!, fixture.Open, (_, _) => held));
        Assert.True(AccountCollection.CreateAccount(Account("friend", 10), fixture.Open, fixture.Exists));
    }

    [Fact]
    public void RegistrationCannotAcquireAnAccountGateWhileHoldingAWizardLane() {
        var fixture = new Fixture(); var wizard = new Wizard();
        Assert.False(WizardCollection.CommitCharacterMutation(11, (_, _) => {
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.Throws<InvalidOperationException>(() => AccountCollection.CreateAccount(Account("friend", 12), fixture.Open, fixture.Exists));
            return false;
        }, null!, fixture.Open, (_, _) => wizard));
        Assert.False(WizardCollection.HoldsWriteLane);
        Assert.True(AccountCollection.CreateAccount(Account("friend", 12), fixture.Open, fixture.Exists));
    }

    private static Account Account(string username, ulong id) {
        var account = new Account(username, "", "synthetic protocol hash");
        typeof(Account).GetProperty(nameof(account.AccountId))!.SetValue(account, id);
        return account;
    }

    private sealed class Fixture {
        internal readonly Dictionary<string, Account> Persisted = new(StringComparer.OrdinalIgnoreCase);
        internal bool FailSave;
        internal int Saves;
        internal bool Exists(IDocumentSession _, string username) => Persisted.ContainsKey(username);
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, SessionProxy>();
            ((SessionProxy) (object) session).Commit = account => {
                if (FailSave) throw new IOException("Synthetic save failure.");
                Persisted.Add(account.Username, account); Saves++;
            };
            return session;
        }
    }

    public class SessionProxy : DispatchProxy {
        internal Action<Account> Commit = null!;
        private Account? _pending;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AdvancedProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced,
            "Store" => Store(args!),
            "SaveChanges" => Save(),
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Store(object?[] args) { _pending = Assert.IsType<Account>(args[0]); return null; }
        private object? Save() { Commit(_pending!); return null; }
    }

    public class AdvancedProxy : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, MetadataProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "GetMetadataFor" => _metadata,
            "set_OptimisticConcurrencyMode" => null,
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public class MetadataProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name == "set_Item") {
                Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
                Assert.Equal(AccountCollection.CollectionName, args[1]); return null;
            }
            throw new NotSupportedException(method.Name);
        }
    }
}
