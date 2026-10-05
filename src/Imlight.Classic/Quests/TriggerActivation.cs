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
 * Tracks, per player, whether a zone trigger is armed: one of its
 * m_deactivateEvents disarms it for the player whose event it was, and one
 * of its m_activateEvents arms it again.
 * 
 * USAGE EXAMPLE:
 * activation.Observe(message.EventName, message.PlayerActor);
 * if (fires && activation.IsArmed(message.PlayerActor)) { ... }
 * 
 * NOTE:
 * A trigger starts armed unless the zone data says it waits for an event
 * (ZoneTriggerLiveness decides; the server never posts StartZone, the
 * activate event of most triggers, so StartZone means armed at start). In
 * Rattlebones' tower (WC_Unicorn_T2), TR_ActivateCombat posts
 * Disable_TR_ActivateCombat and then ActivateFrom_TR_ActivateCombat, one of
 * its own fire events; the disarm is what keeps it from firing again.
 * The "player" key is the state's scope: the zone supervisor passes one key
 * for a whole instanced zone (KingsIsle's trigger state lives in the zone
 * instance) and the player's own key in a public zone, so two players in a
 * shared street stay apart.
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

namespace Imlight.Classic.Quests;

/// <summary>
/// Which players a zone trigger is disarmed for.
/// </summary>
/// <typeparam name="TPlayer">The player key, such as the player's actor.</typeparam>
public sealed class TriggerActivation<TPlayer> where TPlayer : class {

    private readonly HashSet<string> _activateEvents;
    private readonly HashSet<string> _deactivateEvents;
    private readonly bool _initiallyArmed; // CLASSIC: a trigger that waits for its activate event starts disarmed.
    private readonly HashSet<TPlayer> _flippedFor; // the players whose state differs from the initial one
    private bool _flippedForNoPlayer;

    /// <summary>
    /// Creates the state of one trigger, armed (or, with <paramref name="initiallyArmed"/> false, disarmed) for everyone.
    /// </summary>
    /// <param name="activateEvents">The trigger's m_activateEvents.</param>
    /// <param name="deactivateEvents">The trigger's m_deactivateEvents.</param>
    /// <param name="players">Compares player keys; null uses the default comparer.</param>
    /// <param name="initiallyArmed">False for a trigger that waits for one of its activate events.</param>
    public TriggerActivation(IEnumerable<string?>? activateEvents,
                             IEnumerable<string?>? deactivateEvents,
                             IEqualityComparer<TPlayer>? players = null,
                             bool initiallyArmed = true) {
        _activateEvents = ToSet(activateEvents);
        _deactivateEvents = ToSet(deactivateEvents);
        _initiallyArmed = initiallyArmed;
        _flippedFor = new HashSet<TPlayer>(players ?? EqualityComparer<TPlayer>.Default);
    }

    /// <summary>
    /// True when the trigger has a deactivate event, so it can be disarmed.
    /// </summary>
    public bool CanDisarm => _deactivateEvents.Count > 0;

    /// <summary>
    /// True when the trigger's state can ever change: it can be disarmed, or it starts disarmed.
    /// </summary>
    public bool CanChange => CanDisarm || !_initiallyArmed;

    /// <summary>
    /// True when the trigger is armed before any event.
    /// </summary>
    public bool InitiallyArmed => _initiallyArmed;

    /// <summary>
    /// Applies an event posted by <paramref name="player"/>. A deactivate event wins over an activate event
    /// of the same name.
    /// </summary>
    /// <param name="eventName">The posted event.</param>
    /// <param name="player">Whose event it is; null for an event with no player.</param>
    /// <returns>True when the event changed the trigger's state for the player.</returns>
    public bool Observe(string? eventName, TPlayer? player) {
        if (eventName is null) {
            return false;
        }

        var wasArmed = IsArmed(player);
        if (_activateEvents.Contains(eventName)) {
            SetArmed(player, true);
        }

        if (_deactivateEvents.Contains(eventName)) {
            SetArmed(player, false);
        }

        return wasArmed != IsArmed(player);
    }

    /// <summary>
    /// True when the trigger may fire for <paramref name="player"/>.
    /// </summary>
    /// <param name="player">The player; null for an event with no player.</param>
    /// <returns>The initial state unless an event from the player changed it.</returns>
    public bool IsArmed(TPlayer? player)
        => _initiallyArmed != (player is null ? _flippedForNoPlayer : _flippedFor.Contains(player));

    private void SetArmed(TPlayer? player, bool armed) {
        var flipped = armed != _initiallyArmed;
        if (player is null) {
            _flippedForNoPlayer = flipped;
        } else if (flipped) {
            _flippedFor.Add(player);
        } else {
            _flippedFor.Remove(player);
        }
    }

    private static HashSet<string> ToSet(IEnumerable<string?>? events)
        => events is null ? new HashSet<string>(StringComparer.Ordinal)
            : events.OfType<string>().Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);

}
