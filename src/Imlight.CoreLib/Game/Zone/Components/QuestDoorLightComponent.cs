using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed record DoorLightQuery(string Tag, IActorRef Player, CoreObject PlayerObject, Wizard Wizard, IActorRef ReplyTo);
internal sealed class DoorLightRefresh : IServerMessage {
    // In-process only: never serialized or registered as a wire message.
    public byte MessageOrder => 0;
    public byte ServiceID => 0;
    public IActorRef Player;
    public Wizard Wizard;
}
internal sealed record DoorLightDecision(IActorRef Player, string State);

/// <summary>Only data-bound lights are evaluated. No guessed proximity/name bindings.</summary>
internal sealed class QuestDoorLightComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {
    internal const uint TemplateId = 741306;
    public ITimerScheduler Timers { get; set; }
    private sealed record InitialRefresh(IActorRef Player);
    private readonly Dictionary<IActorRef, (CoreObject Object, Wizard Wizard)> _players = [];
    private readonly Dictionary<IActorRef, string> _sent = [];
    private readonly HashSet<IActorRef> _unbound = [];
    private ActorSelection _triggers;
    private bool _warned;

    public static bool IsLight(CoreTemplate template) => template is GameObjectTemplate go
        && go.m_templateID == TemplateId && go.m_objectName == "WC-QuestLight";
    public static bool ShouldAttachToEntity(CoreTemplate template) => ClassicQuestEngine.IsActive && IsLight(template);
    public override void OnStart() {
        _triggers = Entity.SelectZoneChild("ZoneTriggerSupervisor");
    }
    public override void OnPlayerJoin(CoreObject player, IActorRef actor, Wizard wizard) {
        _players[actor] = (player, wizard);
        _sent.Remove(actor);
        _unbound.Remove(actor);
        Timers.StartSingleTimer(actor, new InitialRefresh(actor), TimeSpan.FromMilliseconds(1));
    }
    public override void OnPlayerLeave(IActorRef actor, ulong id) {
        _players.Remove(actor); _sent.Remove(actor); _unbound.Remove(actor);
        Timers.Cancel(actor);
    }
    internal void ReplayFor(IActorRef actor) {
        if (_sent.TryGetValue(actor, out var state)) Entity.ChangeStateExclusiveSender(state, actor);
        // A fresh decision after render replay must win over persisted state.
        _sent.Remove(actor);
        if (_players.ContainsKey(actor)) Timers.StartSingleTimer(actor, new InitialRefresh(actor), TimeSpan.FromMilliseconds(1));
    }
    private void Query(IActorRef actor, CoreObject player) {
        if (_unbound.Contains(actor) || string.IsNullOrEmpty(Entity.Info?.m_zoneTag)) return;
        _triggers?.Tell(new DoorLightQuery(Entity.Info.m_zoneTag, actor, player, _players[actor].Wizard, ActorRef));
    }
    public override void OnPlayerMove(CoreObject player, IActorRef actor, Wizard wizard) {
        if (!_players.ContainsKey(actor)) return;
        _players[actor] = (player, wizard);
        // Movement changes cached context only; quest/activation events refresh eligibility.
    }
    [MessageHandler(typeof(InitialRefresh))]
    private void Initial(InitialRefresh message) {
        if (_players.TryGetValue(message.Player, out var player)) Query(message.Player, player.Object);
    }
    [MessageHandler(typeof(DoorLightRefresh))]
    private void Refresh(DoorLightRefresh message) {
        foreach (var (actor, player) in _players) {
            if (message.Player is not null && actor != message.Player) continue;
            if (message.Wizard is not null) _players[actor] = (player.Object, message.Wizard);
            Query(actor, player.Object);
        }
    }
    [MessageHandler(typeof(DoorLightDecision))]
    private void Decide(DoorLightDecision decision) {
        if (!_players.ContainsKey(decision.Player)) return;
        if (decision.State is null) {
            _unbound.Add(decision.Player);
            if (!_warned) {
                _warned = true;
                Logger.Warning("Door light {0} in {1} has no unambiguous trigger binding; preserving placed state.", Logger.Args(Entity.Info?.m_zoneTag, Zone.ZonePath));
            }
            return;
        }
        if (_sent.TryGetValue(decision.Player, out var previous) && previous == decision.State) return;
        // Send a visual state directly: Off must not flow through generic dynamod despawn.
        Entity.ChangeStateExclusiveSender(decision.State, decision.Player);
        _sent[decision.Player] = decision.State;
    }
}
