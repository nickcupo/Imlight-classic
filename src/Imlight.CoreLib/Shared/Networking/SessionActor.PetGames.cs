// CLASSIC: one owner-only native pet-game logic object per game and completed scene attachment.
using System;
using System.Collections.Generic;
using System.Threading;
using Imcodec.MessageLayer;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Networking;

internal sealed record PetGameAttachContext(Wizard Wizard, CoreObject World, ulong Character, ZoneAttachContext Attach);
// SendJoinResponse: Dance answers its JOIN here; a phantom-zone game already answered it before its transfer.
internal sealed record PetGamePublication(object Token, PetGameAttachContext Context,
    PreparedPetGameObject Object, PET_9_PROTOCOL.MSG_PETGAMEINIT Init, bool SendJoinResponse = true);
internal sealed record PetGamePublicationResult(object Token, bool Accepted, bool ContextValid);
// CLASSIC: acknowledged training output still belongs to its exact completed attachment.
internal sealed record PetGameSessionOutput(object Token, PetGameAttachContext Context,
    IReadOnlyList<IMessage> Messages, CHARACTER_103_PROTOCOL.MSG_RESUMMONPET Resummon = null);
internal sealed record PetGameSessionOutputRefused(object Token, PetGameAttachContext Context);

public sealed partial class SessionActor {
    private PetGameAttachContext _petGameAttach;
    private PetGameAttachContext _danceObjectAttach;
    private readonly Dictionary<string, PreparedPetGameObject> _petGameObjects = new(StringComparer.Ordinal);
    private long _summonedPetGlobalId;

    /// <summary>CLASSIC: the world GID of the pet EquipmentService has summoned in this scene (0: none).</summary>
    internal ulong SummonedPetGlobalId {
        get => (ulong) Interlocked.Read(ref _summonedPetGlobalId);
        set => Interlocked.Exchange(ref _summonedPetGlobalId, (long) value);
    }

    private bool IsRegisteredPetGameService(IActorRef actor)
        => actor is not null && _services.TryGetValue(actor, out var service) && service is PetGameService;

    // Read-only immutable snapshots: the fresh mutation lane can recheck without mutating parent actor state.
    internal bool TryCapturePetGameAttach(Wizard wizard, out PetGameAttachContext context) {
        context = Volatile.Read(ref _petGameAttach);
        return context is not null && ReferenceEquals(context.Wizard, wizard) && MatchesPetGameAttach(context);
    }

