// CLASSIC: strategy preferences operate only on public battle information and legal hand choices.
using System;
using System.Linq;
using Imlight.Classic.Ambient;
using Imlight.Classic.Pvp;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaPvpBrainTests {
    private static ArenaPvpCombatant Wizard(int slot, bool ally, int hp = 1000, int pips = 3,
        string school = "Fire", params ArenaModifier[] modifiers)
        => new(slot, ally, school, hp, 1000, pips, modifiers);
    private static ArenaPvpCard Hit(int index, double amount, int pips = 2, double accuracy = 1,
        bool castable = true, int available = 4, bool dot = false, double drain = 0)
        => new(index, (uint) (100 + index), "fixture hit", "Fire", pips, available, castable, accuracy,
            ArenaCardRole.Damage, [new("Fire", amount, dot, drain)], []);
    private static ArenaPvpCard Support(int index, ArenaCardRole role, ArenaModifier modifier)
        => new(index, (uint) (100 + index), "fixture support", "Fire", 0, 4, true, 1, role, [], [modifier]);
    private static ArenaPvpCard Heal(int index, bool self = false, bool hot = false)
        => new(index, (uint) (100 + index), "fixture heal", "Life", 2, 4, true, 1, ArenaCardRole.Heal, [], [],
            Heal: 400, HealOverTime: hot, SelfOnly: self);
    private static ArenaPvpView View(ArenaPvpCard[] hand, params ArenaPvpCombatant[] combatants)
        => new(0, false, 8, hand, combatants);
    private static AllyMove Choose(ArenaPvpView view) => ArenaPvpBrain.Choose(view, new Random(42));

    [Fact]
    public void StunnedWizardPassesAndCannotDiscard() {
        var view = View([Hit(0, 400)], Wizard(0, true), Wizard(4, false)) with { Stunned = true };
        Assert.Equal(AllyMoveKind.Pass, Choose(view).Kind);
        Assert.Null(ArenaPvpBrain.DiscardChoice(view));
    }

    [Fact]
    public void OffSchoolCardMustBeActuallyAffordableNotJustHaveTwoPipsPerPowerPip() {
        var move = Choose(View([Hit(0, 900, castable: false, available: 2), Hit(1, 200)], Wizard(0, true), Wizard(4, false)));
        Assert.Equal(1, move.HandIndex);
    }

    [Fact]
    public void CheapReliableFinishBeatsLargeHitAndSetup() {
        var blade = Support(2, ArenaCardRole.Blade, new(102, ArenaModifierKind.OutgoingDamage, "Fire", 40));
        var move = Choose(View([Hit(0, 150, 1), Hit(1, 800, 6), blade], Wizard(0, true), Wizard(4, false, 100)));
        Assert.Equal(0, move.HandIndex);
        Assert.Contains("finish", move.Reason);
    }

    [Fact]
    public void DamageOverTimeAndTenPercentBoltAreNotReliableImmediateFinishes() {
        var move = Choose(View([Hit(0, 1000, 2, .1), Hit(1, 500, 2, dot: true), Hit(2, 120, 1)],
            Wizard(0, true), Wizard(4, false, 110)));
        Assert.Equal(2, move.HandIndex);
        Assert.Contains("finish", move.Reason);
    }

    [Fact]
    public void VisibleShieldChangesTargetAndQueuedTeammateFocusBreaksEqualTargets() {
        var shield = new ArenaModifier(1, ArenaModifierKind.IncomingDamage, "Fire", -80);
        Assert.Equal(5, Choose(View([Hit(0, 300)], Wizard(0, true), Wizard(4, false, modifiers: [shield]), Wizard(5, false))).TargetSlot);
        var focused = Wizard(5, false) with { TeamFocus = 2 };
        Assert.Equal(5, Choose(View([Hit(0, 300)], Wizard(0, true), Wizard(4, false), focused)).TargetSlot);
    }

    [Fact]
    public void OutgoingWeaknessAndVisibleBladeTrapUseTheirRealSignedPercentages() {
        var self = Wizard(0, true, modifiers: [new(1, ArenaModifierKind.OutgoingDamage, "Fire", 40),
            new(2, ArenaModifierKind.OutgoingDamage, "All", -25)]);
        var enemy = Wizard(4, false, modifiers: [new(3, ArenaModifierKind.IncomingDamage, "Fire", 25)]);
        Assert.Equal(262.5, ArenaPvpBrain.DamageTo(Hit(0, 200), self, enemy).Immediate, 6);
    }

    [Fact]
    public void ConsecutiveHitsConsumeOneShieldButRandomAlternativesEachMeetIt() {
        var self = Wizard(0, true);
        var target = Wizard(4, false, modifiers: [new(1, ArenaModifierKind.IncomingDamage, "Fire", -50)]);
        var twoHits = Hit(0, 100) with { Damage = [new("Fire", 100), new("Fire", 100)] };
        Assert.Equal(150, ArenaPvpBrain.DamageTo(twoHits, self, target).Immediate);
        var random = twoHits with { DamageBranches = [new(.5, [new("Fire", 100)]), new(.5, [new("Fire", 300)])] };
        Assert.Equal(100, ArenaPvpBrain.DamageTo(random, self, target).Immediate);
        Assert.Single(target.Modifiers); // Planning never consumes the live ward.
    }

    [Fact]
    public void PartiallyConsumedAbsorbProtectsTheFollowingHitWithoutChangingTheView() {
        var target = Wizard(4, false, modifiers: [new(1, ArenaModifierKind.Absorb, "All", 150)]);
        var hit = Hit(0, 100) with { Damage = [new("Fire", 100), new("Fire", 100)] };
        Assert.Equal(50, ArenaPvpBrain.DamageTo(hit, Wizard(0, true), target).Immediate);
        Assert.Equal(150, Assert.Single(target.Modifiers).Amount);
    }

    [Fact]
    public void EmergencyHealTargetsEndangeredTeammateButSelfHealCannot() {
        var ally = Wizard(1, true, 100);
        var ordinary = Choose(View([Hit(0, 250), Heal(1)], Wizard(0, true), ally, Wizard(4, false)));
        Assert.Equal(1, ordinary.HandIndex);
        Assert.Equal(1, ordinary.TargetSlot);
        var selfOnly = Choose(View([Hit(0, 250), Heal(1, self: true)], Wizard(0, true), ally, Wizard(4, false)));
        Assert.Equal(0, selfOnly.HandIndex);
    }

    [Fact]
    public void DirectHealPrioritisesEligibleRevivalButHotAndRemovedWizardDoNot() {
        var down = Wizard(1, true, 0) with { Revivable = true };
        var view = View([Hit(0, 250), Heal(1)], Wizard(0, true), down, Wizard(4, false));
        var move = Choose(view);
        Assert.Equal(1, move.HandIndex);
        Assert.Equal(1, move.TargetSlot);
        Assert.Contains("revive", move.Reason);
        Assert.Equal(0, Choose(view with { Hand = [Hit(0, 250), Heal(1, hot: true)] }).HandIndex);
        Assert.Equal(0, Choose(view with { Combatants = [Wizard(0, true), down with { Revivable = false }, Wizard(4, false)] }).HandIndex);
    }

    [Fact]
    public void TeamHealCountsEveryEndangeredAlly() {
        var card = Heal(1) with { AllAllies = true };
        var move = Choose(View([Hit(0, 500), card], Wizard(0, true, 200), Wizard(1, true, 200), Wizard(4, false)));
        Assert.Equal(1, move.HandIndex);
        Assert.Equal(0, move.TargetSlot);
    }

    [Fact]
    public void BladeSetsUpAffordableNextRoundHitAndDoesNotDuplicateExistingBlade() {
        var blade = Support(1, ArenaCardRole.Blade, new(101, ArenaModifierKind.OutgoingDamage, "Fire", 40)) with { SelfOnly = true };
        var view = View([Hit(0, 700, 5, castable: false, available: 4), blade], Wizard(0, true), Wizard(4, false));
        Assert.Equal(1, Choose(view).HandIndex);
        var already = Wizard(0, true, modifiers: [blade.Modifiers[0]]);
        Assert.Equal(AllyMoveKind.Pass, Choose(view with { Combatants = [already, Wizard(4, false)] }).Kind);
    }

    [Fact]
    public void ShieldRespondsToSchoolPipsAndBladesAndWeaknessTargetsPreparedEnemy() {
        var shield = Support(1, ArenaCardRole.Shield, new(101, ArenaModifierKind.IncomingDamage, "Storm", -80));
        var storm = Wizard(4, false, pips: 7, school: "Storm", modifiers: [new(9, ArenaModifierKind.OutgoingDamage, "Storm", 40)]);
        var move = Choose(View([Hit(0, 100), shield], Wizard(0, true, 300), storm));
        Assert.Equal(1, move.HandIndex);
        var weakness = Support(2, ArenaCardRole.Weakness, new(102, ArenaModifierKind.OutgoingDamage, "All", -25));
        var weakMove = Choose(View([Hit(0, 10), weakness], Wizard(0, true), Wizard(4, false, pips: 0), storm with { Slot = 5 }));
        Assert.Equal(2, weakMove.HandIndex);
        Assert.Equal(5, weakMove.TargetSlot);
    }

    [Fact]
    public void SafePipSavingDoesNotOverrideUrgentHealing() {
        var hand = new[] { Hit(0, 100, 1), Hit(1, 600, 5, castable: false, available: 4) };
        Assert.Equal(AllyMoveKind.Pass, Choose(View(hand, Wizard(0, true), Wizard(4, false))).Kind);
        Assert.Equal(2, Choose(View([.. hand, Heal(2)], Wizard(0, true, 100), Wizard(4, false))).HandIndex);
    }

    [Fact]
    public void ReshuffleRecoversExhaustedDeckButNotAnEarlyFullPile() {
        var shuffle = new ArenaPvpCard(0, 100, "fixture reshuffle", "Balance", 4, 4, true, 1, ArenaCardRole.Reshuffle, [], [], SelfOnly: true);
        var view = View([shuffle], Wizard(0, true), Wizard(4, false));
        Assert.Equal(AllyMoveKind.Pass, Choose(view).Kind);
        Assert.Equal(0, Choose(view with { RemainingCards = 0 }).HandIndex);
    }

    [Fact]
    public void SupportOnlyHandCyclesWithoutDiscardingProtectedCardsOrReshuffle() {
        var a = Support(0, ArenaCardRole.Shield, new(100, ArenaModifierKind.IncomingDamage, "Ice", -50));
        var b = a with { HandIndex = 1, TemplateId = 101, Discardable = false };
        var shuffle = a with { HandIndex = 2, TemplateId = 102, Role = ArenaCardRole.Reshuffle };
        var view = View([a, b, shuffle], Wizard(0, true), Wizard(4, false));
        Assert.Equal(0, ArenaPvpBrain.DiscardChoice(view));
        Assert.Null(ArenaPvpBrain.DiscardChoice(view with { RemainingCards = 0 }));
        Assert.Null(ArenaPvpBrain.DiscardChoice(view with { Hand = [.. view.Hand, Hit(3, 100)] }));
    }

    [Fact]
    public void AvoidsLethalSelfDamageAndTiesReplayWithTheSameSeed() {
        var dangerous = Hit(0, 900) with { SelfDamage = 200 };
        var view = View([dangerous, Hit(1, 100)], Wizard(0, true, 200), Wizard(4, false));
        Assert.Equal(1, Choose(view).HandIndex);
        var tied = View([Hit(0, 200), Hit(1, 200)], Wizard(0, true), Wizard(4, false), Wizard(5, false));
        var a = new Random(99); var b = new Random(99);
        Assert.Equal(Enumerable.Range(0, 20).Select(_ => ArenaPvpBrain.Choose(tied, a)).ToArray(),
                     Enumerable.Range(0, 20).Select(_ => ArenaPvpBrain.Choose(tied, b)).ToArray());
    }
    [Fact]
    public void BeginnerAndIntermediateAreActualSkillDifferencesWithoutChangingCardsOrRolls() {
        var shield = new ArenaModifier(1, ArenaModifierKind.IncomingDamage, "Fire", -80);
        var view = View([Hit(0, 100, 1), Hit(1, 300, 2)], Wizard(0, true),
            Wizard(4, false, modifiers: [shield]), Wizard(5, false));
        var beginner = ArenaPvpBrain.Choose(view, new Random(42), ArenaPvpSkill.Beginner);
        var advanced = ArenaPvpBrain.Choose(view, new Random(42), ArenaPvpSkill.Advanced);
        Assert.Equal((0, 4), (beginner.HandIndex, beginner.TargetSlot));
        Assert.Equal(5, advanced.TargetSlot);
        var focused = View([Hit(0, 300)], Wizard(0, true), Wizard(4, false), Wizard(5, false) with { TeamFocus = 2 });
        Assert.Equal(5, ArenaPvpBrain.Choose(focused, new Random(42), ArenaPvpSkill.Advanced).TargetSlot);
        Assert.Equal(4, ArenaPvpBrain.Choose(focused, new Random(1), ArenaPvpSkill.Intermediate).TargetSlot);
        Assert.Equal(100, view.Hand[0].Damage[0].Amount); Assert.Single(view.Combatants[1].Modifiers);
    }

    [Theory]
    [InlineData(ArenaPvpSkill.Beginner)] [InlineData(ArenaPvpSkill.Intermediate)] [InlineData(ArenaPvpSkill.Advanced)]
    public void EverySkillTierRefusesIllegalCastsStunsAndLethalSelfDamage(ArenaPvpSkill skill) {
        var view = View([Hit(0, 900, castable: false), Hit(1, 800) with { SelfDamage = 200 }, Hit(2, 50)],
            Wizard(0, true, 200), Wizard(4, false));
        Assert.Equal(2, ArenaPvpBrain.Choose(view, new Random(42), skill).HandIndex);
        Assert.Equal(AllyMoveKind.Pass, ArenaPvpBrain.Choose(view with { Stunned = true }, new Random(42), skill).Kind);
    }

}
