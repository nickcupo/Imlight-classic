using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

// CLASSIC: Briskbreeze Tower's scripted boss cheats (classic-data/creatures/boss-cheats-2009.yaml): the data, and the
// duel playing them (BossCheatDirector, the resolver's hooks).
public sealed class BossCheatsDataTests {

    private static BossCheats Real() => BossCheatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "creatures", "boss-cheats-2009.yaml"));

    [Fact]
    public void TheCanonicalProfileNamesTheFileAndTheEarlyProfileHasNone() {
        Assert.Equal("creatures/boss-cheats-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.BossCheats);
        Assert.Null(ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.BossCheats);   // the tower came in October 2009
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.BossCheats);
    }

    [Fact]
    public void TheTableHasTheTowersTwoBossesAndTheirDatedCheats() {
        var table = Real();
        Assert.Equal("WizardCity/Gauntlets/WC_Gauntlet_01", table.DungeonZone);
        Assert.Equal(2, table.Count);

        Assert.True(table.TryGet(164776, out var orrik));
        Assert.Equal(10, orrik.Floor);
        Assert.Equal(2, orrik.CastsPerRound);
        Assert.Equal(["MeteorStrikeBOSS01"], orrik.FreeSpellsAt(5000));
        Assert.Equal(["MeteorStrikeBOSS01", "EarthquakeBOSS01"], orrik.FreeSpellsAt(1999));
        Assert.NotNull(orrik.Interrupt);
        Assert.Equal([CheatTrigger.Hit], orrik.Interrupt!.On.ToArray());
        Assert.Equal(["Tower ShieldBOSS01"], orrik.Interrupt.Spells.ToArray());
        Assert.Equal("WC-ActorDialog_00000771", orrik.Interrupt.Message);
        Assert.False(orrik.Interrupt.Health.Contains(4000));
        Assert.True(orrik.Interrupt.Health.Contains(3999));
        Assert.True(orrik.Interrupt.Health.Contains(2000));
        Assert.False(orrik.Interrupt.Health.Contains(1999));
        Assert.Equal("Cleanse Ward", orrik.DestroyTraps!.Spell);
        Assert.True(orrik.DestroyTraps.OnPlaced);
        var stompers = Assert.Single(orrik.Summons);
        Assert.Equal((191270u, 3, 4000), (stompers.Creature, stompers.Count, stompers.HealthBelow!.Value));

        Assert.True(table.TryGet(164775, out var angrus));
        Assert.Equal(5, angrus.Floor);
        Assert.Equal(1, angrus.CastsPerRound);
        var ember = Assert.Single(angrus.Summons);
        Assert.Equal(191268u, ember.Creature);
        Assert.True(ember.Resummon);

        Assert.All(table.Bosses, b => Assert.True(b.SourceDate <= new DateOnly(2010, 5, 25)));
    }

    // Owner ruling 2026-10-04: no story boss of the cutoff cheated; only the optional tower's bosses do.
    [Fact]
    public void NoStoryBossHasCheats() {
        var table = Real();
        Assert.All(table.Bosses, b => Assert.StartsWith(table.DungeonZone + "/", b.Zone, StringComparison.OrdinalIgnoreCase));

        var stats = MobStatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "mob-stats-2009.yaml"));
        var decks = CreatureDecksLoader.Load(Path.Combine(ClassicDataFixture.Root, "creatures", "creature-decks-2009.yaml"));
        uint[] tower = [164775, 164776];
        var others = stats.HealthByTemplate.Keys.Select(t => (uint) t).Concat(decks.Decks.Select(d => d.Template))
            .Where(t => !tower.Contains(t)).Distinct().ToList();
        Assert.True(others.Count > 300);
        Assert.All(others, t => Assert.False(table.TryGet(t, out _), $"template {t} is not a tower boss but has cheats"));

        // The story's own bosses by name, as the dated wiki gives them (mob-stats-2009).
        var storyBosses = new[] { "Malistaire", "Lord Nightshade", "Foulgaze", "Rattlebones", "Lady Blackhope", "Krokopatra", "Meowiarty", "Jade Oni" };
        var lines = File.ReadAllLines(Path.Combine(ClassicDataFixture.Root, "progression", "mob-stats-2009.yaml"));
        var found = 0;
        for (var i = 0; i < lines.Length; i++) {
            if (storyBosses.Any(n => lines[i] == $"- name: '{n}'") && lines[i + 1].Trim().StartsWith("templates: [", StringComparison.Ordinal)) {
                foreach (var id in lines[i + 1].Trim()["templates: [".Length..].TrimEnd(']').Split(',')) {
                    Assert.False(table.TryGet(uint.Parse(id.Trim()), out _));
                    found++;
                }
            }
        }

        Assert.True(found >= 4, $"only {found} story boss templates found");
    }

    private const string Head = """
        kind: boss-cheats
        version: 1
        id: boss-cheats-test
        profiles: [late-2009]
        license_tag: own
        dungeon: {name: Tower, zone: WizardCity/Gauntlets/WC_Gauntlet_01, source: x, source_date: '2010-04-17'}
        bosses:
        """;

    private static BossCheats LoadInline(string bosses) {
        using var data = new TempClassicData();
        var dir = Path.Combine(data.Root, "creatures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "boss-cheats-test.yaml");
        File.WriteAllText(path, (Head + "\n" + bosses).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return BossCheatsLoader.Load(path);
    }

    [Fact]
    public void ABossOutsideTheDungeonIsRefused() {
        var error = Assert.Throws<ClassicDataException>(() => LoadInline("""
            - {template: 9999, name: Malistaire, floor: 1, zone: DragonSpire/DS_A3_Kings/Interiors/DS_Crystal_T8, source: x, source_date: '2010-05-20'}
            """));
        Assert.Contains("only the dungeon's bosses may cheat", error.Message);
    }

    [Fact]
    public void ExtraCastsNeedFreeSpellsAndASummonNeedsOneTrigger() {
        Assert.Contains("needs free_spells", Assert.Throws<ClassicDataException>(() => LoadInline("""
            - {template: 1, name: A, floor: 1, zone: WizardCity/Gauntlets/WC_Gauntlet_01/Room01, casts_per_round: 2, source: x, source_date: '2010-04-17'}
            """)).Message);
        Assert.Contains("exactly one of round and health_below", Assert.Throws<ClassicDataException>(() => LoadInline("""
            - template: 1
              name: A
              floor: 1
              zone: WizardCity/Gauntlets/WC_Gauntlet_01/Room01
              summons: [{creature: 5, name: B, count: 1, round: 1, health_below: 10, source: x}]
              source: x
              source_date: '2010-04-17'
            """)).Message);
        Assert.Equal(1, LoadInline("""
            - {template: 1, name: A, floor: 1, zone: WizardCity/Gauntlets/WC_Gauntlet_01/Room01, source: x, source_date: '2010-04-17'}
            """).Count);
    }

}

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class BossCheatDuelTests : IDisposable {

    private const int FireIndex = 9102;
    private const uint Base = uint.MaxValue - 500;
    private const uint OrrikTemplate = 164776;
    private const uint AngrusTemplate = 164775;
    private const uint StoryBossTemplate = 4242;
    private readonly Dictionary<int, MagicSchoolTemplate> _schools;
    private readonly Dictionary<string, (Spell, SpellTemplate)> _spells = [];

    public BossCheatDuelTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-bosscheat-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }

        _schools = (Dictionary<int, MagicSchoolTemplate>) typeof(MagicSchools)
            .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _schools[FireIndex] = new MagicSchoolTemplate { m_schoolName = "Fire", m_schoolIndex = 2 };

        Add(0, "MeteorStrikeBOSS01", 0, Effect(kSpellEffects.kDamage, 750, kEffectTarget.kEnemyTeamAllAtOnce));
        Add(1, "EarthquakeBOSS01", 6, Effect(kSpellEffects.kDamage, 750, kEffectTarget.kEnemyTeam));
        Add(2, "Tower ShieldBOSS01", 0, Effect(kSpellEffects.kModifyIncomingDamage, -65, kEffectTarget.kFriendlySingle));
        Add(3, "Cleanse Ward", 0, new SpellEffect {
            m_effectType = kSpellEffects.kRemoveWard, m_effectParam = 1, m_sDamageType = "All",
            m_effectTarget = kEffectTarget.kFriendlySingle, m_disposition = kHangingDisposition.kHarmful,
        });
        Add(4, "Wizard Bolt", 1, Effect(kSpellEffects.kDamage, 300, kEffectTarget.kEnemySingle));
        Add(5, "Wizard Drain", 1, Effect(kSpellEffects.kStealHealth, 300, kEffectTarget.kEnemySingle));
        Add(6, "Wizard Trap", 0, Effect(kSpellEffects.kModifyIncomingDamage, 30, kEffectTarget.kEnemySingle));
        Add(7, "Wizard Heal", 1, Effect(kSpellEffects.kHeal, 300, kEffectTarget.kFriendlySingle));
        BossCheatDirector.ResolveSpellForTests = name => _spells.TryGetValue(name, out var s) ? s : null;

        // A cast's cinematic time reads the client's cinematics; without the client files they are an empty set.
        var lazy = typeof(SpellCinematics).BaseType!.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (!(bool) lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy)!) {
            lazy.GetType().GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(lazy, RuntimeHelpers.GetUninitializedObject(typeof(SpellCinematics)));
            lazy.GetType().GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(lazy, null);
        }

        var orrik = new BossCheat {
            Template = OrrikTemplate, Name = "Orrik Nightglider", Floor = 10, Zone = "WizardCity/Gauntlets/WC_Gauntlet_01/Room10",
            CastsPerRound = 2,
            FreeSpells = [new BossFreeSpell("MeteorStrikeBOSS01", new HealthWindow(null, null)),
                          new BossFreeSpell("EarthquakeBOSS01", new HealthWindow(2000, null))],
            Interrupt = new BossInterrupt([CheatTrigger.Hit], ["Tower ShieldBOSS01"], "WC-ActorDialog_00000771", 8,
                new HealthWindow(4000, 2000), "test"),
            DestroyTraps = new BossTrapBreak("Cleanse Ward", true, new HealthWindow(2000, null), null, "test"),
            Summons = [new BossSummon(191270, "Stomper", 3, null, 4000, false, "test")],
            Source = "test", SourceDate = new DateOnly(2010, 5, 22),
        };
        var angrus = new BossCheat {
            Template = AngrusTemplate, Name = "Angrus Hollowsoul", Floor = 5, Zone = "WizardCity/Gauntlets/WC_Gauntlet_01/Room05",
            CastsPerRound = 1, FreeSpells = [],
            Summons = [new BossSummon(191268, "Exploding Ember", 1, 2, null, true, "test")],
            Source = "test", SourceDate = new DateOnly(2010, 4, 28),
        };
        ClassicProgression.UseBossCheatsForTests(BossCheats.ForTests("WizardCity/Gauntlets/WC_Gauntlet_01", orrik, angrus));
    }

    public void Dispose() {
        ClassicProgression.UseBossCheatsForTests(null);
        BossCheatDirector.ResolveSpellForTests = null;
        _schools.Remove(FireIndex);
        foreach (var (_, (spell, _)) in _spells) Cache.Remove(spell.m_templateID);
        ClassicRuntime.ResetForTests();
    }

    private static IDictionary<ulong, CoreTemplate> Cache => (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
        .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static SpellEffect Effect(kSpellEffects type, int param, kEffectTarget target) => new() {
        m_effectType = type, m_effectParam = param, m_sDamageType = "Fire", m_effectTarget = target,
    };

    private void Add(uint offset, string name, byte rank, SpellEffect effect) {
        var id = Base + offset;
        var template = new SpellTemplate {
            m_name = name, m_spellBase = name, m_accuracy = 100, m_sMagicSchoolName = "Fire",
            m_spellRank = new SpellRank { m_spellRank = rank }, m_effects = [effect],
        };
        Cache[id] = template;
        _spells[name] = (new Spell {
            m_templateID = id, m_accuracy = 100, m_magicSchoolID = StringHash.Compute("Fire"),
            m_pipCost = new SpellRank { m_spellRank = rank },
        }, template);
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, ulong template, int health) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = template });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = Math.Max(health, 6000);
        stats.m_currentHitpoints = health;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        var combatStats = (WizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(WizGameStats));
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_hangingEffects = [], m_pGameStats = combatStats, m_pipCount = new PipCount(),
        });
        circle.AddedToDuel = true;
        return circle;
    }

    private (CombatDuelComponent Duel, CombatDuelSubCircle Boss, CombatDuelSubCircle Wizard, List<(CombatDuelSubCircle, BossSummon)> Summons)
        Fight(uint bossTemplate, int bossHealth) {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var boss = Occupy(duel, 0, bossTemplate, bossHealth);
        var wizard = Occupy(duel, 4, 1, 50000);
        wizard._combatDeck = new CombatDeck([new CombatDeckSpellData { TemplateId = Base + 4, Quantity = 5 }], [], 7);
        var summons = new List<(CombatDuelSubCircle, BossSummon)>();
        duel.BossCheats.SummonForTests = (b, s) => summons.Add((b, s));
        return (duel, boss, wizard, summons);
    }

    private QueuedCombatAction Cast(CombatDuelSubCircle caster, CombatDuelSubCircle target, string spell)
        => new() { SpellCaster = caster, SelectedTarget = target, Spell = _spells[spell].Item1, SpellTemplate = _spells[spell].Item2 };

    [Fact]
    public void ABossCastsItsCardThenAFreeSpellEachRoundAndAStoryBossDoesNot() {
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, 6000);
        var story = Occupy(duel, 1, StoryBossTemplate, 3000);
        var queue = new List<QueuedCombatAction> { new() { SpellCaster = boss }, new() { SpellCaster = story }, Cast(wizard, boss, "Wizard Bolt") };
        duel.BossCheats.AddExtraCasts(queue);

        Assert.Equal(4, queue.Count);
        var extra = queue[1];
        Assert.Same(boss, extra.SpellCaster);
        Assert.Equal(BossCheatKind.ExtraCast, extra.Cheat!.Kind);
        Assert.False(extra.Cheat.Interrupt);
        Assert.Equal("MeteorStrikeBOSS01", extra.SpellTemplate.m_name);   // Earthquake only below 2000
        Assert.Same(wizard, extra.SelectedTarget);
        Assert.Same(story, queue[2].SpellCaster);
        Assert.Null(queue[2].Cheat);
        Assert.Null(BossCheatDirector.CheatOf(story));
    }

    [Fact]
    public void BelowTwoThousandTheExtraCastMayBeTheEarthquakeAndAStunnedBossCastsNothingExtra() {
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, 1500);
        var names = new HashSet<string>();
        for (var round = 0; round < 40; round++) {
            var queue = new List<QueuedCombatAction> { new() { SpellCaster = boss } };
            duel.BossCheats.AddExtraCasts(queue);
            names.Add(queue[1].SpellTemplate.m_name);
        }

        Assert.Equal(new HashSet<string> { "MeteorStrikeBOSS01", "EarthquakeBOSS01" }, names);

        boss.CombatParticipant.m_stunned = 1;
        var stunned = new List<QueuedCombatAction> { new() { SpellCaster = boss } };
        duel.BossCheats.AddExtraCasts(stunned);
        Assert.Single(stunned);
    }

    [Theory]
    [InlineData(3500, "Wizard Bolt", true)]    // a hit between 4000 and 2000: Tower Shield, out of turn
    [InlineData(5000, "Wizard Bolt", false)]   // above 4000: no interrupt
    [InlineData(1900, "Wizard Bolt", false)]   // below 2000: no more Tower Shields
    [InlineData(3500, "Wizard Drain", false)]  // a drain is not a hit
    [InlineData(3500, "Wizard Heal", false)]   // a heal on the wizards' side is not a hit
    public void AHitBetweenFourAndTwoThousandIsAnsweredWithAnOutOfTurnTowerShield(int health, string spell, bool interrupts) {
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, health);
        var action = Cast(wizard, spell == "Wizard Heal" ? wizard : boss, spell);
        duel.BossCheats.BeforeAction(action);
        boss.ParticipantGameStats.m_currentHitpoints -= spell == "Wizard Heal" ? 0 : 300;
        var responses = duel.BossCheats.AfterAction(action);

        if (!interrupts) {
            Assert.DoesNotContain(responses, r => r.Cheat!.Kind == BossCheatKind.Interrupt);
            return;
        }

        var answer = Assert.Single(responses);
        Assert.Equal(BossCheatKind.Interrupt, answer.Cheat!.Kind);
        Assert.True(answer.Cheat.Interrupt);
        Assert.Equal("WC-ActorDialog_00000771", answer.Cheat.Message);
        Assert.Equal("Tower ShieldBOSS01", answer.SpellTemplate.m_name);
        Assert.Same(boss, answer.SpellCaster);
        Assert.Same(boss, answer.SelectedTarget);   // a shield goes on the boss itself
        Assert.Contains(duel.BossCheats.Events, e => e.Contains("interrupt (Hit by slot 4)"));
    }

    [Fact]
    public void ATrapOnTheBossIsBrokenAtOnceWithCleanseWard() {
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, 6000);
        var trap = Cast(wizard, boss, "Wizard Trap");
        duel.BossCheats.BeforeAction(trap);
        boss._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = 30, m_sDamageType = "Fire" });
        var responses = duel.BossCheats.AfterAction(trap);

        var breaker = Assert.Single(responses);
        Assert.Equal(BossCheatKind.TrapBreak, breaker.Cheat!.Kind);
        Assert.True(breaker.Cheat.Interrupt);
        Assert.Equal("Cleanse Ward", breaker.SpellTemplate.m_name);
        Assert.Same(boss, breaker.SelectedTarget);

        // A boss that does not break traps leaves it.
        var (angrusDuel, angrus, angrusWizard, _) = Fight(AngrusTemplate, 4000);
        var angrusTrap = Cast(angrusWizard, angrus, "Wizard Trap");
        angrusDuel.BossCheats.BeforeAction(angrusTrap);
        angrus._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = 30 });
        Assert.Empty(angrusDuel.BossCheats.AfterAction(angrusTrap));
    }

    [Fact]
    public void BelowTwoThousandItAlsoBreaksATrapInItsOwnTurn() {
        var (duel, boss, _, _) = Fight(OrrikTemplate, 1500);
        boss._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = 25 });
        var queue = new List<QueuedCombatAction> { new() { SpellCaster = boss } };
        duel.BossCheats.AddExtraCasts(queue);
        Assert.Equal(3, queue.Count);
        Assert.Equal(BossCheatKind.RoundTrapBreak, queue[1].Cheat!.Kind);
        Assert.Equal(BossCheatKind.ExtraCast, queue[2].Cheat!.Kind);
    }

    [Fact]
    public void TheStompersComeWithTheHitThatTakesHimBelowFourThousandOnlyOnce() {
        var (duel, boss, wizard, summons) = Fight(OrrikTemplate, 4100);
        var hit = Cast(wizard, boss, "Wizard Bolt");
        duel.BossCheats.BeforeAction(hit);
        boss.ParticipantGameStats.m_currentHitpoints = 4050;
        duel.BossCheats.AfterAction(hit);
        Assert.Empty(summons);

        duel.BossCheats.BeforeAction(hit);
        boss.ParticipantGameStats.m_currentHitpoints = 3700;
        duel.BossCheats.AfterAction(hit);
        var (by, summon) = Assert.Single(summons);
        Assert.Same(boss, by);
        Assert.Equal((191270u, 3), (summon.Creature, summon.Count));

        duel.BossCheats.BeforeAction(hit);
        boss.ParticipantGameStats.m_currentHitpoints = 3000;
        duel.BossCheats.AfterAction(hit);
        Assert.Single(summons);
    }

    [Fact]
    public void TheEmberComesInRoundTwoAndAgainARoundAfterItFalls() {
        var (duel, boss, _, summons) = Fight(AngrusTemplate, 4800);
        duel.BossCheats.NewRound(1);
        Assert.Empty(summons);
        duel.BossCheats.NewRound(2);
        Assert.Single(summons);

        // The Ember is in the duel (slot 1, owned by Angrus), then falls.
        var ember = Occupy(duel, 1, 191268, 1000);
        CombatRegressionTests.SetProperty(ember, "IsSummonedMinion", true);
        ember.CaptureMinionOwner(boss.SlotIndex);
        duel.BossCheats.NewRound(3);
        Assert.Single(summons);
        ember.ParticipantGameStats.m_currentHitpoints = 0;
        duel.BossCheats.NewRound(4);
        Assert.Equal(2, summons.Count);
    }

    [Fact]
    public void TheResolverPlaysTheInterruptRightAfterTheHitAsAnOutOfTurnFreeCast() {
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, 3500);
        var list = Resolve(duel, Cast(wizard, boss, "Wizard Bolt"));

        Assert.Equal(2, list.Count);
        Assert.Equal(wizard.SlotIndex, list[0].m_spellCaster);
        Assert.False(list[0].m_interrupt);
        Assert.Equal(boss.SlotIndex, list[1].m_spellCaster);
        Assert.True(list[1].m_interrupt);
        Assert.Equal("WC-ActorDialog_00000771", list[1].m_stringKeyMessage);
        Assert.Equal(3200, boss.ParticipantGameStats.m_currentHitpoints);
        // The Tower Shield is on him, and it cost nothing.
        Assert.Contains(boss._hangingEffects, w => w.m_effectType == kSpellEffects.kModifyIncomingDamage && w.m_effectParam == -65);
        Assert.Equal(0, boss.CombatParticipant.m_pipCount.m_genericPips);
    }

    [Fact]
    public void TheResolverPlaysTheExtraCastForNoPips() {
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, 6000);
        boss.CombatParticipant.m_pipCount.m_genericPips = 0;
        var resolver = new CombatResolver(duel.Duel, duel.SubCircles);
        resolver.Reset();
        var queue = Queue(resolver);
        queue.Add(new QueuedCombatAction { SpellCaster = boss });
        resolver.ApplyQueuedCombatActions(out var actions);

        var cast = actions.m_actionList.Single(a => a.m_spell is not null);
        Assert.Equal(boss.SlotIndex, cast.m_spellCaster);
        Assert.Equal(_spells["MeteorStrikeBOSS01"].Item1.m_templateID, cast.m_spell.m_templateID);
        Assert.Equal(50000 - 750, wizard.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(0, boss.CombatParticipant.m_pipCount.m_genericPips);
    }

    [Fact]
    public void WithoutTheProfileRuleNoBossCheats() {
        ClassicProgression.UseBossCheatsForTests(null);
        var (duel, boss, wizard, _) = Fight(OrrikTemplate, 3500);
        Assert.Null(BossCheatDirector.CheatOf(boss));
        var list = Resolve(duel, Cast(wizard, boss, "Wizard Bolt"));
        Assert.Single(list);
    }

    private static List<QueuedCombatAction> Queue(CombatResolver resolver) => (List<QueuedCombatAction>) typeof(CombatResolver)
        .GetField("_queuedCombatActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;

    private static List<CombatAction> Resolve(CombatDuelComponent duel, QueuedCombatAction action) {
        var resolver = new CombatResolver(duel.Duel, duel.SubCircles);
        resolver.Reset();
        Queue(resolver).Add(action);
        var list = new CombatActionListObj { m_actionList = [] };
        typeof(CombatResolver).GetMethod("ProcessQueuedActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(resolver, [list]);
        return list.m_actionList;
    }

}
