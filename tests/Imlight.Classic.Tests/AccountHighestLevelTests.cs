// CLASSIC: account-level statistics must not depend on which saved sibling is initialized first.
using System;
using System.Reflection;
using Imlight.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class AccountHighestLevelTests : IDisposable {
    private static readonly MethodInfo InitializeStats = typeof(Wizard).GetMethod("AfterDatabaseLoadWizardGameStats",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo MaxLevel = typeof(MagicLevelsConfig).GetProperty(nameof(MagicLevelsConfig.MaxLevel))!;
    private readonly int _oldMaxLevel;

    public AccountHighestLevelTests() {
        EquipmentAttachConcurrencyTests.Configure("[Login Server]\nMaxAllowedCharactersPerAccount=6\n[Character]\nBaseGoldPouch=100000\nMaxLevel=50\n");
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap()));
        _oldMaxLevel = MagicLevelsConfig.MaxLevel;
        MaxLevel.SetValue(null, 50);
    }

    public void Dispose() {
        MaxLevel.SetValue(null, _oldMaxLevel);
        ClassicRuntime.ResetForTests();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void AccountAndInitializationOrderCannotHideTheHigherSavedSibling(bool highFirstOnAccount, bool highFirstToLoad) {
        var low = SavedWizard(1); var high = SavedWizard(50); var account = new Account();
        account.Characters.AddRange(highFirstOnAccount ? [high, low] : [low, high]);
        low.Account = high.Account = account;
        Assert.Equal(0, low.GameStats.Level); Assert.Equal(0, high.GameStats.Level);
        Assert.Same(high, account.GetHighestLevelWizard());

        foreach (var wizard in highFirstToLoad ? new[] { high, low } : new[] { low, high }) {
            InitializeStats.Invoke(wizard, null);
            Assert.Equal(wizard.MagicSchoolBehavior.Level, wizard.GameStats.Level);
            Assert.Equal(50, wizard.GameStats.m_highestCharacterLevelOnAccount);
            Assert.Same(high, account.GetHighestLevelWizard());
        }
        Assert.Equal(50, low.GameStats.m_highestCharacterLevelOnAccount);
        Assert.Equal(50, high.GameStats.m_highestCharacterLevelOnAccount);
    }

    [Fact]
    public void InitializedLowerSiblingsAndInflatedTransientStatsCannotOverrideSavedLevel() {
        var low = SavedWizard(1); var middle = SavedWizard(20); var high = SavedWizard(50);
        var account = new Account { Characters = [low, middle, high] };
        low.Account = middle.Account = high.Account = account;
        low.GameStats.Level = 99; middle.GameStats.Level = 20;
        Assert.Same(high, account.GetHighestLevelWizard());
        InitializeStats.Invoke(high, null);
        Assert.Equal(50, high.GameStats.m_highestCharacterLevelOnAccount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HighestSavedLevelStillUsesTheExistingProfileCap(bool highFirstToLoad) {
        var low = SavedWizard(1); var high = SavedWizard(75);
        var account = new Account { Characters = [low, high] };
        low.Account = high.Account = account;
        Assert.Same(high, account.GetHighestLevelWizard());
        foreach (var wizard in highFirstToLoad ? new[] { high, low } : new[] { low, high }) {
            InitializeStats.Invoke(wizard, null);
            Assert.Equal(50, wizard.GameStats.m_highestCharacterLevelOnAccount);
        }
        Assert.Equal(50, high.MagicSchoolBehavior.Level);
        Assert.Equal(50, high.GameStats.Level);
        Assert.Equal(1, low.MagicSchoolBehavior.Level);
    }

    [Fact]
    public void OneUninitializedWizardUsesItsSavedLevel() {
        var wizard = SavedWizard(50); var account = new Account { Characters = [wizard] };
        wizard.Account = account;
        InitializeStats.Invoke(wizard, null);
        Assert.Equal(50, wizard.GameStats.m_highestCharacterLevelOnAccount);
    }

    [Fact]
    public void EqualSavedLevelsRetainTheFirstCharacter() {
        var first = SavedWizard(50); var second = SavedWizard(50);
        second.GameStats.Level = 50;
        Assert.Same(first, new Account { Characters = [first, second] }.GetHighestLevelWizard());
    }

    [Fact]
    public void EmptyAccountStillReturnsNull() => Assert.Null(new Account().GetHighestLevelWizard());

    [Fact]
    public void NullCharacterCollectionStillRefuses() => Assert.Throws<ArgumentNullException>(
        () => new Account { Characters = null! }.GetHighestLevelWizard());

    private static Wizard SavedWizard(int level) {
        var stats = new ServerWizGameStats(MagicSchool.Fire, level);
        // The saved stats lose JsonIgnore Level while the school behavior retains the persisted level.
        var savedStats = JsonConvert.DeserializeObject<ServerWizGameStats>(JsonConvert.SerializeObject(stats))!;
        Assert.Equal(0, savedStats.Level);
        return new Wizard {
            MagicSchoolBehavior = new ServerMagicSchoolBehavior { MagicSchool = MagicSchool.Fire, Level = level },
            GameStats = savedStats,
        };
    }
}
