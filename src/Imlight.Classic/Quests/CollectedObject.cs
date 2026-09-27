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
 * PER-PLAYER COLLECTION OBJECTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: who has picked up a collection object (a usage goal counted
 * above 1: crates, barrels, mushrooms) and when it comes back for them.
 *
 * USAGE EXAMPLE:
 * var taken = new CollectedObject<IActorRef>(TimeSpan.FromSeconds(30));
 * taken.Take(player, now); taken.IsTakenBy(player, now);
 *
 * NOTE:
 * Stock Imlight deletes the object for the whole zone on use, so in a
 * grouped zone the second wizard cannot finish (MB_KatzLab places exactly
 * the 3 crates "If You Build It..." counts), and a goal counting more uses
 * than there are objects (MS_MushroomPoison: 8 counted, 5 placed) can
 * never finish. Here the object leaves only the collector's view and comes
 * back for them after the respawn delay.
 *
 * TODO:
 * - The respawn delay is our estimate; KingsIsle's own delay for 2009 collection objects is not documented.
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
/// One collection object's per-player state: taken by whom, and until when.
/// </summary>
/// <typeparam name="TPlayer">The player key (a session actor on the server).</typeparam>
/// <param name="respawnDelay">How long the object stays gone for the player who took it.</param>
public sealed class CollectedObject<TPlayer>(TimeSpan respawnDelay) where TPlayer : notnull {

    /// <summary>The respawn delay the server uses for collection objects (estimate).</summary>
    public static readonly TimeSpan DefaultRespawnDelay = TimeSpan.FromSeconds(30);

    private readonly Dictionary<TPlayer, DateTime> _takenUntil = [];

    /// <summary>How long the object stays gone for its collector.</summary>
    public TimeSpan RespawnDelay { get; } = respawnDelay;

    /// <summary>Marks the object taken by <paramref name="player"/> at <paramref name="now"/>.</summary>
    /// <returns>When the object comes back for that player.</returns>
    public DateTime Take(TPlayer player, DateTime now) {
        var until = now + RespawnDelay;
        _takenUntil[player] = until;

        return until;
    }

    /// <summary>True while the object is gone for <paramref name="player"/>.</summary>
    public bool IsTakenBy(TPlayer player, DateTime now)
        => _takenUntil.TryGetValue(player, out var until) && now < until;

    /// <summary>Gives the object back to <paramref name="player"/> (respawn or leaving the zone).</summary>
    /// <returns>True when the player had taken it.</returns>
    public bool Release(TPlayer player)
        => _takenUntil.Remove(player);

    /// <summary>The players whose respawn time has passed; they are released.</summary>
    public IReadOnlyList<TPlayer> ReleaseDue(DateTime now) {
        var due = _takenUntil.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList();
        foreach (var player in due) {
            _takenUntil.Remove(player);
        }

        return due;
    }

}
