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
 * AMBIENT DIRECTOR
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: starts the ambient wizards when [Classic] AmbientWizards is on.
 * It reads the AmbientWizards collection (off the actors), makes the
 * wizards each zone still needs (same zone, same seeds, so a restart
 * brings back the same people), builds their in-memory Wizards with a
 * 2009 deck, and starts one AmbientZone actor per zone. Zones: the
 * AmbientWizardZones list, or the Commons, Unicorn Way, the Shopping
 * District and every world hub the profile opens.
 *
 * USAGE EXAMPLE:
 * AmbientDirector.StartIfEnabled(Context, Self);  // GameServer
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Classic;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>Starts ambient wizards (see the file header).</summary>
internal sealed class AmbientDirector : ReceiveActor {

    private sealed record Ready(List<(string Zone, List<AmbientWizard> Wizards)> Zones);

    private readonly IActorRef _server;

    public AmbientDirector(IActorRef server) {
        _server = server;
        Receive<Ready>(ready => {
            foreach (var (zone, wizards) in ready.Zones.Where(z => z.Wizards.Count > 0)) {
                Context.ActorOf(AmbientZone.Props(zone, wizards, _server), "zone-" + zone.Replace('/', '.'));
            }

            Logger.Information("Ambient wizards: {Count} in {Zones} zones.",
                Logger.Args(ready.Zones.Sum(z => z.Wizards.Count), ready.Zones.Count(z => z.Wizards.Count > 0)));
            // CLASSIC: the bounded Practice/Ranked population starts without requiring a human to open the guard.
            Arena.ArenaAmbientParticipants.Configure(Context.System, _server);
        });
        Receive<Status.Failure>(failure => Logger.Error("Ambient wizards did not start: {Error}",
            Logger.Args(failure.Cause?.GetBaseException().ToString())));
    }

    /// <summary>Reads the settings and starts the director under <paramref name="context"/> when ambient wizards are on.</summary>
    internal static void StartIfEnabled(IUntypedActorContext context, IActorRef server) {
        var settings = AmbientSettings.Parse(
            Setting("Classic.AmbientWizards"), Setting("Classic.AmbientWizardZones"), Setting("Classic.AmbientWizardChat"),
            Setting("Classic.AmbientWizardBattles"), Setting("Classic.AmbientWizardStreetFights"),
            Setting("Classic.AmbientWizardHatching"), Setting("Classic.AmbientWizardBazaar")); // CLASSIC (2026-10-04)
        AmbientWizards.Settings = settings;
        // CLASSIC (2026-10-04): grouping for dungeons (AmbientZone.Dungeons); needs chat (a helper asks, a player may say no).
        AmbientDungeons.Settings = settings.Enabled && settings.Chat
            ? DungeonSettings.Parse(Setting("Classic.AmbientWizardDungeons"), Setting("Classic.AmbientWizardDungeonChance"),
                Setting("Classic.AmbientWizardDungeonHelpers"))
            : DungeonSettings.Off;
        // CLASSIC (2026-10-09): grouping with players (AmbientZone.Groups, AmbientCompanionGroup).
        AmbientGroups.Settings = settings.Enabled
            ? GroupSettings.Parse(Setting("Classic.AmbientWizardGroups"), Setting("Classic.AmbientWizardGroupSize"),
                Setting("Classic.AmbientWizardGroupChance"), Setting("Classic.AmbientWizardGroupWindow"),
                Setting("Classic.AmbientWizardGroupMinutes"))
            : GroupSettings.Off;
        if (!settings.Enabled) {
            Logger.Information("Ambient wizards are off ([Classic] AmbientWizards).");
            return;
        }

        var groups = AmbientGroups.Settings;
        Logger.Information(groups.Enabled
            ? "Ambient wizards group with players who ask: up to {Size} in a group, {Chance} willing, about {Minutes} minutes, client group window {Window}."
            : "Ambient wizards do not group with players ([Classic] AmbientWizardGroups).",
            Logger.Args(groups.MaxSize, groups.Willing, groups.StayMinutes, groups.Window ? "on" : "off"));

        AmbientChat.System = context.System;
        AmbientZone.ConfigureChat(Setting); // CLASSIC (2026-10-05): chat word lists, optional local LLM
        context.ActorOf(Akka.Actor.Props.Create(() => new AmbientDirector(server)), "AmbientWizards");
    }

    private static string Setting(string key) {
        try {
            return ConfigurationManager.Settings[key].AsString();
        }
        catch (Exception) {
            return "";
        }
    }

    protected override void PreStart() {
        var settings = AmbientWizards.Settings;
        Task.Run(() => new Ready(Prepare(settings))).PipeTo(Self);
    }

