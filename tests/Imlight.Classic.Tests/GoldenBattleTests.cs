using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

// Golden battles: a duel's rolls all come from its seed, so the same seed and the same moves give the same fight.
// The pinned log catches any rule change that alters a seeded fight; update it only on purpose.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class GoldenBattleTests : IDisposable {
    private const int FireIndex = 9101;
    private readonly Dictionary<int, MagicSchoolTemplate> _schools;

    public GoldenBattleTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-golden-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
        _schools = (Dictionary<int, MagicSchoolTemplate>) typeof(MagicSchools)
            .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _schools.Add(FireIndex, new MagicSchoolTemplate { m_schoolName = "Fire", m_schoolIndex = 2 });
        for (uint i = 1; i <= 3; i++) {
            Cache[CardBase + i] = new SpellTemplate {
                m_name = $"Golden Card {i}", m_accuracy = 75,
                m_spellRank = new SpellRank { m_spellRank = 1 }, m_effects = [Damage(100)],
            };
        }
    }

    private static IDictionary<ulong, CoreTemplate> Cache => (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
        .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    public void Dispose() {
        _schools.Remove(FireIndex);
        for (uint i = 0; i <= 3; i++) Cache.Remove(CardBase + i);
        ClassicRuntime.ResetForTests();
    }

    [Fact]
    public void TheSameSeedAndMovesGiveTheSameFight() {
        Assert.Equal(Fight(12345), Fight(12345));
        Assert.NotEqual(Fight(12345), Fight(67890));
    }

    [Fact]
    public void ABeguiledMonsterAttacksItsOwnTeamOnceThenActsNormally() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var wizard = Occupy(duel, 4, true);
        var beguiled = Occupy(duel, 0, false);
        var teammate = Occupy(duel, 1, false);
        var template = new SpellTemplate {
            m_name = "Golden Bolt", m_accuracy = 100, m_spellRank = new SpellRank { m_spellRank = 0 }, m_effects = [Damage(100)],
        };
        Cache[CardBase] = template;
        var spell = new Spell { m_templateID = CardBase, m_accuracy = 100, m_magicSchoolID = StringHash.Compute("Fire"), m_pipCost = new SpellRank { m_spellRank = 0 } };
        foreach (var circle in new[] { wizard, beguiled, teammate }) {
            circle._combatDeck = new CombatDeck([new CombatDeckSpellData { TemplateId = CardBase, Quantity = 5 }], [], 7);
        }

        CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = kSpellEffects.kMindControl, m_numRounds = 1 }, [], wizard, [beguiled]);
        Assert.Equal(CombatTeam.Player, beguiled.ActingTeam);

        // Through the resolver: the monster planned a card at the wizard; beguiled, it goes at its teammate. A 0% card
        // fizzles, and the fizzle names the target (a cast would also need the client's cinematics).
        var fizzling = new Spell { m_templateID = CardBase, m_accuracy = 0, m_magicSchoolID = spell.m_magicSchoolID, m_pipCost = spell.m_pipCost };
        var actions = Resolve(duel, new QueuedCombatAction { SpellCaster = beguiled, SelectedTarget = wizard, Spell = fizzling, SpellTemplate = template });
        Assert.Equal(new[] { teammate.SlotIndex }, Assert.Single(actions).m_targetSubcircleList);
        Assert.Equal(0, beguiled.BeguiledActions);
        Assert.Equal(CombatTeam.Monster, beguiled.ActingTeam);

        // The effect side: while beguiled, its all-enemy and single-target damage lands on its own team.
        beguiled.BeguiledActions = 1;
        var bolt = new QueuedCombatAction { SpellCaster = beguiled, SelectedTarget = teammate, Spell = spell, SpellTemplate = template };
        var combatAction = new CombatAction { m_spellCaster = beguiled.SlotIndex, m_targetSubcircleList = [] };
        var time = 0f;
        CombatActionResolver.ProcessedQueuedCombatAction(bolt, ref combatAction, ref time);
        Assert.Equal(400, teammate.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(500, wizard.ParticipantGameStats.m_currentHitpoints);
        var wave = new SpellTemplate { m_name = "Golden Wave", m_effects = [new SpellEffect {
            m_effectType = kSpellEffects.kDamage, m_effectParam = 50, m_sDamageType = "Fire", m_effectTarget = kEffectTarget.kEnemyTeamAllAtOnce }] };
        CombatActionResolver.ProcessedQueuedCombatAction(new QueuedCombatAction { SpellCaster = beguiled, SelectedTarget = teammate, Spell = spell, SpellTemplate = wave },
            ref combatAction, ref time);
        Assert.Equal(350, teammate.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(450, beguiled.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(500, wizard.ParticipantGameStats.m_currentHitpoints);
    }

    private static List<CombatAction> Resolve(CombatDuelComponent duel, QueuedCombatAction action) {
        var resolver = new CombatResolver(duel.Duel, duel.SubCircles);
        resolver.Reset();
        var queue = (List<QueuedCombatAction>) typeof(CombatResolver)
            .GetField("_queuedCombatActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
        queue.Add(action);
        var list = new CombatActionListObj { m_actionList = [] };
        typeof(CombatResolver).GetMethod("ProcessQueuedActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(resolver, [list]);
        return list.m_actionList;
    }

    [Theory]
    [InlineData(3, (int) CombatTeam.Monster)]
    [InlineData(4, (int) CombatTeam.Player)]
    public void TheFirstSideIsSetWhenCombatStartsAndNeverChanges(int wizardsAtStart, int expectedFirst) {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        typeof(CombatDuelComponent).GetField("_randomFirstTeam", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, CombatTeam.Monster);
        duel.Duel.m_firstTeamToAct = (int) CombatTeam.Monster;
        Occupy(duel, 0, false);
        foreach (var slot in new[] { 4, 5, 6, 7 }.Take(wizardsAtStart)) Occupy(duel, slot, true);
        var apply = typeof(CombatDuelComponent).GetMethod("ApplyFullTeamGoesFirst", BindingFlags.Instance | BindingFlags.NonPublic)!;

        duel.Duel.m_roundNum = 1;
        apply.Invoke(duel, []);
        Assert.Equal(expectedFirst, duel.Duel.m_firstTeamToAct);

        // A fourth wizard joining later, or one leaving, changes nothing.
        if (wizardsAtStart < 4) Occupy(duel, 7, true); else duel.SubCircles[7].AddedToDuel = false;
        duel.Duel.m_roundNum = 2;
        apply.Invoke(duel, []);
        Assert.Equal(expectedFirst, duel.Duel.m_firstTeamToAct);
    }

    [Fact]
    public void SeededStreamsAreIndependentAndStable() {
        var a = CombatRng.Stream(42, CombatRng.DuelStream).Next();
        var b = CombatRng.Stream(42, CombatRng.DeckStream(0)).Next();
        var c = CombatRng.Stream(42, CombatRng.AiStream(0)).Next();
        Assert.Equal(a, CombatRng.Stream(42, CombatRng.DuelStream).Next());
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
    }

    [Fact]
    public void GoldenFightSeed12345() {
        var log = Fight(12345);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log)))[..16];
        // A deliberate rule change updates this value; print the log with the test output to review the new fight.
        Assert.True(hash == GoldenHash, $"golden fight changed ({hash}):\n{log}");
    }

    private const string GoldenHash = "AB2C10F4D5F6BC36";
    private const uint CardBase = uint.MaxValue - 300;

    // Two wizards and two monsters, eight rounds. Every caster casts a 75% Fire card whose damage is one of three
    // random effects at the first living enemy; each wizard also draws a hand from a seeded deck.
    private static string Fight(ulong seed) {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        CombatRegressionTests.SetProperty(duel, "DuelSeed", seed);
        typeof(CombatDuelComponent).GetField("_rng", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, null);

        var wizards = new[] { Occupy(duel, 4, true), Occupy(duel, 5, true) };
        var monsters = new[] { Occupy(duel, 0, false), Occupy(duel, 1, false) };
        var template = new SpellTemplate {
            m_name = "Golden Random Fire", m_accuracy = 75, m_spellRank = new SpellRank { m_spellRank = 1 },
            m_effects = [new RandomSpellEffect {
                m_effectType = kSpellEffects.kDamage, m_effectTarget = kEffectTarget.kEnemySingle,
                m_effectList = [Damage(80), Damage(120), Damage(160)],
            }],
        };
        Cache[CardBase] = template;
        var spell = new Spell { m_templateID = CardBase, m_accuracy = 75, m_magicSchoolID = StringHash.Compute("Fire"), m_pipCost = new SpellRank { m_spellRank = 1 } };
        var hits = typeof(CombatResolver).GetMethod("SpellHits", BindingFlags.Static | BindingFlags.NonPublic)!;

        var log = new StringBuilder();
        log.Append("first team ").Append(duel.Rng.Next(0, 2)).Append('\n');
        foreach (var wizard in wizards) {
            var deck = new CombatDeck([new CombatDeckSpellData { TemplateId = CardBase + 1, Quantity = 3 },
                                       new CombatDeckSpellData { TemplateId = CardBase + 2, Quantity = 3 },
                                       new CombatDeckSpellData { TemplateId = CardBase + 3, Quantity = 3 }], [], 7,
                duel.StreamFor(CombatRng.DeckStream(wizard.SlotIndex)));
            log.Append("deck ").Append(wizard.SlotIndex).Append(' ').Append(DrawOrder(deck)).Append('\n');
        }

        for (var round = 1; round <= 8; round++) {
            foreach (var caster in wizards.Concat(monsters)) {
                if (!caster.IsAlive) continue;
                var enemies = caster.OccupiedTeam == wizards[0].OccupiedTeam ? monsters : wizards;
                var target = enemies.FirstOrDefault(e => e.IsAlive);
                if (target is null) break;
                var hit = (bool) hits.Invoke(null, [caster, spell])!;
                log.Append(round).Append(':').Append(caster.SlotIndex).Append(hit ? " hit " : " fizzle ");
                if (hit) {
                    var action = new QueuedCombatAction { SpellCaster = caster, SelectedTarget = target, Spell = spell, SpellTemplate = template };
                    var combatAction = new CombatAction { m_spellCaster = caster.SlotIndex, m_targetSubcircleList = [] };
                    var time = 0f;
                    CombatActionResolver.ProcessedQueuedCombatAction(action, ref combatAction, ref time);
                }
                log.Append(target.SlotIndex).Append('=').Append(target.ParticipantGameStats.m_currentHitpoints).Append('\n');
            }
        }

        return log.ToString();
    }

    // The ids of the first hand the deck deals.
    private static string DrawOrder(CombatDeck deck) {
        deck.GetHand();
        return string.Join(',', deck.LastGivenHand.Select(card => card.m_templateID - CardBase));
    }

    private static SpellEffect Damage(int amount) => new() {
        m_effectType = kSpellEffects.kDamage, m_effectParam = amount, m_sDamageType = "Fire", m_effectTarget = kEffectTarget.kEnemySingle,
    };

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool player) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 500;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        var combatStats = (WizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(WizGameStats));
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pGameStats = combatStats });
        circle.AddedToDuel = true;
        return circle;
    }
}
