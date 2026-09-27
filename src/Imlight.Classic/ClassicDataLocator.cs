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
 * CLASSIC DATA LOADING
 * ========================================================================
 * 
 * PURPOSE:
 * Resolves where classic-data lives from the [Classic] ini settings and the
 * server binary's directory. The server and the tests share this code.
 * 
 * USAGE EXAMPLE:
 * var profiles = ClassicDataLocator.ResolveProfilesPath(configured, AppContext.BaseDirectory);
 * var zones = ClassicDataLocator.ResolveZoneWorldsPath(configuredZones, profiles, AppContext.BaseDirectory);
 * 
 * NOTE:
 * Relative settings resolve against the binary's directory, never the
 * working directory, so a service's cwd cannot change which data loads.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Imlight.Classic;

/// <summary>
/// Path resolution for classic-data.
/// </summary>
public static class ClassicDataLocator {

    /// <summary>
    /// The name of the data directory in the monorepo and in deployments.
    /// </summary>
    public const string DataDirectoryName = "classic-data";

    /// <summary>
    /// Resolves the profiles directory.
    /// </summary>
    /// <param name="configured">The <c>Classic.ProfilesPath</c> setting; empty searches upward.</param>
    /// <param name="baseDirectory">The server binary's directory.</param>
    /// <returns>The absolute profiles directory.</returns>
    /// <exception cref="DirectoryNotFoundException">The search found no classic-data/profiles; the message lists every probed path.</exception>
    public static string ResolveProfilesPath(string? configured, string baseDirectory) {
        if (!string.IsNullOrWhiteSpace(configured)) {
            return ResolveConfigured(configured, baseDirectory);
        }

        var probed = ProbeProfilesPaths(baseDirectory);
        foreach (var candidate in probed) {
            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"No {DataDirectoryName}/profiles directory was found above {baseDirectory}. Set Classic.ProfilesPath. Probed:"
            + Environment.NewLine + string.Join(Environment.NewLine, probed.Select(path => "  " + path)));
    }

    /// <summary>
    /// Resolves the zone map entry file.
    /// </summary>
    /// <param name="configured">The <c>Classic.ZoneWorldsPath</c> setting; empty uses zones/worlds.yaml next to the profiles.</param>
    /// <param name="profilesPath">The resolved profiles directory.</param>
    /// <param name="baseDirectory">The server binary's directory.</param>
    /// <returns>The absolute path of worlds.yaml.</returns>
    public static string ResolveZoneWorldsPath(string? configured, string profilesPath, string baseDirectory) {
        if (!string.IsNullOrWhiteSpace(configured)) {
            return ResolveConfigured(configured, baseDirectory);
        }

        return Path.GetFullPath(Path.Combine(profilesPath, "..", "zones", "worlds.yaml"));
    }

    /// <summary>
    /// The directories an empty <c>ProfilesPath</c> probes, nearest first.
    /// </summary>
    /// <param name="baseDirectory">The directory to start from.</param>
    /// <returns><c>&lt;dir&gt;/classic-data/profiles</c> for the directory and each ancestor.</returns>
    public static IReadOnlyList<string> ProbeProfilesPaths(string baseDirectory) {
        var probed = new List<string>();
        for (var dir = new DirectoryInfo(Path.GetFullPath(baseDirectory)); dir is not null; dir = dir.Parent) {
            probed.Add(Path.Combine(dir.FullName, DataDirectoryName, "profiles"));
        }

        return probed;
    }

    /// <summary>
    /// Finds the nearest classic-data directory (one with a profiles folder) at or above a directory.
    /// </summary>
    /// <param name="startDirectory">The directory to start from.</param>
    /// <returns>The classic-data directory, or null when there is none.</returns>
    public static string? FindClassicDataRoot(string startDirectory) {
        foreach (var profiles in ProbeProfilesPaths(startDirectory)) {
            if (Directory.Exists(profiles)) {
                return Path.GetDirectoryName(profiles);
            }
        }

        return null;
    }

    /// <summary>
    /// Shortens a path for error messages: relative to the parent of the nearest enclosing
    /// classic-data directory, with forward slashes, else the full path.
    /// </summary>
    /// <param name="path">A file or directory path.</param>
    /// <returns>For example <c>classic-data/profiles/late-2009.yaml</c>.</returns>
    public static string DisplayPath(string path) {
        var full = Path.GetFullPath(path);
        for (var dir = new DirectoryInfo(full).Parent; dir is not null; dir = dir.Parent) {
            if (string.Equals(dir.Name, DataDirectoryName, StringComparison.Ordinal) && dir.Parent is not null) {
                return Path.GetRelativePath(dir.Parent.FullName, full).Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        return full;
    }

    private static string ResolveConfigured(string configured, string baseDirectory) {
        var trimmed = configured.Trim();

        return Path.IsPathFullyQualified(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(baseDirectory, trimmed));
    }

}