    /// <summary>The zones and their wizards: stored ones first, new ones made and stored for the rest.</summary>
    private static List<(string, List<AmbientWizard>)> Prepare(AmbientSettings settings) {
        var zones = settings.Zones.ToList();
        if (settings.UsesDefaultZones) {
            foreach (var world in new[] { "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Grizzleheim" }) {
                if (WorldHubZones.GetHubForZone(world)?.m_hubZone is { Length: > 0 } hub
                    && zones.All(z => !string.Equals(z.Zone, hub, StringComparison.OrdinalIgnoreCase))) {
                    zones.Add((hub, settings.PerZone));
                }
            }
        }

        zones = [.. zones.Where(z => {
            var open = ClassicGate.Decide(z.Zone).Allowed;
            if (!open) {
                Logger.Information("Ambient wizards: {Zone} is closed by the profile; none there.", Logger.Args(z.Zone));
            }

            return open && z.Count > 0;
        })];

        var stored = AmbientWizardCollection.LoadAll();
        var nextId = stored.Count == 0 ? AmbientWizardCollection.CharIdBase + 1 : stored.Max(r => r.CharId) + 1;
        var sizes = WizardNameBank.ClassicCreationNameCounts();
        var tables = new NameTableSizes(sizes.FirstBoy, sizes.FirstGirl, sizes.Middle, sizes.Last);
        Logger.Information("Ambient wizards: 2009 creation names {Boys} boy and {Girls} girl first names, {Middle} x {Last} last names.",
            Logger.Args(sizes.FirstBoy, sizes.FirstGirl, sizes.Middle, sizes.Last));
        var cap = ClassicRuntime.IsActive ? ClassicRuntime.Rules.ClampLevel(MagicLevelsConfig.MaxLevel, MagicLevelsConfig.MaxLevel)
            : Math.Max(1, MagicLevelsConfig.MaxLevel);
        var created = new List<AmbientWizardRecord>();
        var renamed = new List<AmbientWizardRecord>();
        var result = new List<(string, List<AmbientWizard>)>();
        var usedNames = new HashSet<uint>(stored.Select(r => r.NameKeys));

        foreach (var (zone, count) in zones) {
            var mine = stored.Where(r => string.Equals(r.HomeZone, zone, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.CharId).Take(count).ToList();

            // CLASSIC (2026-10-03): wizards made with later name parts get a 2009 creation-screen name; the character id
            // (and so the friends who know them) stays.
            foreach (var record in mine.Where(r => !tables.Allows(r.NameKeys, r.Female))) {
                var renameSeed = (int) (record.CharId & 0x7FFFFFFF);
                uint keys;
                do {
                    keys = AmbientIdentity.ClassicNameKeys(renameSeed++, record.Female, tables);
                } while (!usedNames.Add(keys));

                var gender = record.Female ? Imcodec.ObjectProperty.TypeCache.eGender.Female : Imcodec.ObjectProperty.TypeCache.eGender.Male;
                Logger.Information("Ambient wizard {Old} renamed {New} (2009 creation names).", Logger.Args(
                    WizardNameBank.GetEnglishName(record.NameKeys, gender), WizardNameBank.GetEnglishName(keys, gender)));
                record.NameKeys = keys;
                renamed.Add(record);
            }

            var seed = StableHash(zone) * 1000;
            while (mine.Count < count) {
                var identity = AmbientIdentity.Generate(seed++, zone, tables, AmbientIdentity.LevelsFor(zone, cap));
                if (!usedNames.Add(identity.NameKeys)) {
                    continue; // one of each name
                }

                var record = AmbientWizardRecord.From(identity, nextId++);
                mine.Add(record);
                created.Add(record);
            }

            var wizards = new List<AmbientWizard>();
            foreach (var record in mine) {
                var wizard = AmbientWizards.BuildWizard(record);
                var deck = AmbientCombat.DeckFor(wizard.MagicSchoolBehavior.MagicSchool, wizard.MagicSchoolBehavior.Level);
                wizard.SpellbookBehavior.SpellList = deck;
                wizard.SpellbookBehavior.LearnedSpellTemplateIds = [.. deck.Select(d => d.m_templateID)];
                wizard.FriendsBehavior.Relationships = WizardData.Collections.BuddyRelationshipCollection.GetRelationshipsForPlayer(record.CharId);
                wizards.Add(new AmbientWizard(record, wizard));
            }

            result.Add((zone, wizards));
        }

        if (renamed.Count > 0) {
            AmbientWizardCollection.SaveNow(renamed);
        }

        if (created.Count > 0) {
            AmbientWizardCollection.SaveNow(created);
            Logger.Information("Ambient wizards: made {Count} new wizards.", Logger.Args(created.Count));
        }

        return result;
    }

    private static int StableHash(string text) {
        var hash = 17;
        foreach (var c in text.ToLowerInvariant()) {
            hash = unchecked(hash * 31 + c);
        }

        return hash & 0xFFFFF;
    }

}
