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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the zone's state objects, such as the symbol obelisks of the Temple
 * of Storms mind puzzle (KT-CRY5-C01-002 "Get Smart"). A player's click moves
 * an object to the next state of its state set. The object entering a state
 * posts "<zone tag>.<state>.EnterState". Zone triggers check object states
 * with KingsIsle's ReqState requirement (m_triggerObjName, m_triggerObjState).
 *
 * USAGE EXAMPLE:
 * table.Track("Bug1", "Idle_Off"); table.Set("Bug1", "Idle_On");
 * ObjectStateRules.Met(table, "Bug1", "Idle_On") == true
 *
 * NOTE:
 * ReqState is the client class hashed 456212888 ("class ReqState"). The
 * r806919 type dump and r756936 dev dump do not list it; its name and its two
 * std::string properties were recovered by hashing candidate names against
 * the hashes in the 2014 and r806919 KT_TempleOfStorms triggers.xml.
 * An object the zone does not track (no state object with that tag, never set
 * by a trigger result) passes, which keeps the behaviour of the checks before
 * they were decoded (the Temple's Courage and Mind teleporter pads, which
 * check DynaTrigger_KT_Teleporter* "On" that nothing on the server sets).
 *
 * TODO:
 * - What sets DynaTrigger_KT_TeleporterMind "On" in 2009 (the StartMind event) is not known.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Imlight.Classic.Quests;

/// <summary>
/// KingsIsle's ReqState requirement class and property hashes.
/// </summary>
public static class ReqStateIds {

    /// <summary>The hash of "class ReqState".</summary>
    public const uint ClassHash = 456212888;

    /// <summary>m_applyNOT (bool).</summary>
    public const uint ApplyNotHash = 0x6816DA0A;

    /// <summary>m_operator (enum Requirement::Operator).</summary>
    public const uint OperatorHash = 0x9F4F92FD;

    /// <summary>m_triggerObjName (std::string): the object's zone tag.</summary>
    public const uint ObjectNameHash = 3336963211;

    /// <summary>m_triggerObjState (std::string): the state the object must be in.</summary>
    public const uint ObjectStateHash = 2067625387;

}

/// <summary>
/// The current state of each tracked state object of one zone instance. Safe to share between actors.
/// </summary>
public sealed class ObjectStateTable {

    private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);

    /// <summary>Starts tracking <paramref name="tag"/> in <paramref name="startState"/>; a tracked tag keeps its state.</summary>
    public void Track(string? tag, string? startState) {
        if (!string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(startState)) {
            _states.TryAdd(tag, startState);
        }
    }

    /// <summary>Sets the state of <paramref name="tag"/>; true when it changed (or was untracked).</summary>
    public bool Set(string? tag, string? state) {
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(state)) {
            return false;
        }

        var changed = true;
        _states.AddOrUpdate(tag, state, (_, old) => {
            changed = !string.Equals(old, state, StringComparison.Ordinal);
            return state;
        });

        return changed;
    }

    /// <summary>The state of <paramref name="tag"/>, when tracked.</summary>
    public bool TryGet(string? tag, out string state) {
        state = "";
        if (string.IsNullOrEmpty(tag) || !_states.TryGetValue(tag, out var found)) {
            return false;
        }

        state = found;
        return true;
    }

}

/// <summary>
/// The ReqState check and the state events.
/// </summary>
public static class ObjectStateRules {

    /// <summary>The suffix of the event an object posts when it enters a state.</summary>
    public const string EnterStateSuffix = ".EnterState";

    /// <summary>
    /// ReqState: true when the object is in <paramref name="state"/>. An untracked object (or no table) passes.
    /// </summary>
    public static bool Met(ObjectStateTable? table, string? tag, string? state) {
        if (table is null || !table.TryGet(tag, out var current)) {
            return true;
        }

        return string.Equals(current, state, StringComparison.Ordinal);
    }

    /// <summary>"&lt;tag&gt;.&lt;state&gt;.EnterState".</summary>
    public static string EnterStateEvent(string tag, string state)
        => $"{tag}.{state}{EnterStateSuffix}";

    /// <summary>Splits an EnterState event into its tag and state (the state is the last dotted part before the suffix).</summary>
    public static bool TryParseEnterState(string? eventName, out string tag, out string state) {
        tag = state = "";
        if (string.IsNullOrEmpty(eventName) || !eventName.EndsWith(EnterStateSuffix, StringComparison.Ordinal)) {
            return false;
        }

        var body = eventName[..^EnterStateSuffix.Length];
        var dot = body.LastIndexOf('.');
        if (dot <= 0 || dot == body.Length - 1) {
            return false;
        }

        tag = body[..dot];
        state = body[(dot + 1)..];
        return true;
    }

    /// <summary>The zone tags whose state events <paramref name="events"/> (a zone's trigger events) listen for.</summary>
    public static HashSet<string> ListenedTags(IEnumerable<string?> events) {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in events) {
            if (TryParseEnterState(name, out var tag, out _)) {
                tags.Add(tag);
            }
        }

        return tags;
    }

}

/// <summary>A state of a state set as the click needs it: its transitions and, for a transition state, where it ends.</summary>
/// <param name="Name">m_stateName.</param>
/// <param name="Transitions">m_transitions: (m_targetState, m_transitionState).</param>
/// <param name="AutoState">m_autoState when m_autoTransition, else null.</param>
public sealed record StateNode(string Name, IReadOnlyList<(string Target, string? Via)> Transitions, string? AutoState = null);

/// <summary>
/// What clicking a state object does: it takes the current state's first transition.
/// </summary>
public static class StateClick {

    /// <summary>
    /// The state a click leads to (<paramref name="next"/>, the state the object rests in) and the state to show the
    /// client (<paramref name="shown"/>: the transition state, whose animation ends in the next state, or the next
    /// state itself). False when the current state has no transition.
    /// </summary>
    public static bool TryNext(IReadOnlyCollection<StateNode> states, string? current, out string next, out string shown) {
        next = shown = "";
        StateNode? node = null;
        foreach (var candidate in states) {
            if (string.Equals(candidate.Name, current, StringComparison.Ordinal)) {
                node = candidate;
                break;
            }
        }

        if (node is null || node.Transitions.Count == 0 || string.IsNullOrEmpty(node.Transitions[0].Target)) {
            return false;
        }

        var (target, via) = node.Transitions[0];
        next = target;
        // A target that is itself an auto transition (Turn_On) rests in its auto state (Idle_On).
        foreach (var candidate in states) {
            if (string.Equals(candidate.Name, target, StringComparison.Ordinal) && !string.IsNullOrEmpty(candidate.AutoState)) {
                next = candidate.AutoState!;
                shown = target;
                return true;
            }
        }

        shown = string.IsNullOrEmpty(via) ? target : via!;
        return true;
    }

}
