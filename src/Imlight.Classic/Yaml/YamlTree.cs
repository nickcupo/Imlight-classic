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
 * A small, neutral YAML tree (map, list, scalar, null), each node tagged
 * with its file and line, built from YamlDotNet's RepresentationModel.
 * Profile inheritance merges run on this tree.
 * 
 * USAGE EXAMPLE:
 * var root = YamlTree.Parse(path, displayPath, diagnostics);
 * 
 * NOTE:
 * Only plain null, ~ and empty scalars are null; a quoted "null" is a string.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Imlight.Classic.Yaml;

internal abstract class YNode(string file, int line) {

    public string File { get; } = file;
    public int Line { get; } = line;

    public abstract string Describe();

}

internal sealed record YEntry(string Key, int Line, YNode Value);

internal sealed class YMap(string file, int line, ImmutableArray<YEntry> entries) : YNode(file, line) {

    public ImmutableArray<YEntry> Entries { get; } = entries;

    public YEntry? Find(string key) {
        foreach (var entry in Entries) {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal)) {
                return entry;
            }
        }

        return null;
    }

    public override string Describe() => "a mapping";

}

internal sealed class YSeq(string file, int line, ImmutableArray<YNode> items) : YNode(file, line) {

    public ImmutableArray<YNode> Items { get; } = items;

    public override string Describe() => "a list";

}

internal sealed class YScalar(string file, int line, string value, bool isPlain) : YNode(file, line) {

    public string Value { get; } = value;
    public bool IsPlain { get; } = isPlain;

    public override string Describe() => $"'{Value}'";

}

internal sealed class YNull(string file, int line) : YNode(file, line) {

    public override string Describe() => "null";

}

internal static class YamlTree {

    /// <summary>
    /// Parses a YAML file into a <see cref="YNode"/> tree, or returns null after recording why it could not.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="displayPath">The path errors and nodes report.</param>
    /// <param name="diagnostics">Where problems are recorded.</param>
    /// <returns>The root node, or null.</returns>
    public static YNode? Parse(string path, string displayPath, YamlDiagnostics diagnostics) {
        string text;
        try {
            text = System.IO.File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            diagnostics.Add(new ClassicDataError(displayPath, "", null, $"cannot read the file: {ex.Message}"));

            return null;
        }

        var stream = new YamlStream();
        try {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex) {
            var line = (int) ex.Start.Line;
            if (ex.Message.StartsWith("Duplicate key", StringComparison.Ordinal)) {
                var keyPath = FindKeyPathAt(text, ex.Start) ?? "";
                diagnostics.Add(new ClassicDataError(displayPath, keyPath, line, "duplicate key"));
            }
            else {
                diagnostics.Add(new ClassicDataError(displayPath, "", line, $"not valid YAML: {ex.Message}"));
            }

            return null;
        }

        if (stream.Documents.Count == 0) {
            diagnostics.Add(new ClassicDataError(displayPath, "", null, "the file is empty"));

            return null;
        }

        if (stream.Documents.Count > 1) {
            diagnostics.Add(new ClassicDataError(displayPath, "", null,
                $"expected one YAML document, found {stream.Documents.Count}"));

            return null;
        }

        return Convert(stream.Documents[0].RootNode, displayPath, "", diagnostics);
    }

    /// <summary>
    /// Merges <paramref name="child"/> over <paramref name="parent"/>: maps merge recursively,
    /// anything else in the child replaces the parent's value.
    /// </summary>
    /// <param name="parent">The inherited map.</param>
    /// <param name="child">The overriding map.</param>
    /// <returns>A new merged map; parent key order first, then the child's new keys.</returns>
    public static YMap Merge(YMap parent, YMap child) {
        var entries = ImmutableArray.CreateBuilder<YEntry>();
        foreach (var parentEntry in parent.Entries) {
            var childEntry = child.Find(parentEntry.Key);
            if (childEntry is null) {
                entries.Add(parentEntry);
            }
            else if (parentEntry.Value is YMap parentMap && childEntry.Value is YMap childMap) {
                entries.Add(childEntry with { Value = Merge(parentMap, childMap) });
            }
            else {
                entries.Add(childEntry);
            }
        }

        foreach (var childEntry in child.Entries) {
            if (parent.Find(childEntry.Key) is null) {
                entries.Add(childEntry);
            }
        }

        return new YMap(child.File, child.Line, entries.ToImmutable());
    }

