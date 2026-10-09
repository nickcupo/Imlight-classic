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
 * AMBIENT GROUP TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): grouping with ambient wizards (GroupManners,
 * GroupLines, AmbientGroups, AmbientCompanionGroup): the settings, which
 * lines are calls for company and what they are about, who answers how,
 * goodbyes and thanks, the group's size, the lines' tone, the registry
 * and its routing of duels and sigils, and the group actor taking and
 * refusing companions.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter AmbientGroupTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Math;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(AmbientGroupTests))]
public sealed class AmbientGroupTests {

    public AmbientGroupTests() {
        // The group actor logs; the logger needs a configuration (a quiet one).
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-ambient-group-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        }
        finally {
            File.Delete(config);
        }
    }

    // ---- settings -------------------------------------------------------------------------------

    [Fact]
    public void BlankSettingsGroupWithTheDefaults() {
        var settings = GroupSettings.Parse("", "", "", "", "");
        Assert.True(settings.Enabled);
        Assert.Equal(4, settings.MaxSize);
        Assert.Equal(0.5, settings.Willing);
        Assert.True(settings.Window);
        Assert.Equal(45, settings.StayMinutes);
    }

    [Fact]
    public void SettingsAreClampedAndCanTurnGroupingOff() {
        Assert.False(GroupSettings.Parse("false", "", "", "", "").Enabled);
        Assert.False(GroupSettings.Parse("true", "4", "0", "", "").Enabled);
        var odd = GroupSettings.Parse("true", "9", "1.5", "false", "1");
        Assert.Equal(4, odd.MaxSize);
        Assert.Equal(1, odd.Willing);
        Assert.False(odd.Window);
        Assert.Equal(10, odd.StayMinutes);
        Assert.Equal(2, GroupSettings.Parse("", "1", "", "", "").MaxSize);
        Assert.Equal(GroupSettings.DefaultSize, GroupSettings.Parse("", "lots", "x", "maybe", "y").MaxSize);
    }

    // ---- calls ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("anyone want to do Jotun?", "jotun")]
    [InlineData("need help with Kraken", "kraken")]
    [InlineData("LF group for Hall of Kings", "hall of kings")]
    [InlineData("lfg hall of kings", null)]
    [InlineData("wanna group?", null)]
    [InlineData("group?", null)]
    [InlineData("anyone wanna come with me to do the jotun quest", "jotun")]
    [InlineData("can someone help me with sneak attack", "sneak attack")]
    [InlineData("who wants to quest", null)]
    [InlineData("help pls", null)]
    [InlineData("Would you like to join my group?", null)]
    [InlineData("Let's go defeat a boss", null)]
    [InlineData("anyone help me beat Lord Nightshade!", "lord nightshade")]
    public void CallsForCompanyAreHeard(string text, string target) {
        var call = GroupManners.ParseCall(text);
        Assert.Equal(RecruitKind.Open, call.Kind);
        Assert.Equal(target, call.Target);
    }

    [Theory]
    [InlineData("hi everyone")]
    [InlineData("where is the bazaar")]
    [InlineData("can anyone help me find the bazaar")]
    [InlineData("i want to go home")]
    [InlineData("anyone wanna duel?")]
    [InlineData("anyone want to trade?")]
    [InlineData("no help needed, i'll solo it")]
    [InlineData("nice hat")]
    [InlineData("lol")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherLinesAreNotCalls(string text) => Assert.Equal(RecruitKind.None, GroupManners.ParseCall(text).Kind);

    [Fact]
    public void ANameOrAWhisperAsksThatWizard() {
        Assert.Equal(RecruitKind.Named, GroupManners.ParseCall("Tom, wanna group?", "Tom").Kind);
        Assert.Equal(RecruitKind.Open, GroupManners.ParseCall("Tommy wanna group?", "Tom").Kind);
        Assert.Equal(RecruitKind.Named, GroupManners.ParseCall("wanna group?", "Tom", whisper: true).Kind);
        Assert.Equal(RecruitKind.None, GroupManners.ParseCall("hi tom", "Tom", whisper: true).Kind);
    }

    [Theory]
    [InlineData("bye", DismissKind.Bye)]
    [InlineData("ok cya guys", DismissKind.Bye)]
    [InlineData("gtg", DismissKind.Bye)]
    [InlineData("ty bye", DismissKind.Bye)]
    [InlineData("you can go now", DismissKind.Bye)]
    [InlineData("thanks!", DismissKind.Thanks)]
    [InlineData("ty for the help", DismissKind.Thanks)]
    [InlineData("thank you so much", DismissKind.Thanks)]
    [InlineData("nice one", DismissKind.None)]
    [InlineData("type faster lol", DismissKind.None)]
    [InlineData("", DismissKind.None)]
    public void GoodbyesAndThanksAreHeard(string text, DismissKind kind) => Assert.Equal(kind, GroupManners.ParseDismiss(text));

    [Fact]
    public void ThanksEndsTheGroupOnlyOutOfAFightAfterAWhile() {
        Assert.False(GroupManners.ThanksEnds(inDuel: true, TimeSpan.FromMinutes(30)));
        Assert.False(GroupManners.ThanksEnds(inDuel: false, TimeSpan.FromMinutes(1)));
        Assert.True(GroupManners.ThanksEnds(inDuel: false, GroupManners.ThanksAfter));
    }

    // ---- answers --------------------------------------------------------------------------------

    private static RecruitFacts Facts(int helper = 20, int player = 20, int target = 0, bool direct = false, bool invite = false,
                                      AmbientTemper temper = AmbientTemper.Friendly, int open = 3, bool schoolTaken = false,
                                      bool healer = false, bool hasHealer = false)
        => new(helper, player, target, direct, invite, temper, schoolTaken, healer, hasHealer, open);

    [Fact]
    public void AGroupHoldsFourRealPlayersFirst() {
        Assert.Equal(3, GroupManners.OpenSlots(1, 0, 4));
        Assert.Equal(1, GroupManners.OpenSlots(1, 2, 4));
        Assert.Equal(0, GroupManners.OpenSlots(1, 3, 4));
        Assert.Equal(0, GroupManners.OpenSlots(2, 2, 4));
        Assert.Equal(1, GroupManners.OpenSlots(1, 0, 2));
        Assert.Equal(3, GroupManners.OpenSlots(1, 0, 9)); // never more than four
    }

    [Fact]
    public void AFullGroupGetsNoYes() {
        Assert.Equal(GroupAnswer.Ignore, GroupManners.Answer(Facts(open: 0), 1, 0));
        Assert.Equal(GroupAnswer.Full, GroupManners.Answer(Facts(open: 0, direct: true), 1, 0));
        Assert.Equal(0, GroupManners.YesChance(Facts(open: 0), 1));
    }

    [Fact]
    public void ALowLevelWizardSaysItIsTooLowOnlyWhenAsked() {
        Assert.Equal(GroupAnswer.Ignore, GroupManners.Answer(Facts(helper: 30, player: 40, target: 40), 1, 0));
        Assert.Equal(GroupAnswer.TooLow, GroupManners.Answer(Facts(helper: 30, player: 40, target: 40, direct: true), 1, 0));
        Assert.Equal(GroupAnswer.TooLow, GroupManners.Answer(Facts(helper: 12, player: 20, invite: true), 1, 0));
        Assert.NotEqual(GroupAnswer.TooLow, GroupManners.Answer(Facts(helper: 38, player: 40, target: 40, direct: true), 0.5, 0.01));
    }

    [Fact]
    public void YesesBrbsAndNos() {
        var facts = Facts();
        var p = GroupManners.YesChance(facts, 0.5);
        Assert.Equal(0.5, p, 3);
        Assert.Equal(GroupAnswer.Brb, GroupManners.Answer(facts, 0.5, 0.01));
        Assert.Equal(GroupAnswer.Yes, GroupManners.Answer(facts, 0.5, 0.3));
        Assert.Equal(GroupAnswer.Decline, GroupManners.Answer(facts, 0.5, 0.55)); // a few say no out loud...
        Assert.Equal(GroupAnswer.Ignore, GroupManners.Answer(facts, 0.5, 0.9));   // ...most let an open call pass
        Assert.Equal(GroupAnswer.Decline, GroupManners.Answer(Facts(direct: true), 0.5, 0.8)); // asked by name: it answers
        Assert.Equal(GroupAnswer.Done, GroupManners.Answer(Facts(helper: 50, player: 45, target: 40, direct: true), 0.5, 0.6));
    }

    [Fact]
    public void DirectAsksTempersAndHealersMoveTheChance() {
        var open = GroupManners.YesChance(Facts(), 0.5);
        Assert.True(GroupManners.YesChance(Facts(direct: true), 0.5) > open);
        Assert.True(GroupManners.YesChance(Facts(invite: true), 0.5) > GroupManners.YesChance(Facts(direct: true), 0.5));
        Assert.True(GroupManners.YesChance(Facts(temper: AmbientTemper.Quiet), 0.5) < open);
        Assert.True(GroupManners.YesChance(Facts(temper: AmbientTemper.Chatty), 0.5) > open);
        Assert.True(GroupManners.YesChance(Facts(schoolTaken: true), 0.5) < open);
        Assert.True(GroupManners.YesChance(Facts(healer: true), 0.5) > open);
        Assert.Equal(open, GroupManners.YesChance(Facts(healer: true, hasHealer: true), 0.5), 6);
        Assert.True(GroupManners.YesChance(Facts(helper: 45, player: 10), 0.5) < open); // a high level walking a low one through
        Assert.True(GroupManners.YesChance(Facts(invite: true, temper: AmbientTemper.Chatty), 1) <= 0.95);
    }

    [Fact]
    public void AboutHalfOfAStreetLetsAnOpenCallPass() {
        var rng = new Random(7);
        var answers = Enumerable.Range(0, 4000).Select(_ => GroupManners.Answer(Facts(), 0.5, rng.NextDouble())).ToList();
        var yes = answers.Count(a => a is GroupAnswer.Yes or GroupAnswer.Brb) / 4000.0;
        var ignored = answers.Count(a => a == GroupAnswer.Ignore) / 4000.0;
        Assert.InRange(yes, 0.45, 0.55);
        Assert.InRange(ignored, 0.35, 0.45);
        Assert.Contains(GroupAnswer.Brb, answers);
        Assert.Contains(GroupAnswer.Decline, answers);
    }

    [Fact]
    public void ACompanionStaysItsOwnWhile() {
        var stays = Enumerable.Range(0, 500).Select(seed => GroupManners.StayFor(seed * 7919, 45)).ToList();
        Assert.All(stays, s => Assert.InRange(s.TotalMinutes, 27, 72));
        Assert.True(stays.Distinct().Count() > 100);
        Assert.Equal(GroupManners.StayFor(42, 45), GroupManners.StayFor(42, 45));
    }

    [Theory]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", true)]
    [InlineData("Grizzleheim/GH_AbandCity/GH_HallofKings", true)]
    [InlineData("Housing/WizardCity/WC_Dorm_Room", false)]
    [InlineData("WizardCity/Tutorial", false)]
    [InlineData("", false)]
    public void CompanionsDoNotFollowIntoHouses(string zone, bool followable) => Assert.Equal(followable, GroupManners.Followable(zone));

    [Fact]
    public void SchoolsMapToTheClientsGroupWindowIcons() {
        Assert.Equal(1u, GroupManners.SocialSchool(AmbientSchool.Balance));
        Assert.Equal(3u, GroupManners.SocialSchool(AmbientSchool.Fire));
        Assert.Equal(5u, GroupManners.SocialSchool(AmbientSchool.Life));
        Assert.Equal(7u, GroupManners.SocialSchool(AmbientSchool.Storm));
        Assert.Equal(7, Enum.GetValues<AmbientSchool>().Select(GroupManners.SocialSchool).Distinct().Count());
    }

    // ---- lines ----------------------------------------------------------------------------------

    public static IEnumerable<object[]> Pools() => [
        [GroupLines.Yes], [GroupLines.Brb], [GroupLines.Back], [GroupLines.Decline], [GroupLines.Done], [GroupLines.TooLow],
        [GroupLines.Full], [GroupLines.ByeBack], [GroupLines.ThanksBack], [GroupLines.OwnLeave], [GroupLines.Defeated],
        [GroupLines.BackAfterDefeat], [GroupLines.AfkAsk], [GroupLines.AfkLeave], [GroupLines.NewZone], [GroupLines.WaitOutside],
        [GroupLines.WaitedTooLong], [GroupLines.AfterWin],
    ];

    [Theory]
    [MemberData(nameof(Pools))]
    public void GroupLinesAreShortCleanChat(System.Collections.Immutable.ImmutableArray<string> pool) {
        Assert.NotEmpty(pool);
        Assert.All(pool, line => Assert.True(AmbientChatBrain.IsClean(line), line));
        Assert.All(pool, line => Assert.True(line.Length <= 40, line));
    }

    [Fact]
    public void MenuChatWizardsUseTheClientsMenuPhrasesOrSayNothing() {
        var menu = Enumerable.Range(0, 400).Select(seed => ChatPersona.For(seed, AmbientTemper.Quiet)).First(p => p.Channel == ChatChannel.Menu);
        var typed = Enumerable.Range(0, 400).Select(seed => ChatPersona.For(seed, AmbientTemper.Chatty)).First(p => p.Channel != ChatChannel.Menu);
        Assert.Contains(GroupLines.For(GroupLines.Yes, menu, 3), GroupLines.MenuYes);
        Assert.Contains(GroupLines.For(GroupLines.Decline, menu, 3), GroupLines.MenuNo);
        Assert.Contains(GroupLines.For(GroupLines.Full, menu, 3), GroupLines.MenuInGroup);
        Assert.Contains(GroupLines.For(GroupLines.OwnLeave, menu, 3), GroupLines.MenuLeave);
        Assert.Null(GroupLines.For(GroupLines.NewZone, menu, 3));
        Assert.Contains(GroupLines.For(GroupLines.Yes, typed, 3), GroupLines.Yes);
    }

    // ---- registry and routing -------------------------------------------------------------------

    private sealed class Probe : ReceiveActor {
        public Probe(List<object> got) => ReceiveAny(m => {
            lock (got) {
                got.Add(m);
            }
        });
    }

    private static async Task<T> Next<T>(List<object> got, int timeoutMs = 5000) {
        for (var waited = 0; waited < timeoutMs; waited += 20) {
            lock (got) {
                if (got.OfType<T>().FirstOrDefault() is { } found) {
                    return found;
                }
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return default;
    }

    [Fact]
    public void TheRegistryKnowsWhoLeadsWhom() {
        AmbientGroups.ClearForTests();
        using var system = ActorSystem.Create("ambient-groups-registry", "akka.actor.provider = local");
        var one = system.ActorOf(Props.Create(() => new Probe(new List<object>())));
        var two = system.ActorOf(Props.Create(() => new Probe(new List<object>())));
        try {
            Assert.True(AmbientGroups.Register(0xBEEF01, one, 0xC4A1));
            Assert.True(AmbientGroups.Register(0xBEEF01, one, 0xC4A1)); // the same group again
            Assert.False(AmbientGroups.Register(0xBEEF01, two, 0xC4A2)); // a second group for one player
            Assert.True(AmbientGroups.HasGroup(0xBEEF01));
            Assert.Equal(0xBEEF01ul, AmbientGroups.LeaderOfChannel(0xC4A1));
            Assert.Equal(0ul, AmbientGroups.LeaderOfChannel(0xC4A2));

            AmbientGroups.SetMembers(0xBEEF01, [0xA3B1E0000000F001, 0xA3B1E0000000F002]);
            Assert.Equal(2, AmbientGroups.CompanionCount(0xBEEF01));
            Assert.Equal(0xBEEF01ul, AmbientGroups.LeaderOf(0xA3B1E0000000F002));
            AmbientGroups.SetMembers(0xBEEF01, [0xA3B1E0000000F001]);
            Assert.Equal(0ul, AmbientGroups.LeaderOf(0xA3B1E0000000F002));

            AmbientGroups.Unregister(0xBEEF01, one, 0xC4A1);
            Assert.False(AmbientGroups.HasGroup(0xBEEF01));
            Assert.Equal(0ul, AmbientGroups.LeaderOf(0xA3B1E0000000F001));
            Assert.Equal(0ul, AmbientGroups.LeaderOfChannel(0xC4A1));
        }
        finally {
            AmbientGroups.ClearForTests();
        }
    }

    [Fact]
    public async Task DuelsAndSigilsReachTheLeadersGroup() {
        AmbientGroups.ClearForTests();
        using var system = ActorSystem.Create("ambient-groups-routing", "akka.actor.provider = local");
        var got = new List<object>();
        var group = system.ActorOf(Props.Create(() => new Probe(got)));
        var ambient = new AmbientWizard(AmbientWizardRecord.From(AmbientIdentity.Generate(11, "WizardCity/WC_Hub",
            new NameTableSizes(100, 100, 50, 50), (1, 12)), 0xA3B1E0000000F011), new Wizard { CharId = 0xA3B1E0000000F011 }) {
            Endpoint = system.ActorOf(Props.Create(() => new Probe(new List<object>()))),
        };
        AmbientWizards.Register(ambient); // the sigil routing runs only while ambient wizards exist
        try {
            AmbientGroups.Register(0xBEEF02, group, 0xC4B1);
            AmbientWizards.NotifyDuel(null, new AmbientDuelNotice(77, new Vector3(1, 2, 3), [0xBEEF02], 3, true, false));
            AmbientWizards.NotifyDuel(null, new AmbientDuelNotice(78, new Vector3(1, 2, 3), [0xBEEF03], 3, true, false));
            var duel = await Next<AmbientDuelNotice>(got);
            Assert.Equal(77ul, duel.SigilId);

            var sigil = new SigilGroup(5, DateTime.UtcNow, 10);
            AmbientDungeons.NotifySigil(null, new AmbientSigilNotice(sigil, new Vector3(0, 0, 0), 0, 1, "", "Dungeon/Zone", "Start", 0xBEEF02, 20));
            var notice = await Next<AmbientSigilNotice>(got);
            Assert.Equal(0xBEEF02ul, notice.PlayerCharId);
            lock (got) {
                Assert.DoesNotContain(got.OfType<AmbientDuelNotice>(), n => n.SigilId == 78);
            }
        }
        finally {
            AmbientWizards.Unregister(ambient);
            AmbientGroups.ClearForTests();
        }
    }

    [Fact]
    public void ACallIsAboutWhatTheServerKnows() {
        AmbientGroups.SetTargetsForTests(new Dictionary<string, (int Level, string Zone)> {
            ["jotun"] = (40, "Grizzleheim/GH_AbandCity/GH_HallofKings"),
            ["hallofkings"] = (40, "Grizzleheim/GH_AbandCity/GH_HallofKings"),
            ["sneakattack"] = (32, "MooShu/MS_Hub"),
            ["gh"] = (12, "Grizzleheim/GH_MainHub"),
        });
        try {
            Assert.Equal(40, AmbientGroups.Target("Jotun").Level);
            Assert.Equal(40, AmbientGroups.Target("hall of kings").Level);
            Assert.Equal(40, AmbientGroups.Target("the hall of kings").Level);
            Assert.Equal(32, AmbientGroups.Target("sneak attack").Level);
            Assert.Equal(12, AmbientGroups.Target("gh").Level);
            Assert.Equal(40, AmbientGroups.Target("jotuns").Level); // close enough
            Assert.Null(AmbientGroups.Target("kraken"));
            Assert.Null(AmbientGroups.Target("x"));
        }
        finally {
            AmbientGroups.SetTargetsForTests(null);
        }
    }

    // ---- the group actor ------------------------------------------------------------------------

    private static AmbientWizard Companion(ActorSystem system, ulong charId, IActorRef home) {
        var record = AmbientWizardRecord.From(AmbientIdentity.Generate((int) (charId & 0xFFFF), "WizardCity/WC_Hub",
            new NameTableSizes(100, 100, 50, 50), (10, 20)), charId);
        var character = new Wizard {
            CharId = charId,
            PlayerNameBehavior = new Imlight.CoreLib.Shared.Behaviors.ServerWizPlayerNameBehavior { NameOverride = $"Test {charId & 0xFF}" },
        };
        var wizard = new AmbientWizard(record, character) {
            Endpoint = system.ActorOf(Props.Create(() => new Probe(new List<object>()))),
            Group = home,
            Present = false,
        };
        return wizard;
    }

    [Fact]
    public async Task TheGroupTakesCompanionsUpToItsSizeAndSendsTheRestHome() {
        AmbientGroups.ClearForTests();
        var before = AmbientGroups.Settings;
        AmbientGroups.Settings = GroupSettings.Parse("true", "2", "0.5", "false", "45"); // the player and one companion
        using var system = ActorSystem.Create("ambient-groups-actor", "akka.actor.provider = local");
        var homeGot = new List<object>();
        var home = system.ActorOf(Props.Create(() => new Probe(homeGot)));
        var group = system.ActorOf(AmbientCompanionGroup.Props(0xBEEF04, 0xC4C1, 0xC4C2, home));
        AmbientGroups.Register(0xBEEF04, group, 0xC4C1);
        var first = Companion(system, 0xA3B1E0000000F021, home);
        var second = Companion(system, 0xA3B1E0000000F022, home);
        try {
            group.Tell(new AmbientCompanionGroup.Take(first));
            group.Tell(new AmbientCompanionGroup.Take(second));
            var back = await Next<AmbientCompanionBack>(homeGot);
            Assert.NotNull(back);
            Assert.Same(second, back.Wizard); // full: the second goes back to its street
            Assert.Null(second.Driver);
            Assert.Equal(0xBEEF04ul, AmbientGroups.LeaderOf(first.CharId));
            Assert.Equal(0ul, AmbientGroups.LeaderOf(second.CharId));
            Assert.Equal(1, AmbientGroups.CompanionCount(0xBEEF04));
            Assert.Equal(AmbientActivity.Grouped, first.Activity);
            Assert.Equal(group, first.Driver);

            group.Tell(new AmbientCompanionGroup.LeaveGroup()); // the client's Leave Group: everyone goes
            for (var i = 0; i < 100 && AmbientGroups.HasGroup(0xBEEF04); i++) {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.False(AmbientGroups.HasGroup(0xBEEF04));
            Assert.Null(first.Driver);
            lock (homeGot) {
                Assert.Contains(homeGot.OfType<AmbientCompanionBack>(), b => b.Wizard == first);
            }
        }
        finally {
            AmbientGroups.Settings = before;
            AmbientGroups.ClearForTests();
        }
    }

}

[CollectionDefinition(nameof(AmbientGroupTests), DisableParallelization = true)]
public sealed class AmbientGroupTestsCollection;
