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
        catch (InvalidOperationException) {
            // YamlDotNet's scanner throws this, with no position, when a flow [ or { is left open and a
            // later line holds another key. Replay the parser to report where it stopped.
            var (keyPath, line, inFlow) = FindWhereParsingStopped(text);
            diagnostics.Add(new ClassicDataError(displayPath, keyPath, line, inFlow
                ? "not valid YAML: the parser failed inside this [ ] or { }; check that it is closed"
                : "not valid YAML: the parser failed after this line"));

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
        var tracker = new PathTracker();
        var parser = new Parser(new StringReader(text));
        try {
            while (parser.MoveNext()) {
                var parsingEvent = parser.Current!;
                if (tracker.Follow(parsingEvent) is { } keyPath
                    && parsingEvent.Start.Line == at.Line && parsingEvent.Start.Column == at.Column) {
                    return keyPath;
                }
            }
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException) {
            // The replay hits the same error the loader did, or a later one; any path found before it stands.
        }

        return null;
    }

    private static (string KeyPath, int? Line, bool InFlow) FindWhereParsingStopped(string text) {
        var tracker = new PathTracker();
        var parser = new Parser(new StringReader(text));
        int? lastLine = null;
        try {
            while (parser.MoveNext()) {
                _ = tracker.Follow(parser.Current!);
                lastLine = (int) parser.Current!.Start.Line;
            }
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException) {
            // Expected: the replay stops where the loader did.
        }

        // An open flow collection is the likely culprit, so point at the line of its [ or {.
        return tracker.Open is { IsFlow: true } open
            ? (open.Path, open.Line, true)
            : (tracker.Open?.Path ?? "", lastLine, false);
    }

    private sealed class PathTracker {

        private readonly Stack<Frame> _frames = new();

        /// <summary>
        /// The innermost collection that has started and not ended.
        /// </summary>
        public Frame? Open => _frames.TryPeek(out var top) ? top : null;

        /// <summary>
        /// Follows one parser event.
        /// </summary>
        /// <returns>The key path when the event is a mapping key, else null.</returns>
        public string? Follow(ParsingEvent parsingEvent) {
            switch (parsingEvent) {
                case MappingStart mapping:
                    _frames.Push(new Frame(ChildPath(), isMap: true, mapping.Style == MappingStyle.Flow, (int) mapping.Start.Line));
                    break;
                case SequenceStart sequence:
                    _frames.Push(new Frame(ChildPath(), isMap: false, sequence.Style == SequenceStyle.Flow, (int) sequence.Start.Line));
                    break;
                case MappingEnd or SequenceEnd:
                    _frames.Pop();
                    CompleteValue();
                    break;
                case Scalar scalar when _frames.TryPeek(out var top) && top.IsMap && top.ExpectingKey:
                    top.CurrentKey = scalar.Value;
                    top.ExpectingKey = false;

                    return Join(top.Path, scalar.Value);
                case Scalar or AnchorAlias:
                    CompleteValue();
                    break;
            }

            return null;
        }

        private string ChildPath() {
            if (!_frames.TryPeek(out var parent)) {
                return "";
            }

            return parent.IsMap ? Join(parent.Path, parent.CurrentKey) : Index(parent.Path, parent.Index);
        }

        private void CompleteValue() {
            if (!_frames.TryPeek(out var parent)) {
                return;
            }

            if (parent.IsMap) {
                parent.ExpectingKey = true;
            }
            else {
                parent.Index++;
            }
        }

    }

    private sealed class Frame(string path, bool isMap, bool isFlow, int line) {

        public string Path { get; } = path;
        public bool IsMap { get; } = isMap;
        public bool IsFlow { get; } = isFlow;
        public int Line { get; } = line;
        public bool ExpectingKey { get; set; } = true;
        public string CurrentKey { get; set; } = "";
        public int Index { get; set; }

    }

}
