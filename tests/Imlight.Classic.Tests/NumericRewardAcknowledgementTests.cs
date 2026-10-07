// CLASSIC: gold and training-point mutations publish after ACK and quarantine an uncertain saved write.
using System;
using System.Reflection;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class NumericRewardAcknowledgementTests {
    public NumericRewardAcknowledgementTests()
        => EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void FailedOrDurableLostAckQuarantinesInsideTheLaneAndCannotGrantOrSpendEitherCurrencyAgain(
        bool training, bool spend, bool durable) {
        var f = new Fixture { FailSave = true, Durable = durable };
        var live = f.Live(); live.GameStats.m_currentGold = 999; live.MagicSchoolBehavior.TrainingPoints = 77;
        var stats = live.GameStats; var school = live.MagicSchoolBehavior;
        var observedBeforeLaneRelease = false;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
            Assert.Equal(999, live.GameStats.m_currentGold); Assert.Equal(77, live.MagicSchoolBehavior.TrainingPoints);
        };
        f.OnDispose = () => {
            // CommitCharacterMutation disposes the session while its character lane is still held.
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
            observedBeforeLaneRelease = true;
        };
        Assert.Throws<InvalidOperationException>(() => f.Change(live, training, spend));
        Assert.True(observedBeforeLaneRelease); Assert.False(WizardCollection.HoldsWriteLane);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(training || !durable ? 100 : spend ? 80 : 120, f.Saved.GameStats.m_currentGold);
        Assert.Equal(!training || !durable ? 5 : spend ? 3 : 7, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Same(stats, live.GameStats); Assert.Same(school, live.MagicSchoolBehavior);
        Assert.Equal(999, live.GameStats.m_currentGold); Assert.Equal(77, live.MagicSchoolBehavior.TrainingPoints);

        foreach (var nextTraining in new[] { false, true }) {
            Assert.False(f.Change(live, nextTraining, spend: false));
            Assert.False(f.Change(live, nextTraining, spend: true));
        }
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.Opened);
        Assert.Equal(999, live.GameStats.m_currentGold); Assert.Equal(77, live.MagicSchoolBehavior.TrainingPoints);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OrdinaryGainAndSpendUseTheFreshSavedBalanceAndRetainLiveBehaviorAliases(bool training, bool spend) {
        var f = new Fixture(); var live = f.Live();
        live.GameStats.m_currentGold = 999; live.MagicSchoolBehavior.TrainingPoints = 77;
        var stats = live.GameStats; var school = live.MagicSchoolBehavior;
        f.OnSave = () => {
            Assert.Equal(999, live.GameStats.m_currentGold); Assert.Equal(77, live.MagicSchoolBehavior.TrainingPoints);
            Assert.Equal(training ? 100 : spend ? 80 : 120, f.Working!.Wizard.GameStats.m_currentGold);
            Assert.Equal(training ? spend ? 3 : 7 : 5, f.Working.Wizard.MagicSchoolBehavior.TrainingPoints);
        };
        Assert.True(f.Change(live, training, spend));
        Assert.Equal(training ? 100 : spend ? 80 : 120, f.Saved.GameStats.m_currentGold);
        Assert.Equal(training ? spend ? 3 : 7 : 5, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(training ? 999 : spend ? 80 : 120, live.GameStats.m_currentGold);
        Assert.Equal(training ? spend ? 3 : 7 : 77, live.MagicSchoolBehavior.TrainingPoints);
        Assert.Same(stats, live.GameStats); Assert.Same(school, live.MagicSchoolBehavior);
        Assert.Equal(33, live.GameStats.m_currentMana); Assert.Equal(33, f.Saved.GameStats.m_currentMana);
        Assert.Equal(1, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UncoveredDebitOrOverflowIsAProvenRefusalWithoutSavePublicationOrQuarantine(bool training, bool overflow) {
        var f = new Fixture(); var live = f.Live();
        live.GameStats.m_currentGold = 999; live.MagicSchoolBehavior.TrainingPoints = 77;
        var refused = training
            ? WizardCollection.ChangeTrainingPoints(live, overflow ? int.MaxValue : -6, f.Open, f.Load)
            : WizardCollection.ChangeGold(live, overflow ? int.MaxValue : -101, false, f.Open, f.Load);
        Assert.False(refused); Assert.Equal(0, f.SaveAttempts);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(999, live.GameStats.m_currentGold); Assert.Equal(77, live.MagicSchoolBehavior.TrainingPoints);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.True(f.Change(live, training, spend: false)); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSavedCharacterDoesNotSavePublishOrQuarantine(bool training) {
        var f = new Fixture { Missing = true }; var live = f.Live();
        Assert.False(f.Change(live, training, spend: false)); Assert.Equal(0, f.SaveAttempts);
        Assert.Equal(100, live.GameStats.m_currentGold); Assert.Equal(5, live.MagicSchoolBehavior.TrainingPoints);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        f.Missing = false; Assert.True(f.Change(live, training, spend: false)); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(100, 30, true, 110)]
    [InlineData(100, 30, false, 130)]
    [InlineData(110, 1, true, 110)]
    public void GoldKeepsTheExistingSavedPouchCapAndAcknowledgedNoOp(int savedGold, int delta, bool cap, int expected) {
        var f = new Fixture(); f.Saved.GameStats.m_currentGold = savedGold; f.Saved.GameStats.m_baseGoldPouch = 110;
        var live = f.Live(); live.GameStats.m_currentGold = 1; live.GameStats.m_baseGoldPouch = 999;
        Assert.True(WizardCollection.ChangeGold(live, delta, cap, f.Open, f.Load));
        Assert.Equal(expected, f.Saved.GameStats.m_currentGold); Assert.Equal(expected, live.GameStats.m_currentGold);
        Assert.Equal(110, f.Saved.GameStats.m_baseGoldPouch); Assert.Equal(999, live.GameStats.m_baseGoldPouch);
        Assert.Equal(1, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertaintyRaisedDuringTheFreshLoadRefusesInsideTheLaneWithoutChangingTrackedBalances(bool training) {
        var f = new Fixture(); var live = f.Live();
        f.OnLoad = () => {
            Assert.True(WizardCollection.HoldsWriteLane);
            WizardCollection.MarkInventorySnapshotUncertain(live);
        };
        Assert.False(f.Change(live, training, spend: false)); Assert.Equal(1, f.Opened); Assert.Equal(0, f.SaveAttempts);
        Assert.Equal(100, f.Working!.Wizard.GameStats.m_currentGold); Assert.Equal(5, f.Working.Wizard.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(100, live.GameStats.m_currentGold); Assert.Equal(5, live.MagicSchoolBehavior.TrainingPoints);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void AnAlreadyUncertainLiveCopyCannotOpenATransactionForEitherCurrency() {
        var f = new Fixture(); var live = f.Live(); WizardCollection.MarkInventorySnapshotUncertain(live);
        Assert.False(f.Change(live, training: false, spend: false)); Assert.False(f.Change(live, training: false, spend: true));
        Assert.False(f.Change(live, training: true, spend: false)); Assert.False(f.Change(live, training: true, spend: true));
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.SaveAttempts);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints);
    }

    private sealed class Fixture {
        internal const ulong Character = 941771;
        internal Wizard Saved = new() { CharId = Character,
            GameStats = new(default, 1) { m_currentGold = 100, m_baseGoldPouch = 200, m_currentMana = 33 },
            MagicSchoolBehavior = new() { TrainingPoints = 5 } };
        internal int SaveAttempts, Opened;
        internal bool FailSave, Durable, Missing;
        internal System.Action? OnLoad, OnSave, OnDispose;
        internal NumericSession? Working;
        internal Wizard Live() => Clone(Saved);
        internal bool Change(Wizard live, bool training, bool spend)
            => training ? WizardCollection.ChangeTrainingPoints(live, spend ? -2 : 2, Open, Load)
                : WizardCollection.ChangeGold(live, spend ? -20 : 20, false, Open, Load);
        internal IDocumentSession Open() {
            Opened++; var session = DispatchProxy.Create<IDocumentSession, NumericSession>();
            var proxy = (NumericSession)(object)session; Working = proxy; proxy.Wizard = Clone(Saved);
            proxy.DisposeSession = () => OnDispose?.Invoke();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); SaveAttempts++; OnSave?.Invoke();
                if (FailSave && !Durable) throw new InvalidOperationException("fixture failed numeric write");
                Saved = Clone(proxy.Wizard);
                if (FailSave) throw new InvalidOperationException("fixture lost numeric acknowledgement");
            };
            return session;
        }
        internal Wizard Load(IDocumentSession session, ulong character) {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(Character, character); OnLoad?.Invoke();
            return Missing ? null! : ((NumericSession)(object)session).Wizard;
        }
        private static Wizard Clone(Wizard wizard) => new() { CharId = wizard.CharId,
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            MagicSchoolBehavior = new() { TrainingPoints = wizard.MagicSchoolBehavior.TrainingPoints } };
    }

    public class NumericSession : DispatchProxy {
        internal Wizard Wizard = null!;
        internal System.Action Save = null!, DisposeSession = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, NumericAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced, "SaveChanges" => SaveNow(), "Dispose" => DisposeNow(),
            _ => throw new NotSupportedException(method.Name),
        };
        private object? SaveNow() { Save(); return null; }
        private object? DisposeNow() { DisposeSession(); return null; }
    }

    public class NumericAdvanced : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, _ => throw new NotSupportedException(method.Name),
        };
    }
}
