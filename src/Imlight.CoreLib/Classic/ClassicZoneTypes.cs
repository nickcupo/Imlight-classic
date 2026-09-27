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
 * CLASSIC: reads the requirement class KingsIsle's Monster_Killed zone
 * triggers use (and, through the same registry, the cinematic results in
 * Cinematics/ClassicCinematicResults.cs), which the generated type registries do not know, so the
 * triggers keep their check instead of loading it as null (which passes).
 *
 * USAGE EXAMPLE:
 * var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
 *
 * NOTE:
 * The class is server-only (the r806919 type dump does not list it); its
 * hash is 1826357494 and its properties hash as m_applyNOT, m_operator,
 * m_checkTarget (bool) and m_adjectiveList (std::string vector), the names
 * recovered by hashing the client's property names. Everything else falls
 * through to the client registry, then to the server registry, as before.
 * KilledMonsterScope carries the defeated monsters of the Monster_Killed
 * event being evaluated, on the evaluating thread.
 *
 * TODO:
 * - KingsIsle's name for the class is unknown; ClassicReqMonsterKilled is ours.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Cinematics;
using Action = System.Action;
using Type = System.Type;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The Monster_Killed trigger requirement: the defeated monster is one of <see cref="m_adjectiveList"/>.
/// </summary>
public sealed record ClassicReqMonsterKilled : Requirement {

    public const uint CLASS_HASH = 1826357494;
    private const uint APPLY_NOT_HASH = 0x6816DA0A;
    private const uint OPERATOR_HASH = 0x9F4F92FD;
    private const uint CHECK_TARGET_HASH = 0x64407CD8;
    private const uint ADJECTIVE_LIST_HASH = 0xBE731606;

    public bool m_checkTarget { get; set; }
    public List<string> m_adjectiveList { get; set; } = [];

    public override uint GetHash() => CLASS_HASH;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            base.Encode(writer, serializer);
            writer.WriteBit(m_checkTarget);
            WriteVectorSize(writer, m_adjectiveList.Count, serializer);
            foreach (var entry in m_adjectiveList) {
                writer.WriteString(entry);
            }

            return true;
        }

        var objectStart = writer.BitPos();
        writer.WriteUInt32(0);
        WriteProperty(writer, APPLY_NOT_HASH, () => writer.WriteBit(m_applyNOT));
        WriteProperty(writer, OPERATOR_HASH, () => {
            if (serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)) {
                writer.WriteString(m_operator.ToString());
            } else {
                writer.WriteUInt32((uint) (int) m_operator);
            }
        });
        WriteProperty(writer, CHECK_TARGET_HASH, () => writer.WriteBit(m_checkTarget));
        WriteProperty(writer, ADJECTIVE_LIST_HASH, () => {
            WriteVectorSize(writer, m_adjectiveList.Count, serializer);
            foreach (var entry in m_adjectiveList) {
                writer.WriteString(entry);
            }
        });
        var objectSize = writer.BitPos() - objectStart;
        writer.SeekBit(objectStart);
        writer.WriteUInt32((uint) objectSize);
        writer.SeekBit(objectStart + objectSize);

        return true;
    }

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            base.Decode(reader, serializer);
            m_checkTarget = reader.ReadBit();
            m_adjectiveList = ReadStrings(reader, serializer);

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
                case APPLY_NOT_HASH:
                    m_applyNOT = reader.ReadBit();
                    break;
                case OPERATOR_HASH:
                    if (serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)) {
                        string raw = reader.ReadString();
                        var name = raw.Replace('-', '_');
                        name = name[(name.LastIndexOf(':') + 1)..];
                        m_operator = Enum.TryParse<Operator>(name, true, out var op) ? op : default;
                    } else {
                        m_operator = (Operator) Enum.ToObject(typeof(Operator), reader.ReadUInt32());
                    }
                    break;
                case CHECK_TARGET_HASH:
                    m_checkTarget = reader.ReadBit();
                    break;
                case ADJECTIVE_LIST_HASH:
                    m_adjectiveList = ReadStrings(reader, serializer);
                    break;
            }

            reader.SeekBit((int) (propertyStart + propertySize));
        }

        reader.SeekBit((int) (objectStart + objectSize));

        return true;
    }

    private static List<string> ReadStrings(BitReader reader, ObjectSerializer serializer) {
        var count = ReadVectorSize(reader, serializer);
        var entries = new List<string>((int) Math.Min(count, 256));
        for (var i = 0; i < count; i++) {
            entries.Add(reader.ReadString());
        }

        return entries;
    }

    private static void WriteProperty(BitWriter writer, uint hash, Action writeValue) {
        var sizeStart = writer.BitPos();
        writer.WriteUInt32(0);
        writer.WriteUInt32(hash);
        writeValue();
        var size = writer.BitPos() - sizeStart;
        writer.SeekBit(sizeStart);
        writer.WriteUInt32((uint) size);
        writer.SeekBit(sizeStart + size);
    }

    private static void WriteVectorSize(BitWriter writer, int size, ObjectSerializer serializer) {
        if (!serializer.SerializerFlags.HasFlag(SerializerFlags.CompactLength)) {
            writer.WriteUInt32((uint) size);

            return;
        }

        if (size < 127) {
            writer.WriteBits((byte) size, 7);
        } else {
            writer.WriteBit(true);
            writer.WriteUInt32((uint) size);
        }
    }

    private static uint ReadVectorSize(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.SerializerFlags.HasFlag(SerializerFlags.CompactLength)) {
            return reader.ReadUInt32();
        }

        var large = reader.ReadBit();

        return reader.ReadBits<uint>(large ? 31 : 7);
    }

}

/// <summary>
/// The client type registry plus the zone data classes only the classic engine reads.
/// </summary>
public sealed class ClassicZoneTypeRegistry : TypeRegistry {

    public static ClassicZoneTypeRegistry Instance { get; } = new();

    private readonly ClientGeneratedTypeRegistry _client = new();
    private readonly Dictionary<uint, Type> _extra = new() {
        [ClassicReqMonsterKilled.CLASS_HASH] = typeof(ClassicReqMonsterKilled),
        // The zone-trigger results that play cinematics (Classic/Cinematics/ClassicCinematicResults.cs).
        [16312488] = typeof(ClassicResPlayCinematic),
        [82637767] = typeof(ClassicResCinematic),
        [145615551] = typeof(ClassicResStartStagedCinematic),
        [ClassicResZoneTimer.TypeHash] = typeof(ClassicResZoneTimer),
    };

    public override void RegisterType(uint hash, Type t)
        => _extra[hash] = t;

    public override Type LookupType(uint hash)
        => _extra.TryGetValue(hash, out var type) ? type : _client.LookupType(hash);

}

/// <summary>
/// The monsters a Monster_Killed event reports, for the requirement handlers evaluating it on this thread.
/// </summary>
public static class KilledMonsterScope {

    [ThreadStatic]
    private static ulong[] t_current;

    /// <summary>The defeated monsters' template IDs of the event being evaluated, or null outside one.</summary>
    public static ulong[] Current => t_current;

    /// <summary>Sets the defeated monsters until the returned scope is disposed.</summary>
    public static IDisposable Enter(ulong[] templateIds) {
        var previous = t_current;
        t_current = templateIds;

        return new Scope(previous);
    }

    private sealed class Scope(ulong[] previous) : IDisposable {

        public void Dispose()
            => t_current = previous;

    }

}
