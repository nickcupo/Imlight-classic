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
 * CLASSIC RULE FILES
 * ========================================================================
 *
 * PURPOSE:
 * Reading shared by the badge and quest-card rule files.
 *
 * USAGE EXAMPLE:
 * var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Immutable;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

internal static class ClassicRuleFiles {

    /// <summary>
    /// The file's <c>profiles</c> list: valid, unrepeated profile ids.
    /// </summary>
    internal static ImmutableArray<string> ReadProfiles(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("profiles") is not { } entry || diagnostics.ReadList(entry.Value, "profiles") is not { } list) {
            return [];
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, "profiles", "needs at least one profile");
        }

        var profiles = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("profiles", i);
            if (diagnostics.ReadString(list.Items[i], keyPath) is not { } profile) {
                continue;
            }

            if (!ClassicSchema.IsValidId(profile) || profiles.Contains(profile)) {
                diagnostics.At(list.Items[i], keyPath, $"'{profile}' is not a valid, unrepeated profile id");
                continue;
            }

            profiles.Add(profile);
        }

        return profiles.ToImmutable();
    }

}
