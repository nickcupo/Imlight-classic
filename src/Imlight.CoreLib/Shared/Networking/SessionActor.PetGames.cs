// CLASSIC: one owner-only native pet-game logic object per game and completed scene attachment.
using System;
using System.Collections.Generic;
using System.Threading;
using Imcodec.MessageLayer;
using Imlight.Common;
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
// CLASSIC: told to the pet service when a completed attach became trusted after the fanout already passed.
internal sealed record PetGameSceneTrusted(long Generation) : IServerMessage {
    public byte MessageOrder => 142;
    public byte ServiceID => 101;
}

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

    // CLASSIC: why the completed-attachment capture failed, for the refusal log (no secrets, only identities).
    internal string DescribePetGameAttach(Wizard wizard) {
        var completed = Volatile.Read(ref _petGameAttach);
        var current = DoorAttach;
        ActiveWizardDirectory.TryGet(ActorRef, out var selected, out var world);
        return $"completed={(completed is null ? "none" : $"{completed.Attach?.Zone}#{completed.Attach?.Generation}")} " +
            $"door={(current is null ? "none" : $"{current.Zone}#{current.Generation}")} disposed={IsDisposed} transferring={TransferringOut} " +
            $"sameWizard={ReferenceEquals(completed?.Wizard, wizard)} selectedSame={ReferenceEquals(selected, wizard)} " +
            $"sameWorld={ReferenceEquals(completed?.World, world)} zone={wizard?.Zone}";
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

    // CLASSIC: the zone answers MSG_ADDPLAYER to AttachService and WizardService in parallel, so the directory's world
    // object can still be missing (first attach of this SessionActor) when ATTACHCOMPLETE reaches the parent. The trust
    // decision waits briefly for exactly that object; it never trusts a different scene, sender or owner.
    internal sealed record PetGameAttachRetry(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE Message, int Attempt);
    private const int PetGameAttachRetries = 40;
    private static readonly TimeSpan s_petGameAttachRetry = TimeSpan.FromMilliseconds(50);

    private void ReceivePetGameAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (_services.TryGetValue(Sender, out var service) && service is AttachService) {
            TryCompletePetGameAttach(message, 0);
        }
        else {
            Logger.Debug("Pet games: attach completion from a non-attach sender ignored for session {0}.", Logger.Args(SessionID));
        }
        // Keep the existing ZoneService and other post-attach recipients, including their original sender. Only once:
        // a retry below re-evaluates the pet-game trust alone.
        HandleInternalTell(message);
    }

    private void ReceivePetGameAttachRetry(PetGameAttachRetry retry) => TryCompletePetGameAttach(retry.Message, retry.Attempt);

    private void TryCompletePetGameAttach(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message, int attempt) {
        if (IsDisposed || TransferringOut || DoorAttach is not { } attach || attach.Actor != message.ZoneActorRef
            || attach.Generation != message.AttachGeneration || !ActiveWizardDirectory.TryGet(ActorRef, out var wizard, out var world)
            || wizard is null || attach.Owner == 0 || attach.Owner != wizard.GameObjectID
            || !string.Equals(wizard.Zone, attach.Zone, StringComparison.OrdinalIgnoreCase)) {
            var door = DoorAttach;
            ActiveWizardDirectory.TryGet(ActorRef, out var w, out _);
            Logger.Debug("Pet games: attach completion not trusted for session {0}: door={1} sameActor={2} generation={3}/{4} " +
                "owner={5}/{6} zone={7}/{8}", Logger.Args(SessionID, door is not null, door?.Actor == message.ZoneActorRef,
                    door?.Generation, message.AttachGeneration, door?.Owner, w?.GameObjectID, w?.Zone, door?.Zone));
            return;
        }
        if (world is null || world.m_globalID.Full != attach.Owner) {
            // WizardService has not recorded this attach's world object yet.
            if (attempt < PetGameAttachRetries) {
                Context.System.Scheduler.ScheduleTellOnce(s_petGameAttachRetry, Self, new PetGameAttachRetry(message, attempt + 1), Self);
            }
            else {
                Logger.Warning("Pet games: session {0} never saw its world object for attach generation {1}.",
                    Logger.Args(SessionID, message.AttachGeneration));
            }
            return;
        }

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
        if (attempt != 0) {
            Logger.Debug("Pet games: session {0} trusted attach generation {1} after {2} wait(s) for its world object.",
                Logger.Args(SessionID, message.AttachGeneration, attempt));
            // A pet service that already handled the fanout re-checks its arrival now that the scene is trusted.
            HandleInternalTell(new PetGameSceneTrusted(message.AttachGeneration));
        }
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
