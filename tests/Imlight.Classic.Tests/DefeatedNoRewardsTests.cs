// CLASSIC: owner ruling 2026-10-10, "take the rewards away": a wizard defeated during a fight their side still wins
// gets no rewards and no quest kill credit from it. CombatService (XP, gold, Crowns, drops, reagents, Treasure Cards,
// badges, Second Chance) and QuestService (defeat goals) hand all of those out only on MSG_COMBATWIN, so the duel
// withholding that message from a defeated wizard withholds every reward. Drives a real PvE duel ending, no database.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Settings;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class DefeatedNoRewardsTests : IDisposable {
    private const uint MobTid = uint.MaxValue - 2310;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ActorSystem _system;
    private readonly IActorRef _host, _zone, _mob, _fallen, _standing;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly CoreTemplate? _previous;
    private readonly ConcurrentDictionary<string, string> _overrides;
    private readonly string? _previousSetting;

    public DefeatedNoRewardsTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var fixture = Path.Combine(Path.GetTempPath(), "imlight-defeat-rewards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var config = Path.Combine(fixture, "settings.ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(fixture, "server.log")}\n");
        ConfigurationManager.Initialize(config);
        _overrides = (ConcurrentDictionary<string, string>) typeof(ClassicSettingsStore)
            .GetField("_overrides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ClassicSettings.Store)!;
        _overrides.TryGetValue(ClassicSettingKeys.DefeatedGetNoRewards, out _previousSetting);
        _overrides.TryRemove(ClassicSettingKeys.DefeatedGetNoRewards, out _);
        _cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.TryGetValue(MobTid, out _previous);
        _cache[MobTid] = new GameObjectTemplate {
            m_templateID = MobTid, m_objectName = "Defeat rewards mob fixture", m_adjectiveList = ["Undead"],
            m_behaviors = [new NPCBehaviorTemplate {
                m_behaviorName = "NPCBehavior", m_schoolOfFocus = "Death", m_nLevel = 1,
                m_nStartingHealth = 100, m_baseEffects = [],
            }],
        };
        _system = ActorSystem.Create("defeat-rewards-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        _zone = _system.ActorOf(Props.Create(() => new Recorder()));
        _mob = _system.ActorOf(Props.Create(() => new Recorder()));
        _fallen = _system.ActorOf(Props.Create(() => new Recorder()));
        _standing = _system.ActorOf(Props.Create(() => new Recorder()));
        _host = _system.ActorOf(Props.Create(() => new DuelHost(_zone)));
    }

    public void Dispose() {
        _system.Terminate().GetAwaiter().GetResult();
        if (_previous is not null) _cache[MobTid] = _previous;
        else _cache.Remove(MobTid);
        if (_previousSetting is not null) _overrides[ClassicSettingKeys.DefeatedGetNoRewards] = _previousSetting;
        else _overrides.TryRemove(ClassicSettingKeys.DefeatedGetNoRewards, out _);
        ClassicRuntime.ResetForTests();
    }

    [Fact]
    public void TheSwitchIsOnByDefault() {
        var definition = ClassicSettingsStore.Find(ClassicSettingKeys.DefeatedGetNoRewards);
        Assert.NotNull(definition);
        Assert.Equal("true", definition!.Default);
        Assert.True(new ClassicSettingsStore(_ => null, null).Bool(ClassicSettingKeys.DefeatedGetNoRewards));
        Assert.True(ClassicSettings.DefeatedGetNoRewards);
    }

    [Fact]
    public async Task ATwoPlayerWinGivesTheDefeatedWizardNothingAndTheSurvivorEverything() {
        await Run(duel => {
            var mob = Occupy(duel, 0, wizard: false, _mob);
            var fallen = Occupy(duel, 4, wizard: true, _fallen);
            var standing = Occupy(duel, 5, wizard: true, _standing);
            fallen._usedPipsForExperienceGain = 6;
            standing._usedPipsForExperienceGain = 9;
            fallen.ParticipantGameStats.m_currentHitpoints = 0;
            mob.ParticipantGameStats.m_currentHitpoints = 0;
            Resolve(duel);
            Assert.Equal(kDuelPhase.kPhase_Ended, duel.Duel.m_duelPhase);
            return true;
        });

        // The defeated wizard goes home defeated and gets no victory: no XP, gold, Crowns, drops or kill credit.
        var fallenMessages = await Messages(_fallen);
        Assert.Single(fallenMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT>());
        Assert.Empty(fallenMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());

        // The survivor gets the victory as before, carrying its pips (XP) and the defeated mobs (drops, quest goals).
        var standingMessages = await Messages(_standing);
        Assert.Empty(standingMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT>());
        var win = Assert.Single(standingMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
        Assert.Equal(9, win.UsedPips);
        Assert.Equal([(ulong) MobTid], win.MobTemplateIds);
        Assert.Contains("Undead", win.MobAdjectives);

        var result = Assert.Single((await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>()
            .Select(m => m.Message).OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Equal((byte) CombatTeam.Player, result.WinningTeam);
    }

    [Fact]
    public async Task AWizardHealedBackUpBeforeTheEndCountsAsStanding() {
        await Run(duel => {
            var mob = Occupy(duel, 0, wizard: false, _mob);
            var revived = Occupy(duel, 4, wizard: true, _fallen);
            Occupy(duel, 5, wizard: true, _standing);
            revived.ParticipantGameStats.m_currentHitpoints = 0;
            CombatEffectApplicator.HealParticipantBounded(revived, 400); // a direct heal revives (March 2009)
            Assert.True(revived.IsAlive);
            mob.ParticipantGameStats.m_currentHitpoints = 0;
            Resolve(duel);
            return true;
        });

        var revivedMessages = await Messages(_fallen);
        Assert.Empty(revivedMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT>());
        Assert.Single(revivedMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
        Assert.Single((await Messages(_standing)).OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
    }

    [Fact]
    public async Task ASoloLossStaysAsBefore() {
        await Run(duel => {
            Occupy(duel, 0, wizard: false, _mob);
            var solo = Occupy(duel, 4, wizard: true, _fallen);
            solo.ParticipantGameStats.m_currentHitpoints = 0;
            Resolve(duel);
            Assert.Equal(kDuelPhase.kPhase_Ended, duel.Duel.m_duelPhase);
            return true;
        });

        var messages = await Messages(_fallen);
        Assert.NotEmpty(messages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT>());
        Assert.Empty(messages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
        var result = Assert.Single((await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>()
            .Select(m => m.Message).OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Equal((byte) CombatTeam.Monster, result.WinningTeam);
        // The winning mob stays in the world at full health (classic reset), not deleted.
        Assert.Single((await Messages(_mob)).OfType<CLASSIC_FEATURES_PROTOCOL.MSG_COMBATRESET>());
    }

    [Fact]
    public async Task WithTheSwitchOffTheDefeatedWizardIsRewardedAsBefore() {
        _overrides[ClassicSettingKeys.DefeatedGetNoRewards] = "false";
        await Run(duel => {
            var mob = Occupy(duel, 0, wizard: false, _mob);
            var fallen = Occupy(duel, 4, wizard: true, _fallen);
            Occupy(duel, 5, wizard: true, _standing);
            fallen.ParticipantGameStats.m_currentHitpoints = 0;
            mob.ParticipantGameStats.m_currentHitpoints = 0;
            Resolve(duel);
            return true;
        });

        var fallenMessages = await Messages(_fallen);
        Assert.Single(fallenMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT>());
        Assert.Single(fallenMessages.OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
        Assert.Single((await Messages(_standing)).OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
    }

    [Fact]
    public async Task ADefeatedHenchmanOrMinionDoesNotChangeItsOwnersRewards() {
        await Run(duel => {
            var mob = Occupy(duel, 0, wizard: false, _mob);
            var owner = Occupy(duel, 4, wizard: true, _standing);
            var minion = Occupy(duel, 5, wizard: false, _zone, owner);
            minion.ParticipantGameStats.m_currentHitpoints = 0;
            mob.ParticipantGameStats.m_currentHitpoints = 0;
            Resolve(duel);
            return true;
        });

        Assert.Single((await Messages(_standing)).OfType<COMBAT_106_PROTOCOL.MSG_COMBATWIN>());
    }

    private static void Resolve(CombatDuelComponent duel)
        => CombatRegressionTests.Invoke(duel, "ReceiveRoundResolution", new COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION());

    private Task<object> Run(Func<CombatDuelComponent, object> action)
        => _host.Ask<object>(new RunDuel(action), Timeout, TestContext.Current.CancellationToken);
    private static Task<object[]> Messages(IActorRef actor)
        => actor.Ask<object[]>(new GetMessages(), Timeout, TestContext.Current.CancellationToken);
    private static void SetField(CombatDuelComponent duel, string name, object value)
        => typeof(CombatDuelComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, value);

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool wizard, IActorRef actor,
        CombatDuelSubCircle? owner = null) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject {
            m_templateID = wizard ? 1UL : MobTid, m_globalID = (ulong) (slot + 100),
        });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", actor);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_hangingEffects = [], m_pipCount = new PipCount(), m_subcircle = slot,
            m_isMinion = owner is not null, m_minionSubCircle = owner?.SlotIndex ?? 0,
        });
        CombatRegressionTests.SetProperty(circle, "IsSummonedMinion", owner is not null);
        if (owner is not null) circle.CaptureMinionOwner(owner.SlotIndex);
        circle.AddedToDuel = true;
        return circle;
    }

    private sealed record RunDuel(Func<CombatDuelComponent, object> Action);
    private sealed record GetMessages;
    private sealed class Recorder : ReceiveActor {
        private readonly List<object> _messages = [];
        public Recorder() {
            Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray()));
            ReceiveAny(message => _messages.Add(message));
        }
    }
    private sealed class DuelHost : ZoneEntity {
        private readonly CombatDuelComponent _duel;
        public DuelHost(IActorRef zone)
            : base(new CoreObject { m_globalID = 998 }, new GameObjectTemplate { m_behaviors = [] },
                new CoreObjectInfo(), zone, null!) {
            _duel = new CombatDuelComponent(this);
            _duel.AttachTo(Self);
            _duel.Timers = DispatchProxy.Create<ITimerScheduler, ArenaMinionTimers>();
            CombatRegressionTests.SetProperty(_duel, "Duel", new Duel {
                m_duelID = 998, m_bPVP = false, m_firstTeamToAct = (int) CombatTeam.Player,
                m_duelPhase = kDuelPhase.kPhase_Execution, m_flatParticipantList = [],
                m_duelModifier = new DuelModifier { m_battlefieldEffects = [] },
            });
            CombatRegressionTests.SetProperty(_duel, "SubCircles", Enumerable.Range(0, 8).Select(slot =>
                new CombatDuelSubCircle(_duel, 0, 0, default, slot) {
                    SlotType = slot < 4 ? CombatSlotType.Creature : CombatSlotType.Player,
                }).ToArray());
            SetField(_duel, "_pvp", false);
            SetField(_duel, "_isActive", true);
            SetField(_duel, "_tutorialDirector", new TutorialDuelDirector(_duel, ""));
            SetField(_duel, "_combatSigilObjectInfo", new CombatSigilObjectInfo { m_zoneTag = "Defeat rewards fixture" });
        }
        protected override void ConfigureReceivers() {
            Receive<RunDuel>(request => {
                try { Sender.Tell(request.Action(_duel)); }
                catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            base.ConfigureReceivers();
        }
        protected override void PostStop() {
            ActiveDuels.Remove(998);
            base.PostStop();
        }
    }
}
