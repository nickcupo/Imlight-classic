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
 * CHAT GUARD
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what a player's chat line may carry to other clients. The server used to pass the sender's bytes on
 * untouched. Now: control characters and the client's text-markup brackets '<' '>' are removed, the line is cut at
 * MaxLength, and an empty line is dropped. The radial chat's leading byte (the client's own prefix, kept by the
 * server before) is kept as it is; everything the r806919 client itself types stays as typed otherwise.
 *
 * Rate limit (per session, all typed and quick chat together): a burst of RateBurst lines, then RatePerSecond.
 *
 * USAGE EXAMPLE:
 * var clean = ChatGuard.SanitizeRadial(raw); if (clean is null) return;
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Text;

namespace Imlight.Classic.Security;

public static class ChatGuard {

    /// <summary>Longest line passed on (characters or bytes). Generous: the client's own chat box is shorter.</summary>
    public const int MaxLength = 256;

    public const int RateBurst = 8;
    public const double RatePerSecond = 1;

    private static bool Keeps(int c) => c >= 0x20 && c != 0x7F && c != '<' && c != '>';

    /// <summary>
    /// A radial (typed) chat STR: the first byte is the client's prefix and is kept (unless it is the command dot);
    /// the rest is filtered and cut. Null when nothing is left to say.
    /// </summary>
    public static byte[]? SanitizeRadial(byte[]? raw) {
        if (raw is null || raw.Length == 0) {
            return null;
        }

        var prefix = raw[0] == (byte) '.' ? 0 : 1;
        var output = new byte[Math.Min(raw.Length, prefix + MaxLength)];
        var length = 0;
        for (var i = 0; i < prefix && i < raw.Length; i++) {
            output[length++] = raw[i];
        }

        var text = 0;
        for (var i = prefix; i < raw.Length && length < output.Length; i++) {
            if (!Keeps(raw[i])) continue;
            output[length++] = raw[i];
            if (raw[i] != (byte) ' ') text++;
        }

        return text == 0 ? null : output.AsSpan(0, length).ToArray();
    }

    /// <summary>A whisper (WSTR or STR as text): filtered and cut. Null when nothing is left to say.</summary>
    public static string? SanitizeText(string? text) {
        if (string.IsNullOrEmpty(text)) {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxLength));
        foreach (var c in text) {
            if (builder.Length >= MaxLength) break;
            if (Keeps(c)) builder.Append(c);
        }

        return string.IsNullOrWhiteSpace(builder.ToString()) ? null : builder.ToString();
    }

    /// <summary>A quick-chat "extended" STR is a client phrase reference; only its length is checked.</summary>
    public static bool AcceptsQuickChatExt(byte[]? raw) => raw is { Length: > 0 and <= MaxLength * 2 };

}
