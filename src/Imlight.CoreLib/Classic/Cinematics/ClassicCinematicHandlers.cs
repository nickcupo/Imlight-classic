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
 * CLASSIC CINEMATICS
 * ========================================================================
 *
 * PURPOSE:
 * Sends the client what it needs to play a zone trigger's cinematic, and
 * runs the zone timer that waits for a staged cinematic, so a cinematic
 * never holds up the results, events and spawns that follow it.
 *
 * USAGE EXAMPLE:
 * Registered like every BaseResultHandler; a trigger's ResultExecutorActor
 * picks them when the classic zone registry decoded the result.
 *
 * NOTE:
 * ResPlayCinematic and ResCinematic become GAME MSG_PLAYCINEMATIC: the
 * cinematic's template id (ObjectData/.../<name>.xml, a
 * WizCinematicActorTemplate or CinematicDefTemplate), the player as the
 * target, start and end at the player or at the data's locations, the
 * template's .nif as the asset, and m_bDisableInteraction.
 * ResStartStagedCinematic becomes MSG_STARTSTAGEDCINEMATIC with the
 * cinematic's own name and first stage; its stages place their actors and
 * cameras at absolute zone positions, so the start offset is zero. A
 * routing of ROUTING_ZONE or ROUTING_PROXIMITY, or
 * m_bIncludeAllPlayersInZone, sends to every player in the zone. Nothing
 * waits for the client: "blocking" cinematics block the client's input,
 * not the server's result list.
 *
 * TODO:
 * - The official client's use of MSG_PLAYCINEMATIC's Asset field is inferred.
 * - m_unique (one playing at a time per name) is not enforced.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Action = System.Action;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Cinematics;
using Imlight.Common;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Cinematics;

/// <summary>
/// Shared state and sends for the classic cinematic handlers.
/// </summary>
internal static class ClassicCinematics {

    private static readonly ConcurrentDictionary<string, uint> s_templateIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The zone timers that wait for staged cinematics.
    /// </summary>
    internal static CinematicTimers Timers { get; } = new(Schedule);

    private static IDisposable Schedule(TimeSpan delay, Action action)
        => new Timer(_ => {
            try {
                action();
            }
            catch (Exception ex) {
                Logger.Error("Classic cinematic timer failed: {0}", Logger.Args(ex.Message));
            }
        }, null, delay, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// The zone key the timers use for a zone actor.
    /// </summary>
    internal static string ZoneKey(IActorRef zoneActor) => zoneActor?.Path.ToString();

    /// <summary>
    /// The template id of a cinematic object template, by its name; 0 if the client has none.
    /// </summary>
    internal static uint TemplateIdOf(string cinematicName) {
        if (string.IsNullOrEmpty(cinematicName)) {
            return 0;
        }

        return s_templateIds.GetOrAdd(cinematicName, name => {
            var id = CoreObjectFactory.GetCoreTemplateID(location => {
                var file = location.m_filename.ToString();
                return file.StartsWith("ObjectData/", StringComparison.Ordinal)
                       || file.Contains("|ObjectData/", StringComparison.Ordinal)
                    ? string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.Ordinal)
                    : false;
            });
            if (id == 0) {
                Logger.Warning("Classic cinematic {0} has no client template; it is not played.", Logger.Args(name));
            }

            return id;
        });
    }

    /// <summary>
    /// Sends a message to the player, or to every player in the zone.
    /// </summary>
    internal static void Send(IResultContext context, IMessage message, bool zoneWide) {
        var zoneActor = context.GetZoneActor();
        if (zoneWide && zoneActor is not null) {
            zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Message = message,
                Targets = ZoneBroadcastTarget.Players,
            });

