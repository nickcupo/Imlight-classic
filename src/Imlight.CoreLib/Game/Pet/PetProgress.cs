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
 * CLASSIC PET PROGRESS
 * ========================================================================
 *
 * PURPOSE:
 * A pet item's growth on its ClientPetItemBehavior: stats and maximums from
 * the template, experience and levels (Baby..Epic), the talents it learns,
 * and the talent pool it can learn from (PetRules has the 2010 numbers).
 *
 * USAGE EXAMPLE:
 * PetProgress.EnsureInitialized(petItem);
 * var applied = PetProgress.ApplyStats(petItem, changes);
 * var growth = PetProgress.AddXp(petItem, xp, Random.Shared);
 *
 * NOTE:
 * Wire facts (official client r806919): PetStat.m_statID is the KI string
 * hash of the stat name (the pet window looks stats up by hashing
 * "Agility" and so on; m_name is not transmitted with an item), and a talent
 * id (m_expressedTalents, m_allTalents, MSG_PETLEVELUP.NewTalent) is the
 * string hash of the talent's m_talentName, which is also its
 * TemplateManifest id. m_XP is the pet's total experience and m_requiredXP
 * the total at which it reaches its next level.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>What one experience gain did to a pet.</summary>
internal sealed record PetGrowth(int OldLevel, int NewLevel, int Xp, IReadOnlyList<uint> NewTalents) {

    public bool LeveledUp => NewLevel > OldLevel;

}

internal static class PetProgress {

    private static Dictionary<uint, string> s_talentNames;

    public static ClientPetItemBehavior Behavior(WizClientObjectItem pet)
        => pet is not null && CoreObjectFactory.FindBehaviorInstance<ClientPetItemBehavior>(pet, out var b) ? b : null;

    public static uint StatId(string stat) => StringHash.Compute(stat);

    public static uint TalentId(string talent) => StringHash.Compute(talent);

    /// <summary>A talent's name from its id, from the client's TalentData templates.</summary>
    public static string TalentName(uint id) {
        var map = s_talentNames ??= BuildTalentNames();
        return map.GetValueOrDefault(id);
    }

