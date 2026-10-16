// CLASSIC: work package C, the Astral schools of celestia-2010 (Sun enchantments, Star auras, Moon polymorph, the Celestia
// trainers) and the owner's Moon rework extra. Older profiles must stay exactly as they were.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class AstralSchoolTests : IDisposable {

    private const uint FormId = 990001;

    public AstralSchoolTests() => Profile("celestia-2010");

    public void Dispose() {
        MoonReworkSettings.OverrideForTests = null;
        CombatPolymorph.RevertCastOverrideForTests = null;
        ClassicProgression.SetAstralForTests(AstralRules.Empty);
        PolymorphForms.ClearForTests();
        ClassicRuntime.ResetForTests();
    }

    private static AstralRules RealAstral() => AstralRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "astral-2010.yaml"));

    private static void Profile(string id) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(id));
        ClassicProgression.SetAstralForTests(RealAstral());
    }

    // ---------------------------------------------------------------- data and profile gating

    [Fact]
    public void AstralRecordsLoadAndAreTrainableOnlyInTheCelestiaFamily() {
        var book = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));
        var astral = book.Records.Where(record => ClassicSpellSchema.AstralSchools.Contains(record.School)).ToList();
        Assert.Equal(6, astral.Count(record => record.School == "sun"));
        Assert.Equal(6, astral.Count(record => record.School == "star"));
        Assert.Equal(8, astral.Count(record => record.School == "moon"));
        Assert.All(astral, record => Assert.Equal(["celestia-2010"], record.Profiles));

        var celestia = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("celestia-2010"));
        var wintertusk = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("wintertusk-2011"));
        foreach (var record in astral) {
            Assert.True(celestia.IsTrainable(record.ClientTemplate), record.Id);
            Assert.True(wintertusk.IsTrainable(record.ClientTemplate), record.Id);
            foreach (var older in new[] { "october-2010-arc1", "late-2009", "arc1-2009h1" }) {
                var overrides = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile(older));
                Assert.False(overrides.IsTrainable(record.ClientTemplate), $"{record.Id} in {older}");
                Assert.False(overrides.AppliesHere(record), $"{record.Id} in {older}");
            }
        }
    }

    [Theory]
    [InlineData("october-2010-arc1")]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    public void OlderProfilesKeepTheClientsAstralCardsUnchanged(string profile) {
        var book = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));
        var overrides = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile(profile));
        Assert.Null(overrides.PlanFor(PrecisionShape()));
    }

    [Fact]
    public void PrecisionAuraTakesTheDated10PointsAndKeepsTheRestOfTheClientCard() {
        var book = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));
        var overrides = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("celestia-2010"));
        var plan = overrides.PlanFor(PrecisionShape())!;
        var result = SpellPlanSimulator.Apply(PrecisionShape(), plan);
        var accuracy = Assert.Single(result.Effects, effect => effect.EffectTypeName == "kModifyAccuracy");
        Assert.Equal(10, accuracy.Param);
        Assert.Equal(4, accuracy.Rounds);
        // The client's later armor piercing stays on the card (outside the 2010 vocabulary); the aura code ignores it.
        Assert.Contains("ArmorPiercing", Assert.Single(plan.RemainingIssues).Detail);
    }

    [Theory]
    [InlineData("Spells/Fortification.xml", "kModifyIncomingDamage", -15)]
    [InlineData("Spells/Aura of Amplification.xml", "kModifyOutgoingDamage", 15)]
    [InlineData("Spells/Vengeance.xml", "kCritBoost", 20)]
    [InlineData("Spells/Aura of Empowerment.xml", "kPipConversion", 1)]
    [InlineData("Spells/Transform - Gobbler.xml", "kPolymorph", 264517)]
    public void ClientValueCardsNeedNoChange(string path, string type, int param) {
        var book = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));
        var overrides = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("celestia-2010"));
        var shape = Shape(path, (type, param, type == "kPolymorph" ? 6 : 4, type == "kPolymorph" ? "Ice" : "All"));
        var plan = overrides.PlanFor(shape)!;
        Assert.False(plan.ChangesTemplate, string.Join("; ", plan.EffectChanges));
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void TheAstralFileNamesTrainersTheDatedExclusionAndTheReworkAsAnOwnerExtra() {
        var astral = RealAstral();
        Assert.True(astral.AppliesTo("celestia-2010"));
        Assert.True(astral.AppliesTo("wintertusk-2011"));
        Assert.False(astral.AppliesTo("october-2010-arc1"));
        Assert.Equal(7, astral.Trainers.Length);
        Assert.True(astral.FormItemExclusions.Contains(468229u));
        var ulantor = Assert.Single(astral.Trainers, trainer => trainer.Npc == 324084);
        Assert.Equal(4, ulantor.Spells.Length);
        var rework = astral.MoonRework!;
        Assert.Equal(8, rework.Rounds);
        Assert.Equal(90, rework.FormFor("spell.moon.polymorph_gobbler")!.StunResistPercent);
        Assert.Equal("2011-05-11", rework.FormFor("spell.moon.polymorph_gobbler")!.MatchesOfficial);
    }

    [Fact]
    public void TheReworkSwitchIsOnByDefaultInCelestiaOnlyAndCanBeTurnedOff() {
        Assert.NotNull(MoonReworkSettings.Current);
        MoonReworkSettings.OverrideForTests = false;
        Assert.Null(MoonReworkSettings.Current);
        MoonReworkSettings.OverrideForTests = null;
        Profile("october-2010-arc1");
        Assert.False(ClassicCelestiaRules.Active);
        Assert.Null(MoonReworkSettings.Current);
    }

    [Fact]
    public void CelestiaTrainersAreStockedFromTheAstralFile() {
        var field = typeof(SpiralDB).GetField("s_npcSpellInventories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try {
            field.SetValue(null, new System.Collections.Concurrent.ConcurrentDictionary<ulong, NPCSpellInventory>());
            var records = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).Records;
            var ids = new Dictionary<string, ulong>();
            ulong Resolve(string path) => ids.TryGetValue(path, out var id) ? id : ids[path] = (ulong) (ids.Count + 1);
            Assert.Equal(7, ClassicTrainerInventories.RefreshAstralTrainers(RealAstral(), records, path => Resolve(path)));
            Assert.True(SpiralDB.TryGetNpcSpellInventory(324084, out var ulantor));
            var treant = ids["Spells/Polymorph - Treant.xml"];
            Assert.Equal(55, Assert.Single(ulantor.Spells, entry => entry.TemplateID == treant).Level);
            Assert.True(SpiralDB.TryGetNpcSpellInventory(311089, out var moon));
            Assert.Equal(50, Assert.Single(moon.Spells, entry => entry.TemplateID == ids["Spells/Transform - Gobbler.xml"]).Level);
            Assert.True(SpiralDB.TryGetNpcSpellInventory(311087, out var sun));
            Assert.Contains(sun.Spells, entry => entry.TemplateID == ids["Spells/Cloak.xml"]);
        } finally {
            field.SetValue(null, previous);
        }
    }

    // ---------------------------------------------------------------- Sun

    [Theory]
    [InlineData(0, true, false)]   // rank 0 damage card
    [InlineData(2, true, true)]    // ranked damage card
    [InlineData(2, false, false)]  // ranked card without damage
    public void SunEnchantmentsNeedARankedDamageCard(byte rank, bool damage, bool allowed) {
        var strong = new SpellTemplate { m_sMagicSchoolName = "Sun", m_effects = [new SpellEffect { m_effectType = kSpellEffects.kModifyCardDamage, m_effectParam = 100 }] };
        var target = new SpellTemplate {
            m_spellRank = new SpellRank { m_spellRank = rank },
            m_effects = [new SpellEffect { m_effectType = damage ? kSpellEffects.kDamage : kSpellEffects.kModifyIncomingDamage, m_effectParam = 100 }],
        };
        Assert.Equal(!allowed, ClassicHandEnchantment.IsSunRuleBreak(strong, target, strong.m_effects[0]));
        var cloak = new SpellTemplate { m_sMagicSchoolName = "Sun", m_effects = [new SpellEffect { m_effectType = kSpellEffects.kModifyCardCloak }] };
        Assert.False(ClassicHandEnchantment.IsSunRuleBreak(cloak, target, cloak.m_effects[0]));
        var myth = strong with { m_sMagicSchoolName = "Myth" };
        Assert.False(ClassicHandEnchantment.IsSunRuleBreak(myth, target, myth.m_effects[0])); // 2009 Treasure Card rules
        Profile("october-2010-arc1");
        Assert.False(ClassicHandEnchantment.IsSunRuleBreak(strong, target, strong.m_effects[0]));
    }

    // ---------------------------------------------------------------- Star auras

    [Fact]
    public void AnAuraReplacesTheLastOneAndIsNeverRemovedOrUsedUp() {
        var wizard = Circle();
        CombatAuras.Apply(wizard, Aura(kSpellEffects.kModifyOutgoingDamage, 15), 1);
        CombatAuras.Apply(wizard, Aura(kSpellEffects.kModifyIncomingDamage, -15), 2);
        Assert.Equal(0f, CombatAuras.OutgoingDamage(wizard, "Fire"));
        Assert.Equal(-0.15f, CombatAuras.IncomingDamage(wizard, "Fire"), 3);

        foreach (var removal in new[] { kSpellEffects.kRemoveWard, kSpellEffects.kRemoveCharm, kSpellEffects.kStealWard, kSpellEffects.kStealCharm }) {
            CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = removal, m_effectParam = -1 }, [], Circle(), [wizard]);
        }
        Damage(wizard, 100);
        Damage(wizard, 100);
        Assert.Equal(1000 - 85 - 85, wizard.ParticipantGameStats.m_currentHitpoints);
        Assert.True(CombatAuras.HasAura(wizard));
    }

    [Fact]
    public void AmplificationRaisesEveryHitWhileItLasts() {
        var caster = Circle(); var target = Circle();
        CombatAuras.Apply(caster, Aura(kSpellEffects.kModifyOutgoingDamage, 15), 1);
        for (var hit = 0; hit < 2; hit++) {
            CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = kSpellEffects.kDamage, m_sDamageType = "Fire", m_effectParam = 100 },
                [], caster, [target]);
        }
        Assert.Equal(1000 - 115 - 115, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void AnAuraCoversTheNextFourRoundsThenFades() {
        var wizard = Circle();
        CombatAuras.Apply(wizard, Aura(kSpellEffects.kModifyAccuracy, 10), 1);
        CombatAuras.EndOfRound(wizard); // the round it was cast
        for (var round = 0; round < 4; round++) {
            Assert.Equal(10, CombatAuras.AccuracyPoints(wizard, "Fire"));
            CombatAuras.EndOfRound(wizard);
        }
        Assert.False(CombatAuras.HasAura(wizard));
        Assert.True(wizard.CombatParticipant.m_needToCleanAuras);
    }

    [Fact]
    public void ConvictionResistsAStunWithoutSpendingAShield() {
        var wizard = Circle();
        CombatAuras.Apply(wizard, Aura(kSpellEffects.kStunResist, 100), 1); // certain resist for the test
        CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = kSpellEffects.kStun, m_effectTarget = kEffectTarget.kEnemySingle }, [], Circle(), [wizard]);
        Assert.Equal(0, wizard.CombatParticipant.m_stunned);
        Assert.Empty(wizard._hangingEffects);
    }

    [Fact]
    public void VengeanceAndConvictionFeedTheCriticalModelHooks() {
        var caster = Circle(); var target = Circle();
        CombatAuras.Apply(caster, Aura(kSpellEffects.kCritBoost, 20), 1);
        CombatAuras.Apply(target, new SpellTemplate { m_sTypeName = "Aura", m_effects = [
            new SpellEffect { m_effectType = kSpellEffects.kStunResist, m_effectParam = 90, m_sDamageType = "All", m_effectTarget = kEffectTarget.kSelf, m_numRounds = 4 },
            new SpellEffect { m_effectType = kSpellEffects.kCritBlock, m_effectParam = 20, m_sDamageType = "All", m_effectTarget = kEffectTarget.kSelf, m_numRounds = 4 },
        ] }, 2);
        CombatAuras.HookCriticalModel();
        Assert.Equal(20, CombatCriticals.AuraCritBoostPoints(caster));
        Assert.Equal(20, CombatCriticals.AuraBlockBoostPoints(target));
        Assert.Equal(0, CombatCriticals.AuraCritBoostPoints(target));
        Assert.Equal(90, CombatAuras.StunResistPercent(target));
        Assert.Empty(target._hangingEffects); // never a hanging (consumed) effect
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    public void EmpowermentGivesAPipForRankFourOrMore(int rank, int gained) {
        var wizard = Circle();
        wizard.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 2 };
        CombatAuras.Apply(wizard, Aura(kSpellEffects.kPipConversion, 1), 1);
        CombatAuras.OnSpellUsed([wizard], rank);
        Assert.Equal(2 + gained, wizard.CombatParticipant.m_pipCount.m_genericPips);
    }

    [Fact]
    public void AurasAreCelestiaOnly() {
        var aura = Aura(kSpellEffects.kModifyOutgoingDamage, 15);
        Assert.True(CombatAuras.IsAura(aura));
        Profile("october-2010-arc1");
        Assert.False(CombatAuras.IsAura(aura));
        Assert.Null(CombatPolymorph.PolymorphEffectOf(PolymorphCard()));
    }

    // ---------------------------------------------------------------- Moon polymorph (dated)

    [Fact]
    public void PolymorphConvertsHealthAndSwapsStatsDeckAndSchool() {
        MoonReworkSettings.OverrideForTests = false;
        PolymorphForms.RegisterForTests(Form(health: 5000));
        var wizard = Circle(current: 500, max: 1000);
        var ownStats = wizard.ParticipantGameStats;

        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
        Assert.NotNull(wizard.Polymorph);
        Assert.Equal(5000, wizard.ParticipantGameStats.m_baseHitpoints);
        Assert.Equal(2500, wizard.ParticipantGameStats.m_currentHitpoints);         // 50% stays 50%
        Assert.Equal((int) MagicSchool.Ice, wizard.CombatParticipant.m_primaryMagicSchoolID);
        Assert.Equal(6, wizard.CombatParticipant.m_nPolymorphTurnLength);
        Assert.Equal(9999, (int) wizard._combatDeck.TotalCardCount);                // a creature's endless deck
        Assert.False(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null)); // not twice

        Damage(wizard, 1500);
        Assert.Equal(1000, wizard.ParticipantGameStats.m_currentHitpoints);
        Assert.True(CombatPolymorph.Revert(wizard));
        Assert.Same(ownStats, wizard.ParticipantGameStats);
        Assert.Equal(200, ownStats.m_currentHitpoints);                              // 20% of the form is 20% of the wizard
        Assert.Equal(0, wizard.CombatParticipant.m_nPolymorphTurnLength);
        Assert.Null(wizard.CombatParticipant.m_pSavedGameStats);
    }

    [Fact]
    public void AFormLastsItsRoundsAfterTheCastThenRevertsAtTheEndOfARound() {
        MoonReworkSettings.OverrideForTests = false;
        CombatPolymorph.RevertCastOverrideForTests = false;
        PolymorphForms.RegisterForTests(Form(health: 2000));
        var wizard = Circle();
        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
        CombatPolymorph.EndOfRound([wizard], null);           // the cast round
        for (var round = 6; round > 1; round--) {
            CombatPolymorph.EndOfRound([wizard], null);
            Assert.NotNull(wizard.Polymorph);
            Assert.Equal(round - 1, wizard.CombatParticipant.m_nPolymorphTurnLength);
        }
        CombatPolymorph.EndOfRound([wizard], null);
        Assert.Null(wizard.Polymorph);
        Assert.Equal(1000, wizard.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void ADefeatedFormLeavesADefeatedWizardAndTheDuelEndPutsTheWizardBack() {
        MoonReworkSettings.OverrideForTests = false;
        PolymorphForms.RegisterForTests(Form(health: 2000));
        var wizard = Circle();
        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
        Damage(wizard, 5000);
        Assert.Null(wizard.Polymorph);
        Assert.Equal(0, wizard.ParticipantGameStats.m_currentHitpoints);
        Assert.False(wizard.IsAlive);

        var other = Circle(current: 1000, max: 1000);
        Assert.True(CombatPolymorph.TryBegin(other, PolymorphCard().m_effects[0], null));
        other.EndPolymorphSilently();
        Assert.Null(other.Polymorph);
        Assert.Equal(1000, other.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void ThePolymorphCardResolvesThroughTheActionResolver() {
        MoonReworkSettings.OverrideForTests = false;
        PolymorphForms.RegisterForTests(Form(health: 3000));
        var wizard = Circle();
        var action = new CombatAction { m_targetSubcircleList = [] };
        var time = 0f;
        Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(new QueuedCombatAction {
            SpellCaster = wizard, SelectedTarget = wizard, Spell = new Spell { m_templateID = 1234, m_pipCost = new SpellRank(), m_accuracy = 100 },
            SpellTemplate = PolymorphCard(),
        }, ref action, ref time));
        Assert.NotNull(wizard.Polymorph);
        Assert.Equal(wizard.SlotIndex, Assert.Single(action.m_targetSubcircleList));
        Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(new QueuedCombatAction {
            SpellCaster = wizard, SelectedTarget = wizard, Spell = new Spell { m_templateID = 1235, m_pipCost = new SpellRank() },
            SpellTemplate = new SpellTemplate { m_effects = [new SpellEffect { m_effectType = kSpellEffects.kUnPolymorph, m_effectTarget = kEffectTarget.kSelf }] },
        }, ref action, ref time));
        Assert.Null(wizard.Polymorph);
    }

    // ---------------------------------------------------------------- the owner's Moon rework

    [Fact]
    public void ReworkFormsLinkHealthGearLevelAndCriticalToTheWizard() {
        PolymorphForms.RegisterForTests(Form(health: 2000));
        var wizard = Circle(current: 4000, max: 4000);
        wizard.ParticipantGameStats.Level = 60;
        wizard.ParticipantGameStats.m_dmgBonusPercentAll = 0.20f;
        wizard.ParticipantGameStats.m_dmgReducePercentAll = 0.10f;
        wizard.ParticipantGameStats.m_criticalHitRatingAll = 30;
        var gobbler = new Spell { m_templateID = 77 };
        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], gobbler));
        var form = wizard.ParticipantGameStats;
        Assert.Equal(4000, form.m_baseHitpoints);                  // link 100% (no record for the test card) beats the form's 2000
        Assert.Equal(4000, form.m_currentHitpoints);
        Assert.Equal(0.30f, form.m_dmgBonusPercentAll, 3);         // gear 20% + 10 levels above 50
        Assert.Equal(0.10f, form.m_dmgReducePercentAll, 3);
        Assert.Equal(30f, form.m_criticalHitRatingAll);
        Assert.Equal(8, wizard.CombatParticipant.m_nPolymorphTurnLength);
        Assert.Equal(0, wizard.Polymorph!.StunResistPercent);
    }

    [Fact]
    public void ReworkHealthLinkFollowsTheFormAndIsCappedInPvp() {
        Assert.Equal(5200, PolymorphMath.ReworkFormMaxHealth(2000, 4000, 130));
        Assert.Equal(4000, PolymorphMath.ReworkFormMaxHealth(2000, 4000, 130, capPercent: 100));
        Assert.Equal(5000, PolymorphMath.ReworkFormMaxHealth(5000, 3000, 130));
        Assert.Equal(0.10f, PolymorphMath.ReworkLevelDamageBonus(60, 1, 10), 3);
        Assert.Equal(0.10f, PolymorphMath.ReworkLevelDamageBonus(70, 1, 10), 3);
        Assert.Equal(0f, PolymorphMath.ReworkLevelDamageBonus(45, 1, 10));
        Assert.Equal(1, PolymorphMath.ConvertHealth(1, 1000, 100));      // never rounds a living wizard to 0
        Assert.Equal(0, PolymorphMath.ConvertHealth(0, 1000, 5000));
        Assert.Equal(5000, PolymorphMath.ConvertHealth(1000, 1000, 5000));
    }

    [Fact]
    public void ReworkPvpAllowsOnePolymorphPerMatchAtLevelFifty() {
        PolymorphForms.RegisterForTests(Form(health: 2000));
        var wizard = Circle();
        wizard._duelActor.Duel.m_bPVP = true;
        wizard.ParticipantGameStats.Level = 49;
        Assert.False(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
        wizard.ParticipantGameStats.Level = 55;
        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
        Assert.True(CombatPolymorph.Revert(wizard));
        Assert.False(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
        MoonReworkSettings.OverrideForTests = false; // the dated polymorph has no such guard
        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));
    }

    [Fact]
    public void ReworkHandKeepsTwoOfTheWizardsHeldCardsReachable() {
        PolymorphForms.RegisterForTests(Form(health: 2000, cards: false));
        var wizard = Circle();
        var held = Enumerable.Range(0, 5).Select(i => new Spell { m_templateID = (uint) (500 + i) }).ToList();
        var tc = new Spell { m_templateID = 600, m_treasureCard = true };
        var own = new CombatDeck([], [], 7);
        own.LastGivenHand.AddRange([tc, .. held]);
        wizard._combatDeck = own;
        Assert.True(CombatPolymorph.TryBegin(wizard, PolymorphCard().m_effects[0], null));

        var hand = wizard.DrawHand().m_spellList;
        Assert.Equal([held[0], held[1]], hand.Where(card => card.m_templateID >= 500));   // treasure cards stay in the deck
        Assert.True(wizard.DiscardCard(held[0]));
        Assert.DoesNotContain(held[0], own.LastGivenHand);
        hand = wizard.DrawHand().m_spellList;
        Assert.Equal([held[1], held[2]], hand.Where(card => card.m_templateID >= 500));
        Assert.True(CombatPolymorph.Revert(wizard));
        Assert.Same(own, wizard._combatDeck);
        Assert.Equal([tc, held[1], held[2], held[3], held[4]], own.LastGivenHand);
    }

    [Fact]
    public void ReworkDeckIsARealDeckOfTheFormsOwnCards() {
        var form = Form(health: 2000) with { Cards = [new PolymorphCard(10, true), new PolymorphCard(11, true), new PolymorphCard(12, false)] };
        var deck = CombatPolymorph.DeckOf(form, RealAstral().MoonRework);
        Assert.Equal(4u, deck.Single(card => card.TemplateId == 10).Quantity);
        Assert.Equal(2u, deck.Single(card => card.TemplateId == 12).Quantity);
        Assert.All(CombatPolymorph.DeckOf(form, null), card => Assert.Equal(9999u, card.Quantity));
    }

    // ---------------------------------------------------------------- helpers

    private static PolymorphForm Form(int health, bool cards = true) => new(FormId, 1, "Test Gobbler", MagicSchool.Ice, 50, health, [],
        cards ? [new PolymorphCard(42, true)] : []);

    private static SpellTemplate PolymorphCard() => new() {
        m_name = "Polymorph - Test", m_sTypeName = "Polymorph", m_sMagicSchoolName = "Moon",
        m_effects = [new SpellEffect { m_effectType = kSpellEffects.kPolymorph, m_effectParam = unchecked((int) FormId), m_sDamageType = "Ice",
            m_effectTarget = kEffectTarget.kSelf, m_numRounds = 6 }],
    };

    private static SpellTemplate Aura(kSpellEffects kind, int param) => new() {
        m_sTypeName = "Aura", m_sMagicSchoolName = "Star",
        m_effects = [new SpellEffect { m_effectType = kind, m_effectParam = param, m_sDamageType = "All", m_effectTarget = kEffectTarget.kSelf, m_numRounds = 4 }],
    };

    private static SpellTemplateShape PrecisionShape() => Shape("Spells/Infallibility.xml",
        ("kModifyAccuracy", 15, 4, "All"), ("kModifyOutgoingArmorPiercing", 15, 4, "All"));

    private static SpellTemplateShape Shape(string path, params (string Type, int Param, int Rounds, string School)[] effects) => new() {
        Path = path, Name = Path.GetFileNameWithoutExtension(path), Rank = 0, Accuracy = 100,
        Effects = [.. effects.Select(effect => new TemplateEffectNode {
            EffectType = effect.Type, TargetName = "kSelf", Target = TemplateTarget.Self, DamageType = effect.School, Param = effect.Param,
            Rounds = effect.Rounds,
        })],
    };

    private static void Damage(CombatDuelSubCircle target, int amount)
        => CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = kSpellEffects.kDamage, m_sDamageType = "Fire", m_effectParam = amount },
            [], target, [target]);

    private static CombatDuelSubCircle Circle(int current = 1000, int max = 1000) {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var circle = duel.SubCircles[0];
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = max;
        stats.m_currentHitpoints = current;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pipCount = new PipCount() });
        circle._combatDeck = new CombatDeck([], [], 7);
        return circle;
    }

    private static CombatActionListObj Resolve(CombatDuelSubCircle caster, QueuedCombatAction action) {
        var resolver = new CombatResolver(caster._duelActor.Duel, [caster]);
        resolver.Reset();
        var queue = (List<QueuedCombatAction>) typeof(CombatResolver)
            .GetField("_queuedCombatActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
        queue.Add(action);
        var actions = new CombatActionListObj { m_actionList = [] };
        typeof(CombatResolver).GetMethod("ProcessQueuedActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(resolver, [actions]);
        return actions;
    }
}