            return;
        }

        context.GetPlayerRef()?.Tell(message);
    }

    /// <summary>
    /// Builds MSG_PLAYCINEMATIC for a trigger's cinematic.
    /// </summary>
    internal static GAME_5_PROTOCOL.MSG_PLAYCINEMATIC PlayMessage(CinematicPlayback playback, CoreObject player,
                                                                  uint templateId) {
        var at = player?.m_location ?? new Vector3(0, 0, 0);
        var start = playback.StartAtActor || IsZero(playback.StartLoc) ? at : playback.StartLoc;
        var end = playback.EndAtActor || IsZero(playback.EndLoc) ? at : playback.EndLoc;
        var asset = CoreObjectFactory.GetCoreTemplate(templateId) is CinematicDefTemplate definition
            ? definition.m_nifFile?.ToString() ?? ""
            : "";

        return new GAME_5_PROTOCOL.MSG_PLAYCINEMATIC {
            CinematicID = templateId,
            TargetID = player is null ? 0UL : (ulong) player.m_globalID,
            Start_X = start.X, Start_Y = start.Y, Start_Z = start.Z,
            End_X = end.X, End_Y = end.Y, End_Z = end.Z,
            Asset = asset,
            DisableInteraction = (byte) (playback.DisableInteraction ? 1 : 0),
        };
    }

    internal static bool IsZoneWide(ZoneRouter router)
        => router is not null && router.m_routingType is ZoneRouter.RoutingType.ROUTING_ZONE
                                                     or ZoneRouter.RoutingType.ROUTING_PROXIMITY;

    private static bool IsZero(Vector3 v) => v.X == 0 && v.Y == 0 && v.Z == 0;

    internal static bool Play(IResultContext context, CinematicPlayback playback) {
        var templateId = TemplateIdOf(playback.CinematicName);
        if (templateId == 0) {
            return true; // CLASSIC: a missing cinematic never stops the rest of the trigger.
        }

        var message = PlayMessage(playback, context.GetPlayerObj(), templateId);
        Send(context, message, IsZoneWide(playback.Router));
        Logger.Debug("Classic cinematic {0} ({1}) played.", Logger.Args(playback.CinematicName, templateId));

        return true;
    }

}

/// <summary>
/// Plays ResPlayCinematic.
/// </summary>
internal sealed class ClassicResPlayCinematicHandler : BaseResultHandler<ClassicResPlayCinematic> {

    public override bool Execute(IResultContext context)
        => Result is null || ClassicCinematics.Play(context, Result.Playback);

}

/// <summary>
/// Plays ResCinematic.
/// </summary>
internal sealed class ClassicResCinematicHandler : BaseResultHandler<ClassicResCinematic> {

    public override bool Execute(IResultContext context)
        => Result is null || ClassicCinematics.Play(context, Result.Playback);

}

/// <summary>
/// Starts ResStartStagedCinematic.
/// </summary>
internal sealed class ClassicResStartStagedCinematicHandler : BaseResultHandler<ClassicResStartStagedCinematic> {

    public override bool Execute(IResultContext context) {
        if (Result is null || string.IsNullOrEmpty(Result.CinematicName)) {
            return true;
        }

        var message = new GAME_5_PROTOCOL.MSG_STARTSTAGEDCINEMATIC {
            CinematicName = Result.CinematicName,
            InitialStageName = Result.StageName ?? "",
        };
        ClassicCinematics.Send(context, message, Result.IncludeAllPlayersInZone);
        Logger.Debug("Classic staged cinematic {0} started at stage {1}.",
            Logger.Args(Result.CinematicName, Result.StageName));

        return true;
    }

}

/// <summary>
/// Starts the zone timer that waits for a staged cinematic, and posts End&lt;name&gt; when it ends.
/// </summary>
internal sealed class ClassicResZoneTimerHandler : BaseResultHandler<ClassicResZoneTimer> {

    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        if (Result is null || string.IsNullOrEmpty(Result.TimerName) || zoneActor is null) {
            return true;
        }

        var playerRef = context.GetPlayerRef();
        var playerObj = context.GetPlayerObj();
        var endEvent = CinematicTimers.EndEventOf(Result.TimerName);
        ClassicCinematics.Timers.Start(ClassicCinematics.ZoneKey(zoneActor), Result.TimerName, Result.Condition,
            TimeSpan.FromSeconds(Math.Max(0, Result.LimitSeconds)), () => {
                Logger.Debug("Classic zone timer {0} ended; posting {1}.", Logger.Args(Result.TimerName, endEvent));
                zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                    EventName = endEvent,
                    PlayerActor = playerRef,
                    PlayerGameObject = playerObj,
                });
            });

        return true;
    }

}
