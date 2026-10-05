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
 * CLASSIC: builds a zone's trigger plan (ZoneTriggerLiveness) from the
 * loaded zone data and the events the server posts: which triggers start
 * armed, which trigger objects the zone creates, which triggers stay open
 * because nothing posts their enabling event.
 *
 * USAGE EXAMPLE:
 * var plan = ZoneTriggerPlans.For(zonePath, message.TriggerData, message.VolumeData, message.ZoneData);
 *
 * NOTE:
 * One plan per loaded zone data: the trigger and object supervisors of a
 * zone share it (the same MSG_ZONELOADRESULTS reaches both). Root events:
 * EnterZone and Monster_Killed (the server posts both), the zone's volume
 * enter and exit events, the events quest results post (any loaded quest,
 * GoalComplete_<quest>_<goal> included), and the events object use posts
 * in the zone (InteractableQuestEvents). A trigger's posted events are its
 * ResPostEvent results, a zone timer's end event, and the state events of
 * its ResModifyTriggerObject / ResAddDynaMod results. A requirement class
 * that did not decode, or that no handler evaluates (unless negated), can
 * never pass.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Cinematics;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic.Cinematics;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// A zone's trigger plan and the data it was built from.
/// </summary>
/// <param name="ZonePath">The zone.</param>
/// <param name="Triggers">The zone's triggers in data order (nulls left out); the plan's indices refer to them.</param>
/// <param name="Liveness">The analysis.</param>
/// <param name="ObjectInfos">The first trigger object info of each managed trigger object tag.</param>
internal sealed record ZoneTriggerPlan(string ZonePath, IReadOnlyList<Trigger> Triggers, ZoneTriggerLiveness Liveness,
                                       IReadOnlyDictionary<string, CoreObjectInfo> ObjectInfos) {

    private readonly Dictionary<Trigger, int> _index = Triggers.Select((t, i) => (t, i))
        .ToDictionary(p => p.t, p => p.i, (IEqualityComparer<Trigger>) ReferenceEqualityComparer.Instance);

    /// <summary>The trigger's index in the plan, or -1 for a trigger the plan does not know (one the server added).</summary>
    internal int IndexOf(Trigger trigger) => trigger is not null && _index.TryGetValue(trigger, out var i) ? i : -1;

    /// <summary>The trigger objects the zone creates (not stuck), with their object info.</summary>
    internal IEnumerable<CoreObjectInfo> TriggerObjectsToSpawn()
        => Liveness.Objects.Values.Where(o => !o.Placed && o.Spawn && ObjectInfos.ContainsKey(o.Tag)).Select(o => ObjectInfos[o.Tag]);

}

/// <summary>
/// Builds and shares zone trigger plans.
/// </summary>
internal static class ZoneTriggerPlans {

    internal const string EnterZoneEvent = "EnterZone";

    private static readonly ConditionalWeakTable<WizZoneTriggers, ZoneTriggerPlan> s_plans = new();
    private static readonly Lock s_questEventsLock = new();
    private static HashSet<string> s_questEvents;

    /// <summary>
    /// The plan for a zone's loaded data, built once per load (both supervisors receive the same data).
    /// </summary>
    internal static ZoneTriggerPlan For(string zonePath, WizZoneTriggers triggerData, WizZoneVolumes volumeData, WizZoneData zoneData) {
        if (triggerData is null) {
            return Build(zonePath, [], volumeData, zoneData);
        }

        lock (s_plans) {
            if (!s_plans.TryGetValue(triggerData, out var plan)) {
                plan = Build(zonePath, [.. (triggerData.m_triggers ?? []).Where(t => t is not null)], volumeData, zoneData);
                s_plans.Add(triggerData, plan);
            }

            return plan;
        }
    }

