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
 * A trigger starts armed, as stock Imlight treats every trigger, because the
 * server never posts StartZone, the activate event of most triggers. In
 * Rattlebones' tower (WC_Unicorn_T2), TR_ActivateCombat posts
 * Disable_TR_ActivateCombat and then ActivateFrom_TR_ActivateCombat, one of
 * its own fire events; the disarm is what keeps it from firing again.
 * 
 * TODO:
 * - Is KingsIsle's trigger state per zone instance rather than per player? Per player keeps two players in a shared zone apart.
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
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
    private readonly HashSet<TPlayer> _disarmedFor;
    private bool _disarmedForNoPlayer;

    /// <summary>
    /// Creates the state of one trigger, armed for everyone.
    /// </summary>
    /// <param name="activateEvents">The trigger's m_activateEvents.</param>
    /// <param name="deactivateEvents">The trigger's m_deactivateEvents.</param>
    /// <param name="players">Compares player keys; null uses the default comparer.</param>
    public TriggerActivation(IEnumerable<string?>? activateEvents,
                             IEnumerable<string?>? deactivateEvents,
                             IEqualityComparer<TPlayer>? players = null) {
        _activateEvents = ToSet(activateEvents);
        _deactivateEvents = ToSet(deactivateEvents);
        _disarmedFor = new HashSet<TPlayer>(players ?? EqualityComparer<TPlayer>.Default);
    }

    /// <summary>
    /// True when the trigger has a deactivate event, so its state can ever change.
    /// </summary>
    public bool CanDisarm => _deactivateEvents.Count > 0;

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
    /// <returns>True unless a deactivate event from the player is still in force.</returns>
    public bool IsArmed(TPlayer? player)
        => player is null ? !_disarmedForNoPlayer : !_disarmedFor.Contains(player);

    private void SetArmed(TPlayer? player, bool armed) {
        if (player is null) {
            _disarmedForNoPlayer = !armed;
        } else if (armed) {
            _disarmedFor.Remove(player);
        } else {
            _disarmedFor.Add(player);
        }
    }

    private static HashSet<string> ToSet(IEnumerable<string?>? events)
        => events is null ? new HashSet<string>(StringComparer.Ordinal)
            : events.OfType<string>().Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);

}
