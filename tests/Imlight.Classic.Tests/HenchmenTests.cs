/*
 * CLASSIC (2026-10-09): henchmen hired from the Crown Shop during a duel. The shop lists the client's own henchman
 * items (Level 20/30/40/50 <School> Henchman), whose ElixirBehavior summons the tier's wizard (ResSummonHenchman);
 * prices and level floors are dated (February 2010 shop screenshot, January 2010 wiki, October 2009 notes); the duel
 * takes one only in PvE, during card selection, with a free seat, and answers the shop with the reason otherwise.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HenchmenTests : IDisposable {

    public HenchmenTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-henchmen-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    // --- the rules ------------------------------------------------------------------------------------------------

    [Fact]
    public void RefusalsComeInTheClientsOrderWithItsOwnTexts() {
        Assert.Equal(HenchmanRefusal.NotInCombat, HenchmanRules.Check(false, true, true, false, false, false, false));
        Assert.Equal(HenchmanRefusal.Pvp, HenchmanRules.Check(true, true, true, false, false, false, false));
        Assert.Equal(HenchmanRefusal.NotThisSigil, HenchmanRules.Check(true, false, true, false, false, false, false));
        Assert.Equal(HenchmanRefusal.NotPlanning, HenchmanRules.Check(true, false, false, false, false, false, false));
        Assert.Equal(HenchmanRefusal.Unavailable, HenchmanRules.Check(true, false, false, true, false, false, false));
        Assert.Equal(HenchmanRefusal.SigilFull, HenchmanRules.Check(true, false, false, true, true, false, false));
        Assert.Equal(HenchmanRefusal.Unavailable, HenchmanRules.Check(true, false, false, true, true, true, false));
        Assert.Equal(HenchmanRefusal.None, HenchmanRules.Check(true, false, false, true, true, true, true));

        Assert.Null(HenchmanRules.ClientText(HenchmanRefusal.None));
        Assert.Equal("Your duel circle is full, you cannot purchase a Henchman.", HenchmanRules.ClientText(HenchmanRefusal.SigilFull));
        Assert.Equal("You can only purchase a Henchman during card selection.", HenchmanRules.ClientText(HenchmanRefusal.NotPlanning));
        Assert.Equal("You cannot purchase a Henchman in PVP combat.", HenchmanRules.ClientText(HenchmanRefusal.Pvp));
        Assert.Equal("You can only purchase a Henchman while in combat.", HenchmanRules.ClientText(HenchmanRefusal.NotInCombat));
        Assert.All(Enum.GetValues<HenchmanRefusal>().Where(r => r != HenchmanRefusal.None),
            r => Assert.False(string.IsNullOrEmpty(HenchmanRules.ClientText(r))));
    }

    // --- the catalog ----------------------------------------------------------------------------------------------

    private static readonly ulong[] s_items = [
        191419, 175115, 175111, 175112, 175114, 175117, 175113, // Level 20 (T2)
        175123, 175122, 175116, 175118, 175121, 175120, 175119, // Level 30 (T3)
        175130, 175129, 175124, 175125, 175128, 175127, 175126, // Level 40 (T4)
        175137, 175136, 175131, 175132, 175135, 175134, 175133, // Level 50 (T5)
    ];

    [Theory]
    [InlineData("october-2010-arc1")]
    [InlineData("late-2009")]
    public void TheShopSellsTheClientsHenchmanItemsAtTheirDatedPricesAndLevels(string profile) {
        var rules = ClassicDataFixture.RealRules(profile);
        var catalog = CrownShopCatalogLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "crown-shop-2009.yaml"));
        var henchmen = catalog.Offered(rules.IsFeatureEnabled, rules.Profile.Id).Values
            .Where(item => item.Category == CrownShopCategories.Henchmen).ToArray();

        Assert.Equal(s_items.OrderBy(t => t), henchmen.Select(item => item.Template).OrderBy(t => t));
        foreach (var item in henchmen) {
            var level = int.Parse(item.Name.Split(' ')[1]);
            Assert.Matches(@"^Level (20|30|40|50) (Balance|Death|Fire|Ice|Life|Myth|Storm) Henchman$", item.Name);
            Assert.Equal((level, level, 0, true), (item.Crowns, item.MinLevel, item.Gold, item.CombatOnly));
            Assert.Equal(level, HenchmanRules.Level(item));
        }
        foreach (var level in new[] { 20, 30, 40, 50 }) {
            Assert.Equal(7, henchmen.Count(item => item.MinLevel == level));
        }
    }

    [Fact]
    public void Arc1StillHasNoHenchmen() {
        var rules = ClassicDataFixture.RealRules("arc1-2009h1");
        var catalog = CrownShopCatalogLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "crown-shop-2009.yaml"));
        Assert.DoesNotContain(catalog.Offered(rules.IsFeatureEnabled, rules.Profile.Id).Values,
            item => item.Category == CrownShopCategories.Henchmen);
    }

    private static WizItemTemplate HenchmanItem(ulong id, ulong creature) => new() {
        m_templateID = (uint) id,
        m_behaviors = [
            new ElixirBehaviorTemplate {
                m_equipActionList = new ResultList { m_results = [new ResSummonHenchman { m_templateID = (GID) creature }] },
            },
        ],
    };

    [Fact]
    public void AHenchmanItemNamesTheWizardItSummons() {
        Assert.Equal(191184u, CrownShopService.HenchmanCreature(HenchmanItem(175111, 191184)));
        Assert.Null(CrownShopService.HenchmanCreature(new WizItemTemplate { m_behaviors = [new ElixirBehaviorTemplate()] }));
        Assert.Null(CrownShopService.HenchmanCreature(new WizItemTemplate { m_behaviors = [] }));
    }

    [Fact]
    public void TheClientCatalogKeepsHenchmanItemsAndDropsCreatureTemplates() {
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(Imlight.CoreLib.Shared.Resources.CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var prior = cache.TryGetValue(175111, out var old) ? old : null;
        cache[175111] = HenchmanItem(175111, 191184);
        try {
            var shown = CrownShopService.ForClient([
                new("Level 20 Fire Henchman", 175111, CrownShopCategories.Henchmen, 20, 0, null, 20, true, "henchmen"),
                new("Eldon Dragonbloom", 191184, CrownShopCategories.Henchmen, 20, 0, null, 20, true, "henchmen"),
            ]).ToArray();

            var only = Assert.Single(shown);
            Assert.Equal((175111UL, 20, 0), (only.Template, only.Crowns, only.Gold));
        } finally {
            if (prior is null) cache.Remove(175111); else cache[175111] = prior;
        }
    }

    // --- the duel ---------------------------------------------------------------------------------------------------

    private sealed class Probe : ReceiveActor {
        public Probe(BlockingCollection<object> inbox) => ReceiveAny(inbox.Add);
    }

    private static (CombatDuelComponent Duel, CombatDuelSubCircle Buyer, BlockingCollection<object> Inbox, ActorSystem System)
        Duel(bool active = true, bool pvp = false, bool noHenchmen = false, kDuelPhase phase = kDuelPhase.kPhase_Planning) {
        var system = ActorSystem.Create("henchmen-tests-" + Guid.NewGuid().ToString("N"));
        var inbox = new BlockingCollection<object>();
        var probe = system.ActorOf(Props.Create(() => new Probe(inbox)));
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel {
            m_duelPhase = phase, m_bPVP = pvp, m_noHenchmen = noHenchmen,
            m_duelModifier = new DuelModifier { m_battlefieldEffects = [] },
        });
        typeof(CombatDuelComponent).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, active);
        var buyer = Occupy(duel, 4, probe);
        return (duel, buyer, inbox, system);
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, IActorRef actor, CombatDuelSubCircle? owner = null) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = owner is null ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", actor);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        CombatRegressionTests.SetProperty(circle, "IsSummonedMinion", owner is not null);
        if (owner is not null) circle.CaptureMinionOwner(owner.SlotIndex);
        circle.AddedToDuel = true;
        return circle;
    }

    private static COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED Hire(CombatDuelComponent duel, CombatDuelSubCircle buyer,
                                                              BlockingCollection<object> inbox) {
        CombatRegressionTests.Invoke(duel, "ReceiveHireHenchman", new COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN {
            CreatureTid = 191184, Actor = buyer.ParticipantActor, Level = 20,
        });
        Assert.True(inbox.TryTake(out var answer, TimeSpan.FromSeconds(5)));
        return Assert.IsType<COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED>(answer);
    }

    [Theory]
    [InlineData(false, false, false, kDuelPhase.kPhase_Planning, HenchmanRefusal.NotInCombat)]
    [InlineData(true, true, false, kDuelPhase.kPhase_Planning, HenchmanRefusal.Pvp)]
    [InlineData(true, false, true, kDuelPhase.kPhase_Planning, HenchmanRefusal.NotThisSigil)]
    [InlineData(true, false, false, kDuelPhase.kPhase_Execution, HenchmanRefusal.NotPlanning)]
    public void TheDuelRefusesAHenchmanOutsideAPveCardSelection(bool active, bool pvp, bool noHenchmen, kDuelPhase phase,
                                                                HenchmanRefusal expected) {
        var (duel, buyer, inbox, system) = Duel(active, pvp, noHenchmen, phase);
        try {
            var answer = Hire(duel, buyer, inbox);
            Assert.Equal((false, expected, 191184u), (answer.Success, answer.Refusal, answer.CreatureTid));
            Assert.All(duel.SubCircles.Skip(5), circle => Assert.False(circle.Occupied));
        } finally {
            system.Terminate().Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AFullCircleRefusesAHenchmanSoTheShopRefunds() {
        var (duel, buyer, inbox, system) = Duel();
        try {
            for (var slot = 5; slot < 8; slot++) Occupy(duel, slot, ActorRefs.Nobody);
            Assert.Equal(HenchmanRefusal.SigilFull, Hire(duel, buyer, inbox).Refusal);
        } finally {
            system.Terminate().Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ADefeatedBuyerCannotHire() {
        var (duel, buyer, inbox, system) = Duel();
        try {
            buyer.ParticipantGameStats.m_currentHitpoints = 0;
            Assert.Equal(HenchmanRefusal.Unavailable, Hire(duel, buyer, inbox).Refusal);
        } finally {
            system.Terminate().Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AHenchmanPlaysAWizardDeckAtItsLevelAndOnlyAMinionCanBecomeOne() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var owner = Occupy(duel, 4, ActorRefs.Nobody);
        var henchman = Occupy(duel, 5, ActorRefs.Nobody, owner);
        var deck = new List<SpellData> { new() { m_templateID = 1001, m_quantity = 6 }, new() { m_templateID = 1002, m_quantity = 4 } };

        henchman.BecomeHenchman(30, deck);
        owner.BecomeHenchman(30, deck);

        Assert.True(henchman.IsHenchman);
        Assert.Equal(30, henchman.HenchmanLevel);
        Assert.Equal(10u, henchman.TotalSpells);
        Assert.True(henchman.IsOwnedMinionOf(owner));
        Assert.False(owner.IsHenchman);
        henchman.RemoveParticipant();
        Assert.False(henchman.IsHenchman);
        Assert.Equal(0, henchman.HenchmanLevel);
    }

    [Fact]
    public void OnlyTheBuyerCanDismissTheirHenchman() {
        var (duel, buyer, inbox, system) = Duel();
        try {
            var stranger = Occupy(duel, 6, ActorRefs.Nobody);
            var henchman = Occupy(duel, 5, ActorRefs.Nobody, buyer);
            henchman.BecomeHenchman(20, []);
            CombatRegressionTests.Invoke(duel, "ReceiveDismissHenchman", new COMBAT_106_PROTOCOL.MSG_DISMISSHENCHMAN {
                Actor = stranger.ParticipantActor, SubCircle = 5,
            });
            CombatRegressionTests.Invoke(duel, "ReceiveDismissHenchman", new COMBAT_106_PROTOCOL.MSG_DISMISSHENCHMAN {
                Actor = buyer.ParticipantActor, SubCircle = 4,
            });
            CombatRegressionTests.Invoke(duel, "ReceiveDismissHenchman", new COMBAT_106_PROTOCOL.MSG_DISMISSHENCHMAN {
                Actor = buyer.ParticipantActor, SubCircle = 99,
            });
            Assert.True(henchman.Occupied);
            Assert.True(henchman.IsHenchman);
        } finally {
            system.Terminate().Wait(TimeSpan.FromSeconds(5));
        }
    }

}
