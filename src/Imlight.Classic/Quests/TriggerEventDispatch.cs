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
 * Decides which of a zone's triggers fire on one posted event: per-player
 * arming (TriggerActivation), requirements, and one teleport per event.
 *
 * USAGE EXAMPLE:
 * var fires = dispatch.Dispatch(orderedTriggers, t => t.Actor, eventName, player, listens, meetsRequirements, teleportsSomewhere);
 *
 * NOTE:
 * Client wads pair triggers on one event, for example a quest door ahead of
 * the plain door on the same volume. A teleport without a destination does
 * nothing on the classic engine, so it never takes the event's one teleport
 * from a later trigger.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Quests;

/// <summary>
/// One trigger that fires on a posted event.
/// </summary>
/// <param name="Trigger">The trigger.</param>
/// <param name="SuppressTeleport">True when an earlier trigger already teleports the player on this event; the trigger's own teleports, if any, are dropped.</param>
/// <typeparam name="T">The caller's trigger entry.</typeparam>
public readonly record struct TriggerFire<T>(T Trigger, bool SuppressTeleport);

/// <summary>
/// The per-event trigger decision of a zone, with each tracked trigger's arming state.
/// </summary>
/// <typeparam name="TKey">Identifies a trigger, such as its actor.</typeparam>
/// <typeparam name="TPlayer">The player key, such as the player's actor.</typeparam>
public sealed class TriggerEventDispatch<TKey, TPlayer> where TKey : notnull where TPlayer : class {

    private readonly Dictionary<TKey, (string Name, TriggerActivation<TPlayer> State)> _activation;
    private readonly IEqualityComparer<TPlayer>? _players;

    /// <summary>
    /// Creates an empty dispatch.
    /// </summary>
    /// <param name="triggers">Compares trigger keys; null uses the default comparer.</param>
    /// <param name="players">Compares player keys; null uses the default comparer.</param>
    public TriggerEventDispatch(IEqualityComparer<TKey>? triggers = null, IEqualityComparer<TPlayer>? players = null) {
        _activation = new Dictionary<TKey, (string, TriggerActivation<TPlayer>)>(triggers);
        _players = players;
    }

    /// <summary>
    /// Forgets every tracked trigger, as when the zone reloads.
    /// </summary>
    public void Clear()
        => _activation.Clear();

    /// <summary>
    /// Tracks a trigger's arming when it has a deactivate event or starts disarmed; other triggers are always armed.
    /// </summary>
    /// <param name="trigger">The trigger's key.</param>
    /// <param name="name">The trigger's name, for logs.</param>
    /// <param name="activateEvents">The trigger's m_activateEvents.</param>
    /// <param name="deactivateEvents">The trigger's m_deactivateEvents.</param>
    /// <param name="initiallyArmed">False for a trigger that waits for one of its activate events (ZoneTriggerLiveness).</param>
    /// <returns>True when the trigger is tracked.</returns>
    public bool Track(TKey trigger, string? name, IEnumerable<string?>? activateEvents, IEnumerable<string?>? deactivateEvents,
                      bool initiallyArmed = true) {
        var state = new TriggerActivation<TPlayer>(activateEvents, deactivateEvents, _players, initiallyArmed);
        if (!state.CanChange) {
            return false;
        }

        _activation[trigger] = (name ?? "", state);

        return true;
    }

    /// <summary>
    /// True when the trigger may fire for <paramref name="player"/>.
    /// </summary>
    /// <param name="trigger">The trigger's key.</param>
    /// <param name="player">The player; null for an event with no player.</param>
    /// <returns>False only while a deactivate event from the player is in force.</returns>
    public bool IsArmed(TKey trigger, TPlayer? player)
        => !_activation.TryGetValue(trigger, out var entry) || entry.State.IsArmed(player);

    /// <summary>
    /// Decides the fires for one event. Every tracked trigger observes the event first, so an event that
    /// disarms a trigger also keeps it from firing on that event. Then, in data order, a trigger fires when it
    /// listens for the event, is armed for the player and meets its requirements; requirements are evaluated
    /// only for triggers that got that far. The first firing trigger that teleports somewhere keeps its
    /// teleport and every later one fires with its teleports suppressed. A teleport without a destination
    /// ahead of it is not suppressed.
    /// </summary>
    /// <param name="orderedTriggers">The zone's triggers in data order.</param>
    /// <param name="key">The trigger's key, as given to <see cref="Track"/>.</param>
    /// <param name="eventName">The posted event.</param>
    /// <param name="player">Whose event it is; null for an event with no player.</param>
    /// <param name="listens">True when the trigger's m_fireEvents hold the event.</param>
    /// <param name="meetsRequirements">True when the player meets the trigger's requirements.</param>
    /// <param name="teleportsSomewhere">True when the trigger has a teleport with a destination.</param>
    /// <param name="stateChanged">Told the key, name and new arming of each trigger the event re-armed or disarmed.</param>
    /// <typeparam name="T">The caller's trigger entry.</typeparam>
    /// <returns>The firing triggers in data order.</returns>
    public List<TriggerFire<T>> Dispatch<T>(IEnumerable<T> orderedTriggers,
                                            Func<T, TKey> key,
                                            string? eventName,
                                            TPlayer? player,
                                            Func<T, bool> listens,
                                            Func<T, bool> meetsRequirements,
                                            Func<T, bool> teleportsSomewhere,
                                            Action<TKey, string, bool>? stateChanged = null) {
        ArgumentNullException.ThrowIfNull(orderedTriggers);

        foreach (var (trigger, (name, state)) in _activation) {
            if (state.Observe(eventName, player)) {
                stateChanged?.Invoke(trigger, name, state.IsArmed(player));
            }
        }

        var fires = new List<TriggerFire<T>>();
        var teleportTaken = false;
        foreach (var trigger in orderedTriggers) {
            if (!listens(trigger) || !IsArmed(key(trigger), player) || !meetsRequirements(trigger)) {
                continue;
            }

            fires.Add(new TriggerFire<T>(trigger, teleportTaken));
            teleportTaken |= teleportsSomewhere(trigger);
        }

        return fires;
    }

}
