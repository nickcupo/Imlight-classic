using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Services;

namespace Imlight.CoreLib.Shared.Networking;

// In-process only: this wrapper is never encoded as a client packet.
internal sealed record LegacyDoorOwnerObject(ZoneAttachContext Attach, ulong Owner, GAME_5_PROTOCOL.MSG_NEWOBJECT Object);

internal sealed record LegacyDoorSocketBatch(ZoneAttachContext Attach, ulong Owner, IReadOnlyList<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS> Packets);

public sealed partial class SessionActor {
    private readonly Type[] _inProcessServices;

    // Exercise the real session receivers and service registration without a socket.
    internal SessionActor(IActorRef socketSender, params Type[] services) {
        _socketSenderRef = socketSender;
        _inProcessServices = services;
        _services = [];
        ActorRef = Context.Self;
        ServerRef = ActorRefs.Nobody;
        ConfigureReceivers();
    }

    private bool MatchesDoorAttach(ZoneAttachContext expected, ulong owner) {
        var current = DoorAttach;
        return current is not null && expected is not null && owner != 0
            && current.Actor == expected.Actor && current.Generation == expected.Generation
            && current.Owner == owner && expected.Owner == owner
            && string.Equals(current.Zone, expected.Zone, StringComparison.OrdinalIgnoreCase);
    }

    private void ReceiveLegacyDoorOwnerObject(LegacyDoorOwnerObject message) {
        if (message.Object is null || !MatchesDoorAttach(message.Attach, message.Owner)) return;
        // Only the owner object and door batches use this session as socket sender.
        // Their FIFO order does not change sender semantics for other gameplay.
        _socketSenderRef.Tell(message.Object, Self);
        HandleInternalTell(new LegacyDoorPlayerReady {
            Zone = message.Attach.Zone, ZoneActor = message.Attach.Actor, AttachGeneration = message.Attach.Generation,
            Owner = message.Owner, Replay = true,
        });
    }

    private void ReceiveLegacyDoorSocketBatch(LegacyDoorSocketBatch message) {
        if (message.Packets is null || !MatchesDoorAttach(message.Attach, message.Owner)) return;
        foreach (var packet in message.Packets)
            if (packet is null || packet.GlobalID != message.Owner) return;
        foreach (var packet in message.Packets) _socketSenderRef.Tell(packet, Self);
    }
}
