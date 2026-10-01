using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;
internal sealed record LegacyDoorState(LegacyDoorBindings.Binding Binding, string State);
internal sealed class LegacyDoorSnapshot : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 102;
    public string Zone;
    public LegacyDoorState[] States;
    public bool Replay;
    public IActorRef ZoneActor;
    public long AttachGeneration;
}
internal sealed class LegacyDoorPlayerReady : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 102;
    public string Zone;
    public IActorRef ZoneActor;
    public long AttachGeneration;
}
internal sealed class LegacyDoorPlayerLeft : IServerMessage {
    public byte MessageOrder => 0;
    public byte ServiceID => 102;
    public string Zone;
    public IActorRef ZoneActor;
    public long AttachGeneration;
}
internal partial class QuestService {
    private readonly LegacyDoorFeed _doorFeed = new();
    [MessageHandler(typeof(LegacyDoorPlayerReady))]
    private void DoorPlayerReady(LegacyDoorPlayerReady ready) {
        _doorFeed.ObserveCurrent(SessionActor.DoorAttach);
        if (!_doorFeed.Ready(ready.Zone, ready.ZoneActor, ready.AttachGeneration)) return;
        RefreshDoorLights(); // A fresh authoritative snapshot follows MSG_NEWOBJECT.
    }
    [MessageHandler(typeof(LegacyDoorPlayerLeft))]
    private void DoorPlayerLeft(LegacyDoorPlayerLeft left) {
        _doorFeed.ObserveCurrent(SessionActor.DoorAttach);
        foreach (var packet in _doorFeed.Leave(left.Zone, left.ZoneActor, left.AttachGeneration)) SendToSocket(packet);
    }
    [MessageHandler(typeof(LegacyDoorSnapshot))]
    private void ReceiveDoorSnapshot(LegacyDoorSnapshot snapshot) {
        _doorFeed.ObserveCurrent(SessionActor.DoorAttach);
        var wizard = GetActiveWizard();
        var player = GetActiveGameObject();
        if (wizard is null || player is null) return;
        foreach (var packet in _doorFeed.Apply(snapshot, wizard.Zone, player.m_globalID,
            player.m_inactiveBehaviors?.OfType<ClientDynaModBehavior>().Any() == true)) SendToSocket(packet);
    }
    internal static int LegacyDoorIndex(LegacyDoorBindings.Binding binding, string state) => binding.Index + (state switch {
        "Off" => 0, "Quest" => 1, "On" => 2, _ => throw new ArgumentOutOfRangeException(nameof(state)),
    });
    internal static string LegacyDoorAlias(LegacyDoorBindings.Binding binding, string state) => binding.Tag + (state switch {
        "Off" => "_d", "Quest" => "_b", "On" => "_y", _ => throw new ArgumentOutOfRangeException(nameof(state)),
    });
    internal static GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS LegacyDoorRemove(ulong id, LegacyDoorBindings.Binding binding, string state) => new() {
        GlobalID = id, Remove = 1, Index = LegacyDoorIndex(binding, state), NewMod = "", AllMods = "",
    };
    internal static GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS LegacyDoorPacket(ulong id, LegacyDoorBindings.Binding binding, string state = "On") {
        var mod = new DynaMod { m_clientTag = LegacyDoorAlias(binding, state), m_index = LegacyDoorIndex(binding, state), m_originator = id,
            m_zoneName = binding.Zone };
        // Property mask 24 carries Transmit + AuthorityTransmit (tag/index/originator),
        // matching the stock nonversionable network object contract.
        if (!new ObjectSerializer(false, SerializerFlags.None).Serialize(mod, 24, out var data)) throw new InvalidOperationException("Cannot serialize legacy door mod.");
        return new GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS {
            GlobalID = id, Add = 1, Index = LegacyDoorIndex(binding, state), NewMod = data, AllMods = "",
        };
    }
}
