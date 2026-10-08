// CLASSIC: actual pet service handlers bind native Game identities and retire only successfully replaced games.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.ObjectProperty;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Classic;
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
        var packets = await f.Drain(); Assert.Equal(3, packets.Length);
        Assert.Equal(1, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(packets[0]).Success);
        Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(packets[1]);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(packets[2]);
        Assert.Equal(needsInitialization ? 1 : 0, f.Store.Saves);
        Assert.Same(alias, Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems));
        Assert.Equal(125u, PetProgress.Behavior(alias).m_requiredXP);
        Assert.Equal(125u, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP);
    }

    [Fact]
    public async Task FirstDancePublishesRealNativeMappedLogicBeforeInitAndRepeatsReuseIt() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        var first = await f.Drain(); Assert.Equal(3, first.Length);
        Assert.Equal(1, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(first[0]).Success);
        var packet = Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(first[1]);
        var raw = (byte[])packet.Data;
        // Independently inspect the native mapped class/template envelope, not only a symmetric codec read.
        Assert.Equal((byte)115, raw[0]); Assert.Equal((byte)9, raw[1]);
        Assert.Equal((uint)PetGameObjectCodec.DanceTemplate, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(2, 4)));
        var codec = ClassicCoreObjectSerializer.Create(false, SerializerFlags.None);
        Assert.True(codec.Deserialize<WizClientObjectItem>(raw, 28, out var logic));
        Assert.NotNull(logic); Assert.NotEqual(0UL, logic.m_globalID.Full);
        Assert.NotEqual(f.Store.Live.GameObjectID, logic.m_globalID.Full);
        var behavior = Assert.IsType<ClientPetGameBehavior>(Assert.Single(logic.m_inactiveBehaviors));
        // BehaviorInstance's name-ID has native flags39 and is omitted by mask28. The inherited template
        // pointer supplies gameName to220bfe0; preserve the native mapped item/template and class header.
        Assert.Equal(0u, behavior.m_behaviorTemplateNameID);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(6, 4)));
        Assert.Equal((byte)0, raw[10]); Assert.Equal((byte)0, raw[11]);
        Assert.Equal(1483251235u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4)));
        Assert.Equal(logic.m_globalID.Full, BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(16, 8)));
        var allocated = CoreObjectFactory.InitializeCoreObjectBehaviors(new WizClientObjectItem(), AuthoredDanceTemplate());
        Assert.Equal(PetGameObjectCodec.BehaviorNameId, Assert.IsType<ClientPetGameBehavior>(Assert.Single(allocated.m_inactiveBehaviors)).m_behaviorTemplateNameID);
        var decoded = Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(packet))!));
        Assert.Equal(raw, (byte[])decoded.Data);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(first[2]);
        foreach (var next in new[] { Dance, "PetGameDrop", Dance, Morph, Dance }) {
            await f.Join(next); var replay = await f.Drain();
            Assert.DoesNotContain(replay, item => item is GAME_5_PROTOCOL.MSG_NEWOBJECT or GAME_5_PROTOCOL.MSG_REMOVEOBJECT);
            Assert.Equal(1, Assert.Single(replay.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        }
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = Dance }); await f.Drain();
        await f.Join(Dance);
        Assert.DoesNotContain(await f.Drain(), item => item is GAME_5_PROTOCOL.MSG_NEWOBJECT or GAME_5_PROTOCOL.MSG_REMOVEOBJECT);
        Assert.Equal(0, f.Store.Saves);
    }

    [Theory]
    [InlineData("class")] [InlineData("template")] [InlineData("name")]
    [InlineData("game")] [InlineData("missing")] [InlineData("extra")]
    public async Task InvalidNativeDanceTemplateRefusesBeforeFreshReadAndPreservesTheStartedGame(string invalid) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        var template = AuthoredDanceTemplate(); var behavior = (PetGameBehaviorTemplate)template.m_behaviors[0];
        switch (invalid) {
            case "class": f.ReplaceDanceTemplate(new GameObjectTemplate { m_templateID = PetGameObjectCodec.DanceTemplate, m_behaviors = template.m_behaviors }); break;
            case "template": template.m_templateID = PetGameObjectCodec.DanceTemplate + 1; f.ReplaceDanceTemplate(template); break;
            case "name": behavior.m_behaviorName = "WrongBehavior"; f.ReplaceDanceTemplate(template); break;
            case "game": behavior.m_gameName = "PetGameMaze"; f.ReplaceDanceTemplate(template); break;
            case "missing": template.m_behaviors = []; f.ReplaceDanceTemplate(template); break;
            case "extra": template.m_behaviors.Add(new RenderBehaviorTemplate { m_behaviorName = "RenderBehavior" }); f.ReplaceDanceTemplate(template); break;
        }
        PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        var opened = f.Store.Opened; await f.Join(Dance);
        Assert.Equal(opened, f.Store.Opened); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain(); Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer);
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task ContextReplacementDuringFreshOpenRefusesWithoutAnInitializationSave(bool initialize, bool replaceWizard) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        if (initialize) PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        f.BeforeOpen = () => {
            if (replaceWizard) ActiveWizardDirectory.SetWizard(f.Endpoint, f.Store.Reload());
            else f.Instance.PublishDoorAttach(f.Attach with { Generation = f.Attach.Generation + 1 });
        };
        await f.Join(Dance); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain(); Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var after = await f.State(); Assert.Same(old.Session, after.Session); Assert.Same(old.Timer, after.Timer);
        ActiveWizardDirectory.SetWizard(f.Endpoint, f.Store.Live); f.Instance.PublishDoorAttach(f.Attach);
        await f.Fire(old.Timer!); Assert.NotNull((await f.State()).Current);
    }

    [Fact]
    public async Task SameWizardCharacterMutationDuringFreshOpenCannotUseTheCapturedAttachment() {
        using var f = await Fixture.Create(); var character = f.Store.Live.CharId;
        f.LoadByDurableIdentity = true;
        PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        f.BeforeOpen = () => f.Store.Live.CharId = character + 1;
        await f.Join(Dance); Assert.Equal(0, f.Store.Saves);
        var packets = await f.Drain(); Assert.Equal(0, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(packets, p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT);
        Assert.Null((await f.State()).Session); f.Store.Live.CharId = character;
    }

    [Fact]
    public async Task AcknowledgedInitializationAfterContextChangesIsKeptButNeverAdmitsNativeSetup() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); await f.Drain();
        PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        f.Store.OnSave = () => f.Instance.PublishDoorAttach(f.Attach with { Generation = f.Attach.Generation + 1 });
        await f.Join(Dance); Assert.Equal(1, f.Store.Saves);
        Assert.Equal(125u, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Store.Live));
        Assert.DoesNotContain(await f.Drain(), p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT
            || p is PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Success: 1 });
        var closed = await f.State(); Assert.Null(closed.Session); Assert.Null(closed.Timer);
        await f.ParentBarrier(); Assert.True(f.Instance.IsDisposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DanceInitializationLostAcknowledgementQuarantinesAndClosesWithoutPublishingSetup(bool durable) {
        using var f = await Fixture.Create();
        var alias = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP = 0;
        f.Store.FailSave = true; f.Store.Durable = durable;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Join(Dance));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Store.Live)); Assert.Equal(1, f.Store.Saves);
        Assert.Equal(durable ? 125u : 0u, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_requiredXP);
        Assert.Equal(125u, PetProgress.Behavior(alias).m_requiredXP);
        await f.ParentBarrier(); Assert.True(f.Instance.IsDisposed);
        var packets = await f.Drain();
        Assert.DoesNotContain(packets, p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT
            || p is PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Success: 1 });
        var closed = await f.State(); Assert.Null(closed.Session); Assert.Null(closed.Timer);
        await f.Join(Dance); Assert.Equal(1, f.Store.Saves); Assert.Empty(await f.Drain());
    }

    [Fact]
    public async Task OldQueuedRoundAndNewReadyWaitForThePublicationResultWithoutSendingOldDataAfterInit() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); var old = await f.State(); await f.Drain();
        f.HoldResult = true;
        await f.BeginSend(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = Dance, Track = "0" });
        await f.Drain(); await f.State(); Assert.Single(f.HeldResults);
        var round = f.BeginSend(old.Timer!); var ready = f.BeginSend(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        var pending = await f.State(); Assert.Same(old.Session, pending.Session); Assert.Same(old.Timer, pending.Timer);
        Assert.Empty(await f.Drain()); Assert.False(round.IsCompleted); Assert.False(ready.IsCompleted);
        await f.ReleaseResults(); Assert.True(await round); Assert.True(await ready);
        var replaced = await f.State(); Assert.NotSame(old.Session, replaced.Session); Assert.True(replaced.Started);
        Assert.Null(replaced.Current); Assert.NotSame(old.Timer, replaced.Timer);
        var packets = await f.Drain(); Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMESTART>());
        Assert.DoesNotContain(packets, p => p is PET_9_PROTOCOL.MSG_PETGAMEDANCE);
    }

    [Fact]
    public async Task CloseBypassesThePendingPublicationAndLateResultCannotReopenTraining() {
        using var f = await Fixture.Create(); f.HoldResult = true;
        await f.BeginSend(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = Dance, Track = "0" });
        await f.Drain(); await f.State(); Assert.Single(f.HeldResults);
        await f.PreDispose(); Assert.Null((await f.State()).Session);
        await f.ReleaseResults(); await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        Assert.Empty(await f.Drain()); Assert.Null((await f.State()).Timer);
    }

    [Fact]
    public async Task DuplicateTrustedCompletionReusesLogicAndANewCompletedGenerationPublishesAFreshObject() {
        using var f = await Fixture.Create(); await f.Join(Dance);
        var first = Assert.Single((await f.Drain()).OfType<GAME_5_PROTOCOL.MSG_NEWOBJECT>());
        await f.CompleteAttach(f.Attach); await f.Join(Dance);
        Assert.DoesNotContain(await f.Drain(), p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT);
        await f.CompleteAttach(f.Attach with { Generation = f.Attach.Generation + 1 }); await f.Join(Dance);
        var next = await f.Drain(); Assert.Equal(3, next.Length);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(next[0]);
        var created = Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(next[1]);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(next[2]);
        Assert.NotEqual((byte[])first.Data, (byte[])created.Data);
        Assert.DoesNotContain(next, p => p is GAME_5_PROTOCOL.MSG_REMOVEOBJECT);
    }

    [Fact]
    public async Task UntrustedAndStaleAttachCompletionsCannotSeedSetupAndTrustedFanoutKeepsItsSender() {
        using var f = await Fixture.Create(); await f.Join(Dance); await f.Drain();
        var next = f.Attach with { Generation = f.Attach.Generation + 1 };
        f.Instance.PublishDoorAttach(next);
        f.Endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE { AttachGeneration = next.Generation, ZoneActorRef = next.Actor }, f.Service);
        await f.ParentBarrier(); Assert.False(f.Instance.TryCapturePetGameAttach(f.Store.Live, out _));
        f.Endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE { AttachGeneration = f.Attach.Generation, ZoneActorRef = f.Attach.Actor }, f.AttachSender);
        await f.ParentBarrier(); Assert.False(f.Instance.TryCapturePetGameAttach(f.Store.Live, out _));
        await f.Join(Dance); Assert.Equal(0, Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        await f.CompleteAttach(next);
        Assert.True(f.Instance.TryCapturePetGameAttach(f.Store.Live, out var captured)); Assert.Equal(next.Generation, captured.Attach.Generation);
        await f.FanoutBarrier(); Assert.Equal(f.AttachSender, f.AttachFanout.Last());
        await f.Join(Dance); Assert.Single((await f.Drain()).OfType<GAME_5_PROTOCOL.MSG_NEWOBJECT>());
    }

    [Fact]
    public async Task AnUnauthorizedSiblingCannotPublishOrPopulateTheOwnerDanceCache() {
        using var f = await Fixture.Create();
        f.Endpoint.Tell(f.PreparedPublication(), f.AttachSender);
        await f.ParentBarrier(); Assert.Empty(await f.Drain()); Assert.Equal(0, f.Store.Opened);
        await f.Join(Dance); var first = await f.Drain();
        Assert.Single(first.OfType<GAME_5_PROTOCOL.MSG_NEWOBJECT>()); Assert.Equal(3, first.Length);
    }

    [Fact]
    public async Task ForgedPublicationResultsCannotCommitOrReleaseThePendingStage() {
        using var f = await Fixture.Create(); f.HoldResult = true;
        await f.BeginSend(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = Dance, Track = "0" });
        await f.Drain(); await f.State(); var genuine = Assert.Single(f.HeldResults);
        f.HoldResult = false;
        f.Service.Tell(genuine, f.AttachSender); // Correct token, unauthorized sibling sender.
        f.Service.Tell(genuine with { Token = new object() }, f.Endpoint); // Correct parent, wrong operation.
        var ready = f.BeginSend(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        Assert.Null((await f.State()).Session); Assert.False(ready.IsCompleted); Assert.Empty(await f.Drain());
        await f.ReleaseResults(); Assert.True(await ready); Assert.True((await f.State()).Started);
    }

    [Fact]
    public async Task RealParentNativeIngressRoutesJoinReadyAndResultWithTheParentSender() {
        using var f = await Fixture.Create();
        await f.NativeSend(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = Dance, Track = "0" });
        Assert.NotNull((await f.State()).Session);
        await f.NativeSend(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); Assert.True((await f.State()).Started);
        Assert.Contains(f.Ingress, item => item.Type == typeof(PET_9_PROTOCOL.MSG_PETGAMEJOIN) && item.Sender == f.Endpoint);
        Assert.Contains(f.Ingress, item => item.Type == typeof(PET_9_PROTOCOL.MSG_PETGAMEREADY) && item.Sender == f.Endpoint);
        Assert.Contains(f.Ingress, item => item.Type == typeof(PetGamePublicationResult) && item.Sender == f.Endpoint);
        var packets = await f.Drain(); Assert.Equal(4, packets.Length);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(packets[0]); Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(packets[1]);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(packets[2]); Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESTART>(packets[3]);
        Assert.All(f.WireSenders, sender => Assert.Equal(f.Endpoint, sender));
    }

    [Fact]
    public async Task RuntimeRestartOfTheRegisteredServiceReusesTheRetainedParentLogicObject() {
        using var f = await Fixture.Create(); await f.Join(Dance); await f.Drain();
        await f.RestartService(); Assert.Null((await f.State()).Session);
        await f.Join(Dance); var replay = await f.Drain(); Assert.Equal(2, replay.Length);
        Assert.Equal(1, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(replay[0]).Success);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(replay[1]);
        Assert.DoesNotContain(replay, p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or GAME_5_PROTOCOL.MSG_REMOVEOBJECT);
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
    [InlineData(true)] [InlineData(false)]
    public async Task NormalTerminalDancePublishesOneAcknowledgedRewardAndRetainsThePostEndSnack(bool allCorrect) {
        using var f = await Fixture.Create(); f.AddSnack();
        var final = await f.PrepareFinalDanceAnswer(allCorrect);
        var pet = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        var xp = PetProgress.Behavior(pet).m_XP; var energy = f.Store.Saved.PetOwnerBehavior.Energy;
        f.Store.OnSave = () => {
            Assert.Equal(xp, PetProgress.Behavior(pet).m_XP); Assert.Equal(energy, f.Store.Live.PetOwnerBehavior.Energy);
            Assert.Empty(f.Packets); // Preparation cannot publish a result, cost or growth before the save ACK.
        };
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = final.Answer });
        var ended = await f.State(); Assert.True(ended.Ended); Assert.Null(ended.Timer); Assert.Null(ended.Current);
        Assert.Equal(allCorrect ? 5 : 3, ended.Round); Assert.Equal(allCorrect ? 5 : 0, ended.Successes);
        Assert.Equal(1, f.Store.Saves); Assert.False(f.Instance.IsDisposed);
        var packets = await f.Drain(); Assert.Equal(4, packets.Length);
        Assert.Equal(energy - 2, Assert.IsType<PET_9_PROTOCOL.MSG_PETENERGYTICK>(packets[0]).Energy);
        Assert.Equal(Dance, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEEND>(packets[1]).Game.ToString());
        Assert.Equal(allCorrect ? 8u : 0u, Assert.IsType<WIZARD2_53_PROTOCOL.MSG_GAINPETXP>(packets[2]).XP);
        Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM>(packets[3]);
        Assert.Equal(allCorrect ? 5 : 0, f.PreparedEnd!.m_Score);
        Assert.Equal(allCorrect ? 5u : 0u, f.PreparedEnd.m_wins);
        Assert.Equal(allCorrect ? 8u : 0u, f.PreparedEnd.m_xpGain);
        Assert.Equal(allCorrect ? 4u : 0u, Assert.Single(f.PreparedEnd.m_statMods.m_modifications).m_actualChange);
        Assert.Equal(xp + (allCorrect ? 8u : 0u), PetProgress.Behavior(pet).m_XP);
        Assert.Equal(PetProgress.Behavior(pet).m_XP, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_XP);
        Assert.Equal(energy - 2, f.Store.Live.PetOwnerBehavior.Energy); Assert.Equal(energy - 2, f.Store.Saved.PetOwnerBehavior.Energy);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = final.Answer });
        await f.Fire(final.Timer); await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        Assert.Empty(await f.Drain()); Assert.Equal(1, f.Store.Saves);
        f.Store.OnSave = null;
        byte[] snackData = [4, ..BitConverter.GetBytes(SnackId)];
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(snackData) });
        var snack = await f.Drain(); Assert.Equal(4, snack.Length); Assert.Equal(2, f.Store.Saves);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETSNACKUPDATE>(snack[0]);
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDSUCCESS>(snack[1]);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_GAINPETXP>(snack[2]);
        Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM>(snack[3]);
        Assert.Equal(1, Assert.Single(f.Snacks).m_quantity); Assert.Equal(energy - 2, f.Store.Saved.PetOwnerBehavior.Energy);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(snackData) });
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDFAILED>(Assert.Single(await f.Drain())); Assert.Equal(2, f.Store.Saves);
    }

    [Theory]
    [InlineData("missing-pet", true)] [InlineData("foreign-pet", false)]
    [InlineData("empty-end", true)] [InlineData("throw-end", false)]
    [InlineData("empty-pet", false)] [InlineData("throw-pet", true)]
    [InlineData("missing-wizard", true)] [InlineData("missing-config", false)]
    public async Task NormalTerminalDanceRefusalClosesWithoutSavingOrPublishingAndCannotReopen(string failure, bool allCorrect) {
        using var f = await Fixture.Create(); f.AddSnack();
        var final = await f.PrepareFinalDanceAnswer(allCorrect);
        var pet = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        var xp = PetProgress.Behavior(pet).m_XP; var energy = f.Store.Saved.PetOwnerBehavior.Energy;
        var opened = f.Store.Opened;
        switch (failure) {
            case "missing-pet": f.BeforeOpen = () => f.Store.Items.Clear(); break;
            case "foreign-pet": f.BeforeOpen = () => f.Store.Items[0].m_characterId = f.Store.Saved.CharId + 1; break;
            case "missing-wizard": f.Store.MissingWizard = true; break;
            case "missing-config": f.RemoveGameConfiguration(Dance); break;
            case "empty-end": f.ProgressDependencies.SerializeEnd = _ => default; break;
            case "throw-end": f.ProgressDependencies.SerializeEnd = _ => throw new InvalidOperationException("Authored END preparation failure"); break;
            case "empty-pet": f.ProgressDependencies.SerializePet = _ => default; break;
            case "throw-pet": f.ProgressDependencies.SerializePet = _ => throw new InvalidOperationException("Authored pet preparation failure"); break;
            default: throw new InvalidOperationException("Unknown authored refusal");
        }
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = final.Answer });
        Assert.True(f.Instance.IsDisposed);
        var closed = await f.State(); Assert.Null(closed.Session); Assert.Null(closed.Timer);
        Assert.Equal(failure == "missing-config" ? opened : opened + 1, f.Store.Opened);
        Assert.Equal(0, f.Store.Saves); Assert.Empty(await f.Drain());
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Store.Live));
        Assert.Equal(xp, PetProgress.Behavior(pet).m_XP); Assert.Equal(energy, f.Store.Live.PetOwnerBehavior.Energy);
        Assert.Equal(energy, f.Store.Saved.PetOwnerBehavior.Energy); Assert.Equal(2, Assert.Single(f.Snacks).m_quantity);
        if (f.Store.Items.Count != 0) Assert.Equal(xp, PetProgress.Behavior(Assert.Single(f.Store.Items)).m_XP);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); await f.Join(Dance); await f.Join(Morph);
        await f.Fire(final.Timer); await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = final.Answer });
        byte[] snackData = [4, ..BitConverter.GetBytes(SnackId)];
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(snackData) });
        Assert.Equal(0, f.Store.Saves); Assert.Empty(await f.Drain());
        var after = await f.State(); Assert.Null(after.Session); Assert.Null(after.Timer); Assert.Null(after.Lobby);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NormalTerminalDanceLostAcknowledgementQuarantinesAndClosesWithoutAResultOrRetry(bool durable) {
        using var f = await Fixture.Create(); var final = await f.PrepareFinalDanceAnswer(allCorrect: true);
        var pet = Assert.Single(f.Store.Live.EquipmentBehavior.EquippedItems);
        var xp = PetProgress.Behavior(pet).m_XP; var energy = f.Store.Saved.PetOwnerBehavior.Energy;
        f.Store.FailSave = true; f.Store.Durable = durable;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = final.Answer }));
        await f.ParentBarrier(); Assert.True(f.Instance.IsDisposed); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Store.Live));
        Assert.Equal(1, f.Store.Saves); Assert.Empty(await f.Drain());
        Assert.Equal(xp, PetProgress.Behavior(pet).m_XP); Assert.Equal(energy, f.Store.Live.PetOwnerBehavior.Energy);
        Assert.Equal(xp + (durable ? 8u : 0u), PetProgress.Behavior(Assert.Single(f.Store.Items)).m_XP);
        Assert.Equal(energy - (durable ? 2 : 0), f.Store.Saved.PetOwnerBehavior.Energy);
        var closed = await f.State(); Assert.Null(closed.Session); Assert.Null(closed.Timer);
        await f.Fire(final.Timer); await f.Join(Dance); await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        Assert.Equal(1, f.Store.Saves); Assert.Empty(await f.Drain());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreterminalQaFinishRefusalPreservesItsGameAndAnyOutstandingDance(bool started) {
        using var f = await Fixture.Create(); await f.Join(Dance);
        if (started) { await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); await f.Fire((await f.State()).Timer!); }
        await f.Drain(); var old = await f.State();
        f.ProgressDependencies.SerializeEnd = _ => default;
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Dance, Data = new ByteString(new byte[] { 1 }) });
        var refused = await f.State(); Assert.Same(old.Session, refused.Session); Assert.Same(old.Timer, refused.Timer);
        Assert.Equal(old.Current, refused.Current); Assert.False(refused.Ended); Assert.False(f.Instance.IsDisposed);
        Assert.Equal(0, f.Store.Saves); Assert.Empty(await f.Drain());
        if (started) {
            await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = old.Current });
            Assert.Equal(1, (await f.State()).Round); Assert.NotNull((await f.State()).Timer);
        } else {
            await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); Assert.True((await f.State()).Started);
        }
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
    private sealed record RestartPetActor;
    private sealed record State(object? Session, string? Game, bool Started, bool Ended, string? Current, int Round, int Successes,
        object? Timer, object? Lobby, int Side, ulong MorphPet);

    private sealed class Fixture : IDisposable {
        internal readonly TerminalClaimFixture Store = new();
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        internal readonly ConcurrentQueue<IActorRef> WireSenders = new(), AttachFanout = new();
        internal readonly ConcurrentQueue<(Type Type, IActorRef Sender)> Ingress = new();
        internal List<ClientPetSnackItem> Snacks = [];
        internal readonly PetProgressDependencies ProgressDependencies = new() { SerializePet = _ => new ByteString(new byte[] { 2 }), MaxEnergy = _ => 50 };
        internal PetGameEndData? PreparedEnd;
        internal System.Action? BeforeOpen;
        internal bool LoadByDurableIdentity;
        private readonly Dictionary<IDocumentSession, List<ClientPetSnackItem>> _snackRows = [];
        private readonly ActorSystem _system;
        internal IActorRef Service = null!, Endpoint = null!, AttachSender = null!;
        internal SessionActor Instance = null!;
        internal ZoneAttachContext Attach = null!;
        internal bool HoldResult;
        internal readonly ConcurrentQueue<PetGamePublicationResult> HeldResults = new();
        private IActorRef _service = null!, _endpoint = null!, _socket = null!;
        private IActorRef _fanout = null!;
        private readonly FieldInfo _gamesField = typeof(PetGameConfigs).GetField("s_games", Static)!;
        private readonly object? _oldGames, _oldLazyValue, _oldLazyState;
        private readonly object _lazy;
        private readonly FieldInfo _lazyValue, _lazyState;
        private readonly IDictionary<ulong, CoreTemplate> _templates;
        private readonly CoreTemplate? _oldPet, _oldSnack, _oldDance;
        private readonly FieldInfo _talents = typeof(PetProgress).GetField("s_talentNames", Static)!;
        private readonly object? _oldTalents;
        private readonly List<string> _fullKeys = [];
        private readonly List<string> _morphKeys = [];
        private Fixture() {
            ProgressDependencies.SerializeEnd = data => { PreparedEnd = data; return new ByteString(new byte[] { 1 }); };
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
            _oldDance = _templates.TryGetValue(PetGameObjectCodec.DanceTemplate, out var dance) ? dance : null;
            _templates[PetGameObjectCodec.DanceTemplate] = AuthoredDanceTemplate();
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
            Store.Live.GameObject = new WizClientObject { m_templateID = 1, m_inactiveBehaviors = [] };
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
        internal Wizard Load(IDocumentSession session, ulong id) {
            if (!LoadByDurableIdentity) return Store.Load(session, id);
            // This one race intentionally mutates the live alias after the row key was captured. Keep the
            // original durable row-key assertion instead of asserting the now-mutated live alias.
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(Store.Saved.CharId, id);
            Store.OnLoad?.Invoke();
            return Store.MissingWizard ? null! : ((TerminalClaimFixture.ClaimSession)(object)session).Wizard;
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
        internal void ReplaceDanceTemplate(CoreTemplate template) => _templates[PetGameObjectCodec.DanceTemplate] = template;
        internal void RemoveGameConfiguration(string game)
            => ((Dictionary<string, PetGameInfo>)_gamesField.GetValue(null)!).Remove(game);
        internal async Task<(string Answer, object Timer)> PrepareFinalDanceAnswer(bool allCorrect) {
            Store.Live.Account.AuthLevel = AuthLevel.None; // This path must complete without QA debug commands.
            await Join(Dance); await Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
            var rounds = allCorrect ? DanceGame.Rounds : DanceGame.MaxFailures;
            for (var i = 0; i < rounds - 1; i++) {
                await Fire((await State()).Timer!); var issued = await State(); Assert.NotNull(issued.Current);
                await Send(new PET_9_PROTOCOL.MSG_PETGAMEDANCE { Moves = allCorrect ? issued.Current : "x" });
            }
            var timer = (await State()).Timer!; await Fire(timer); var last = await State(); Assert.NotNull(last.Current);
            await Drain(); Assert.Equal(0, Store.Saves);
            return (allCorrect ? last.Current : "x", timer);
        }
        internal async Task CompleteAttach(ZoneAttachContext attach) {
            Attach = attach; Instance.PublishDoorAttach(attach);
            Endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE { AttachGeneration = attach.Generation,
                ZoneActorRef = attach.Actor }, AttachSender);
            await Endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await FanoutBarrier();
        }
        internal Task<ActorIdentity> FanoutBarrier() => _fanout.Ask<ActorIdentity>(new Identify("fanout"), Timeout, TestContext.Current.CancellationToken);
        internal PetGamePublication PreparedPublication() {
            Assert.True(Instance.TryCapturePetGameAttach(Store.Live, out var context));
            Assert.True(PetGameObjectCodec.TryPrepareDance(context.World, out var gameObject));
            Assert.True(PetGameConfigs.TryGet(Dance, out var info));
            Assert.True(PetGameInitializationCodec.TryPrepare(info, out var data));
            return new(new object(), context, gameObject, new() { Game = Dance, Data = data });
        }
        internal async Task NativeSend(IMessage packet) {
            Endpoint.Tell(new SERVER_100_PROTOCOL.MSG_RECEIVEDPACKET { Packet = packet }, _socket);
            await ParentBarrier(); await State(); await ParentBarrier(); await State();
        }
        internal async Task RestartService() {
            var services = (ConcurrentDictionary<IActorRef, MessageService>)typeof(SessionActor).GetField("_services", Private)!.GetValue(Instance)!;
            var before = services[_service];
            _service.Tell(new RestartPetActor());
            Assert.True(await _service.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
            Assert.NotSame(before, services[_service]);
        }
        internal static async Task<Fixture> Create() {
            var f = new Fixture();
            try {
                f._socket = f._system.ActorOf(Props.Create(() => new SocketProbe(f.Packets, f.WireSenders)), "socket");
                f._endpoint = f._system.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "session");
                var session = await f._endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                f.Instance = session; f.Endpoint = f._endpoint;
                ActiveWizardDirectory.SetWizard(f._endpoint, f.Store.Live);
                ActiveWizardDirectory.SetGameObject(f._endpoint, f.Store.Live.GameObject);
                f.AttachSender = f._system.ActorOf(Props.Create(() => new AttachProbeService(session)), "attach");
                await f.AttachSender.Ask<ActorIdentity>(new Identify("ready"), Timeout, TestContext.Current.CancellationToken);
                f.Attach = new(f.Store.Live.Zone, f._socket, 1, f.Store.Live.GameObjectID);
                f._fanout = f._system.ActorOf(Props.Create(() => new AttachFanoutProbeService(session, f.AttachFanout)), "fanout");
                await f._fanout.Ask<ActorIdentity>(new Identify("ready"), Timeout, TestContext.Current.CancellationToken);
                var routes = (Dictionary<Type, List<IActorRef>>)typeof(SessionActor).GetField("_dispatchTable", Private)!.GetValue(session)!;
                routes[typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE)] = [f._fanout];
                session.PublishDoorAttach(f.Attach);
                f._endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE {
                    AttachGeneration = f.Attach.Generation, ZoneActorRef = f.Attach.Actor }, f.AttachSender);
                await f._endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                f._service = f._system.ActorOf(Props.CreateBy(new PetProducer(session, f)), "pet");
                f.Service = f._service;
                Assert.True(await f._service.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                // Keep the real parent's routing and graceful close recipients in this scoped actor fixture.
                var order = (List<(IActorRef Ref, Type Type)>)typeof(SessionActor).GetField("_serviceOrder", Private)!.GetValue(session)!;
                order.Add((f._service, typeof(PetGameService)));
                foreach (var type in MessageHandlerTable.HandlersOf(typeof(PetGameService)).Keys) routes[type] = [f._service];
                f._endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_GETALLSERVICES());
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal Task<bool> BeginSend(object message) => _service.Ask<bool>(new Send(message), Timeout, TestContext.Current.CancellationToken);
        internal async Task<bool> Send(object message) {
            var result = await BeginSend(message);
            await ParentBarrier();
            await State(); // A guarded parent result already enqueued before the parent barrier is processed first.
            return result;
        }
        internal async Task ReleaseResults() {
            HoldResult = false;
            while (HeldResults.TryDequeue(out var result)) _service.Tell(result, _endpoint);
            await State();
        }
        internal Task<bool> Fire(object message) => Send(message);
        internal Task<SERVICE_101_PROTOCOL.MSG_PREDISPOSE> PreDispose()
            => _service.Ask<SERVICE_101_PROTOCOL.MSG_PREDISPOSE>(new Send(new SERVICE_101_PROTOCOL.MSG_PREDISPOSE()),
                Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> Join(string game) {
            if (game == Morph) _morphKeys.Add(Store.Live.Zone);
            return Send(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = game, Track = "0" });
        }
        internal Task<State> State() => _service.Ask<State>(new Inspect(), Timeout, TestContext.Current.CancellationToken);
        internal async Task ParentBarrier() {
            if (Instance.IsDisposed) return;
            try { await _endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken); }
            catch (AskTimeoutException) when (Instance.IsDisposed) { } // Closing may win the queued identity request.
        }
        internal async Task<IMessage[]> Drain() {
            await ParentBarrier();
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
            ActiveWizardDirectory.Remove(_endpoint);
            // Keep static lobby fixtures bounded to authored keys; no live database or game client is initialized.
            var lobbies = typeof(PetGameService).GetField("s_lobbies", Static)!.GetValue(null)!;
            var remove = lobbies.GetType().GetMethods().Single(m => m.Name == "TryRemove" && m.GetParameters().Length == 2);
            foreach (var key in _fullKeys.Concat(_morphKeys).Append(Store.Saved.Zone).Distinct()) remove.Invoke(lobbies, [key, null]);
            _gamesField.SetValue(null, _oldGames); _lazyValue.SetValue(_lazy, _oldLazyValue); _lazyState.SetValue(_lazy, _oldLazyState);
            if (_oldPet is null) _templates.Remove(PetTemplate); else _templates[PetTemplate] = _oldPet;
            if (_oldSnack is null) _templates.Remove(SnackTemplate); else _templates[SnackTemplate] = _oldSnack;
            if (_oldDance is null) _templates.Remove(PetGameObjectCodec.DanceTemplate); else _templates[PetGameObjectCodec.DanceTemplate] = _oldDance;
            _talents.SetValue(null, _oldTalents); Store.Dispose();
        }
    }

    private sealed class SocketProbe : ReceiveActor {
        public SocketProbe(ConcurrentQueue<IMessage> packets, ConcurrentQueue<IActorRef> senders)
            => Receive<IMessage>(packet => { senders.Enqueue(Sender); packets.Enqueue(packet); });
    }
    private sealed class AttachFanoutProbeService(SessionActor parent, ConcurrentQueue<IActorRef> seen) : MessageService(parent) {
        [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
        private void Completed(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) => seen.Enqueue(Sender);
    }
    // A real registered AttachService identity without starting a login/timeout or touching a live database.
    private sealed class AttachProbeService(SessionActor parent) : AttachService(parent) {
        protected override void PreStart() { }
    }

    private static WizItemTemplate AuthoredDanceTemplate() => new() { m_templateID = PetGameObjectCodec.DanceTemplate,
        m_displayName = "", m_behaviors = [new PetGameBehaviorTemplate {
            m_behaviorName = "PetGameBehavior", m_gameName = Dance }] };
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
        private PetAdmissionTimers _timers = null!;
        internal bool Dispatch(object message) {
            // A real runtime restart recreates the sealed service using the original producer and actor ref.
            if (message is RestartPetActor) throw new ServiceRetryException("Authored pet service retry");
            var sender = (IActorRef)typeof(ActorBase).GetProperty("Sender", Private | BindingFlags.Public)!.GetValue(service)!;
            try {
                if (message is Ready) {
                    service.Timers = DispatchProxy.Create<ITimerScheduler, PetAdmissionTimers>();
                    _timers = (PetAdmissionTimers)(object)service.Timers; sender.Tell(true); return true;
                }
                if (message is Inspect) { sender.Tell(Snapshot()); return true; }
                var actual = message is Send send ? send.Message : message;
                fixture.Ingress.Enqueue((actual.GetType(), sender));
                if (actual is PetGamePublicationResult result && fixture.HoldResult) {
                    fixture.HeldResults.Enqueue(result); return true;
                }
                // The actual service guard is also executed by this fixture's scoped mailbox adapter.
                if (service.ShouldStashForPublication(actual)) { service.Stash.Stash(); return true; }
                using var scope = EnterScope();
                var dispatch = MessageHandlerTable.DispatcherFor(typeof(PetGameService), actual.GetType());
                Assert.NotNull(dispatch); dispatch(service, actual);
                // The actual graceful-close handler replies itself, after retiring its session state.
                if (message is Send && actual is not SERVICE_101_PROTOCOL.MSG_PREDISPOSE) sender.Tell(true);
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
            fixture.Store.Install(); ClassicPetProgressTransactions.TestScope.Value = fixture.ProgressDependencies;
            WizardCollection.TestStoreScope.Value = new(fixture.Open, fixture.Load);
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
