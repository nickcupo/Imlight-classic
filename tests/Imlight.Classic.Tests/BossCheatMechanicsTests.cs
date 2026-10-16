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

// CLASSIC: the Waterworks boss-cheat primitives (classic-data/creatures/boss-cheats-waterworks-2011.yaml): the data, the
// several-files rule, and each primitive in a duel (BossCheatDirector.Mechanics).
public sealed class BossCheatMechanicsDataTests {

    private const uint Luska = 439083, Sylster = 439075, Spellhammer = 469333, WaterWing = 469334;

    private static BossCheats Waterworks() => BossCheatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "creatures", "boss-cheats-waterworks-2011.yaml"));
    private static BossCheats Briskbreeze() => BossCheatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "creatures", "boss-cheats-2009.yaml"));

    [Fact]
    public void WintertuskListsBothDungeonsAndOlderProfilesKeepOneFile() {
        var wintertusk = ClassicDataFixture.LoadProfile("wintertusk-2011").Rules;
        Assert.Equal(["creatures/boss-cheats-2009.yaml", "creatures/boss-cheats-waterworks-2011.yaml"], wintertusk.BossCheatFiles.ToArray());
        Assert.Equal("creatures/boss-cheats-2009.yaml", wintertusk.BossCheats);

        foreach (var id in new[] { "late-2009", "october-2010-arc1", "celestia-2010" }) {
            var rules = ClassicDataFixture.LoadProfile(id).Rules;
            Assert.Equal(["creatures/boss-cheats-2009.yaml"], rules.BossCheatFiles.ToArray());
        }

        Assert.Empty(ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.BossCheatFiles);
    }

    [Fact]
    public void TheWaterworksFileHasLuskasInkAndSylstersCycle() {
        var table = Waterworks();
        Assert.Equal("WizardCity/Gauntlets/WC_Triton_Gauntlet1", table.DungeonZone);
        Assert.Equal(4, table.Count);
        Assert.Equal(["wintertusk-2011"], table.Profiles.ToArray());

        Assert.True(table.TryGet(Luska, out var luska));
        var marks = luska.Marks!;
        Assert.Equal(new (int, int?)[] { (1, null), (2, 75), (3, 50) }, marks.Seats.Select(s => (s.Seat, s.HealthBelowPercent)).ToArray());
        Assert.Equal(("Black Mantle Fire", "Hex_TriBoss"), (marks.MarkedSpell, marks.UnmarkedSpell));
        Assert.True(marks.SureHit);
        Assert.Equal([1], marks.SeatsAt(21150, 21150).ToArray());
        Assert.Equal([1, 2], marks.SeatsAt(15000, 21150).ToArray());
        Assert.Equal([1, 2, 3], marks.SeatsAt(10000, 21150).ToArray());
        Assert.Equal(2, luska.Reactions.Length);
        Assert.Equal(CheatCasterFilter.Unmarked, luska.Reactions[1].When.Caster);
        Assert.Equal(["Power Link-TriBoss", "Skeletal Dragon-TriBoss"], luska.Reactions[1].Do[1].Spells.ToArray());
        Assert.Equal(CheatStepTarget.Caster, luska.Reactions[1].Do[1].Target);
        Assert.Equal("WizardBossFights_00000065", luska.Reactions[1].Message);
        Assert.Null(table.CycleOf(Luska));

        var cycle = table.CycleOf(Sylster)!;
        Assert.Same(cycle, table.CycleOf(WaterWing));
        Assert.Equal([Sylster, Spellhammer, WaterWing], cycle.Holders.Select(h => h.Template).ToArray());
        Assert.Equal("WizardBossFights_00000095", cycle.Holders[1].TakeoverMessage);
        Assert.Equal([("white", "Doom and Gloom Light", 3, 4), ("black", "Doom and Gloom Dark", 4, 4)],
            cycle.Phases.Select(p => (p.Id, p.Spell, p.FirstRounds, p.Rounds)).ToArray());
        Assert.Equal((4, CheatRemoveOnly.Beneficial), (cycle.Phases[0].OnEnter[0].RemoveCount!.Value, cycle.Phases[0].OnEnter[0].RemoveOnly));
        Assert.Equal((4, CheatRemoveOnly.Harmful), (cycle.Phases[1].OnEnter[0].RemoveCount!.Value, cycle.Phases[1].OnEnter[0].RemoveOnly));

        foreach (var (template, punish) in new[] { (Sylster, "Ra-TriBoss"), (WaterWing, "RebirthBOSS01"), (Spellhammer, "Scarecrow") }) {
            Assert.True(table.TryGet(template, out var boss));
            Assert.Equal([("white", CheatMissing.TrapOnTarget), ("black", CheatMissing.BladeOnCaster)],
                boss.Reactions.Select(r => (r.When.Phase!, r.When.Missing)).ToArray());
            Assert.All(boss.Reactions, r => Assert.Equal(punish, Assert.Single(r.Do).Spells.Single()));
        }

        Assert.All(table.Bosses, b => Assert.StartsWith(table.DungeonZone + "/", b.Zone, StringComparison.OrdinalIgnoreCase));
        Assert.All(table.Bosses, b => Assert.True(b.SourceDate <= new DateOnly(2011, 5, 31)));
    }

    [Fact]
    public void TwoFilesCombineAndBriskbreezeStaysAsItWas() {
        var briskbreeze = Briskbreeze();
        var combined = BossCheats.Combine([briskbreeze, Waterworks()]);
        Assert.Equal(6, combined.Count);
        Assert.Same(briskbreeze.Guide, combined.Guide);
        Assert.True(combined.TryGet(164776, out var orrik));
        Assert.Same(briskbreeze.Bosses.Single(b => b.Template == 164776), orrik);
        Assert.Single(combined.Cycles);
        Assert.Equal(2, combined.Tables.Length);

        // The 2009 bosses use none of the new primitives.
        Assert.All(briskbreeze.Bosses, b => Assert.True(b.Marks is null && b.Reactions.IsEmpty && b.RoundStart.IsEmpty));
        Assert.Empty(briskbreeze.Cycles);
        Assert.Same(briskbreeze, BossCheats.Combine([briskbreeze]));

        var error = Assert.Throws<ClassicDataException>(() => BossCheats.Combine([briskbreeze, briskbreeze]));
        Assert.Contains("a boss cheats in one file only", error.Message);
    }

    private const string Head = """
        kind: boss-cheats
        version: 1
        id: boss-cheats-test
        profiles: [wintertusk-2011]
        license_tag: own
        dungeon: {name: Waterworks, zone: WizardCity/Gauntlets/WC_Triton_Gauntlet1, source: x, source_date: '2011-04-27'}
        """;

    private static BossCheats LoadInline(string body) {
        using var data = new TempClassicData();
        var dir = Path.Combine(data.Root, "creatures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "boss-cheats-test.yaml");
        File.WriteAllText(path, (Head + "\n" + body).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return BossCheatsLoader.Load(path);
    }

    private const string Boss = "{template: 1, name: A, floor: 1, zone: WizardCity/Gauntlets/WC_Triton_Gauntlet1/WC_Triton_Gauntlet_01, source: x, source_date: '2011-04-27'";

    [Theory]
    [InlineData("reactions: [{when: {kinds: [attack], phase: white}, do: [{spell: Ra}], source: x}]", "which its cycle does not have")]
    [InlineData("reactions: [{when: {kinds: [attack], caster: unmarked}, do: [{spell: Ra}], source: x}]", "the boss marks none")]
    [InlineData("reactions: [{when: {kinds: [global]}, do: [{recast_phase: true}], source: x}]", "recast_phase needs the boss to hold a cycle")]
    [InlineData("reactions: [{when: {kinds: [attack]}, do: [{spell: Ra, one_of: [A, B]}], source: x}]", "exactly one of spell, one_of and recast_phase")]
    [InlineData("reactions: [{when: {kinds: [sneeze]}, do: [{spell: Ra}], source: x}]", "sneeze")]
    [InlineData("round_start: [{do: [{spell: Ra, target: caster}], source: x}]", "target caster is only for a reaction")]
    [InlineData("marks: {seats: [{seat: 1}, {seat: 1}], marked_spell: M, source: x}", "seat 1 is listed twice")]
    [InlineData("reactions: [{when: {kinds: [attack]}, do: [{spell: Ra}], message: 'No traps', source: x}]", "is not a client string key")]
    public void TheLoaderRefusesBrokenPrimitives(string primitive, string expected) {
        var error = Assert.Throws<ClassicDataException>(() => LoadInline($"bosses:\n- {Boss}, {primitive}}}"));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ACycleNeedsItsHoldersInTheFile() {
        const string Cycle = """
            cycles:
            - id: c
              holders: [{template: 1}, {template: 2}]
              phases: [{id: white, spell: W, rounds: 4}]
              source: x
              source_date: '2011-04-27'
            """;
        var error = Assert.Throws<ClassicDataException>(() => LoadInline("bosses:\n- " + Boss + "}\n" + Cycle));
        Assert.Contains("holder 2 is not a boss of this file", error.Message);

        const string Good = """
            cycles:
            - id: c
              holders: [{template: 1}]
              phases: [{id: white, spell: W, first_rounds: 3, rounds: 4, reactions: [{when: {kinds: [global], target: any}, do: [{recast_phase: true}], source: x}]}]
              source: x
              source_date: '2011-04-27'
            """;
        var table = LoadInline("bosses:\n- " + Boss + ", reactions: [{when: {kinds: [attack], phase: white}, do: [{spell: Ra}], source: x}]}\n" + Good);
        Assert.Equal("white", Assert.Single(table.Cycles).Phases[0].Reactions[0].When.Phase);
    }


}

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class BossCheatMechanicsDuelTests : IDisposable {

    private const int FireIndex = 9102;
    private const uint Base = uint.MaxValue - 700;
    private const uint LuskaT = 439083, SylsterT = 439075, HammerT = 469333, WingT = 469334, FrostT = 346652;
    private readonly Dictionary<int, MagicSchoolTemplate> _schools;
    private readonly Dictionary<string, (Spell, SpellTemplate)> _spells = [];
    private uint _next;

    public BossCheatMechanicsDuelTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-bosscheat-mech-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }

        _schools = (Dictionary<int, MagicSchoolTemplate>) typeof(MagicSchools)
            .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _schools[FireIndex] = new MagicSchoolTemplate { m_schoolName = "Fire", m_schoolIndex = 2 };

        // Boss spells (the shapes of the client's templates).
        Add("Black Mantle Fire", Effect(kSpellEffects.kModifyAccuracy, -45, kEffectTarget.kEnemySingle, "All"));
        Add("Hex_TriBoss", Effect(kSpellEffects.kModifyIncomingDamage, 70, kEffectTarget.kEnemySingle, "All"));
        Add("Cleanse Ward", Remove(kSpellEffects.kRemoveWard, 1, kEffectTarget.kFriendlySingle, kHangingDisposition.kHarmful));
        Add("Shatter-TriBoss", Remove(kSpellEffects.kRemoveWard, 10, kEffectTarget.kEnemyTeam, kHangingDisposition.kBeneficial));
        Add("Power Link-TriBoss", Effect(kSpellEffects.kDamage, 400, kEffectTarget.kEnemySingle));
        Add("Skeletal Dragon-TriBoss", Effect(kSpellEffects.kDamage, 800, kEffectTarget.kEnemySingle));
        Add("Doom and Gloom Light", Effect(kSpellEffects.kModifyOutgoingHeal, -50, kEffectTarget.kGlobal, "Life"));
        Add("Doom and Gloom Dark", Effect(kSpellEffects.kModifyOutgoingHeal, -50, kEffectTarget.kGlobal, "Life"));
        Add("Cleanse Charm_TriBoss", Remove(kSpellEffects.kRemoveCharm, 3, kEffectTarget.kEnemyTeam, kHangingDisposition.kBeneficial));
        Add("Cleanse Ward_TriBoss", Remove(kSpellEffects.kRemoveWard, 3, kEffectTarget.kFriendlyTeam, kHangingDisposition.kBoth));
        Add("Ra-TriBoss", Effect(kSpellEffects.kDamage, 1500, kEffectTarget.kEnemyTeamAllAtOnce, "Balance"));
        Add("RebirthBOSS01", Effect(kSpellEffects.kHeal, 750, kEffectTarget.kFriendlyTeamAllAtOnce, "Life"));
        Add("Scarecrow", Effect(kSpellEffects.kStealHealth, 590, kEffectTarget.kEnemyTeamAllAtOnce, "Death"));
        Add("Smoke Screen", Effect(kSpellEffects.kModifyAccuracy, -40, kEffectTarget.kEnemyTeam, "All"));
        Add("Efreet", Effect(kSpellEffects.kDamage, 780, kEffectTarget.kEnemySingle));
        // Wizard spells.
        Add("Wizard Bolt", Effect(kSpellEffects.kDamage, 300, kEffectTarget.kEnemySingle), rank: 1);
        Add("Wizard Storm", Effect(kSpellEffects.kDamage, 300, kEffectTarget.kEnemyTeamAllAtOnce), rank: 2);
        Add("Wizard Trap", Effect(kSpellEffects.kModifyIncomingDamage, 30, kEffectTarget.kEnemySingle));
        Add("Wizard Blade", Effect(kSpellEffects.kModifyOutgoingDamage, 25, kEffectTarget.kFriendlySingle));
        Add("Wizard Shield", Effect(kSpellEffects.kModifyIncomingDamage, -50, kEffectTarget.kFriendlySingle));
        Add("Wizard Bubble", Effect(kSpellEffects.kModifyOutgoingDamage, 25, kEffectTarget.kGlobal));
        BossCheatDirector.ResolveSpellForTests = name => _spells.TryGetValue(name, out var s) ? s : null;

        var lazy = typeof(SpellCinematics).BaseType!.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (!(bool) lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy)!) {
            lazy.GetType().GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(lazy, RuntimeHelpers.GetUninitializedObject(typeof(SpellCinematics)));
            lazy.GetType().GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(lazy, null);
        }

        // The real Waterworks file, plus a Frostbranch-shaped boss (built in code: its cheat is not authored).
        var waterworks = BossCheatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "creatures", "boss-cheats-waterworks-2011.yaml"));
        var frost = new BossCheat {
            Template = FrostT, Name = "Frostbranch (test shape)", Floor = 1, Zone = "x/y", CastsPerRound = 1, FreeSpells = [], Summons = [],
            RoundStart = [
                new BossRoundStart([CheatStep.Of("Smoke Screen")], 1, 1, default, false, null, "test"),
                new BossRoundStart([CheatStep.Of("Efreet")], 2, 1, default, true, null, "test"),
            ],
            Source = "test", SourceDate = new DateOnly(2011, 12, 19),
        };
        ClassicProgression.UseBossCheatsForTests(BossCheats.ForTests(waterworks.DungeonZone, waterworks.Cycles, [.. waterworks.Bosses, frost]));
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

    private static SpellEffect Effect(kSpellEffects type, int param, kEffectTarget target, string school = "Fire") => new() {
        m_effectType = type, m_effectParam = param, m_sDamageType = school, m_effectTarget = target,
    };

    private static SpellEffect Remove(kSpellEffects type, int count, kEffectTarget target, kHangingDisposition disposition) => new() {
        m_effectType = type, m_effectParam = count, m_sDamageType = "All", m_effectTarget = target, m_disposition = disposition,
    };

    private void Add(string name, SpellEffect effect, byte rank = 0, uint accuracy = 100) {
        var id = Base + _next++;
        var template = new SpellTemplate {
            m_name = name, m_spellBase = name, m_accuracy = (byte) accuracy, m_sMagicSchoolName = "Fire",
            m_spellRank = new SpellRank { m_spellRank = rank }, m_effects = [effect],
        };
        Cache[id] = template;
        _spells[name] = (new Spell {
            m_templateID = id, m_accuracy = (byte) accuracy, m_magicSchoolID = StringHash.Compute("Fire"),
            m_pipCost = new SpellRank { m_spellRank = rank },
        }, template);
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, ulong template, int health, int max) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = template });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = max;
        stats.m_currentHitpoints = health;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        var combatStats = (WizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(WizGameStats));
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_hangingEffects = [], m_pGameStats = combatStats, m_pipCount = new PipCount(),
        });
        circle.AddedToDuel = true;
        return circle;
    }

    private static CombatDuelComponent NewDuel() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        return duel;
    }

    private static CombatDuelSubCircle[] Wizards(CombatDuelComponent duel, int count)
        => [.. Enumerable.Range(4, count).Select(slot => Occupy(duel, slot, 1, 5000, 5000))];

    private QueuedCombatAction Cast(CombatDuelSubCircle caster, CombatDuelSubCircle target, string spell)
        => new() { SpellCaster = caster, SelectedTarget = target, Spell = _spells[spell].Item1, SpellTemplate = _spells[spell].Item2 };

    // A wizard's spell as the resolver brackets it: the boss's health and the target's wards change in between.
    private static List<QueuedCombatAction> Play(CombatDuelComponent duel, QueuedCombatAction action, int damage = 0, SpellEffect? lands = null) {
        duel.BossCheats.BeforeAction(action);
        action.SelectedTarget.ParticipantGameStats.m_currentHitpoints -= damage;
        if (lands is not null) action.SelectedTarget._hangingEffects.Add(lands);
        return duel.BossCheats.AfterAction(action);
    }

    private static List<QueuedCombatAction> RoundStart(CombatDuelComponent duel, int round) {
        duel.BossCheats.NewRound(round);
        var queue = new List<QueuedCombatAction>();
        duel.BossCheats.AddExtraCasts(queue);
        return queue;
    }

    private static string[] Names(IEnumerable<QueuedCombatAction> casts) => [.. casts.Select(c => c.SpellTemplate.m_name)];

    private static SpellEffect Trap() => new() { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = 30, m_sDamageType = "Fire" };
    private static SpellEffect Blade() => new() { m_effectType = kSpellEffects.kModifyOutgoingDamage, m_effectParam = 25, m_sDamageType = "Fire" };
    private static SpellEffect Shield() => new() { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = -50, m_sDamageType = "Fire" };

    // ------------------------------------------------------------ marks (Luska's ink)

    [Fact]
    public void RoundOneInksTheSunSeatAndHexesTheOthersOnlyWhenTheLastOneIsGone() {
        var duel = NewDuel();
        var luska = Occupy(duel, 0, LuskaT, 21150, 21150);
        var w = Wizards(duel, 4);

        var first = RoundStart(duel, 1);
        Assert.Equal(["Black Mantle Fire", "Hex_TriBoss", "Hex_TriBoss", "Hex_TriBoss"], Names(first));
        Assert.Equal([w[0], w[1], w[2], w[3]], first.Select(c => c.SelectedTarget).ToArray());
        Assert.Equal("WizardBossFights_00000071", first[0].Cheat!.Message);
        Assert.All(first, c => Assert.True(c.Cheat!.Interrupt && c.Cheat.SureHit && c.Cheat.Kind == BossCheatKind.Mark));
        Assert.Equal([1], duel.BossCheats.MarkedSeats(luska).ToArray());

        // The effects landed; next round nothing is recast, until the sun's mantle and the star's hex are gone.
        w[0]._hangingEffects.Add(Effect(kSpellEffects.kModifyAccuracy, -45, kEffectTarget.kEnemySingle, "All"));
        foreach (var x in w[1..]) x._hangingEffects.Add(Effect(kSpellEffects.kModifyIncomingDamage, 70, kEffectTarget.kEnemySingle, "All"));
        Assert.Empty(RoundStart(duel, 2));
        w[0]._hangingEffects.Clear();
        w[2]._hangingEffects.Clear();
        var third = RoundStart(duel, 3);
        Assert.Equal(["Black Mantle Fire", "Hex_TriBoss"], Names(third));
        Assert.Equal([w[0], w[2]], third.Select(c => c.SelectedTarget).ToArray());
        Assert.Null(third[0].Cheat!.Message);
    }

    [Fact]
    public void AHitBelowSeventyFivePercentInksTheEyeSeatAtOnceAndADeadSunIsNotReplaced() {
        var duel = NewDuel();
        var luska = Occupy(duel, 0, LuskaT, 21150, 21150);
        var w = Wizards(duel, 4);
        RoundStart(duel, 1);
        w[0].ParticipantGameStats.m_currentHitpoints = 0;   // the sun falls: nobody else is inked for it

        // The sun's partner's group spell (no punishment) takes Luska to 15,000 (below 15,862).
        var answer = Play(duel, Cast(w[1], luska, "Wizard Storm"), damage: 6150);
        var ink = Assert.Single(answer);
        Assert.Equal("Black Mantle Fire", ink.SpellTemplate.m_name);
        Assert.Same(w[1], ink.SelectedTarget);
        Assert.Equal("WizardBossFights_00000064", ink.Cheat!.Message);
        Assert.Equal([1, 2], duel.BossCheats.MarkedSeats(luska).ToArray());

        // One hit from above 50% to below inks the star with it; the moon never.
        var both = Play(duel, Cast(w[1], luska, "Wizard Storm"), damage: 6000);
        Assert.Equal([w[2]], both.Select(c => c.SelectedTarget).ToArray());
        Assert.Equal([1, 2, 3], duel.BossCheats.MarkedSeats(luska).ToArray());
    }

    // ------------------------------------------------------------ reactions (Luska)

    [Fact]
    public void AnUninkedSingleTargetSpellOnLuskaIsPunishedAndAnInkedOneOrAGroupSpellIsNot() {
        var duel = NewDuel();
        var luska = Occupy(duel, 0, LuskaT, 21150, 21150);
        var w = Wizards(duel, 3);
        RoundStart(duel, 1);

        // The eye seat (not inked) bolts him: no shields to shatter, so Power Link or Skeletal Dragon on the caster,
        // with the taunt on it.
        var punish = Play(duel, Cast(w[1], luska, "Wizard Bolt"), damage: 300);
        var hit = Assert.Single(punish);
        Assert.Contains(hit.SpellTemplate.m_name, new[] { "Power Link-TriBoss", "Skeletal Dragon-TriBoss" });
        Assert.Same(w[1], hit.SelectedTarget);
        Assert.Equal("WizardBossFights_00000065", hit.Cheat!.Message);
        Assert.Equal(BossCheatKind.Reaction, hit.Cheat.Kind);

        // With a shield on the team, Shatter comes first and carries the taunt.
        w[2]._hangingEffects.Add(Shield());
        var withShield = Play(duel, Cast(w[2], luska, "Wizard Bolt"), damage: 300);
        Assert.Equal("Shatter-TriBoss", withShield[0].SpellTemplate.m_name);
        Assert.Equal("WizardBossFights_00000065", withShield[0].Cheat!.Message);
        Assert.Null(withShield[1].Cheat!.Message);
        Assert.Same(w[2], withShield[1].SelectedTarget);

        // The inked sun may hit; a group spell from anyone is not punished.
        Assert.Empty(Play(duel, Cast(w[0], luska, "Wizard Bolt"), damage: 300));
        Assert.Empty(Play(duel, Cast(w[1], luska, "Wizard Storm"), damage: 300));
        // A blade on oneself is not a spell on Luska.
        Assert.Empty(Play(duel, Cast(w[1], w[1], "Wizard Blade")));

        // Both Power Link and Skeletal Dragon come up over many punishments.
        var names = new HashSet<string>();
        for (var i = 0; i < 40; i++) names.Add(Play(duel, Cast(w[1], luska, "Wizard Bolt")).Last().SpellTemplate.m_name);
        Assert.Equal(new HashSet<string> { "Power Link-TriBoss", "Skeletal Dragon-TriBoss" }, names);
    }

    [Fact]
    public void ATrapOnLuskaIsCleansedEvenFromTheSunAndAnUninkedTrapIsAlsoPunished() {
        var duel = NewDuel();
        var luska = Occupy(duel, 0, LuskaT, 21150, 21150);
        var w = Wizards(duel, 2);
        RoundStart(duel, 1);

        var sun = Play(duel, Cast(w[0], luska, "Wizard Trap"), lands: Trap());
        var cleanse = Assert.Single(sun);
        Assert.Equal("Cleanse Ward", cleanse.SpellTemplate.m_name);
        Assert.Same(luska, cleanse.SelectedTarget);

        luska._hangingEffects.Clear();
        var eye = Play(duel, Cast(w[1], luska, "Wizard Trap"), lands: Trap());
        Assert.Equal("Cleanse Ward", eye[0].SpellTemplate.m_name);
        Assert.Contains(eye[1].SpellTemplate.m_name, new[] { "Power Link-TriBoss", "Skeletal Dragon-TriBoss" });
        Assert.Equal("WizardBossFights_00000065", eye[1].Cheat!.Message);   // the message goes with its own reaction's first cast
    }

    // ------------------------------------------------------------ cycles (Sylster)

    private (CombatDuelComponent Duel, CombatDuelSubCircle Sylster, CombatDuelSubCircle Hammer, CombatDuelSubCircle Wing, CombatDuelSubCircle[] W) Trio() {
        var duel = NewDuel();
        var sylster = Occupy(duel, 0, SylsterT, 24980, 24980);
        var wing = Occupy(duel, 1, WingT, 8190, 8190);
        var hammer = Occupy(duel, 2, HammerT, 6560, 6560);
        return (duel, sylster, hammer, wing, Wizards(duel, 3));
    }

    [Fact]
    public void TheBubbleIsWhiteForThreeRoundsThenSwitchesEveryFourAtTheStartOfTheRound() {
        var (duel, sylster, _, _, _) = Trio();
        var cycle = ClassicProgression.BossCheats.CycleOf(SylsterT)!;
        var phases = new List<string>();
        var switches = new List<(int Round, string Bubble, string? Message)>();
        for (var round = 1; round <= 12; round++) {
            var casts = RoundStart(duel, round);
            phases.Add(duel.BossCheats.CurrentPhase(cycle)!.Id);
            if (casts.Count > 0) {
                Assert.Same(sylster, casts[0].SpellCaster);
                Assert.Equal(BossCheatKind.Phase, casts[0].Cheat!.Kind);
                Assert.True(casts[0].Cheat!.Interrupt);
                switches.Add((round, casts[0].SpellTemplate.m_name, casts[0].Cheat!.Message));
            }
        }

        Assert.Equal(["white", "white", "white", "black", "black", "black", "black", "white", "white", "white", "white", "black"], phases);
        Assert.Equal([(1, "Doom and Gloom Light", "WizardBossFights_00000063"), (4, "Doom and Gloom Dark", "WizardBossFights_00000062"),
                      (8, "Doom and Gloom Light", "WizardBossFights_00000063"), (12, "Doom and Gloom Dark", "WizardBossFights_00000062")], switches);
    }

    [Fact]
    public void AtEachSwitchTheBossesLoseFourTrapsOrTheWizardsFourBladesAndNothingIsCastWhenThereIsNothing() {
        var (duel, sylster, _, wing, w) = Trio();
        Assert.Single(RoundStart(duel, 1));   // the white bubble; the Disarm is skipped: no blades yet
        RoundStart(duel, 2);
        RoundStart(duel, 3);
        for (var i = 0; i < 5; i++) sylster._hangingEffects.Add(Trap());
        sylster._hangingEffects.Add(Shield());   // his own shield stays: only traps go

        var black = RoundStart(duel, 4);
        Assert.Equal(["Doom and Gloom Dark", "Cleanse Ward_TriBoss"], Names(black));
        var cleanse = black[1].SpellTemplate.m_effects.Single();
        Assert.Equal((4, kHangingDisposition.kHarmful), (cleanse.m_effectParam, cleanse.m_disposition));
        Assert.Equal(3, _spells["Cleanse Ward_TriBoss"].Item2.m_effects.Single().m_effectParam);   // the shared template is untouched

        // The resolver plays it: 4 of his 5 traps go, the shield stays, and Water-Wing (no traps) loses nothing.
        Resolve(duel, black[1]);
        Assert.Equal(1, sylster._hangingEffects.Count(e => e.m_effectParam == 30));
        Assert.Single(sylster._hangingEffects, e => e.m_effectParam == -50);

        for (var round = 5; round <= 7; round++) RoundStart(duel, round);
        w[0]._hangingEffects.Add(Blade());
        var white = RoundStart(duel, 8);
        Assert.Equal(["Doom and Gloom Light", "Cleanse Charm_TriBoss"], Names(white));
        Assert.Equal(4, white[1].SpellTemplate.m_effects.Single().m_effectParam);
    }

    [Fact]
    public void WhenSylsterFallsTheSpellhammerTakesTheCycleOverAndThenWaterWing() {
        var (duel, sylster, hammer, wing, _) = Trio();
        var cycle = ClassicProgression.BossCheats.CycleOf(SylsterT)!;
        for (var round = 1; round <= 5; round++) RoundStart(duel, round);
        Assert.Equal("black", duel.BossCheats.CurrentPhase(cycle)!.Id);

        sylster.ParticipantGameStats.m_currentHitpoints = 0;
        var takeover = RoundStart(duel, 6);
        Assert.Same(hammer, takeover[0].SpellCaster);
        Assert.Equal("Doom and Gloom Light", takeover[0].SpellTemplate.m_name);
        Assert.Equal("WizardBossFights_00000095", takeover[0].Cheat!.Message);
        Assert.Equal("white", duel.BossCheats.CurrentPhase(cycle)!.Id);
        Assert.Empty(RoundStart(duel, 7));
        Assert.Empty(RoundStart(duel, 8));
        Assert.Equal("Doom and Gloom Dark", RoundStart(duel, 9)[0].SpellTemplate.m_name);   // a new 3-round white

        hammer.ParticipantGameStats.m_currentHitpoints = 0;
        var last = RoundStart(duel, 10);
        Assert.Same(wing, last[0].SpellCaster);
        Assert.Equal(("Doom and Gloom Light", "WizardBossFights_00000096"), (last[0].SpellTemplate.m_name, last[0].Cheat!.Message));

        wing.ParticipantGameStats.m_currentHitpoints = 0;
        Assert.Empty(RoundStart(duel, 11));
    }

    [Fact]
    public void ASingleHitWithoutTheBubblesBuffIsPunishedByTheBossThatWasHit() {
        var (duel, sylster, hammer, wing, w) = Trio();
        RoundStart(duel, 1);   // white: traps

        var ra = Assert.Single(Play(duel, Cast(w[0], sylster, "Wizard Bolt"), damage: 300));
        Assert.Equal(("Ra-TriBoss", "WizardBossFights_00000093"), (ra.SpellTemplate.m_name, ra.Cheat!.Message));
        Assert.Equal("RebirthBOSS01", Assert.Single(Play(duel, Cast(w[0], wing, "Wizard Bolt"), damage: 300)).SpellTemplate.m_name);
        var scarecrow = Assert.Single(Play(duel, Cast(w[0], hammer, "Wizard Bolt"), damage: 300));
        Assert.Same(hammer, scarecrow.SpellCaster);

        // A trap present when the card is played is enough, even though the hit uses it up; a group spell is free.
        sylster._hangingEffects.Add(Trap());
        var bolt = Cast(w[1], sylster, "Wizard Bolt");
        duel.BossCheats.BeforeAction(bolt);
        sylster._hangingEffects.Clear();
        Assert.Empty(duel.BossCheats.AfterAction(bolt));
        Assert.Empty(Play(duel, Cast(w[1], sylster, "Wizard Storm"), damage: 300));
        // A blade does not count in white.
        w[2]._hangingEffects.Add(Blade());
        Assert.Equal("Ra-TriBoss", Play(duel, Cast(w[2], sylster, "Wizard Bolt"), damage: 300).Last().SpellTemplate.m_name);

        // Black: the caster needs a blade; a trap on the target does not count.
        for (var round = 2; round <= 4; round++) RoundStart(duel, round);
        Assert.Empty(Play(duel, Cast(w[2], sylster, "Wizard Bolt"), damage: 300));
        sylster._hangingEffects.Add(Trap());
        var black = Play(duel, Cast(w[0], sylster, "Wizard Bolt"), damage: 300);
        Assert.Equal(("Ra-TriBoss", "WizardBossFights_00000094"), (black.Single().SpellTemplate.m_name, black.Single().Cheat!.Message));
    }

    [Fact]
    public void TheHolderAnswersAForbiddenBuffAndABubbleInEachPhase() {
        var (duel, sylster, _, wing, w) = Trio();
        RoundStart(duel, 1);   // white

        // A blade in white is stripped at once (4 a wizard).
        var disarm = Assert.Single(Play(duel, Cast(w[0], w[0], "Wizard Blade"), lands: Blade()));
        Assert.Equal(("Cleanse Charm_TriBoss", 4), (disarm.SpellTemplate.m_name, disarm.SpellTemplate.m_effects.Single().m_effectParam));
        Assert.Same(sylster, disarm.SpellCaster);

        // A wizard's bubble is knocked out with the phase's own bubble.
        var recast = Assert.Single(Play(duel, Cast(w[1], w[1], "Wizard Bubble")));
        Assert.Equal("Doom and Gloom Light", recast.SpellTemplate.m_name);

        // A trap in white is fine; in black it is cleansed off the bosses at once.
        Assert.Empty(Play(duel, Cast(w[0], wing, "Wizard Trap"), lands: Trap()));
        for (var round = 2; round <= 4; round++) RoundStart(duel, round);
        var cleanse = Assert.Single(Play(duel, Cast(w[0], wing, "Wizard Trap"), lands: Trap()));
        Assert.Equal("Cleanse Ward_TriBoss", cleanse.SpellTemplate.m_name);
        Assert.Equal("Doom and Gloom Dark", Assert.Single(Play(duel, Cast(w[1], w[1], "Wizard Bubble"))).SpellTemplate.m_name);
        // A blade in black is fine.
        Assert.Empty(Play(duel, Cast(w[0], w[0], "Wizard Blade"), lands: Blade()));
    }

    // ------------------------------------------------------------ round start (Frostbranch's shape)

    [Fact]
    public void ARoundStartCastComesEveryRoundAndAnotherOnlyAfterARoundWithoutAnAttack() {
        var duel = NewDuel();
        var frost = Occupy(duel, 0, FrostT, 5545, 5545);
        var w = Wizards(duel, 2);

        Assert.Equal(["Smoke Screen"], Names(RoundStart(duel, 1)));            // round 1: no round before it
        var second = RoundStart(duel, 2);                                        // nobody attacked in round 1
        Assert.Equal(["Smoke Screen", "Efreet"], Names(second));
        Assert.All(second, c => Assert.Equal(BossCheatKind.RoundStart, c.Cheat!.Kind));
        Play(duel, Cast(w[0], frost, "Wizard Bolt"), damage: 300);              // round 2: a wizard hits it
        Assert.Equal(["Smoke Screen"], Names(RoundStart(duel, 3)));
        var quiet = RoundStart(duel, 4);
        Assert.Equal(["Smoke Screen", "Efreet"], Names(quiet));
        Assert.Contains(w, x => x == quiet[1].SelectedTarget);
        // Only once a round, however often the resolver asks.
        var again = new List<QueuedCombatAction>();
        duel.BossCheats.AddExtraCasts(again);
        Assert.Empty(again);
    }

    // ------------------------------------------------------------ the resolver

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

    [Fact]
    public void AStunnedBossStillPlaysItsScriptKeepsTheStunAndASureHitInkNeverMisses() {
        var duel = NewDuel();
        var luska = Occupy(duel, 0, LuskaT, 21150, 21150);
        var w = Wizards(duel, 2);
        luska.CombatParticipant.m_stunned = 1;
        luska.CombatParticipant.m_pGameStats.m_accReducePercentAll = 1.0f;   // -100% accuracy: only a sure hit lands

        var list = Resolve(duel, [.. RoundStart(duel, 1)]);
        Assert.Equal(2, list.Count);
        Assert.All(list, a => Assert.Equal((char) 1, a.m_spellHits));
        Assert.All(list, a => Assert.True(a.m_interrupt));
        Assert.Equal("WizardBossFights_00000071", list[0].m_stringKeyMessage);
        Assert.Equal(1, luska.CombatParticipant.m_stunned);
        Assert.Contains(w[0]._hangingEffects, e => e.m_effectType == kSpellEffects.kModifyAccuracy && e.m_effectParam == -45);
        Assert.Contains(w[1]._hangingEffects, e => e.m_effectType == kSpellEffects.kModifyIncomingDamage && e.m_effectParam == 70);
    }

    [Fact]
    public void TheResolverPlaysTheBubbleAsTheDuelsGlobalEffect() {
        var (duel, _, _, _, _) = Trio();
        Resolve(duel, [.. RoundStart(duel, 1)]);
        var bubble = Assert.Single(duel.Duel.m_duelModifier.m_battlefieldEffects);
        Assert.Equal((kSpellEffects.kModifyOutgoingHeal, -50), (bubble.m_effectType, bubble.m_effectParam));
    }

}
