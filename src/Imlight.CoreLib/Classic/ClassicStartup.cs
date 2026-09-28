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
 * CLASSIC RULES STARTUP
 * ========================================================================
 * 
 * PURPOSE:
 * Loads the [Classic] profile, zone map, spell values, XP table and mob
 * reward rules at boot, before
 * any resource or server reads them, and checks them against the client's
 * resources once those have loaded.
 * 
 * USAGE EXAMPLE:
 * if (!ClassicStartup.Initialize()) { Environment.ExitCode = 1; return; }   // after the ini loads
 * ClassicStartup.ValidateAfterResources();                                  // after ResourceContainer
 * 
 * NOTE:
 * No [Classic] section, or an empty Profile, runs stock Imlight without
 * reading classic-data. A configured profile that fails to load stops the
 * boot, as does a configured SpellsPath that does not exist (the default
 * path only warns). The zone census logs counts and unmapped first
 * segments only; zone names come from the client's WAD, so never commit
 * the log. Spell values load only for a restricted profile; the spell
 * census names classic record ids, never client template paths.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Classic;
using Imlight.Classic.Audit;
using Imlight.Classic.Zones;
using Imlight.Common;
using Imlight.CoreLib.Game.World;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Boot-time loading and validation of the classic rules.
/// </summary>
public static class ClassicStartup {

    // The same literal as Wizard.TutorialStartingZone, which is private.
    private const string TutorialStartingZone = "WizardCity/Tutorial_Exterior";

    private static string? s_classicDataRoot;

    /// <summary>
    /// Loads the configured profile and zone map into <see cref="ClassicRuntime"/>.
    /// </summary>
    /// <returns>False when a configured profile fails to load; the server must not start.</returns>
    public static bool Initialize() {
        var profileId = ConfigurationManager.Settings["Classic.Profile"].AsString().Trim();
        if (profileId.Length == 0) {
            Logger.Warning("No [Classic] Profile is configured; running stock Imlight rules without classic-data.");
            ClassicRuntime.Initialize(ClassicRules.Stock);

            return true;
        }

        try {
            var baseDirectory = AppContext.BaseDirectory;
            var profilesPath = ClassicDataLocator.ResolveProfilesPath(
                ConfigurationManager.Settings["Classic.ProfilesPath"].AsString(), baseDirectory);
            var zoneWorldsPath = ClassicDataLocator.ResolveZoneWorldsPath(
                ConfigurationManager.Settings["Classic.ZoneWorldsPath"].AsString(), profilesPath, baseDirectory);
            var configuredSpellsPath = ConfigurationManager.Settings["Classic.SpellsPath"].AsString();
            var spellsPath = ClassicDataLocator.ResolveSpellsPath(configuredSpellsPath, profilesPath, baseDirectory);
            var auditVerbose = ConfigurationManager.Settings["Classic.AuditVerbose"].AsBool();
            Logger.Information("Classic rules: profiles {ProfilesPath}, zone map {ZoneWorldsPath}.",
                Logger.Args(profilesPath, zoneWorldsPath));

            var profile = ClassicProfileLoader.Load(profilesPath, profileId);

            // An unrestricted profile never consults the zone map, so a missing or broken map must not
            // stop the profile meant to tell an Imlight bug from a Classic-layer one.
            var zones = profile.IsUnrestricted ? ZoneWorldMap.Empty : ZoneWorldMapLoader.Load(zoneWorldsPath);
            var rules = new ClassicRules(profile, zones);
            var classicDataRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profilesPath));
            if (rules.IsRestricted) {
                var accuracyTablePath = profile.Rules.AccuracyTable is { } table && classicDataRoot is not null
                    ? Path.Combine(classicDataRoot, table)
                    : null;
                ClassicSpellTemplates.Initialize(profile, spellsPath, !string.IsNullOrWhiteSpace(configuredSpellsPath), accuracyTablePath);
                if (classicDataRoot is not null) {
                    ClassicProgression.Initialize(profile, classicDataRoot);
                }
            }

            ClassicRuntime.Initialize(rules, new LoggerAuditSink(), auditVerbose);
            s_classicDataRoot = classicDataRoot;

