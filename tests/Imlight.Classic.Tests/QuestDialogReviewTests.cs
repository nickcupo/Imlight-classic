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
 * ========================================================================
 * QUEST DIALOGUE REVIEW
 * ========================================================================
 *
 * PURPOSE:
 * The spellbook's ? button (MSG_REQUESTQUESTDIALOG -> MSG_QUESTDIALOG,
 * QuestDialogReview): the lines a wizard has had for a held quest, offer
 * first, then each finished goal's talk, nothing ahead of them; other ids
 * answered with nothing; read only; rate limited.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter QuestDialogReview
 *
 * NOTE:
 * Uses the Lost Lieutenant overlay (classic-data). Its expected review after
 * three goals is the one the live server sent a real client (DialogCache,
 * 2014 client): WizQst2EBE5 lines 4 to 16 in that order.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class QuestDialogReviewTests {

    private const string LostLieutenant = "WC-GNT-C01-001";

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static QuestTemplate Load(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(Path.Combine(ClassicDataFixture.Root,
            "spiraldb-overlay", "QuestTemplates", name + ".json")), s_json)!;

    private static readonly Func<RequirementList, bool> s_all = _ => true;

    private static string Key(int line) => $"WizQst2EBE5_{line:D8}";

    private static List<string> Lines(ActorDialog dialog) => [.. dialog.m_dialogEntries.Select(e => (string) e.m_dialog)];

    private static List<string> LinesOf(ActorDialogListBase list, string tag)
        => [.. ((ActorDialogList) list).m_dialogs.Single(d => d.m_dialogTag == tag).m_dialogEntries.Select(e => (string) e.m_dialog)];

    // Completes the quest's goals in order up to (not including) goal index <paramref name="upTo"/> and starts that one.
    private static QuestInstance HeldAt(QuestTemplate quest, int upTo) {
        var instance = new QuestInstance(quest, 7);
        for (var i = 0; i < upTo; i++) {
            instance.CompleteGoal(quest.m_goals[i].m_goalName);
            if (i + 1 < quest.m_goals.Count) {
                instance.StartGoal(quest.m_goals[i + 1].m_goalName);
            }
        }

        return instance;
    }

    private static Wizard Holding(params QuestInstance[] quests) {
        var wizard = new Wizard { CharId = 7, QuestBehavior = new ServerQuestBehavior() };
        foreach (var quest in quests) {
            wizard.QuestBehavior.AddQuest(quest);
        }

        return wizard;
    }

    // --- what is shown, in order ---

    [Fact]
    public void AFreshQuestShowsItsOfferOnly() {
        var quest = Load(LostLieutenant);
        var dialog = QuestDialogReview.Dialog(quest, new QuestInstance(quest, 7), s_all);
        Assert.Equal(LinesOf(quest.m_dialogList, "Prep"), Lines(dialog));
        Assert.Equal([Key(4), Key(5), Key(6), Key(7)], Lines(dialog));
    }

    [Fact]
    public void AfterThreeTalksItIsTheLiveServersAnswer() {
        // The live server's review of this quest at its fourth goal (a real client's DialogCache): lines 4..16.
        var quest = Load(LostLieutenant);
        var dialog = QuestDialogReview.Dialog(quest, HeldAt(quest, 3), s_all);
        Assert.Equal(Enumerable.Range(4, 13).Select(Key), Lines(dialog));

        // One dialog with an empty tag, no events, each source dialog's NPC name block kept (offer + 3 talks).
        Assert.Equal("", dialog.m_dialogTag);
        Assert.Empty(dialog.m_dialogEvents);
        Assert.Equal(4, dialog.m_madlibs.Count);
        Assert.Equal(["WC-ST02-NPC01_Persona", "WC-SHP-NPC01_Persona", "WC-ST06-NPC01_Persona", "WC-ST06-NPC04_Persona"],
            dialog.m_dialogEntries.OfType<NPCDialogEntry>().Select(e => e.m_personaName.ToString()).Distinct());
    }

    [Fact]
    public void EachTalkIsAddedOnlyOnceItHappens() {
        var quest = Load(LostLieutenant);
        var offer = LinesOf(quest.m_dialogList, "Prep");
        var expected = new List<string>(offer);
        for (var done = 0; done <= 3; done++) {
            Assert.Equal(expected, Lines(QuestDialogReview.Dialog(quest, HeldAt(quest, done), s_all)));
            if (done < 3) {
                expected.AddRange(LinesOf(quest.m_goals[done].m_dialogList, "Completion"));
            }
        }

        // Never the giver's "still waiting" chatter, never the hand-in to Culpepper before it happens.
        var all = Lines(QuestDialogReview.Dialog(quest, HeldAt(quest, 3), s_all));
        Assert.DoesNotContain(all, LinesOf(quest.m_dialogList, "Underway").Contains);
        Assert.DoesNotContain(all, LinesOf(quest.m_goals[3].m_dialogList, "Completion").Contains);
    }

    [Fact]
    public void GoalsFollowTheGoalLogicNotTheTemplateOrder() {
        // Goals listed backwards in the template; the logic runs B -> A.
        static GoalTemplate Goal(string name, string line) => new PersonaGoalTemplate {
            m_goalName = name,
            m_dialogList = new ActorDialogList {
                m_dialogs = [new ActorDialog { m_dialogTag = "Completion", m_dialogEntries = [new NPCDialogEntry { m_dialog = line }] }],
            },
        };
        var quest = new QuestTemplate {
            m_questName = "TEST-ORDER",
            m_goals = [Goal("A", "a-done"), Goal("B", "b-done")],
            m_startGoals = ["B"],
            m_goalLogic = [new GoalCompleteLogic { m_goalsAND = ["B"], m_goalsToAdd = ["A"] }],
            m_dialogList = new ActorDialogList {
                m_dialogs = [
                    new ActorDialog { m_dialogTag = "Prep", m_dialogEntries = [new NPCDialogEntry { m_dialog = "offer" }] },
                    new ActorDialog { m_dialogTag = "Start", m_dialogEntries = [new NPCDialogEntry { m_dialog = "start" }] },
                    new ActorDialog { m_dialogTag = "Underway", m_dialogEntries = [new NPCDialogEntry { m_dialog = "waiting" }] },
                    new ActorDialog { m_dialogTag = "Complete", m_dialogEntries = [new NPCDialogEntry { m_dialog = "thanks" }] },
                ],
            },
        };
        Assert.Equal(["B", "A"], QuestDialogReview.GoalsInOrder(quest).Select(g => g.m_goalName));

        var instance = new QuestInstance(quest, 7);
        instance.CompleteGoal("B");
        instance.StartGoal("A");
        instance.CompleteGoal("A");
        Assert.Equal(["offer", "start", "b-done", "a-done"], Lines(QuestDialogReview.Dialog(quest, instance, s_all)));
    }

    // --- how lines are sent ---

    [Fact]
    public void LinesKeepTheirKeysAndPersonasAndLoseTheCamera() {
        var quest = Load(LostLieutenant);
        var source = ((ActorDialogList) quest.m_dialogList).m_dialogs.Single(d => d.m_dialogTag == "Prep").m_dialogEntries
            .Cast<NPCDialogEntry>().ToList();
        var sent = QuestDialogReview.Dialog(quest, new QuestInstance(quest, 7), s_all).m_dialogEntries.Cast<NPCDialogEntry>().ToList();
        Assert.Equal(source.Count, sent.Count);
        for (var i = 0; i < sent.Count; i++) {
            Assert.Equal(source[i].m_dialog, sent[i].m_dialog);
            Assert.Equal(source[i].m_personaName, sent[i].m_personaName);
            Assert.Equal(source[i].m_nameSTKey, sent[i].m_nameSTKey);
            Assert.Equal(source[i].m_picture, sent[i].m_picture);
            Assert.Equal(source[i].m_soundFile, sent[i].m_soundFile);
            Assert.Equal("", sent[i].m_cameraName);
            Assert.Equal("", sent[i].m_dialogEvent);
            Assert.True(sent[i].m_bypassCameraOnReview);
            Assert.True(sent[i].m_meetsRequirements); // the client drops lines without it
        }
    }

    [Fact]
    public void ALineWhoseRequirementsFailIsLeftOut() {
        var gated = new RequirementList { m_requirements = [new ReqHasEntry { m_entryName = "X" }] };
        var quest = new QuestTemplate {
            m_questName = "TEST-REQ",
            m_goals = [],
            m_startGoals = [],
            m_dialogList = new ActorDialogList {
                m_dialogs = [new ActorDialog {
                    m_dialogTag = "Prep",
                    m_dialogEntries = [
                        new NPCDialogEntry { m_dialog = "everyone" },
                        new NPCDialogEntry { m_dialog = "gated", m_requirements = gated },
                    ],
                }],
            },
        };
        var instance = new QuestInstance(quest, 7);
        Assert.Equal(["everyone", "gated"], Lines(QuestDialogReview.Dialog(quest, instance, _ => true)));
        Assert.Equal(["everyone"], Lines(QuestDialogReview.Dialog(quest, instance, _ => false)));
        Assert.Equal(["everyone"], Lines(QuestDialogReview.Dialog(quest, instance, _ => throw new InvalidOperationException())));
    }

    [Fact]
    public void TheMessageRoundTripsUnderTheQuestsNameId() {
        var quest = Load(LostLieutenant);
        var message = QuestDialogReview.Message(quest, HeldAt(quest, 2), s_all);
        Assert.NotNull(message);
        Assert.Equal(StringHash.Compute(LostLieutenant), message!.QuestNameID);
        Assert.True(new ObjectSerializer(Versionable: false).Deserialize<ActorDialog>(message.ActorDialog, 16, out var dialog));
        Assert.Equal(Lines(QuestDialogReview.Dialog(quest, HeldAt(quest, 2), s_all)), Lines(dialog!));
    }

    [Fact]
    public void ALongDialogueIsCapped() {
        var quest = new QuestTemplate {
            m_questName = "TEST-LONG",
            m_goals = [],
            m_startGoals = [],
            m_dialogList = new ActorDialogList {
                m_dialogs = [new ActorDialog {
                    m_dialogTag = "Prep",
                    m_dialogEntries = [.. Enumerable.Range(0, 1000).Select(i => (ActorDialogEntry) new NPCDialogEntry { m_dialog = $"l{i}" })],
                }],
            },
        };
        Assert.Equal(QuestDialogReview.MaxEntries, QuestDialogReview.Dialog(quest, new QuestInstance(quest, 7), s_all).m_dialogEntries.Count);
    }

    // --- whose quests: only held ones ---

    [Fact]
    public void OnlyAHeldQuestIsFound() {
        var quest = Load(LostLieutenant);
        var held = new QuestInstance(quest, 7);
        var wizard = Holding(held);
        Assert.Same(held, QuestDialogReview.HeldQuest(wizard, StringHash.Compute(LostLieutenant)));

        Assert.Null(QuestDialogReview.HeldQuest(wizard, StringHash.Compute("WC-TUT-C01-001"))); // a real quest they do not hold
        Assert.Null(QuestDialogReview.HeldQuest(wizard, 0));
        Assert.Null(QuestDialogReview.HeldQuest(wizard, 0xDEADBEEF));
        Assert.Null(QuestDialogReview.HeldQuest(wizard, TowerGuide.QuestNameId)); // the guide is answered by the guide
        Assert.Null(QuestDialogReview.HeldQuest(Holding(), StringHash.Compute(LostLieutenant)));
        Assert.Null(QuestDialogReview.HeldQuest(null, StringHash.Compute(LostLieutenant)));
        Assert.Null(QuestDialogReview.HeldQuest(new Wizard { CharId = 7 }, StringHash.Compute(LostLieutenant)));
    }

    [Fact]
    public void AFinishedQuestIsNotFound() {
        var quest = Load(LostLieutenant);
        var wizard = Holding(new QuestInstance(quest, 7));
        Assert.True(wizard.QuestBehavior.CompleteQuest(LostLieutenant)); // what Wizard.CompleteQuest does, without its save
        Assert.Null(QuestDialogReview.HeldQuest(wizard, StringHash.Compute(LostLieutenant)));
    }

    // --- read only ---

    [Fact]
    public void ReviewingChangesNothing() {
        var quest = Load(LostLieutenant);
        var instance = HeldAt(quest, 2);
        var wizard = Holding(instance);
        var templateBefore = JsonConvert.SerializeObject(quest, s_json);
        var progressBefore = instance.GoalProgress.Select(g => (g.ID, g.GoalName, g.CurrentProgress)).ToList();
        var registryBefore = wizard.QuestBehavior.Registry.ToDictionary();

        for (var i = 0; i < 3; i++) {
            Assert.NotNull(QuestDialogReview.Message(quest, QuestDialogReview.HeldQuest(wizard, StringHash.Compute(LostLieutenant))!, s_all));
        }

        Assert.Equal(templateBefore, JsonConvert.SerializeObject(quest, s_json));
        Assert.Equal(progressBefore, instance.GoalProgress.Select(g => (g.ID, g.GoalName, g.CurrentProgress)));
        Assert.Equal(registryBefore, wizard.QuestBehavior.Registry.ToDictionary());
        Assert.Equal([instance], wizard.QuestBehavior.CurrentQuestInstances);
    }

    // --- rate limit ---

    [Fact]
    public void AFloodOfRequestsIsDropped() {
        var limiter = QuestDialogReview.NewRequestLimiter();
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(QuestDialogReview.RequestBurst, Enumerable.Range(0, 100).Count(_ => limiter.TryTake(now)));
        Assert.False(limiter.TryTake(now.AddMilliseconds(500)));
        Assert.True(limiter.TryTake(now.AddSeconds(1.01)));
    }

    // --- the Briskbreeze guide keeps its own answer ---

    [Fact]
    public void TheGuidesIdIsNoQuestsName() {
        Assert.NotEqual(StringHash.Compute(LostLieutenant), TowerGuide.QuestNameId);
        var all = Directory.GetFiles(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates"), "*.json")
            .Select(Path.GetFileNameWithoutExtension);
        Assert.DoesNotContain(all, name => StringHash.Compute(name!) == TowerGuide.QuestNameId);
    }

}
