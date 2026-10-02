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
 * CLASSIC: KingsIsle's ReqState zone-trigger requirement ("this object is in
 * this state"), which the generated type registries do not know, so it used to
 * load as null and pass. The Temple of Storms mind puzzle is made of them.
 *
 * USAGE EXAMPLE:
 * Registered in ClassicZoneTypeRegistry; checked by ClassicReqStateHandler.
 *
 * NOTE:
 * Hashes and provenance: Imlight.Classic.Quests.ReqStateIds.
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

namespace Imlight.CoreLib.Classic;

/// <summary>
/// ReqState: the zone object tagged <see cref="m_triggerObjName"/> is in state <see cref="m_triggerObjState"/>.
/// </summary>
public sealed record ClassicReqState : Requirement {

    public string m_triggerObjName { get; set; } = "";
    public string m_triggerObjState { get; set; } = "";

    public override uint GetHash() => ReqStateIds.ClassHash;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            base.Encode(writer, serializer);
            writer.WriteString(m_triggerObjName);
            writer.WriteString(m_triggerObjState);

            return true;
        }

        var objectStart = writer.BitPos();
        writer.WriteUInt32(0);
        WriteProperty(writer, ReqStateIds.ApplyNotHash, () => writer.WriteBit(m_applyNOT));
        WriteProperty(writer, ReqStateIds.OperatorHash, () => {
            if (serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)) {
                writer.WriteString(m_operator.ToString());
            } else {
                writer.WriteUInt32((uint) (int) m_operator);
            }
        });
        WriteProperty(writer, ReqStateIds.ObjectNameHash, () => writer.WriteString(m_triggerObjName));
        WriteProperty(writer, ReqStateIds.ObjectStateHash, () => writer.WriteString(m_triggerObjState));
        var objectSize = writer.BitPos() - objectStart;
        writer.SeekBit(objectStart);
        writer.WriteUInt32((uint) objectSize);
        writer.SeekBit(objectStart + objectSize);

        return true;
    }

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            base.Decode(reader, serializer);
            m_triggerObjName = reader.ReadString();
            m_triggerObjState = reader.ReadString();

            return true;
        }

        var objectStart = reader.BitPos();
        var objectSize = reader.ReadUInt32();
        while (reader.BitPos() - objectStart < objectSize) {
            var propertyStart = reader.BitPos();
            var propertySize = reader.ReadUInt32();
            var propertyHash = reader.ReadUInt32();
            if (propertySize == 0) {
                return false;
            }

            switch (propertyHash) {
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
                case ReqStateIds.ObjectNameHash:
                    m_triggerObjName = reader.ReadString();
                    break;
                case ReqStateIds.ObjectStateHash:
                    m_triggerObjState = reader.ReadString();
                    break;
            }

            reader.SeekBit((int) (propertyStart + propertySize));
        }

        reader.SeekBit((int) (objectStart + objectSize));

        return true;
    }

    private static void WriteProperty(BitWriter writer, uint hash, System.Action writeValue) {
        var sizeStart = writer.BitPos();
        writer.WriteUInt32(0);
        writer.WriteUInt32(hash);
        writeValue();
        var size = writer.BitPos() - sizeStart;
        writer.SeekBit(sizeStart);
        writer.WriteUInt32((uint) size);
        writer.SeekBit(sizeStart + size);
    }

}
