// CLASSIC: native purchase eligibility must reflect saved tickets and ranked standing, not a missing/stale ladder.
using System;
using System.Collections.Generic;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaShopSnapshotTests : IDisposable {
    private const ulong Character = 771922;
    private const ulong Account = 881933;
    private static ArenaConfig Config => ArenaLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));

    private readonly FieldInfo _levelConfig = typeof(MagicLevelsConfig).GetField("s_playerLevelConfig", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousLevelConfig;

    public ArenaShopSnapshotTests() {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arena-shop-tests-{Guid.NewGuid():N}.ini");
        System.IO.File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={System.IO.Path.GetTempPath()}arena-shop-tests.log\n");
        Imlight.Common.ConfigurationManager.Initialize(path);
        _previousLevelConfig = _levelConfig.GetValue(null);
        _levelConfig.SetValue(null, new Dictionary<string, List<MagicLevelInfo>> {
            [default(MagicSchool).ToString()] = [new(), new() { m_hitpoints = 200, m_mana = 20 }],
        });
    }

    public void Dispose() => _levelConfig.SetValue(null, _previousLevelConfig);

    [Fact]
    public void AFirstTimeWizardGetsTheTruthfulStartingPrivateLadderWithoutCreatingAnEarnedRecord() {
        var store = new ArenaLadderCollection.Memory();
        var config = Config;
        var ladder = ArenaShopSnapshot.OwnLadder(Character, config, store);
        Assert.Equal(Character, ladder.m_characterID.Full);
        Assert.Equal(ArenaRules.Hash(config.RankedTournament), ladder.m_gameNameID);
        Assert.Equal(config.StartRating, ladder.m_score);
        Assert.Equal("Private", ArenaRules.RankOf(ladder.m_score, config.Ranks));
        Assert.Equal(0, ladder.m_gamesWon);
        Assert.Equal(0, ladder.m_gamesLost);
        Assert.Equal(0, ladder.m_gamesPlayed);
        Assert.Null(store.Load(Character));
        // The real native ReqArenaSeasonTitle requires a nonnull PvPSanctioned GameStats ladder even at Private.
        var wizard = Wizard();
        ArenaShopSnapshot.ReadOwn(wizard, config, store, _ => Wizard());
        Assert.NotNull(wizard.GameStats.GetClientTypeAlternative().m_pArenaLadder);
    }

    [Fact]
    public void NativeLadderReplyRoundTripsTheExactSavedScoreWinsLossesAndCharacter() {
        var store = new ArenaLadderCollection.Memory();
        store.Save(new ArenaLadderEntry { CharId = Character, Rating = 973, Wins = 28, Losses = 13 });
        var wizard = Wizard();
        var reply = ArenaShopSnapshot.Reply(wizard, Character, ArenaRules.Hash(Config.RankedTournament), Config, store)!;
        Assert.Equal(Character, reply.CharacterID);
        var native = ArenaMessages.Read<Ladder>(reply.LadderData)!;
        Assert.Equal(Character, native.m_characterID.Full);
        Assert.Equal(ArenaRules.Hash(Config.RankedTournament), native.m_gameNameID);
        Assert.Equal(973, native.m_score);
        Assert.Equal(28, native.m_gamesWon);
        Assert.Equal(13, native.m_gamesLost);
        Assert.Equal(0, native.m_gamesTied);
        Assert.Equal(41, native.m_gamesPlayed);
        Assert.Equal(0u, reply.TourneyCredits);
        Assert.Equal(0u, reply.TourneyHostingCredits);
    }

    [Fact]
    public void AChangedEarnedRankReplacesTheOldNativeGameStatsCache() {
        var store = new ArenaLadderCollection.Memory();
        var wizard = Wizard();
        ArenaShopSnapshot.ReadOwn(wizard, Config, store, _ => Wizard());
        var first = wizard.GameStats.m_pArenaLadder;
        store.Save(new ArenaLadderEntry { CharId = Character, Rating = 950, Wins = 24, Losses = 8 });
        var snapshot = ArenaShopSnapshot.ReadOwn(wizard, Config, store, _ => Wizard())!;
        Assert.NotSame(first, snapshot.Ladder);
        Assert.Same(snapshot.Ladder, wizard.GameStats.m_pArenaLadder);
        Assert.Equal(950, wizard.GameStats.GetClientTypeAlternative().m_pArenaLadder.m_score);
        Assert.Equal(24, snapshot.Ladder.m_gamesWon);
        Assert.Equal(8, snapshot.Ladder.m_gamesLost);
    }

    [Fact]
    public void AttachGameStatsRoundTripContainsTheSavedNativeLadderAndWallet() {
        var store = new ArenaLadderCollection.Memory();
        store.Save(new ArenaLadderEntry { CharId = Character, Rating = 701, Wins = 12, Losses = 4 });
        var wizard = Wizard(1, 2);
        ArenaShopSnapshot.ReadOwn(wizard, Config, store, _ => Wizard(44, 33));
        // WizardObjectLoader.SetWizardGameStats uses this same alternative in the native attach object (mask 0x18).
        var native = ArenaMessages.Read<WizGameStats>(ArenaMessages.Blob(wizard.GameStats.GetClientTypeAlternative()))!;
        Assert.Equal(44, native.m_currentArenaPoints);
        Assert.Equal(33, native.m_currentPvPCurrency);
        Assert.Equal(Character, native.m_pArenaLadder.m_characterID.Full);
        Assert.Equal(ArenaRules.Hash(Config.RankedTournament), native.m_pArenaLadder.m_gameNameID);
        Assert.Equal(701, native.m_pArenaLadder.m_score);
        Assert.Equal(12, native.m_pArenaLadder.m_gamesWon);
        Assert.Equal(4, native.m_pArenaLadder.m_gamesLost);
        Assert.Equal(16, native.m_pArenaLadder.m_gamesPlayed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(771923)]
    public void LadderRequestCannotDiscloseAnotherCharacterOrProbeTheDatabase(ulong requested) {
        var wizard = Wizard();
        var untouched = new Ladder { m_score = 500 };
        wizard.GameStats.m_pArenaLadder = untouched;
        Assert.Null(ArenaShopSnapshot.Reply(wizard, requested, ArenaRules.Hash(Config.RankedTournament), Config, new UnreadableLadder()));
        Assert.Same(untouched, wizard.GameStats.m_pArenaLadder);
    }

    [Theory]
    [InlineData("PvPPractice")]
    [InlineData("PvPPetRace")]
    [InlineData("PvPDecathlon")]
    public void UnsupportedLaddersDoNotInventRankOrReadRankedRecords(string name) {
        Assert.Null(ArenaShopSnapshot.Reply(Wizard(), Character, ArenaRules.Hash(name), Config, new UnreadableLadder()));
    }

    [Fact]
    public void ShopRefreshPublishesBothSavedWalletAliasesWithoutSavingOrChangingOtherStats() {
        var wizard = Wizard(1, 2);
        wizard.GameStats.m_currentHitpoints = 75;
        var saved = Wizard(183, 71);
        saved.GameStats.m_currentHitpoints = 20;
        var loads = 0;
        var snapshot = ArenaShopSnapshot.ReadOwn(wizard, Config, new ArenaLadderCollection.Memory(), id => {
            loads++;
            Assert.Equal(Character, id);
            Assert.True(WizardCollection.HoldsWriteLane);
            return saved;
        })!;
        Assert.Equal(1, loads);
        Assert.Equal(183, snapshot.ArenaPoints);
        Assert.Equal(71, snapshot.PvpCurrency);
        Assert.Equal(183, wizard.GameStats.m_currentArenaPoints);
        Assert.Equal(71, wizard.GameStats.m_currentPvPCurrency);
        Assert.Equal(75, wizard.GameStats.m_currentHitpoints);
        Assert.Equal(20, saved.GameStats.m_currentHitpoints);
        Assert.Null(saved.GameStats.m_pArenaLadder);
    }

    [Fact]
    public void ZeroSavedTicketsRemainZeroWithoutAFreeGrant() {
        var wizard = Wizard(123, 456);
        var snapshot = ArenaShopSnapshot.ReadOwn(wizard, Config, new ArenaLadderCollection.Memory(), _ => Wizard(0, 0))!;
        Assert.Equal(0, snapshot.ArenaPoints);
        Assert.Equal(0, snapshot.PvpCurrency);
        Assert.Equal(0, wizard.GameStats.m_currentArenaPoints);
        Assert.Equal(0, wizard.GameStats.m_currentPvPCurrency);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongSavedCharacterOrAccountCannotPublishItsWallet(bool wrongCharacter) {
        var wizard = Wizard(5, 6);
        var saved = Wizard(999, 888);
        if (wrongCharacter) saved.CharId++; else saved.AccountId++;
        Assert.Null(ArenaShopSnapshot.ReadOwn(wizard, Config, new UnreadableLadder(), _ => saved));
        Assert.Equal(5, wizard.GameStats.m_currentArenaPoints);
        Assert.Equal(6, wizard.GameStats.m_currentPvPCurrency);
        Assert.Null(wizard.GameStats.m_pArenaLadder);
    }

    [Fact]
    public void MissingSavedWizardDoesNotPublishAStaleWalletOrInventALadder() {
        var wizard = Wizard(5, 6);
        Assert.Null(ArenaShopSnapshot.ReadOwn(wizard, Config, new UnreadableLadder(), _ => null));
        Assert.Equal(5, wizard.GameStats.m_currentArenaPoints);
        Assert.Equal(6, wizard.GameStats.m_currentPvPCurrency);
        Assert.Null(wizard.GameStats.m_pArenaLadder);
    }

    [Fact]
    public void MismatchedLadderRecordFailsWithoutPublishingAnyWalletOrRank() {
        var wizard = Wizard(5, 6);
        Assert.Throws<InvalidOperationException>(() => ArenaShopSnapshot.ReadOwn(wizard, Config,
            new WrongLadder(), _ => Wizard(100, 200)));
        Assert.Equal(5, wizard.GameStats.m_currentArenaPoints);
        Assert.Equal(6, wizard.GameStats.m_currentPvPCurrency);
        Assert.Null(wizard.GameStats.m_pArenaLadder);
    }

    private static Wizard Wizard(int tickets = 0, int pvpCurrency = 0) => new() {
        CharId = Character, AccountId = Account,
        GameStats = new ServerWizGameStats(default, 1) { m_currentArenaPoints = tickets, m_currentPvPCurrency = pvpCurrency },
    };

    private sealed class UnreadableLadder : IArenaLadderStore {
        public ArenaLadderEntry? Load(ulong id) => throw new InvalidOperationException("Must not read an unauthorized ladder.");
        public void Save(ArenaLadderEntry entry) => throw new InvalidOperationException("Read-only snapshot must not save.");
        public void SaveMany(System.Collections.Generic.IReadOnlyCollection<ArenaLadderEntry> entries) => throw new InvalidOperationException("Read-only snapshot must not save.");
    }

    private sealed class WrongLadder : IArenaLadderStore {
        public ArenaLadderEntry? Load(ulong id) => new() { CharId = Character + 1, Rating = 1000 };
        public void Save(ArenaLadderEntry entry) => throw new InvalidOperationException("Read-only snapshot must not save.");
        public void SaveMany(System.Collections.Generic.IReadOnlyCollection<ArenaLadderEntry> entries) => throw new InvalidOperationException("Read-only snapshot must not save.");
    }
}