    /// <summary>
    /// Builds a plan. Public for tests and the Arc 1 report.
    /// </summary>
    internal static ZoneTriggerPlan Build(string zonePath, IReadOnlyList<Trigger> triggers, WizZoneVolumes volumeData, WizZoneData zoneData,
                                          IEnumerable<string> questEvents = null) {
        var facts = triggers.Select(Facts).ToList();
        var placed = (zoneData?.m_objectList ?? [])
            .Where(o => o is not null && o.m_loadingType == LoadingType.DYNAMIC_SERVER && !string.IsNullOrEmpty(o.m_zoneTag))
            .Select(o => (string) o.m_zoneTag);
        var liveness = ZoneTriggerLiveness.Analyze(facts, RootEvents(zonePath, volumeData), questEvents ?? QuestEvents(), placed);

        var infos = new Dictionary<string, CoreObjectInfo>(StringComparer.Ordinal);
        foreach (var trigger in triggers) {
            if (SpawnableObject(trigger) is { } info && !infos.ContainsKey(info.m_zoneTag)) {
                // A trigger that only holds its object (no fire events) can only mean its requirements for who has the
                // object: Krokotopia's explore beetles (ReqHasGoal Explore-001-1, incomplete), a quest's chest.
                if (trigger.m_fireEvents is not { Count: > 0 } && trigger.m_requirements?.m_requirements is { Count: > 0 }
                        && info.m_spawnRequirements?.m_requirements is not { Count: > 0 }) {
                    info.m_spawnRequirements = trigger.m_requirements;
                }

                infos[info.m_zoneTag] = info;
            }
        }

        return new ZoneTriggerPlan(zonePath, triggers, liveness, infos);
    }

    /// <summary>
    /// The trigger's own object when the server creates it: a decoded TriggerObjectInfo with a template and a tag,
    /// not one the client loads itself (STATIC_CLIENT).
    /// </summary>
    internal static CoreObjectInfo SpawnableObject(Trigger trigger)
        => trigger?.m_triggerObjInfo is ClassicTriggerObjectInfo info && info.m_templateID.Full != 0
            && !string.IsNullOrEmpty(info.m_zoneTag) && info.m_loadingType != LoadingType.STATIC_CLIENT
            ? info : null;

