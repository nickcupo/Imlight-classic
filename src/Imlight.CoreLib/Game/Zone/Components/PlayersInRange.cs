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
 * PLAYERS IN RANGE
 * ========================================================================
 *
 * PURPOSE:
 * The players within one radius of a zone object, by game object id, with the
 * session actor to send to.
 *
 * USAGE EXAMPLE:
 * if (_inInteractionRange.Update(playerId, playerActor, IsInRadius(playerObj, radius)) == RangeChange.Entered) { ... }
 *
 * NOTE:
 * A wizard keeps its game object id across sessions, but every login and zone
 * transfer gives it a new session actor. The service memento component kept
 * the first actor it saw for an id; when that actor was gone (a removal lost to
 * a move flushed after it), the wizard's later sessions never got the object's
 * quest markers or options again until the server restarted. A move from a new
 * actor for a known id now replaces the old actor and counts as entering.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// What an update did to a player's place in the range.
/// </summary>
internal enum RangeChange {
    Unchanged,
    Entered,
    Left,
}

/// <summary>
/// The players within one radius of a zone object.
/// </summary>
internal sealed class PlayersInRange {

    private readonly Dictionary<ulong, IActorRef> _players = [];

    public int Count => _players.Count;

    /// <summary>
    /// The session actors of the players in range.
    /// </summary>
    public IActorRef[] Actors => [.. _players.Values];

    /// <summary>
    /// Records where a player is.
    /// </summary>
    /// <param name="playerId">The player's game object id.</param>
    /// <param name="playerActor">The player's session actor.</param>
    /// <param name="inRange">True when the player is within the radius.</param>
    /// <returns><see cref="RangeChange.Entered"/> when the player came into range, or is in range with a new session
    /// actor; <see cref="RangeChange.Left"/> when the player went out of range; else <see cref="RangeChange.Unchanged"/>.</returns>
    public RangeChange Update(ulong playerId, IActorRef playerActor, bool inRange) {
        var known = _players.TryGetValue(playerId, out var current);
        if (!inRange) {
            return known && _players.Remove(playerId) ? RangeChange.Left : RangeChange.Unchanged;
        }

        if (known && Equals(current, playerActor)) {
            return RangeChange.Unchanged;
        }

        _players[playerId] = playerActor;

        return RangeChange.Entered;
    }

    /// <summary>
    /// Forgets every entry of a session actor.
    /// </summary>
    /// <param name="playerActor">The session actor of a player who left the zone.</param>
    /// <returns>True if the actor was in range.</returns>
    public bool Remove(IActorRef playerActor) {
        var ids = _players.Where(pair => Equals(pair.Value, playerActor)).Select(pair => pair.Key).ToList();
        foreach (var id in ids) {
            _players.Remove(id);
        }

        return ids.Count > 0;
    }

    /// <summary>
    /// True when the session actor is in range.
    /// </summary>
    public bool Contains(IActorRef playerActor)
        => _players.ContainsValue(playerActor);

}
