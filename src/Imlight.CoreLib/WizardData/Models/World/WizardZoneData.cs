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

using Imcodec.ObjectProperty.TypeCache;
using System.Collections.Generic;

namespace Imlight.CoreLib.WizardData.Models.World;

public class WizardZoneData {

    public string ZoneName { get; set; }
    public List<WizardTeleportData> Teleports { get; set; } = [];

    // CLASSIC: an overlay record with "Merge": true adds its entries to the zone's loaded record by trigger name
    // (a null Teleport removes one) instead of replacing the record (SpiralDB.LoadZoneData, ZoneTransferMerge).
    public bool Merge { get; set; }

    // CLASSIC: the classic travel overlay's zone-level data (the teleport stones' discovery triggers), or null.
    public ClassicZoneTravel Classic { get; set; }

}

public class WizardTeleportData {

    public string TriggerName { get; set; }
    public ResTeleport Teleport { get; set; }

    // CLASSIC: the classic travel overlay's data for a press-X teleport object's entry, or null.
    public ClassicTeleportObject Classic { get; set; }

}

// CLASSIC: a press-X teleport object's classic travel data (InteractTeleportObjectComponent). The entry is named after
// the placed object's m_zoneTag; two placements that share a tag get "<tag>#<n>" entries told apart by At.
public class ClassicTeleportObject {

    // The placement's location (X, Y, Z), telling apart the entries of placements that share a tag.
    public List<float> At { get; set; }

    // The profile feature the object needs, such as hub_teleporters (the Oct 2009 Marleybone and MooShu stones).
    public string Feature { get; set; }

    // Client tags the wizard must have turned "On" (per-wizard dynamods, set by the zone's discovery trigger) before
    // the object teleports: a teleport stone works once its pair is discovered.
    public List<string> DiscoveredBy { get; set; }

    // The template InteractableBehavior's icon, title key and prompt key (the client's "Teleport Stone" / "Press X").
    public string Icon { get; set; }
    public string TitleKey { get; set; }
    public string TextKey { get; set; }

}

// CLASSIC: a zone's classic travel data.
public class ClassicZoneTravel {

    // Teleport stone discovery triggers: in the r806919 client they fire on EnterZone (every stone is found on
    // arrival); with the profile's rules.teleport_stones: discover they fire only on their volume beside the far
    // stone, as in 2009 ("discover both points before you can teleport between them").
    public List<ClassicDiscoveryTrigger> DiscoveryTriggers { get; set; }

}

public class ClassicDiscoveryTrigger {

    public string Trigger { get; set; }
    public string Event { get; set; }

}
