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
 * TUTORIAL TIP LOG
 * ========================================================================
 *
 * PURPOSE:
 * The r806919 client's record of the tutorial tips a wizard has read
 * (MSG_UPDATETUTORIALTIPLOG.TutorialTipData), so a read tip stays read
 * after a relog.
 *
 * NOTE:
 * Layout from TutorialLogBehaviorBase::OnPostLoad (0x141f258b0), little-endian:
 * uint32 magic 0x2DEC0DED, uint32 version 1, uint16 count, count x uint32 tip id.
 * The client asks for the log at login (MSG_REQUESTTUTORIALTIPLOG), reports
 * each read tip (MSG_READTUTORIALTIP), and may send the whole log back
 * (MSG_UPDATETUTORIALTIPLOG, 0x1413045f0 / 0x1413048d0 after dropping ids it
 * no longer knows).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/17/2026
 */

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Net;

/// <summary>Encodes and decodes the client's tutorial tip log.</summary>
public static class TutorialTipLog {

    public const uint Magic = 0x2DEC0DED;
    public const uint Version = 1;

    /// <summary>The most tips one log holds (its count is 16-bit).</summary>
    public const int MaxTips = ushort.MaxValue;

    /// <summary>The log for these tip ids (distinct, in the order given).</summary>
    public static byte[] Encode(IEnumerable<uint> tips) {
        var ids = tips.Distinct().Take(MaxTips).ToArray();
        var data = new byte[10 + 4 * ids.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort) ids.Length);
        for (var i = 0; i < ids.Length; i++) {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10 + 4 * i), ids[i]);
        }

        return data;
    }

    /// <summary>The tip ids in a log the client sent; false when it is not one (wrong magic, version or length).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out List<uint> tips) {
        tips = [];
        if (data.Length < 10 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
                || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != Version) {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (data.Length < 10 + 4 * count) {
            return false;
        }

        for (var i = 0; i < count; i++) {
            tips.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[(10 + 4 * i)..]));
        }

        tips = [.. tips.Distinct()];
        return true;
    }

}
