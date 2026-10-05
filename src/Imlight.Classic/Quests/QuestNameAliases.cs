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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * The made-up names quests were first served under, and the KingsIsle
 * names they carry now (classic-data/quests/quest-name-aliases.yaml).
 * The client's quest arrow finds a quest by StringHash(name) in its
 * quest-helper table, so a made-up name had no arrow. Old names keep
 * resolving: a quest name, a "QT-<quest>" entry or a "<quest>_<entry>"
 * quest-registry key that uses one means the quest's new name, and
 * QuestNameMigration renames them in saved characters.
 *
 * USAGE EXAMPLE:
 * QuestNameAliases.Current = QuestNameAliasesLoader.Load(path);
 * var name = QuestNameAliases.Current.Canonical("WC-CLASSIC-SIDE-059");   // "WC-ST07-C01-001"
 * var key = QuestNameAliases.Current.CanonicalEntry("QT-WC-CLASSIC-SIDE-059"); // "QT-WC-ST07-C01-001"
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Quests;

/// <summary>
/// One renamed quest.
/// </summary>
/// <param name="Old">The name the quest was served under.</param>
/// <param name="Name">The KingsIsle name it carries now.</param>
/// <param name="Evidence">How the name was found (dialogcache, targets, chain, documented).</param>
public sealed record QuestNameAlias(string Old, string Name, string Evidence);

/// <summary>
/// One registry key a saved character must rename.
/// </summary>
/// <param name="From">The key as saved.</param>
/// <param name="To">The key with the quest's new name.</param>
public sealed record RegistryRename(string From, string To);

/// <summary>
/// Old quest names and the names they resolve to.
/// </summary>
public sealed class QuestNameAliases {

    /// <summary>
    /// No aliases.
    /// </summary>
    public static QuestNameAliases Empty { get; } = new([], "");

    private static volatile QuestNameAliases s_current = Empty;

    /// <summary>
    /// The aliases the server runs with (set at start-up; empty before that and in stock rules).
    /// </summary>
    public static QuestNameAliases Current {
        get => s_current;
        set => s_current = value ?? Empty;
    }

    private readonly FrozenDictionary<string, string> _byOld;

    /// <summary>
    /// Creates the set from validated entries.
    /// </summary>
    public QuestNameAliases(ImmutableArray<QuestNameAlias> aliases, string sourceFile) {
        Aliases = aliases;
        SourceFile = sourceFile;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in aliases) {
            map[alias.Old] = alias.Name;
        }

        _byOld = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The entries, in file order.
    /// </summary>
    public ImmutableArray<QuestNameAlias> Aliases { get; }

    /// <summary>
    /// The file they came from.
    /// </summary>
    public string SourceFile { get; }

    /// <summary>
    /// The number of old names.
    /// </summary>
    public int Count => _byOld.Count;

    /// <summary>
    /// The new name an old name stands for, or null when <paramref name="name"/> is not an old name.
    /// </summary>
    public string? NewNameOf(string? name)
        => name is not null && _byOld.TryGetValue(name, out var renamed) ? renamed : null;

    /// <summary>
    /// The quest's current name: the new name for an old one, else <paramref name="name"/> itself.
    /// </summary>
    public string Canonical(string name)
        => NewNameOf(name) ?? name;

    /// <summary>
    /// A registry key with an old quest name in it rewritten: "QT-old" -> "QT-new", and quest-registry keys
    /// "old_entry" -> "new_entry" ("old_Complete" among them), whose entry may itself be "QT-old"
    /// ("old_QT-old" -> "new_QT-new", "other_QT-old" -> "other_QT-new"). Any other key is returned unchanged.
    /// </summary>
    public string CanonicalEntry(string key) {
        if (_byOld.Count == 0 || string.IsNullOrEmpty(key)) {
            return key;
        }

        if (TakenEntry(key) is { } taken) {
            return taken;
        }

        // "<quest>_<entry>": a quest name may itself hold '_' (HO-Halloween_2009-C01-001), so try every split.
        for (var i = key.IndexOf('_'); i > 0; i = key.IndexOf('_', i + 1)) {
            var head = NewNameOf(key[..i]);
            var tail = TakenEntry(key[(i + 1)..]);
            if (head is not null || tail is not null) {
                return (head ?? key[..i]) + "_" + (tail ?? key[(i + 1)..]);
            }
        }

        return key;
    }

    // "QT-old" -> "QT-new"; null for any other key.
    private string? TakenEntry(string key)
        => key.StartsWith(QuestTakenEntry.Prefix, StringComparison.Ordinal)
           && NewNameOf(key[QuestTakenEntry.Prefix.Length..]) is { } renamed
            ? QuestTakenEntry.Prefix + renamed
            : null;

