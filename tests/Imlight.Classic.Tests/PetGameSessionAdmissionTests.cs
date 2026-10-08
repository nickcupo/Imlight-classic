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
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))] // The fixture replaces shared rules and template caches.
public sealed class PetGameSessionAdmissionTests {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const string Dance = "PetGameDance", Morph = "PetGameMorph";
    private const ulong PetId = (1UL << 40) + 793002;
    private const uint PetTemplate = 793003;
    private const ulong SnackId = (1UL << 40) + 793005;
    private const uint SnackTemplate = 793006;
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
    [InlineData(false)] [InlineData(true)]
    public async Task CapturedPetMovedIntoBackpackBeforeFreshJoinReadCannotReplaceTheCurrentGame(bool needsInitialization) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        if (needsInitialization) PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        var alias = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        var before = PetProgress.Behavior(alias).m_requiredXP;
        var moved = false;
        // ReceiveJoin has captured the equipped selection before its fresh transaction opens this session.
        f.BeforeOpen = () => { f.MoveEquippedPetToBackpack(); moved = true; };
        await f.Join(Dance);
        Assert.True(moved); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain();
        Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, packet => packet is PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer); Assert.True(after.Started);
        Assert.Same(alias, Assert.Single(f.Store.Live.InventoryBehavior.Items));
        Assert.Equal(before, PetProgress.Behavior(alias).m_requiredXP);
        Assert.Equal(needsInitialization ? 0u : before, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP);
        Assert.Equal(PetId, Assert.Single(f.Store.Saved.InventoryBehavior.InventoryItemIds));
        Assert.Empty(f.Store.Saved.EquipmentBehavior.EquippedItemIds); Assert.Empty(f.Store.Live.EquipmentBehavior.EquippedItems);
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Theory]
    [InlineData("saved-missing")] [InlineData("saved-wrong")] [InlineData("saved-duplicate")] [InlineData("live-duplicate")]
    [InlineData("live-reference-missing")] [InlineData("live-reference-duplicate")]
    public async Task FreshJoinRequiresOneMatchingPersistedAndLivePetSlot(string invalidSlot) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        f.BeforeOpen = () => {
            switch (invalidSlot) {
                case "saved-missing": f.Store.Saved.EquipmentBehavior.SlotList = []; break;
                case "saved-wrong": f.Store.Saved.EquipmentBehavior.SlotList[0].ItemId = PetId + 1; break;
                case "saved-duplicate": f.Store.Saved.EquipmentBehavior.SlotList = [
                    ..f.Store.Saved.EquipmentBehavior.SlotList, new EquipmentSlot { ItemId = PetId, SlotType = EquipmentSlotType.Pet }]; break;
                case "live-duplicate": f.Store.Live.EquipmentBehavior.SlotList = [
                    ..f.Store.Live.EquipmentBehavior.SlotList, new EquipmentSlot { ItemId = PetId, SlotType = EquipmentSlotType.Pet }]; break;
                case "live-reference-missing": f.Store.Live.EquipmentBehavior.EquippedItemIds = []; break;
                case "live-reference-duplicate": f.Store.Live.EquipmentBehavior.EquippedItemIds = [PetId, PetId]; break;
            }
        };
        await f.Join(Dance); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain();
        Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, packet => packet is PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer);
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FreshJoinStillRequiresOneOwnedLivePetAlias(bool duplicateAlias) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        f.BeforeOpen = () => {
            var pet = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
            if (duplicateAlias) f.Store.Live.EquipmentBehavior.EquippedItems = [pet, TerminalClaimFixture.CloneItem(pet)];
            else pet.m_characterId = f.Store.Live.CharId + 1;
        };
        await f.Join(Dance); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain();
        Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, packet => packet is PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer);
    }

    [Theory]
    [InlineData("backpack")] [InlineData("bank")]
    public async Task FreshJoinRefusesCrossCategoryLivePetAliasesMissingFromTheIdLists(string category) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        var equipped = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        var duplicate = TerminalClaimFixture.CloneItem(equipped); Assert.NotSame(equipped, duplicate);
        Assert.Equal(PetId, duplicate.m_globalID.Full);
        f.BeforeOpen = () => {
            // Both authoritative references still name only the equipped pet; the stray materialized alias is distinct.
            Assert.DoesNotContain(PetId, f.Store.Saved.InventoryBehavior.InventoryItemIds);
            Assert.DoesNotContain(PetId, f.Store.Saved.StorageBehavior.BankItemIds);
            Assert.DoesNotContain(PetId, f.Store.Live.InventoryBehavior.InventoryItemIds);
            Assert.DoesNotContain(PetId, f.Store.Live.StorageBehavior.BankItemIds);
            if (category == "backpack") f.Store.Live.InventoryBehavior.Items = [duplicate];
            else f.Store.Live.StorageBehavior.Items = [duplicate];
        };
        await f.Join(Dance); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain();
        Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, packet => packet is PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer); Assert.True(after.Started);
        Assert.Same(equipped, Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems));
        Assert.Same(duplicate, Assert.Single(category == "backpack" ? f.Store.Live.InventoryBehavior.Items : f.Store.Live.StorageBehavior.Items));
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExactFreshEquippedPetAdmitsJoinAndPublishesOnlyAfterInitializationAcknowledgement(bool needsInitialization) {
        using var f = await Fixture.Create();
        var alias = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        if (needsInitialization) PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        f.Store.OnSave = () => { Assert.Empty(f.Packets); Assert.Equal(125u, PetProgress.Behavior(alias).m_requiredXP); };
        await f.Join(Dance); var state = await f.State(); Assert.NotNull(state.Session); Assert.False(state.Started);
        var packets = await f.Drain(); Assert.Equal(2, packets.Length);
        Assert.Equal(1, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(packets[0]).Success);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(packets[1]);
        Assert.Equal(needsInitialization ? 1 : 0, f.Store.Saves);
        Assert.Same(alias, Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems));
        Assert.Equal(125u, PetProgress.Behavior(alias).m_requiredXP);
        Assert.Equal(125u, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP);
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

    [Fact]
    public async Task MatchingPostEndSnackCommitsAndPublishesOnceWhileOtherGameDataCannotConsumeIt() {
        using var f = await Fixture.Create(); f.AddSnack(); await f.Join(Dance); await f.Drain();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 1 }) });
        Assert.True((await f.State()).Ended); Assert.Equal(1, f.Store.Saves); await f.Drain();
        var pet = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        var snack = Assert.Single(f.Store.Live.PetSnackBehavior.Snacks);
        var xp = PetProgress.Behavior(pet).m_XP; var energy = f.Store.Live.PetOwnerBehavior.Energy;
        f.Store.OnSave = () => {
            Assert.Equal(2, snack.m_quantity); Assert.Equal(xp, PetProgress.Behavior(pet).m_XP);
            Assert.Empty(f.Packets); // Native success and live aliases remain unpublished until acknowledgement.
        };
        byte[] data = [4, ..BitConverter.GetBytes(SnackId)];
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = "PetGameDrop", Data = new ByteString(data) });
        Assert.Empty(await f.Drain()); Assert.Equal(1, f.Store.Saves); Assert.Equal(2, Assert.Single(f.Snacks).m_quantity);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(data) });
        var packets = await f.Drain(); Assert.Equal(4, packets.Length); Assert.Equal(2, f.Store.Saves);
        Assert.Equal(1, Assert.IsType<PET_9_PROTOCOL.MSG_PETSNACKUPDATE>(packets[0]).Quantity);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDSUCCESS>(packets[1]);
        var gained = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_GAINPETXP>(packets[2]).XP; Assert.True(gained > 0);
        Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM>(packets[3]);
        Assert.Equal(xp + gained, PetProgress.Behavior(pet).m_XP);
        Assert.Equal(xp + gained, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_XP);
        Assert.Same(snack, Assert.Single(f.Store.Live.PetSnackBehavior.Snacks)); Assert.Equal(1, snack.m_quantity);
        Assert.Equal(1, Assert.Single(f.Snacks).m_quantity); Assert.Equal(energy, f.Store.Saved.PetOwnerBehavior.Energy);
        Assert.Equal(energy, f.Store.Live.PetOwnerBehavior.Energy);
        Assert.Equal(SnackId, Assert.Single(f.Store.Saved.PetSnackBehavior.SnackItemIds));
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(data) });
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED>(Assert.Single(await f.Drain()));
        Assert.Equal(2, f.Store.Saves); Assert.Equal(1, Assert.Single(f.Snacks).m_quantity);
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
    public async Task QueuedFormerPartnerSetAndReadyCannotUpdateAnotherLobbyOrAReplacementPeer(bool ready) {
        using var f = await Fixture.Create(); await f.Join(Morph); var old = await f.State();
        var peerType = typeof(PetGameService).GetNestedType("MorphSide", BindingFlags.NonPublic)!;
        var noticeType = typeof(PetGameService).GetNestedType("PartnerChanged", BindingFlags.NonPublic)!;
        var formerPeer = Activator.CreateInstance(peerType, nonPublic: true)!;
        peerType.GetField("PetId")!.SetValue(formerPeer, PetId + 1);
        peerType.GetField("Ready")!.SetValue(formerPeer, ready);
        Sides(old.Lobby!).SetValue(formerPeer, 1 - old.Side);
        f.Store.Live.Zone += "/replacement"; await f.Join(Morph); var replacement = await f.State();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Morph, Data = $"set:side=0;id={PetId}" });
        var currentPeer = Activator.CreateInstance(peerType, nonPublic: true)!;
        // Equal pet IDs make this an association check, rather than merely rejecting a different offer.
        peerType.GetField("PetId")!.SetValue(currentPeer, PetId + 1);
        peerType.GetField("Ready")!.SetValue(currentPeer, ready);
        Sides(replacement.Lobby!).SetValue(currentPeer, 1 - replacement.Side);
        await f.Drain();
        await f.Send(Activator.CreateInstance(noticeType, [old.Lobby!, formerPeer, PetId + 1, ready, false])!);
        Assert.Empty(await f.Drain()); Assert.Same(replacement.Lobby, (await f.State()).Lobby);
        await f.Send(Activator.CreateInstance(noticeType, [replacement.Lobby!, formerPeer, PetId + 1, ready, false])!);
        Assert.Empty(await f.Drain()); Assert.Equal(PetId, (await f.State()).MorphPet); Assert.Equal(0, f.Store.Saves);
        // A notice from the current peer still publishes the native SET/READY pair.
        await f.Send(Activator.CreateInstance(noticeType, [replacement.Lobby!, currentPeer, PetId + 1, ready, false])!);
        var packets = await f.Drain(); Assert.Equal(2, packets.Length);
        Assert.Equal(PetId + 1, Assert.IsType<PET_9_PROTOCOL.MSG_PETMORPHSET>(packets[0]).PetID);
        Assert.Equal(ready ? 1 : 0, Assert.IsType<PET_9_PROTOCOL.MSG_PETMORPHREADY>(packets[1]).Confirmed);
        Assert.Equal(0, f.Store.Saves);
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

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task GracefulSessionCloseRetiresTrainingBeforeItsAcknowledgementAndRejectsQueuedWork(bool started, bool directDispose) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        object? queued = null;
        if (started) {
            await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
            queued = (await f.State()).Timer!;
        }
        await f.Drain();
        if (directDispose) await f.Send(new SERVICE_101_PROTOCOL.MSG_DISPOSE());
        else await f.PreDispose();
        var closed = await f.State();
        Assert.Null(closed.Session); Assert.Null(closed.Timer);
        if (queued is not null) await f.Fire(queued);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = "abc" });
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 1 }) });
        await f.Join(Dance); await f.Join(Morph);
        await f.PreDispose(); // The existing close acknowledgement remains idempotent.
        var after = await f.State(); Assert.Null(after.Session); Assert.Null(after.Lobby); Assert.Null(after.Timer);
        Assert.Empty(await f.Drain()); Assert.Equal(0, f.Store.Saves);
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
        internal List<ClientPetSnackItem> Snacks = [];
        internal System.Action? BeforeOpen;
        private readonly Dictionary<IDocumentSession, List<ClientPetSnackItem>> _snackRows = [];
        private readonly ActorSystem _system;
        private IActorRef _service = null!, _endpoint = null!, _socket = null!;
        private readonly FieldInfo _gamesField = typeof(PetGameConfigs).GetField("s_games", Static)!;
        private readonly object? _oldGames, _oldLazyValue, _oldLazyState;
        private readonly object _lazy;
        private readonly FieldInfo _lazyValue, _lazyState;
        private readonly IDictionary<ulong, CoreTemplate> _templates;
        private readonly CoreTemplate? _oldPet, _oldSnack;
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
            _oldSnack = _templates.TryGetValue(SnackTemplate, out var snack) ? snack : null;
            _oldTalents = _talents.GetValue(null); _talents.SetValue(null, new Dictionary<uint, string>());
            _templates[PetTemplate] = new WizItemTemplate { m_templateID = PetTemplate, m_adjectiveList = ["Pet"], m_school = "Fire",
                m_behaviors = [new PetItemBehaviorTemplate { m_behaviorName = "PetItemBehavior", m_Levels = [],
                    m_maxStats = Stats(200), m_startStats = Stats(1), m_talents = [], m_favoriteSnackCategories = [] }] };
            Store.Saved.Zone = "QA/PetAdmission/" + Guid.NewGuid().ToString("N");
            Store.Saved.PetSnackBehavior = new() { SnackItemIds = [], Snacks = [] };
            Store.Saved.EquipmentBehavior.EquippedItemIds = [PetId];
            Store.Saved.EquipmentBehavior.SlotList = [new EquipmentSlot { ItemId = PetId, SlotType = EquipmentSlotType.Pet }];
            Store.Items.Add(new WizClientObjectItem { m_globalID = PetId, m_characterId = Store.Saved.CharId, m_templateID = PetTemplate,
                m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 1, m_XP = 10, m_requiredXP = 125,
                    m_maxStats = Stats(200), m_currentStats = Stats(1), m_allTalents = [793004], m_expressedTalents = [] }] });
            PetProgress.EnsureInitialized(Store.Items[0]);
            Store.Live = Store.Reload(); Store.Live.EquipmentBehavior.EquippedItems = [..Store.Items.Select(TerminalClaimFixture.CloneItem)];
            Store.Live.PetSnackBehavior = new() { SnackItemIds = [], Snacks = [] };
            Store.Live.Account = new Account { AuthLevel = AuthLevel.QualityAssurance };
            _system = ActorSystem.Create("pet-admission-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        }
        private static List<PetStat> Stats(int amount) => PetRules.StatNames.Select(name => new PetStat { m_name = name,
            m_statID = PetProgress.StatId(name), m_value = amount }).ToList();
        internal void AddSnack() {
            // Use the same authored native snack shape as the acknowledged pet-progress transaction tests.
            _templates[SnackTemplate] = new PetSnackItemTemplate { m_templateID = SnackTemplate, m_school = "Storm", m_adjectiveList = ["Cereal"],
                m_statModifierSet = new() { m_modifications = [new() { m_name = "Strength", m_change = 3 }, new() { m_name = "Agility", m_change = 2 }] } };
            Snacks = [new() { m_globalID = SnackId, m_characterId = Store.Saved.CharId, m_templateID = SnackTemplate, m_quantity = 2 }];
            Store.Saved.PetSnackBehavior.SnackItemIds = [SnackId]; Store.Live.PetSnackBehavior.SnackItemIds = [SnackId];
            Store.Live.PetSnackBehavior.Snacks = Snacks.Select(snack => snack with { }).ToList();
        }
        internal IDocumentSession Open() {
            var before = BeforeOpen; BeforeOpen = null; before?.Invoke();
            var session = Store.Open(); var working = (TerminalClaimFixture.ClaimSession)(object)session;
            // TerminalClaimFixture intentionally clones only quest/reward fields. This fixture owns the snack rows.
            working.Wizard.PetSnackBehavior = new() { SnackItemIds = [..Store.Saved.PetSnackBehavior.SnackItemIds], Snacks = [] };
            var rows = Snacks.Select(snack => snack with { }).ToList(); _snackRows.Add(session, rows);
            var save = working.Save;
            working.Save = () => {
                save(); // Retain the existing tracked session and acknowledged item/wizard write.
                Store.Saved.PetSnackBehavior = new() { SnackItemIds = [..working.Wizard.PetSnackBehavior.SnackItemIds], Snacks = [] };
                Snacks = rows.Select(snack => snack with { }).ToList();
            };
            return session;
        }
        internal void MoveEquippedPetToBackpack() {
            var pet = Assert.Single(Store.Live.EquipmentBehavior.EquippedItems); Assert.Equal(PetId, pet.m_globalID.Full);
            Store.Saved.EquipmentBehavior.EquippedItemIds = [..Store.Saved.EquipmentBehavior.EquippedItemIds.Where(id => id != PetId)];
            Store.Saved.EquipmentBehavior.SlotList = [..Store.Saved.EquipmentBehavior.SlotList.Where(slot => slot.SlotType != EquipmentSlotType.Pet)];
            Store.Saved.InventoryBehavior.InventoryItemIds = [..Store.Saved.InventoryBehavior.InventoryItemIds, PetId];
            Store.Live.EquipmentBehavior.EquippedItemIds = [..Store.Live.EquipmentBehavior.EquippedItemIds.Where(id => id != PetId)];
            Store.Live.EquipmentBehavior.SlotList = [..Store.Live.EquipmentBehavior.SlotList.Where(slot => slot.SlotType != EquipmentSlotType.Pet)];
            Store.Live.EquipmentBehavior.EquippedItems = [];
            Store.Live.InventoryBehavior.InventoryItemIds = [..Store.Live.InventoryBehavior.InventoryItemIds, PetId];
            Store.Live.InventoryBehavior.Items = [..Store.Live.InventoryBehavior.Items, pet];
        }
        internal List<ClientPetSnackItem> SnackRows(IDocumentSession session) => _snackRows[session];
        internal static async Task<Fixture> Create() {
            var f = new Fixture();
            try {
                f._socket = f._system.ActorOf(Props.Create(() => new SocketProbe(f.Packets)), "socket");
                f._endpoint = f._system.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "session");
                var session = await f._endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                f._service = f._system.ActorOf(Props.CreateBy(new PetProducer(session, f)), "pet");
                Assert.True(await f._service.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal Task<bool> Send(object message) => _service.Ask<bool>(new Send(message), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Fire(object message) => Send(message);
        internal Task<SERVICE_101_PROTOCOL.MSG_PREDISPOSE> PreDispose()
            => _service.Ask<SERVICE_101_PROTOCOL.MSG_PREDISPOSE>(new Send(new SERVICE_101_PROTOCOL.MSG_PREDISPOSE()),
                Timeout, TestContext.Current.CancellationToken);
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
            if (_oldSnack is null) _templates.Remove(SnackTemplate); else _templates[SnackTemplate] = _oldSnack;
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
    private sealed class PetProducer(SessionActor session, Fixture fixture) : IIndirectActorProducer {
        public Type ActorType => typeof(PetGameService);
        public ActorBase Produce() {
            var actual = new PetGameService(session);
            typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(actual, fixture.Store.Live);
            typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(actual, fixture.Store.Live.GameObject);
            // Replace only the mailbox adapter. Native dispatch and all lifecycle handlers execute on the actual
            // sealed service with its real Context; a recording timer lets tests deliver obsolete queued callbacks.
            var driver = new PetDriver(actual, fixture);
            typeof(ActorBase).GetMethod("Become", Private, null, [typeof(Receive)], null)!.Invoke(actual, [new Receive(driver.Dispatch)]);
            return actual;
        }
        public void Release(ActorBase actor) { }
    }
    private sealed class PetDriver(PetGameService service, Fixture fixture) {
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
                Assert.NotNull(dispatch); dispatch(service, send.Message);
                // The actual graceful-close handler replies itself, after retiring its session state.
                if (send.Message is not SERVICE_101_PROTOCOL.MSG_PREDISPOSE) sender.Tell(true);
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
            var oldSnacks = WizardPetSnackTransactions.TestRowsScope.Value;
            fixture.Store.Install(); ClassicPetProgressTransactions.TestScope.Value = _dependencies;
            WizardCollection.TestStoreScope.Value = new(fixture.Open, fixture.Store.Load);
            WizardPetSnackTransactions.TestRowsScope.Value = fixture.SnackRows;
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = oldStore; Imlight.CoreLib.Classic.ClassicQuestClaims.TestScope.Value = oldClaim;
                Imlight.CoreLib.Game.DropTables.ClassicStackRewards.TestScope.Value = oldStack;
                WizardProgressionTransactions.TestScope.Value = oldProgress; WizardInventoryTransactions.TestRowsScope.Value = oldItems;
                WizardReagentCollection.TestRowsScope.Value = oldReagents; ClassicPetProgressTransactions.TestScope.Value = oldPet;
                WizardPetSnackTransactions.TestRowsScope.Value = oldSnacks;
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
