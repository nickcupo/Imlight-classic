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
 * IGNORE LIST NAMES
 * ========================================================================
 *
 * PURPOSE:
 * MSG_IGNORELIST's ListData with each IgnoreEntryData.m_ignoreName as the
 * wizard's packed name bytes ([0x80|0x82, k1, k2, k3]), exactly as r806919
 * reads them: BuddyListManager's MSG_IgnoreList handler (0x141745e70) copies
 * m_ignoreName byte for byte into a BuddyEntry, whose GetName (0x141738520)
 * hands it to the NameCodec's packed-name decoder (ios-parity, 2026-11-06).
 *
 * USAGE EXAMPLE:
 * IgnoreListNames.TrySerialize(list, packedNames, mask, out var listBytes);
 *
 * NOTE:
 * Imcodec generates std::string properties as C# strings and writes them as
 * UTF-8, so the packed bytes (the gender byte is 0x80 or 0x82) came out as
 * U+FFFD (EF BF BD). The list is serialized with an ASCII stand-in of the
 * same length in each name, then each stand-in, in order, is replaced by the
 * packed bytes; the replaced buffer is read back and every entry's character
 * id must still match, or the plain serialization is used.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 11/06/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>Serializes an ignore list with its names as packed name bytes (see the file header).</summary>
internal static class IgnoreListNames {

    private const byte StandIn = (byte) '~';

    /// <summary>
    /// Serializes <paramref name="list"/> with entry i's m_ignoreName as <paramref name="packedNames"/>[i]. False when
    /// the names cannot be placed (the caller sends the plain serialization).
    /// </summary>
    internal static bool TrySerialize(IgnoreEntryDataList list, IReadOnlyList<byte[]> packedNames, uint mask,
                                      out byte[] bytes) {
        bytes = null;
        var entries = list.m_ignoreDataList ?? [];
        if (entries.Count != packedNames.Count || packedNames.Any(name => name is not { Length: > 0 })) {
            return false;
        }

        var originals = entries.Select(entry => entry.m_ignoreName).ToList();
        try {
            for (var i = 0; i < entries.Count; i++) {
                entries[i].m_ignoreName = new string((char) StandIn, packedNames[i].Length);
            }

            var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
            if (!serializer.Serialize(list, mask, out var written)) {
                return false;
            }

            var buffer = ((byte[]) written).ToArray();
            var at = 0;
            for (var i = 0; i < entries.Count; i++) {
                var name = packedNames[i];
                at = Find(buffer, name.Length, at);
                if (at < 0) {
                    return false;
                }

                Array.Copy(name, 0, buffer, at, name.Length);
                at += name.Length;
            }

            var reader = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
            if (!reader.Deserialize<IgnoreEntryDataList>(buffer, mask, out var check)
                    || check?.m_ignoreDataList is not { } read || read.Count != entries.Count
                    || read.Where((entry, i) => entry.m_characterID.Full != entries[i].m_characterID.Full).Any()) {
                return false;
            }

            bytes = buffer;
            return true;
        }
        catch (Exception) {
            return false;
        }
        finally {
            for (var i = 0; i < entries.Count; i++) {
                entries[i].m_ignoreName = originals[i];
            }
        }
    }

    /// <summary>The first run of <paramref name="length"/> stand-in bytes at or after <paramref name="from"/>.</summary>
    private static int Find(byte[] buffer, int length, int from) {
        for (var i = from; i + length <= buffer.Length; i++) {
            var j = 0;
            while (j < length && buffer[i + j] == StandIn) j++;
            if (j == length) return i;
        }

        return -1;
    }
}
