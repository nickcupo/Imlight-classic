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
 * CLASSIC RULES
 * ========================================================================
 * 
 * PURPOSE:
 * The one place server hooks ask what the active profile allows: zones and
 * worlds, hubs for fallbacks, the level cap and XP ceiling, and features.
 * 
 * USAGE EXAMPLE:
 * var decision = ClassicRuntime.Rules.IsZoneAllowed(message.DestinationZone);
 * level = ClassicRuntime.Rules.ClampLevel(level, MagicLevelsConfig.MaxLevel);
 * 
 * NOTE:
 * An unrestricted profile (dev-unrestricted, or Stock when no profile is
 * configured) answers exactly like stock Imlight from every member, so hook
 * sites need no "is classic on" checks. On a restricted profile a zone no
 * rule covers is closed.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Classic.Travel;
using Imlight.Classic.Zones;

namespace Imlight.Classic;

/// <summary>
/// The rules of one profile over one zone map.
/// </summary>
public sealed class ClassicRules {

    /// <summary>
    /// The <see cref="ZoneDecision.RuleSource"/> of a zone no world or area prefix covers.
    /// </summary>
    public const string UnmappedRule = "unmapped";

    /// <summary>
    /// The <see cref="ZoneDecision.RuleSource"/> of a Spiral Door request for a hub key no open world has.
    /// </summary>
    public const string HubKeyRule = "hub_key";

    private readonly XpCapPolicy _xpPolicy;

    /// <summary>
    /// Creates the rules and cross-checks the profile against the map.
    /// </summary>
    /// <param name="profile">The merged profile.</param>
    /// <param name="zones">The zone map.</param>
    /// <param name="xpPolicy">Where XP stops at the cap.</param>
    /// <exception cref="ClassicDataException">A restricted profile names a world the map lacks, or allows no world with a hub.</exception>
    public ClassicRules(ClassicProfile profile, ZoneWorldMap zones, XpCapPolicy xpPolicy = XpCapPolicy.FillBar) {
        Profile = profile;
        Zones = zones;
        _xpPolicy = xpPolicy;
        IsRestricted = !profile.IsUnrestricted;
        CriticalAndBlockEnabled = profile.Features.IsEnabled(ClassicFeatures.CriticalAndBlock);
        UntargetedAreaSpells = profile.Features.IsEnabled(ClassicFeatures.UntargetedAreaSpells);
        StunGivesStunBlock = profile.Cutoff is not { } cutoff || cutoff >= StunBlockRuleDate;

        if (IsRestricted) {
            CrossValidate();
        }
    }

    /// <summary>
    /// The built-in stock rules: an unrestricted profile over an empty map. Everything passes through.
    /// </summary>
    public static ClassicRules Stock { get; } = new(new ClassicProfile {
        Id = "stock",
        Title = "Stock Imlight (no [Classic] profile configured)",
        Status = ProfileStatus.Debug,
        Features = FeatureSwitches.None,
        Rules = ProfileRules.None,
        SourceFiles = [],
    }, ZoneWorldMap.Empty);

    public ClassicProfile Profile { get; }
    public ZoneWorldMap Zones { get; }

    /// <summary>
    /// True when the profile restricts anything.
    /// </summary>
    public bool IsRestricted { get; }

    /// <summary>
    /// The <c>critical_and_block</c> switch, precomputed for the combat hot path.
    /// </summary>
    public bool CriticalAndBlockEnabled { get; }

    /// <summary>
    /// The <c>untargeted_area_spells</c> switch (May 2010). Off: a spell that attacks all enemies takes an enemy
    /// target and is not cast when that enemy is defeated before it resolves.
    /// </summary>
    public bool UntargetedAreaSpells { get; }

    /// <summary>
    /// The July 2009 update (1.90, 2009-07-01) that gives a stunned target one stun block.
    /// </summary>
    public static readonly DateOnly StunBlockRuleDate = new(2009, 7, 1);

    /// <summary>
    /// True when a stun leaves its target one stun block: every profile whose cutoff is on or after
    /// <see cref="StunBlockRuleDate"/>, and stock. Off for arc1-2009h1.
    /// </summary>
    public bool StunGivesStunBlock { get; }

    /// <summary>
    /// True when quest data is read the KingsIsle way: requirement lists left to right, every item of a quest
    /// reward table granted, zone triggers disarmed by their deactivate events, and held quests or teleports
    /// with missing data skipped. Stock Imlight's reading otherwise.
    /// </summary>
    public bool UsesKingsIsleQuestRules => IsRestricted;

