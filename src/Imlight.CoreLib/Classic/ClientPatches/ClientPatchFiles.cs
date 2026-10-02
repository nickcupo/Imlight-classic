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
 * CLIENT PATCH FILES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the players' launcher (tools/mac/classic-update.py) downloads the
 * classic client patches over HTTP on the LAN: GET /classic/manifest.json,
 * /classic/news.txt and /classic/files/<sha256>, published by
 * tools/mac/classic-publish.py into [Classic] ClientPatchesPath. Aurorium serves
 * only the packages registered in its database, so these come from the game
 * server's small HTTP port (the Minion Helper port, 12090).
 *
 * USAGE EXAMPLE:
 * var file = ClientPatchFiles.Resolve(root, "/classic/files/ab12...");   // null: 404
 * var range = ClientPatchFiles.ParseRange("bytes=100-", length);          // resume
 *
 * NOTE:
 * Read-only, no directory listings, and nothing outside the configured folder
 * (no "..", no hidden files, no absolute paths, no symbolic links out).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.IO;
using Imlight.Common;

namespace Imlight.CoreLib.Classic.ClientPatches;

internal static class ClientPatchFiles {
    internal const string Prefix = "/classic/";

    /// <summary>The published folder ([Classic] ClientPatchesPath), or null when the server does not serve patches.</summary>
    internal static string? ConfiguredRoot {
        get {
            var value = ConfigurationManager.Settings["Classic.ClientPatchesPath"].AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>The file a /classic/... request names, or null (not found, or outside the folder).</summary>
    internal static string? Resolve(string? root, string urlPath) {
        if (root is null || !urlPath.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var relative = Uri.UnescapeDataString(urlPath[Prefix.Length..]);
        if (relative.Length == 0 || relative.Length > 200 || relative.Contains('\\') || relative.Contains('\0')) return null;
        foreach (var part in relative.Split('/')) {
            if (part.Length == 0 || part.StartsWith('.')) return null;   // "", ".", "..", hidden and partial files
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(top, StringComparison.Ordinal) || !File.Exists(full)) return null;
        var info = new FileInfo(full);
        if (info.LinkTarget is not null) return null;
        return full;
    }

    /// <summary>
    /// The byte range of a "Range: bytes=A-B" header (one range only), or null for the whole file.
    /// Unsatisfiable ranges come back as (-1, -1).
    /// </summary>
    internal static (long Start, long End)? ParseRange(string? header, long length) {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = header[6..].Trim();
        if (spec.Contains(',')) return null;  // multiple ranges: send the whole file
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        long start, end;
        if (first.Length == 0) {
            // Suffix: the last N bytes.
            if (!long.TryParse(last, out var suffix) || suffix <= 0) return (-1, -1);
            start = Math.Max(0, length - suffix);
            end = length - 1;
        } else {
            if (!long.TryParse(first, out start) || start < 0) return null;
            end = last.Length == 0 ? length - 1 : long.TryParse(last, out var e) ? Math.Min(e, length - 1) : -2;
            if (end == -2) return null;
        }

        if (start >= length || end < start) return (-1, -1);
        return (start, end);
    }

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".json" => "application/json",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
