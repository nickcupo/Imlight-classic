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
 * CLASSIC SECOND CHANCE RULES
 * ========================================================================
 *
 * PURPOSE:
 * The Second Chance chests of the October 2009 update (classic-data/rules/second-chance-2009.yaml): which client
 * chest belongs to which boss, the Crown price of each use and the uses a day.
 *
 * USAGE EXAMPLE:
 * var rules = SecondChanceRulesLoader.Load(path);
 * var chest = rules.ChestByTemplate(191252);   // Plague Oni's chest
 * var crowns = rules.CostOfUse(usesToday: 2);  // 150
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>One boss's Second Chance chest.</summary>
public sealed record SecondChanceChest(string Chest, ulong Template, string Zone, string Boss, ImmutableArray<ulong> BossTemplates);

/// <summary>
/// The Second Chance chests and their prices.
/// </summary>
public sealed class SecondChanceRules {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required int FirstCost { get; init; }
    public required int CostStep { get; init; }
    public required int DailyUses { get; init; }
    public required ImmutableArray<SecondChanceChest> Chests { get; init; }
    public required string SourceFile { get; init; }

    private FrozenDictionary<ulong, SecondChanceChest>? _byTemplate;
    private FrozenDictionary<string, SecondChanceChest>? _byName;

    /// <summary>The chest of a client template id, or null.</summary>
    public SecondChanceChest? ChestByTemplate(ulong template)
        => (_byTemplate ??= Chests.ToFrozenDictionary(c => c.Template)).GetValueOrDefault(template);

    /// <summary>The chest of a client object name (such as MS_MonsterChest_PlagueOni), or null.</summary>
    public SecondChanceChest? ChestByName(string? name)
        => name is null ? null : (_byName ??= Chests.ToFrozenDictionary(c => c.Chest, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(name);

    /// <summary>Crowns for the next use after <paramref name="usesToday"/> uses today.</summary>
    public int CostOfUse(int usesToday) => FirstCost + CostStep * Math.Max(0, usesToday);

}

/// <summary>
/// Loads and validates the Second Chance chest rules.
/// </summary>
public static class SecondChanceRulesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "license_tag", "notes", "cost", "daily_uses", "chests", "provenance");
    private static readonly FrozenSet<string> s_costKeys = FrozenSet.Create(StringComparer.Ordinal, "first", "step");
    private static readonly FrozenSet<string> s_chestKeys = FrozenSet.Create(StringComparer.Ordinal,
        "chest", "template", "zone", "boss", "boss_templates", "notes");
    private static readonly Regex s_id = new(@"^second-chance-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_chest = new(@"^[A-Z]{2}_(MonsterChest|BossChest)_[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the rules at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static SecondChanceRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the Second Chance rules do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is not YMap map) {
            if (root is not null) {
                diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "license_tag", "cost", "daily_uses", "chests", "provenance"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be second-chance-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        int? first = null, step = null;
        if (map.Find("cost") is { } c && diagnostics.ReadMap(c.Value, "cost") is { } cost) {
            diagnostics.CheckKeys(cost, "cost", s_costKeys, s_costKeys);
            first = cost.Find("first") is { } f ? diagnostics.ReadInt(f.Value, "cost.first", 0, 100_000) : null;
            step = cost.Find("step") is { } s ? diagnostics.ReadInt(s.Value, "cost.step", 0, 100_000) : null;
        }

        var daily = map.Find("daily_uses") is { } d ? diagnostics.ReadInt(d.Value, "daily_uses", 1, 1000) : null;
        var chests = ImmutableArray.CreateBuilder<SecondChanceChest>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templates = new HashSet<ulong>();
        if (map.Find("chests") is { } ch && diagnostics.ReadList(ch.Value, "chests") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("chests", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } row) {
                    continue;
                }

                diagnostics.CheckKeys(row, keyPath, s_chestKeys, ["chest", "template", "zone", "boss", "boss_templates"]);
                var name = row.Find("chest") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "chest")) : null;
                var template = row.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(keyPath, "template"), 1) : null;
                var zone = row.Find("zone") is { } z ? diagnostics.ReadString(z.Value, YamlTree.Join(keyPath, "zone")) : null;
                var boss = row.Find("boss") is { } b ? diagnostics.ReadString(b.Value, YamlTree.Join(keyPath, "boss")) : null;
                var bosses = ImmutableArray.CreateBuilder<ulong>();
                if (row.Find("boss_templates") is { } bt && diagnostics.ReadList(bt.Value, YamlTree.Join(keyPath, "boss_templates")) is { } bl) {
                    for (var j = 0; j < bl.Items.Length; j++) {
                        if (diagnostics.ReadInt(bl.Items[j], YamlTree.Index(YamlTree.Join(keyPath, "boss_templates"), j), 1) is { } bossTemplate) {
                            bosses.Add((ulong) bossTemplate);
                        }
                    }

                    if (bl.Items.IsEmpty) {
                        diagnostics.At(bl, YamlTree.Join(keyPath, "boss_templates"), "needs at least one boss template");
                    }
                }

                if (row.Find("notes") is { } nt) {
                    _ = diagnostics.ReadString(nt.Value, YamlTree.Join(keyPath, "notes"));
                }

                if (name is not null && (!s_chest.IsMatch(name) || !names.Add(name))) {
                    diagnostics.At(row, keyPath, $"chest '{name}' is not a <World>_MonsterChest_<Boss> name or is listed twice");
                }

                if (template is not null && !templates.Add((ulong) template)) {
                    diagnostics.At(row, keyPath, $"template {template} is listed twice");
                }

                if (name is not null && template is not null && zone is not null && boss is not null) {
                    chests.Add(new SecondChanceChest(name, (ulong) template, zone, boss, bosses.ToImmutable()));
                }
            }
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new SecondChanceRules {
            Id = id!,
            Profiles = profiles,
            FirstCost = first!.Value,
            CostStep = step!.Value,
            DailyUses = daily!.Value,
            Chests = chests.ToImmutable(),
            SourceFile = display,
        };
    }

}
