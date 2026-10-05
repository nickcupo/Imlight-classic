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
 * CLASSIC: decides, from a zone's trigger data and the events the server
 * can post there, which triggers start armed, which trigger objects exist,
 * and which events are safe to post again for a wizard whose quest
 * progress posted them in an earlier instance.
 *
 * USAGE EXAMPLE:
 * var plan = ZoneTriggerLiveness.Analyze(facts, rootEvents, placedTags);
 * if (plan.StartsArmed[i]) { ... }  plan.Objects["Gate of Paulson"].Spawn
 *
 * NOTE:
 * KingsIsle's triggers start inactive unless "StartZone" (posted when the
 * zone instance starts) is one of their m_activateEvents; the others wait
 * for an "Enable_..." style event. A trigger object (m_triggerObjInfo, a
 * gate, a collision wall, a teleporter pad) exists while its trigger is
 * active, and ResRemoveTriggerObject / ResAddTriggerObject take it away and
 * bring it back by name (Sunken City's gates and Marla stand-ins, the Tomb
 * of the Beguiler's gauntlet walls). The server used to arm every trigger
 * and spawn no trigger object at all, so doors opened by themselves and
 * nothing ever blocked.
 * Reachability is a fixpoint over the events the server can post (volume
 * enter/exit events, EnterZone, Monster_Killed, object events, and the
 * replay-safe events quest results post) and the events the zone's own
 * firing triggers post. A trigger
 * whose requirements cannot pass on this server (an undecoded class or one
 * without a handler) never fires. Two rules keep wizards from being
 * stranded where the old server let them through:
 * - a trigger that waits for an event nothing reachable posts stays armed
 *   from the start, as before (KeptOpen, reported);
 * - a trigger object that is meant to go away (removed, opened, or owned by
 *   a trigger with a deactivate event) but that no firing trigger ever
 *   releases is not spawned (Stuck, reported).
 * An event is replay-safe when every trigger that fires on it only arms or
 * disarms triggers, changes trigger objects, or waits.
 *
 * TODO:
 * - Quest-posted events count for every zone that listens to them; KingsIsle posted a quest's event to the wizard's
 *   zone of the moment.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

/// <summary>
/// What the liveness analysis needs to know about one trigger of a zone, in data order.
/// </summary>
public sealed record TriggerFacts {

    /// <summary>The trigger's name (m_triggerName).</summary>
    public string Name { get; init; } = "";

    /// <summary>m_activateEvents.</summary>
    public IReadOnlyList<string> ActivateEvents { get; init; } = [];

    /// <summary>m_fireEvents.</summary>
    public IReadOnlyList<string> FireEvents { get; init; } = [];

    /// <summary>m_deactivateEvents.</summary>
    public IReadOnlyList<string> DeactivateEvents { get; init; } = [];

    /// <summary>False when the trigger's requirements can never pass on this server.</summary>
    public bool RequirementsCanPass { get; init; } = true;

    /// <summary>The events its results post when it fires (ResPostEvent, a zone timer's end event, a state event).</summary>
    public IReadOnlyList<string> PostedEvents { get; init; } = [];

    /// <summary>The objects its results remove by name (ResRemoveTriggerObject).</summary>
    public IReadOnlyList<string> RemovedObjects { get; init; } = [];

    /// <summary>The objects its results bring back by name (ResAddTriggerObject).</summary>
    public IReadOnlyList<string> AddedObjects { get; init; } = [];

    /// <summary>The objects whose state its results change (ResModifyTriggerObject, ResStateChange: doors opening).</summary>
    public IReadOnlyList<string> ChangedObjects { get; init; } = [];

    /// <summary>The zone tag of its trigger object, or null.</summary>
    public string? ObjectTag { get; init; }

    /// <summary>True when every result only arms or disarms triggers, changes trigger objects or waits.</summary>
    public bool StateOnly { get; init; }

}

/// <summary>
/// The plan for one object a zone's triggers manage.
/// </summary>
/// <param name="Tag">The object's zone tag.</param>
/// <param name="Owners">The triggers (data order) whose trigger object it is; empty for an object the zone places.</param>
/// <param name="Placed">True for an object of the zone's own placement (gamedata.bin) that triggers remove or add.</param>
/// <param name="MeantToGo">True when some trigger removes it, changes its state, or its owner can be deactivated.</param>
/// <param name="Releasable">True when a trigger that can fire releases it.</param>
public sealed record TriggerObjectPlan(string Tag, IReadOnlyList<int> Owners, bool Placed, bool MeantToGo, bool Releasable) {

    /// <summary>True when the object is created: placed objects always, trigger objects unless stuck.</summary>
    public bool Spawn => Placed || !Stuck;

    /// <summary>True for a trigger object that is meant to go away but that nothing which fires releases.</summary>
    public bool Stuck => !Placed && MeantToGo && !Releasable;

}

/// <summary>
/// The result of <see cref="ZoneTriggerLiveness.Analyze"/>.
/// </summary>
public sealed class ZoneTriggerLiveness {

    /// <summary>The event every zone instance posts when it starts; the server never posts it.</summary>
    public const string StartZone = "StartZone";

    private ZoneTriggerLiveness(IReadOnlyList<TriggerFacts> triggers) {
        Triggers = triggers;
    }

    /// <summary>The analysed triggers, in data order.</summary>
    public IReadOnlyList<TriggerFacts> Triggers { get; }

    /// <summary>Per trigger: armed before any event.</summary>
    public IReadOnlyList<bool> StartsArmed { get; private set; } = [];

    /// <summary>Per trigger: can fire on this server.</summary>
    public IReadOnlyList<bool> CanFire { get; private set; } = [];

    /// <summary>The triggers that wait for an event nothing reachable posts; they start armed, as before.</summary>
    public IReadOnlyList<int> KeptOpen { get; private set; } = [];

    /// <summary>The triggers that start disarmed and wait for a reachable event.</summary>
    public IReadOnlyList<int> Gated { get; private set; } = [];

    /// <summary>Every event the server can post in the zone.</summary>
    public IReadOnlySet<string> Reachable { get; private set; } = new HashSet<string>();

    /// <summary>The managed objects by zone tag.</summary>
    public IReadOnlyDictionary<string, TriggerObjectPlan> Objects { get; private set; } = new Dictionary<string, TriggerObjectPlan>();

    /// <summary>The events that are safe to post again (only state results follow from them).</summary>
    public IReadOnlySet<string> ReplaySafe { get; private set; } = new HashSet<string>();

    /// <summary>Every event a trigger of the zone listens to (activate, fire or deactivate).</summary>
    public IReadOnlySet<string> Listened { get; private set; } = new HashSet<string>();

    /// <summary>
    /// True when a trigger waits for an activate event: it has activate events and StartZone is not one of them.
    /// A trigger without activate events is armed, as the server always treated it.
    /// </summary>
    /// <param name="trigger">The trigger.</param>
    public static bool WaitsForEvent(TriggerFacts trigger)
        => trigger.ActivateEvents.Count > 0 && !trigger.ActivateEvents.Contains(StartZone, StringComparer.Ordinal);

    /// <summary>
    /// Analyses a zone's triggers.
    /// </summary>
    /// <param name="triggers">The triggers in data order.</param>
    /// <param name="rootEvents">The events the server posts in the zone on its own (volumes, EnterZone, Monster_Killed,
    /// object use): a new instance of the zone sees them again as the wizard walks it.</param>
    /// <param name="questEvents">The events quest results post. Only the replay-safe ones count: a wizard who enters a new
    /// instance after the quest step posted it gets it posted again (ZoneTriggerSupervisor's replay); another one would
    /// leave a new instance's door shut for good.</param>
    /// <param name="placedTags">The zone tags of the objects the zone places that the server creates (DYNAMIC_SERVER).</param>
    /// <returns>The plan.</returns>
    public static ZoneTriggerLiveness Analyze(IReadOnlyList<TriggerFacts> triggers, IEnumerable<string> rootEvents,
                                              IEnumerable<string> questEvents, IEnumerable<string> placedTags) {
        ArgumentNullException.ThrowIfNull(triggers);
        var plan = new ZoneTriggerLiveness(triggers);
        plan.Listened = triggers.SelectMany(t => t.ActivateEvents.Concat(t.FireEvents).Concat(t.DeactivateEvents))
            .Where(e => !string.IsNullOrEmpty(e)).ToHashSet(StringComparer.Ordinal);
        plan.ReplaySafe = ReplaySafeEvents(triggers, plan.Listened);
        var roots = new HashSet<string>(rootEvents ?? [], StringComparer.Ordinal);
        roots.UnionWith((questEvents ?? []).Where(plan.ReplaySafe.Contains));
        var placed = new HashSet<string>(placedTags ?? [], StringComparer.Ordinal);

        // Phase 1: a waiting trigger arms only on a reachable event.
        var waiting = triggers.Select(WaitsForEvent).ToArray();
        var reachable = Fixpoint(triggers, roots, i => !waiting[i]);

        // Triggers whose activate events are never reachable are kept open (armed from the start), and may post events.
        var keptOpen = Enumerable.Range(0, triggers.Count)
            .Where(i => waiting[i] && !triggers[i].ActivateEvents.Any(reachable.Contains)).ToHashSet();
        reachable = Fixpoint(triggers, roots, i => !waiting[i] || keptOpen.Contains(i));

        var startsArmed = Enumerable.Range(0, triggers.Count).Select(i => !waiting[i] || keptOpen.Contains(i)).ToArray();
        var canFire = Enumerable.Range(0, triggers.Count)
            .Select(i => CanFireWith(triggers[i], startsArmed[i], reachable)).ToArray();

        plan.StartsArmed = startsArmed;
        plan.CanFire = canFire;
        plan.KeptOpen = [.. keptOpen.Order()];
        plan.Gated = [.. Enumerable.Range(0, triggers.Count).Where(i => waiting[i] && !keptOpen.Contains(i))];
        plan.Reachable = reachable;
        plan.Objects = PlanObjects(triggers, placed, canFire, reachable);

        return plan;
    }

    private static bool CanFireWith(TriggerFacts trigger, bool startsArmed, IReadOnlySet<string> reachable)
        => (startsArmed || trigger.ActivateEvents.Any(reachable.Contains))
            && trigger.FireEvents.Any(reachable.Contains)
            && trigger.RequirementsCanPass;

    private static HashSet<string> Fixpoint(IReadOnlyList<TriggerFacts> triggers, HashSet<string> roots, Func<int, bool> armedAtStart) {
        var reachable = new HashSet<string>(roots, StringComparer.Ordinal);
        var fired = new bool[triggers.Count];
        bool changed;
        do {
            changed = false;
            for (var i = 0; i < triggers.Count; i++) {
                if (fired[i] || !CanFireWith(triggers[i], armedAtStart(i), reachable)) {
                    continue;
                }

                fired[i] = true;
                changed = true;
                foreach (var posted in triggers[i].PostedEvents) {
                    if (!string.IsNullOrEmpty(posted)) {
                        reachable.Add(posted);
                    }
                }
            }
        } while (changed);

        return reachable;
    }

    private static Dictionary<string, TriggerObjectPlan> PlanObjects(IReadOnlyList<TriggerFacts> triggers, HashSet<string> placed,
                                                                     bool[] canFire, HashSet<string> reachable) {
        var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < triggers.Count; i++) {
            if (!string.IsNullOrEmpty(triggers[i].ObjectTag)) {
                if (!owners.TryGetValue(triggers[i].ObjectTag!, out var list)) {
                    owners[triggers[i].ObjectTag!] = list = [];
                }

                list.Add(i);
            }
        }

        // Objects the zone places count only when a trigger removes or adds them.
        var named = triggers.SelectMany(t => t.RemovedObjects.Concat(t.AddedObjects)).ToHashSet(StringComparer.Ordinal);
        var plans = new Dictionary<string, TriggerObjectPlan>(StringComparer.Ordinal);
        foreach (var tag in owners.Keys.Concat(named.Where(placed.Contains)).Distinct(StringComparer.Ordinal)) {
            var isPlaced = !owners.ContainsKey(tag);
            var ownerList = owners.TryGetValue(tag, out var list) ? (IReadOnlyList<int>) list : [];
            var removedOrChanged = Enumerable.Range(0, triggers.Count)
                .Where(i => triggers[i].RemovedObjects.Contains(tag, StringComparer.Ordinal)
                    || triggers[i].ChangedObjects.Contains(tag, StringComparer.Ordinal)).ToList();
            var meantToGo = removedOrChanged.Count > 0 || ownerList.Any(o => triggers[o].DeactivateEvents.Count > 0);
            var releasable = removedOrChanged.Any(i => canFire[i])
                || ownerList.Any(o => triggers[o].DeactivateEvents.Any(reachable.Contains));
            plans[tag] = new TriggerObjectPlan(tag, ownerList, isPlaced, meantToGo, releasable);
        }

        return plans;
    }

    private static HashSet<string> ReplaySafeEvents(IReadOnlyList<TriggerFacts> triggers, IReadOnlySet<string> listened) {
        var safe = new HashSet<string>(listened, StringComparer.Ordinal);
        bool changed;
        do {
            changed = false;
            foreach (var eventName in safe.ToList()) {
                var listeners = triggers.Where(t => t.FireEvents.Contains(eventName, StringComparer.Ordinal));
                if (listeners.Any(t => !t.StateOnly || t.PostedEvents.Any(p => listened.Contains(p) && !safe.Contains(p)))) {
                    safe.Remove(eventName);
                    changed = true;
                }
            }
        } while (changed);

        return safe;
    }

}