    /// <summary>
    /// True when new characters get the classic start (<c>rules.tutorial: unicorn-way-classic</c>): they land in
    /// Ambrose's office with the classic starter kit.
    /// </summary>
    public bool UsesClassicStart
        => IsRestricted && string.Equals(Profile.Rules.Tutorial, ClassicSchema.ClassicTutorial, StringComparison.Ordinal);

    /// <summary>
    /// True when teleport stones must be discovered by reaching the far stone of each pair
    /// (<c>rules.teleport_stones: discover</c>): the stones' discovery triggers fire on their volume only, not on
    /// arriving in the zone.
    /// </summary>
    public bool TeleportStonesNeedDiscovery
        => IsRestricted && string.Equals(Profile.Rules.TeleportStones, ClassicSchema.DiscoverTeleportStones, StringComparison.Ordinal);

    /// <summary>
    /// Decides whether a player may enter <paramref name="zone"/>. The first failing check decides.
    /// </summary>
    /// <param name="zone">The zone name.</param>
    /// <returns>The decision with its audit reason and player text.</returns>
    public ZoneDecision IsZoneAllowed(string? zone) {
        var name = zone ?? "";
        if (!IsRestricted) {
            return new ZoneDecision(true, name, null, null, null,
                $"profile {Profile.Id} is unrestricted", "", "profile", null);
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return Deny(name, null, null, "empty zone name", ClassicMessages.Unmapped, "zone", null);
        }

        var classification = Zones.Classify(name);
        var home = classification.Home;
        if (home is null) {
            return Deny(name, null, null, $"no zone-map prefix covers '{name}'", ClassicMessages.Unmapped, UnmappedRule, null);
        }

        var worldId = classification.EffectiveWorld?.Id;
        var homeWorldId = home.World?.Id;
        var areaId = home.Area?.Id;
        var area = home.Area;

        foreach (var rule in classification.Overrides) {
            if (rule.Deny) {
                return Deny(classification, $"closed by {Describe(rule)}",
                    rule.Message ?? ClassicMessages.AreaClosed, rule.RuleSource, rule.Confidence);
            }
        }

        if (area is { Access: AreaAccess.Deny }) {
            return Deny(classification, $"area {area.Id} is closed ({Describe(home, area)})",
                area.Message ?? ClassicMessages.AreaClosed, home.RuleSource, area.Confidence);
        }

        if (classification.EffectiveWorld is { } world && !Profile.AllowsWorld(world.Id)) {
            var source = classification.WorldOverride?.RuleSource ?? home.RuleSource;
            var confidence = classification.WorldOverride?.Confidence;
            var detail = classification.WorldOverride is { } reassigned ? $": {reassigned.Reason} (confidence {confidence})" : "";

            return Deny(classification, $"world {world.Id} is not in profile {Profile.Id} ({source}){detail}",
                ClassicMessages.WorldClosed(world.Name), source, confidence);
        }

        if (area is { Access: AreaAccess.Feature, Feature: { } areaFeature } && !IsFeatureEnabled(areaFeature)) {
            return Deny(classification, $"feature {areaFeature} is off in profile {Profile.Id} ({Describe(home, area)})",
                area.Message ?? ClassicMessages.AreaClosed, home.RuleSource, area.Confidence);
        }

        foreach (var rule in classification.Overrides) {
            if (rule.Feature is { } feature && !IsFeatureEnabled(feature)) {
                return Deny(classification, $"feature {feature} is off in profile {Profile.Id} ({Describe(rule)})",
                    rule.Message ?? ClassicMessages.AreaClosed, rule.RuleSource, rule.Confidence);
            }
        }

        if (Profile.Cutoff is { } cutoff) {
            if (area is not null && DateRuleCloses(area.Introduced, area.IntroducedAfter, cutoff) is { } areaWhy) {
                return Deny(classification, $"{areaWhy} ({Describe(home, area)})",
                    area.Message ?? ClassicMessages.AreaClosed, home.RuleSource, area.Confidence);
            }

            foreach (var rule in classification.Overrides) {
                if (DateRuleCloses(rule.Introduced, rule.IntroducedAfter, cutoff) is { } overrideWhy) {
                    return Deny(classification, $"{overrideWhy} ({Describe(rule)})",
                        rule.Message ?? ClassicMessages.AreaClosed, rule.RuleSource, rule.Confidence);
                }
            }
        }

        var what = worldId is not null ? $"world {worldId}" : $"area {areaId}";

        return new ZoneDecision(true, name, worldId, homeWorldId, areaId,
            $"open: {what} ({home.RuleSource})", "", home.RuleSource, area?.Confidence);
    }