            Logger.Information("Classic profile chain: {Chain}.", Logger.Args(string.Join(" -> ", profile.SourceFiles)));
            if (profile.IsUnrestricted) {
                Logger.Information("Classic zone map not loaded: profile {Profile} is unrestricted.", Logger.Args(profile.Id));
            }
            else {
                Logger.Information("Classic zone map: {Worlds} worlds, {Areas} areas, {Overrides} overrides from {Files}.",
                    Logger.Args(zones.Worlds.Length, zones.Areas.Length, zones.Overrides.Length, string.Join(", ", zones.SourceFiles)));
            }
            ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.Startup, null, profile.Id, profile.Describe()));
            if (rules.IsRestricted && !profile.WorldUnlocks.IsEmpty) {
                Logger.Information("Classic world unlocks at the Spiral Door: {Unlocks}.", Logger.Args(string.Join("; ",
                    profile.WorldUnlocks.Values.OrderBy(unlock => unlock.WorldId, StringComparer.Ordinal)
                        .Select(unlock => $"{unlock.WorldId}: {unlock.Describe()}"))));
            }

            if (!rules.IsRestricted) {
                Logger.Warning("Classic profile {Profile} restricts nothing; the server runs stock Imlight rules.",
                    Logger.Args(profile.Id));
            }

            return true;
        }
        catch (Exception ex) {
            Logger.Fatal("Classic profile {Profile} failed to load, so the server will not start: {Error}",
                Logger.Args(profileId, ex.Message));

            return false;
        }
    }

    /// <summary>
    /// Checks the active profile against the loaded client resources. Only warns; read the startup log.
    /// </summary>
    public static void ValidateAfterResources() {
        if (!ClassicRuntime.IsActive) {
            return;
        }

        try {
            var rules = ClassicRuntime.Rules;
            CheckHubs(rules);
            CheckStartingZones(rules);
            LogClassicStart(rules);
            CheckRuleTables(rules);
            LogZoneCensus(rules);
            ClassicSpellTemplates.LogCensus();
        }
        catch (Exception ex) {
            Logger.Error("Classic startup checks failed: {Error}", Logger.Args(ex.Message));
        }
    }

    private static void CheckHubs(ClassicRules rules) {
        foreach (var world in rules.Zones.Worlds) {
            if (world.HubKey is not { } hubKey || !rules.IsHubKeyAllowed(hubKey)) {
                continue;
            }

            var hub = WorldHubZones.GetHubForZone(hubKey);
            if (hub is null) {
                Logger.Warning("Classic hub key {HubKey} (world {World}) is not in WorldHubZones.xml; Go Home and fallbacks to it will fail.",
                    Logger.Args(hubKey, world.Id));
                continue;
            }

            foreach (var zone in new[] { hub.m_hubZone, hub.m_universeTPZone }) {
                if (string.IsNullOrEmpty(zone)) {
                    continue;
                }

                var decision = rules.IsZoneAllowed(zone);
                if (!decision.Allowed) {
                    Logger.Warning("Classic hub zone {Zone} of open world {World} is closed: {Reason}",
                        Logger.Args(zone, world.Id, decision.Reason));
                }
            }
        }
    }

    private static void CheckStartingZones(ClassicRules rules) {
        var zones = new List<string>();
        var startingZone = rules.UsesClassicStart
            ? ClassicStart.StartingZone
            : ConfigurationManager.Settings["Character.StartingZone"].AsString();
        if (!ConfigurationManager.Settings["Character.TutorialDisabled"].AsBool()) {
            zones.Add(TutorialStartingZone);
        }

        if (startingZone.Length > 0) {
            zones.Add(startingZone);
        }

        foreach (var zone in zones) {
            var decision = rules.IsZoneAllowed(zone);
            if (!decision.Allowed) {
                Logger.Error("Classic profile {Profile} closes {Zone}, where new characters start; character select will move them. {Reason}",
                    Logger.Args(rules.Profile.Id, zone, decision.Reason));
            }
        }
    }

    private static void LogClassicStart(ClassicRules rules) {
        if (!rules.UsesClassicStart) {
            Logger.Information("Classic profile {Profile} does not set rules.tutorial: {Tutorial}; new characters get stock Imlight's start.",
                Logger.Args(rules.Profile.Id, ClassicSchema.ClassicTutorial));

            return;
        }

        Logger.Information("Classic start: new characters leave the tutorial for {Zone} with their school's starter wand ({Wands}), "
            + "deck {Deck} and no pet; Character.StartingZone ({StartingZone}) and Character.DefaultItems are not used.",
            Logger.Args(ClassicStart.StartingZone,
                string.Join(",", ClassicStart.StarterWandTemplateIds.OrderBy(pair => pair.Key.ToString()).Select(pair => $"{pair.Key}={pair.Value}")),
                ClassicStart.StarterDeckTemplateId,
                ConfigurationManager.Settings["Character.StartingZone"].AsString()));
    }

    private static void CheckRuleTables(ClassicRules rules) {
        if (s_classicDataRoot is null) {
            return;
        }

        var tables = new[] {
            ("rules.xp_table", rules.Profile.Rules.XpTable),
            ("rules.accuracy_table", rules.Profile.Rules.AccuracyTable),
            ("rules.mob_rewards", rules.Profile.Rules.MobRewards),
            ("rules.badges", rules.Profile.Rules.Badges), // CLASSIC
            ("rules.quest_cards", rules.Profile.Rules.QuestCards), // CLASSIC
            ("rules.treasure_prices", rules.Profile.Rules.TreasurePrices), // CLASSIC
            ("rules.mob_stats", rules.Profile.Rules.MobStats), // CLASSIC
            ("rules.crown_shop", rules.Profile.Rules.CrownShop), // CLASSIC
        };
        foreach (var (key, relativePath) in tables) {
            if (relativePath is null || File.Exists(Path.Combine(s_classicDataRoot, relativePath))) {
                continue;
            }

            Logger.Warning("Classic profile {Profile}: {Key} names classic-data/{Path}, which does not exist yet; the client's values stay in use.",
                Logger.Args(rules.Profile.Id, key, relativePath));
        }
    }

    private static void LogZoneCensus(ClassicRules rules) {
        var zones = AccessPassManager.AllZones;
        if (zones is null) {
            Logger.Warning("Classic zone census skipped: AccessPass.xml did not load.");

            return;
        }

        var open = 0;
        var closed = 0;
        var unmapped = 0;
        var unmappedRoots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in zones) {
            var decision = rules.IsZoneAllowed(zone);
            if (decision.Allowed) {
                open++;
            }
            else if (decision.RuleSource == ClassicRules.UnmappedRule) {
                unmapped++;
                unmappedRoots.Add(ZonePattern.SplitZone(zone).FirstOrDefault() ?? zone);
            }
            else {
                closed++;
            }
        }

        Logger.Information("Classic zone census over {Total} client zones: {Open} open, {Closed} closed, {Unmapped} unmapped.",
            Logger.Args(zones.Count, open, closed, unmapped));
        if (unmappedRoots.Count > 0) {
            Logger.Warning("Classic zones no zone-map prefix covers start with: {Roots}",
                Logger.Args(string.Join(", ", unmappedRoots)));
        }
    }

}
