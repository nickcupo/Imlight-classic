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
 * CLASSIC OPEN PVP (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * The open PvP circles in the server's zones: which zone gets which
 * circles (ZoneSigilSupervisor places them), the circles running now (for
 * the dashboard and the .pvp command), and the hook that lets another
 * server feature seat a wizard of its own in a circle.
 *
 * USAGE EXAMPLE (the ambient-wizard hook, branch claude/perf-ambient):
 * foreach (var circle in ClassicPvp.CirclesIn(zone)) { ... }
 * ClassicPvp.Seat(circle.DuelActor, ambientActor, ambientObject, ambientWizard, side: -1);
 *
 * NOTE:
 * A seated actor must behave as a player session does in a duel: answer
 * CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD with its Wizard (the seat
 * asks it, with a timeout), take MSG_ACTORADDEDTODUEL, send its moves as
 * COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE (Actor = itself) during planning,
 * and take CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE when the fight ends.
 * Its CoreObject must have template id 1 (a wizard).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

/// <summary>A PvP circle as the server knows it.</summary>
public sealed record PvpCircleState(string Zone, string Tag, ulong DuelId, IActorRef DuelActor, string Phase, int Side0,
    int Side1, int Ready);

/// <summary>
/// Open PvP: the circles' data, the circles running now and the seating hook.
/// </summary>
public static class ClassicPvp {

    private static OpenPvpConfig? s_config;
    private static readonly ConcurrentDictionary<(string Zone, string Tag), PvpCircleState> s_circles = new();

    /// <summary>The loaded circles, or null.</summary>
    public static OpenPvpConfig? Config => s_config;

    /// <summary>True while open PvP is on: the profile's pvp_arena switch and [Classic] OpenPvp.</summary>
    public static bool Enabled => s_config is not null && ClassicSettings.OpenPvp
        && (!ClassicRuntime.IsActive || ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PvpArena));

    public static void Initialize(string? classicDataRoot, string profileId) {
        if (s_config is not null || classicDataRoot is null || !Directory.Exists(Path.Combine(classicDataRoot, "pvp"))) {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(classicDataRoot, "pvp"), "open-pvp-*.yaml").Order()) {
            try {
                var config = OpenPvpLoader.Load(path);
                if (config.Profiles.Contains(profileId, StringComparer.Ordinal)) {
                    s_config = config;
                    break;
                }
            }
            catch (ClassicDataException ex) {
                Logger.Error("Classic PvP: {Path} is invalid, so there are no open PvP circles: {Error}", Logger.Args(path, ex.Message));

                return;
            }
        }

        if (s_config is not null) {
            Logger.Information("Classic PvP: {Count} open circles in {Zone} ({File}).",
                Logger.Args(s_config.Circles.Length, s_config.Zone, s_config.SourceFile));
            AdminDashboard.AddSection("PvP circles", () => s_circles.Values
                .Select(circle => new { circle.Zone, circle.Tag, circle.Phase, sides = $"{circle.Side0} v {circle.Side1}", circle.Ready })
                .ToList());
        }
    }

    /// <summary>The circles to place in <paramref name="zone"/> (none when open PvP is off).</summary>
    public static IEnumerable<CombatSigilObjectInfo> CircleInfosFor(string zone) {
        if (!Enabled || !string.Equals(zone, s_config!.Zone, StringComparison.OrdinalIgnoreCase)) {
            yield break;
        }

        foreach (var circle in s_config.Circles) {
            yield return new CombatSigilObjectInfo {
                m_templateID = s_config.Template,
                m_location = new Imcodec.Math.Vector3(circle.X, circle.Y, circle.Z),
                m_orientation = new Imcodec.Math.Vector3(0, 0, circle.Yaw),
                m_fScale = 0,
                m_zoneTag = circle.Tag,
                m_zoneTag2 = circle.Tag,
                m_sigilType = s_config.SigilType,
                m_radius = s_config.Radius,
                m_firstTeamToAct = -1,
                m_loadingType = LoadingType.DYNAMIC_SERVER,
                m_activateEvents = ["StartZone"],
            };
        }
    }

    /// <summary>
    /// True when <paramref name="zone"/> is the open PvP arena: a shared zone, never a per-player instance (2009: wizards
    /// walked into the Wizard City Arena together and took sides in its duel circle).
    /// </summary>
    public static bool IsOpenPvpZone(string? zone)
        => Enabled && zone is not null && string.Equals(zone, s_config!.Zone, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the circle tagged <paramref name="tag"/> in <paramref name="zone"/> is an open PvP circle.</summary>
    public static bool IsPvpCircle(string? zone, string? tag)
        => Enabled && zone is not null && tag is not null && string.Equals(zone, s_config!.Zone, StringComparison.OrdinalIgnoreCase)
           && s_config.Circles.Any(circle => string.Equals(circle.Tag, tag, StringComparison.Ordinal));

    /// <summary>The lobby timeout from the data.</summary>
    public static int LobbyTimeoutSeconds => s_config?.LobbyTimeoutSeconds ?? 120;

    /// <summary>Publishes a circle's state (duel components call this).</summary>
    internal static void Publish(PvpCircleState state) => s_circles[(state.Zone, state.Tag)] = state;

    internal static void Forget(string zone, string tag) => s_circles.TryRemove((zone, tag), out _);

    /// <summary>The PvP circles running in <paramref name="zone"/>.</summary>
    public static IReadOnlyList<PvpCircleState> CirclesIn(string zone)
        => [.. s_circles.Values.Where(circle => string.Equals(circle.Zone, zone, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// HOOK for the ambient wizards (claude/perf-ambient): seats <paramref name="actor"/> in the circle whose duel actor is
    /// <paramref name="duelActor"/>, on <paramref name="side"/> (0 or 1; -1 for the side with fewer wizards). The circle
    /// answers nothing; the actor gets MSG_ACTORADDEDTODUEL when it is seated. See this file's NOTE for the contract.
    /// </summary>
    public static void Seat(IActorRef duelActor, IActorRef actor, CoreObject participantObject, Wizard wizard, int side = -1)
        => duelActor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPSEATREQUEST {
            ParticipantActor = actor,
            ParticipantObject = participantObject,
            Wizard = wizard,
            Side = side,
        });

}
