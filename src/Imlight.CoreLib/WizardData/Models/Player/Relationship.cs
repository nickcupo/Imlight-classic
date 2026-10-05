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
*/

using System;
using System.Collections.Generic;
using Imlight.CoreLib.Shared.Utilities;
using Newtonsoft.Json;

namespace Imlight.CoreLib.WizardData.Models.Player;

public class Relationship {

    public ulong RelationshipId { get; init; }
    public ulong FirstPlayerId { get; set; }
    public ulong SecondPlayerId { get; set; }
    public bool AddedViaTrueFriend { get; set; }
    public bool BestFriends { get; set; }
    /// <summary>
    /// True while either wizard ignores the other (the friendship is suspended). CLASSIC: kept in step with
    /// <see cref="BlockedBy"/> (Classic.IgnoreRules) so a database query can still filter on it; it says nothing about
    /// who ignored whom. Before player data schema 3 it was the only ignore flag, read as "FirstPlayerId ignores
    /// SecondPlayerId" by chat and as "either way" by the ignore list (multiplayer audit item C).
    /// </summary>
    public bool Blocked { get; set; }
    /// <summary>
    /// CLASSIC (player data schema 3): the character ids of the wizards on this row who ignore the other one. An ignore
    /// belongs to its owner: A ignoring B hides B's chat, whispers and friend requests from A only.
    /// </summary>
    public List<ulong> BlockedBy { get; set; } = [];
    public uint RelationshipEpochInSeconds { get; set; }
    public bool IsBrokenUp { get; set; }

    // ctor
    public Relationship(ulong firstPlayerId,
                        ulong secondPlayerId,
                        bool addedViaTrueFriend,
                        bool bestFriends,
                        bool blocked,
                        bool isBrokenUp) {
        this.RelationshipId = RandomGen.GenerateGUID();
        this.FirstPlayerId = firstPlayerId;
        this.SecondPlayerId = secondPlayerId;
        this.AddedViaTrueFriend = addedViaTrueFriend;
        this.BestFriends = bestFriends;
        this.Blocked = blocked;
        this.BlockedBy = blocked ? [firstPlayerId] : []; // CLASSIC: the first player is the one ignoring
        this.RelationshipEpochInSeconds = (uint)DateTimeOffset.Now.ToUnixTimeSeconds();
        this.IsBrokenUp = isBrokenUp;
    }

    // Empty ctor for database deserialization; assume all values are set
    // after deserialization.
    [JsonConstructor]
    public Relationship() { }
    
}