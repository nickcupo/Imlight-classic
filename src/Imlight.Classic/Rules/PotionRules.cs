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
 * CLASSIC POTION RULES
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 potion flasks (classic-data/rules/potions-2009.yaml): minigames refill mana and, with the mana globe
 * full, fill the flasks; Hilda Brewer sells refills by the wizard's level.
 *
 * USAGE EXAMPLE:
 * var rules = PotionRulesLoader.Load(path);
 * var fill = rules.MinigameFill(currentMana: 120, maxMana: 120, flasks: 0.5f, maxFlasks: 3);  // mana 120, flasks 1.5
 * var gold = rules.ShopPrice(level: 13);                                                        // 130
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>What a minigame's mana reward does: the new mana and flask charge, and how much went to each.</summary>
public readonly record struct PotionFill(int Mana, float Flasks, int ManaGained, float FlasksGained);

/// <summary>
/// How potion flasks fill and what a refill costs.
/// </summary>
public sealed class PotionRules {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>Mana for reaching a minigame's first score threshold, as a fraction of max mana.</summary>
    public required double MinigameManaReward { get; init; }

    /// <summary>Flasks filled by one max mana's worth of mana the globe cannot hold.</summary>
    public required double FlaskPerMaxMana { get; init; }

    public required int PricePerLevel { get; init; }
    public required int MinPrice { get; init; }
    public required int MaxPrice { get; init; }

    public required string SourceFile { get; init; }

    /// <summary>The mana a minigame pays a wizard with <paramref name="maxMana"/> for reaching its first threshold.</summary>
    public int MinigameMana(int maxMana) => maxMana <= 0 ? 0 : (int) Math.Round(maxMana * MinigameManaReward, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Pays <paramref name="reward"/> mana: the globe first, then what it cannot hold into the flasks, up to
    /// <paramref name="maxFlasks"/>.
    /// </summary>
    public PotionFill Fill(int reward, int currentMana, int maxMana, float flasks, float maxFlasks) {
        if (reward <= 0 || maxMana <= 0) {
            return new PotionFill(currentMana, flasks, 0, 0);
        }

        var room = Math.Max(0, maxMana - currentMana);
        var toMana = Math.Min(room, reward);
        var overflow = reward - toMana;
        var newFlasks = flasks;
        if (overflow > 0 && maxFlasks > flasks) {
            newFlasks = (float) Math.Min(maxFlasks, flasks + overflow * FlaskPerMaxMana / maxMana);
        }

        return new PotionFill(currentMana + toMana, newFlasks, toMana, newFlasks - flasks);
    }

    /// <summary>A minigame's mana reward for reaching its first threshold, applied (see <see cref="Fill"/>).</summary>
    public PotionFill MinigameFill(int currentMana, int maxMana, float flasks, float maxFlasks)
        => Fill(MinigameMana(maxMana), currentMana, maxMana, flasks, maxFlasks);

    /// <summary>Hilda Brewer's price for one potion at <paramref name="level"/>.</summary>
    public int ShopPrice(int level) => Math.Clamp(PricePerLevel * Math.Max(1, level), MinPrice, MaxPrice);

}

/// <summary>
/// Loads and validates classic potion rules.
/// </summary>
public static class PotionRulesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "license_tag", "notes", "minigame", "shop", "provenance");
    private static readonly FrozenSet<string> s_minigameKeys = FrozenSet.Create(StringComparer.Ordinal, "mana_reward", "flask_per_max_mana");
    private static readonly FrozenSet<string> s_shopKeys = FrozenSet.Create(StringComparer.Ordinal, "price_per_level", "min_price", "max_price");
    private static readonly Regex s_id = new(@"^potions-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the rules at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static PotionRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the potion rules do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is not YMap map) {
            if (root is not null) {
                diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "license_tag", "minigame", "shop", "provenance"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be potions-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        double? manaReward = null, flaskPerMaxMana = null;
        if (map.Find("minigame") is { } mg && diagnostics.ReadMap(mg.Value, "minigame") is { } minigame) {
            diagnostics.CheckKeys(minigame, "minigame", s_minigameKeys, s_minigameKeys);
            manaReward = minigame.Find("mana_reward") is { } r ? diagnostics.ReadFraction(r.Value, "minigame.mana_reward") : null;
            flaskPerMaxMana = minigame.Find("flask_per_max_mana") is { } f ? diagnostics.ReadFraction(f.Value, "minigame.flask_per_max_mana") : null;
            if (manaReward == 0 || flaskPerMaxMana == 0) {
                diagnostics.At(minigame, "minigame", "mana_reward and flask_per_max_mana must be above 0");
            }
        }

        int? perLevel = null, min = null, max = null;
        if (map.Find("shop") is { } sh && diagnostics.ReadMap(sh.Value, "shop") is { } shop) {
            diagnostics.CheckKeys(shop, "shop", s_shopKeys, s_shopKeys);
            perLevel = shop.Find("price_per_level") is { } p ? diagnostics.ReadInt(p.Value, "shop.price_per_level", 0, 100_000) : null;
            min = shop.Find("min_price") is { } lo ? diagnostics.ReadInt(lo.Value, "shop.min_price", 0, 1_000_000) : null;
            max = shop.Find("max_price") is { } hi ? diagnostics.ReadInt(hi.Value, "shop.max_price", 0, 1_000_000) : null;
            if (min > max) {
                diagnostics.At(shop, "shop", $"min_price {min} is above max_price {max}");
            }
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new PotionRules {
            Id = id!,
            Profiles = profiles,
            MinigameManaReward = manaReward!.Value,
            FlaskPerMaxMana = flaskPerMaxMana!.Value,
            PricePerLevel = perLevel!.Value,
            MinPrice = min!.Value,
            MaxPrice = max!.Value,
            SourceFile = display,
        };
    }

}
