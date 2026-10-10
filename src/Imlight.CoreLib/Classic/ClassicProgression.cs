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
 * CLASSIC PROGRESSION
 * ========================================================================
 *
 * PURPOSE:
 * Holds the profile's XP table, mob reward rules, badges and treasure-card
 * quest rewards, loaded at boot, for MagicLevelsConfig, the combat
 * resolver, CombatService, ClassicBadges and QuestService.
 *
 * USAGE EXAMPLE:
 * ClassicProgression.Initialize(profile, classicDataRoot);   // ClassicStartup, restricted profiles only
 * if (ClassicProgression.MobRewards is { } rules) { ... }
 *
 * NOTE:
 * Null tables mean stock Imlight: the client's XP curve, 3 XP per pip
 * counted Imlight's way and SpiralDB mob loot only. A table the profile
 * names but that does not exist is a warning (ClassicStartup.CheckRuleTables);
 * one that exists but is invalid stops the boot.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System.IO;
using System.Linq;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The active profile's XP table and mob reward rules.
/// </summary>
public static class ClassicProgression {

    private static volatile XpTable? s_xpTable;
    private static volatile HealthTable? s_healthTable; // CLASSIC
    private static volatile MobRewardRules? s_mobRewards;
    private static volatile BadgeRules? s_badges;
    private static volatile QuestCardRewards? s_questCards;
    private static volatile TreasurePrices? s_treasurePrices; // CLASSIC
    private static volatile MobStats? s_mobStats; // CLASSIC
    private static volatile CrownShopCatalog? s_crownShop; // CLASSIC
    private static volatile LaterObjects s_laterObjects = LaterObjects.Empty; // CLASSIC
    private static volatile CreatureDecks s_creatureDecks = CreatureDecks.Empty; // CLASSIC
    private static volatile PotionRules? s_potions; // CLASSIC
    private static volatile SecondChanceRules? s_secondChance; // CLASSIC
    private static volatile BossCheats s_bossCheats = BossCheats.Empty; // CLASSIC

    /// <summary>
    /// The profile's XP table, or null for the client's curve.
    /// </summary>
    public static XpTable? XpTable => s_xpTable;

    /// <summary>
    /// The profile's base health per level and school, or null for the client's. CLASSIC.
    /// </summary>
    public static HealthTable? HealthTable => s_healthTable;

    /// <summary>
    /// The profile's combat XP, gold and drop rules, or null for stock Imlight.
    /// </summary>
    public static MobRewardRules? MobRewards => s_mobRewards;

    /// <summary>
    /// The profile's badges, or null for stock Imlight (no badges awarded).
    /// </summary>
    public static BadgeRules? Badges => s_badges;

    /// <summary>
    /// The profile's treasure-card quest rewards, or null for none.
    /// </summary>
    public static QuestCardRewards? QuestCards => s_questCards;

    /// <summary>
    /// The profile's library treasure-card prices, or null for none.
    /// </summary>
    public static TreasurePrices? TreasurePrices => s_treasurePrices;

    /// <summary>
    /// The profile's creature health at the cutoff, or null for template health.
    /// </summary>
    public static MobStats? MobStats => s_mobStats;

    /// <summary>
    /// The profile's Crown Shop catalog, or null for no Crown Shop.
    /// </summary>
    public static CrownShopCatalog? CrownShop => s_crownShop;

    /// <summary>
    /// The zone objects of later versions the server does not spawn; empty when the profile names no list.
    /// </summary>
    public static LaterObjects LaterObjects => s_laterObjects;

    /// <summary>
    /// The decks creatures of the era cast, by creature template; empty when the profile names no file. CLASSIC.
    /// </summary>
    public static CreatureDecks CreatureDecks => s_creatureDecks;

    /// <summary>
    /// The profile's potion flask rules, or null for stock Imlight (no minigame fill, level-band shop prices). CLASSIC.
    /// </summary>
    public static PotionRules? Potions => s_potions;

    /// <summary>
    /// The profile's Second Chance chests, or null for none. CLASSIC.
    /// </summary>
    public static SecondChanceRules? SecondChance => s_secondChance;

