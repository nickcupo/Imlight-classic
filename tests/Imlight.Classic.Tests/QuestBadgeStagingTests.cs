using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: authored fixtures only. Shares the collection of the existing static badge hooks.
[Collection(nameof(BadgeRulesTests))]
public sealed class QuestBadgeStagingTests {
    private const string FirstQuest = "QA-BADGE-FIRST";
    private const string SecondQuest = "QA-BADGE-SECOND";

    [Fact]
    public void StagingRequiresCompletionAndPreparesPacketsWithoutSavingOrPublishing() {
        var badge = QuestBadge("first");
        using var hooks = new Hooks(Rules(badge));
        var saved = NewWizard();

        Assert.Empty(ClassicBadges.StageQuestCompleted(saved, FirstQuest));
        saved.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);
        var award = Assert.Single(ClassicBadges.StageQuestCompleted(saved, FirstQuest));

        Assert.Same(badge, award.Badge);
        Assert.Equal(1UL, saved.QuestBehavior.Registry[BadgeRules.BadgeKey(badge.Id)]);
        Assert.Equal(badge.NameKey, (string) award.Message.BadgeName);
        Assert.Equal(ClassicBadges.NameId(badge), award.Message.BadgeNameID);
        Assert.Equal(1, award.Message.Add);
        Assert.Equal(1, award.Message.Display);
        Assert.Empty(hooks.Events);
    }

    [Fact]
    public void AllQuestAwardsReadEveryCompletionMarkFromTheStagedWizard() {
        var badge = MakeBadge("all", new AllQuestsBadgeAward([FirstQuest, SecondQuest]));
        using var hooks = new Hooks(Rules(badge));
        var saved = NewWizard();
        saved.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);

        Assert.Empty(ClassicBadges.StageQuestCompleted(saved, FirstQuest));
        saved.QuestBehavior.SetQuestRegistryValue(SecondQuest, "Complete", 1);

        Assert.Same(badge, Assert.Single(ClassicBadges.StageQuestCompleted(saved, SecondQuest)).Badge);
        Assert.Empty(hooks.Events);
    }

    [Fact]
    public void AnExistingZeroBadgeEntryStillBlocksAdmission() {
        var badge = QuestBadge("first");
        using var hooks = new Hooks(Rules(badge));
        var saved = NewWizard();
        saved.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);
        saved.QuestBehavior.Registry[BadgeRules.BadgeKey(badge.Id)] = 0;

        Assert.False(ClassicBadges.Has(saved, badge));
        Assert.Empty(ClassicBadges.StageQuestCompleted(saved, FirstQuest));
        Assert.Equal(0UL, saved.QuestBehavior.Registry[BadgeRules.BadgeKey(badge.Id)]);
        Assert.Empty(hooks.Events);
    }

    [Fact]
    public void ASecondStagingPassDoesNotReadmitTheSameBadge() {
        var badge = QuestBadge("first");
        using var hooks = new Hooks(Rules(badge));
        var saved = NewWizard();
        saved.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);

        Assert.Single(ClassicBadges.StageQuestCompleted(saved, FirstQuest));
        Assert.Empty(ClassicBadges.StageQuestCompleted(saved, FirstQuest));
        Assert.Empty(hooks.Events);
    }

    [Fact]
    public void ConcurrentStagingAdmitsTheBadgeToOnePreparedBatch() {
        using var hooks = new Hooks(Rules(QuestBadge("first")));
        var saved = NewWizard();
        saved.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);
        var awards = new ConcurrentBag<ClassicBadges.PreparedQuestBadgeAward>();

        Parallel.For(0, 4, _ => {
            foreach (var award in ClassicBadges.StageQuestCompleted(saved, FirstQuest)) awards.Add(award);
        });

        Assert.Single(awards);
        Assert.Empty(hooks.Events);
    }

    [Fact]
    public void PublishingOnlyLogsAndSendsThePreparedObjects() {
        var badge = QuestBadge("first");
        using var hooks = new Hooks(Rules(badge));
        var saved = NewWizard();
        saved.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);
        var awards = ClassicBadges.StageQuestCompleted(saved, FirstQuest);
        var live = NewWizard();
        var sent = new List<IMessage>();

        ClassicBadges.PublishQuestCompleted(live, awards, sent.Add);

        Assert.Equal(["award:first"], hooks.Events);
        Assert.Same(Assert.Single(awards).Message, Assert.Single(sent));
        Assert.Empty(live.QuestBehavior.Registry);
        Assert.Empty(live.QuestBehavior.CurrentQuestIDs);
    }

    [Fact]
    public void StandaloneAwardsStillSaveEachBadgeBeforeLoggingAndSendingIt() {
        using var hooks = new Hooks(Rules(QuestBadge("first"), QuestBadge("second")));
        var live = NewWizard();
        live.QuestBehavior.SetQuestRegistryValue(FirstQuest, "Complete", 1);

        ClassicBadges.QuestCompleted(live, FirstQuest, message => {
            var packet = Assert.IsType<GAME_5_PROTOCOL.MSG_BADGES>(message);
            hooks.Events.Add("send:" + ((string) packet.BadgeName).Split('_')[1]);
        });

        Assert.Equal(["save", "award:first", "send:first", "save", "award:second", "send:second"], hooks.Events);
    }

    private static Wizard NewWizard() => new() { CharId = 77, QuestBehavior = new ServerQuestBehavior() };

    private static Badge QuestBadge(string id) => MakeBadge(id, new QuestBadgeAward([FirstQuest]));

    private static Badge MakeBadge(string id, BadgeAward award)
        => new(id, "Authored fixture", "AuthoredBadge_" + id, null, "AuthoredTitle_" + id,
            "AuthoredFilter_Fixture", "wizard_city", award);

    private static BadgeRules Rules(params Badge[] badges) => new() {
        Id = "badges-authored-fixture",
        Profiles = ["late-2009"],
        Badges = [.. badges],
        SourceFile = "authored-fixture",
    };

    private sealed class Hooks : IDisposable {
        private readonly Action<Wizard> _persist = ClassicBadges.Persist;
        private readonly Func<BadgeRules?> _rules = ClassicBadges.Rules;
        private readonly Action<Wizard, Badge> _awarded = ClassicBadges.Awarded;

        public List<string> Events { get; } = [];

        public Hooks(BadgeRules rules) {
            ClassicBadges.Rules = () => rules;
            ClassicBadges.Persist = _ => Events.Add("save");
            ClassicBadges.Awarded = (_, badge) => Events.Add("award:" + badge.Id);
        }

        public void Dispose() {
            ClassicBadges.Persist = _persist;
            ClassicBadges.Rules = _rules;
            ClassicBadges.Awarded = _awarded;
        }
    }
}
