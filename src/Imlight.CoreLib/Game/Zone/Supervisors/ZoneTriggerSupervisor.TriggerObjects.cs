/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * ZONE TRIGGERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the zone's trigger state as the client data has it: triggers
 * that wait for their "Enable_" event start disarmed, trigger objects come
 * and go with their triggers and with ResRemoveTriggerObject /
 * ResAddTriggerObject, and a wizard whose quest progress posted a zone's
 * state events in an earlier instance gets them posted again on entry.
 *
 * USAGE EXAMPLE:
 * PrepareTriggerPlan(message) in ReceiveZoneLoadResults; Scope(player) for
 * every activation decision; OnTriggerStateChanged from the dispatch.
 *
 * NOTE:
 * The plan (ZoneTriggerPlans) keeps open, as before, every trigger whose
 * enabling event nothing the server serves posts, and leaves out every
 * trigger object that would never be released; both are logged once per
 * zone. State is per zone instance in an instanced zone and per player in
 * a public one (ZoneTriggerTable.ScopeOf). The replay on EnterZone posts
 * only replay-safe events (ZoneTriggerLiveness.ReplaySafe) the scope has
 * not seen, whose quest result ran for the wizard: a start result of a
 * held or done quest, an activate result of an active or done goal, a
 * complete result of a done goal, an end result of a done quest.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

/// <summary>
/// CLASSIC: a trigger result asks the zone's trigger supervisor to take an object away or bring it back.
/// </summary>
internal sealed class TriggerObjectPresenceChange : IServerMessage {
    // In-process only: never serialized or registered as a wire message.
    public byte MessageOrder => 0;
    public byte ServiceID => 0;
    public string Tag;
    public bool Present;
    public IActorRef Player;
}

/// <summary>
/// CLASSIC: the trigger supervisor tells the zone's objects that an object is now there or gone, for one player
/// (a public zone) or for everyone (Player null: an instanced zone).
/// </summary>
internal sealed class TriggerObjectPresenceUpdate : IServerMessage {
    // In-process only: never serialized or registered as a wire message.
    public byte MessageOrder => 0;
    public byte ServiceID => 0;
    public string Tag;
    public bool Present;
    public IActorRef Player;
}

internal sealed partial class ZoneTriggerSupervisor {

    private static readonly ConcurrentDictionary<string, bool> s_reportedPlans = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<string, List<(string Quest, QuestResultSource Source)>>> s_replayable
        = new(StringComparer.Ordinal);

    private ZoneTriggerPlan _plan;
    private ZoneTriggerTable _table;
    private Dictionary<string, List<(string Quest, QuestResultSource Source)>> _replayable = [];

    private void PrepareTriggerPlan(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        _plan = null;
        _table = null;
        _replayable = [];
        if (!ClassicQuestEngine.IsActive) {
            return;
        }

        _plan = ZoneTriggerPlans.For(Zone.ZonePath, message.TriggerData, message.VolumeData, message.ZoneData);
        _table = ZoneTriggerTables.Create(ZoneRef, _plan, Zone.InstanceOwnerId != 0);
        _replayable = s_replayable.GetOrAdd(Zone.ZonePath, _ => ReplayableQuestEvents(_plan)); // quest data does not change
        ReportPlanOnce(_plan);
    }

    // The quest result lists that post one of this zone's replay-safe events, by event.
    private static Dictionary<string, List<(string, QuestResultSource)>> ReplayableQuestEvents(ZoneTriggerPlan plan) {
        var wanted = plan.Liveness.ReplaySafe;
        var found = new Dictionary<string, List<(string, QuestResultSource)>>(StringComparer.Ordinal);
        if (wanted.Count == 0) {
            return found;
        }

        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            if (quest is null || string.IsNullOrEmpty(quest.m_questName)) {
                continue;
            }

            foreach (var (source, posted) in ZoneTriggerPlans.QuestEventSources(quest)
                         .SelectMany(s => ZoneTriggerPlans.EventsPostedBy(s.Results).Select(e => (s.Source, e)))
                         .Concat(ZoneTriggerPlans.GoalCompleteEvents(quest))) {
                if (!wanted.Contains(posted)) {
                    continue;
                }

                if (!found.TryGetValue(posted, out var list)) {
                    found[posted] = list = [];
                }

                list.Add((quest.m_questName, source));
            }
        }