    private static Dictionary<uint, string> BuildTalentNames() {
        var map = new Dictionary<uint, string>();
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            var file = location?.m_filename.ToString();
            if (file is null || !file.StartsWith("TalentData/", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            map.TryAdd(TalentId(name), name);
        }

        return map;
    }

    /// <summary>The talent's rarity (m_rank, 1..5) for pedigree; 1 when the template is missing.</summary>
    public static int TalentRank(string talent)
        => CoreObjectFactory.GetCoreTemplate(TalentId(talent)) is PetTalentTemplate t ? Math.Max(1, (int) t.m_rank) : 1;

    public static int[] Thresholds(uint templateId) {
        var levels = PetFactory.GetPetItemBehaviorTemplate(templateId)?.m_Levels;
        var perLevel = new List<uint>();
        if (levels is not null) {
            foreach (var info in levels.Where(l => l is not null).OrderBy(l => l.m_level)) {
                while (perLevel.Count < info.m_level) {
                    perLevel.Add(0);
                }

                perLevel.Add(info.m_requiredXP);
            }
        }

        return PetRules.Thresholds(perLevel);
    }

    /// <summary>The pet's school (the item template's m_school), for liked snacks.</summary>
    public static string School(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate t ? t.m_school.ToString() : "";

    public static IReadOnlyList<string> FavouriteSnackKinds(uint templateId)
        => PetFactory.GetPetItemBehaviorTemplate(templateId)?.m_favoriteSnackCategories?.Select(c => c.ToString()).ToList() ?? [];

    /// <summary>Fills a hatched pet's stats, maximums, talent pool and next-level mark from its template when missing.</summary>
    public static bool EnsureInitialized(WizClientObjectItem pet) {
        var b = Behavior(pet);
        if (b is null || b.m_level == 0) {
            return false;
        }

        var template = PetFactory.GetPetItemBehaviorTemplate((uint) pet.m_templateID);
        var changed = false;
        if (b.m_maxStats is null || b.m_maxStats.Count == 0) {
            b.m_maxStats = ToStats(template?.m_maxStats, fallback: 0);
            changed = true;
        }

        if (b.m_currentStats is null || b.m_currentStats.Count == 0) {
            b.m_currentStats = ToStats(template?.m_startStats, fallback: 1);
            changed = true;
        }

        if (b.m_allTalents is null || b.m_allTalents.Count == 0) {
            b.m_allTalents = [.. (template?.m_talents ?? []).Select(t => t.ToString()).Where(PetRules.IsTalentIn2010).Select(TalentId)];
            changed = true;
        }

        b.m_expressedTalents ??= [];
        b.m_level = (byte) Math.Clamp((int) b.m_level, PetRules.Baby, PetRules.MaxLevel);
        var required = (uint) PetRules.NextLevelXp(b.m_level, Thresholds((uint) pet.m_templateID));
        if (b.m_requiredXP != required) {
            b.m_requiredXP = required;
            changed = true;
        }

        changed |= RefreshRating(b);
        return changed;
    }

    private static List<PetStat> ToStats(IEnumerable<PetStat> source, int fallback) {
        var byName = (source ?? []).Where(s => s is not null).GroupBy(s => s.m_name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().m_value, StringComparer.OrdinalIgnoreCase);

        return [.. PetRules.StatNames.Select(name => new PetStat {
            m_name = name,
            m_statID = StatId(name),
            m_value = byName.TryGetValue(name, out var v) ? v : fallback,
        })];
    }

    public static Dictionary<string, int> Stats(List<PetStat> stats)
        => (stats ?? []).Where(s => s is not null)
            .GroupBy(s => NameOf(s), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().m_value, StringComparer.OrdinalIgnoreCase);

    private static string NameOf(PetStat stat) {
        var name = stat.m_name.ToString();
        if (!string.IsNullOrEmpty(name)) {
            return name;
        }

        return PetRules.StatNames.FirstOrDefault(n => StatId(n) == stat.m_statID) ?? stat.m_statID.ToString();
    }

    /// <summary>Raises the pet's stats (never past its maximums); returns what changed.</summary>
    public static IReadOnlyList<PetStatChange> ApplyStats(WizClientObjectItem pet, IEnumerable<PetStatChange> changes) {
        var b = Behavior(pet);
        if (b is null) {
            return [];
        }

        var current = Stats(b.m_currentStats);
        var applied = PetRules.ApplyStats(current, PetTalentRuntime.EffectiveMaximums(pet), changes);
        b.m_currentStats = [.. PetRules.StatNames.Select(name => new PetStat {
            m_name = name, m_statID = StatId(name), m_value = current.GetValueOrDefault(name),
        })];
        RefreshRating(b);
        return applied;
    }

    /// <summary>Adds experience; a pet that reaches a new level learns one talent per level gained.</summary>
    public static PetGrowth AddXp(WizClientObjectItem pet, int xp, Random random) {
        var b = Behavior(pet);
        if (b is null) {
            return new PetGrowth(0, 0, 0, []);
        }

        var thresholds = Thresholds((uint) pet.m_templateID);
        var oldLevel = (int) b.m_level;
        var oldXp = (int) b.m_XP;
        var total = PetRules.CapXp(oldXp + Math.Max(0, xp), thresholds);
        b.m_XP = (uint) total;
        var newLevel = Math.Max(oldLevel, PetRules.LevelFor(total, thresholds));
        var learned = new List<uint>();
        b.m_expressedTalents ??= [];
        for (var level = oldLevel + 1; level <= newLevel; level++) {
            var known = b.m_expressedTalents.Select(TalentName).Where(n => n is not null).ToList();
            var pool = (b.m_allTalents ?? []).Select(TalentName).Where(n => n is not null).ToList();
            if (known.Count >= PetRules.MaxTalents) {
                break;
            }

            var pick = PetRules.PickTalent(pool, known, random);
            if (pick is not null) {
                var id = TalentId(pick);
                b.m_expressedTalents.Add(id);
                learned.Add(id);
            }
        }

        b.m_level = (byte) newLevel;
        b.m_requiredXP = (uint) PetRules.NextLevelXp(newLevel, thresholds);
        RefreshRating(b);
        return new PetGrowth(oldLevel, newLevel, total - oldXp, learned);
    }

    /// <summary>
    /// The two numbers over a pet's head: pedigree (the summed rarity of every talent it can learn) and, in brackets,
    /// that of the talents it knows (Training Pets, oldid 124234).
    /// </summary>
    private static bool RefreshRating(ClientPetItemBehavior b) {
        var overall = (uint) (b.m_allTalents ?? []).Select(TalentName).Where(n => n is not null).Sum(TalentRank);
        var active = (uint) (b.m_expressedTalents ?? []).Select(TalentName).Where(n => n is not null).Sum(TalentRank);
        if (b.m_overallRating == overall && b.m_activeRating == active) {
            return false;
        }

        (b.m_overallRating, b.m_activeRating) = (overall, active);
        return true;
    }

}
