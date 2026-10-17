using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Xunit;

namespace Imlight.Classic.Tests;

// REA audit 2026-10-17: hidden quests and finished tutorial tips are kept in the strings the client writes
// (Classic/ClientJournal.cs) and go back into the player object at login.
public sealed class ClientJournalTests {

    private static bool Handles(System.Type message)
        => typeof(QuestService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(m => m.GetCustomAttributes<MessageHandlerAttribute>().Any(a => a.MessageType == message));

    [Fact]
    public void TheJournalMessagesHaveHandlers() {
        Assert.True(Handles(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS)));
        Assert.True(Handles(typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP)));
        Assert.True(Handles(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG)));
        Assert.True(Handles(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG)));
    }

    [Fact]
    public void OnlyAPlainStringOfBoundedLengthIsKept() {
        Assert.Equal("123,456", ClientJournalStore.Accept("123,456"));
        Assert.Equal("", ClientJournalStore.Accept(""));
        Assert.Null(ClientJournalStore.Accept(null));
        Assert.Null(ClientJournalStore.Accept(new string('1', ClientJournalStore.MaxLength + 1)));
        Assert.Null(ClientJournalStore.Accept("1\02"));
        Assert.Equal("ClientJournals/42", ClientJournalStore.IdFor(42));
    }

    [Fact]
    public void TheLoaderPutsTheSavedStringsIntoTheBehaviors() {
        var hidden = new HiddenQuestsBehavior();
        var tips = new TutorialLogBehavior();
        var player = new WizClientObject { m_inactiveBehaviors = new List<BehaviorInstance> { hidden, tips } };
        WizardObjectLoader.SetJournalBehaviors(player, new ClientJournal { CharId = 7, HiddenQuests = "h", TutorialTips = "t" });

        Assert.Equal("h", hidden.m_hiddenQuestsData);
        Assert.Equal("t", tips.m_tutorialTipData);
    }

    [Fact]
    public void AWizardWithoutAJournalKeepsTheTemplateDefaults() {
        var hidden = new HiddenQuestsBehavior { m_hiddenQuestsData = "template" };
        var player = new WizClientObject { m_inactiveBehaviors = new List<BehaviorInstance> { hidden } };

        WizardObjectLoader.SetJournalBehaviors(player, null);

        Assert.Equal("template", hidden.m_hiddenQuestsData);
    }

}
