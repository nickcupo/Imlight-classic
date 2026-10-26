// CLASSIC: door kill gates that name a creature their room never spawns (rules.kill_gates, Imlight.Classic.Rules.KillGates).
// The Dragonspyre Detention and Necropolis gauntlets' third room waits for a Vile Apparition (Ghost-Red-R8) that no
// spawn brings, so its wall never went; the watcher now waits for the room's boss, Ranulf Moonclaw. Applied at zone load
// to the freshly decoded triggers (ZoneLoader), before either supervisor builds its plan.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

internal static class ClassicKillGates {

    internal static WizZoneTriggers? Apply(string zonePath, WizZoneTriggers? data)
        => Apply(ClassicProgression.KillGates, zonePath, data);

    /// <summary>
    /// Replaces, in each named trigger of <paramref name="zonePath"/>, the kill-requirement entry a fix names. Returns
    /// <paramref name="data"/> (changed in place: every load decodes its own copy).
    /// </summary>
    internal static WizZoneTriggers? Apply(KillGates gates, string zonePath, WizZoneTriggers? data) {
        if (data?.m_triggers is not { } triggers) {
            return data;
        }

        foreach (var fix in gates.For(zonePath)) {
            var trigger = triggers.FirstOrDefault(t => t is not null && (string) t.m_triggerName == fix.Trigger);
            var kills = trigger is null ? [] : KillRequirements(trigger.m_requirements).ToList();
            var replaced = 0;
            foreach (var kill in kills) {
                for (var i = 0; i < kill.m_adjectiveList.Count; i++) {
                    if (string.Equals(kill.m_adjectiveList[i], fix.From, StringComparison.Ordinal)) {
                        kill.m_adjectiveList[i] = fix.To;
                        replaced++;
                    }
                }
            }

            if (replaced > 0) {
                Logger.Debug("Zone {Zone}: {Trigger} waits for {To} instead of {From} (kill-gate fix).",
                    Logger.Args(zonePath, fix.Trigger, fix.To, fix.From));
            } else {
                Logger.Warning("Zone {Zone}: kill-gate fix for {Trigger} found no {From}; the zone data has changed.",
                    Logger.Args(zonePath, fix.Trigger, fix.From));
            }
        }

        return data;
    }

    private static IEnumerable<ClassicReqMonsterKilled> KillRequirements(Requirement? requirement) => requirement switch {
        ClassicReqMonsterKilled kill => [kill],
        RequirementList list => (list.m_requirements ?? []).SelectMany(KillRequirements),
        _ => [],
    };

}
