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
 * CLASSIC LATER OBJECTS
 * ========================================================================
 *
 * PURPOSE:
 * Zone objects that the r806919 zone data places but that belong to later
 * versions of the game (fishing, Spellements, Archmastery, event NPCs, ...).
 * The classic server does not spawn them. The list is data: the profile rule
 * rules.later_objects names a file such as zones/later-objects.yaml.
 *
 * USAGE EXAMPLE:
 * var later = LaterObjectsLoader.Load(path);
 * if (later.Hides(objectTemplateId, "WizardCity/WC_Hub")) continue;
 *
 * NOTE:
 * Each entry names a template id, the zone it hides it in ('*' for every zone,
 * or a zone pattern such as WizardCity/WC_Hub), a reason and the evidence.
 * A missing file means nothing is hidden.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;
using Imlight.Classic.Zones;

namespace Imlight.Classic.Rules;

/// <summary>
/// One later object the server does not spawn.
/// </summary>
/// <param name="Template">The client template id.</param>
/// <param name="Name">The object's display name, or its template name when it has none.</param>
/// <param name="Zone">Where it is hidden: '*' or a zone pattern.</param>
/// <param name="Reason">Why it is a later object.</param>
/// <param name="Evidence">What shows it.</param>
public sealed record LaterObject(ulong Template, string Name, ZonePattern Zone, string Reason, string Evidence);

/// <summary>
/// The objects the classic server hides, by template and zone.
/// </summary>
public sealed class LaterObjects {

    /// <summary>
    /// A set that hides nothing.
    /// </summary>
    public static LaterObjects Empty { get; } = new() {
        Id = "",
        Profiles = [],
        Objects = [],
        SourceFile = "",
    };

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required ImmutableArray<LaterObject> Objects { get; init; }
    public required string SourceFile { get; init; }

    internal FrozenSet<ulong> _everywhere { get; init; } = FrozenSet<ulong>.Empty;
    internal FrozenDictionary<ulong, ImmutableArray<ZonePattern>> _byTemplate { get; init; } = FrozenDictionary<ulong, ImmutableArray<ZonePattern>>.Empty;

    /// <summary>
    /// The number of distinct templates the file hides.
    /// </summary>
    public int TemplateCount => _byTemplate.Count;

    /// <summary>
    /// True when <paramref name="templateId"/> is hidden in <paramref name="zone"/>.
    /// </summary>
    /// <param name="templateId">The object's template id.</param>
    /// <param name="zone">The zone name such as <c>WizardCity/WC_Hub</c>; null matches only entries for every zone.</param>
    public bool Hides(ulong templateId, string? zone) {
        if (_everywhere.Contains(templateId)) {
            return true;
        }

        if (!_byTemplate.TryGetValue(templateId, out var patterns)) {
            return false;
        }

        var segments = ZonePattern.SplitZone(zone);

        return patterns.Any(pattern => pattern.Matches(segments));
    }

}

/// <summary>
/// Loads and validates the later-objects list.
/// </summary>
public static class LaterObjectsLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kind", "version", "id", "title", "profiles", "license_tag", "notes", "objects");
    internal static readonly FrozenSet<string> s_objectKeys = FrozenSet.Create(StringComparer.Ordinal,
        "template", "name", "template_name", "zone", "category", "reason", "evidence", "confidence", "notes");
    internal static readonly FrozenSet<string> s_categories = FrozenSet.Create(StringComparer.Ordinal,
        "fishing", "spellements", "archmastery", "castle-magic", "hall-of-fame", "event", "later-quest", "later-system", "placeholder");
    private static readonly Regex s_id = new(@"^later-objects(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the list at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static LaterObjects Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the later-objects list does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["kind", "version", "id", "profiles", "license_tag", "objects"]);
        if (map.Find("kind") is { } kindEntry && diagnostics.ReadString(kindEntry.Value, "kind") is { } kind && kind != "later-objects") {
            diagnostics.At(kindEntry.Value, "kind", $"kind '{kind}' must be later-objects");
        }

        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be later-objects[-name] and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var objects = ImmutableArray.CreateBuilder<LaterObject>();
        var seen = new HashSet<(ulong, string)>();
        if (map.Find("objects") is { } objectsEntry && diagnostics.ReadList(objectsEntry.Value, "objects") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("objects", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } entry) {
                    continue;
                }

                diagnostics.CheckKeys(entry, keyPath, s_objectKeys, ["template", "name", "zone", "reason", "evidence"]);
                var template = entry.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(keyPath, "template"), 1, int.MaxValue) : null;
                var name = entry.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "name")) : null;
                var zoneText = entry.Find("zone") is { } z ? diagnostics.ReadString(z.Value, YamlTree.Join(keyPath, "zone")) : null;
                var reason = entry.Find("reason") is { } r ? diagnostics.ReadString(r.Value, YamlTree.Join(keyPath, "reason")) : null;
                var evidence = entry.Find("evidence") is { } e ? diagnostics.ReadString(e.Value, YamlTree.Join(keyPath, "evidence")) : null;
                if (entry.Find("category") is { } c) {
                    _ = diagnostics.ReadEnum(c.Value, YamlTree.Join(keyPath, "category"), s_categories);
                }

                ZonePattern? zone = null;
                if (zoneText is not null) {
                    if (ZonePattern.IsValid(zoneText)) {
                        zone = ZonePattern.Parse(zoneText);
                    }
                    else {
                        diagnostics.At(entry.Find("zone")!.Value, YamlTree.Join(keyPath, "zone"), $"'{zoneText}' is not a valid zone pattern");
                    }
                }

                if (template is null || name is null || zone is null || reason is null || evidence is null) {
                    continue;
                }

                if (!seen.Add(((ulong) template.Value, zoneText!.ToLowerInvariant()))) {
                    diagnostics.At(entry.Find("template")!.Value, YamlTree.Join(keyPath, "template"), $"template {template} is listed twice for zone '{zoneText}'");
                    continue;
                }

                objects.Add(new LaterObject((ulong) template.Value, name, zone, reason, evidence));
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        var built = objects.ToImmutable();

        return new LaterObjects {
            Id = id!,
            Profiles = profiles,
            Objects = built,
            SourceFile = display,
            _everywhere = built.Where(o => o.Zone.Text == "*").Select(o => o.Template).ToFrozenSet(),
            _byTemplate = built.GroupBy(o => o.Template)
                .ToFrozenDictionary(g => g.Key, g => g.Select(o => o.Zone).ToImmutableArray()),
        };
    }

}
