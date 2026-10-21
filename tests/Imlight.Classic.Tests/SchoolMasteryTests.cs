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

    private const int FireIndex = 9201, IceIndex = 9202, StormIndex = 9203;
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
        _schools[FireIndex] = new MagicSchoolTemplate { m_schoolName = "Fire", m_schoolIndex = 2 };
        _schools[IceIndex] = new MagicSchoolTemplate { m_schoolName = "Ice", m_schoolIndex = 3 };
        _schools[StormIndex] = new MagicSchoolTemplate { m_schoolName = "Storm", m_schoolIndex = 4 };

        // The client templates the procs cast (their r806919 shapes) and the fight's cards.
        Add("Pet - Detonate", MagicSchool.Fire, Effect(kSpellEffects.kDetonateOverTime, 1, kEffectTarget.kEnemySingle, "All"), accuracy: 75);
        Add("PetPower-Combat-IceArmor-01", MagicSchool.Ice, Effect(kSpellEffects.kAbsorbDamage, 125, kEffectTarget.kSelf, "All"), rank: 1);
        Add("Link", MagicSchool.Fire, Burn(180), rank: 2);
        Add("Fire Elf", MagicSchool.Fire, Burn(210), rank: 2);
        Add("Thunder Snake", MagicSchool.Storm, Effect(kSpellEffects.kDamageOverTime, 180, kEffectTarget.kEnemySingle, "Storm", rounds: 3), rank: 1);
        Add("Krok Hit", MagicSchool.Fire, Effect(kSpellEffects.kDamage, 300, kEffectTarget.kEnemySingle));
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
        _schools.Remove(FireIndex);
        _schools.Remove(IceIndex);
        _schools.Remove(StormIndex);
        foreach (var (_, (spell, _)) in _spells) Cache.Remove(spell.m_templateID);
        ClassicRuntime.ResetForTests();
    }

    private static IDictionary<ulong, CoreTemplate> Cache => (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
        .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static SpellEffect Effect(kSpellEffects type, int param, kEffectTarget target, string school = "Fire", int rounds = 0) => new() {
        m_effectType = type, m_effectParam = param, m_sDamageType = school, m_effectTarget = target, m_numRounds = rounds,
    };

    private static SpellEffect Burn(int total) => Effect(kSpellEffects.kDamageOverTime, total, kEffectTarget.kEnemySingle, "Fire", rounds: 3);

    private void Add(string name, MagicSchool school, SpellEffect effect, byte rank = 0, uint accuracy = 100) {
        var id = Base + _next++;
        effect.m_spellTemplateID = id;   // as a loaded template's effects carry it (ward matching groups by it)
        var template = new SpellTemplate {
            m_name = name, m_spellBase = name, m_accuracy = (byte) accuracy, m_sMagicSchoolName = school.ToString(),
            m_spellRank = new SpellRank { m_spellRank = rank }, m_effects = [effect],
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

    private QueuedCombatAction Hit(CombatDuelSubCircle creature, CombatDuelSubCircle target) => new() {
        // A creature's free cast, so the test needs no creature deck.
        SpellCaster = creature, SelectedTarget = target, Spell = _spells["Krok Hit"].Item1, SpellTemplate = _spells["Krok Hit"].Item2,
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

}
