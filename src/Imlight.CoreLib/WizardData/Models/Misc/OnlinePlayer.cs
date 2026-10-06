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

namespace Imlight.CoreLib.WizardData.Models.Misc;

public class OnlinePlayer {

    public ushort SessionId;
    public ulong AccountId;
    public ulong CharacterId;
    public string CurrentZone;
    public string CurrentZoneDisplayName;
    public string CurrentRealm;
    public string ActorPath;

    /// <summary>CLASSIC: the instance (owner or sigil run) of the zone the player is in; 0 for a public zone.</summary>
    public ulong InstanceOwnerId;
    public ulong HousingDeedId; // CLASSIC: ephemeral, validated house identity; never a client claim.

    /// <summary>CLASSIC: the hard player limit of the zone the player is in.</summary>
    public int ZoneHardLimit;

}
