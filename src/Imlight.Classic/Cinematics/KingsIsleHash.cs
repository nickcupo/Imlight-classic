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
 * CLASSIC CINEMATICS
 * ========================================================================
 *
 * PURPOSE:
 * KingsIsle's ObjectProperty hashes: a class's type hash and a property's
 * hash, as they appear in versionable (BINd) data.
 *
 * USAGE EXAMPLE:
 * const uint CinematicName = 0x9BA8BF49; // KingsIsleHash.Property("m_cinematicName", "std::string")
 *
 * NOTE:
 * A property hash is djb2(name) & 0x7FFFFFFF plus the type hash of its C++
 * type name, both 32-bit and wrapping. Server-only result classes the server
 * type registry lists without property hashes (ResCinematic,
 * ResStartStagedCinematic) are read with these; tests pin them against
 * hashes the registry does list.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Text;

namespace Imlight.Classic.Cinematics;

/// <summary>
/// KingsIsle's ObjectProperty type and property hashes.
/// </summary>
public static class KingsIsleHash {

    /// <summary>
    /// The type hash of a C++ type name such as <c>class ResCinematic</c> or <c>std::string</c>.
    /// </summary>
    /// <param name="typeName">The type name.</param>
    /// <returns>The hash.</returns>
    public static uint Type(string typeName) {
        var state = 0;
        var bytes = Encoding.ASCII.GetBytes(typeName);
        for (var i = 0; i < bytes.Length; i++) {
            var value = bytes[i] - 32;
            var shift = i * 5 % 32;
            state ^= value << shift;
            if (shift > 24) {
                state ^= (int) ((uint) value >> (32 - shift));
            }
        }

        return (uint) (state < 0 ? -(long) state : state);
    }

    /// <summary>
    /// The hash of a property: djb2 of its name plus the type hash of its type.
    /// </summary>
    /// <param name="name">The property name such as <c>m_cinematicName</c>.</param>
    /// <param name="typeName">The C++ type name such as <c>std::string</c>.</param>
    /// <returns>The hash.</returns>
    public static uint Property(string name, string typeName) {
        uint djb2 = 5381;
        foreach (var b in Encoding.ASCII.GetBytes(name)) {
            djb2 = unchecked(djb2 * 33 + b);
        }

        return unchecked((djb2 & 0x7FFFFFFF) + Type(typeName));
    }

}