    /// <summary>
    /// True when the world whose hub key is <paramref name="hubKey"/> is open. Unknown keys are closed on a restricted profile.
    /// </summary>
    /// <param name="hubKey">A WorldHubZones.xml key such as <c>Grizzleheim</c>.</param>
    /// <returns>True if the Spiral Door may list the world.</returns>
    public bool IsHubKeyAllowed(string hubKey) {
        if (!IsRestricted) {
            return true;
        }

        return Zones.FindWorldByHubKey(hubKey) is { } world && Profile.AllowsWorld(world.Id);
    }

    /// <summary>
    /// Decides a Spiral Door request. The teleport zone must be open, and the hub key must be one the
    /// door lists (<see cref="IsHubKeyAllowed"/>), because the client can send any key.
    /// </summary>
    /// <param name="hubKey">The requested WorldHubZones.xml key.</param>
    /// <param name="zone">That key's universe teleport zone.</param>
    /// <returns>The zone's decision, or a denial when only the hub key is closed.</returns>
    public ZoneDecision IsWorldTeleportAllowed(string hubKey, string zone) {
        var decision = IsZoneAllowed(zone);
        if (!decision.Allowed || IsHubKeyAllowed(hubKey)) {
            return decision;
        }

        return decision with {
            Allowed = false,
            Reason = $"hub key '{hubKey}' belongs to no open world of profile {Profile.Id}, so the Spiral Door does not list it",
            PlayerMessage = ClassicMessages.Unmapped,
            RuleSource = HubKeyRule,
            Confidence = null,
        };
    }

    /// <summary>
    /// Decides whether the world of <paramref name="hubKey"/> is unlocked for a wizard: the profile's
    /// world_unlocks rule for that world, if it has one. A world without a rule, a hub key no world has, and
    /// every world of an unrestricted profile are unlocked (the zone gate decides those).
    /// </summary>
    /// <param name="hubKey">A WorldHubZones.xml key such as <c>Krokotopia</c>.</param>
    /// <param name="progress">What the wizard has done.</param>
    /// <returns>The decision and the rule behind it.</returns>
    public WorldUnlockDecision IsWorldUnlocked(string hubKey, IPlayerProgress progress) {
        ArgumentNullException.ThrowIfNull(progress);
        if (!IsRestricted || Zones.FindWorldByHubKey(hubKey) is not { } world) {
            return new WorldUnlockDecision(true, null, null);
        }

        if (!Profile.WorldUnlocks.TryGetValue(world.Id, out var rule)) {
            return new WorldUnlockDecision(true, world.Id, null);
        }

        return new WorldUnlockDecision(rule.IsMet(progress), world.Id, rule);
    }

    /// <summary>
    /// The hub key a player in <paramref name="zone"/> should go to: the zone's own (or reassigned) world
    /// when it is open and has a hub, else the fallback world, else the first open world with a hub.
    /// </summary>
    /// <param name="zone">The zone name.</param>
    /// <returns>A WorldHubZones.xml key; null only when the profile is unrestricted.</returns>
    public string? HubKeyFor(string? zone) {
        if (!IsRestricted) {
            return null;
        }

        var classification = Zones.Classify(zone);
        var candidates = new[] {
            Zones.FindWorld(classification.WorldOverride?.WorldId),
            classification.Home?.World,
        };
        foreach (var world in candidates) {
            if (world is { HubKey: { } hubKey } && Profile.AllowsWorld(world.Id)) {
                return hubKey;
            }
        }

        return FallbackHubKey();
    }

    /// <summary>
    /// The hub key Go Home sends a wizard in <paramref name="zone"/> to: <see cref="HubKeyFor"/>, unless the
    /// profile's world_unlocks rule for that world is not met, in which case the fallback world's hub. A wizard
    /// can stand in a world before it unlocks (the Grizzleheim preview, GrizzleheimLite, is open to a level-5
    /// wizard on Trade Voyage), and Go Home must not carry them into the locked world's hub.
    /// </summary>
    /// <param name="zone">The zone the wizard is in.</param>
    /// <param name="progress">What the wizard has done.</param>
    /// <returns>A WorldHubZones.xml key; null only when the profile is unrestricted.</returns>
    public string? HomeHubKeyFor(string? zone, IPlayerProgress progress) {
        ArgumentNullException.ThrowIfNull(progress);
        var hubKey = HubKeyFor(zone);
        if (hubKey is null || IsWorldUnlocked(hubKey, progress).Unlocked) {
            return hubKey;
        }

        return FallbackHubKey() ?? hubKey;
    }

    public int EffectiveMaxLevel(int stockMaxLevel)
        => LevelCapRules.EffectiveMaxLevel(stockMaxLevel, Profile.LevelCap);

