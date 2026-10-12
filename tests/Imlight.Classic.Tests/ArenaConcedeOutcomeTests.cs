// CLASSIC: native concede translation, started arena outcome delivery and personal flee standings.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaConcedeOutcomeTests : IDisposable {
    private const ulong Sigil = 996763;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly FieldInfo ArenaInstance = typeof(ArenaMatchmaker).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly ArenaMatchmaker? _previousArena;
    private readonly ActorSystem _system;
    private readonly IActorRef _host, _zone;
    private readonly IActorRef[] _players;
    private readonly World _world;

    public ArenaConcedeOutcomeTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var fixture = Path.Combine(Path.GetTempPath(), "imlight-arena-concede-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var settings = Path.Combine(fixture, "settings.ini");
        File.WriteAllText(settings, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(fixture, "server.log")}\n");
        ConfigurationManager.Initialize(settings);
        _system = ActorSystem.Create("arena-concede-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        _players = Enumerable.Range(0, 10).Select(_ => _system.ActorOf(Props.Create(() => new Recorder()))).ToArray();
        _zone = _system.ActorOf(Props.Create(() => new ZoneRecorder(_players)));
        _world = new World(_players);
        _previousArena = (ArenaMatchmaker?) ArenaInstance.GetValue(null);
        ArenaInstance.SetValue(null, _world.Arena);
        _host = _system.ActorOf(Props.Create(() => new DuelHost(_zone)));
    }

    public void Dispose() {
        _system.Terminate().GetAwaiter().GetResult();
        ArenaInstance.SetValue(null, _previousArena);
        ClassicRuntime.ResetForTests();
    }

    public static IEnumerable<object[]> Singles() {
        foreach (var kind in new[] { ArenaKind.Practice, ArenaKind.Ranked })
            foreach (var losingSide in new[] { 0, 1 })
                foreach (var ambientRole in new[] { "none", "conceder", "opponent" })
                    yield return [kind, losingSide, ambientRole];
    }

    [Theory]
    [MemberData(nameof(Singles))]
    public async Task StartedOneVersusOneConcedeAwardsPersonalLossAndOpposingWinOnce(
        ArenaKind kind, int losingSide, string ambientRole) {
        var loser = losingSide * 4;
        var opponent = (1 - losingSide) * 4;
        var run = await Start(kind, 1, ambientRole == "conceder" ? [loser] : ambientRole == "opponent" ? [opponent] : []);
        await Concede(loser);
        Assert.Equal(kDuelPhase.kPhase_Ended, await Phase());
        await AssertOutcomes(kind, [0, 4], 1 - losingSide, [loser]);
        await AssertOneSharedResult(1 - losingSide);
        await AssertNoSecondResult(run, [loser, opponent]);
    }

    public static IEnumerable<object[]> Teams() {
        foreach (var kind in new[] { ArenaKind.Practice, ArenaKind.Ranked })
            foreach (var size in new[] { 2, 3, 4 })
                foreach (var losingSide in new[] { 0, 1 }) yield return [kind, size, losingSide];
    }

    [Theory]
    [MemberData(nameof(Teams))]
    public async Task TeamConcedeRemovesOnlyThatWizardAndEndsWhenNoLivingWizardRemains(
        ArenaKind kind, int size, int losingSide) {
        var slots = Slots(size);
        // Both teams contain human and ambient wizards; all use the same actual combat move delivery.
        var run = await Start(kind, size, slots.Where(slot => slot % 2 == 1).ToArray());
        var losingSlots = Enumerable.Range(losingSide * 4, size).ToArray();
        foreach (var slot in losingSlots.SkipLast(1)) {
            await Concede(slot);
            Assert.Equal(kDuelPhase.kPhase_Planning, await Phase());
            Assert.Empty(_world.Outcomes);
            Assert.Equal(0, _world.LadderStore.Batches);
            var packets = await Messages(_players[slot]);
            Assert.False(Assert.Single(packets.OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>()).Won);
            Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE>());
            Assert.DoesNotContain(await NativeBroadcasts(), packet => packet is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT);
            foreach (var remaining in slots.Where(candidate => candidate != slot && !losingSlots.TakeWhile(left => left != slot).Contains(candidate))) {
                var continuing = await Messages(_players[remaining]);
                Assert.DoesNotContain(continuing, packet => packet is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT or DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE);
            }
        }
        await Concede(losingSlots[^1]);
        Assert.Equal(kDuelPhase.kPhase_Ended, await Phase());
        await AssertOutcomes(kind, slots, 1 - losingSide, losingSlots);
        await AssertOneSharedResult(1 - losingSide);
        await AssertNoSecondResult(run, slots);
    }

    [Theory]
    [InlineData(ArenaKind.Practice, 2)] [InlineData(ArenaKind.Ranked, 2)]
    [InlineData(ArenaKind.Practice, 3)] [InlineData(ArenaKind.Ranked, 3)]
    [InlineData(ArenaKind.Practice, 4)] [InlineData(ArenaKind.Ranked, 4)]
    public async Task ConcederStillLosesIfRemainingTeamLaterWins(ArenaKind kind, int size) {
        var slots = Slots(size);
        var run = await Start(kind, size, [0, 5]);
        await Concede(0);
        Assert.Equal(kDuelPhase.kPhase_Planning, await Phase());
        foreach (var slot in Enumerable.Range(4, size)) await Concede(slot);
        await AssertOutcomes(kind, slots, winningSide: 0, [0, .. Enumerable.Range(4, size)]);
        // The native actor result must distinguish the departed loser from winning teammates.
        var result = Result(_world.Outcomes[Id(0)]);
        var departed = ActorResult(result, 0);
        var teammate = ActorResult(result, 1);
        Assert.NotEqual(departed.m_gameResult, teammate.m_gameResult);
        await AssertNoSecondResult(run, slots);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task DeadTeammateAndLivingMinionDoNotPreventLastWizardConcedeLoss(int losingSide) {
        var losingSlot = losingSide * 4;
        var otherSlot = (1 - losingSide) * 4;
        var run = await Start(ArenaKind.Ranked, 2, [losingSlot, otherSlot + 1]);
        await Run(duel => {
            duel.SubCircles[losingSlot + 1].ParticipantGameStats.m_currentHitpoints = 0;
            var owner = duel.SubCircles[losingSlot];
            var minion = Occupy(duel, losingSlot + 2, _zone, 0, wizard: false);
            CombatRegressionTests.SetProperty(minion, "IsSummonedMinion", true);
            minion.CaptureMinionOwner(owner.SlotIndex);
            return true;
        });
        await Concede(losingSlot);
        Assert.Equal(kDuelPhase.kPhase_Ended, await Phase());
        await AssertOutcomes(ArenaKind.Ranked, Slots(2), 1 - losingSide, [losingSlot]);
        await AssertOneSharedResult(1 - losingSide);
        await AssertNoSecondResult(run, Slots(2));
    }

    [Fact]
    public async Task ActualAmbientFailurePreservesGenuineNoContestAndNoAwards() {
        var run = await Start(ArenaKind.Ranked, 2, [1, 5]);
        await Run(duel => {
            CombatRegressionTests.Invoke(duel, "ReceiveArenaAmbientFailed", new ArenaAmbientFailed(run));
            return true;
        });
        foreach (var outcome in _world.Outcomes.Values) {
            Assert.True(outcome.NoContest);
            Assert.False(outcome.Won);
            Assert.Equal((500, 500, 0), (outcome.RatingBefore, outcome.RatingAfter, outcome.Tickets));
            Assert.All(Result(outcome).m_actorList, actor => {
                Assert.Equal(0, actor.m_ratingGained);
                Assert.Equal(0, actor.m_arenaPoints);
            });
        }
        Assert.Equal(4, _world.Outcomes.Count);
        await Messages(_zone);
        foreach (var slot in Slots(2)) {
            var packets = await Messages(_players[slot]);
            Assert.DoesNotContain(packets, packet => packet is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT);
            Assert.False(Assert.Single(packets.OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>()).Fought);
        }
        Assert.DoesNotContain(await NativeBroadcasts(), packet => packet is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT);
        Assert.Equal(0, _world.LadderStore.Batches);
        Assert.Empty(_world.LadderStore.Saved);
        await AssertNoSecondResult(run, Slots(2));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OptionalExclusionsDefaultToEmptyAndPreserveExistingFanout(bool internalPayload) {
        var native = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT { DuelID = Sigil, WinningTeam = (int) CombatTeam.Player };
        var server = new COMBAT_106_PROTOCOL.MSG_COMBATDEATH();
        var broadcast = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message = native,
            Messages = internalPayload ? [server] : null };
        Assert.Empty(Exclusions(broadcast));
        _zone.Tell(broadcast);
        await Messages(_zone);
        foreach (var actor in _players) {
            var packets = await Messages(actor);
            Assert.Same(native, Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>()));
            Assert.Equal(internalPayload ? 1 : 0, packets.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEATH>().Count());
        }
    }

    [Fact]
    public async Task ProductionFanoutExcludesOnlyExactActorEvenWhenAnotherActorHasTheSameName() {
        var firstParent = _system.ActorOf(Props.Create(() => new NamedRecorderParent()));
        var secondParent = _system.ActorOf(Props.Create(() => new NamedRecorderParent()));
        var excluded = await firstParent.Ask<IActorRef>(new GetChild(), Timeout, TestContext.Current.CancellationToken);
        var sameName = await secondParent.Ask<IActorRef>(new GetChild(), Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(excluded.Path.Name, sameName.Path.Name);
        Assert.NotEqual(excluded, sameName);
        var supervisor = _system.ActorOf(Props.Create(() => new ZoneRecorder(new[] { excluded, sameName, _players[0] })));
        var native = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT { DuelID = Sigil, WinningTeam = (int) CombatTeam.Player };
        var server = new COMBAT_106_PROTOCOL.MSG_COMBATDEATH();
        var broadcast = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message = native, Messages = [server] };
        SetExclusions(broadcast, [excluded]);
        supervisor.Tell(broadcast);
        await Messages(supervisor);
        Assert.Empty(await Messages(excluded));
        foreach (var included in new[] { sameName, _players[0] }) {
            var packets = await Messages(included);
            Assert.Same(native, Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>()));
            Assert.Same(server, Assert.Single(packets.OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEATH>()));
        }
    }

    private sealed record GetChild;
    private sealed class NamedRecorderParent : ReceiveActor {
        public NamedRecorderParent() {
            var child = Context.ActorOf(Props.Create(() => new Recorder()), "player");
            Receive<GetChild>(_ => Sender.Tell(child));
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public async Task UnaddedDisconnectedAndUnfoughtSeatsNeverReceiveADecisivePersonalResult(bool added, bool disconnected, bool fought) {
        await Start(ArenaKind.Practice, 2, [1, 5]);
        await Run(duel => {
            var circle = duel.SubCircles[0];
            circle.AddedToDuel = added;
            if (disconnected) circle.HoldSeat(DateTime.UtcNow);
            CombatRegressionTests.Invoke(duel, "PvpReleaseSeat", circle, false, fought);
            Assert.Equal(kDuelPhase.kPhase_Planning, duel.Duel.m_duelPhase);
            return true;
        });
        await Messages(_zone);
        var packets = await Messages(_players[0]);
        Assert.DoesNotContain(packets, packet => packet is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT);
        Assert.Equal(added && !disconnected ? 1 : 0, packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE>().Count());
        Assert.Empty(_world.Outcomes);
        Assert.Equal(0, _world.LadderStore.Batches);
    }

    // Reflection lets the same regression source run against the pre-fix baseline without changing production first.
    private static IReadOnlyCollection<IActorRef> Exclusions(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message)
        => typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST).GetField("ExcludedRecipients")?.GetValue(message)
            as IReadOnlyCollection<IActorRef> ?? Array.Empty<IActorRef>();
    private static void SetExclusions(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message, IActorRef[] actors) {
        var field = typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST).GetField("ExcludedRecipients");
        Assert.NotNull(field);
        field.SetValue(message, actors);
    }

    private static int[] Slots(int size) => [.. Enumerable.Range(0, size), .. Enumerable.Range(4, size)];
    private static ulong Id(int slot) => (ulong) (1000 + slot);
    private static ArenaMatchResults Result(ArenaOutcome outcome)
        => ArenaMessages.Read<ArenaMatchResults>(Assert.IsType<GAME_5_PROTOCOL.MSG_MATCHRESULT>(outcome.Result).ResultData)!;
    private static MatchActorResult ActorResult(ArenaMatchResults result, int slot)
        => Assert.Single(result.m_actorList, actor => actor.m_pActor.m_nActorID.Full == Id(slot) + 7);

    private async Task<ulong> Start(ArenaKind kind, int size, int[] ambientSlots) {
        var slots = Slots(size);
        foreach (var slot in slots) _world.Online[Id(slot)] = new ArenaPlayer(Id(slot), Id(slot) + 7,
            [0x82, 1, 2, (byte) slot], "concede fixture", 20, "Fire", 0, ambientSlots.Contains(slot));
        _world.Arena.Create(Id(0), kind, ArenaRules.Hash(ArenaRules.MatchName(kind == ArenaKind.Ranked ? "PvPSanctioned" : "PvPPractice", size)), 0, 0, false);
        var match = _world.Arena.MatchOf(Id(0));
        var teams = _world.Arena.TeamIdsOf(match);
        foreach (var slot in slots.Where(slot => slot != 0)) _world.Arena.Join(Id(slot), match, teams[slot < 4 ? 0 : 1]);
        foreach (var slot in slots) _world.Arena.Confirm(Id(slot), true);
        var runId = _world.Arena.RunOf(match);
        Assert.NotEqual(0UL, runId);
        var run = _world.Arena.Run(runId)!;
        _world.Arena.Started(runId);
        foreach (var spectator in new[] { 8, 9 }) _world.Online[Id(spectator)] = new ArenaPlayer(Id(spectator), Id(spectator) + 7, [0x82, 1, 2, 3], "spectator fixture", 20, "Fire", 0);
        _world.Arena.Watch(Id(8), match);
        Assert.True(_world.Arena.IsSpectator(Id(8), match));
        Assert.False(_world.Arena.IsSpectator(Id(9), match));
        await Run(duel => {
            SetField(duel, "_arenaRun", run);
            foreach (var slot in slots) Occupy(duel, slot, _players[slot], Id(slot));
            var onlookers = (Dictionary<IActorRef, ulong>) typeof(CombatDuelComponent).GetField("_arenaOnlookers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(duel)!;
            onlookers[_players[8]] = Id(8);
            onlookers[_players[9]] = Id(9); // stale unauthorized entry must never receive the public direct result
            return true;
        });
        return runId;
    }

    private async Task Concede(int slot) {
        // Exactly the native planning-window move and production service translation, not a direct HandleFlee call.
        await _host.Ask<bool>(new ConcedeMove(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE {
            MoveType = (byte) CombatMoveType.Flee, SpellTarget = 1,
        }, _players[slot]), Timeout, TestContext.Current.CancellationToken);
    }
    private Task<object> Run(Func<CombatDuelComponent, object> action)
        => _host.Ask<object>(new RunDuel(action), Timeout, TestContext.Current.CancellationToken);
    private async Task<kDuelPhase> Phase() => (kDuelPhase) await Run(duel => duel.Duel.m_duelPhase);
    private static Task<object[]> Messages(IActorRef actor)
        => actor.Ask<object[]>(new GetMessages(), Timeout, TestContext.Current.CancellationToken);
    private async Task<IMessage[]> NativeBroadcasts()
        => (await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>().Select(message => message.Message).ToArray();

    private async Task AssertOutcomes(ArenaKind kind, int[] slots, int winningSide, int[] fledSlots) {
        await Messages(_zone); // zone fanout happens before the recipient barriers below
        Assert.Equal(slots.Length, _world.Outcomes.Count);
        Assert.Equal(kind == ArenaKind.Ranked ? 1 : 0, _world.LadderStore.Batches);
        foreach (var slot in slots) {
            var won = (slot < 4 ? 0 : 1) == winningSide && !fledSlots.Contains(slot);
            var outcome = _world.Outcomes[Id(slot)];
            Assert.Equal(won, outcome.Won);
            Assert.Equal(fledSlots.Contains(slot), outcome.Fled);
            Assert.False(outcome.NoContest);
            Assert.Equal(500, outcome.RatingBefore);
            Assert.Equal(kind == ArenaKind.Ranked ? won ? 10 : 3 : 0, outcome.Tickets);
            Assert.Equal(ArenaRules.RankOf(outcome.RatingAfter, _world.Config.Ranks), outcome.Rank);
            var packets = await Messages(_players[slot]);
            var personal = Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
            Assert.Equal(Sigil, personal.DuelID);
            var ownTeam = slot < 4 ? CombatTeam.Monster : CombatTeam.Player;
            var expectedTeam = won ? ownTeam : ownTeam == CombatTeam.Monster ? CombatTeam.Player : CombatTeam.Monster;
            Assert.Equal((int) expectedTeam, personal.WinningTeam);
            var ending = Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE>());
            Assert.Equal((byte) kDuelPhase.kPhase_Ended, ending.NewPhase);
            Assert.True(Array.IndexOf(packets, personal) < Array.IndexOf(packets, ending));
            var release = Assert.Single(packets.OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>());
            Assert.Equal(won, release.Won);
            Assert.True(release.Fought);
            Assert.True(Array.IndexOf(packets, ending) < Array.IndexOf(packets, release));
            var native = Assert.Single(packets.OfType<GAME_5_PROTOCOL.MSG_MATCHRESULT>());
            Assert.Equal(Id(slot) + 7, native.CharacterID);
            Assert.Equal(outcome.Result, native);
            var actor = ActorResult(Result(outcome), slot);
            Assert.Equal(outcome.RatingAfter - 500, actor.m_ratingGained);
            Assert.Equal(outcome.Tickets, actor.m_arenaPoints);
            if (kind == ArenaKind.Ranked) {
                Assert.Equal(won ? 1 : -1, Math.Sign(outcome.RatingAfter - 500));
                var standing = _world.Arena.Standing(Id(slot));
                Assert.Equal(new ArenaStanding(outcome.RatingAfter, won ? 1 : 0, won ? 0 : 1), standing);
                Assert.Equal(1, _world.LadderStore.Saved[Id(slot)]);
            } else {
                Assert.Equal(500, outcome.RatingAfter);
                Assert.Equal(new ArenaStanding(500, 0, 0), _world.Arena.Standing(Id(slot)));
                Assert.Null(_world.Ladder.Load(Id(slot)));
            }
        }
        Assert.Equal(slots.Count(slot => _world.Online[Id(slot)].Ambient), _world.Released.Count);
    }

    private async Task AssertOneSharedResult(int winningSide) {
        var packets = await NativeBroadcasts();
        var result = Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Equal((byte) (winningSide == 0 ? CombatTeam.Monster : CombatTeam.Player), result.WinningTeam);
        Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL>());
        var spectator = await Messages(_players[8]);
        var shared = Assert.Single(spectator.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Equal((int) (winningSide == 0 ? CombatTeam.Monster : CombatTeam.Player), shared.WinningTeam);
        Assert.DoesNotContain(spectator, message => message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE);
        // Observer winner and end remain the existing public zone route; spectator authority is retired by ArenaReport.
        var ordinaryObserver = Assert.Single((await Messages(_players[9])).OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Equal(shared.WinningTeam, ordinaryObserver.WinningTeam);
        var global = Assert.Single((await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(), message => message.Message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT);
        Assert.Equal(ZoneBroadcastTarget.All, global.Targets);
        Assert.Equal(_world.Outcomes.Count, Exclusions(global).Count);
        Assert.DoesNotContain(_players[8], Exclusions(global));
        Assert.DoesNotContain(_players[9], Exclusions(global));
        await Run(duel => {
            var recipients = (HashSet<IActorRef>) typeof(CombatDuelComponent).GetField("_pvpResultRecipients", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(duel)!;
            Assert.Empty(recipients);
            return true;
        });
        Assert.Equal(_world.Outcomes.Count, Exclusions(global).Count); // exclusion snapshot survives cleanup
    }
    private async Task AssertNoSecondResult(ulong run, int[] slots) {
        var before = _world.Outcomes.Values.ToArray();
        var saved = _world.LadderStore.Saved.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var slot in slots) await Concede(slot); // late/duplicate native clicks have no participant anymore
        _world.Arena.Finish(run, -1, []);
        foreach (var slot in slots) _world.Arena.AmbientLost(Id(slot));
        await Run(duel => { CombatRegressionTests.Invoke(duel, "ReceiveArenaAmbientFailed", new ArenaAmbientFailed(run)); return true; });
        Assert.Equal(before, _world.Outcomes.Values.ToArray());
        Assert.Equal(saved.OrderBy(pair => pair.Key), _world.LadderStore.Saved.OrderBy(pair => pair.Key));
        foreach (var slot in slots) Assert.Single((await Messages(_players[slot])).OfType<GAME_5_PROTOCOL.MSG_MATCHRESULT>());
        Assert.Null(_world.Arena.Run(run));
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, IActorRef actor, ulong charId, bool wizard = true) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", actor);
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = wizard ? 1UL : 567UL, m_globalID = (ulong) (2000 + slot) });
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_currentHitpoints = stats.m_baseHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pipCount = new PipCount(), m_subcircle = slot });
        if (wizard) {
            var character = (Wizard) RuntimeHelpers.GetUninitializedObject(typeof(Wizard));
            character.GameObject = new WizClientObject(); character.CharId = charId; character.GameStats = stats;
            circle._wizard = character;
        }
        circle.AddedToDuel = true;
        return circle;
    }
    private static void SetField(CombatDuelComponent duel, string name, object value)
        => typeof(CombatDuelComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, value);
    private sealed record RunDuel(Func<CombatDuelComponent, object> Action);
    private sealed record ConcedeMove(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE Message, IActorRef Actor);
    private sealed record GetMessages;
    private sealed class Recorder : ReceiveActor {
        private readonly List<object> _messages = [];
        public Recorder() { Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray())); ReceiveAny(message => _messages.Add(message)); }
    }
    private sealed class ZoneRecorder : ZoneEntitySupervisor {
        private readonly List<object> _messages = [];
        public ZoneRecorder(IActorRef[] recipients) : base(null!) { EntityActors.AddRange(recipients); }
        protected override void ConfigureReceivers() {
            Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray()));
            base.ConfigureReceivers();
        }
        public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) { }
        [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
        public override void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
            _messages.Add(message);
            if ((message.Targets & ZoneBroadcastTarget.Players) != 0) base.ReceiveZoneBroadcast(message);
        }
    }
    private sealed class DuelHost : ZoneEntity {
        private readonly CombatDuelComponent _duel;
        public DuelHost(IActorRef zone) : base(new CoreObject { m_globalID = Sigil }, new GameObjectTemplate { m_behaviors = [] }, new CoreObjectInfo(), zone, null!) {
            _duel = new CombatDuelComponent(this);
            _duel.AttachTo(Self);
            _duel.Timers = DispatchProxy.Create<ITimerScheduler, ArenaConcedeTimers>();
            CombatRegressionTests.SetProperty(_duel, "Duel", new Duel { m_duelID = Sigil, m_bPVP = true,
                m_duelPhase = kDuelPhase.kPhase_Planning, m_firstTeamToAct = (int) CombatTeam.Player,
                m_flatParticipantList = [], m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
            CombatRegressionTests.SetProperty(_duel, "SubCircles", Enumerable.Range(0, 8).Select(slot => new CombatDuelSubCircle(_duel, 0, 0, default, slot) {
                PvpTeam = slot < 4 ? CombatTeam.Monster : CombatTeam.Player,
                SlotType = slot < 4 ? CombatSlotType.Creature : CombatSlotType.Player }).ToArray());
            CombatRegressionTests.SetProperty(_duel, "CombatResolver", new Imlight.CoreLib.Game.Combat.CombatResolver(_duel.Duel, _duel.SubCircles));
            _duel.CombatResolver.Reset();
            SetField(_duel, "_pvp", true); SetField(_duel, "_arena", true); SetField(_duel, "_isActive", true);
            SetField(_duel, "_awaitingCombatMoves", true);
            SetField(_duel, "_tutorialDirector", new TutorialDuelDirector(_duel, ""));
            SetField(_duel, "_combatSigilObjectInfo", new CombatSigilObjectInfo { m_zoneTag = "arena concede fixture" });
        }
        protected override void ConfigureReceivers() {
            Receive<RunDuel>(request => {
                try { Sender.Tell(request.Action(_duel)); } catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            Receive<ConcedeMove>(request => {
                try { CombatRegressionTests.Invoke(_duel, "ReceiveCombatMove", CombatService.TranslateCombatMove(request.Message, request.Actor)); Sender.Tell(true); }
                catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            base.ConfigureReceivers();
        }
        protected override void PostStop() { ActiveDuels.Remove(Sigil); ClassicPvp.Forget("", "arena concede fixture"); base.PostStop(); }
    }
    private sealed class CountingLadder : IArenaLadderStore {
        private readonly ArenaLadderCollection.Memory _inner = new();
        public int Batches;
        public readonly Dictionary<ulong, int> Saved = [];
        public ArenaLadderEntry? Load(ulong id) => _inner.Load(id);
        public void Save(ArenaLadderEntry entry) { Saved[entry.CharId] = Saved.GetValueOrDefault(entry.CharId) + 1; _inner.Save(entry); }
        public void SaveMany(IReadOnlyCollection<ArenaLadderEntry> entries) { Batches++; foreach (var entry in entries) Save(entry); }
    }
    private sealed class World : IArenaWorld, IArenaAmbientWorld {
        private readonly IActorRef[] _recipients;
        public readonly ArenaConfig Config = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        public readonly Dictionary<ulong, ArenaPlayer> Online = [];
        public readonly ConcurrentDictionary<ulong, ArenaOutcome> Outcomes = new();
        public readonly CountingLadder LadderStore = new();
        public readonly List<ulong> Released = [];
        private ulong _runs = 90000;
        public ArenaMatchmaker Arena { get; }
        public World(IActorRef[] recipients) { _recipients = recipients; Arena = new ArenaMatchmaker(Config, this); }
        public IArenaLadderStore Ladder => LadderStore;
        public bool AmbientEnabled => false; // explicit authored rosters, no autonomous reservation/timer
        public bool IsAmbient(ulong id) => Online.GetValueOrDefault(id)?.Ambient == true;
        public ArenaPlayer? ReserveAmbient(int level, int preferredSchool) => null;
        public ArenaPlayer? Player(ulong id) => Online.GetValueOrDefault(id);
        public bool AreFriends(ulong first, ulong second) => true;
        public void Send(ulong id, IMessage message) { }
        public void Inform(ulong id, string text) { }
        public void Travel(ulong id, string zone, string location, ulong run) { }
        public string? ZoneOf(ulong id) => Config.HallZone;
        public ulong NewRunId() => ++_runs;
        public void Deliver(ulong id, ArenaOutcome outcome) { Assert.True(Outcomes.TryAdd(id, outcome)); _recipients[(int) (id - 1000)].Tell(outcome.Result); }
        public void ReleaseAmbient(ulong id) { Released.Add(id); Arena.AmbientLost(id); }
    }
}

public class ArenaConcedeTimers : DispatchProxy {
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        => method!.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