    /// <summary>
    /// The registry keys a saved character must rename (old quest names in QT- and quest-registry keys).
    /// </summary>
    public List<RegistryRename> RegistryRenames(IEnumerable<string> keys) {
        var renames = new List<RegistryRename>();
        foreach (var key in keys) {
            var canonical = CanonicalEntry(key);
            if (!string.Equals(canonical, key, StringComparison.Ordinal)) {
                renames.Add(new RegistryRename(key, canonical));
            }
        }

        return renames;
    }

    /// <summary>
    /// Applies <see cref="RegistryRenames"/> to a registry. A key already present under the new name keeps the
    /// larger of the two values (both mean "done"/"taken"). Returns the renames made; a second call makes none.
    /// </summary>
    public List<RegistryRename> MigrateRegistry(IDictionary<string, ulong> registry) {
        var renames = RegistryRenames([.. registry.Keys]);
        foreach (var rename in renames) {
            var value = registry[rename.From];
            registry.Remove(rename.From);
            registry[rename.To] = registry.TryGetValue(rename.To, out var existing) ? Math.Max(existing, value) : value;
        }

        return renames;
    }

}

/// <summary>
/// Loads and validates classic-data/quests/quest-name-aliases.yaml.
/// </summary>
public static class QuestNameAliasesLoader {

    /// <summary>
    /// The file's path under the classic-data root.
    /// </summary>
    public const string RelativePath = "quests/quest-name-aliases.yaml";

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kind", "version", "id", "license_tag", "notes", "aliases");
    internal static readonly FrozenSet<string> s_aliasKeys = FrozenSet.Create(StringComparer.Ordinal,
        "old", "name", "evidence", "arrow_goals", "notes");
    internal static readonly FrozenSet<string> s_evidence = FrozenSet.Create(StringComparer.Ordinal,
        "dialogcache", "targets", "chain", "documented");

    /// <summary>
    /// Loads the aliases under <paramref name="classicDataRoot"/>; empty when the file does not exist.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is invalid; every error is reported.</exception>
    public static QuestNameAliases LoadFromRoot(string classicDataRoot) {
        var path = Path.Combine(classicDataRoot, RelativePath);

        return File.Exists(path) ? Load(path) : QuestNameAliases.Empty;
    }

    /// <summary>
    /// Loads the aliases at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static QuestNameAliases Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the quest name aliases file does not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is null) {
            throw diagnostics.ToException();
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["kind", "version", "id", "license_tag", "aliases"]);
        if (map.Find("kind") is { } kindEntry && diagnostics.ReadString(kindEntry.Value, "kind") is { } kind
                && kind != "quest-name-aliases") {
            diagnostics.At(kindEntry.Value, "kind", $"kind '{kind}' must be quest-name-aliases");
        }

        var aliases = ImmutableArray.CreateBuilder<QuestNameAlias>();
        var olds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (map.Find("aliases") is { } aliasesEntry && diagnostics.ReadList(aliasesEntry.Value, "aliases") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("aliases", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } entry) {
                    continue;
                }

                diagnostics.CheckKeys(entry, keyPath, s_aliasKeys, ["old", "name", "evidence"]);
                var old = entry.Find("old") is { } o ? diagnostics.ReadString(o.Value, YamlTree.Join(keyPath, "old")) : null;
                var name = entry.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "name")) : null;
                var evidence = entry.Find("evidence") is { } e ? diagnostics.ReadString(e.Value, YamlTree.Join(keyPath, "evidence")) : null;
                if (evidence is not null && !s_evidence.Contains(evidence)) {
                    diagnostics.At(entry.Find("evidence")!.Value, YamlTree.Join(keyPath, "evidence"),
                        $"evidence '{evidence}' must be one of {string.Join(", ", s_evidence)}");
                }

                if (old is null || name is null || evidence is null) {
                    continue;
                }

                if (string.Equals(old, name, StringComparison.OrdinalIgnoreCase)) {
                    diagnostics.At(entry, keyPath, $"old and name are both '{old}'");
                    continue;
                }

                if (!olds.Add(old)) {
                    diagnostics.At(entry, keyPath, $"old name '{old}' is listed twice");
                }

                if (!names.Add(name)) {
                    diagnostics.At(entry, keyPath, $"name '{name}' is the new name of two quests");
                }

                aliases.Add(new QuestNameAlias(old, name, evidence));
            }
        }

        // An alias must point at a final name, never at another old name (no chains to follow).
        foreach (var alias in aliases) {
            if (olds.Contains(alias.Name)) {
                diagnostics.At(map, "aliases", $"'{alias.Old}' points at '{alias.Name}', which is itself an old name");
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new QuestNameAliases(aliases.ToImmutable(), display);
    }

}
