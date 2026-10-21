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
        Add("PetPower-Combat-IceArmor-01", MagicSchool.Ice, Effect(kSpellEffects.kAbsorbDamage, 125, kEffectTarget.kSelf, "All"), rank: 1);
        Add("Link", MagicSchool.Fire, Burn(180), rank: 2);
        Add("Fire Elf", MagicSchool.Fire, Burn(210), rank: 2);
        Add("Thunder Snake", MagicSchool.Storm, Effect(kSpellEffects.kDamageOverTime, 180, kEffectTarget.kEnemySingle, "Storm", rounds: 3), rank: 1);
        Add("Krok Hit", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 300, kEffectTarget.kEnemySingle));
        Add("Big Krok Hit", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 700, kEffectTarget.kEnemySingle));
        Add("Storm Shark", MagicSchool.Storm, Effect(kSpellEffects.kDamage, 400, kEffectTarget.kEnemySingle, "Storm"), rank: 3);
        Add("Tempest", MagicSchool.Storm, Effect(kSpellEffects.kDamage, 400, kEffectTarget.kEnemyTeam, "Storm"), rank: 3);
        Add("Troll", MagicSchool.Myth, Effect(kSpellEffects.kDamage, 190, kEffectTarget.kEnemySingle, "Myth"), rank: 2);
        Add("Minion Bonk", MagicSchool.Myth, Effect(kSpellEffects.kDamage, 100, kEffectTarget.kEnemySingle, "Myth"));
        AddMany("PetPower-Combat-DrawPower-01", MagicSchool.Myth, rank: 1,
            Effect(kSpellEffects.kInstantKill, 10000, kEffectTarget.kMinion, "Myth"), Effect(kSpellEffects.kModifyPips, 2, kEffectTarget.kSelf, "Myth"));
        // The minion cards summon nothing here (param 0): the spawn needs the client's creature templates and a live zone.
        Add("Minion Myth 000", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"));
        Add("Minion Myth 001", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"));
        Add("Minion Myth 002", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"));
        Add("Minion Myth 003", MagicSchool.Myth, Effect(kSpellEffects.kSummonCreature, 0, kEffectTarget.kSelf, "Myth"), rank: 3);
        Add("Pixie Life", MagicSchool.Life, Effect(kSpellEffects.kHeal, 420, kEffectTarget.kFriendlySingle, "Life"), rank: 2);
        Add("Satyr", MagicSchool.Life, Effect(kSpellEffects.kHeal, 860, kEffectTarget.kFriendlySingle, "Life"), rank: 4, accuracy: 90);
        Add("Ghoul", MagicSchool.Death, Effect(kSpellEffects.kStealHealth, 160, kEffectTarget.kEnemySingle, "Death"), rank: 2);
        Add("Pet - Beguile", MagicSchool.Death, Effect(kSpellEffects.kMindControl, 1, kEffectTarget.kEnemySingle, "Death"), rank: 3, accuracy: 85);
        Add("Hex", MagicSchool.Balance, Effect(kSpellEffects.kModifyIncomingDamage, 30, kEffectTarget.kEnemySingle, "All"));
        Add("Balanceblade", MagicSchool.Balance, Effect(kSpellEffects.kModifyOutgoingDamage, 25, kEffectTarget.kFriendlySingle, "All"));
        Add("Scorpion", MagicSchool.Balance, Effect(kSpellEffects.kDamage, 180, kEffectTarget.kEnemySingle, "Balance"), rank: 2);
        AddMany("PetPower-Combat-Hydra-01", MagicSchool.Balance, rank: 6,
            Effect(kSpellEffects.kDamage, 190, kEffectTarget.kEnemySingle, "Fire"), Effect(kSpellEffects.kDamage, 190, kEffectTarget.kEnemySingle, "Ice"),
            Effect(kSpellEffects.kDamage, 190, kEffectTarget.kEnemySingle, "Storm"));
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

    // ------------------------------------------------------------ Ice: Permafrost

    private QueuedCombatAction Hit(CombatDuelSubCircle creature, CombatDuelSubCircle target, string spell = "Krok Hit") => new() {
        // A creature's free cast, so the test needs no creature deck.
        SpellCaster = creature, SelectedTarget = target, Spell = _spells[spell].Item1, SpellTemplate = _spells[spell].Item2,
        Cheat = new BossCheatCast(BossCheatKind.ExtraCast, Interrupt: false, Message: null),
    };

    [Fact]
    public void AShieldThatSoftensAHitFormsAFreeIceArmorOnceARound() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var ice = Occupy(duel, 4, 1, 1091, MagicSchool.Ice, level: 20);
        ice._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Hit(krok, ice));
        Assert.Equal(["Krok Hit", "PetPower-Combat-IceArmor-01"], list.Select(NameOf).ToArray());
        Assert.Equal(ice.SlotIndex, list[1].m_spellCaster);
        Assert.True(list[1].m_interrupt);
        Assert.Equal(1091 - 150, ice.ParticipantGameStats.m_currentHitpoints);   // 300 halved by Tower Shield
        Assert.Equal(125, Assert.Single(ice._hangingEffects, e => e.m_effectType == kSpellEffects.kAbsorbDamage).m_paramPerRound);

        // Later the same round, another shielded hit: the armor and the shield take it, and no second armor forms.
        ice._hangingEffects.Add(Shield());
        list = Resolve(duel, Hit(krok, ice));
        Assert.Equal(["Krok Hit"], list.Select(NameOf).ToArray());
        Assert.Equal(1091 - 150 - 87, ice.ParticipantGameStats.m_currentHitpoints);   // 300 - 125 absorbed, then halved
        Assert.DoesNotContain(ice._hangingEffects, e => e.m_effectType is kSpellEffects.kAbsorbDamage or kSpellEffects.kModifyIncomingDamage);

        // A new round: the next shielded hit forms another.
        ice._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(2);
        list = Resolve(duel, Hit(krok, ice));
        Assert.Equal(["Krok Hit", "PetPower-Combat-IceArmor-01"], list.Select(NameOf).ToArray());
    }

    [Theory]
    [InlineData(MagicSchool.Ice, 15, true)]    // below Tower Shield's level
    [InlineData(MagicSchool.Life, 20, true)]   // not an Ice wizard
    [InlineData(MagicSchool.Ice, 20, false)]   // no shield to soften the hit
    public void PermafrostNeedsAnIceWizardAndAShield(MagicSchool school, int level, bool shielded) {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var wizard = Occupy(duel, 4, 1, 1091, school, level);
        if (shielded) wizard._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Hit(krok, wizard));

        Assert.Equal(["Krok Hit"], list.Select(NameOf).ToArray());
    }

    [Fact]
    public void ATeammatesSpellNeverFormsPermafrost() {
        var duel = NewDuel();
        Occupy(duel, 0, Krok, 1200);
        var ice = Occupy(duel, 4, 1, 1091, MagicSchool.Ice, level: 20);
        var fire = Occupy(duel, 5, 1, 836, MagicSchool.Fire, level: 20);
        ice._hangingEffects.Add(Shield());
        duel.Mastery.NewRound(1);
        duel.Mastery.BeforeAction(Cast(fire, ice, "Krok Hit"));
        ice._hangingEffects.Clear();
        ice.ParticipantGameStats.m_currentHitpoints -= 100;

        Assert.Empty(duel.Mastery.AfterAction(Cast(fire, ice, "Krok Hit")));
    }

    // ------------------------------------------------------------ Storm: Chain Lightning

    [Fact]
    public void AStormSpellThatDefeatsItsTargetLeapsToTheStrongestEnemyAtMostTwiceARound() {
        var duel = NewDuel();
        var a = Occupy(duel, 0, Krok, 300);
        var b = Occupy(duel, 1, Krok, 380);
        var c = Occupy(duel, 2, Krok, 350);
        var d = Occupy(duel, 3, Krok, 390);
        var storm = Occupy(duel, 4, 1, 710, MagicSchool.Storm, level: 20);
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(storm, a, "Storm Shark"));

        Assert.Equal(["Storm Shark", "Storm Shark", "Storm Shark"], list.Select(NameOf).ToArray());
        Assert.Equal([a.SlotIndex, d.SlotIndex, b.SlotIndex], list.Select(x => x.m_targetSubcircleList.Single()).ToArray());
        Assert.True(list.Skip(1).All(x => x.m_interrupt));
        Assert.False(a.IsAlive || b.IsAlive || d.IsAlive);
        Assert.Equal(350, c.ParticipantGameStats.m_currentHitpoints);   // two leaps a round
        Assert.Equal(4, storm.CombatParticipant.m_pipCount.m_genericPips); // only the first Storm Shark cost pips
    }

    [Fact]
    public void ALeapThatDoesNotDefeatStops() {
        var duel = NewDuel();
        var a = Occupy(duel, 0, Krok, 300);
        var b = Occupy(duel, 1, Krok, 900);
        var storm = Occupy(duel, 4, 1, 710, MagicSchool.Storm, level: 20);
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(storm, a, "Storm Shark"));

        Assert.Equal(2, list.Count);
        Assert.Equal(500, b.ParticipantGameStats.m_currentHitpoints);
    }

    [Theory]
    [InlineData("Tempest", 20)]       // an area spell never leaps
    [InlineData("Storm Shark", 9)]    // below Storm Shark's level
    public void ChainLightningNeedsASingleTargetStormSpellAndTheLevel(string spell, int level) {
        var duel = NewDuel();
        var a = Occupy(duel, 0, Krok, 300);
        Occupy(duel, 1, Krok, 900);
        var storm = Occupy(duel, 4, 1, 710, MagicSchool.Storm, level);
        duel.Mastery.NewRound(1);

        Assert.Equal([spell], Resolve(duel, Cast(storm, a, spell)).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Death: Puppeteer

    [Fact]
    public void ADrainThatLeavesACreatureAtHalfHealthBeguilesItOncePerCreature() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        krok.ParticipantGameStats.m_currentHitpoints = 700;
        var death = Occupy(duel, 4, 1, 915, MagicSchool.Death, level: 20);
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(death, krok, "Ghoul"));
        Assert.Equal(["Ghoul", "Pet - Beguile"], list.Select(NameOf).ToArray());
        Assert.Equal(1, krok.BeguiledActions);

        krok.BeguiledActions = 0;
        duel.Mastery.NewRound(2);
        Assert.Equal(["Ghoul"], Resolve(duel, Cast(death, krok, "Ghoul")).Select(NameOf).ToArray());
    }

    [Fact]
    public void AHealthyCreatureIsNotBeguiled() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 1200);
        var death = Occupy(duel, 4, 1, 915, MagicSchool.Death, level: 20);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Ghoul"], Resolve(duel, Cast(death, krok, "Ghoul")).Select(NameOf).ToArray());
    }

    // ------------------------------------------------------------ Myth: Evolution

    private static CombatDuelSubCircle Minion(CombatDuelComponent duel, int slot, ulong creature, CombatDuelSubCircle owner) {
        var minion = Occupy(duel, slot, creature, 600);
        CombatRegressionTests.SetProperty(minion, "IsSummonedMinion", true);
        minion.CaptureMinionOwner(owner.SlotIndex);
        return minion;
    }

    [Fact]
    public void AMinionThatHasActedTwiceEvolvesIntoTheNextMonster() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var myth = Occupy(duel, 4, 1, 842, MagicSchool.Myth, level: 20);
        myth.CombatParticipant.m_pipCount.m_genericPips = 3;
        var golem = Minion(duel, 5, 35972, myth);
        duel.Mastery.NewRound(1);

        Assert.Equal(["Minion Bonk"], Resolve(duel, Cast(golem, krok, "Minion Bonk")).Select(NameOf).ToArray());
        duel.Mastery.NewRound(2);
        var list = Resolve(duel, Cast(golem, krok, "Minion Bonk"));

        Assert.Equal(["Minion Bonk", "PetPower-Combat-DrawPower-01", "Minion Myth 001"], list.Select(NameOf).ToArray());
        Assert.All(list.Skip(1), a => Assert.Equal(myth.SlotIndex, a.m_spellCaster));
        Assert.False(golem.IsAlive);                                       // the Golem is given up ...
        Assert.Equal(5, myth.CombatParticipant.m_pipCount.m_genericPips);  // ... for Draw Power's 2 pips
    }

    [Theory]
    [InlineData(35680, 17, false)]   // a Troll needs Cyclops Minion's level (18)
    [InlineData(35680, 20, true)]
    [InlineData(35682, 50, false)]   // a Minotaur is the last monster
    public void EvolutionNeedsTheNextCardsLevelAndANextMonster(ulong creature, int level, bool evolves) {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var myth = Occupy(duel, 4, 1, 842, MagicSchool.Myth, level);
        var minion = Minion(duel, 5, creature, myth);
        duel.Mastery.NewRound(1);
        Resolve(duel, Cast(minion, krok, "Minion Bonk"));

        var names = Resolve(duel, Cast(minion, krok, "Minion Bonk")).Select(NameOf).ToArray();

        Assert.Equal(evolves, names.Contains("Minion Myth 002"));
    }

    [Fact]
    public void NoEvolutionWithoutAFreeSeat() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 5000);
        var myth = Occupy(duel, 4, 1, 842, MagicSchool.Myth, level: 20);
        var golem = Minion(duel, 5, 35972, myth);
        Occupy(duel, 6, 1, 800, MagicSchool.Fire, level: 20);
        Occupy(duel, 7, 1, 800, MagicSchool.Ice, level: 20);
        Resolve(duel, Cast(golem, krok, "Minion Bonk"));

        Assert.Equal(["Minion Bonk"], Resolve(duel, Cast(golem, krok, "Minion Bonk")).Select(NameOf).ToArray());
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

    // ------------------------------------------------------------ Balance: Equilibrium

    [Fact]
    public void AHitThatUsesBothYourBladeAndTheirTrapTipsTheScalesIntoAFreeHydra() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 2000);
        var balance = Occupy(duel, 4, 1, 992, MagicSchool.Balance, level: 20);
        Resolve(duel, Cast(balance, balance, "Balanceblade"));
        Resolve(duel, Cast(balance, krok, "Hex"));
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(balance, krok, "Scorpion"));

        Assert.Equal(["Scorpion", "PetPower-Combat-Hydra-01"], list.Select(NameOf).ToArray());
        // Scorpion 180 x 1.25 blade, x 1.3 trap = 292; then the Hydra 3 x 190 = 570.
        Assert.Equal(2000 - 292 - 570, krok.ParticipantGameStats.m_currentHitpoints);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EquilibriumNeedsBothABladeAndATrap(bool blade, bool trap) {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 2000);
        var balance = Occupy(duel, 4, 1, 992, MagicSchool.Balance, level: 20);
        if (blade) Resolve(duel, Cast(balance, balance, "Balanceblade"));
        if (trap) Resolve(duel, Cast(balance, krok, "Hex"));
        duel.Mastery.NewRound(1);

        Assert.Equal(["Scorpion"], Resolve(duel, Cast(balance, krok, "Scorpion")).Select(NameOf).ToArray());
    }

    [Fact]
    public void WhenTheHitDefeatsTheTargetTheHydraFindsTheHealthiestEnemy() {
        var duel = NewDuel();
        var krok = Occupy(duel, 0, Krok, 200);
        var big = Occupy(duel, 1, Krok, 2000);
        var balance = Occupy(duel, 4, 1, 992, MagicSchool.Balance, level: 20);
        Resolve(duel, Cast(balance, balance, "Balanceblade"));
        Resolve(duel, Cast(balance, krok, "Hex"));
        duel.Mastery.NewRound(1);

        var list = Resolve(duel, Cast(balance, krok, "Scorpion"));

        Assert.Equal(big.SlotIndex, list[1].m_targetSubcircleList.Single());
        Assert.Equal(2000 - 570, big.ParticipantGameStats.m_currentHitpoints);
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
