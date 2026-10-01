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
 * CLASSIC ZONE WADS
 * ========================================================================
 *
 * PURPOSE:
 * Older zone packages (from the 2014 client) that replace the patch
 * server's for zones remade after 2014. The server loads a zone's objects,
 * triggers and volumes from the replacement; the players' clients get the
 * same file (tools/mac/classic-zones.sh), so art and collision match.
 *
 * USAGE EXAMPLE:
 * if (ClassicZoneWads.TryLoad("MooShu-MS_Hub", out var wad)) return wad;   // ResourceManager
 *
 * NOTE:
 * [Classic] ZoneWadsPath names the directory of <wadName>.wad files. Empty
 * or missing: every zone comes from the patch server as before. A wad is
 * read once and kept in memory; a file that fails to parse is logged and the
 * patch server's copy is used.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using Imcodec.Wad;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Zone packages that replace the patch server's, from <c>[Classic] ZoneWadsPath</c>.
/// </summary>
internal static class ClassicZoneWads {

    private static readonly ConcurrentDictionary<string, Archive?> s_loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string? s_directory = Directory();

    /// <summary>
    /// True when a replacement for <paramref name="wadName"/> (for example <c>MooShu-MS_Hub</c>) exists and parses.
    /// </summary>
    internal static bool TryLoad(string wadName, out Archive wad) {
        wad = null!;
        if (s_directory is null) {
            return false;
        }

        var loaded = s_loaded.GetOrAdd(wadName, name => Load(s_directory, name));
        if (loaded is null) {
            return false;
        }

        wad = loaded;

        return true;
    }

    /// <summary>
    /// The replacement's path for <paramref name="wadName"/> in <paramref name="directory"/>.
    /// </summary>
    internal static string PathOf(string directory, string wadName)
        => System.IO.Path.Combine(directory, wadName.Replace('/', '-') + ".wad");

    private static Archive? Load(string directory, string wadName) {
        var path = PathOf(directory, wadName);
        if (!File.Exists(path)) {
            return null;
        }

        try {
            // The archive reads its entries from the stream on demand, so the stream stays open (in memory).
            var wad = ArchiveParser.Parse(new MemoryStream(File.ReadAllBytes(path)));
            Logger.Information("Classic zone package {WadName} loaded from {Path}.", Logger.Args(wadName, path));

            return wad;
        }
        catch (Exception ex) {
            Logger.Error("Classic zone package {Path} could not be read ({Error}); using the patch server's.",
                Logger.Args(path, ex.Message));

            return null;
        }
    }

    private static string? Directory() {
        var configured = ConfigurationManager.Settings["Classic.ZoneWadsPath"].AsString()?.Trim();
        if (string.IsNullOrEmpty(configured)) {
            return null;
        }

        if (!System.IO.Directory.Exists(configured)) {
            Logger.Warning("[Classic] ZoneWadsPath {Path} does not exist; zones come from the patch server.",
                Logger.Args(configured));

            return null;
        }

        return configured;
    }

}
