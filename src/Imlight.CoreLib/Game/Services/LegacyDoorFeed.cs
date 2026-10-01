using System;
using System.Collections.Generic;
using Akka.Actor;
using Imlight.CoreLib.Shared.Networking;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Quests;
namespace Imlight.CoreLib.Game.Services;

// One instance per player's QuestService; no DB state or dynamic index allocator.
internal sealed class LegacyDoorFeed {
    private string _readyZone, _zone;
    private IActorRef _readyActor, _actor;
    private ulong _owner;
    private ZoneAttachContext _current;
    private long _readyGeneration, _generation;
    internal void ObserveCurrent(ZoneAttachContext context) {
        if (_current == context) return;
        _current = context; _readyZone = null; _readyActor = null; _readyGeneration = 0;
    }
    private readonly Dictionary<string, (LegacyDoorBindings.Binding Binding, string State)> _states = new(StringComparer.Ordinal);
    internal bool Ready(string zone, IActorRef actor, long generation) {
        if (_current is null || _current.Actor != actor || _current.Generation != generation || !string.Equals(_current.Zone, zone, StringComparison.OrdinalIgnoreCase)) return false;
        _readyZone = zone; _readyActor = actor; _readyGeneration = generation; return true;
    }
    internal List<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS> Leave(string zone, IActorRef actor, long generation) {
        List<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS> packets = [];
        if (_readyActor == actor && _readyGeneration == generation && _readyZone == zone) { _readyZone = null; _readyActor = null; _readyGeneration = 0; }
        if (_zone != zone || _actor != actor || _generation != generation) return packets;
        foreach (var entry in _states.Values) packets.Add(QuestService.LegacyDoorRemove(_owner, entry.Binding, entry.State));
        _states.Clear(); _zone = null; _actor = null;
        return packets;
    }
    internal List<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS> Apply(LegacyDoorSnapshot snapshot, string actualZone, ulong owner, bool attached) {
        List<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS> packets = [];
        if (_current is null || _current.Actor != snapshot.ZoneActor || _current.Generation != snapshot.AttachGeneration || _readyGeneration != snapshot.AttachGeneration || !attached || !string.Equals(actualZone, snapshot.Zone, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_readyZone, snapshot.Zone, StringComparison.OrdinalIgnoreCase) || _readyActor != snapshot.ZoneActor) return packets;
        if (_owner != owner) { _states.Clear(); _zone = null; _actor = null; _owner = owner; }
        if (_zone != snapshot.Zone || _actor != snapshot.ZoneActor || _generation != snapshot.AttachGeneration || snapshot.Replay) {
            foreach (var entry in _states.Values) packets.Add(QuestService.LegacyDoorRemove(owner, entry.Binding, entry.State));
            _states.Clear(); _zone = snapshot.Zone; _actor = snapshot.ZoneActor; _generation = snapshot.AttachGeneration;
        }
        foreach (var decision in snapshot.States ?? []) {
            if (decision.State is null || (_states.TryGetValue(decision.Binding.Tag, out var old) && old.State == decision.State)) continue;
            if (!_states.TryGetValue(decision.Binding.Tag, out old)) {
                // Stock Remove ignores unknown indices: establish every alias once
                // before hiding it. The baseline works even after a scene reload.
                foreach (var state in new[] { "Off", "Quest", "On" }) {
                    packets.Add(QuestService.LegacyDoorPacket(owner, decision.Binding, state));
                    packets.Add(QuestService.LegacyDoorRemove(owner, decision.Binding, state));
                }
            } else packets.Add(QuestService.LegacyDoorRemove(owner, old.Binding, old.State));
            packets.Add(QuestService.LegacyDoorPacket(owner, decision.Binding, decision.State));
            _states[decision.Binding.Tag] = (decision.Binding, decision.State);
        }
        return packets;
    }
}