    internal bool MatchesPetGameAttach(PetGameAttachContext expected) {
        var completed = Volatile.Read(ref _petGameAttach);
        var current = DoorAttach;
        return !IsDisposed && !TransferringOut && expected?.Wizard is { } wizard && expected.Attach is not null
            && completed is not null && ReferenceEquals(completed.Wizard, wizard)
            && ActiveWizardDirectory.TryGet(ActorRef, out var selected, out var world)
            && ReferenceEquals(selected, wizard) && world is not null && ReferenceEquals(world, expected.World)
            && ReferenceEquals(completed.World, expected.World) && completed.Character == expected.Character
            && wizard.CharId != 0 && wizard.CharId == expected.Character && wizard.GameObjectID == expected.Attach.Owner
            && world.m_globalID.Full == expected.Attach.Owner
            && MatchesPetGameScene(current, expected.Attach)
            && MatchesPetGameScene(completed.Attach, expected.Attach)
            && string.Equals(wizard.Zone, expected.Attach.Zone, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPetGameScene(ZoneAttachContext current, ZoneAttachContext expected)
        => current is not null && expected is not null && expected.Owner != 0
            && current.Owner == expected.Owner && current.Actor == expected.Actor
            && current.Generation == expected.Generation
            && string.Equals(current.Zone, expected.Zone, StringComparison.OrdinalIgnoreCase);

    private void ReceivePetGameAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (!IsDisposed && !TransferringOut && _services.TryGetValue(Sender, out var service)
            && service is AttachService && DoorAttach is { } attach
            && attach.Actor == message.ZoneActorRef && attach.Generation == message.AttachGeneration
            && ActiveWizardDirectory.TryGet(ActorRef, out var wizard, out var world) && world is not null
            && attach.Owner != 0 && attach.Owner == wizard.GameObjectID && world.m_globalID.Full == attach.Owner
            && string.Equals(wizard.Zone, attach.Zone, StringComparison.OrdinalIgnoreCase))
        {
            var completed = new PetGameAttachContext(wizard, world, wizard.CharId, attach);
            if (_petGameObjects.Count != 0 && (!ReferenceEquals(_danceObjectAttach.Wizard, wizard)
                || !ReferenceEquals(_danceObjectAttach.World, world)
                || !MatchesPetGameScene(_danceObjectAttach.Attach, attach))) {
                // A trusted new completed scene retires its former scene-owned cache. Same-scene completion
                // replay retains it; no per-game REMOVEOBJECT can race a later named logic registration.
                _petGameObjects.Clear();
                _danceObjectAttach = null;
            }
            Volatile.Write(ref _petGameAttach, completed);
        }
        // Keep the existing ZoneService and other post-attach recipients, including their original sender.
        HandleInternalTell(message);
    }

    private void ReceivePetGamePublication(PetGamePublication message) {
        if (!IsRegisteredPetGameService(Sender)) return;
        var valid = MatchesPetGameAttach(message.Context);
        var game = message.Init?.Game.ToString();
        var accepted = valid && message.Token is not null && message.Object is { GlobalId: not 0 }
            && message.Object.Data.Length != 0 && message.Object.GlobalId != message.Context.Attach.Owner
            && message.Init is not null && message.Init.Data.Length != 0
            && game is not null && PetGameObjectCodec.LogicTemplates.ContainsKey(game)
            && (message.SendJoinResponse || PetGameScenes.IsPhantomGame(game));
        if (_petGameObjects.Count != 0 && (!ReferenceEquals(_danceObjectAttach.Wizard, message.Context?.Wizard)
            || !MatchesPetGameScene(_danceObjectAttach.Attach, message.Context?.Attach))) {
            accepted = false;
            valid = false; // A retained object cannot be replaced inside a different scene on this parent.
        }
        if (!accepted) {
            Sender.Tell(new PetGamePublicationResult(message.Token, false, valid), Self);
            return;
        }

        var first = !_petGameObjects.TryGetValue(game, out var logic);
        if (first) { logic = message.Object; _petGameObjects[game] = logic; _danceObjectAttach = message.Context; }
        // Result and subsequent native pet requests share this parent sender. The service stages old callbacks
        // until this result, then commits its new training state before it can process a follow-on READY.
        Sender.Tell(new PetGamePublicationResult(message.Token, true, true), Self);
        if (message.SendJoinResponse) _socketSenderRef.Tell(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 1 }, Self);
        if (first) _socketSenderRef.Tell(new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = logic.Data }, Self);
        _socketSenderRef.Tell(message.Init, Self);
    }

    private void ReceivePetGameSessionOutput(PetGameSessionOutput message) {
        // Only the registered pet service may request this narrow contextual publication. Never widen
        // the generic client-batch route, and never close a connection merely because it is travelling.
        if (!IsRegisteredPetGameService(Sender)) return;
        if (message.Token is null || !MatchesPetGameAttach(message.Context)) {
            Sender.Tell(new PetGameSessionOutputRefused(message.Token, message.Context), Self);
            return;
        }
        foreach (var packet in message.Messages ?? []) _socketSenderRef.Tell(packet, Self);
        if (message.Resummon is not null && _dispatchTable.TryGetValue(message.Resummon.GetType(), out var handlers)) {
            foreach (var handler in handlers) if (handler != Sender) handler.Tell(message.Resummon, Self);
        }
    }

    private void RetirePetGamePublication() {
        Volatile.Write(ref _petGameAttach, null);
        _danceObjectAttach = null;
        _petGameObjects.Clear();
        // No per-game REMOVEOBJECT: client scene teardown owns the retained invisible object.
    }
}
