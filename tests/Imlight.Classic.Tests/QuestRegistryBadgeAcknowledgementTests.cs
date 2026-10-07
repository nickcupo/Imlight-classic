// CLASSIC: actual standalone badge entry points use selected fresh registry changes and acknowledged publication.
using System;
using System.Collections.Generic;
using System.Reflection;
using Imcodec.MessageLayer;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class QuestRegistryBadgeAcknowledgementTests {
    private static void UseProductionBadgePersistence(TerminalClaimFixture f, Badge badge) {
        ClassicBadges.Persist = (Action<Wizard>)typeof(ClassicBadges)
            .GetField("DefaultPersist", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        ClassicBadges.Rules = () => new BadgeRules { Id = "authored-ack-badges", Profiles = ["late-2009"],
            Badges = [badge], SourceFile = "authored-fixture" };
    }
    private static Badge ZoneBadge() => new("ack-zone", "Authored visit", "QA_Badge_Visit", null,
        "QA_Title", "QA_Filter", "wizard_city", new ZoneVisitBadgeAward("QA/Claim", 2));
    private static Badge QuestBadge() => new("ack-quest", "Authored quest", "QA_Badge_Quest", null,
        "QA_Title", "QA_Filter", "wizard_city", new QuestBadgeAward([TerminalClaimFixture.QuestName]));

    [Fact]
    public void ZoneCounterUsesSavedCountAndSavesItsNewBadgeTogetherBeforePublication() {
        using var f = new TerminalClaimFixture(); var badge = ZoneBadge(); UseProductionBadgePersistence(f, badge);
        var packets = new List<IMessage>(); var behavior = f.Live.QuestBehavior; var registry = behavior.Registry;
        var key = BadgeRules.ZoneVisitKey(f.Live.Zone); registry[key] = 99;
        f.OnSave = () => { Assert.Empty(packets); Assert.Equal(99UL, registry[key]); Assert.False(ClassicBadges.Has(f.Live, badge)); };
        ClassicBadges.ZoneEntered(f.Live, f.Live.Zone, packets.Add);
        Assert.Equal(1, f.Saves); Assert.Equal(1UL, f.Saved.GetRegistryValue(key)); Assert.Equal(1UL, registry[key]);
        Assert.Empty(packets); Assert.False(ClassicBadges.Has(f.Live, badge));
        f.OnSave = () => { Assert.Empty(packets); Assert.Equal(1UL, registry[key]); Assert.False(ClassicBadges.Has(f.Live, badge));
            Assert.Equal(2UL, f.Working!.Wizard.GetRegistryValue(key)); Assert.True(ClassicBadges.Has(f.Working.Wizard, badge)); };
        ClassicBadges.ZoneEntered(f.Live, f.Live.Zone, message => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(2, f.Saves);
            Assert.True(ClassicBadges.Has(f.Saved, badge)); packets.Add(message);
        });
        Assert.Equal(2, f.Saves); Assert.Single(packets); Assert.Equal(2UL, registry[key]); Assert.True(ClassicBadges.Has(f.Live, badge));
        Assert.Same(behavior, f.Live.QuestBehavior); Assert.Same(registry, behavior.Registry);
        Assert.Equal(45UL, registry["LiveUnrelated"]); Assert.Equal(21UL, f.Saved.GetRegistryValue("SavedUnrelated"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ZoneCounterAndBadgeUnknownSaveCannotChangeLiveStateOrSendAnAward(bool durable) {
        using var f = new TerminalClaimFixture { FailSave = true, Durable = durable };
        var badge = ZoneBadge(); UseProductionBadgePersistence(f, badge);
        var key = BadgeRules.ZoneVisitKey(f.Live.Zone); f.Saved.QuestBehavior.Registry[key] = 1;
        var packets = new List<IMessage>();
        ClassicBadges.ZoneEntered(f.Live, f.Live.Zone, packets.Add);
        Assert.Equal(1, f.Saves); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Empty(packets); Assert.False(ClassicBadges.Has(f.Live, badge)); Assert.False(f.Live.HasRegistryValue(key));
        Assert.Equal(durable, ClassicBadges.Has(f.Saved, badge)); Assert.Equal(durable ? 2UL : 1UL, f.Saved.GetRegistryValue(key));
    }

    [Fact]
    public void BadgeNativePreparationRefusalLeavesCountersAndOriginalsUnchanged() {
        using var f = new TerminalClaimFixture(); var badge = ZoneBadge(); UseProductionBadgePersistence(f, badge);
        var key = BadgeRules.ZoneVisitKey(f.Live.Zone); f.Saved.QuestBehavior.Registry[key] = 1;
        f.Dependencies.Prepare = _ => false; var packets = new List<IMessage>();
        ClassicBadges.ZoneEntered(f.Live, f.Live.Zone, packets.Add);
        Assert.Equal(0, f.Saves); Assert.Equal(1UL, f.Saved.GetRegistryValue(key)); Assert.Empty(packets);
        Assert.False(ClassicBadges.Has(f.Live, badge)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void QuestBadgeRequiresFreshCompletionAndRepeatingAnEarnedBadgeMakesNoSaveOrSuccess() {
        using var f = new TerminalClaimFixture(); var badge = QuestBadge(); UseProductionBadgePersistence(f, badge);
        f.Live.QuestBehavior.SetQuestRegistryValue(TerminalClaimFixture.QuestName, "Complete", 1);
        var packets = new List<IMessage>();
        ClassicBadges.QuestCompleted(f.Live, TerminalClaimFixture.QuestName, packets.Add);
        Assert.Equal(0, f.Saves); Assert.Empty(packets); Assert.False(ClassicBadges.Has(f.Live, badge));
        f.Saved.QuestBehavior.SetQuestRegistryValue(TerminalClaimFixture.QuestName, "Complete", 1);
        ClassicBadges.QuestCompleted(f.Live, TerminalClaimFixture.QuestName, packets.Add);
        Assert.Equal(1, f.Saves); Assert.Single(packets); Assert.True(ClassicBadges.Has(f.Live, badge));
        ClassicBadges.QuestCompleted(f.Live, TerminalClaimFixture.QuestName, packets.Add);
        Assert.Equal(1, f.Saves); Assert.Single(packets);
    }

    [Fact]
    public void AlreadyUncertainBadgeCallDoesNotOpenASessionOrAdvanceTheCounter() {
        using var f = new TerminalClaimFixture(); var badge = ZoneBadge(); UseProductionBadgePersistence(f, badge);
        WizardCollection.MarkInventorySnapshotUncertain(f.Live); var packets = new List<IMessage>();
        ClassicBadges.ZoneEntered(f.Live, f.Live.Zone, packets.Add);
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves); Assert.Empty(packets);
        Assert.False(f.Live.HasRegistryValue(BadgeRules.ZoneVisitKey(f.Live.Zone)));
    }
}