        return found;
    }

    private void ReportPlanOnce(ZoneTriggerPlan plan) {
        if (!s_reportedPlans.TryAdd(Zone.ZonePath, true)) {
            return;
        }

        var liveness = plan.Liveness;
        if (liveness.KeptOpen.Count > 0) {
            Logger.Information("Zone {Zone}: {Count} trigger(s) wait for an event nothing posts here and stay open: {Triggers}.",
                Logger.Args(Zone.ZonePath, liveness.KeptOpen.Count, string.Join(", ", liveness.KeptOpen.Select(i => liveness.Triggers[i].Name))));
        }

        var stuck = liveness.Objects.Values.Where(o => o.Stuck).Select(o => o.Tag).ToList();
        if (stuck.Count > 0) {
            Logger.Information("Zone {Zone}: {Count} trigger object(s) nothing would release are not created: {Objects}.",
                Logger.Args(Zone.ZonePath, stuck.Count, string.Join(", ", stuck)));
        }

        Logger.Debug("Zone {Zone}: {Gated} trigger(s) wait for their event; {Objects} trigger object(s).",
            Logger.Args(Zone.ZonePath, liveness.Gated.Count, plan.TriggerObjectsToSpawn().Count()));
    }

    /// <summary>The state scope of a player's event: the instance in an instanced zone, the player in a public one.</summary>
    private IActorRef Scope(IActorRef player) => _table?.ScopeOf(player) ?? player;

    private bool InitiallyArmed(Trigger trigger) {
        var index = _plan?.IndexOf(trigger) ?? -1;

        return index < 0 || _plan.Liveness.StartsArmed[index];
    }

    private void OnTriggerStateChanged(IActorRef triggerActor, bool armed, IActorRef scope) {
        if (_table is null) {
            return;
        }

        var entry = _orderedTriggers.FirstOrDefault(t => t.Actor.Equals(triggerActor));
        var index = _plan.IndexOf(entry.Trigger);
        PublishPresence(_table.SetArmed(index, scope, armed), scope);
    }

    private void ReceiveTriggerObjectPresenceChange(TriggerObjectPresenceChange change) {
        if (_table is null || string.IsNullOrEmpty(change.Tag)) {
            return;
        }

        if (!_table.Manages(change.Tag)) {
            Logger.Debug("Zone {Zone}: no object {Tag} to {Change}.",
                Logger.Args(Zone.ZonePath, change.Tag, change.Present ? "add" : "remove"));

            return;
        }

        var scope = Scope(change.Player);
        PublishPresence(_table.SetPresent(change.Tag, scope, change.Present), scope);
    }

    private void PublishPresence(IReadOnlyList<(string Tag, bool Present)> changes, IActorRef scope) {
        if (changes.Count == 0) {
            return;
        }

        foreach (var (tag, present) in changes) {
            Logger.Debug("Zone {Zone} object {Tag} is {State} for {Scope}.",
                Logger.Args(Zone.ZonePath, tag, present ? "there" : "gone", _table.Shared ? "the instance" : scope?.Path.Name));
        }

        ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Targets = ZoneBroadcastTarget.Objects,
            Messages = [.. changes.Select(change => (IServerMessage) new TriggerObjectPresenceUpdate {
                Tag = change.Tag, Present = change.Present, Player = _table.Shared ? null : scope,
            })],
        });
    }

    // On EnterZone: the replay-safe events of this zone that the wizard's quest progress posted, which this scope has
    // not seen (a new instance of a dungeon the wizard is half way through).
    private void ReplayProgressEvents(ZONE_102_PROTOCOL.MSG_POSTEVENT entry) {
        if (_table is null || _replayable.Count == 0 || entry.PlayerActor is null) {
            return;
        }

        var unseen = _replayable.Keys.Where(e => !_table.HasSeen(e, entry.PlayerActor)).ToList();
        if (unseen.Count == 0) {
            return;
        }

        var wizard = PlayerQuery.ActiveWizard(entry.PlayerActor, $"Zone {Zone.ZonePath} trigger replay");
        if (wizard?.QuestBehavior is null) {
            return;
        }

        foreach (var eventName in unseen) {
            if (!_replayable[eventName].Any(source => ProgressRan(wizard, source.Quest, source.Source))) {
                continue;
            }

            Logger.Debug("Zone {Zone}: replaying {Event} for {Player} (quest progress).",
                Logger.Args(Zone.ZonePath, eventName, entry.PlayerActor.Path.Name));
            ReceiveClassicPostEvent(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = eventName,
                PlayerActor = entry.PlayerActor,
                PlayerGameObject = entry.PlayerGameObject,
            });
        }
    }

    /// <summary>True when the wizard's progress has run the quest result list.</summary>
    internal static bool ProgressRan(Wizard wizard, string questName, QuestResultSource source) {
        var done = wizard.QuestBehavior.HasCompletedQuest(questName);
        if (done) {
            return true;
        }

        var held = wizard.QuestBehavior.CurrentQuestInstances?.FirstOrDefault(q => q?.QuestName == questName);
        if (held is null) {
            return false;
        }

        return source.When switch {
            QuestResultWhen.Start => true,
            QuestResultWhen.GoalActivate => held.IsGoalActive(source.GoalName) || held.IsGoalCompleted(source.GoalName),
            QuestResultWhen.GoalComplete => held.IsGoalCompleted(source.GoalName),
            _ => false,
        };
    }

    protected override void PostStop() {
        if (ClassicQuestEngine.IsActive) {
            ZoneTriggerTables.Remove(ZoneRef);
        }

        base.PostStop();
    }

}