    /// <summary>The analysis facts of one trigger.</summary>
    internal static TriggerFacts Facts(Trigger trigger) {
        var results = (trigger.m_results?.m_results ?? []).Where(r => r is not null).ToList();
        var passing = results.Where(r => CanPass(r.m_requirements)).ToList();

        return new TriggerFacts {
            Name = (string) trigger.m_triggerName ?? "",
            ActivateEvents = Names(trigger.m_activateEvents),
            FireEvents = Names(trigger.m_fireEvents),
            DeactivateEvents = Names(trigger.m_deactivateEvents),
            RequirementsCanPass = CanPass(trigger.m_requirements),
            PostedEvents = [.. passing.SelectMany(PostedEvents).Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.Ordinal)],
            RemovedObjects = [.. passing.OfType<ClassicResTriggerObjectPresence>().Where(r => !r.Adds).Select(r => r.ObjectName).Where(n => !string.IsNullOrEmpty(n))],
            AddedObjects = [.. passing.OfType<ClassicResTriggerObjectPresence>().Where(r => r.Adds).Select(r => r.ObjectName).Where(n => !string.IsNullOrEmpty(n))],
            ChangedObjects = [.. passing.Select(ChangedObject).Where(n => !string.IsNullOrEmpty(n))],
            ObjectTag = SpawnableObject(trigger)?.m_zoneTag,
            StateOnly = results.All(IsStateResult),
        };
    }

    private static IEnumerable<string> PostedEvents(Result result) => result switch {
        ResPostEvent post => [post.m_eventName],
        ClassicResZoneTimer timer when !string.IsNullOrEmpty(timer.TimerName) => [CinematicTimers.EndEventOf(timer.TimerName)],
        ClassicResModifyTriggerObject modify when modify.CanExecute => [ObjectStateRules.EnterStateEvent(modify.ObjectName, modify.State)],
        ResAddDynaMod dynaMod when !string.IsNullOrEmpty(dynaMod.m_dynaModClientTag) && !string.IsNullOrEmpty(dynaMod.m_dynaModState)
            => [$"{dynaMod.m_dynaModClientTag}.{dynaMod.m_dynaModState}.EnterState"],
        _ => [],
    };

    private static string ChangedObject(Result result) => result switch {
        ClassicResModifyTriggerObject modify when modify.CanExecute => modify.ObjectName,
        ClassicResStateChange change when change.CanExecute => change.ObjectName,
        _ => null,
    };

    // Results that only arm or disarm triggers, change trigger objects or wait: safe to run again for a wizard whose
    // quest progress posted the event in an earlier instance.
    private static bool IsStateResult(Result result) => result switch {
        ResPostEvent or ClassicResModifyTriggerObject or ClassicResStateChange or ClassicResTriggerObjectPresence or ResWait => true,
        ResActorDialog dialog => dialog.m_dialog?.m_dialogEntries is not { Count: > 0 },
        _ => false,
    };

    /// <summary>
    /// True unless the list holds a requirement that never passes on this server: one that did not decode (null), or a
    /// class no handler evaluates that is not negated.
    /// </summary>
    internal static bool CanPass(RequirementList requirements) {
        foreach (var requirement in requirements?.m_requirements ?? []) {
            switch (requirement) {
                case null:
                    return false;
                case RequirementList nested:
                    if (!nested.m_applyNOT && !CanPass(nested)) {
                        return false;
                    }

                    break;
                default:
                    if (!requirement.m_applyNOT && !RequirementDispatcher.HasHandlerFor(requirement.GetType())) {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>The events the server posts in the zone without its triggers: entry, kills, volumes, object use.</summary>
    internal static IEnumerable<string> RootEvents(string zonePath, WizZoneVolumes volumeData) {
        yield return EnterZoneEvent;
        yield return KilledMonster.EventName;
        foreach (var volume in volumeData?.m_volumes ?? []) {
            foreach (var name in (volume?.m_enterEvents ?? []).Concat(volume?.m_exitEvents ?? [])) {
                if (!string.IsNullOrEmpty(name)) {
                    yield return name;
                }
            }
        }

        foreach (var name in InteractableQuestEvents.ZoneEventsAnyObjectFiresIn(zonePath)) {
            yield return name;
        }
    }

    /// <summary>
    /// Every event a loaded quest's results post (start, end, goal activate and complete), and the GoalComplete_ event of
    /// every goal. Built once.
    /// </summary>
    internal static IReadOnlySet<string> QuestEvents() {
        lock (s_questEventsLock) {
            if (s_questEvents is { Count: > 0 }) {
                return s_questEvents;
            }

            var built = BuildQuestEvents(QuestTemplateCollection.GetAllQuests());
            if (built.Count > 0) { // not before the quest data is loaded
                s_questEvents = built;
            }

            return built;
        }
    }

    internal static HashSet<string> BuildQuestEvents(IEnumerable<QuestTemplate> quests) {
        var events = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quest in quests ?? []) {
            if (quest is null) {
                continue;
            }

            foreach (var (_, results) in QuestEventSources(quest)) {
                foreach (var result in results?.m_results ?? []) {
                    foreach (var posted in result is null ? [] : PostedEvents(result)) {
                        if (!string.IsNullOrEmpty(posted)) {
                            events.Add(posted);
                        }
                    }
                }
            }

            foreach (var (_, name) in GoalCompleteEvents(quest)) {
                events.Add(name);
            }
        }

        return events;
    }

    /// <summary>The GoalComplete_&lt;quest&gt;_&lt;goal&gt; event the server posts as each goal of the quest completes.</summary>
    internal static IEnumerable<(QuestResultSource Source, string Event)> GoalCompleteEvents(QuestTemplate quest)
        => (quest?.m_goals ?? []).Where(goal => !string.IsNullOrEmpty(goal?.m_goalName))
            .Select(goal => (new QuestResultSource(QuestResultWhen.GoalComplete, goal.m_goalName),
                $"GoalComplete_{quest.m_questName}_{goal.m_goalName}"));

    /// <summary>
    /// Where a quest's results run: (null, start), (null, end), (goal, activate), (goal, complete).
    /// </summary>
    internal static IEnumerable<(QuestResultSource Source, ResultList Results)> QuestEventSources(QuestTemplate quest) {
        yield return (new QuestResultSource(QuestResultWhen.Start, null), quest.m_startResults);
        yield return (new QuestResultSource(QuestResultWhen.End, null), quest.m_endResults);
        foreach (var goal in quest.m_goals ?? []) {
            if (goal is null) {
                continue;
            }

            yield return (new QuestResultSource(QuestResultWhen.GoalActivate, goal.m_goalName), goal.m_activateResults);
            yield return (new QuestResultSource(QuestResultWhen.GoalComplete, goal.m_goalName), goal.m_completeResults);
        }
    }

    /// <summary>The events quest results post, for a quest result list.</summary>
    internal static IEnumerable<string> EventsPostedBy(ResultList results)
        => (results?.m_results ?? []).Where(r => r is not null).SelectMany(PostedEvents).Where(e => !string.IsNullOrEmpty(e));

    private static List<string> Names(IEnumerable<Imcodec.IO.ByteString> names)
        => [.. (names ?? []).Select(n => (string) n).Where(n => !string.IsNullOrEmpty(n))];

}

/// <summary>When a quest's result list runs.</summary>
internal enum QuestResultWhen { Start, End, GoalActivate, GoalComplete }

/// <summary>A quest result list's place: when it runs and, for a goal's, which goal.</summary>
internal readonly record struct QuestResultSource(QuestResultWhen When, string GoalName);
