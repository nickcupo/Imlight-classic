// CLASSIC: native wire, typed state and production route coverage for Sorcery Stones.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using SavedScore = Imlight.CoreLib.WizardData.Models.Misc.ScoreTracking;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Security;
using Imlight.CoreLib.Game.Minigames;
using Imlight.CoreLib.Game.Services;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SoblocksProtocolTests {
    public SoblocksProtocolTests() => EquipmentAttachConcurrencyTests.Configure();
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly int[] PlainTiles = [1, 6, 3, 10, 7, 5];
    private static T Wire<T>(T source) where T : IMessage {
        var decoded = Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(source))!);
        Assert.Equal(source.GetType(), decoded.GetType());
        return (T)decoded;
    }
    private static int[] Cells(SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW row) =>
        [row.Index1, row.Index2, row.Index3, row.Index4, row.Index5, row.Index6, row.Index7];

    [Fact]
    public void NativeGeneratedOrdersAndUppercaseScoreSurviveActualWireEncoding() {
        (IMessage Message, byte Order)[] messages = [
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF(), 1),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ATTACK(), 3),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = 0x0180fffa }, 5),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_READY(), 13),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_REQUESTROW(), 14),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 2 }, 18),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW { Index1 = 200, Index7 = 255 }, 19),
            (new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP(), 21),
        ];
        foreach (var (source, order) in messages) {
            var bytes = MessageEncoder.Encode(source);
            Assert.Equal((byte)25, bytes[8]); Assert.Equal(order, bytes[9]);
            Assert.Equal(source.GetType(), Assert.Single(MessageEncoder.Decode(bytes)!).GetType());
        }
        var encoded = MessageEncoder.Encode(messages[2].Message);
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(10, 2)));
        Assert.Equal(0x0180fffa, BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(12, 4)));
        Assert.Equal(0x0180fffa, Wire((SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME)messages[2].Message).Score);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SelectionSendsTheNativeMatchFreeBoardUpcomingRowAndStartSignal(int mode) {
        for (var seed = 0; seed < 50; seed++) {
            var f = new Route(new Random(seed).Next);
            f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = mode });
            Assert.True(f.Run.Started);
            Assert.Equal(mode, Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME>(f.Replies[0]).GameType);
            Assert.IsType<WIZARD_12_PROTOCOL.MSG_ENTERMINIGAME>(f.Replies[^1]);
            var rows = f.Replies.OfType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW>().Select(Cells).ToArray();
            Assert.Equal(6, rows.Length);
            Assert.All(rows.SelectMany(x => x), tile => Assert.Contains(tile, PlainTiles));
            for (var r = 0; r < 5; r++) for (var c = 0; c < 7; c++) {
                if (r >= 2) Assert.False(rows[r][c] == rows[r - 1][c] && rows[r][c] == rows[r - 2][c]);
                if (c >= 2) Assert.False(rows[r][c] == rows[r][c - 1] && rows[r][c] == rows[r][c - 2]);
            }
            for (var c = 1; c < 7; c++) Assert.NotEqual(rows[5][c - 1], rows[5][c]);
        }
    }

    [Fact]
    public void LeaderboardBeforePlayAndControlOrderOneOrThreeNeverStartOrRewardARun() {
        var f = new Route();
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = -1 });
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_READY());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ATTACK { Index1 = 999999 });
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 3 });
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = 5000 });
        Assert.False(f.Run.Started); Assert.Empty(f.Replies); Assert.Equal([-1], f.Scores);
    }

    [Fact]
    public void OnlyAValidResultResetSelectionLifecycleStartsANewRewardRun() {
        var f = new Route(); var paid = new Dictionary<ulong, DateTimeOffset>();
        f.Start(); f.Replies.Clear();
        f.Now = T0.AddSeconds(5);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 1 });
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_READY());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF());
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ATTACK());
        Assert.Single(f.Replies); // AdvanceOff echo only; no new enter/board/reward.
        f.Now = T0.AddSeconds(21);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = 0x80ff });
        Assert.Equal([0x80ff], f.Scores); Assert.Equal(MinigameRewardDecision.Reward, f.Run.Decide(f.Now, 7, paid));
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = int.MaxValue });
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 0 });
        Assert.Single(f.Scores); Assert.Equal(MinigameRewardDecision.AlreadyRewarded, f.Run.Decide(f.Now, 7, paid));
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME());
        Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME>(f.Replies[^1]);
        f.Now = T0.AddSeconds(22); f.Start();
        Assert.Equal(MinigameRewardDecision.TooQuick, f.Run.Decide(T0.AddSeconds(23), 7, paid));
        Assert.Equal(MinigameRewardDecision.Cooldown, f.Run.Decide(T0.AddSeconds(43), 7, paid));
        Assert.Equal(MinigameRewardDecision.Reward, f.Run.Decide(T0.AddSeconds(52), 7, paid));
        Assert.Equal(5000, MinigameRun.ClampScore(int.MaxValue, [100, 300, 500]));
    }

    [Fact]
    public void AnEarlyRejectedResultCannotBecomePayableByRepeatingItLater() {
        var f = new Route(); f.Start();
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = 100 });
        Assert.Equal(MinigameRewardDecision.TooQuick, f.Run.Decide(T0, 7, new Dictionary<ulong, DateTimeOffset>()));
        f.Now = T0.AddMinutes(1);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = 10000 });
        Assert.Equal([100], f.Scores);
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 1, 201)] // frozen wins over every other special
    [InlineData(1, 1, 1, 1, 0, 101)]
    [InlineData(1, 1, 1, 0, 0, 21)]
    [InlineData(1, 1, 0, 0, 0, 11)]
    [InlineData(1, 0, 0, 0, 0, 22)]
    public void ClientChanceDenominatorsProduceTheNativeSpecialPriority(int jewel, int bomb, int pinwheel,
            int reinforced, int frozen, int expected) {
        var f = new Route(_ => 0); f.Start(); f.Replies.Clear();
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW {
            Index1 = jewel, Index2 = bomb, Index3 = pinwheel, Index4 = reinforced, Index5 = frozen,
        });
        Assert.Empty(f.Replies);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_REQUESTROW());
        Assert.All(Cells(Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW>(Assert.Single(f.Replies))),
                   tile => Assert.Equal(expected, tile));
    }

    [Fact]
    public void PlainRequestsAvoidAdjacentMatchesAndLevelUpAddsOnlyTheFourAuthoredColors() {
        var f = new Route(); f.Start();
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW { Index1 = -1, Index2 = int.MinValue });
        for (var level = 0; level < 5; level++) {
            f.Replies.Clear();
            for (var n = 0; n < 100; n++) f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_REQUESTROW());
            var allowed = PlainTiles.Concat(new[] { 4, 8, 9, 2 }.Take(level)).ToArray();
            var rows = f.Replies.OfType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW>().Select(Cells).ToArray();
            Assert.All(rows.SelectMany(x => x), tile => Assert.Contains(tile, allowed));
            Assert.All(allowed, tile => Assert.Contains(tile, rows.SelectMany(x => x)));
            foreach (var row in rows) for (var c = 1; c < 7; c++) Assert.NotEqual(row[c - 1], row[c]);
            f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_LEVELUP());
        }
    }

    [Fact]
    public void PauseSwapAdvanceAndPolledCountdownHaveNoRewardOrRunResetSideEffects() {
        var f = new Route(); f.Start(); f.Replies.Clear();
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSE());
        Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSEON>(Assert.Single(f.Replies));
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP()); Assert.Single(f.Replies);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSE());
        Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSEOFF>(f.Replies[^1]);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP());
        Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP>(f.Replies[^1]);
        f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEON());
        Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEON>(f.Replies[^1]);
        f.Replies.Clear(); f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN());
        f.Now = T0.AddSeconds(1); f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN());
        Assert.Single(f.Replies);
        for (var i = 1; i <= 4; i++) {
            f.Now = T0.AddMilliseconds(1001 * i); f.Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN());
        }
        Assert.Equal([4, 3, 2, 1, 0], f.Replies.OfType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN>().Select(x => x.Number));
        Assert.Empty(f.Scores);
        Assert.Equal(MinigameRewardDecision.Reward, f.Run.Decide(T0.AddSeconds(21), 7, new Dictionary<ulong, DateTimeOffset>()));
    }

    [Fact]
    public void ForeignGameServicesCannotEnterOrRewardSorceryAndService25CannotEnterOtherJobs() {
        var f = new Route();
        f.Send(new SKULLRIDERS_MESSAGES_40_PROTOCOL.MSG_SKULLRIDERS_CONNECT());
        Assert.False(f.Run.Started); Assert.Empty(f.Replies);
        foreach (var message in new IMessage[] { new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME(),
                new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = 10000 },
                new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF(), new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ATTACK() })
            Assert.True(MinigameProcess.TryDispatchSoblocks("SkullRiders", f.Session, f.Run, Wire(message), T0,
                f.Replies.Add, f.Scores.Add));
        Assert.False(f.Run.Started); Assert.Empty(f.Replies); Assert.Empty(f.Scores);
        Assert.False(MinigameProcess.TryDispatchSoblocks("SkullRiders", f.Session, f.Run,
            Wire(new SKULLRIDERS_MESSAGES_40_PROTOCOL.MSG_SKULLRIDERS_CONNECT()), T0, f.Replies.Add, f.Scores.Add));
    }

    [Fact]
    public void LateJobTunnelIsDroppedBeforeDecodeOrAccessToTheSession() {
        var service = (MinigameService)RuntimeHelpers.GetUninitializedObject(typeof(MinigameService));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MinigameService).GetField("_minigameJob", flags)!.SetValue(service, ActorRefs.Nobody);
        typeof(MinigameService).GetField("_minigameProcessId", flags)!.SetValue(service, 101u);
        var handler = typeof(MinigameService).GetMethod("ReceiveMessageProcess", flags)!;
        // If routed or decoded, this invalid tunnel would reach uninitialized session state or throw.
        handler.Invoke(service, [new GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS { JobID = 100, Message = new byte[] { 255 } }]);
    }

    [Fact]
    public async Task RealMinigameActorDispatchesTypedSelectionWithoutTheOldGenericOrderConvention() {
        using var system = ActorSystem.Create("soblocks-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var queue = new ConcurrentQueue<IMessage>();
        var errors = new ConcurrentQueue<Akka.Event.Error>();
        var probe = system.ActorOf(Props.Create(() => new Capture(queue, errors)));
        system.EventStream.Subscribe(probe, typeof(Akka.Event.Error));
        var info = new MinigameInfo { m_name = "Soblocks", m_scoreThresholds = [100, 300, 500] };
        var actor = system.ActorOf(Props.Create(() => new MinigameProcess("Soblocks", 501, 0, info)));
        try {
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF()), probe);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ATTACK()), probe);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 0 }), probe);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (queue.Count < 8 && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
            var replies = queue.ToArray();
            Assert.True(replies.Length == 8, "Expected 8 startup replies, got " + replies.Length + "; "
                + string.Join("; ", errors.Select(error => error.Cause?.ToString() ?? error.Message?.ToString())));
            Assert.IsType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME>(replies[0]);
            Assert.Equal(6, replies.OfType<SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW>().Count());
            Assert.IsType<WIZARD_12_PROTOCOL.MSG_ENTERMINIGAME>(replies[^1]);
        }
        finally { await system.Terminate().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task PositiveEndgameUsesTheRealRewardRoutineQueryCapAndOncePerRunGuards() {
        // Only persistence and time are substituted. The production actor, query, reward admission,
        // loot construction, score record and final native packet all run unchanged.
        var potions = typeof(ClassicProgression).GetField("s_potions", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = potions.GetValue(null);
        potions.SetValue(null, null);
        using var system = ActorSystem.Create("soblocks-rewards-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var clock = new Clock();
        var queue = new ConcurrentQueue<IMessage>();
        var saved = new ConcurrentQueue<SavedScore>();
        var queried = 0;
        var wizard = new Wizard { CharId = 981776331,
            PlayerNameBehavior = new ServerWizPlayerNameBehavior { NameOverride = "Fixture Wizard" },
            GameStats = new(default, 1) { m_baseMana = 50 },
        };
        var probe = system.ActorOf(Props.Create(() => new PlayerCapture(queue, wizard, () => Interlocked.Increment(ref queried))));
        var info = new MinigameInfo { m_name = "Soblocks", m_scoreThresholds = [100, 300, 500] };
        Func<string, SavedScore[]> lookup = _ => saved.ToArray();
        Action<SavedScore> record = saved.Enqueue;
        var actor = system.ActorOf(Props.Create(() => new MinigameProcess("Soblocks", 802, 7, info, clock, lookup, record)));
        try {
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = -1 }), probe);
            var leaderboard = await NextReward();
            Assert.Equal(0, leaderboard.FinalPhase); Assert.Equal(-1, leaderboard.Score); Assert.Equal(0, queried);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 0 }), probe);
            await Entered();
            clock.At(T0.AddSeconds(21));
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = int.MaxValue }), probe);
            var result = await NextReward();
            Assert.Equal(1, result.FinalPhase); Assert.Equal(5000, result.Score); Assert.Equal(1, result.Success);
            Assert.Equal((byte)7, result.MinigameIndex); Assert.Equal(1, queried);
            Assert.Equal(5000, Assert.Single(saved).GameScore);
            var serializer = new ObjectSerializer(Behaviors: SerializerFlags.None);
            Assert.True(serializer.Deserialize((byte[])result.Data, 5, out LootInfoList loot));
            Assert.InRange(loot.m_goldInfo.m_goldAmount, 10, 15);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = int.MaxValue }), probe);
            // The next leaderboard reply fences the duplicate in the same sender's mailbox order.
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = -1 }), probe);
            var afterDuplicate = await NextReward();
            Assert.Equal(0, afterDuplicate.FinalPhase); Assert.Single(saved); Assert.Equal(1, queried);
            Assert.True(serializer.Deserialize((byte[])afterDuplicate.Scores, 5, out ScoreTrackingList scores));
            Assert.Equal(5000, Assert.Single(scores.m_scores).m_gameScore);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME()), probe);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME()), probe);
            await Entered();
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = int.MaxValue }), probe);
            var early = await NextReward();
            Assert.Equal(0, early.Success); Assert.Equal(5000, early.Score); Assert.Single(saved); Assert.Equal(2, queried);
            Assert.True(serializer.Deserialize((byte[])early.Data, 5, out LootInfoList empty));
            Assert.Equal(0, empty.m_goldInfo.m_goldAmount); Assert.Empty(empty.m_loot);
            clock.At(T0.AddMinutes(2));
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = int.MaxValue }), probe);
            actor.Tell(Wire(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME { Score = -1 }), probe);
            Assert.Equal(0, (await NextReward()).FinalPhase); Assert.Single(saved); Assert.Equal(2, queried);
        }
        finally {
            try { await system.Terminate().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
            finally { potions.SetValue(null, previous); }
        }

        async Task Entered() {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) {
                if (queue.TryDequeue(out var packet) && packet is WIZARD_12_PROTOCOL.MSG_ENTERMINIGAME) return;
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            Assert.Fail("Minigame did not start");
        }
        async Task<WIZARD_12_PROTOCOL.MSG_MINIGAMEREWARDS> NextReward() {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) {
                if (queue.TryDequeue(out var packet) && packet is WIZARD_12_PROTOCOL.MSG_MINIGAMEREWARDS reward)
                    return Wire(reward);
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("Production reward routine did not reply");
        }
    }

    private sealed class PlayerCapture : ReceiveActor {
        public PlayerCapture(ConcurrentQueue<IMessage> queue, Wizard wizard, System.Action queried) {
            Receive<CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD>(_ => {
                queried(); Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER { Wizard = wizard });
            });
            Receive<IMessage>(queue.Enqueue);
        }
    }
    private sealed class Clock : TimeProvider {
        private long _ticks = T0.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void At(DateTimeOffset now) => Interlocked.Exchange(ref _ticks, now.UtcTicks);
    }
    private sealed class Capture : ReceiveActor {
        public Capture(ConcurrentQueue<IMessage> queue, ConcurrentQueue<Akka.Event.Error> errors) {
            Receive<Akka.Event.Error>(errors.Enqueue);
            Receive<IMessage>(queue.Enqueue);
        }
    }
    private sealed class Route(Func<int, int>? next = null) {
        internal readonly SoblocksSession Session = new(next ?? new Random(7341).Next);
        internal readonly MinigameRun Run = new();
        internal readonly List<IMessage> Replies = [];
        internal readonly List<int> Scores = [];
        internal DateTimeOffset Now = T0;
        internal void Start() => Send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = 0 });
        internal void Send<T>(T message) where T : IMessage => Assert.True(MinigameProcess.TryDispatchSoblocks(
            "Soblocks", Session, Run, Wire(message), Now, Replies.Add, Scores.Add));
    }
}
