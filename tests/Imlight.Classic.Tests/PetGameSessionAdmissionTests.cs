// CLASSIC: actual pet service handlers bind native Game identities and retire only successfully replaced games.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class PetGameSessionAdmissionTests {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const string Dance = "PetGameDance", Morph = "PetGameMorph";
    private const ulong PetId = (1UL << 40) + 793002;
    private const uint PetTemplate = 793003;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DanceAnswersBeforeReadyOrBeforeTheFirstIssuedRoundDoNotScheduleOrScore() {
        using var f = await Fixture.Create();
        await f.Join(Dance); await f.Drain();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = "abc" });
        var before = await f.State();
        Assert.False(before.Started); Assert.Null(before.Timer); Assert.Equal(0, before.Round);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        var ready = await f.State(); Assert.NotNull(ready.Timer);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = "abc" });
        var after = await f.State();
        Assert.True(after.Started); Assert.Same(ready.Timer, after.Timer); Assert.Null(after.Current); Assert.Equal(0, after.Round);
        Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMESTART>());
        Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public async Task AnIssuedDanceRoundAcceptsOneAnswerAndDuplicateCallbackCannotReplaceItsMoves() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        var first = (await f.State()).Timer!;
        await f.Fire(first); var issued = await f.State(); Assert.NotNull(issued.Current);
        await f.Fire(first); Assert.Equal(issued.Current, (await f.State()).Current);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = issued.Current });
        var answered = await f.State(); Assert.Equal(1, answered.Round); Assert.Equal(1, answered.Successes); Assert.Null(answered.Current);
        await f.Fire(first); Assert.Null((await f.State()).Current); // The preceding round's queued callback cannot issue this one early.
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = issued.Current });
        var repeated = await f.State();
        Assert.Equal(1, repeated.Round); Assert.Equal(1, repeated.Successes); Assert.Same(answered.Timer, repeated.Timer);
        Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEDANCE>());
        await f.Fire(answered.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Theory]
    [InlineData("PetGameDrop")] [InlineData("PetGameDance")]
    public async Task SuccessfulTrainingReplacementCancelsTheOldTimerAndQueuedCallbackCannotStartTheReplacement(string next) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State();
        await f.Join(next); var replacement = await f.State();
        Assert.NotSame(old.Session, replacement.Session); Assert.False(replacement.Started); Assert.Null(replacement.Timer);
        await f.Fire(old.Timer!); Assert.Null((await f.State()).Current);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var ready = await f.State();
        await f.Fire(old.Timer!); Assert.Null((await f.State()).Current);
        if (next == Dance) {
            Assert.NotNull(ready.Timer); await f.Fire(ready.Timer!); Assert.NotNull((await f.State()).Current);
        }
        Assert.Equal(0, f.Store.Saves);
    }

    [Theory]
    [InlineData("UnknownGame")] [InlineData("PetGameDrop")]
    public async Task RefusedTrainingJoinPreservesTheExistingStartedDanceAndTimer(string game) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        if (game != "UnknownGame") f.Store.Saved.PetOwnerBehavior.SetEnergy(0);
        await f.Join(game); var after = await f.State();
        Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer); Assert.True(after.Started);
        Assert.Equal(0, Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Theory]
    [InlineData("PetGameDrop")] [InlineData("petgamedance")] [InlineData("")]
    public async Task DataForAnotherOrDifferentlyCasedGameCannotFinishTheCurrentTraining(string game) {
        using var f = await Fixture.Create(); await f.Join(Dance); await f.Drain(); var old = await f.State();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = game, Data = new ByteString(new byte[] { 1 }) });
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.False(after.Ended);
        Assert.Equal(0, f.Store.Saves); Assert.Empty(await f.Drain());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DebugFinishRequiresAnActualQaAccount(bool missingAccount) {
        using var f = await Fixture.Create(); await f.Join(Dance); await f.Drain();
        if (missingAccount) f.Store.Live.Account = null!;
        else f.Store.Live.Account.AuthLevel = AuthLevel.None;
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 1 }) });
        Assert.False((await f.State()).Ended); Assert.Equal(0, f.Store.Saves); Assert.Empty(await f.Drain());
    }

    [Fact]
    public async Task MatchingQaFinishRetainsItsExistingPreReadyPathAndEndedSnackCommandsAreStillAdmitted() {
        using var f = await Fixture.Create(); await f.Join(Dance); await f.Drain();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 1 }) });
        var ended = await f.State(); Assert.True(ended.Ended); Assert.False(ended.Started); Assert.Null(ended.Timer);
        Assert.Equal(1, f.Store.Saves); Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEEND>());
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = "abc" });
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 1 }) });
        Assert.Equal(1, f.Store.Saves); Assert.Empty(await f.Drain());
        // An unavailable snack still reaches the existing post-END failure response, without another save.
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = "PetGameDrop", Data = new ByteString(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0, 0 }) });
        Assert.Empty(await f.Drain());
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0, 0 }) });
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED>(Assert.Single(await f.Drain()));
        Assert.Equal(1, f.Store.Saves);
    }

    [Theory]
    [InlineData("PetGameDrop")] [InlineData("petgamedance")] [InlineData("PetGameMorph")]
    public async Task MismatchedEndingCannotRetireTrainingAndMatchingEndingDoesNotGuessGlobalId(string game) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = game, GlobalID = 0 });
        Assert.Same(old.Session, (await f.State()).Session); Assert.Same(old.Timer, (await f.State()).Timer);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = Dance, GlobalID = ulong.MaxValue });
        Assert.Null((await f.State()).Session); Assert.Null((await f.State()).Timer);
        await f.Fire(old.Timer!); Assert.Null((await f.State()).Session); Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public async Task SuccessfulMorphReplacesTrainingAndTrainingJoinLeavesTheExactOldMorphAssociation() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State();
        await f.Join(Morph); var morph = await f.State(); Assert.Null(morph.Session); Assert.NotNull(morph.Lobby); Assert.Null(morph.Timer);
        await f.Fire(old.Timer!); Assert.Null((await f.State()).Session);
        await f.Join(Dance); var training = await f.State(); Assert.NotNull(training.Session); Assert.Null(training.Lobby);
        Assert.Null(Sides(morph.Lobby!).GetValue(morph.Side));
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Morph, Data = "set:side=0;id=1" });
        Assert.Null((await f.State()).Lobby); Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public async Task FullMorphCandidatePreservesTrainingAndItsQueuedRound() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        f.FillLobby(f.Store.Live.Zone);
        await f.Join(Morph); var after = await f.State();
        Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer); Assert.Null(after.Lobby);
        Assert.Equal(0, Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Fact]
    public async Task RefusedTrainingAndFullReplacementMorphPreserveTheExistingMorphOffer() {
        using var f = await Fixture.Create(); await f.Join(Morph);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Morph, Data = $"set:side=0;id={PetId}" });
        var old = await f.State(); Assert.Equal(PetId, old.MorphPet);
        f.Store.Saved.PetOwnerBehavior.SetEnergy(0); await f.Join(Dance);
        Assert.Same(old.Lobby, (await f.State()).Lobby); Assert.Equal(PetId, (await f.State()).MorphPet);
        var key = f.Store.Live.Zone + "/full"; f.FillLobby(key); f.Store.Live.Zone = key;
        await f.Join(Morph); var refused = await f.State();
        Assert.Same(old.Lobby, refused.Lobby); Assert.Equal(old.Side, refused.Side); Assert.Equal(PetId, refused.MorphPet);
        Assert.NotNull(Sides(old.Lobby!).GetValue(old.Side));
    }

    [Fact]
    public async Task DuplicateMorphJoinReplaysAdmissionWithoutDroppingItsOfferAndMatchingEndingReleasesIt() {
        using var f = await Fixture.Create(); await f.Join(Morph);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Morph, Data = $"set:side=0;id={PetId}" });
        var old = await f.State(); await f.Drain();
        await f.Join(Morph); var after = await f.State();
        Assert.Same(old.Lobby, after.Lobby); Assert.Equal(old.Side, after.Side); Assert.Equal(PetId, after.MorphPet);
        var replay = await f.Drain(); Assert.Equal(3, replay.Length);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(replay[0]); Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(replay[1]); Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESTART>(replay[2]);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = Dance }); Assert.Same(old.Lobby, (await f.State()).Lobby);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = Morph, GlobalID = ulong.MaxValue });
        Assert.Null((await f.State()).Lobby); Assert.Null(Sides(old.Lobby!).GetValue(old.Side));
    }

    [Fact]
    public async Task AStaleVacantMorphHandleCannotRemoveTheNewlyAcquiredSameLobbySlot() {
        using var f = await Fixture.Create(); await f.Join(Morph); var old = await f.State();
        // Simulate a retired association while its old service-side handle remains queued.
        Sides(old.Lobby!).SetValue(null, old.Side);
        await f.Join(Morph); var replacement = await f.State();
        Assert.Same(old.Lobby, replacement.Lobby); Assert.Equal(old.Side, replacement.Side);
        Assert.NotNull(Sides(replacement.Lobby!).GetValue(replacement.Side));
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Morph, Data = $"set:side=0;id={PetId}" });
        Assert.Equal(PetId, (await f.State()).MorphPet);
    }

    [Fact]
    public async Task AQueuedFormerPartnerDepartureCannotClearTheNewMorphLobby() {
        using var f = await Fixture.Create(); await f.Join(Morph); var old = await f.State();
        var peerType = typeof(PetGameService).GetNestedType("MorphSide", BindingFlags.NonPublic)!;
        var formerPeer = Activator.CreateInstance(peerType, nonPublic: true)!;
        var noticeType = typeof(PetGameService).GetNestedType("PartnerChanged", BindingFlags.NonPublic)!;
        var stale = Activator.CreateInstance(noticeType, [old.Lobby!, formerPeer, 0UL, false, true])!;
        f.Store.Live.Zone += "/replacement"; await f.Join(Morph); var replacement = await f.State();
        Assert.NotSame(old.Lobby, replacement.Lobby); await f.Drain();
        await f.Send(stale); Assert.Empty(await f.Drain()); Assert.Same(replacement.Lobby, (await f.State()).Lobby);

        // A departure in this current lobby still clears the native partner display, as before.
        var currentNotice = Activator.CreateInstance(noticeType, [replacement.Lobby!, formerPeer, 0UL, false, true])!;
        var newPeer = Activator.CreateInstance(peerType, nonPublic: true)!;
        Sides(replacement.Lobby!).SetValue(newPeer, 1 - replacement.Side);
        await f.Send(currentNotice); Assert.Empty(await f.Drain()); // This slot now belongs to a different peer.
        Sides(replacement.Lobby!).SetValue(null, 1 - replacement.Side);
        await f.Send(currentNotice);
        var packets = await f.Drain(); Assert.Equal(2, packets.Length);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETMORPHSET>(packets[0]); Assert.IsType<PET_9_PROTOCOL.MSG_PETMORPHREADY>(packets[1]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AnOrphanedWaitingMorphRejoinsTheDiscoverableLobbyOrKeepsItsOfferOnRefusal(bool fullCandidate) {
        using var f = await Fixture.Create(); await f.Join(Morph);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Morph, Data = $"set:side=0;id={PetId}" });
        var old = await f.State(); Assert.Equal(PetId, old.MorphPet); await f.Drain();
        var discoverable = f.ReplaceRegisteredLobby(f.Store.Live.Zone, fullCandidate);
        await f.Join(Morph); var after = await f.State();
        var response = Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>());
        if (fullCandidate) {
            Assert.Equal(0, response.Success); Assert.Same(old.Lobby, after.Lobby); Assert.Equal(PetId, after.MorphPet);
            Assert.NotNull(Sides(old.Lobby!).GetValue(old.Side));
        }
        else {
            Assert.Equal(1, response.Success); Assert.Same(discoverable, after.Lobby); Assert.Equal(1, after.Side);
            Assert.Equal(0UL, after.MorphPet); Assert.Null(Sides(old.Lobby!).GetValue(old.Side));
            Assert.NotNull(Sides(discoverable).GetValue(after.Side));
            // A full pair is deliberately no longer registered; its valid association can still replay.
            await f.Join(Morph); Assert.Same(discoverable, (await f.State()).Lobby);
        }
    }

    private static Array Sides(object lobby) => (Array)lobby.GetType().GetField("Sides")!.GetValue(lobby)!;
    private sealed record Ready;
    private sealed record Inspect;
    private sealed record Send(object Message);
    private sealed record State(object? Session, string? Game, bool Started, bool Ended, string? Current, int Round, int Successes,
        object? Timer, object? Lobby, int Side, ulong MorphPet);

    private sealed class Fixture : IDisposable {
        internal readonly TerminalClaimFixture Store = new();
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        private readonly ActorSystem _system;
        private IActorRef _service = null!, _endpoint = null!, _socket = null!;
        private readonly FieldInfo _gamesField = typeof(PetGameConfigs).GetField("s_games", Static)!;
        private readonly object? _oldGames, _oldLazyValue, _oldLazyState;
        private readonly object _lazy;
        private readonly FieldInfo _lazyValue, _lazyState;
        private readonly IDictionary<ulong, CoreTemplate> _templates;
        private readonly CoreTemplate? _oldPet;
        private readonly FieldInfo _talents = typeof(PetProgress).GetField("s_talentNames", Static)!;
        private readonly object? _oldTalents;
        private readonly List<string> _fullKeys = [];
        private readonly List<string> _morphKeys = [];
        private Fixture() {
            TerminalClaimFixture.SetRules(ClassicRules.Stock);
            _oldGames = _gamesField.GetValue(null);
            _gamesField.SetValue(null, PetGameConfigs.KioskGames.Values.ToDictionary(game => game, game => new PetGameInfo {
                m_name = game, m_energyCosts = [], m_gameIcon = "", m_trackIcons = [], m_trackToolTips = [],
                m_trackChoices = [new() { m_name = "AuthoredTrack", m_scene = "AuthoredScene", m_gameScoreFactor = [],
                    m_modifications = [new() { m_name = "Agility", m_change = 4 }] }],
            }, StringComparer.Ordinal));
            _lazy = typeof(PetGameConfigs).BaseType!.GetField("s_instance", Static)!.GetValue(null)!;
            _lazyValue = _lazy.GetType().GetField("_value", Private)!; _lazyState = _lazy.GetType().GetField("_state", Private)!;
            _oldLazyValue = _lazyValue.GetValue(_lazy); _oldLazyState = _lazyState.GetValue(_lazy);
            _lazyValue.SetValue(_lazy, RuntimeHelpers.GetUninitializedObject(typeof(PetGameConfigs))); _lazyState.SetValue(_lazy, null);
            _templates = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", Static)!.GetValue(null)!;
            _oldPet = _templates.TryGetValue(PetTemplate, out var pet) ? pet : null;
            _oldTalents = _talents.GetValue(null); _talents.SetValue(null, new Dictionary<uint, string>());
            _templates[PetTemplate] = new WizItemTemplate { m_templateID = PetTemplate, m_adjectiveList = ["Pet"], m_school = "Fire",
                m_behaviors = [new PetItemBehaviorTemplate { m_behaviorName = "PetItemBehavior", m_Levels = [],
                    m_maxStats = Stats(200), m_startStats = Stats(1), m_talents = [], m_favoriteSnackCategories = [] }] };
            Store.Saved.Zone = "QA/PetAdmission/" + Guid.NewGuid().ToString("N");
            Store.Saved.EquipmentBehavior.EquippedItemIds = [PetId];
            Store.Saved.EquipmentBehavior.SlotList = [new EquipmentSlot { ItemId = PetId, SlotType = EquipmentSlotType.Pet }];
            Store.Items.Add(new WizClientObjectItem { m_globalID = PetId, m_characterId = Store.Saved.CharId, m_templateID = PetTemplate,
                m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 1, m_XP = 10, m_requiredXP = 125,
                    m_maxStats = Stats(200), m_currentStats = Stats(1), m_allTalents = [793004], m_expressedTalents = [] }] });
            PetProgress.EnsureInitialized(Store.Items[0]);
            Store.Live = Store.Reload(); Store.Live.EquipmentBehavior.EquippedItems = Store.Items.Select(TerminalClaimFixture.CloneItem).ToList();
            Store.Live.Account = new Account { AuthLevel = AuthLevel.QualityAssurance };
            _system = ActorSystem.Create("pet-admission-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        }
        private static List<PetStat> Stats(int amount) => PetRules.StatNames.Select(name => new PetStat { m_name = name,
            m_statID = PetProgress.StatId(name), m_value = amount }).ToList();
        internal static async Task<Fixture> Create() {
            var f = new Fixture();
            try {
                f._socket = f._system.ActorOf(Props.Create(() => new SocketProbe(f.Packets)), "socket");
                f._endpoint = f._system.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "session");
                var session = await f._endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                f._service = f._system.ActorOf(Props.CreateBy(new PetProducer(session, f.Store)), "pet");
                Assert.True(await f._service.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal Task<bool> Send(object message) => _service.Ask<bool>(new Send(message), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Fire(object message) => Send(message);
        internal Task<bool> Join(string game) {
            if (game == Morph) _morphKeys.Add(Store.Live.Zone);
            return Send(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = game, Track = "0" });
        }
        internal Task<State> State() => _service.Ask<State>(new Inspect(), Timeout, TestContext.Current.CancellationToken);
        internal async Task<IMessage[]> Drain() {
            await _endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await _socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var result = new List<IMessage>(); while (Packets.TryDequeue(out var packet)) result.Add(packet); return result.ToArray();
        }
        internal void FillLobby(string key) {
            var lobbyType = typeof(PetGameService).GetNestedType("MorphLobby", BindingFlags.NonPublic)!;
            var sideType = typeof(PetGameService).GetNestedType("MorphSide", BindingFlags.NonPublic)!;
            var lobby = Activator.CreateInstance(lobbyType, nonPublic: true)!; var sides = Sides(lobby);
            sides.SetValue(Activator.CreateInstance(sideType, nonPublic: true), 0);
            sides.SetValue(Activator.CreateInstance(sideType, nonPublic: true), 1);
            var lobbies = typeof(PetGameService).GetField("s_lobbies", Static)!.GetValue(null)!;
            Assert.True((bool)lobbies.GetType().GetMethod("TryAdd")!.Invoke(lobbies, [key, lobby])!); _fullKeys.Add(key);
        }
        internal object ReplaceRegisteredLobby(string key, bool full) {
            var lobbyType = typeof(PetGameService).GetNestedType("MorphLobby", BindingFlags.NonPublic)!;
            var sideType = typeof(PetGameService).GetNestedType("MorphSide", BindingFlags.NonPublic)!;
            var lobby = Activator.CreateInstance(lobbyType, nonPublic: true)!;
            Sides(lobby).SetValue(Activator.CreateInstance(sideType, nonPublic: true), 0);
            if (full) Sides(lobby).SetValue(Activator.CreateInstance(sideType, nonPublic: true), 1);
            var lobbies = typeof(PetGameService).GetField("s_lobbies", Static)!.GetValue(null)!;
            lobbies.GetType().GetProperty("Item")!.SetValue(lobbies, lobby, [key]);
            _fullKeys.Add(key); return lobby;
        }
        public void Dispose() {
            _system.Terminate().GetAwaiter().GetResult(); _system.Dispose();
            // Keep static lobby fixtures bounded to authored keys; no live database or game client is initialized.
            var lobbies = typeof(PetGameService).GetField("s_lobbies", Static)!.GetValue(null)!;
            var remove = lobbies.GetType().GetMethods().Single(m => m.Name == "TryRemove" && m.GetParameters().Length == 2);
            foreach (var key in _fullKeys.Concat(_morphKeys).Append(Store.Saved.Zone).Distinct()) remove.Invoke(lobbies, [key, null]);
            _gamesField.SetValue(null, _oldGames); _lazyValue.SetValue(_lazy, _oldLazyValue); _lazyState.SetValue(_lazy, _oldLazyState);
            if (_oldPet is null) _templates.Remove(PetTemplate); else _templates[PetTemplate] = _oldPet;
            _talents.SetValue(null, _oldTalents); Store.Dispose();
        }
    }

    private sealed class SocketProbe : ReceiveActor {
        public SocketProbe(ConcurrentQueue<IMessage> packets) => Receive<IMessage>(packets.Enqueue);
    }
    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
    private sealed class PetProducer(SessionActor session, TerminalClaimFixture store) : IIndirectActorProducer {
        public Type ActorType => typeof(PetGameService);
        public ActorBase Produce() {
            var actual = new PetGameService(session);
            typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(actual, store.Live);
            typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(actual, store.Live.GameObject);
            // Replace only the mailbox adapter. Native dispatch and all lifecycle handlers execute on the actual
            // sealed service with its real Context; a recording timer lets tests deliver obsolete queued callbacks.
            var driver = new PetDriver(actual, store);
            typeof(ActorBase).GetMethod("Become", Private, null, [typeof(Receive)], null)!.Invoke(actual, [new Receive(driver.Dispatch)]);
            return actual;
        }
        public void Release(ActorBase actor) { }
    }
    private sealed class PetDriver(PetGameService service, TerminalClaimFixture store) {
        private readonly PetProgressDependencies _dependencies = new() { SerializeEnd = _ => new ByteString(new byte[] { 1 }),
            SerializePet = _ => new ByteString(new byte[] { 2 }), MaxEnergy = _ => 50 };
        private PetAdmissionTimers _timers = null!;
        internal bool Dispatch(object message) {
            var sender = (IActorRef)typeof(ActorBase).GetProperty("Sender", Private | BindingFlags.Public)!.GetValue(service)!;
            try {
                if (message is Ready) {
                    service.Timers = DispatchProxy.Create<ITimerScheduler, PetAdmissionTimers>();
                    _timers = (PetAdmissionTimers)(object)service.Timers; sender.Tell(true); return true;
                }
                if (message is Inspect) { sender.Tell(Snapshot()); return true; }
                if (message is not Send send) return true;
                using var scope = EnterScope();
                var dispatch = MessageHandlerTable.DispatcherFor(typeof(PetGameService), send.Message.GetType());
                Assert.NotNull(dispatch); dispatch(service, send.Message); sender.Tell(true);
            } catch (Exception error) { sender.Tell(new Status.Failure(error)); }
            return true;
        }
        private State Snapshot() {
            var current = typeof(PetGameService).GetField("_session", Private)!.GetValue(service);
            object? Value(string field) => current?.GetType().GetField(field)!.GetValue(current);
            var dance = (DanceGame?)Value("Dance");
            var morph = typeof(PetGameService).GetField("_morph", Private)!.GetValue(service);
            var lobby = morph?.GetType().GetField("Item1")!.GetValue(morph);
            var side = morph is null ? 0 : (int)morph.GetType().GetField("Item2")!.GetValue(morph)!;
            var offered = lobby is null ? null : Sides(lobby).GetValue(side);
            return new(current, (string?)Value("Game"), Value("Started") is true, Value("Ended") is true,
                dance?.Current, dance?.Round ?? 0, dance?.Successes ?? 0, _timers.Messages.GetValueOrDefault("petDanceRound"),
                lobby, side, offered is null ? 0 : (ulong)offered.GetType().GetField("PetId")!.GetValue(offered)!);
        }
        private IDisposable EnterScope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldClaim = Imlight.CoreLib.Classic.ClassicQuestClaims.TestScope.Value;
            var oldStack = Imlight.CoreLib.Game.DropTables.ClassicStackRewards.TestScope.Value;
            var oldProgress = WizardProgressionTransactions.TestScope.Value; var oldItems = WizardInventoryTransactions.TestRowsScope.Value;
            var oldReagents = WizardReagentCollection.TestRowsScope.Value; var oldPet = ClassicPetProgressTransactions.TestScope.Value;
            store.Install(); ClassicPetProgressTransactions.TestScope.Value = _dependencies;
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = oldStore; Imlight.CoreLib.Classic.ClassicQuestClaims.TestScope.Value = oldClaim;
                Imlight.CoreLib.Game.DropTables.ClassicStackRewards.TestScope.Value = oldStack;
                WizardProgressionTransactions.TestScope.Value = oldProgress; WizardInventoryTransactions.TestRowsScope.Value = oldItems;
                WizardReagentCollection.TestRowsScope.Value = oldReagents; ClassicPetProgressTransactions.TestScope.Value = oldPet;
            });
        }
    }
    private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    public class PetAdmissionTimers : DispatchProxy {
        internal readonly Dictionary<object, object> Messages = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "StartSingleTimer" => Start(args!), "Cancel" => Cancel(args![0]!), "CancelAll" => Clear(),
            "IsTimerActive" => Messages.ContainsKey(args![0]!), "get_ActiveTimers" => Messages.Keys.ToList(),
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Start(object?[] args) { Messages[args[0]!] = args[1]!; return null; }
        private object? Cancel(object key) { Messages.Remove(key); return null; }
        private object? Clear() { Messages.Clear(); return null; }
    }
}
