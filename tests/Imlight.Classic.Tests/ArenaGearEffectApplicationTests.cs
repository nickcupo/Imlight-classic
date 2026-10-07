// CLASSIC: actual canonical factory, equipment aggregation, rebuild and card ownership regressions.
// All objects are authored fixtures; there is no player database, native client or asset dependency.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaGearEffectApplicationTests {
    [Theory]
    [InlineData(164160u)]
    [InlineData(164168u)]
    [InlineData(164169u)]
    [InlineData(164170u)]
    [InlineData(164171u)]
    [InlineData(164172u)]
    public void EveryVerifiedManaReductionUsesActualProjectedEffectAndRestoresMaximum(uint id) {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var template = fixture.Projected(id);
        var info = Assert.Single(template.m_equipEffects.OfType<StatisticEffectInfo>(),
            effect => effect.m_effectName == "CanonicalMaxManaPercentReduce");
        var native = Assert.IsType<WizStatisticEffect>(GameEffectFactory.CreateEffectFromInfo(info, StringHash.Compute("Hat")));
        Assert.Equal(99, native.m_lookupIndex); Assert.Equal(1f, native.m_manaBonus);
        Assert.Equal(StringHash.Compute("CanonicalMaxManaPercentReduce"), native.m_effectNameID);
        Assert.True(fixture.Equip(wizard, template, 901));
        Assert.Equal(0, wizard.GameStats.m_baseMana); Assert.Equal(0, wizard.GameStats.m_currentMana);
        Assert.True(fixture.Unequip(wizard, template, 901));
        Assert.Equal(100, wizard.GameStats.m_baseMana); Assert.Equal(0, wizard.GameStats.m_currentMana);
        CharacterHelper.RecalculateGameStats(wizard);
        Assert.Equal(100, wizard.GameStats.m_baseMana); Assert.Equal(0, wizard.GameStats.m_currentMana);
    }

    [Fact]
    public void DatedSandalsBonusesReachActualEquipmentAndSurviveRebuildExactlyOnce() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var sandals = fixture.Projected(100540);
        Assert.True(fixture.Equip(wizard, sandals, 901));
        for (var iteration = 0; iteration < 3; iteration++) {
            Assert.Equal(148, wizard.GameStats.m_baseHitpoints);
            Assert.Equal(.03f, wizard.GameStats.m_powerPipBonusPercentAll, 6);
            Assert.Equal(.02f, wizard.GameStats.m_dmgBonusPercent[6], 6);
            Assert.Equal(.06f, wizard.GameStats.m_dmgReducePercent[6], 6);
            Assert.Equal(71, wizard.GameStats.m_currentHitpoints);
            Assert.Equal(75, wizard.GameStats.m_currentMana);
            CharacterHelper.RecalculateGameStats(wizard);
        }
        Assert.True(fixture.Unequip(wizard, sandals, 901));
        Assert.Equal(100, wizard.GameStats.m_baseHitpoints);
        Assert.Equal(0, wizard.GameStats.m_powerPipBonusPercentAll);
        Assert.Equal(0, wizard.GameStats.m_dmgBonusPercent[6]);
        Assert.Equal(0, wizard.GameStats.m_dmgReducePercent[6]);
    }

    [Fact]
    public void DatedFootgearKeepsAllFourBonusesWithItsExplicitPercentageManaPenalty() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var footgear = fixture.Projected(164172);
        Assert.True(fixture.Equip(wizard, footgear, 901));
        for (var iteration = 0; iteration < 3; iteration++) {
            Assert.Equal(0, wizard.GameStats.m_baseMana);
            Assert.Equal(.04f, wizard.GameStats.m_powerPipBonusPercentAll, 6);
            Assert.Equal(.05f, wizard.GameStats.m_accBonusPercentAll, 6);
            Assert.Equal(.06f, wizard.GameStats.m_dmgBonusPercentAll, 6);
            Assert.Equal(.10f, wizard.GameStats.m_dmgReducePercentAll, 6);
            Assert.Equal(71, wizard.GameStats.m_currentHitpoints);
            CharacterHelper.RecalculateGameStats(wizard);
        }
        Assert.True(fixture.Unequip(wizard, footgear, 901));
        Assert.Equal(100, wizard.GameStats.m_baseMana);
        Assert.Equal(0, wizard.GameStats.m_currentMana);
        Assert.Equal(0, wizard.GameStats.m_powerPipBonusPercentAll);
        Assert.Equal(0, wizard.GameStats.m_accBonusPercentAll);
        Assert.Equal(0, wizard.GameStats.m_dmgBonusPercentAll);
        Assert.Equal(0, wizard.GameStats.m_dmgReducePercentAll);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlatManaAndMultipleReducedSlotsAreOrderIndependent(bool flatFirst) {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var flat = fixture.Template(730001, "Amulet", Fixture.Stat("CanonicalMaxMana", 402));
        var hat = fixture.Template(730002, "Hat", Fixture.Stat("CanonicalMaxManaPercentReduce", 99));
        var robe = fixture.Template(730003, "Robe", Fixture.Stat("CanonicalMaxManaPercentReduce", 99));
        if (flatFirst) Assert.True(fixture.Equip(wizard, flat, 901));
        Assert.True(fixture.Equip(wizard, hat, 902));
        if (!flatFirst) Assert.True(fixture.Equip(wizard, flat, 901));
        Assert.True(fixture.Equip(wizard, robe, 903));
        Assert.Equal(0, wizard.GameStats.m_baseMana); Assert.Equal(0, wizard.GameStats.m_currentMana);
        Assert.True(fixture.Unequip(wizard, hat, 902));
        Assert.Equal(0, wizard.GameStats.m_baseMana);
        Assert.True(fixture.Unequip(wizard, robe, 903));
        Assert.Equal(600, wizard.GameStats.m_baseMana); Assert.Equal(0, wizard.GameStats.m_currentMana);
        Assert.True(fixture.Unequip(wizard, flat, 901));
        Assert.Equal(100, wizard.GameStats.m_baseMana);
    }

    [Fact]
    public void RemovingFlatManaWhileReducedRestoresOnlyRemainingUnreducedMaximum() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var flat = fixture.Template(730001, "Amulet", Fixture.Stat("CanonicalMaxMana", 402));
        var hat = fixture.Template(730002, "Hat", Fixture.Stat("CanonicalMaxManaPercentReduce", 99));
        fixture.Equip(wizard, flat, 901); fixture.Equip(wizard, hat, 902);
        fixture.Unequip(wizard, flat, 901);
        Assert.Equal(0, wizard.GameStats.m_baseMana);
        fixture.Unequip(wizard, hat, 902);
        Assert.Equal(100, wizard.GameStats.m_baseMana);
        Assert.Equal(0, wizard.GameStats.m_currentMana);
    }

    [Fact]
    public void OrdinaryFlatManaFactoryAddRemoveAndRebuildRemainUnchanged() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var flat = fixture.Template(730001, "Amulet", Fixture.Stat("CanonicalMaxMana", 402));
        var effect = Assert.IsType<WizStatisticEffect>(GameEffectFactory.CreateEffectFromInfo(flat.m_equipEffects[0], 0));
        Assert.Equal(500f, effect.m_manaBonus);
        fixture.Equip(wizard, flat, 901);
        Assert.Equal(600, wizard.GameStats.m_baseMana); Assert.Equal(75, wizard.GameStats.m_currentMana);
        CharacterHelper.RecalculateGameStats(wizard); CharacterHelper.RecalculateGameStats(wizard);
        Assert.Equal(600, wizard.GameStats.m_baseMana); Assert.Equal(75, wizard.GameStats.m_currentMana);
        fixture.Unequip(wizard, flat, 901);
        Assert.Equal(100, wizard.GameStats.m_baseMana); Assert.Equal(75, wizard.GameStats.m_currentMana);
    }

    [Fact]
    public void ActualEquipmentSlotGuardAndReductionLedgerRejectDuplicateApplication() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var hat = fixture.Template(730002, "Hat", Fixture.Stat("CanonicalMaxManaPercentReduce", 99));
        Assert.True(fixture.Equip(wizard, hat, 901));
        Assert.False(fixture.Equip(wizard, hat, 901));
        Assert.Empty(CharacterEffectHelper.AddEffectsToWizard(wizard, hat));
        var effect = Assert.Single(wizard.GameEffects.Snapshot());
        Assert.Equal(StringHash.Compute("Hat"), effect.m_itemSlotID);
        fixture.Unequip(wizard, hat, 901);
        Assert.Empty(CharacterEffectHelper.RemoveEffectsFromWizard(wizard, hat));
        Assert.Equal(100, wizard.GameStats.m_baseMana);
        Assert.Empty(wizard.GameEffects.Snapshot());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    [InlineData(2f)]
    public void InvalidCanonicalReductionCannotChangeMana(float value) {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        fixture.SetValue("ManaReduce_AllSchools", 99, value);
        Assert.Throws<InvalidOperationException>(() => CharacterEffectHelper.AddGameEffectToStats(
            wizard.GameStats, Fixture.Stat("CanonicalMaxManaPercentReduce", 99), StringHash.Compute("Hat")));
        Assert.Equal(100, wizard.GameStats.m_baseMana); Assert.Equal(75, wizard.GameStats.m_currentMana);
    }

    [Fact]
    public void NativeFractionsAndFlatPointsReachActualFactoryAndAggregationWithoutExtraScaling() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var checks = new[] {
            ("CanonicalIceDamage", 100, .01f), ("CanonicalFireFlatDamage", 2, 3f),
            ("CanonicalFireAccuracy", 100, .01f), ("CanonicalFireReduceDamage", 102, .03f),
            ("CanonicalPowerPip", 102, .03f),
        };
        foreach (var (name, index, expected) in checks) {
            var info = Fixture.Stat(name, index);
            Assert.Equal(expected, CanonicalStatEffects.GetCanonicalStatValue(info), 6);
            var effect = Assert.IsType<WizStatisticEffect>(CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, info));
            var actual = name.Contains("Flat") ? effect.m_damageBonusFlat : name.Contains("Reduce") ? effect.m_damageReducePercent
                : name.Contains("Accuracy") ? effect.m_accuracyBonusPercent : name.Contains("PowerPip") ? effect.m_powerPipBonusPercent
                : effect.m_damageBonusPercent;
            Assert.Equal(expected, actual, 6);
        }
        Assert.Equal(.01f, wizard.GameStats.m_dmgBonusPercent[1], 6);
        Assert.Equal(3f, wizard.GameStats.m_dmgBonusFlat[0]);
        Assert.Equal(.01f, wizard.GameStats.m_accBonusPercent[0], 6);
        Assert.Equal(.03f, wizard.GameStats.m_dmgReducePercent[0], 6);
        Assert.Equal(.03f, wizard.GameStats.m_powerPipBonusPercentAll, 6);
        foreach (var (name, index, _) in checks)
            CharacterEffectHelper.RemoveGameEffectFromStats(wizard.GameStats, Fixture.Stat(name, index));
        Assert.Equal(0f, wizard.GameStats.m_dmgBonusPercent[1]); Assert.Equal(0f, wizard.GameStats.m_dmgBonusFlat[0]);
        Assert.Equal(0f, wizard.GameStats.m_accBonusPercent[0]); Assert.Equal(0f, wizard.GameStats.m_dmgReducePercent[0]);
        Assert.Equal(0f, wizard.GameStats.m_powerPipBonusPercentAll);
    }

    [Fact]
    public void RepeatedActualFullRecomputeRebuildsGearAndPetExtrasOncePreservingOtherState() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var hat = fixture.Template(730002, "Hat", Fixture.Stat("CanonicalIceDamage", 100), Fixture.Stat("CanonicalFireAccuracy", 100));
        var robe = fixture.Template(730003, "Robe", Fixture.Stat("CanonicalFireReduceDamage", 102), Fixture.Stat("CanonicalPowerPip", 102));
        var shoe = fixture.Template(730004, "Shoes", Fixture.Stat("CanonicalFireFlatDamage", 2));
        var pet = fixture.Template(730005, "Pet", Fixture.Stat("CanonicalPowerPip", 102),
            new StartingPipEffectInfo { m_effectName = "FixtureStartingPip", m_pipsGiven = 1, m_powerPipsGiven = 1 });
        fixture.Equip(wizard, hat, 901); fixture.Equip(wizard, robe, 902);
        fixture.Equip(wizard, shoe, 903); fixture.Equip(wizard, pet, 904);
        var ladder = wizard.GameStats.m_pArenaLadder;
        for (var iteration = 0; iteration < 3; iteration++) {
            CharacterHelper.RecalculateGameStats(wizard);
            Assert.Equal(.01f, wizard.GameStats.m_dmgBonusPercent[1], 6);
            Assert.Equal(.01f, wizard.GameStats.m_accBonusPercent[0], 6);
            Assert.Equal(.03f, wizard.GameStats.m_dmgReducePercent[0], 6);
            Assert.Equal(3f, wizard.GameStats.m_dmgBonusFlat[0]);
            Assert.Equal(.06f, wizard.GameStats.m_powerPipBonusPercentAll, 6);
            Assert.Equal(1, wizard.GameStats.m_startingPips); Assert.Equal(1, wizard.GameStats.m_startingPowerPips);
            Assert.Equal(7, wizard.GameEffects.Snapshot().Count);
            Assert.Equal(71, wizard.GameStats.m_currentHitpoints); Assert.Equal(75, wizard.GameStats.m_currentMana);
            Assert.Equal(123, wizard.GameStats.m_currentGold); Assert.Equal(456, wizard.GameStats.m_currentArenaPoints);
            Assert.Same(ladder, wizard.GameStats.m_pArenaLadder); Assert.True(wizard.GameStats.m_shadowMagicUnlocked);
            Assert.Equal(8, wizard.PetOwnerBehavior.Energy); Assert.Equal(4, wizard.PetOwnerBehavior.MaxSlots);
            Assert.Equal(77, wizard.MagicSchoolBehavior.TrainingPoints);
        }
        fixture.Unequip(wizard, pet, 904); fixture.Unequip(wizard, robe, 902);
        CharacterHelper.RecalculateGameStats(wizard);
        Assert.Equal(0f, wizard.GameStats.m_powerPipBonusPercentAll); Assert.Equal(0, wizard.GameStats.m_startingPips);
        Assert.Null(wizard.GameStats.m_dmgReducePercent);
        Assert.Equal(3, wizard.GameEffects.Snapshot().Count);
    }

    [Fact]
    public void RebuildAndFreshRelogDiscardOldManaLedgerAndUseNewBaseWithoutRefilling() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var hat = fixture.Template(730002, "Hat", Fixture.Stat("CanonicalMaxManaPercentReduce", 99));
        var flat = fixture.Template(730001, "Amulet", Fixture.Stat("CanonicalMaxMana", 402));
        fixture.Equip(wizard, hat, 901); fixture.Equip(wizard, flat, 902);
        CharacterHelper.RecalculateGameStats(wizard); CharacterHelper.RecalculateGameStats(wizard);
        Assert.Equal(0, wizard.GameStats.m_baseMana); Assert.Equal(0, wizard.GameStats.m_currentMana);
        var login = fixture.Relog(wizard);
        CharacterHelper.RecalculateGameStats(login);
        Assert.Equal(0, login.GameStats.m_baseMana); Assert.Equal(0, login.GameStats.m_currentMana);
        login.GameStats.Level = 2; login.MagicSchoolBehavior.Level = 2;
        CharacterHelper.RecalculateGameStats(login);
        fixture.Unequip(login, hat, 901);
        Assert.Equal(700, login.GameStats.m_baseMana); Assert.Equal(0, login.GameStats.m_currentMana);
        CharacterHelper.RecalculateGameStats(login);
        Assert.Equal(700, login.GameStats.m_baseMana); Assert.Equal(0, login.GameStats.m_currentMana);
    }

    [Fact]
    public void ProvidedCardsRetireExactOwnedCopiesAcrossRebuildUnequipSharedNameAndRelog() {
        using var fixture = new Fixture(); var wizard = fixture.Wizard();
        var amulet = fixture.Projected(164173);
        var robe = fixture.Template(730003, "Robe", new ProvideSpellEffectInfo {
            m_effectName = "ProvideSpell", m_spellName = "Infection", m_numSpells = 2 });
        var unrelated = new Spell { m_spellID = StringHash.Compute("Infection"), m_itemCard = true };
        wizard.SpellbookBehavior.TemporarySpells.Add(unrelated);
        wizard.SpellbookBehavior.LearnedSpellTemplateIds.Add(99);
        wizard.SpellbookBehavior.TreasureCardTemplateIds.Add(98);
        fixture.Equip(wizard, amulet, 901); fixture.Equip(wizard, robe, 902);
        Assert.Equal(5, wizard.SpellbookBehavior.TemporarySpells.Count);
        Assert.Equal(2, wizard.GameEffects.Snapshot().OfType<ProvideSpellEffect>().Count());
        foreach (var _ in Enumerable.Range(0, 2)) {
            CharacterHelper.RecalculateGameStats(wizard);
            Assert.Equal(5, wizard.SpellbookBehavior.TemporarySpells.Count);
            Assert.Contains(wizard.SpellbookBehavior.TemporarySpells, card => ReferenceEquals(card, unrelated));
            Assert.Equal(2, wizard.GameEffects.Snapshot().OfType<ProvideSpellEffect>().Count());
            Assert.Equal(new uint[] { 99 }, wizard.SpellbookBehavior.LearnedSpellTemplateIds);
            Assert.Equal(new uint[] { 98 }, wizard.SpellbookBehavior.TreasureCardTemplateIds);
        }
        fixture.Unequip(wizard, amulet, 901);
        Assert.Equal(3, wizard.SpellbookBehavior.TemporarySpells.Count);
        Assert.Contains(wizard.SpellbookBehavior.TemporarySpells, card => ReferenceEquals(card, unrelated));
        var login = fixture.Relog(wizard);
        CharacterHelper.RecalculateGameStats(login); CharacterHelper.RecalculateGameStats(login);
        Assert.Equal(2, login.SpellbookBehavior.TemporarySpells.Count);
        fixture.Unequip(login, robe, 902);
        Assert.Empty(login.SpellbookBehavior.TemporarySpells);
        fixture.Unequip(wizard, robe, 902);
        Assert.Same(unrelated, Assert.Single(wizard.SpellbookBehavior.TemporarySpells));
    }

    private sealed class Fixture : IDisposable {
        private readonly FieldInfo _canonical = typeof(CanonicalStatEffects).GetField("s_effectTable", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo _tablesField = typeof(GameEffectRuleData).GetField("s_statTables", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo _levels = typeof(MagicLevelsConfig).GetField("s_playerLevelConfig", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _oldCanonical, _oldTables, _oldLevels;
        private readonly TemplateManifest _oldManifest;
        private readonly IDictionary<ulong, CoreTemplate> _cache;
        private readonly Dictionary<ulong, CoreTemplate?> _prior = [];
        private readonly Dictionary<int, MagicSchoolTemplate> _schools;
        private readonly Dictionary<int, MagicSchoolTemplate> _oldSchools;
        private readonly Dictionary<uint, SpellTemplate> _spells;
        private readonly Dictionary<uint, SpellTemplate> _oldSpells;
        private readonly Dictionary<uint, string> _spellPaths;
        private readonly Dictionary<uint, string> _oldSpellPaths;
        private readonly Dictionary<string, WizardStatTable> _tables = [];
        private readonly List<GameEffectTemplate> _effects = [];
        private readonly JsonDocument _map;
        internal Fixture() {
            // Retain this authored configuration for recovery; no fixture is permanently deleted.
            var config = Path.Combine(Path.GetTempPath(), "w101c-arena-gear-effects-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-arena-gear-effects.log")}\n[Character]\nBaseGoldPouch=1000\n");
            ConfigurationManager.Initialize(config);
            ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
            ClassicArenaGearTemplates.Initialize(ClassicDataFixture.Root, "october-2010-arc1");
            _oldCanonical = _canonical.GetValue(null); _oldTables = _tablesField.GetValue(null); _oldLevels = _levels.GetValue(null);
            _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _map = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassicDataFixture.Root, ClassicArenaGearTemplates.RelativePath)));
            foreach (var item in _map.RootElement.GetProperty("items").EnumerateArray())
                foreach (var effect in item.GetProperty("effects").EnumerateArray())
                    if (effect.GetProperty("kind").GetString() == "stat")
                        Binding(effect.GetProperty("effect_name").GetString()!, effect.GetProperty("category").GetString()!,
                            effect.GetProperty("stat_table").GetString()!, effect.GetProperty("lookup_index").GetInt32(),
                            effect.GetProperty("canonical_value").GetSingle());
            Binding("CanonicalMaxMana", "MaxMana", "MaxMana_AllSchools", 402, 500f);
            _canonical.SetValue(null, new GameEffectTemplateList { m_effectTemplates = _effects });
            _tablesField.SetValue(null, _tables);
            _schools = (Dictionary<int, MagicSchoolTemplate>)typeof(MagicSchools).GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _oldSchools = new(_schools); _schools.Clear();
            var names = new[] { "Fire", "Ice", "Storm", "Life", "Myth", "Death", "Balance", "FixtureSentinel" };
            for (var index = 0; index < names.Length; index++)
                _schools[index] = new MagicSchoolTemplate { m_schoolName = names[index], m_schoolIndex = index };
            var baseStats = new ServerWizGameStats(default, 1);
            _levels.SetValue(null, new Dictionary<string, List<MagicLevelInfo>> { [baseStats.MagicSchool.ToString()] = [
                new() { m_xpToLevel = 0, m_hitpoints = 100, m_mana = 100, m_pipChance = .25f, m_petEnergy = 50 },
                new() { m_xpToLevel = 100, m_hitpoints = 100, m_mana = 100, m_pipChance = .25f, m_petEnergy = 50 },
                new() { m_xpToLevel = 200, m_hitpoints = 200, m_mana = 200, m_pipChance = .30f, m_petEnergy = 55 },
            ] });
            _spells = (Dictionary<uint, SpellTemplate>)typeof(SpellFactory).GetField("s_spellTemplates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _spellPaths = (Dictionary<uint, string>)typeof(SpellFactory).GetField("s_spellTemplatePaths", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _oldSpells = new(_spells); _oldSpellPaths = new(_spellPaths);
            _spells.Clear(); _spellPaths.Clear();
            var hash = StringHash.Compute("Infection");
            _spells[hash] = new SpellTemplate { m_name = "Infection", m_sMagicSchoolName = "Death", m_accuracy = 100,
                m_spellRank = new SpellRank { m_spellRank = 0 } };
            _spellPaths[hash] = "Spells/FixtureInfection.xml";
            _oldManifest = CoreObjectFactory.TemplateManifest;
            CoreObjectFactory.TemplateManifest = new() { m_serializedTemplates = [
                new TemplateLocation { m_id = 730099u, m_filename = "Spells/FixtureInfection.xml" },
            ] };
        }
        private void Binding(string name, string category, string table, int index, float value) {
            if (_effects.All(effect => effect.m_effectName != name))
                _effects.Add(new WizStatisticEffectTemplate { m_effectName = name, m_effectCategory = category, m_statTableName = table });
            if (!_tables.TryGetValue(table, out var vector))
                _tables[table] = vector = new WizardStatTable { m_statVector = [] };
            while (vector.m_statVector.Count <= index) vector.m_statVector.Add(0);
            if (vector.m_statVector[index] != 0) Assert.Equal(vector.m_statVector[index], value);
            vector.m_statVector[index] = value;
        }
        internal void SetValue(string table, int index, float value) => _tables[table].m_statVector[index] = value;
        internal static StatisticEffectInfo Stat(string name, int index) => new() { m_effectName = name, m_lookupIndex = index };
        internal WizItemTemplate Projected(uint id) {
            var row = _map.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("template_id").GetUInt32() == id);
            var path = row.GetProperty("path").GetString()!;
            var slot = path.Contains("Hat-", StringComparison.Ordinal) ? "Hat" : path.Contains("Robe-", StringComparison.Ordinal) ? "Robe"
                : path.Contains("Shoe-", StringComparison.Ordinal) ? "Shoes" : "Amulet";
            var template = Template(id, slot);
            ClassicArenaGearTemplates.Apply(template, path);
            return template;
        }
        internal WizItemTemplate Template(uint id, string slot, params GameEffectInfo[] effects) {
            if (!_prior.ContainsKey(id)) _prior[id] = _cache.TryGetValue(id, out var previous) ? previous : null;
            var item = new WizItemTemplate { m_templateID = id, m_objectName = "FixtureGear", m_adjectiveList = [slot],
                m_behaviors = [], m_equipEffects = [.. effects] };
            _cache[id] = item; return item;
        }
        internal Wizard Wizard() {
            var wizard = new Wizard { CharId = 730000, GameStats = new(default, 1),
                PlayerNameBehavior = new() { NameOverride = "Fixture Wizard" },
                MagicSchoolBehavior = new() { Level = 1, ExperiencePoints = 0, TrainingPoints = 77 },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                SpellbookBehavior = new() { LearnedSpellTemplateIds = [], TreasureCardTemplateIds = [], TemporarySpells = [] },
                PetOwnerBehavior = new() { MaxSlots = 4 }, Zone = "WizardCity/WC_Hub" };
            wizard.PetOwnerBehavior.SetEnergy(8);
            wizard.GameStats.SetBaseStats(); wizard.GameStats.m_currentHitpoints = 71; wizard.GameStats.m_currentMana = 75;
            wizard.GameStats.m_currentGold = 123; wizard.GameStats.m_currentArenaPoints = 456;
            wizard.GameStats.m_pArenaLadder = new(); wizard.GameStats.m_shadowMagicUnlocked = true;
            return wizard;
        }
        internal bool Equip(Wizard wizard, WizItemTemplate template, ulong id) {
            var slot = ItemHelper.GetItemSlot(template).SlotType;
            var item = new WizClientObjectItem { m_globalID = id, m_templateID = template.m_templateID,
                m_characterId = wizard.CharId, m_debugName = "FixtureGear", m_inactiveBehaviors = [] };
            if (!wizard.EquipmentBehavior.EquipItem(item, slot)) return false;
            CharacterEffectHelper.AddEffectsToWizard(wizard, template); return true;
        }
        internal bool Unequip(Wizard wizard, WizItemTemplate template, ulong id) {
            if (!wizard.EquipmentBehavior.UnequipItem(id)) return false;
            CharacterEffectHelper.RemoveEffectsFromWizard(wizard, template); return true;
        }
        internal Wizard Relog(Wizard previous) {
            var fresh = Wizard();
            fresh.GameStats.m_currentMana = previous.GameStats.m_currentMana;
            fresh.EquipmentBehavior.EquippedItems = [.. previous.EquipmentBehavior.EquippedItems.Select(item => item with { })];
            fresh.EquipmentBehavior.EquippedItemIds = [.. previous.EquipmentBehavior.EquippedItemIds];
            return fresh;
        }
        public void Dispose() {
            _canonical.SetValue(null, _oldCanonical); _tablesField.SetValue(null, _oldTables); _levels.SetValue(null, _oldLevels);
            foreach (var (id, prior) in _prior) { if (prior is null) _cache.Remove(id); else _cache[id] = prior; }
            _schools.Clear(); foreach (var (id, prior) in _oldSchools) _schools[id] = prior;
            _spells.Clear(); foreach (var (id, prior) in _oldSpells) _spells[id] = prior;
            _spellPaths.Clear(); foreach (var (id, prior) in _oldSpellPaths) _spellPaths[id] = prior;
            CoreObjectFactory.TemplateManifest = _oldManifest; _map.Dispose();
            ClassicArenaGearTemplates.Initialize(null, "late-2009"); ClassicRuntime.ResetForTests();
        }
    }
}
