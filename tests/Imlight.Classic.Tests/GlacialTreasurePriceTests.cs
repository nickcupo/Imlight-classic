// CLASSIC: bounded historical base correction and persisted quote acknowledgement semantics.

using System;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic.Bazaar;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class GlacialTreasurePriceTests : IDisposable {
    private readonly FieldInfo _prices = typeof(ClassicProgression).GetField("s_treasurePrices", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousPrices;
    private readonly FieldInfo _overrides = typeof(ClassicSpellTemplates).GetField("s_overrides", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousOverrides;

    public GlacialTreasurePriceTests() {
        _previousPrices = _prices.GetValue(null);
        _previousOverrides = _overrides.GetValue(null);
        var path = Path.Combine(Path.GetTempPath(), $"glacial-prices-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}glacial-prices-tests.log\n");
        ConfigurationManager.Initialize(path);
    }

    public void Dispose() {
        _prices.SetValue(null, _previousPrices);
        _overrides.SetValue(null, _previousOverrides);
        ClassicRuntime.ResetForTests();
    }

    private static TreasurePrices Prices()
        => TreasurePricesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "treasure-prices-2009.yaml"));

    private static BazaarRules Bazaar()
        => BazaarRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-2009.yaml"));

    private static SpellTemplate Card(int cost = 350, string name = ClassicSpellTemplates.GlacialTreasureName)
        => new() { m_name = name, m_baseCost = cost, m_spellRank = new SpellRank { m_spellRank = 0 }, m_accuracy = 100,
            m_sMagicSchoolName = "Fire", m_effects = [Shield("Storm"), Shield("Ice")] };

    private static SpellEffect Shield(string school) => new() {
        m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = -70, m_sDamageType = school,
        m_effectTarget = kEffectTarget.kFriendlySingle,
    };

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("october-2010-arc1")]
    public void PriceAppliesBeforeAbsentOrUnchangedCombatPlanReturns(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
        _prices.SetValue(null, Prices());
        _overrides.SetValue(null, null);
        var spell = Card();
        var effects = spell.m_effects;
        ClassicSpellTemplates.Apply(spell, ClassicSpellTemplates.GlacialTreasurePath);
        Assert.Equal(175, spell.m_baseCost);
        Assert.Equal(350, TreasurePrices.ClientPrice(spell.m_baseCost, 2));
        Assert.Equal(0, spell.m_spellRank.m_spellRank);
        Assert.Equal(100, spell.m_accuracy);
        Assert.Same(effects, spell.m_effects);

        // The actual shield record's combat values already agree with this card. Price correction must
        // survive the earlier Find/ChangesTemplate returns in both the census and on-demand passes.
        ClassicSpellTemplates.Initialize(ClassicDataFixture.LoadProfile(profile), Path.Combine(ClassicDataFixture.Root, "spells"), true, null);
        var plan = ((ClassicSpellOverrides) _overrides.GetValue(null)!).PlanFor(SpellTemplateEditor.ShapeOf(spell, ClassicSpellTemplates.GlacialTreasurePath));
        Assert.NotNull(plan);
        Assert.False(plan.ChangesTemplate);
        spell.m_baseCost = 350;
        ClassicSpellTemplates.Apply(spell, ClassicSpellTemplates.GlacialTreasurePath);
        Assert.Equal(175, spell.m_baseCost);
        ClassicSpellTemplates.Apply(spell, ClassicSpellTemplates.GlacialTreasurePath, census: true);
        Assert.Equal(175, spell.m_baseCost);
    }

    [Theory]
    [InlineData("dev-unrestricted", ClassicSpellTemplates.GlacialTreasurePath, "Glacial Shield TC", 350)]
    [InlineData("future", ClassicSpellTemplates.GlacialTreasurePath, "Glacial Shield TC", 350)]
    [InlineData("late-2009", "Spells/Glacial Shield.xml", "Glacial Shield", 350)]
    [InlineData("late-2009", "Spells/TreasureCards/Glacial Shield Item.xml", "Glacial Shield TC", 350)]
    [InlineData("late-2009", ClassicSpellTemplates.GlacialTreasurePath, "Glacial Shield Item", 350)]
    [InlineData("late-2009", ClassicSpellTemplates.GlacialTreasurePath, "glacial shield tc", 350)]
    [InlineData("late-2009", ClassicSpellTemplates.GlacialTreasurePath, "Glacial Shield TC", 351)]
    [InlineData("late-2009", ClassicSpellTemplates.GlacialTreasurePath, "Glacial Shield TC", -1)]
    public void UnknownProfileVariantsAndUnexpectedBasesRetainNativeValues(string profile, string path, string name, int cost) {
        var spell = Card(cost, name);
        Assert.False(ClassicSpellTemplates.ApplyTreasureBaseCost(spell, path, profile, Prices()));
        Assert.Equal(cost, spell.m_baseCost);
    }

    [Fact]
    public void UninitializedStockAndUnrestrictedRuntimeKeepNativeBaseEvenWithAStaleLoadedTable() {
        _prices.SetValue(null, Prices());
        _overrides.SetValue(null, null);
        foreach (var mode in new[] { "uninitialized", "stock", "dev-unrestricted" }) {
            ClassicRuntime.ResetForTests();
            if (mode == "stock") { ClassicRuntime.Initialize(ClassicRules.Stock); }
            if (mode == "dev-unrestricted") { ClassicRuntime.Initialize(ClassicDataFixture.RealRules(mode)); }
            var spell = Card();
            ClassicSpellTemplates.Apply(spell, ClassicSpellTemplates.GlacialTreasurePath);
            Assert.Equal(350, spell.m_baseCost);
        }
    }

    [Fact]
    public void OnlyCanonicalLibrary350RecordEnablesProjectionAndItIsIdempotent() {
        var prices = Prices();
        var wrongPrice = new TreasurePrices { Id = prices.Id, Profiles = prices.Profiles, SourceFile = prices.SourceFile,
            ByName = new[] { new System.Collections.Generic.KeyValuePair<string, int>("Glacial Shield", 351) }.ToFrozenDictionary() };
        foreach (var table in new[] { null, wrongPrice }) {
            var unchanged = Card();
            Assert.False(ClassicSpellTemplates.ApplyTreasureBaseCost(unchanged, ClassicSpellTemplates.GlacialTreasurePath, "late-2009", table));
            Assert.Equal(350, unchanged.m_baseCost);
        }
        var corrected = Card();
        Assert.True(ClassicSpellTemplates.ApplyTreasureBaseCost(corrected, ClassicSpellTemplates.GlacialTreasurePath, "late-2009", prices));
        Assert.True(ClassicSpellTemplates.ApplyTreasureBaseCost(corrected, ClassicSpellTemplates.GlacialTreasurePath, "late-2009", prices));
        Assert.Equal(175, ClassicBazaar.BaseCostOf(corrected));
    }

    [Theory]
    [InlineData(1, 0.9)]
    [InlineData(7, 1.0)]
    [InlineData(30, 1.1)]
    public void ExistingLotQuoteUsesHistoricalBaseAndRetainsItsFactorCopiesAndIdentity(int copies, double factor) {
        var rules = Bazaar();
        Assert.True(ClassicBazaar.TryGlacialTreasureQuote("late-2009", Prices(), ClassicSpellTemplates.GlacialTreasureId,
            Card(175), ClassicSpellTemplates.GlacialTreasurePath, copies, rules, factor, out var quote));
        Assert.Equal(rules.BuyPrice(175, copies, BazaarKind.TreasureCard, factor), quote.Buy);
        Assert.Equal(rules.SellPrice(175, copies, BazaarKind.TreasureCard), quote.Sell);
        var stored = Lot(copies, rules.BuyPrice(350, copies, BazaarKind.TreasureCard, factor), rules.SellPrice(350, copies, BazaarKind.TreasureCard));
        var cached = Lot(copies, stored.m_buyPrice, stored.m_sellPrice);
        var saves = 0;
        Assert.True(AuctionHouseQuoteCorrection.Apply([stored], [cached], ClassicSpellTemplates.GlacialTreasureId, copies, quote.Buy, quote.Sell, () => saves++));
        Assert.False(AuctionHouseQuoteCorrection.Apply([stored], [cached], ClassicSpellTemplates.GlacialTreasureId, copies, quote.Buy, quote.Sell, () => saves++));
        Assert.Equal(1, saves);
        Assert.Equal(copies, stored.m_numForSale);
        Assert.Equal(copies, cached.m_numForSale);
        Assert.Equal(ClassicSpellTemplates.GlacialTreasureId, stored.m_templateID.Full);
        Assert.Equal(quote, (cached.m_buyPrice, cached.m_sellPrice));
    }

    [Fact]
    public void StartupUsesExistingDefaultWhenPriorProcessFactorWasNotPersisted() {
        var factors = (System.Collections.Generic.Dictionary<ulong, double>) typeof(ClassicBazaar)
            .GetField("s_priceFactors", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var before = factors.ToArray();
        try {
            factors.Clear();
            var prior = Lot(7, Bazaar().BuyPrice(350, 7, BazaarKind.TreasureCard, 0.9), Bazaar().SellPrice(350, 7, BazaarKind.TreasureCard));
            var current = ClassicBazaar.CurrentGlacialTreasureFactor();
            Assert.Equal((1.0, true), current); // no inverse reconstruction from the prior rounded quote
            Assert.True(ClassicBazaar.TryGlacialTreasureQuote("late-2009", Prices(), ClassicSpellTemplates.GlacialTreasureId,
                Card(175), ClassicSpellTemplates.GlacialTreasurePath, 7, Bazaar(), current.Factor, out var quote));
            Assert.Equal(Bazaar().BuyPrice(175, 7, BazaarKind.TreasureCard), quote.Buy);
            Assert.NotEqual(Bazaar().BuyPrice(175, 7, BazaarKind.TreasureCard, 0.9), quote.Buy);
            Assert.True(AuctionHouseQuoteCorrection.Apply([prior], [], ClassicSpellTemplates.GlacialTreasureId, 7, quote.Buy, quote.Sell, () => {}));
            Assert.Equal(7, prior.m_numForSale);
            Assert.Empty(factors); // correction does not make up or persist a new factor

            factors.Add(ClassicSpellTemplates.GlacialTreasureId, 0.9);
            Assert.Equal((0.9, false), ClassicBazaar.CurrentGlacialTreasureFactor());
            Assert.Equal(0.9, factors[ClassicSpellTemplates.GlacialTreasureId]);
        }
        finally {
            factors.Clear();
            foreach (var pair in before) { factors.Add(pair.Key, pair.Value); }
        }
    }

    [Fact]
    public void RepriceRefusesUnknownTemplateBaseProfileAndInvalidFactor() {
        foreach (var test in new[] {
            ("dev-unrestricted", ClassicSpellTemplates.GlacialTreasureId, Card(175), 1.0),
            ("late-2009", 1ul, Card(175), 1.0),
            ("late-2009", ClassicSpellTemplates.GlacialTreasureId, Card(350), 1.0),
            ("late-2009", ClassicSpellTemplates.GlacialTreasureId, Card(176), 1.0),
            ("late-2009", ClassicSpellTemplates.GlacialTreasureId, Card(175, "Fire Shield TC"), 1.0),
            ("late-2009", ClassicSpellTemplates.GlacialTreasureId, Card(175), double.NaN),
        }) {
            Assert.False(ClassicBazaar.TryGlacialTreasureQuote(test.Item1, Prices(), test.Item2, test.Item3,
                ClassicSpellTemplates.GlacialTreasurePath, 7, Bazaar(), test.Item4, out _));
        }
    }

    [Fact]
    public void FailedSaveKeepsCacheAndInSessionQuoteAndRereadHandlesUnknownAcknowledgement() {
        var stored = Lot(7, 473, 140);
        var cached = Lot(7, 473, 140);
        Assert.Throws<IOException>(() => AuctionHouseQuoteCorrection.Apply([stored], [cached], ClassicSpellTemplates.GlacialTreasureId,
            7, 237, 70, () => throw new IOException("save acknowledgement lost")));
        Assert.Equal((473, 140), (stored.m_buyPrice, stored.m_sellPrice));
        Assert.Equal((473, 140), (cached.m_buyPrice, cached.m_sellPrice));
        // A new session may discover that the remote commit succeeded. It synchronizes cache without another save.
        var reread = Lot(7, 237, 70);
        Assert.False(AuctionHouseQuoteCorrection.Apply([reread], [cached], ClassicSpellTemplates.GlacialTreasureId, 7, 237, 70,
            () => Assert.Fail("an already persisted quote must not save again")));
        Assert.Equal((237, 70), (cached.m_buyPrice, cached.m_sellPrice));
        Assert.Equal(7, cached.m_numForSale);
    }

    [Fact]
    public void QuoteOnlyUpdateRetainsDuplicateLotsAndRefusesDifferentPersistedStock() {
        var first = Lot(2, 473, 140);
        var second = Lot(5, 473, 140);
        var unrelated = new AuctionHouseEntry { m_templateID = (GID) 1ul, m_numForSale = 9, m_buyPrice = 600, m_sellPrice = 90 };
        Assert.Throws<InvalidOperationException>(() => AuctionHouseQuoteCorrection.Apply([first, second], [first, second, unrelated],
            ClassicSpellTemplates.GlacialTreasureId, 8, 237, 70, () => Assert.Fail("copy mismatch must not save")));
        Assert.Throws<InvalidOperationException>(() => AuctionHouseQuoteCorrection.Apply([unrelated], [unrelated],
            ClassicSpellTemplates.GlacialTreasureId, 9, 237, 70, () => Assert.Fail("identity mismatch must not save")));
        Assert.True(AuctionHouseQuoteCorrection.Apply([first, second], [first, second, unrelated],
            ClassicSpellTemplates.GlacialTreasureId, 7, 237, 70, () => {}));
        Assert.Equal(new[] { 2, 5, 9 }, new[] { first, second, unrelated }.Select(entry => entry.m_numForSale));
        Assert.Equal((600, 90), (unrelated.m_buyPrice, unrelated.m_sellPrice));
    }

    private static AuctionHouseEntry Lot(int copies, int buy, int sell)
        => new() { m_templateID = (GID) ClassicSpellTemplates.GlacialTreasureId, m_numForSale = copies, m_buyPrice = buy, m_sellPrice = sell };
}
