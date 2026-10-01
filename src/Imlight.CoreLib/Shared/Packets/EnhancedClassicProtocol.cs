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
using System.IO;
using System.Text.Json;
using Imcodec.IO;
using Imcodec.MessageLayer;

namespace Imlight.CoreLib.Shared.Packets;

/// <summary>Opt-in extension. Stock clients never receive these messages.</summary>
public static class EnhancedClassicProtocol {
    public const byte Service = 90;
    public const ushort Version = 1;
    public const int MaximumPayloadBytes = 24_000;
    public abstract class Message : IMessage {
        public abstract byte MessageOrder { get; }
        public byte ServiceId => Service;
        public byte AccessLevel => 0;
        public abstract void Encode(BitWriter writer);
        public abstract void Decode(BitReader reader);
    }
    public sealed class Hello : Message {
        public override byte MessageOrder => 1;
        public ushort ProtocolVersion { get; set; } = Version;
        public bool StrictClassic { get; set; }
        public override void Encode(BitWriter w) { w.WriteUInt16(ProtocolVersion); w.WriteUInt8(StrictClassic ? (byte)1 : (byte)0); }
        public override void Decode(BitReader r) { ProtocolVersion = r.ReadUInt16(); var flag = r.ReadUInt8(); if (flag > 1) throw new InvalidDataException("Invalid profile"); StrictClassic = flag == 1; }
    }
    public sealed class Capabilities : Message {
        public override byte MessageOrder => 2;
        public ushort ProtocolVersion { get; set; } = Version;
        public uint Flags { get; set; }
        public override void Encode(BitWriter w) { w.WriteUInt16(ProtocolVersion); w.WriteUInt32(Flags); }
        public override void Decode(BitReader r) { ProtocolVersion = r.ReadUInt16(); Flags = r.ReadUInt32(); }
    }
    public sealed class MinionRequest : Message {
        public override byte MessageOrder => 3;
        public ulong DuelID { get; set; }
        public int Round { get; set; }
        public ulong MinionID { get; set; }
        public uint RequestID { get; set; }
        public bool Query { get; set; }
        public byte MoveType { get; set; }
        public byte SpellSelection { get; set; }
        public uint SpellTarget { get; set; }
        public override void Encode(BitWriter w) { w.WriteUInt64(DuelID); w.WriteInt32(Round); w.WriteUInt64(MinionID); w.WriteUInt32(RequestID); w.WriteUInt8(Query ? (byte)1 : (byte)0); w.WriteUInt8(MoveType); w.WriteUInt8(SpellSelection); w.WriteUInt32(SpellTarget); }
        public override void Decode(BitReader r) { DuelID = r.ReadUInt64(); Round = r.ReadInt32(); MinionID = r.ReadUInt64(); RequestID = r.ReadUInt32(); var q = r.ReadUInt8(); if(q > 1) throw new InvalidDataException("Invalid request"); Query = q == 1; MoveType = r.ReadUInt8(); SpellSelection = r.ReadUInt8(); SpellTarget = r.ReadUInt32(); }
    }
    /// <summary>Bounded UTF-8 JSON response, used only by extension-aware clients; never stock Monstrology payloads.</summary>
    public sealed class MinionState : Message {
        public override byte MessageOrder => 4;
        public string Payload { get; set; } = "{}";
        public override void Encode(BitWriter w) {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Payload);
            if(bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("State too large");
            w.WriteUInt16((ushort)bytes.Length); foreach(var b in bytes) w.WriteUInt8(b);
        }
        public override void Decode(BitReader r) {
            var length = r.ReadUInt16(); if(length > MaximumPayloadBytes) throw new InvalidDataException("State too large");
            var bytes = new byte[length]; for(var i = 0; i < length; i++) bytes[i] = r.ReadUInt8();
            Payload = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(Payload);
        }
    }
    internal static Message Dispatch(byte order) => order switch {
        1 => new Hello(), 2 => new Capabilities(), 3 => new MinionRequest(), 4 => new MinionState(),
        _ => throw new InvalidDataException("Unknown enhancement message")
    };
}
