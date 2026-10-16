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
    private static volatile LevelStats? s_levelStats; // CLASSIC
    private static volatile CriticalModel? s_criticalModel; // CLASSIC
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
    private static volatile PuzzleHelpers s_puzzleHelpers = PuzzleHelpers.Empty; // CLASSIC
    private static volatile AstralRules s_astral = AstralRules.Empty; // CLASSIC: Celestia Astral schools

    /// <summary>
    /// The profile's XP table, or null for the client's curve.
    /// </summary>
    public static XpTable? XpTable => s_xpTable;

    /// <summary>
    /// The profile's base health per level and school, or null for the client's. CLASSIC.
    /// </summary>
    public static HealthTable? HealthTable => s_healthTable;

    /// <summary>
    /// CLASSIC: the profile's mana, power pip chance and training points for a level range (rules.level_stats), or
    /// null for the client's values at every level.
    /// </summary>
    public static LevelStats? LevelStats => s_levelStats;

    /// <summary>
    /// CLASSIC: the profile's critical and block model (rules.critical, the 2010-2014 percent model), or null for the
    /// modern model. Used only while the profile's critical_and_block switch is on.
    /// </summary>
    public static CriticalModel? CriticalModel => s_criticalModel;

    /// <summary>CLASSIC: test hook.</summary>
    internal static void UseCriticalModelForTests(CriticalModel? model) => s_criticalModel = model;

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

    /// <summary>CLASSIC: October-only puzzle answers and goal-arrow policy; other profiles always receive Empty.</summary>
    public static PuzzleHelpers PuzzleHelpers => s_puzzleHelpers;

    /// <summary>CLASSIC: the Astral schools file (rules.astral: Celestia trainers, dated polymorph, Moon rework); Empty without one.</summary>
    public static AstralRules Astral => s_astral;

    /// <summary>CLASSIC: tests install an Astral rules file (Empty restores the default).</summary>
    internal static void SetAstralForTests(AstralRules rules) => s_astral = rules;

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
        // CLASSIC: unlike optional legacy tables, a configured puzzle policy must exist and validate at startup.
        s_puzzleHelpers = PuzzleHelpersLoader.LoadForProfile(profile, classicDataRoot);
        // CLASSIC: the Astral schools (celestia-2010); a configured file must exist and validate.
        s_astral = AstralRulesLoader.LoadForProfile(profile, classicDataRoot);
        if (s_astral.AppliesTo(profile.Id)) {
            Logger.Information("Classic Astral schools {Table}: {Trainers} trainers, {Exclusions} polymorph item exclusions, Moon rework {Rework} (owner extra, [Classic] MoonRework).",
                Logger.Args(s_astral.Id, s_astral.Trainers.Length, s_astral.FormItemExclusions.Count, s_astral.MoonRework is null ? "absent" : "configured"));
        }
        if (s_puzzleHelpers.AppliesTo(profile.Id)) {
            Logger.Information("Classic puzzle helpers {Table}: {Zones} zones, {Quests} quests.",
                Logger.Args(s_puzzleHelpers.Id, s_puzzleHelpers.Zones.Length, s_puzzleHelpers.QuestHelpers.Length));
        }

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

        s_levelStats = null; // CLASSIC
        if (profile.Rules.LevelStats is { } levelStats && File.Exists(Path.Combine(classicDataRoot, levelStats))) {
            s_levelStats = LevelStatsLoader.Load(Path.Combine(classicDataRoot, levelStats));
            Logger.Information("Classic level stats {Table}: mana, power pip chance and training points for levels {Min}-{Max} ({Status}).",
                Logger.Args(s_levelStats.Id, s_levelStats.MinLevel, s_levelStats.MaxLevel, s_levelStats.Status));
            if (s_levelStats.Status == "unverified") {
                Logger.Warning("Classic level stats {Table} are client values without a dated source, awaiting the owner's ruling {Decision}.",
                    Logger.Args(s_levelStats.Id, s_levelStats.Decision ?? "(none named)"));
            }
        }

        s_criticalModel = null; // CLASSIC
        if (profile.Rules.Critical is { } critical && File.Exists(Path.Combine(classicDataRoot, critical))) {
            s_criticalModel = CriticalModelLoader.Load(Path.Combine(classicDataRoot, critical));
            Logger.Information("Classic critical model {Table}: {Crit} chance per critical rating, {Block} per block rating, x{Multiplier} ({Status}).",
                Logger.Args(s_criticalModel.Id, s_criticalModel.CritPerRating, s_criticalModel.BlockPerRating,
                    s_criticalModel.CritMultiplier, s_criticalModel.Status));
            if (s_criticalModel.Status == "unverified") {
                Logger.Warning("Classic critical model {Table} holds 2014 client values without a source dated in 2010, per the owner's ruling {Decision}.",
                    Logger.Args(s_criticalModel.Id, s_criticalModel.Decision ?? "(none named)"));
            }
        }

        if (profile.Rules.MobRewards is { } mob && File.Exists(Path.Combine(classicDataRoot, mob))) {
            s_mobRewards = MobRewardRulesLoader.Load(Path.Combine(classicDataRoot, mob));
            Logger.Information("Classic mob rewards {Table}: {XpPerPip} XP per pip, gold for {Ranks} ranks, {Mobs} documented mobs ({Templates} templates).",
                Logger.Args(s_mobRewards.Id, s_mobRewards.CombatXp.XpPerPip, s_mobRewards.GoldByRank.Count,
                    s_mobRewards.Mobs.Length, s_mobRewards.MobCount));
        }

        if (profile.Rules.Badges is { } badges && File.Exists(Path.Combine(classicDataRoot, badges))) {
            // CLASSIC: a quest the profile disables (disabled_quests) cannot be completed, so no badge waits for it.
            s_badges = BadgeRulesLoader.Load(Path.Combine(classicDataRoot, badges)).WithoutQuests(profile.DisabledQuests);
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