    public int ClampLevel(int level, int maxLevel)
        => Profile.LevelCap is null ? level : LevelCapRules.ClampLevel(level, maxLevel);

    public int? XpCeiling(int xpToLeaveMaxLevel, int xpToReachMaxLevel)
        => Profile.LevelCap is null ? null : LevelCapRules.XpCeiling(xpToLeaveMaxLevel, xpToReachMaxLevel, _xpPolicy);

    public int XpToApply(int currentXp, int requestedXp, int? xpCeiling)
        => LevelCapRules.XpToApply(currentXp, requestedXp, xpCeiling);

    public int ClampXp(int xp, int? xpCeiling)
        => LevelCapRules.ClampXp(xp, xpCeiling);

    public bool CanGainXp(int currentXp, int? xpCeiling)
        => LevelCapRules.CanGainXp(currentXp, xpCeiling);

    /// <summary>
    /// The profile's switch for <paramref name="featurePath"/>; a switch the profile does not set is enabled.
    /// </summary>
    /// <param name="featurePath">A <see cref="ClassicFeatures"/> path.</param>
    /// <returns>True if the feature is enabled.</returns>
    /// <exception cref="ArgumentException">The path is not a known feature path.</exception>
    public bool IsFeatureEnabled(string featurePath)
        => Profile.Features.IsEnabled(featurePath) || OwnerExtraFeatures().Contains(featurePath);

    /// <summary>
    /// CLASSIC: features the owner turned on beyond the profile's era ([Classic] switches such as PetPavilion), read on
    /// every check so a dashboard change applies at once. Empty by default.
    /// </summary>
    public Func<IReadOnlySet<string>> OwnerExtraFeatures { get; set; } = () => EmptyFeatures;

    private static readonly IReadOnlySet<string> EmptyFeatures = new HashSet<string>();

    private string? FallbackHubKey() {
        if (Zones.FindWorld(Zones.FallbackWorldId) is { HubKey: { } fallbackKey } fallback && Profile.AllowsWorld(fallback.Id)) {
            return fallbackKey;
        }

        var ordered = Profile.Worlds is { } listed
            ? listed.Select(Zones.FindWorld)
            : Zones.Worlds.Select(world => (WorldEntry?) world);

        return ordered.FirstOrDefault(world => world?.HubKey is not null)?.HubKey;
    }

    private void CrossValidate() {
        var file = Zones.SourceFiles.IsEmpty ? "zones/worlds.yaml" : ClassicDataLocator.DisplayPath(Zones.SourceFiles[0]);
        var errors = new List<ClassicDataError>();
        foreach (var worldId in Profile.Worlds ?? ImmutableArray<string>.Empty) {
            if (Zones.FindWorld(worldId) is null) {
                errors.Add(new ClassicDataError(file, YamlKey("worlds", worldId), null,
                    $"profile {Profile.Id} allows world '{worldId}', which the zone map does not define"));
            }
        }

        if (FallbackHubKey() is null) {
            errors.Add(new ClassicDataError(file, "fallback_world", null,
                $"profile {Profile.Id} allows no world with a hub_key, so closed zones have nowhere to send players"));
        }

        if (errors.Count > 0) {
            throw new ClassicDataException(errors);
        }
    }

    private static string YamlKey(string parent, string key) => $"{parent}.{key}";

    private static string? DateRuleCloses(DateOnly? introduced, DateOnly? introducedAfter, DateOnly cutoff) {
        if (introduced is { } date && date > cutoff) {
            return $"introduced {Format(date)}, after the cutoff {Format(cutoff)}";
        }

        if (introducedAfter is { } after) {
            return $"introduced some time after {Format(after)}, and the profile has a cutoff ({Format(cutoff)})";
        }

        return null;
    }

    private static string Format(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Describe(ZoneOverride rule)
        => $"{rule.RuleSource}: {rule.Reason} (confidence {rule.Confidence})";

    private static string Describe(ZoneHome home, AreaEntry area)
        => $"{home.RuleSource}: {area.Reason} (confidence {area.Confidence})";

    private static ZoneDecision Deny(string zone, string? worldId, string? areaId, string reason, string message,
                                     string ruleSource, string? confidence)
        => new(false, zone, worldId, null, areaId, reason, message, ruleSource, confidence);

    private static ZoneDecision Deny(ZoneClassification classification, string reason, string message,
                                     string ruleSource, string? confidence)
        => new(false, classification.Zone, classification.EffectiveWorld?.Id, classification.Home?.World?.Id,
            classification.Home?.Area?.Id, reason, message, ruleSource, confidence);

}
