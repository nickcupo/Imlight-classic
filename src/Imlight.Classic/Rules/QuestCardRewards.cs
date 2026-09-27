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
 * CLASSIC QUEST REWARDS
 * ========================================================================
 *
 * PURPOSE:
 * The treasure cards quests gave on completion (classic-data/quests/
 * cards-*.yaml), which neither quest templates nor drop tables can express:
 * drop-table items go through the item inventory and no quest result adds
 * to the treasure book.
 *
 * USAGE EXAMPLE:
 * foreach (var card in rewards.CardsFor("WC-ST01-C01-002", "Fire")) { ... add card.Template to the treasure book ... }
 *
 * NOTE:
 * A card with a school goes only to wizards of that school (Bad News...
 * gives each school its own card and Ice wizards none).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Spells;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// A treasure card a quest gives.
/// </summary>
/// <param name="Name">The card's name.</param>
/// <param name="Template">The client spell template id of the card.</param>
/// <param name="Count">How many.</param>
/// <param name="School">The only school that gets it, lower case; null for every school.</param>
public sealed record QuestCard(string Name, uint Template, int Count, string? School);

/// <summary>
/// A profile's treasure-card quest rewards.
/// </summary>
public sealed class QuestCardRewards {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required FrozenDictionary<string, ImmutableArray<QuestCard>> ByQuest { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>
    /// The cards completing <paramref name="quest"/> gives a wizard of <paramref name="school"/>.
    /// </summary>
    public IEnumerable<QuestCard> CardsFor(string quest, string? school)
        => ByQuest.GetValueOrDefault(quest, [])
            .Where(card => card.School is null || string.Equals(card.School, school, StringComparison.OrdinalIgnoreCase));

}

/// <summary>
/// Loads and validates treasure-card quest rewards.
/// </summary>
public static class QuestCardRewardsLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "quests");
    internal static readonly FrozenSet<string> s_questKeys = FrozenSet.Create(StringComparer.Ordinal,
        "quest", "title", "cards", "source", "confidence", "notes");
    private static readonly FrozenSet<string> s_cardKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "template", "count", "school");
    private static readonly string[] s_schools = ["balance", "death", "fire", "ice", "life", "myth", "storm"];
    private static readonly Regex s_id = new(@"^quest-cards-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the rewards at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static QuestCardRewards Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the quest card rewards do not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "quests"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = "quest-" + Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be quest-cards-<name> for cards-<name>.yaml ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        var byQuest = new Dictionary<string, ImmutableArray<QuestCard>>(StringComparer.Ordinal);
        if (map.Find("quests") is { } questsEntry && diagnostics.ReadList(questsEntry.Value, "quests") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("quests", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } entry) {
                    continue;
                }

                diagnostics.CheckKeys(entry, keyPath, s_questKeys, ["quest", "title", "cards", "source", "confidence"]);
                var quest = entry.Find("quest") is { } q ? diagnostics.ReadString(q.Value, YamlTree.Join(keyPath, "quest")) : null;
                var cards = entry.Find("cards") is { } c ? ReadCards(c.Value, YamlTree.Join(keyPath, "cards"), diagnostics) : [];
                if (quest is null) {
                    continue;
                }

                if (!byQuest.TryAdd(quest, cards)) {
                    diagnostics.At(entry, keyPath, $"quest '{quest}' is listed twice");
                }
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new QuestCardRewards {
            Id = id!,
            Profiles = profiles,
            ByQuest = byQuest.ToFrozenDictionary(StringComparer.Ordinal),
            SourceFile = display,
        };
    }

    private static ImmutableArray<QuestCard> ReadCards(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, keyPath, "needs at least one card");
        }

        var cards = ImmutableArray.CreateBuilder<QuestCard>();
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(keyPath, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } card) {
                continue;
            }

            diagnostics.CheckKeys(card, path, s_cardKeys, ["name", "template"]);
            var name = card.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(path, "name")) : null;
            var template = card.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(path, "template"), 1) : null;
            var count = card.Find("count") is { } c ? diagnostics.ReadInt(c.Value, YamlTree.Join(path, "count"), 1, 10) : 1;
            var school = card.Find("school") is { } s ? diagnostics.ReadEnum(s.Value, YamlTree.Join(path, "school"), s_schools) : null;
            if (name is null || template is null || count is null) {
                continue;
            }

            cards.Add(new QuestCard(name, (uint) template.Value, count.Value, school));
        }

        return cards.ToImmutable();
    }

}
