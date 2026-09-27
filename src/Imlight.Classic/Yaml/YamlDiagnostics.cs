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
 * Collects validation errors while a classic-data file is checked, and
 * provides the typed reads (string, integer, date, boolean, enum) the
 * profile and zone loaders share.
 * 
 * USAGE EXAMPLE:
 * var cap = diagnostics.ReadPositiveInt(entry.Value, "level_cap");
 * if (diagnostics.HasErrors) { throw diagnostics.ToException(); }
 * 
 * NOTE:
 * Each read records an error and returns null when the value has the wrong
 * type, so one pass reports every problem in a file.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Imlight.Classic.Yaml;

internal sealed class YamlDiagnostics {

    private readonly List<ClassicDataError> _errors = [];

    public IReadOnlyList<ClassicDataError> Errors => _errors;
    public bool HasErrors => _errors.Count > 0;

    public void Add(ClassicDataError error)
        => _errors.Add(error);

    public void At(YNode node, string keyPath, string message)
        => _errors.Add(new ClassicDataError(node.File, keyPath, node.Line, message));

    public void AtKey(YNode map, YEntry entry, string keyPath, string message)
        => _errors.Add(new ClassicDataError(map.File, keyPath, entry.Line, message));

    public ClassicDataException ToException()
        => new(_errors);

    public YMap? ReadMap(YNode node, string keyPath) {
        if (node is YMap map) {
            return map;
        }

        At(node, keyPath, $"expected a mapping, got {node.Describe()}");

        return null;
    }

    public YSeq? ReadList(YNode node, string keyPath) {
        if (node is YSeq list) {
            return list;
        }

        At(node, keyPath, $"expected a list, got {node.Describe()}");

        return null;
    }

    public void CheckKeys(YMap map, string keyPath, IReadOnlySet<string> allowed, IEnumerable<string> required) {
        foreach (var entry in map.Entries) {
            if (!allowed.Contains(entry.Key)) {
                AtKey(map, entry, YamlTree.Join(keyPath, entry.Key), $"unknown key '{entry.Key}'");
            }
        }

        foreach (var key in required) {
            if (map.Find(key) is null) {
                At(map, YamlTree.Join(keyPath, key), $"required key '{key}' is missing");
            }
        }
    }

    public string? ReadString(YNode node, string keyPath) {
        if (node is YScalar scalar) {
            return scalar.Value;
        }

        At(node, keyPath, $"expected a string, got {node.Describe()}");

        return null;
    }

    public string? ReadEnum(YNode node, string keyPath, IReadOnlyCollection<string> values) {
        var value = ReadString(node, keyPath);
        if (value is null) {
            return null;
        }

        if (!values.Contains(value, StringComparer.Ordinal)) {
            At(node, keyPath, $"expected one of {string.Join(", ", values)}; got '{value}'");

            return null;
        }

        return value;
    }

    public bool? ReadBool(YNode node, string keyPath) {
        if (node is YScalar { IsPlain: true } scalar) {
            if (string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            if (string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        At(node, keyPath, $"expected true or false, got {node.Describe()}");

        return null;
    }

    public int? ReadPositiveInt(YNode node, string keyPath, bool allowNull) {
        if (node is YNull && allowNull) {
            return null;
        }

        if (node is YScalar { IsPlain: true } scalar
            && int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= 1) {
            return value;
        }

        var expected = allowNull ? "an integer >= 1 or null" : "an integer >= 1";
        At(node, keyPath, $"expected {expected}, got {node.Describe()}");

        return null;
    }

    public DateOnly? ReadDate(YNode node, string keyPath, bool allowNull) {
        if (node is YNull && allowNull) {
            return null;
        }

        if (node is YScalar scalar
            && DateOnly.TryParseExact(scalar.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) {
            return date;
        }

        var expected = allowNull ? "a yyyy-MM-dd date or null" : "a yyyy-MM-dd date";
        At(node, keyPath, $"expected {expected}, got {node.Describe()}");

        return null;
    }

    public void ReadStringList(YNode node, string keyPath) {
        if (ReadList(node, keyPath) is not { } list) {
            return;
        }

        for (var i = 0; i < list.Items.Length; i++) {
            _ = ReadString(list.Items[i], YamlTree.Index(keyPath, i));
        }
    }

}
