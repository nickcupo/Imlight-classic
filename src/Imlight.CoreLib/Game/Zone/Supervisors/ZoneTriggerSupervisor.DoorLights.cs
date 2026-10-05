using System;
using System.Collections.Generic;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Player;
using Akka.Actor;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {
    private readonly Dictionary<IActorRef, (CoreObject Object, Wizard Wizard, long Generation)> _doorPlayers = [];
    private readonly HashSet<IActorRef> _pendingDoorRefresh = [];
    private readonly HashSet<IActorRef> _doorReplay = [];
    private sealed record RefreshLegacyDoors(IActorRef Player);
    private LegacyDoorBindings.Binding[] _legacyBindings;
    private readonly Dictionary<string, string> _lightBindingEvents = new(StringComparer.Ordinal);
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
    public override void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        foreach (var payload in message.Messages ?? []) {
            switch (payload) {
                case ZONE_102_PROTOCOL.MSG_ADDPLAYER add:
                    _doorPlayers[add.PlayerActor] = (add.PlayerObject, add.Wizard, add.AttachGeneration);
                    _doorReplay.Add(add.PlayerActor);
                    QueueLegacyDoors(add.PlayerActor);
                    break;
                case ZONE_102_PROTOCOL.MSG_REMOVEPLAYER remove:
                    if (_doorPlayers.TryGetValue(remove.PlayerActor, out var leaving) && leaving.Generation != remove.AttachGeneration) break;
                    remove.PlayerActor.Tell(new LegacyDoorPlayerLeft { Zone = Zone.ZonePath, ZoneActor = ZoneRef, AttachGeneration = remove.AttachGeneration });
                    _doorPlayers.Remove(remove.PlayerActor);
                    _pendingDoorRefresh.Remove(remove.PlayerActor);
                    _doorReplay.Remove(remove.PlayerActor);
                    Timers.Cancel(("doors", remove.PlayerActor));
                    break;
                case TriggerObjectPresenceChange presenceChange: // CLASSIC: ResRemoveTriggerObject / ResAddTriggerObject.
                    ReceiveTriggerObjectPresenceChange(presenceChange);
                    break;
                case DoorLightRefresh refresh:
                    foreach (var actor in _doorPlayers.Keys.ToArray()) {
                        if (refresh.Player is not null && actor != refresh.Player) continue;
                        if (refresh.Wizard is not null) _doorPlayers[actor] = (_doorPlayers[actor].Object, refresh.Wizard, _doorPlayers[actor].Generation);
                        QueueLegacyDoors(actor);
                    }
                    break;
            }
        }
        base.ReceiveZoneBroadcast(message);
    }
    private void QueueLegacyDoors(IActorRef player) {
        if (!ClassicQuestEngine.IsActive || player is null || !_doorPlayers.ContainsKey(player) || !_pendingDoorRefresh.Add(player)) return;
        Timers.StartSingleTimer(("doors", player), new RefreshLegacyDoors(player), TimeSpan.FromMilliseconds(10));
    }
    [MessageHandler(typeof(RefreshLegacyDoors))]
    private void RefreshLegacy(RefreshLegacyDoors refresh) {
        _pendingDoorRefresh.Remove(refresh.Player);
        if (!_doorPlayers.TryGetValue(refresh.Player, out var player) || player.Wizard is null) return;
        _legacyBindings ??= LegacyDoorBindings.ForZone(Zone.ZonePath).Where(b =>
            _orderedTriggers.Any(t => t.Trigger?.m_fireEvents?.Any(e => e == b.Event) == true &&
                t.Trigger.m_results?.m_results?.OfType<ResTeleport>().Any(r => b.Destinations.Any(d => string.Equals((string)r.m_destinationZone, d, StringComparison.OrdinalIgnoreCase))) == true)).ToArray();
        if (_legacyBindings.Length == 0) return;
        var destinations = ActiveDoorDestinations(player.Wizard).ToArray();
        var decisions = _legacyBindings.Select(b => new LegacyDoorState(b,
            DoorLightRules.State(DoorRoutes(b.Event, refresh.Player, player.Object, player.Wizard), destinations))).ToArray();
        refresh.Player.Tell(new LegacyDoorSnapshot { Zone = Zone.ZonePath, ZoneActor = ZoneRef, States = decisions, AttachGeneration = player.Generation, Replay = _doorReplay.Remove(refresh.Player) });
    }
    private IEnumerable<string> ActiveDoorDestinations(Wizard wizard) => RelevantDoorDestinations(
        wizard.QuestBehavior?.CurrentQuestInstances?.SelectMany(q => QuestTemplateCollection.GetQuestByName(q.QuestName)?.m_goals?
            .Where(g => q.IsGoalActive(g.m_goalName)) ?? []) ?? [], LegacyDoorBindings.ForZone(Zone.ZonePath));
    internal static IEnumerable<string> RelevantDoorDestinations(IEnumerable<GoalTemplate> activeGoals, IReadOnlyList<LegacyDoorBindings.Binding> bindings) {
        foreach (var goal in activeGoals) {
            if (!string.IsNullOrEmpty(goal.m_destinationZone)) yield return goal.m_destinationZone;
            // A zone-entry waypoint may author the destination in its typed field.
            if (goal is WaypointGoalTemplate { m_zoneEntry: true } waypoint && !string.IsNullOrEmpty(waypoint.m_zoneTag)) yield return waypoint.m_zoneTag;
            // Exact authored lamp tags remain relevant for bounty/interior NPC goals
            // that have no helper destination. Map only verified doors in this zone.
            foreach (var binding in bindings)
                if (goal.m_clientTags?.Any(tag => string.Equals(tag, binding.Tag, StringComparison.Ordinal)) == true) foreach (var destination in binding.Destinations) yield return destination;
        }
    }
    private List<DoorLightRules.Route> DoorRoutes(string boundEvent, IActorRef actor, CoreObject player, Wizard wizard) {
        bool Meets(RequirementList requirements, string name) => !HasUndecodedRequirement(requirements)
            && (requirements?.m_requirements is not { Count: > 0 } || RequirementDispatcher.EvaluateRequirements(requirements,
                new ZoneRequirementContext(requirements, actor, player, wizard, ZoneRef, name)));
        return _orderedTriggers.Where(t => t.Trigger?.m_fireEvents?.Any(e => e == boundEvent) == true)
            .Select(t => DoorLightRules.ApplyZonePolicy(new DoorLightRules.Route(
                (string)t.Trigger.m_results?.m_results?.OfType<ResTeleport>().FirstOrDefault(r => !string.IsNullOrEmpty(r.m_destinationZone))?.m_destinationZone ?? "",
                _activation.IsArmed(t.Actor, Scope(actor)), Meets(t.Trigger.m_requirements, t.Trigger.m_triggerName)
                    && (!_teleportRequirements.TryGetValue(t.Trigger, out var required) || Meets(required, t.Trigger.m_triggerName))), ClassicRuntime.Rules)).ToList();
    }

    [MessageHandler(typeof(DoorLightQuery))]
    private void EvaluateDoorLight(DoorLightQuery query) {
        if (!ClassicQuestEngine.IsActive) return;
        // Only an explicit template + exact placement-tag link is binding evidence.
        if (!_lightBindingEvents.TryGetValue(query.Tag, out var boundEvent)) {
            boundEvent = BindingEvent(_orderedTriggers.Select(t => t.Trigger), query.Tag);
            _lightBindingEvents[query.Tag] = boundEvent;
        }
        if (boundEvent is null) {
            query.ReplyTo.Tell(new DoorLightDecision(query.Player, null));
            return;
        }
        var wizard = query.Wizard;
        if (wizard is null) return; // A timeout/offline player is not proof that a door is locked.
        var routes = DoorRoutes(boundEvent, query.Player, query.PlayerObject, wizard);
        var destinations = ActiveDoorDestinations(wizard);
        query.ReplyTo.Tell(new DoorLightDecision(query.Player, DoorLightRules.State(routes, destinations)));
    }
    internal static string BindingEvent(System.Collections.Generic.IEnumerable<Trigger> triggers, string tag) {
        if (string.IsNullOrEmpty(tag)) return null;
        var events = triggers.Where(t => t?.m_triggerObjInfo is { } info
                && info.m_templateID == QuestDoorLightComponent.TemplateId
                && string.Equals(info.m_zoneTag, tag, StringComparison.Ordinal))
            .SelectMany(t => t.m_fireEvents ?? []).Select(e => (string)e)
            .Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.Ordinal).ToList();
        return events.Count == 1 ? events[0] : null;
    }

}
