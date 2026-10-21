// CLASSIC OWNER EXTRA: School Weaving (design: ~/w101c-private/build/playbot-reports/design-weaving.md). Off by default;
// the dated profiles must be untouched while experiments.weaving is off.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SchoolWeavingTests : IDisposable {
    // Fixture template ids (far above any client id).
    private const uint WeaveCard = 4294965000, Kraken = WeaveCard + 1, Face = WeaveCard + 2, FireCard = WeaveCard + 3, Blizzard = WeaveCard + 4;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly Func<string, ulong?> _resolve = ClassicWeaving.ResolveTemplate;

    public SchoolWeavingTests() {
        var ini = Path.GetTempFileName();
        try {
            File.WriteAllText(ini, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}imlight-weaving-tests.log\n");
            ConfigurationManager.Initialize(ini);
        } finally { File.Delete(ini); }
        _cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    public void Dispose() {
        foreach (var id in new[] { WeaveCard, Kraken, Face, FireCard, Blizzard }) _cache.Remove(id);
        ClassicWeaving.ResolveTemplate = _resolve;
        ClassicProgression.SetWeavingForTests(WeavingRules.Empty);
        ClassicRuntime.ResetForTests();
    }

    private static WeavingRules Real() => WeavingRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "weaving-classic.yaml"));

    // ---------------------------------------------------------------- data and switch

    [Fact]
    public void RealTableHasSevenSchoolsAndFortySixWeaves() {
        var rules = Real();
        Assert.Equal(WeavingRules.Schools, rules.Weaves.Select(weave => weave.School));
        Assert.Equal(46, rules.Weaves.Sum(weave => weave.Inputs.Length));
        Assert.Equal(7, rules.Riders.Length);
        Assert.Equal(20, rules.Level);
        Assert.Equal(2, rules.DeckCopies);
        Assert.All(rules.Weaves, weave => Assert.NotEmpty(weave.Trainers));
        Assert.Equal(new ulong[] { 81102, 82209 }, rules.Weaves.Single(weave => weave.School == "death").Trainers);
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("october-2010-arc1")]
    [InlineData("celestia-2010")]
    [InlineData("wintertusk-2011")]
    public void EveryDatedProfileKeepsWeavingOff(string profile) {
        var rules = ClassicDataFixture.RealRules(profile);
        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.ExperimentsWeaving));
        Assert.Equal("rules/weaving-classic.yaml", rules.Profile.Rules.Weaving);
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(rules);
        ClassicProgression.SetWeavingForTests(Real());
        Assert.False(ClassicWeaving.Active);
    }

    [Fact]
    public void AnExperimentIsOffUnlessSetTrueUnlikeADatedSwitch() {
        Assert.False(new FeatureSwitches(new Dictionary<string, bool>()).IsEnabled(ClassicFeatures.ExperimentsWeaving));
        Assert.True(new FeatureSwitches(new Dictionary<string, bool>()).IsEnabled(ClassicFeatures.Housing));
        Assert.True(new FeatureSwitches(new Dictionary<string, bool> { [ClassicFeatures.ExperimentsWeaving] = true }).IsEnabled(ClassicFeatures.ExperimentsWeaving));
        Assert.True(ClassicFeatures.IsExperiment(ClassicFeatures.ExperimentsWeaving));
        Assert.False(ClassicFeatures.IsExperiment(ClassicFeatures.PetsEnergy));
    }

    [Fact]
    public void AProfileMayTurnTheExperimentOnAndUnknownExperimentsAreRefused() {
        using var data = new TempClassicData();
        data.WriteProfile("weave-on", "id: weave-on\ntitle: Weave on\nstatus: debug\ncutoff: 2010-10-31\nfeatures:\n  experiments:\n    weaving: true\n");
        Assert.True(ClassicProfileLoader.Load(data.ProfilesPath, "weave-on").Features.IsEnabled(ClassicFeatures.ExperimentsWeaving));
        data.WriteProfile("weave-bad", "id: weave-bad\ntitle: Weave bad\nstatus: debug\ncutoff: 2010-10-31\nfeatures:\n  experiments:\n    hybrid_schools: true\n");
        Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(data.ProfilesPath, "weave-bad"));
    }

    // ---------------------------------------------------------------- riders (pure)

    private static SpellEffect Hit(kSpellEffects kind, int amount, string school = "Storm", kEffectTarget target = kEffectTarget.kEnemySingle)
        => new() { m_effectType = kind, m_effectParam = amount, m_sDamageType = school, m_effectTarget = target };

    // Kraken as the client has it: a random roll 520..580 (average 550), Storm, 4 pips, 70%.
    private static SpellTemplate KrakenTemplate() => new() {
        m_name = "Kraken", m_sMagicSchoolName = "Storm", m_sTypeName = "Damage", m_accuracy = 70,
        m_spellRank = new SpellRank { m_spellRank = 4 },
        m_effects = [new RandomSpellEffect { m_effectTarget = kEffectTarget.kEnemySingle,
            m_effectList = [.. new[] { 520, 535, 550, 565, 580 }.Select(n => Hit(kSpellEffects.kDamage, n))] }],
    };

    private static SpellTemplate Woven(string school) {
        var rider = Real().RiderFor(school)!;
        Assert.True(ClassicWeaving.TryBuildCast(KrakenTemplate(), rider, out var built, out var why), why);
        Assert.Equal(ClassicWeaving.SchoolName(school), built.m_sMagicSchoolName);
        return built;
    }

    private static IEnumerable<SpellEffect> Leaves(SpellEffect effect)
        => effect is RandomSpellEffect random ? random.m_effectList.SelectMany(Leaves) : [effect];

    [Fact]
    public void FireKindleHitsSixtyPercentAndBurnsSixtyPercentOverThreeRounds() {
        var built = Woven("fire");
        Assert.Equal(new[] { 312, 321, 330, 339, 348 }, Leaves(built.m_effects[0]).Select(e => e.m_effectParam));
        Assert.All(Leaves(built.m_effects[0]), e => Assert.Equal("Fire", e.m_sDamageType));
        var burn = Assert.Single(built.m_effects, e => e.m_effectType == kSpellEffects.kDamageOverTime);
        Assert.Equal((330, 3, "Fire"), (burn.m_effectParam, burn.m_numRounds, burn.m_sDamageType));
    }

    [Fact]
    public void IceRimeHitsNinetyPercentAndShieldsTheCaster() {
        var built = Woven("ice");
        Assert.Equal(495, Leaves(built.m_effects[0]).ElementAt(2).m_effectParam);
        var absorb = Assert.Single(built.m_effects, e => e.m_effectType == kSpellEffects.kAbsorbDamage);
        Assert.Equal((138, kEffectTarget.kSelf), (absorb.m_effectParam, absorb.m_effectTarget));
    }

    [Fact]
    public void StormSurgeHitsHarderAndTheHandCardLosesAccuracy() {
        var built = Woven("storm");
        Assert.Equal(715, Leaves(built.m_effects[0]).ElementAt(2).m_effectParam);
        Assert.Equal(10, Real().RiderFor("storm")!.AccuracyPenalty);
    }

    [Fact]
    public void MythEchoHitsTwiceSoTheFirstHitBreaksAShield() {
        var built = Woven("myth");
        Assert.Equal(kSpellEffects.kDamage, built.m_effects[0].m_effectType);
        Assert.Equal(165, built.m_effects[0].m_effectParam);
        Assert.Equal(440, Leaves(built.m_effects[1]).ElementAt(2).m_effectParam);
    }

    [Fact]
    public void LifeBloomHitsEightyPercentAndHealsTheCasterOverTime() {
        var built = Woven("life");
        var bloom = Assert.Single(built.m_effects, e => e.m_effectType == kSpellEffects.kHealOverTime);
        Assert.Equal((220, 3, kEffectTarget.kSelf), (bloom.m_effectParam, bloom.m_numRounds, bloom.m_effectTarget));
    }

    [Fact]
    public void DeathHungerTurnsEveryHitIntoAHalfDrain() {
        var built = Woven("death");
        Assert.All(Leaves(built.m_effects[0]), e => {
            Assert.Equal(kSpellEffects.kStealHealth, e.m_effectType);
            Assert.Equal(0.5f, e.m_healModifier);
        });
        Assert.Equal(413, Leaves(built.m_effects[0]).ElementAt(2).m_effectParam);
    }

    [Fact]
    public void BalanceTiltHitsEightyPercentAndTrapsTheTargetForAnySchool() {
        var built = Woven("balance");
        var trap = Assert.Single(built.m_effects, e => e.m_effectType == kSpellEffects.kModifyIncomingDamage);
        Assert.Equal((25, "All", kEffectTarget.kEnemySingle), (trap.m_effectParam, trap.m_sDamageType, trap.m_effectTarget));
    }

    [Fact]
    public void OnlyDeathKeepsADrainAsADrain() {
        var vampire = new SpellTemplate { m_sMagicSchoolName = "Death", m_spellRank = new SpellRank { m_spellRank = 4 },
            m_effects = [Hit(kSpellEffects.kStealHealth, 350, "Death")] };
        Assert.True(ClassicWeaving.TryBuildCast(vampire, Real().RiderFor("myth")!, out var built, out _));
        Assert.Equal(kSpellEffects.kDamage, built.m_effects[1].m_effectType);
        Assert.Equal(280, built.m_effects[1].m_effectParam);
    }

    [Fact]
    public void AreaHealAndXPipCardsAreNeverWoven() {
        var rider = Real().RiderFor("fire")!;
        var blizzard = new SpellTemplate { m_effects = [Hit(kSpellEffects.kDamage, 300, "Ice", kEffectTarget.kEnemyTeam)] };
        Assert.False(ClassicWeaving.TryBuildCast(blizzard, rider, out _, out var why1));
        Assert.Contains("single-target", why1);
        var heal = new SpellTemplate { m_effects = [Hit(kSpellEffects.kHeal, 300, "Life", kEffectTarget.kFriendlySingle)] };
        Assert.False(ClassicWeaving.TryBuildCast(heal, rider, out _, out var why2));
        Assert.Contains("damage", why2);
        var xpip = new SpellTemplate { m_effects = [new VariableSpellEffect { m_effectList = [Hit(kSpellEffects.kDamageOverTime, 120, "Fire")] }] };
        Assert.False(ClassicWeaving.TryBuildCast(xpip, rider, out _, out var why3));
        Assert.Contains("X-pip", why3);
    }

    // ---------------------------------------------------------------- the hand weave, end to end

    private void TurnOn(bool on = true, bool pvp = true) {
        ClassicRuntime.ResetForTests();
        var rules = ClassicDataFixture.RealRules("late-2009");
        if (on) rules.OwnerExtraFeatures = () => new HashSet<string> { ClassicFeatures.ExperimentsWeaving };
        ClassicRuntime.Initialize(rules);
        var real = Real();
        var fire = real.Weaves.Single(weave => weave.School == "fire") with {
            Card = "Spells/Weave Fire.xml", Inputs = [new WeaveInput("Spells/Kraken.xml", "Spells/Inferno Kraken.xml")] };
        ClassicProgression.SetWeavingForTests(real with { Pvp = pvp, Weaves = [fire, .. real.Weaves.Where(weave => weave.School != "fire")] });
        ClassicWeaving.ResolveTemplate = path => path switch {
            "Spells/Weave Fire.xml" => WeaveCard, "Spells/Kraken.xml" => Kraken, "Spells/Inferno Kraken.xml" => Face, _ => null };
        _cache[WeaveCard] = new SpellTemplate { m_name = "Weave Fire", m_sMagicSchoolName = "Fire", m_sTypeName = "Mutate", m_accuracy = 100,
            m_spellRank = new SpellRank { m_spellRank = 0 },
            m_effects = [new SpellEffect { m_effectType = kSpellEffects.kModifyCardMutation, m_effectParam = unchecked((int) Face), m_effectTarget = kEffectTarget.kSpecificSpells }] };
        _cache[Kraken] = KrakenTemplate();
        _cache[Face] = new SpellTemplate { m_name = "Kraken - Mutate - Fire", m_sMagicSchoolName = "Fire", m_sTypeName = "Damage", m_accuracy = 75,
            m_spellRank = new SpellRank { m_spellRank = 4 }, m_effects = [Hit(kSpellEffects.kDamage, 245, "Fire")] };
        _cache[FireCard] = new SpellTemplate { m_name = "Fire Cat", m_sMagicSchoolName = "Fire", m_accuracy = 75,
            m_spellRank = new SpellRank { m_spellRank = 1 }, m_effects = [Hit(kSpellEffects.kDamage, 100, "Fire")] };
    }

    [Fact]
    public void AWeaveCardTurnsKrakenIntoAFireCardThatCastsTheRider() {
        TurnOn();
        Assert.True(ClassicWeaving.Active);
        var source = SpellFactory.GetSpell(WeaveCard);
        var target = SpellFactory.GetSpell(Kraken);
        Assert.True(ClassicHandEnchantment.TryPrepare(source, target, out var woven, out var cast));
        Assert.Equal(Face, woven.m_templateID);                       // the client draws Inferno Kraken
        Assert.Equal(Kraken, woven.m_premutationSpellID);
        Assert.Equal(WeaveCard, woven.m_enchantment);
        Assert.Equal(4, woven.m_pipCost.m_spellRank);                 // Kraken's cost, not the face's
        Assert.Equal(70, woven.m_accuracy);                           // Kraken's accuracy (Fire has no penalty)
        Assert.Equal("Fire", cast.m_sMagicSchoolName);
        Assert.Contains(cast.m_effects, e => e.m_effectType == kSpellEffects.kDamageOverTime && e.m_effectParam == 330);
        Assert.Equal(520, ((RandomSpellEffect) ((SpellTemplate) _cache[Kraken]).m_effects[0]).m_effectList[0].m_effectParam); // template untouched
    }

    [Fact]
    public void NothingIsWovenWhileTheExperimentIsOffInPvpWhenBarredOrOnAnUnlistedCard() {
        TurnOn(on: false);
        Assert.False(ClassicHandEnchantment.TryPrepare(SpellFactory.GetSpell(WeaveCard), SpellFactory.GetSpell(Kraken), out _, out _));
        TurnOn(pvp: false);
        Assert.False(ClassicHandEnchantment.TryPrepare(SpellFactory.GetSpell(WeaveCard), SpellFactory.GetSpell(Kraken), out _, out _, pvp: true));
        Assert.True(ClassicHandEnchantment.TryPrepare(SpellFactory.GetSpell(WeaveCard), SpellFactory.GetSpell(Kraken), out _, out _, pvp: false));
        TurnOn();
        Assert.False(ClassicHandEnchantment.TryPrepare(SpellFactory.GetSpell(WeaveCard), SpellFactory.GetSpell(FireCard), out _, out _));
        var once = SpellFactory.GetSpell(Kraken);
        Assert.True(ClassicHandEnchantment.TryPrepare(SpellFactory.GetSpell(WeaveCard), once, out var woven, out _));
        Assert.False(ClassicHandEnchantment.TryPrepare(SpellFactory.GetSpell(WeaveCard), woven, out _, out _)); // one weave per card
    }

    [Fact]
    public void ProfessorsTeachTheWeaveCardOnlyWhileWeavingIsOnAndOnlyToTheirSchool() {
        TurnOn();
        Assert.True(ClassicWeaving.IsWeaveCard("Spells/Weave Fire.xml"));
        Assert.True(ClassicSpellTemplates.IsTrainable("Spells/Weave Fire.xml"));
        Assert.True(ClassicWeaving.MayTrain("Fire", WeaveCard));
        Assert.False(ClassicWeaving.MayTrain("Storm", WeaveCard));
        Assert.True(ClassicWeaving.MayTrain("Storm", Kraken));              // any other card is untouched
        TurnOn(on: false);
        Assert.False(ClassicWeaving.IsWeaveCard("Spells/Weave Fire.xml"));
        Assert.True(ClassicWeaving.MayTrain("Storm", WeaveCard));
    }

    [Fact]
    public void AWeaveCardGoesOnlyInItsOwnSchoolsDeckAndAtMostTwice() {
        TurnOn();
        SpellTemplate? Lookup(uint id) => _cache.TryGetValue(id, out var t) ? t as SpellTemplate : null;
        var fireDeck = new DeckBehaviorTemplate { m_primarySchoolName = "Fire", m_maxSpells = 64, m_maxTreasureCards = 20,
            m_schoolMaxInstances = 4, m_genericMaxInstances = 4, m_schoolMaxRank = -1, m_genericMaxRank = -1 };
        var stormDeck = fireDeck with { m_primarySchoolName = "Storm" };
        Assert.Equal(DeckAddRefusal.None, ClassicDeckRules.CanAdd(fireDeck, [], WeaveCard, Lookup));
        var two = new List<SpellData> { new() { m_templateID = WeaveCard, m_quantity = 2 } };
        Assert.Equal(DeckAddRefusal.TooManyCopies, ClassicDeckRules.CanAdd(fireDeck, two, WeaveCard, Lookup));
        Assert.NotEqual(DeckAddRefusal.None, ClassicDeckRules.CanAdd(stormDeck, [], WeaveCard, Lookup));
        Assert.Equal(DeckAddRefusal.None, ClassicDeckRules.CanAdd(fireDeck with { m_primarySchoolName = "" }, [], WeaveCard, Lookup)); // starter deck
        TurnOn(on: false); // off: the card is the client's ordinary card again
        Assert.Equal(DeckAddRefusal.None, ClassicDeckRules.CanAdd(fireDeck, two, WeaveCard, Lookup));
    }
}
