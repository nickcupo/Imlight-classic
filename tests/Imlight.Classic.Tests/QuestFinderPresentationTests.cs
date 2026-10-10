// CLASSIC: Quest Finder hides through its native gate, without changing saved options or accepted quest navigation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class QuestFinderPresentationTests : IDisposable {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo LevelConfig = typeof(MagicLevelsConfig).GetField("s_playerLevelConfig", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? _previousLevels;

    public QuestFinderPresentationTests() {
        var config = Path.Combine(Path.GetTempPath(), "w101c-finder-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-finder-tests.log")}\n[Character]\nBaseGoldPouch=1000\nMaxLevel=50\n");
        Imlight.Common.ConfigurationManager.Initialize(config); // Retain authored fixtures; never delete.
        ClassicRuntime.ResetForTests();
        _previousLevels = LevelConfig.GetValue(null);
        LevelConfig.SetValue(null, new Dictionary<string, List<MagicLevelInfo>> {
            [MagicSchool.Fire.ToString()] = [new(), new() { m_hitpoints = 115, m_mana = 30 }],
        });
    }

    public void Dispose() {
        LevelConfig.SetValue(null, _previousLevels);
        ClassicRuntime.ResetForTests();
    }

    [Theory]
    [InlineData("october-2010-arc1", true, false)]
    [InlineData("october-2010-arc1", false, false)]
    [InlineData("late-2009", true, true)]
    [InlineData("late-2009", false, false)]
    [InlineData("arc1-2009h1", true, true)]
    [InlineData("arc1-2009h1", false, false)]
    [InlineData("dev-unrestricted", true, true)]
    [InlineData("dev-unrestricted", false, false)]
    [InlineData("stock", true, true)]
    [InlineData("stock", false, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void AttachPresentationAndNativeEncodingRespectTheProfileWithoutChangingStoredPreference(
        string? profile, bool savedEnabled, bool expectedEnabled) {
        Initialize(profile);
        var wizard = Wizard(savedEnabled);
        var persistedBefore = JsonConvert.SerializeObject(wizard.GameStats);
        var client = new WizClientObject();
        WizardObjectLoader.SetWizardGameStats(client, ref wizard); // Production attach presentation path.
        Assert.Equal(expectedEnabled, (bool)client.m_gameStats.m_questFinderEnabled);
        var serializer = new ClassicCoreObjectSerializer(false, SerializerFlags.None);
        Assert.True(serializer.Serialize(client.m_gameStats, 24, out var bytes));
        Assert.True(serializer.Deserialize<WizGameStats>((byte[])bytes, 24, out var decoded));
        Assert.Equal(expectedEnabled, (bool)decoded!.m_questFinderEnabled);
        Assert.Equal(71, decoded.m_currentHitpoints);
        Assert.Equal(23, decoded.m_currentMana);
        Assert.True(decoded.m_showItemLock);
        Assert.Equal(savedEnabled, wizard.GameStats.m_questFinderEnabled);
        Assert.Equal(persistedBefore, JsonConvert.SerializeObject(wizard.GameStats));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OctoberFinderMaskKeepsTheProductionHeldGoalDestinationAndHelperFlag(bool savedEnabled) {
        Initialize("october-2010-arc1");
        var wizard = Wizard(savedEnabled);
        var client = new WizClientObject();
        WizardObjectLoader.SetWizardGameStats(client, ref wizard);
        Assert.False(client.m_gameStats.m_questFinderEnabled);
        var goal = new GoalTemplate {
            m_goalName = "QA-Held-Goal", m_goalNameID = 782092,
            m_goalTitle = "QA-Held-Goal-Title", m_locationName = "QA-Held-Location",
            m_goalType = GOAL_TYPE.GOAL_TYPE_WAYPOINT, m_noQuestHelper = false,
            m_destinationZone = "WizardCity/WC_Z01", m_displayImage1 = "", m_displayImage2 = "",
        };
        var instance = new GoalInstance(goal, wizard.CharId) { ID = 782093 }; instance.BeginGoal();
        var quest = new QuestInstance { ID = 782094, OwnerCharId = wizard.CharId,
            QuestName = "QA-Held-Quest", GoalProgress = [instance] };
        using var system = ActorSystem.Create("finder-held-goal-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        try {
            var actor = system.ActorOf(Props.Create(() => new HeldGoalProbe(wizard)));
            var packet = await actor.Ask<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL>(new PrepareHeldGoal(goal, quest),
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, packet.NoQuestHelper);
            Assert.Equal((string)goal.m_destinationZone, (string)packet.GoalDestinationZone);
            Assert.Equal(quest.ID, packet.QuestID);
            Assert.Equal(instance.ID, packet.GoalID);
            Assert.Equal(0, packet.SendType); // Existing held-goal resume shape.
            Assert.Equal(savedEnabled, wizard.GameStats.m_questFinderEnabled);
            Assert.False(goal.m_noQuestHelper);
            Assert.Same(instance, Assert.Single(quest.GoalProgress));
        }
        finally { await system.Terminate(); }
    }

    private static void Initialize(string? profile) {
        if (profile is not null) ClassicRuntime.Initialize(profile == "stock" ? ClassicRules.Stock : ClassicDataFixture.RealRules(profile));
    }

    private static Wizard Wizard(bool enabled) => new() {
        CharId = 782091, Zone = "WizardCity/WC_Hub",
        GameStats = new ServerWizGameStats(MagicSchool.Fire, 1) {
            m_questFinderEnabled = enabled, m_showItemLock = true,
            m_currentHitpoints = 71, m_currentMana = 23,
        },
    };

    private sealed record PrepareHeldGoal(GoalTemplate Goal, QuestInstance Quest);
    private sealed class HeldGoalProbe : QuestService {
        public HeldGoalProbe(Wizard wizard) : base(null!) {
            typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(this, wizard);
            typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(this, new WizClientObject());
        }
        protected override void ConfigureReceivers() {
            Receive<PrepareHeldGoal>(request => {
                try {
                    var packet = typeof(QuestService).GetMethod("PrepareGoalMessage", Private)!
                        .Invoke(this, [request.Goal, request.Quest, (byte)0, true, true]);
                    Sender.Tell(packet!);
                }
                catch (Exception exception) { Sender.Tell(new Status.Failure(exception)); }
            });
            base.ConfigureReceivers();
        }
    }
}