    /// <summary>
    /// The profile's scripted boss cheats (Briskbreeze Tower); empty when the profile names no file. CLASSIC.
    /// </summary>
    public static BossCheats BossCheats => s_bossCheats;

    /// <summary>CLASSIC: test hook.</summary>
    internal static void UseBossCheatsForTests(BossCheats? cheats) => s_bossCheats = cheats ?? BossCheats.Empty;

    /// <summary>CLASSIC: test hook.</summary>
    internal static void UseForTests(PotionRules? potions, SecondChanceRules? secondChance) {
        s_potions = potions;
        s_secondChance = secondChance;
    }

    /// <summary>
    /// Loads the tables a restricted profile names.
    /// </summary>
    /// <param name="profile">The active profile.</param>
    /// <param name="classicDataRoot">The classic-data directory.</param>
    /// <exception cref="ClassicDataException">A named table exists but is invalid.</exception>
    public static void Initialize(ClassicProfile profile, string classicDataRoot) {
        if (profile.Rules.XpTable is { } xp && File.Exists(Path.Combine(classicDataRoot, xp))) {
            s_xpTable = XpTableLoader.Load(Path.Combine(classicDataRoot, xp));
            Logger.Information("Classic XP table {Table}: levels 1-{MaxLevel}, {Total} XP to reach level {MaxLevel}.",
                Logger.Args(s_xpTable.Id, s_xpTable.MaxLevel, s_xpTable.XpToReach(s_xpTable.MaxLevel)!.Value, s_xpTable.MaxLevel));
        }

        if (profile.Rules.PlayerHealth is { } health && File.Exists(Path.Combine(classicDataRoot, health))) {
            s_healthTable = HealthTableLoader.Load(Path.Combine(classicDataRoot, health)); // CLASSIC
            Logger.Information("Classic health table {Table}: base health for levels 1-{MaxLevel} of {Schools} schools.",
                Logger.Args(s_healthTable.Id, s_healthTable.MaxLevel, s_healthTable.BySchool.Count));
        }

        if (profile.Rules.MobRewards is { } mob && File.Exists(Path.Combine(classicDataRoot, mob))) {
            s_mobRewards = MobRewardRulesLoader.Load(Path.Combine(classicDataRoot, mob));
            Logger.Information("Classic mob rewards {Table}: {XpPerPip} XP per pip, gold for {Ranks} ranks, {Mobs} documented mobs ({Templates} templates).",
                Logger.Args(s_mobRewards.Id, s_mobRewards.CombatXp.XpPerPip, s_mobRewards.GoldByRank.Count,
                    s_mobRewards.Mobs.Length, s_mobRewards.MobCount));
        }

        if (profile.Rules.Badges is { } badges && File.Exists(Path.Combine(classicDataRoot, badges))) {
            s_badges = BadgeRulesLoader.Load(Path.Combine(classicDataRoot, badges));
            Logger.Information("Classic badges {Table}: {Count} badges, {Granted} awarded by the server.",
                Logger.Args(s_badges.Id, s_badges.Badges.Length, s_badges.Granted.Count()));
        }

        if (profile.Rules.QuestCards is { } cards && File.Exists(Path.Combine(classicDataRoot, cards))) {
            s_questCards = QuestCardRewardsLoader.Load(Path.Combine(classicDataRoot, cards));
            Logger.Information("Classic quest cards {Table}: treasure cards for {Count} quests.",
                Logger.Args(s_questCards.Id, s_questCards.ByQuest.Count));
        }

        if (profile.Rules.TreasurePrices is { } prices && File.Exists(Path.Combine(classicDataRoot, prices))) {
            s_treasurePrices = TreasurePricesLoader.Load(Path.Combine(classicDataRoot, prices));
            Logger.Information("Classic treasure prices {Table}: library prices for {Count} cards; other cards cost their template price.",
                Logger.Args(s_treasurePrices.Id, s_treasurePrices.ByName.Count));
        }

        if (profile.Rules.MobStats is { } mobStats && File.Exists(Path.Combine(classicDataRoot, mobStats))) {
            s_mobStats = MobStatsLoader.Load(Path.Combine(classicDataRoot, mobStats), profile.Id); // CLASSIC: per-profile dated entries
            Logger.Information("Classic mob stats {Table}: dated health for {Count} creature templates; others keep template health.",
                Logger.Args(s_mobStats.Id, s_mobStats.HealthByTemplate.Count));
        }

        if (profile.Rules.LaterObjects is { } laterObjects && File.Exists(Path.Combine(classicDataRoot, laterObjects))) {
            s_laterObjects = LaterObjectsLoader.Load(Path.Combine(classicDataRoot, laterObjects));
            Logger.Information("Classic later objects {Table}: {Count} entries hide {Templates} templates of later versions.",
                Logger.Args(s_laterObjects.Id, s_laterObjects.Objects.Length, s_laterObjects.TemplateCount));
        }

        if (profile.Rules.CreatureDecks is { } creatureDecks && File.Exists(Path.Combine(classicDataRoot, creatureDecks))) {
            s_creatureDecks = CreatureDecksLoader.Load(Path.Combine(classicDataRoot, creatureDecks)); // CLASSIC
            Logger.Information("Classic creature decks {Table}: own spell lists for {Count} creature templates.",
                Logger.Args(s_creatureDecks.Id, s_creatureDecks.Count));
        }

        if (profile.Rules.Potions is { } potions && File.Exists(Path.Combine(classicDataRoot, potions))) {
            s_potions = PotionRulesLoader.Load(Path.Combine(classicDataRoot, potions)); // CLASSIC
            Logger.Information("Classic potions {Table}: minigames pay {Mana} x max mana, overflow fills flasks; shop {Per} gold a level, {Min}-{Max}.",
                Logger.Args(s_potions.Id, s_potions.MinigameManaReward, s_potions.PricePerLevel, s_potions.MinPrice, s_potions.MaxPrice));
        }

        if (profile.Rules.SecondChance is { } secondChance && File.Exists(Path.Combine(classicDataRoot, secondChance))) {
            s_secondChance = SecondChanceRulesLoader.Load(Path.Combine(classicDataRoot, secondChance)); // CLASSIC
            Logger.Information("Classic Second Chance {Table}: {Count} chests, {First} Crowns +{Step} a use, {Uses} uses a day.",
                Logger.Args(s_secondChance.Id, s_secondChance.Chests.Length, s_secondChance.FirstCost, s_secondChance.CostStep, s_secondChance.DailyUses));
        }

        if (profile.Rules.BossCheats is { } bossCheats && File.Exists(Path.Combine(classicDataRoot, bossCheats))) {
            s_bossCheats = BossCheatsLoader.Load(Path.Combine(classicDataRoot, bossCheats)); // CLASSIC
            Logger.Information("Classic boss cheats {Table}: {Count} scripted bosses in {Dungeon}.",
                Logger.Args(s_bossCheats.Id, s_bossCheats.Count, s_bossCheats.DungeonZone));
        }

        // CLASSIC: the dungeons and their reset rule; a profile without the file keeps the built-in Golem Tower rule.
        if (profile.Rules.InstanceResets is { } instanceResets && File.Exists(Path.Combine(classicDataRoot, instanceResets))) {
            var resets = InstanceResetRulesLoader.Load(Path.Combine(classicDataRoot, instanceResets));
            Imlight.Classic.Travel.InstanceGroups.Use(resets);
            Logger.Information("Classic instance resets {Table}: {Groups} dungeons ({Multi} with several zones, {Gauntlets} gauntlets); empty copies kept {Empty} min, left copies {Window} min.",
                Logger.Args(resets.Id, resets.Groups.Length, resets.Groups.Count(g => g.Zones.Length > 1),
                    resets.Groups.Count(g => g.Kind == InstanceKind.Gauntlet), resets.EmptyLifetime.TotalMinutes, resets.ReturnWindow.TotalMinutes));
        }

        if (profile.Rules.CrownShop is { } crownShop && File.Exists(Path.Combine(classicDataRoot, crownShop))) {
            s_crownShop = CrownShopCatalogLoader.Load(Path.Combine(classicDataRoot, crownShop));
            Logger.Information("Classic Crown Shop {Table}: {Count} items.", Logger.Args(s_crownShop.Id, s_crownShop.Items.Length));
        }
    }

}
