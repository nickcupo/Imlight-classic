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
 * CLASSIC ZONE DATA TYPES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: KingsIsle's ReqEncounterComplete zone-trigger requirement, which
 * used to load as null and pass (the Tree of Life spirit-world portal, the
 * Temple of Storms exit, the Crimson Fields back gate, ...).
 *
 * USAGE EXAMPLE:
 * Registered in ClassicZoneTypeRegistry; checked by ClassicReqEncounterCompleteHandler.
 *
 * NOTE:
 * Hashes, provenance and meaning: Imlight.Classic.Quests.EncounterComplete.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic.Cinematics;

namespace Imlight.CoreLib.Classic;

/// <summary>ReqEncounterComplete: the wizard has completed <see cref="m_encounterName"/>.</summary>
public sealed record ClassicReqEncounterComplete : Requirement {

    public string m_encounterName { get; set; } = "";

    public override uint GetHash() => EncounterComplete.ClassHash;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            base.Decode(reader, serializer);
            m_encounterName = reader.ReadString();

            return true;
        }

        return VersionableProperties.Read(reader, hash => {
            switch (hash) {
                case ReqStateIds.ApplyNotHash:
                    m_applyNOT = reader.ReadBit();
                    break;
                case ReqStateIds.OperatorHash:
                    if (serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)) {
                        string raw = reader.ReadString();
                        var name = raw.Replace('-', '_');
                        name = name[(name.LastIndexOf(':') + 1)..];
                        m_operator = Enum.TryParse<Operator>(name, true, out var op) ? op : default;
                    } else {
                        m_operator = (Operator) Enum.ToObject(typeof(Operator), reader.ReadUInt32());
                    }
                    break;
                case EncounterComplete.EncounterNameHash:
                    m_encounterName = reader.ReadString();
                    break;
            }
        });
    }

}