    public static string Join(string parentPath, string key)
        => parentPath.Length == 0 ? key : $"{parentPath}.{key}";

    public static string Index(string parentPath, int index)
        => $"{parentPath}[{index}]";

    private static YNode Convert(YamlNode node, string file, string path, YamlDiagnostics diagnostics) {
        var line = (int) node.Start.Line;
        switch (node) {
            case YamlMappingNode mapping: {
                var entries = ImmutableArray.CreateBuilder<YEntry>();
                foreach (var (keyNode, valueNode) in mapping.Children) {
                    if (keyNode is not YamlScalarNode { Value: { } key }) {
                        diagnostics.Add(new ClassicDataError(file, path, (int) keyNode.Start.Line,
                            "mapping keys must be plain strings"));
                        continue;
                    }

                    var keyPath = Join(path, key);
                    entries.Add(new YEntry(key, (int) keyNode.Start.Line,
                        Convert(valueNode, file, keyPath, diagnostics)));
                }

                return new YMap(file, line, entries.ToImmutable());
            }
            case YamlSequenceNode sequence: {
                var items = ImmutableArray.CreateBuilder<YNode>();
                for (var i = 0; i < sequence.Children.Count; i++) {
                    items.Add(Convert(sequence.Children[i], file, Index(path, i), diagnostics));
                }

                return new YSeq(file, line, items.ToImmutable());
            }
            case YamlScalarNode scalar: {
                var value = scalar.Value ?? "";
                var isPlain = scalar.Style is ScalarStyle.Plain or ScalarStyle.Any;
                if (isPlain && value is "" or "~" or "null" or "Null" or "NULL") {
                    return new YNull(file, line);
                }

                return new YScalar(file, line, value, isPlain);
            }
            default:
                diagnostics.Add(new ClassicDataError(file, path, line, $"unsupported YAML node {node.NodeType}"));

                return new YNull(file, line);
        }
    }

    private static string? FindKeyPathAt(string text, Mark at) {
        // RepresentationModel stops at a duplicate key without saying where in the tree it was.
        // Replay the parser events and report the path of the key that starts at the error mark.
        var frames = new Stack<Frame>();
        var parser = new Parser(new StringReader(text));
        try {
            while (parser.MoveNext()) {
                switch (parser.Current) {
                    case MappingStart:
                        frames.Push(new Frame(ChildPath(frames), isMap: true));
                        break;
                    case SequenceStart:
                        frames.Push(new Frame(ChildPath(frames), isMap: false));
                        break;
                    case MappingEnd or SequenceEnd:
                        frames.Pop();
                        CompleteValue(frames);
                        break;
                    case Scalar scalar when frames.TryPeek(out var top) && top.IsMap && top.ExpectingKey:
                        if (scalar.Start.Line == at.Line && scalar.Start.Column == at.Column) {
                            return Join(top.Path, scalar.Value);
                        }

                        top.CurrentKey = scalar.Value;
                        top.ExpectingKey = false;
                        break;
                    case Scalar or AnchorAlias:
                        CompleteValue(frames);
                        break;
                }
            }
        }
        catch (YamlException) {
            // The replay hits the same error the loader did; any path found before it stands.
        }

        return null;
    }

    private static string ChildPath(Stack<Frame> frames) {
        if (!frames.TryPeek(out var parent)) {
            return "";
        }

        return parent.IsMap ? Join(parent.Path, parent.CurrentKey) : Index(parent.Path, parent.Index);
    }

    private static void CompleteValue(Stack<Frame> frames) {
        if (!frames.TryPeek(out var parent)) {
            return;
        }

        if (parent.IsMap) {
            parent.ExpectingKey = true;
        }
        else {
            parent.Index++;
        }
    }

    private sealed class Frame(string path, bool isMap) {

        public string Path { get; } = path;
        public bool IsMap { get; } = isMap;
        public bool ExpectingKey { get; set; } = true;
        public string CurrentKey { get; set; } = "";
        public int Index { get; set; }

    }

}
