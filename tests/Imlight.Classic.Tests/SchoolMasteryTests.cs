/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * CLASSIC: School Mastery (experiments.mastery, an experiment): the switch stays off in every historical profile, and
 * the Fire (Kindling) and Ice (Permafrost) procs in a duel, through the real resolver.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SchoolMasterySwitchTests {

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("october-2010-arc1")]
    [InlineData("celestia-2010")]
    [InlineData("wintertusk-2011")]
    [InlineData("dev-unrestricted")]
    public void EveryHistoricalProfileKeepsTheExperimentOff(string profile) {
        var rules = ClassicDataFixture.RealRules(profile);
        Assert.False(rules.MasteryEnabled);
        Assert.False(rules.Profile.Features.IsEnabled(ClassicFeatures.ExperimentsMastery));
    }

    [Fact]
    public void TheDemoProfileTurnsItOnAndKeepsTheLiveProfilesRules() {
        var demo = ClassicDataFixture.RealRules("mastery-demo");
        var live = ClassicDataFixture.RealRules("october-2010-arc1");
        Assert.True(demo.MasteryEnabled);
        Assert.Equal(ProfileStatus.Debug, demo.Profile.Status);
        Assert.Equal(live.Profile.Cutoff, demo.Profile.Cutoff);
        Assert.Equal(live.Profile.Features.Disabled.ToArray(), demo.Profile.Features.Disabled.ToArray());
        Assert.False(demo.Profile.Features.IsEnabled(ClassicFeatures.CriticalAndBlock));
    }

    [Fact]
    public void AnExperimentIsOffUnlessSetWhileAHistoricalSwitchIsOnUnlessSet() {
        Assert.False(FeatureSwitches.None.IsEnabled(ClassicFeatures.ExperimentsMastery));
        Assert.True(FeatureSwitches.None.IsEnabled(ClassicFeatures.Housing));
        Assert.True(new FeatureSwitches(new Dictionary<string, bool> { [ClassicFeatures.ExperimentsMastery] = true })
            .IsEnabled(ClassicFeatures.ExperimentsMastery));
        Assert.True(ClassicFeatures.IsExperiment(ClassicFeatures.ExperimentsMastery));
        Assert.False(ClassicFeatures.IsExperiment(ClassicFeatures.PetsEnergy));
    }

    [Fact]
    public void StockRulesKeepItOff() => Assert.False(ClassicRules.Stock.MasteryEnabled);

}

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SchoolMasteryDuelTests : IDisposable {

    private static readonly (int Index, string Name)[] TestSchools = [(9201, "Fire"), (9202, "Ice"), (9203, "Storm"), (9204, "Myth"), (9205, "Life"), (9206, "Death"), (9207, "Balance")];
    private const uint Base = uint.MaxValue - 900;
    private const ulong Krok = 35433;
    private readonly Dictionary<int, MagicSchoolTemplate> _schools;
    private readonly Dictionary<string, (Spell, SpellTemplate)> _spells = [];
    private uint _next;

    public SchoolMasteryDuelTests() {
        UseProfile("mastery-demo");
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-mastery-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }

        _schools = (Dictionary<int, MagicSchoolTemplate>) typeof(MagicSchools)
            .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var (index, name) in TestSchools) {
            _schools[index] = new MagicSchoolTemplate { m_schoolName = name, m_schoolIndex = index - 9199 };
        }

        // The client templates the procs cast (their r806919 shapes) and the fight's cards.
        Add("Pet - Detonate", MagicSchool.Fire, Effect(kSpellEffects.kDetonateOverTime, 1, kEffectTarget.kEnemySingle, "All"), accuracy: 75);
        Add("Link", MagicSchool.Fire, Burn(180), rank: 2);
        Add("Fire Elf", MagicSchool.Fire, Burn(210), rank: 2);
        Add("Thunder Snake", MagicSchool.Storm, Effect(kSpellEffects.kDamageOverTime, 180, kEffectTarget.kEnemySingle, "Storm", rounds: 3), rank: 1);
        Add("Krok Hit", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 300, kEffectTarget.kEnemySingle));
        Add("Big Krok Hit", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 700, kEffectTarget.kEnemySingle));
        Add("Storm Shark", MagicSchool.Storm, Effect(kSpellEffects.kDamage, 400, kEffectTarget.kEnemySingle, "Storm"), rank: 3);
        AddVariable("Tempest", MagicSchool.Storm, [.. Enumerable.Range(1, 7).Select(tier =>
            Effect(kSpellEffects.kDamage, 80 * tier, kEffectTarget.kEnemyTeamAllAtOnce, "Storm"))]);
        Add("Pet - Ghoul", MagicSchool.Death, Effect(kSpellEffects.kStealHealth, 180, kEffectTarget.kEnemySingle, "Death"), rank: 2);
        foreach (var school in new[] { "Fire", "Ice", "Storm" }) {
            Add($"{school}blade", Enum.Parse<MagicSchool>(school), Effect(kSpellEffects.kModifyOutgoingDamage, 35, kEffectTarget.kFriendlySingle, school));
        }
        Add("Fire Cat", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 100, kEffectTarget.kEnemySingle, "Fire"), rank: 1);
        AddRandom("Frost Beetle", MagicSchool.Ice, 1, [65, 75, 85, 95, 105]);
        AddRandom("Snow Serpent", MagicSchool.Ice, 2, [155, 165, 175, 185, 195]);
        AddRandom("Evil Snowman", MagicSchool.Ice, 3, [240, 255, 270, 285, 300]);
        AddRandom("Ice Wyvern", MagicSchool.Ice, 4, [335, 350, 365, 380, 395]);
        AddRandom("Colossus", MagicSchool.Ice, 6, [460, 480, 500, 520, 540]);
        Add("Huge Krok Hit", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 1600, kEffectTarget.kEnemySingle));
        Add("Prism Hit", MagicSchool.Balance, Effect(kSpellEffects.kDamage, 365, kEffectTarget.kEnemySingle, "Ice"), rank: 4);
        AddMany("Hydra", MagicSchool.Balance, rank: 6, Effect(kSpellEffects.kDamage, 230, kEffectTarget.kEnemySingle, "Fire"),
            Effect(kSpellEffects.kDamage, 230, kEffectTarget.kEnemySingle, "Ice"), Effect(kSpellEffects.kDamage, 230, kEffectTarget.kEnemySingle, "Storm"));
        Add("Troll", MagicSchool.Myth, Effect(kSpellEffects.kDamage, 190, kEffectTarget.kEnemySingle, "Myth"), rank: 2);
        // The minion cards summon nothing here (param 0): the spawn needs the client's creature templates and a live zone.
        Add("Minion Myth 000", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"));
        Add("Minion Myth 001", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"));
        Add("Minion Myth 002", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"));
        Add("Minion Myth 003", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"), rank: 3);
        Add("Pixie Life", MagicSchool.Life, Effect(kSpellEffects.kHeal, 420, kEffectTarget.kFriendlySingle, "Life"), rank: 2);
        Add("Ghoul", MagicSchool.Death, Effect(kSpellEffects.kStealHealth, 160, kEffectTarget.kEnemySingle, "Death"), rank: 2);
        Add("Hex", MagicSchool.Balance, Effect(kSpellEffects.kModifyIncomingDamage, 30, kEffectTarget.kEnemySingle, "All"));
        Add("Balanceblade", MagicSchool.Balance, Effect(kSpellEffects.kModifyOutgoingDamage, 25, kEffectTarget.kFriendlySingle, "All"));
        Add("Scorpion", MagicSchool.Balance, Effect(kSpellEffects.kDamage, 180, kEffectTarget.kEnemySingle, "Balance"), rank: 2);
        Add("Bladestorm", MagicSchool.Balance, Effect(kSpellEffects.kModifyOutgoingDamage, 20, kEffectTarget.kFriendlyTeam, "All"), rank: 1);
        // The tome's cards for the test creature (r806919 shapes: Monster Magic Defeat and Summon, Myth, rank 2).
        Add($"Kill{Krok}", MagicSchool.Myth, Effect(kSpellEffects.kKillCreature, (int) Krok, kEffectTarget.kEnemySingle, "Moon"), rank: 2);
        Add($"Summon{Krok}", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"), rank: 2);
        Add("Spirit Armor", MagicSchool.Life, Effect(kSpellEffects.kAbsorbDamage, 400, kEffectTarget.kFriendlySingle, "All"), rank: 3);
        Add("Pixie", MagicSchool.Life, Effect(kSpellEffects.kHeal, 400, kEffectTarget.kSelf, "Life"), rank: 2);
        MasteryDirector.ResolveSpellForTests = name => _spells.TryGetValue(name, out var s) ? s : null;

        var lazy = typeof(SpellCinematics).BaseType!.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (!(bool) lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy)!) {
            lazy.GetType().GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(lazy, RuntimeHelpers.GetUninitializedObject(typeof(SpellCinematics)));
            lazy.GetType().GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(lazy, null);
        }
    }

    private static void UseProfile(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
    }

    public void Dispose() {
        MasteryDirector.ResolveSpellForTests = null;
        MasteryDirector.CreatureForTests = null;
        MasteryDirector.StudiedForTests = null;
        foreach (var (index, _) in TestSchools) _schools.Remove(index);
        foreach (var (_, (spell, _)) in _spells) Cache.Remove(spell.m_templateID);
        ClassicRuntime.ResetForTests();
    }

    private static IDictionary<ulong, CoreTemplate> Cache => (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
        .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static SpellEffect Effect(kSpellEffects type, int param, kEffectTarget target, string school = "Fire", int rounds = 0) => new() {
        m_effectType = type, m_effectParam = param, m_sDamageType = school, m_effectTarget = target, m_numRounds = rounds, m_healModifier = 1f,
    };

    private static SpellEffect Burn(int total) => Effect(kSpellEffects.kDamageOverTime, total, kEffectTarget.kEnemySingle, "Fire", rounds: 3);

    private void Add(string name, MagicSchool school, SpellEffect effect, byte rank = 0, uint accuracy = 100)
        => AddMany(name, school, rank, accuracy, effect);

    private void AddMany(string name, MagicSchool school, byte rank, params SpellEffect[] effects) => AddMany(name, school, rank, 100, effects);

    // A ranged classic attack card: one of fixed values, the client's RandomSpellEffect.
    private void AddRandom(string name, MagicSchool school, byte rank, int[] values) => AddMany(name, school, rank, 80, new RandomSpellEffect {
        m_effectType = kSpellEffects.kInvalidSpellEffect, m_effectTarget = kEffectTarget.kInvalidTarget, m_sDamageType = "All",
        m_effectList = [.. values.Select(v => Effect(kSpellEffects.kDamage, v, kEffectTarget.kEnemySingle, school.ToString()))],
    });

    // An X-pip card: one nested effect per pip tier (m_pipNum 1..n), as the client's Tempest.
    private void AddVariable(string name, MagicSchool school, SpellEffect[] tiers) {
        for (var i = 0; i < tiers.Length; i++) tiers[i].m_pipNum = (byte) (i + 1);
        AddMany(name, school, 0, 70, new VariableSpellEffect {
            m_effectType = kSpellEffects.kInvalidSpellEffect, m_effectTarget = kEffectTarget.kInvalidTarget, m_sDamageType = "All", m_effectList = [.. tiers],
        });
    }

    private void AddMany(string name, MagicSchool school, byte rank, uint accuracy, params SpellEffect[] effects) {
        var id = Base + _next++;
        foreach (var effect in effects) effect.m_spellTemplateID = id;   // as a loaded template's effects carry it (ward matching groups by it)
        var template = new SpellTemplate {
            m_name = name, m_spellBase = name, m_accuracy = (byte) accuracy, m_sMagicSchoolName = school.ToString(),
            m_spellRank = new SpellRank { m_spellRank = rank }, m_effects = [.. effects],
        };
        Cache[id] = template;
        _spells[name] = (new Spell {
            m_templateID = id, m_accuracy = (byte) accuracy, m_magicSchoolID = (uint) school,
            m_pipCost = new SpellRank { m_spellRank = rank },
        }, template);
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, ulong template, int health, MagicSchool school = MagicSchool.None, int level = 0) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = template });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = health;
        stats.m_currentHitpoints = health;
        stats.m_schoolID = (uint) school;
        stats.Level = level;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        var combatStats = (WizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(WizGameStats));
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_hangingEffects = [], m_pGameStats = combatStats, m_pipCount = new PipCount { m_genericPips = 7 },
        });
        circle._combatDeck = new CombatDeck([], [], 7);
        circle.AddedToDuel = true;
        return circle;
    }

    private static CombatDuelComponent NewDuel() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        return duel;
    }

    private QueuedCombatAction Cast(CombatDuelSubCircle caster, CombatDuelSubCircle target, string spell)
        => new() { SpellCaster = caster, SelectedTarget = target, Spell = _spells[spell].Item1, SpellTemplate = _spells[spell].Item2 };

    private static List<CombatAction> Resolve(CombatDuelComponent duel, params QueuedCombatAction[] actions) {
        var resolver = new CombatResolver(duel.Duel, duel.SubCircles);
        resolver.Reset();
        var queue = (List<QueuedCombatAction>) typeof(CombatResolver)
            .GetField("_queuedCombatActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
        queue.AddRange(actions);
        var list = new CombatActionListObj { m_actionList = [] };
        typeof(CombatResolver).GetMethod("ProcessQueuedActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(resolver, [list]);
        return list.m_actionList;
    }

    private static SpellEffect HangingBurn(int perRound, int rounds, string school = "Fire") => new() {
        m_effectType = kSpellEffects.kDamageOverTime, m_paramPerRound = perRound, m_numRounds = rounds, m_sDamageType = school,
    };

    private static SpellEffect Shield(int percent = -50) => new() {
        m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = percent, m_sDamageType = "All", m_spellTemplateID = 777,
    };

    private string NameOf(CombatAction action) => _spells.First(s => s.Value.Item1.m_templateID == action.m_spell?.m_templateID).Key;

    // ------------------------------------------------------------ Fire: Kindling

    [Fact]
    public void ASecondBurnOnABurningEnemyIsSetOffAtOnceByAFreeDetonate() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var fire = Occupy(duel, 4, 1, 836, MagicSchool.Fire, level: 20);
        krok._hangingEffects.Add(HangingBurn(70, 2));            // Fire Elf's burn, two ticks left

        var list = Resolve(duel, Cast(fire, krok, "Link"));

        Assert.Equal(["Link", "Pet - Detonate"], list.Select(NameOf).ToArray());
        var proc = list[1];
        Assert.True(proc.m_interrupt);
        Assert.Equal((char) 1, proc.m_spellHits);
        Assert.Equal(fire.SlotIndex, proc.m_spellCaster);
        // Link (60 a round for 3) went off at once: 180. Fire Elf's burn is left to tick.
        Assert.Equal(1200 - 180, krok.ParticipantGameStats.m_currentHitpoints);
        var left = Assert.Single(krok._hangingEffects, e => e.m_effectType == kSpellEffects.kDamageOverTime);
        Assert.Equal((70, 2), (left.m_paramPerRound, left.m_numRounds));
        // The proc cost nothing: Link's 2 pips only.
        Assert.Equal(5, fire.CombatParticipant.m_pipCount.m_genericPips);
    }

    [Fact]
    public void TheDetonationGoesThroughTheEnemysShieldLikeATick() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var fire = Occupy(duel, 4, 1, 836, MagicSchool.Fire, level: 20);
        krok._hangingEffects.Add(HangingBurn(70, 2));
        krok._hangingEffects.Add(Shield());

        Resolve(duel, Cast(fire, krok, "Link"));

        Assert.Equal(1200 - 90, krok.ParticipantGameStats.m_currentHitpoints);
        Assert.DoesNotContain(krok._hangingEffects, e => e.m_effectType == kSpellEffects.kModifyIncomingDamage);
    }

    [Theory]
    [InlineData(MagicSchool.Fire, 4, "Link", true)]       // below Fire Elf's level
    [InlineData(MagicSchool.Storm, 20, "Link", true)]     // not a Fire wizard
    [InlineData(MagicSchool.Fire, 20, "Link", false)]     // the first burn: nothing to kindle
    [InlineData(MagicSchool.Fire, 20, "Thunder Snake", true)] // not a Fire burn
    public void KindlingNeedsAFireWizardAFireBurnAndABurningEnemy(MagicSchool school, int level, string spell, bool alreadyBurning) {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var wizard = Occupy(duel, 4, 1, 836, school, level);
        if (alreadyBurning) krok._hangingEffects.Add(HangingBurn(70, 2));

        var list = Resolve(duel, Cast(wizard, krok, spell));

        Assert.Equal([spell], list.Select(NameOf).ToArray());
    }

    [Fact]
    public void WithTheSwitchOffNothingProcsAndDetonateStaysInert() {
        UseProfile("october-2010-arc1");
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var fire = Occupy(duel, 4, 1, 836, MagicSchool.Fire, level: 20);
        krok._hangingEffects.Add(HangingBurn(70, 2));

        var list = Resolve(duel, Cast(fire, krok, "Link"));
        Assert.Equal(["Link"], list.Select(NameOf).ToArray());

        // A Detonate card (if any profile dealt one) does nothing outside the experiment, as before.
        var before = krok.ParticipantGameStats.m_currentHitpoints;
        Resolve(duel, Cast(fire, krok, "Pet - Detonate"));
        Assert.Equal(before, krok.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(2, krok._hangingEffects.Count(e => e.m_effectType == kSpellEffects.kDamageOverTime));
    }

    [Fact]
    public void AProcNeverUsesUpADispel() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var fire = Occupy(duel, 4, 1, 836, MagicSchool.Fire, level: 20);
        krok._hangingEffects.Add(HangingBurn(70, 2));
        var proc = new QueuedCombatAction {
            SpellCaster = fire, SelectedTarget = krok, Spell = _spells["Pet - Detonate"].Item1, SpellTemplate = _spells["Pet - Detonate"].Item2,
            Cheat = new BossCheatCast(BossCheatKind.Mastery, Interrupt: true, Message: null),
        };
        var dispel = new SpellEffect { m_effectType = kSpellEffects.kDispel, m_sDamageType = "Fire" };
        fire._hangingEffects.Add(dispel);

        var list = Resolve(duel, proc);

        Assert.Equal((char) 1, Assert.Single(list).m_spellHits);
        Assert.Contains(dispel, fire._hangingEffects);
        Assert.Equal(1200 - 140, krok.ParticipantGameStats.m_currentHitpoints);
    }

    // ------------------------------------------------------------ Ice: Frost Retort

    private QueuedCombatAction Hit(CombatDuelSubCircle creature, CombatDuelSubCircle target, string spell = "Krok Hit", bool mastery = false) => new() {
        // A creature's free cast, so the test needs no creature deck.
        SpellCaster = creature, SelectedTarget = target, Spell = _spells[spell].Item1, SpellTemplate = _spells[spell].Item2,
        Cheat = new BossCheatCast(mastery ? BossCheatKind.Mastery : BossCheatKind.ExtraCast, Interrupt: mastery, Message: null),
    };

    [Theory]
    [InlineData("Krok Hit", "Frost Beetle", 75)]      // 300 halved: 150 stopped, 75 back
    [InlineData("Big Krok Hit", "Snow Serpent", 175)] // 700 halved: 350 stopped, 175 back
    [InlineData("Huge Krok Hit", "Evil Snowman", 270)] // 1600 halved: 800 stopped, 400 capped at a quarter of 1091 (272): 270 shown
    public void HalfOfWhatTheShieldStoppedLashesBackAsAnIceCard(string hit, string card, int back) {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var ice = Occupy(duel, 4, 1, 1091, MagicSchool.Ice, level: 20);
        ice.ParticipantGameStats.m_currentHitpoints = 1091;
        ice._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Hit(krok, ice, hit));

        Assert.Equal([hit, card], list.Select(NameOf).ToArray());
        Assert.Equal((ice.SlotIndex, krok.SlotIndex), (list[1].m_spellCaster, list[1].m_targetSubcircleList.Single()));
        Assert.True(list[1].m_interrupt);
        Assert.Equal(5000 - back, krok.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void TheRetortComesOnceARoundAndNeverWithoutAShield() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var ice = Occupy(duel, 4, 1, 1091, MagicSchool.Ice, level: 20);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Krok Hit"], Resolve(duel, Hit(krok, ice)).Select(NameOf).ToArray());   // no shield, nothing stopped
        ice.ParticipantGameStats.m_currentHitpoints = 1091;
        ice._hangingEffects.Add(Shield());
        Assert.Equal(["Krok Hit", "Frost Beetle"], Resolve(duel, Hit(krok, ice)).Select(NameOf).ToArray());
        ice._hangingEffects.Add(Shield());
        Assert.Equal(["Krok Hit"], Resolve(duel, Hit(krok, ice)).Select(NameOf).ToArray());   // once a round
        duel.Mastery.NewRound(2);
        ice._hangingEffects.Add(Shield());
        Assert.Equal(["Krok Hit", "Frost Beetle"], Resolve(duel, Hit(krok, ice)).Select(NameOf).ToArray());
    }

    [Fact]
    public void ATickAProcAndAFriendNeverSetOffTheRetort() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var ice = Occupy(duel, 4, 1, 1091, MagicSchool.Ice, level: 20);
        var friend = Occupy(duel, 5, 1, 836, MagicSchool.Fire, level: 20);
        duel.Mastery.NewRound(1);

        // A burn ticking on the shielded Ice wizard at the start of their own turn.
        ice._hangingEffects.Add(Shield());
        ice._hangingEffects.Add(HangingBurn(300, 2));
        Assert.Equal(1, Resolve(duel, new QueuedCombatAction { SpellCaster = ice }).Count);
        // Another wizard's School Mastery proc (Storm's Tempest, say) that hits the Ice wizard.
        ice._hangingEffects.Add(Shield());
        Assert.Equal(["Krok Hit"], Resolve(duel, Hit(krok, ice, mastery: true)).Select(NameOf).ToArray());
        // A friend hitting the Ice wizard on purpose.
        ice._hangingEffects.Add(Shield());
        Assert.Equal(["Krok Hit"], Resolve(duel, Cast(friend, ice, "Krok Hit")).Select(NameOf).ToArray());
    }

    [Fact]
    public void FrostRetortNeedsTowerShieldsLevel() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var ice = Occupy(duel, 4, 1, 1000, MagicSchool.Ice, level: 15);
        ice._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(1);

        Assert.Equal(["Krok Hit"], Resolve(duel, Hit(krok, ice)).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Storm: Overkill

    [Theory]
    [InlineData(300, 0f, 1, 80)]      // 100 spare over two: 50 each, nearest tier 1 (80)
    [InlineData(100, 0f, 2, 160)]     // 300 spare over two: 150 each, tier 2 (160)
    [InlineData(100, 0.25f, 2, 200)]  // +25% Storm gear: Shark 500, 400 spare, 200 each = tier 2 (160 x 1.25)
    public void AStormKillSplitsItsOverkillEquallyOverTheOtherEnemiesAsATempest(int firstHealth, float gear, int tier, int each) {
        var duel = NewDuel();
        var a = Occupy(duel, 0, Krok, firstHealth);
        var b = Occupy(duel, 1, Krok, 1000);
        var c = Occupy(duel, 2, Krok, 1000);
        var storm = Occupy(duel, 4, 1, 710, MagicSchool.Storm, level: 20);
        storm.ParticipantGameStats.m_dmgBonusPercentAll = gear;
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(storm, a, "Storm Shark"));

        Assert.Equal(["Storm Shark", "Tempest"], list.Select(NameOf).ToArray());
        Assert.True(list[1].m_interrupt);
        Assert.Equal(tier, list[1].m_xPipCost);
        Assert.False(a.IsAlive);
        Assert.Equal([1000 - each, 1000 - each], new[] { b, c }.Select(x => x.ParticipantGameStats.m_currentHitpoints).ToArray());
        Assert.Equal(4, storm.CombatParticipant.m_pipCount.m_genericPips);   // only the Shark (3 pips) cost pips
    }

    [Fact]
    public void TheSpilloverGoesThroughShieldsAndNeverSpillsAgain() {
        var duel = NewDuel();
        var a = Occupy(duel, 0, Krok, 100);
        var shielded = Occupy(duel, 1, Krok, 1000);
        var weak = Occupy(duel, 2, Krok, 50);
        var storm = Occupy(duel, 4, 1, 710, MagicSchool.Storm, level: 20);
        shielded._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(storm, a, "Storm Shark"));

        // 300 spare over two = tier 2 (160): the shield halves it; the weak one dies with 110 to spare, which stays put.
        Assert.Equal(["Storm Shark", "Tempest"], list.Select(NameOf).ToArray());
        Assert.Equal(1000 - 80, shielded.ParticipantGameStats.m_currentHitpoints);
        Assert.False(weak.IsAlive);
    }

    [Theory]
    [InlineData(MagicSchool.Storm, 20, 900)]   // no kill, nothing to spill
    [InlineData(MagicSchool.Fire, 20, 100)]    // not a Storm wizard
    [InlineData(MagicSchool.Storm, 9, 100)]    // below Storm Shark's level
    public void OverkillNeedsAStormKillByAStormWizard(MagicSchool school, int level, int firstHealth) {
        var duel = NewDuel();
        var a = Occupy(duel, 0, Krok, firstHealth);
        Occupy(duel, 1, Krok, 1000);
        var wizard = Occupy(duel, 4, 1, 710, school, level);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Storm Shark"], Resolve(duel, Cast(wizard, a, "Storm Shark")).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Death: Hungry Dead

    [Fact]
    public void ADrainBelowHalfHealthDrainsAgainOnceARound() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var death = Occupy(duel, 4, 1, 915, MagicSchool.Death, level: 20);
        death.ParticipantGameStats.m_currentHitpoints = 400;
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(death, krok, "Ghoul"));

        Assert.Equal(["Ghoul", "Pet - Ghoul"], list.Select(NameOf).ToArray());
        Assert.Equal(1200 - 160 - 180, krok.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(400 + 160 + 180, death.ParticipantGameStats.m_currentHitpoints);
        death.ParticipantGameStats.m_currentHitpoints = 300;
        Assert.Equal(["Ghoul"], Resolve(duel, Cast(death, krok, "Ghoul")).Select(NameOf).ToArray());   // once a round
    }

    [Fact]
    public void AHealthyDeathWizardDoesNotDrainAgain() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var death = Occupy(duel, 4, 1, 915, MagicSchool.Death, level: 20);
        death.ParticipantGameStats.m_currentHitpoints = 600;
        duel.Mastery.NewRound(1);

        Assert.Equal(["Ghoul"], Resolve(duel, Cast(death, krok, "Ghoul")).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Myth: Taming (Monstrology)

    private const ulong Boss = 35999;

    private void StudyOnly(params uint[] studied) {
        MasteryDirector.CreatureForTests = t => t == Krok ? ((uint) Krok, false) : t == Boss ? ((uint) Boss, true) : null;
        MasteryDirector.StudiedForTests = (_, creature) => studied.Contains(creature);
    }

    [Fact]
    public void AStudiedCreatureAtAQuarterIsTamedByItsOwnMonstrologyCardsOnceAFight() {
        StudyOnly((uint) Krok);
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        krok.ParticipantGameStats.m_currentHitpoints = 450;
        var other = Occupy(duel, 1, Krok, 1200);
        var myth = Occupy(duel, 4, 1, 842, MagicSchool.Myth, level: 20);
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(myth, krok, "Troll"));   // 450 - 190 = 260, under 300

        Assert.Equal(["Troll", "Kill35433", "Summon35433"], list.Select(NameOf).ToArray());
        Assert.All(list.Skip(1), a => Assert.Equal(myth.SlotIndex, a.m_spellCaster));
        Assert.Equal(krok.SlotIndex, list[1].m_targetSubcircleList.Single());
        Assert.False(krok.IsAlive);                     // Monster Magic Defeat takes it ...
        Assert.Equal(5, myth.CombatParticipant.m_pipCount.m_genericPips);

        // Once a fight: the next weakened creature is only hit.
        other.ParticipantGameStats.m_currentHitpoints = 400;
        duel.Mastery.NewRound(2);
        Assert.Equal(["Troll"], Resolve(duel, Cast(myth, other, "Troll")).Select(NameOf).ToArray());
    }

    [Fact]
    public void AnUnstudiedCreatureABossAndAHealthyCreatureAreNotTamed() {
        StudyOnly((uint) Boss);
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        krok.ParticipantGameStats.m_currentHitpoints = 300;
        var boss = Occupy(duel, 1, Boss, 5000);
        boss.ParticipantGameStats.m_currentHitpoints = 500;
        var myth = Occupy(duel, 4, 1, 842, MagicSchool.Myth, level: 20);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Troll"], Resolve(duel, Cast(myth, krok, "Troll")).Select(NameOf).ToArray());   // not studied
        Assert.Equal(["Troll"], Resolve(duel, Cast(myth, boss, "Troll")).Select(NameOf).ToArray());   // a boss

        StudyOnly((uint) Krok);
        var fresh = NewDuel();
        var healthy = Occupy(fresh, 0, Krok, 1200);
        var wizard = Occupy(fresh, 4, 1, 842, MagicSchool.Myth, level: 20);
        fresh.Mastery.NewRound(1);
        Assert.Equal(["Troll"], Resolve(fresh, Cast(wizard, healthy, "Troll")).Select(NameOf).ToArray());   // above a quarter
    }

    [Theory]
    [InlineData(MagicSchool.Fire, 20, false, false)]   // not a Myth wizard
    [InlineData(MagicSchool.Myth, 6, false, false)]    // below Troll Minion's level
    [InlineData(MagicSchool.Myth, 20, true, false)]    // already has a minion
    [InlineData(MagicSchool.Myth, 20, false, true)]    // no free seat
    public void TamingNeedsAMythWizardWithoutAMinionAndASeat(MagicSchool school, int level, bool hasMinion, bool full) {
        StudyOnly((uint) Krok);
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        krok.ParticipantGameStats.m_currentHitpoints = 300;
        var wizard = Occupy(duel, 4, 1, 842, school, level);
        if (hasMinion) {
            var minion = Occupy(duel, 5, 35680, 600);
            CombatRegressionTests.SetProperty(minion, "IsSummonedMinion", true);
            minion.CaptureMinionOwner(wizard.SlotIndex);
        }
        if (full) foreach (var slot in new[] { 5, 6, 7 }) Occupy(duel, slot, 1, 800, MagicSchool.Fire, level: 20);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Troll"], Resolve(duel, Cast(wizard, krok, "Troll")).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Life: Overgrowth

    [Fact]
    public void AHealWith150ToSpareGrowsIntoSpiritArmorOnceARound() {
        var duel = NewDuel();
        Occupy(duel, 0, Krok, 1200);
        var life = Occupy(duel, 4, 1, 980, MagicSchool.Life, level: 20);
        var friend = Occupy(duel, 5, 1, 836, MagicSchool.Fire, level: 20);
        friend.ParticipantGameStats.m_currentHitpoints = 636;   // missing 200; Fairy 420 spares 220
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(life, friend, "Pixie Life"));

        Assert.Equal(["Pixie Life", "Spirit Armor"], list.Select(NameOf).ToArray());
        Assert.Equal(836, friend.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(400, Assert.Single(friend._hangingEffects, e => e.m_effectType == kSpellEffects.kAbsorbDamage).m_paramPerRound);
        Assert.Equal(["Pixie Life"], Resolve(duel, Cast(life, friend, "Pixie Life")).Select(NameOf).ToArray());
    }

    [Theory]
    [InlineData(500, false)]   // missing 336: Fairy spares 84
    [InlineData(836, true)]    // a full friend: all 420 spare
    public void OvergrowthNeedsTheSpare(int health, bool grows) {
        var duel = NewDuel();
        Occupy(duel, 0, Krok, 1200);
        var life = Occupy(duel, 4, 1, 980, MagicSchool.Life, level: 20);
        var friend = Occupy(duel, 5, 1, 836, MagicSchool.Fire, level: 20);
        friend.ParticipantGameStats.m_currentHitpoints = health;
        duel.Mastery.NewRound(1);

        Assert.Equal(grows, Resolve(duel, Cast(life, friend, "Pixie Life")).Select(NameOf).Contains("Spirit Armor"));
    }

    [Fact]
    public void ASelfHealGrowsOnTheLifeWizardAndNeedsSpiritArmorsLevel() {
        var duel = NewDuel();
        Occupy(duel, 0, Krok, 1200);
        var life = Occupy(duel, 4, 1, 980, MagicSchool.Life, level: 16);
        duel.Mastery.NewRound(1);
        var list = Resolve(duel, Cast(life, life, "Pixie"));
        Assert.Equal(["Pixie", "Spirit Armor"], list.Select(NameOf).ToArray());
        Assert.Equal(life.SlotIndex, list[1].m_targetSubcircleList.Single());

        var young = NewDuel();
        Occupy(young, 0, Krok, 1200);
        var low = Occupy(young, 4, 1, 900, MagicSchool.Life, level: 15);
        young.Mastery.NewRound(1);
        Assert.Equal(["Pixie"], Resolve(young, Cast(low, low, "Pixie")).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Balance: Spectrum

    [Fact]
    public void EachNewSchoolGivesItsBladeAndTheFourthSchoolGivesTheTeamBladestorm() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var balance = Occupy(duel, 4, 1, 992, MagicSchool.Balance, level: 20);
        var friend = Occupy(duel, 5, 1, 836, MagicSchool.Fire, level: 20);
        duel.Mastery.NewRound(1);

        string[] Play(CombatDuelSubCircle target, string spell) => [.. Resolve(duel, Cast(balance, target, spell)).Select(NameOf)];

        Assert.Equal(["Hex"], Play(krok, "Hex"));                                 // Balance: counted, no blade
        Assert.Equal(["Fire Cat", "Fireblade"], Play(krok, "Fire Cat"));          // a new school: its blade
        Assert.Equal(["Fire Cat"], Play(krok, "Fire Cat"));                       // the same school again: nothing
        Assert.Equal(["Hydra"], Play(krok, "Hydra"));                             // three schools at once count as Balance
        Assert.Equal(["Prism Hit", "Iceblade"], Play(krok, "Prism Hit"));         // a Balance card that landed Ice counts Ice
        Assert.Equal(["Thunder Snake", "Stormblade", "Bladestorm"], Play(krok, "Thunder Snake"));   // the fourth school
        Assert.Equal(["Frost Beetle"], Play(krok, "Frost Beetle"));               // Ice again, and Bladestorm only once
        Assert.Contains(friend._hangingEffects, e => e.m_effectType == kSpellEffects.kModifyOutgoingDamage && e.m_effectParam == 20);
    }

    [Fact]
    public void SpectrumNeedsBalancebladesLevel() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var balance = Occupy(duel, 4, 1, 992, MagicSchool.Balance, level: 11);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Fire Cat"], Resolve(duel, Cast(balance, krok, "Fire Cat")).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ PvP

    [Fact]
    public void NothingProcsInAPvpDuel() {
        var duel = NewDuel();
        duel.Duel.m_bPVP = true;
        var a = Occupy(duel, 0, Krok, 300);
        Occupy(duel, 1, Krok, 900);
        var storm = Occupy(duel, 4, 1, 710, MagicSchool.Storm, level: 20);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Storm Shark"], Resolve(duel, Cast(storm, a, "Storm Shark")).Select(NameOf).ToArray());
    }

}
