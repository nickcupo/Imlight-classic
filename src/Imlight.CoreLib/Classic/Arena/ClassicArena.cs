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
 * CLASSIC ARENA (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the 2009 arena's switch and data (classic-data/pvp/arena-*.yaml
 * of the running profile, [Classic] ArenaMatches and the profile's
 * pvp_arena feature), the arena hall as a shared zone, and the server side
 * of the matchmaker (ServerArenaWorld: online wizards, sessions, the
 * ladder in RavenDB).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.IO;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imlight.Classic;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Arena;

/// <summary>CLASSIC: the 2009 arena's data and switch.</summary>
public static class ClassicArena {

    private static ArenaConfig? s_config;

    /// <summary>The loaded arena, or null.</summary>
    public static ArenaConfig? Config => s_config;

    /// <summary>True while the arena matches are on: the data, [Classic] ArenaMatches and the profile's pvp_arena.</summary>
    public static bool Enabled => s_config is not null && ClassicSettings.ArenaMatches
        && (!ClassicRuntime.IsActive || ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PvpArena));

    public static void Initialize(string? classicDataRoot, string profileId) {
        if (s_config is not null || classicDataRoot is null || !Directory.Exists(Path.Combine(classicDataRoot, "pvp"))) {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(classicDataRoot, "pvp"), "arena-*.yaml").Order()) {
            try {
                var config = ArenaLoader.Load(path);
                if (config.Profiles.Contains(profileId, StringComparer.Ordinal)) {
                    s_config = config;
                    break;
                }
            }
            catch (ClassicDataException ex) {
                Logger.Error("Classic arena: {Path} is invalid, so the arena guards are off: {Error}", Logger.Args(path, ex.Message));

                return;
            }
        }

        if (s_config is null) {
            return;
        }

        Logger.Information("Classic arena: guards {0} (Practice) and {1} (Ranked), {2} arenas ({3}).",
            Logger.Args(s_config.PracticeKiosk, s_config.RankedKiosk, s_config.Arenas.Length, s_config.SourceFile));
        AdminDashboard.AddSection("Arena matches", () => ArenaMatchmaker.Instance?.Snapshot()
            .Select(m => new { m.Id, kind = m.Kind.ToString(), phase = m.Phase, size = $"{m.TeamSize}v{m.TeamSize}", seats = $"{m.Side0} v {m.Side1}" })
            .ToList<object>() ?? []);
    }

    /// <summary>For tests: use this data.</summary>
    internal static void UseForTests(ArenaConfig? config) => s_config = config;

    /// <summary>The matchmaker, started on first use while the arena is on.</summary>
    internal static ArenaMatchmaker? Matchmaker(ActorSystem system) {
        if (!Enabled) {
            return null;
        }

        if (ArenaMatchmaker.Instance is { } running) {
            return running;
        }

        var made = ArenaMatchmaker.Start(s_config!, new ServerArenaWorld(system));
        StartClock(made);

        return made;
    }

    private static System.Threading.Timer? s_clock;

    private static void StartClock(ArenaMatchmaker matchmaker) {
        if (s_clock is not null) {
            return;
        }

        s_clock = new System.Threading.Timer(_ => {
            try {
                matchmaker.Tick(DateTime.UtcNow);
            }
            catch (Exception ex) {
                Logger.Error("Arena clock: {0}", Logger.Args(ex.Message));
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// True when <paramref name="zone"/> is the arena hall (the guards' zone): one shared zone, never a per-player copy,
    /// so wizards meet at the guards (the 2014 Wizard City Arena's hard limit, 12, would make it an instance).
    /// </summary>
    public static bool IsHall(string? zone)
        => Enabled && zone is not null && string.Equals(zone, s_config!.HallZone, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tag of the duel circle the server places in each arena.</summary>
    public const string CircleTag = "Arena Circle";

    /// <summary>The arena circle to place in <paramref name="zone"/> (none when it is not an arena or the arena is off).</summary>
    public static System.Collections.Generic.IEnumerable<Imcodec.ObjectProperty.TypeCache.CombatSigilObjectInfo> CircleInfosFor(string? zone) {
        if (!Enabled || zone is null) {
            yield break;
        }

        foreach (var arena in s_config!.Arenas.Where(a => string.Equals(a.Zone, zone, StringComparison.OrdinalIgnoreCase))) {
            yield return new Imcodec.ObjectProperty.TypeCache.CombatSigilObjectInfo {
                m_templateID = s_config.CircleTemplate,
                m_location = new Imcodec.Math.Vector3(arena.X, arena.Y, arena.Z),
                m_orientation = new Imcodec.Math.Vector3(0, 0, arena.Yaw),
                m_fScale = 0,
                m_zoneTag = CircleTag,
                m_zoneTag2 = CircleTag,
                m_sigilType = s_config.CircleSigilType,
                m_radius = s_config.CircleRadius,
                m_firstTeamToAct = -1,
                m_loadingType = Imcodec.ObjectProperty.TypeCache.LoadingType.DYNAMIC_SERVER,
                m_activateEvents = ["StartZone"],
            };
        }
    }

    /// <summary>True when <paramref name="zone"/> is one of the arenas matches go to.</summary>
    public static bool IsArenaZone(string? zone)
        => Enabled && zone is not null && s_config!.Arenas.Any(a => string.Equals(a.Zone, zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the circle tagged <paramref name="tag"/> in <paramref name="zone"/> is an arena's match circle.</summary>
    public static bool IsArenaCircle(string? zone, string? tag)
        => Enabled && zone is not null && string.Equals(tag, CircleTag, StringComparison.Ordinal)
           && s_config!.Arenas.Any(a => string.Equals(a.Zone, zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>The guard kind of a template, or null when it is not a guard.</summary>
    public static ArenaKind? KioskKind(uint templateId)
        => s_config is null ? null
            : templateId == s_config.PracticeKiosk ? ArenaKind.Practice
            : templateId == s_config.RankedKiosk ? ArenaKind.Ranked : null;

}

/// <summary>The live server behind the matchmaker.</summary>
internal sealed class ServerArenaWorld(ActorSystem system) : IArenaWorld {

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Wizard> s_live = new();

    /// <summary>ArenaService keeps the live wizard of each player who used the arena.</summary>
    public static void Register(Wizard wizard) => s_live[wizard.CharId] = wizard;

    public static void Forget(ulong charId) => s_live.TryRemove(charId, out _);

    public IArenaLadderStore Ladder { get; } = new ArenaLadderCollection.Raven();

    public ArenaPlayer? Player(ulong charId) {
        if (OnlinePlayerCollection.GetOnlinePlayer(charId) is null || !s_live.TryGetValue(charId, out var wizard)) {
            return null;
        }

        return PlayerOf(wizard);
    }

    /// <summary>The arena's view of a wizard.</summary>
    public static ArenaPlayer PlayerOf(Wizard wizard) {
        var name = wizard.PlayerNameBehavior;
        var school = wizard.MagicSchoolBehavior?.MagicSchool.ToString() ?? "";

        return new ArenaPlayer(wizard.CharId, wizard.GameObjectID,
            DataManipulation.SpacedHexStringToBytes(name.GetWizardNameAsByteHexString()), name.GetWizardName(),
            wizard.MagicSchoolBehavior?.Level ?? 1, school, (short) (name.Gender == Imcodec.ObjectProperty.TypeCache.eGender.Female ? 1 : 0));
    }

    public bool AreFriends(ulong charId, ulong otherCharId)
        => BuddyRelationshipCollection.GetRelationshipsForPlayer(charId).Any(r =>
            (r.FirstPlayerId == otherCharId || r.SecondPlayerId == otherCharId) && !r.Blocked && !r.IsBrokenUp);

    public void Send(ulong charId, IMessage message) => Tell(charId, message);

    public void Inform(ulong charId, string text) => Tell(charId, ClassicChat.Line(text));

    public void Travel(ulong charId, string zone, string location, ulong runId)
        => Tell(charId, new CLASSIC_FEATURES_PROTOCOL.MSG_ARENATRAVEL { Zone = zone, Location = location, RunId = runId });

    public void Deliver(ulong charId, ArenaOutcome outcome) {
        if (OnlinePlayerCollection.GetOnlinePlayer(charId)?.ActorPath is { Length: > 0 }) {
            Tell(charId, new CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME { Outcome = outcome });

            return;
        }

        // Offline (dropped out of the fight): the tickets go straight into the saved game stats.
        if (outcome.Tickets != 0) {
            WizardCollection.CommitCharacterMutation(charId, (_, persisted) => {
                persisted.GameStats.m_currentArenaPoints += outcome.Tickets;
                persisted.GameStats.m_currentPvPCurrency = persisted.GameStats.m_currentArenaPoints;

                return true;
            }, null);
        }
    }

    public ulong NewRunId() => GroupInstances.NewRunId(DateTime.UtcNow);

    public string? ZoneOf(ulong charId) => OnlinePlayerCollection.GetOnlinePlayer(charId)?.CurrentZone;

    private void Tell(ulong charId, object message) {
        if (OnlinePlayerCollection.GetOnlinePlayer(charId)?.ActorPath is { Length: > 0 } path) {
            // No sender: a session hands a server message from one of its own services to the OTHER services only, and
            // the matchmaker often runs on the very wizard's ArenaService (their Go to Arena started the trip).
            system.ActorSelection(path).Tell(message, ActorRefs.Nobody);
        }
    }

}
