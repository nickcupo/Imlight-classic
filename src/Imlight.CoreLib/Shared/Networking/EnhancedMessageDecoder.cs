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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>Bounds each record before dispatch, including mixed stock/extension frames.</summary>
internal static class EnhancedMessageDecoder {
    internal static IReadOnlyCollection<IMessage> Decode(byte[] frame) {
        if (frame.Length < 9 || BinaryPrimitives.ReadUInt16LittleEndian(frame) != 0xF00D)
            throw new InvalidDataException("Invalid frame");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));
        if ((length & 0x8000) != 0 || length != frame.Length - 4 || frame[^1] != 0)
            throw new InvalidDataException("Invalid frame length");
        if (frame[4] == 1) return MessageEncoder.Decode(frame);
        if (frame[4] != 0) throw new InvalidDataException("Invalid control flag");
        var result = new List<IMessage>();
        for (var offset = 8; offset < frame.Length - 1;) {
            if (frame.Length - 1 - offset < 4) throw new InvalidDataException("Truncated message header");
            var size = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset + 2));
            if (size < 4 || size > frame.Length - 1 - offset) throw new InvalidDataException("Invalid message length");
            if (frame[offset] == EnhancedClassicProtocol.Service) {
                if (frame[offset + 1] is not (1 or 3)) throw new InvalidDataException("Server-only enhancement message");
                var message = EnhancedClassicProtocol.Dispatch(frame[offset + 1]);
                var body = frame.AsSpan(offset + 4, size - 4).ToArray();
                var reader = new BitReader(body);
                message.Decode(reader);
                if (reader.BitPos() != body.Length * 8) throw new InvalidDataException("Unexpected enhancement data");
                result.Add(message);
            } else {
                // Decode a bounded stock record using the existing generated protocol implementation.
                var single = new byte[8 + size + 1];
                frame.AsSpan(0, 8).CopyTo(single);
                BinaryPrimitives.WriteUInt16LittleEndian(single.AsSpan(2), (ushort)(single.Length - 4));
                frame.AsSpan(offset, size).CopyTo(single.AsSpan(8));
                var stock = MessageEncoder.Decode(single);
                if (stock != null) result.AddRange(stock);
            }
            offset += size;
        }
        return result;
    }
}
