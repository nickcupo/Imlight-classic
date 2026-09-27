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
 * WORLD DATA ARCHIVE LOADER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: reads the templates TemplateManifest places in a world's
 * WorldData WAD instead of Root.wad.
 *
 * USAGE EXAMPLE:
 * WorldDataArchiveLoader.GetFile<CoreTemplate>("|Krokotopia|WorldData|ObjectData/Krokotopia/KT_List.xml");
 *
 * NOTE:
 * A manifest path "|<Name>|WorldData|<file>" names <file> in <Name>-WorldData.wad,
 * which comes through ResourceManager like a zone's WAD (local cache, then
 * the patch server). Krokotopia's WAD is over 200 MB, so each WAD is read
 * once and only the files the manifest places in it are kept.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Imcodec.ObjectProperty;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// CLASSIC: loads template files from the WorldData WADs that TemplateManifest names.
/// </summary>
internal static class WorldDataArchiveLoader {

    private const char ManifestPathSeparator = '|';
    private const string WorldDataFolder = "WorldData";

    private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, byte[]>>> s_templateFilesByWad
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the manifest path names a file in a WorldData WAD.
    /// </summary>
    /// <param name="manifestPath">A TemplateLocation file name.</param>
    internal static bool IsWorldDataPath(string manifestPath)
        => TrySplitManifestPath(manifestPath, out _, out _);

    /// <summary>
    /// Deserializes a file that TemplateManifest places in a WorldData WAD.
    /// </summary>
    /// <typeparam name="T">The type of the file.</typeparam>
    /// <param name="manifestPath">The TemplateLocation file name, "|&lt;Name&gt;|WorldData|&lt;file&gt;".</param>
    /// <returns>The file, or null if the WAD or the file could not be read.</returns>
    internal static T GetFile<T>(string manifestPath) where T : PropertyClass {
        if (!TrySplitManifestPath(manifestPath, out var wadName, out var fileName)) {
            return null;
        }

        var templateFiles = s_templateFilesByWad
            .GetOrAdd(wadName, name => new Lazy<Dictionary<string, byte[]>>(() => ReadTemplateFiles(name)))
            .Value;
        if (!templateFiles.TryGetValue(fileName, out var fileData)) {
            return null;
        }

        var serializer = new BindSerializer();

        return serializer.Deserialize(fileData, out T file) ? file : null;
    }

    private static bool TrySplitManifestPath(string manifestPath, out string wadName, out string fileName) {
        wadName = null;
        fileName = null;

        var parts = manifestPath?.Split(ManifestPathSeparator);
        if (parts is not { Length: 4 } || parts[0].Length != 0 || parts[1].Length == 0
            || parts[2] != WorldDataFolder || parts[3].Length == 0) {
            return false;
        }

        wadName = $"{parts[1]}-{WorldDataFolder}";
        fileName = parts[3];

        return true;
    }

    private static Dictionary<string, byte[]> ReadTemplateFiles(string wadName) {
        var templateFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!ResourceManager.TryLoadArchive(wadName, out var wad)) {
            Logger.Error("Could not load {WadName} from the local cache or the patch server; its templates will not load.",
                Logger.Args(wadName));

            return templateFiles;
        }

        foreach (var location in CoreObjectFactory.TemplateManifest.m_serializedTemplates) {
            if (location is null
                || !TrySplitManifestPath(location.m_filename, out var locationWadName, out var fileName)
                || !string.Equals(locationWadName, wadName, StringComparison.OrdinalIgnoreCase)
                || templateFiles.ContainsKey(fileName)) {
                continue;
            }

            var fileData = wad.OpenFile(fileName);
            if (fileData.HasValue) {
                templateFiles[fileName] = fileData.Value.ToArray();
            }
        }

        Logger.Information("Loaded {Count} templates from {WadName}.", Logger.Args(templateFiles.Count, wadName));

        return templateFiles;
    }

}
