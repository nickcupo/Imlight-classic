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
 * CLASSIC CREATURE DECKS
 * ========================================================================
 *
 * PURPOSE:
 * The spells each creature (a mob, a boss, a summoned minion or a Monstrology
 * creature) cast in 2009, by creature template id, taken from the creature's
 * Wizard101 wiki page. The profile rule rules.creature_decks names the file
 * (classic-data/creatures/creature-decks-2009.yaml); a profile without the rule
 * has no such decks and creatures keep the template's own spell list.
 *
 * USAGE EXAMPLE:
 * var decks = CreatureDecksLoader.Load(path);
 * if (decks.TryGet(creatureTemplateId, out var deck)) { foreach (var spell in deck.Spells) ... }
 *
 * NOTE:
 * Spells are client spell template names (Spells/<name>.xml without the folder
 * and extension). Count is how many copies the deck holds; the server gives a
 * creature every listed spell without a limit, as it does for any creature.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
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

/// <summary>
/// One spell of a creature deck.
/// </summary>
/// <param name="Spell">The client spell template name.</param>
/// <param name="Count">How many copies the deck holds.</param>
public sealed record CreatureDeckSpell(string Spell, int Count);

/// <summary>
/// The deck of one creature template.
/// </summary>
/// <param name="Template">The creature's client template id.</param>
/// <param name="Name">The creature's name.</param>
/// <param name="Spells">The spells it casts.</param>
/// <param name="Source">The wiki revision the list comes from.</param>
/// <param name="PostCutoff">True when the revision is dated after the profile cutoff.</param>
public sealed record CreatureDeck(uint Template, string Name, ImmutableArray<CreatureDeckSpell> Spells, string Source, bool PostCutoff);

/// <summary>
/// The decks of the creatures the classic server knows.
/// </summary>
public sealed class CreatureDecks {

    /// <summary>
    /// A set with no decks.
    /// </summary>
    public static CreatureDecks Empty { get; } = new() {
        Id = "",
        Profiles = [],
        Decks = [],
        SourceFile = "",
    };

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required ImmutableArray<CreatureDeck> Decks { get; init; }
    public required string SourceFile { get; init; }

    internal FrozenDictionary<uint, CreatureDeck> _byTemplate { get; init; } = FrozenDictionary<uint, CreatureDeck>.Empty;

    /// <summary>
    /// The number of creature templates that have a deck.
    /// </summary>
    public int Count => _byTemplate.Count;

    /// <summary>
    /// The deck of <paramref name="template"/>, if the file lists one.
    /// </summary>
    public bool TryGet(uint template, out CreatureDeck deck) => _byTemplate.TryGetValue(template, out deck!);

}

/// <summary>
/// Loads and validates the creature-deck list.
/// </summary>
public static class CreatureDecksLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kind", "version", "id", "title", "profiles", "license_tag", "provenance", "notes", "creatures");
    internal static readonly FrozenSet<string> s_creatureKeys = FrozenSet.Create(StringComparer.Ordinal,
        "template", "name", "template_name", "use", "spells", "source", "source_date", "post_cutoff", "unmapped", "notes");
    internal static readonly FrozenSet<string> s_spellKeys = FrozenSet.Create(StringComparer.Ordinal, "spell", "count", "kind", "source");
    private static readonly Regex s_id = new(@"^creature-decks(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the list at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static CreatureDecks Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the creature-deck list does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["kind", "version", "id", "profiles", "license_tag", "creatures"]);
        if (map.Find("kind") is { } kindEntry && diagnostics.ReadString(kindEntry.Value, "kind") is { } kind && kind != "creature-decks") {
            diagnostics.At(kindEntry.Value, "kind", $"kind '{kind}' must be creature-decks");
        }

        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be creature-decks[-name] and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var decks = ImmutableArray.CreateBuilder<CreatureDeck>();
        var seen = new HashSet<uint>();
        if (map.Find("creatures") is { } creaturesEntry && diagnostics.ReadList(creaturesEntry.Value, "creatures") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("creatures", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } entry) {
                    continue;
                }

                diagnostics.CheckKeys(entry, keyPath, s_creatureKeys, ["template", "name", "spells", "source", "source_date", "post_cutoff"]);
                var template = entry.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(keyPath, "template"), 1, int.MaxValue) : null;
                var name = entry.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "name")) : null;
                var source = entry.Find("source") is { } s ? diagnostics.ReadString(s.Value, YamlTree.Join(keyPath, "source")) : null;
                var post = entry.Find("post_cutoff") is { } p ? diagnostics.ReadBool(p.Value, YamlTree.Join(keyPath, "post_cutoff")) : null;
                var spells = ImmutableArray.CreateBuilder<CreatureDeckSpell>();
                var spellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (entry.Find("spells") is { } spellsEntry && diagnostics.ReadList(spellsEntry.Value, YamlTree.Join(keyPath, "spells")) is { } spellList) {
                    if (spellList.Items.Length == 0) {
                        diagnostics.At(spellsEntry.Value, YamlTree.Join(keyPath, "spells"), "a deck needs at least one spell");
                    }

                    for (var j = 0; j < spellList.Items.Length; j++) {
                        var spellPath = YamlTree.Index(YamlTree.Join(keyPath, "spells"), j);
                        if (diagnostics.ReadMap(spellList.Items[j], spellPath) is not { } spellMap) {
                            continue;
                        }

                        diagnostics.CheckKeys(spellMap, spellPath, s_spellKeys, ["spell", "count"]);
                        var spell = spellMap.Find("spell") is { } sp ? diagnostics.ReadString(sp.Value, YamlTree.Join(spellPath, "spell")) : null;
                        var count = spellMap.Find("count") is { } c ? diagnostics.ReadInt(c.Value, YamlTree.Join(spellPath, "count"), 1, 12) : null;
                        if (spell is null || count is null) {
                            continue;
                        }

                        if (!spellNames.Add(spell)) {
                            diagnostics.At(spellMap.Find("spell")!.Value, YamlTree.Join(spellPath, "spell"), $"spell '{spell}' is listed twice for this creature");
                            continue;
                        }

                        spells.Add(new CreatureDeckSpell(spell, count.Value));
                    }
                }

                if (template is null || name is null || source is null || post is null) {
                    continue;
                }

                if (!seen.Add((uint) template.Value)) {
                    diagnostics.At(entry.Find("template")!.Value, YamlTree.Join(keyPath, "template"), $"template {template} is listed twice");
                    continue;
                }

                decks.Add(new CreatureDeck((uint) template.Value, name, spells.ToImmutable(), source, post.Value));
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        var built = decks.ToImmutable();

        return new CreatureDecks {
            Id = id!,
            Profiles = profiles,
            Decks = built,
            SourceFile = display,
            _byTemplate = built.ToFrozenDictionary(d => d.Template),
        };
    }

}
