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
 * VOLUME COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spatial trigger volumes in the game world, tracking player 
 * proximity and triggering enter/exit events for specific areas.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Uses actor-based messaging for volume event triggering.
 * Supports dynamic player tracking within defined spatial volumes.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class VolumeComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory {

    // CLASSIC: keyed by the object instance. CoreObject is a record whose hash follows its location, so the default
    // comparer missed the entry after any move: enter events re-fired on every move and exit never fired.
    private readonly Dictionary<CoreObject, IActorRef> _playersInRange = new(ReferenceEqualityComparer.Instance);
    private readonly List<(string QuestName, string GoalName)> _volumeGoals = [];
    private Volume _volume;

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        => template is GameObjectTemplate goT && goT.m_templateID == 1700;

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // If the player spawned within the volume, add them to the list of players in range but
        // do not send any events.
        if (_volume != null && InVolume(playerObj) && !_playersInRange.ContainsKey(playerObj)) {
            _playersInRange.Add(playerObj, playerActor);
            if (ClassicQuestEngine.IsActive) {
                _arrivedInside.Add(playerObj); // CLASSIC: its enter events post on the wizard's first move (OnPlayerMove).
            }

            // A player can log in standing inside a quest-proximity volume.
            NotifyProximityGoals(playerObj, playerActor, playerWizard);
        }
    }

    // CLASSIC: forget a player who leaves the zone, so the same object coming back in range counts as an enter again.
    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        foreach (var key in _playersInRange.Where(x => x.Value.Equals(playerActor)).Select(x => x.Key).ToList()) {
            _playersInRange.Remove(key);
            _arrivedInside.Remove(key);
        }
    }

    // CLASSIC: wizards who arrived inside this volume and have not moved yet. Their first move inside posts the enter
    // events once (ArrivedInside, so no teleport fires): the Jade Palace's Jade Oni volume is the arrival spot itself,
    // and Air Apparent's Sesshu volume (MS_Plague2_T5) holds the landing. Stock Imlight never posted them.
    private readonly HashSet<CoreObject> _arrivedInside = new(ReferenceEqualityComparer.Instance);

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_volume == null) {
            return;
        }

        if (_arrivedInside.Remove(playerObj) && IsInRadius(playerObj, _volume.m_radius)) {
            PostEnterEvents(playerObj, playerActor, arrivedInside: true); // CLASSIC
        }

        // Check if the player is now in range of the object.
        if (InVolume(playerObj) && !_playersInRange.ContainsKey(playerObj)) {
            // If the player is in range, trigger the enter events.
            OnProximityEnter(playerObj, playerActor, playerWizard);
            _playersInRange.Add(playerObj, playerActor);
        } else if (!InVolume(playerObj) && _playersInRange.ContainsKey(playerObj)) {
            // If the player is out of range, trigger the exit events.
            OnProximityExit(playerObj, playerActor);
            _playersInRange.Remove(playerObj);
        }
    }

    // CLASSIC: box volumes (m_primitiveType "Box", radius 0) never fired: only the radius was tested. KingsIsle's box is
    // centred on the volume's position: width along its local X, length along local Y, height (the float in unknown_int)
    // along Z, turned by the volume's yaw (VolumeBounds).
    private bool InVolume(CoreObject playerObj)
        => VolumeBounds.Contains(_volume, Entity.ActiveGameObject.m_location.X, Entity.ActiveGameObject.m_location.Y,
            Entity.ActiveGameObject.m_location.Z, playerObj.m_location.X, playerObj.m_location.Y, playerObj.m_location.Z);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_VOLUMEDETAILS))]
    private void ReceiveVolumeDetails(ZONE_102_PROTOCOL.MSG_VOLUMEDETAILS message) {
        _volume = message.Volume;

        // Track every quest goal that references this volume by its proximity tag, so we
        // only ever bother a player's quest service for goals this volume actually activates.
        string volumeName = _volume.m_volumeName;
        if (string.IsNullOrEmpty(volumeName)) {
            return;
        }

        var zonePath = Entity.Zone?.ZonePath;
        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            foreach (var goal in quest.m_goals) {
                if (goal is not WaypointGoalTemplate waypointGoal
                    || string.IsNullOrEmpty(waypointGoal.m_proximityTag)
                    || waypointGoal.m_proximityTag != volumeName) {
                    continue;
                }

                // Only track goals that target this zone (when one is specified).
                if (!string.IsNullOrEmpty(waypointGoal.m_zoneTag)
                    && waypointGoal.m_zoneTag != zonePath) {
                    continue;
                }

                _volumeGoals.Add((quest.m_questName, goal.m_goalName));
            }
        }
    }

    private void OnProximityEnter(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // CLASSIC: a trace of every volume enter, to tell a volume that stopped posting from a trigger that stopped
        // deciding (the Commons gates going dead in live play).
        Logger.Debug("Volume {Volume} in {Zone} posts {Events} for {Player}.",
            Logger.Args(_volume.m_volumeName, Entity.Zone?.ZonePath, string.Join(", ", _volume.m_enterEvents ?? []),
                playerActor?.Path.Name));

        PostEnterEvents(playerObj, playerActor, arrivedInside: false);

        // Quest proximity goals are tied to the volume by name; if the player has one of
        // this volume's goals active, tell their quest service to complete it.
        NotifyProximityGoals(playerObj, playerActor, playerWizard);
    }

    private void PostEnterEvents(CoreObject playerObj, IActorRef playerActor, bool arrivedInside) {
        foreach (var enterEvent in _volume.m_enterEvents ?? []) {
            Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = enterEvent,
                PlayerActor = playerActor,
                PlayerGameObject = playerObj,
                ArrivedInside = arrivedInside, // CLASSIC
            });
        }
    }

    private void NotifyProximityGoals(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_volumeGoals.Count == 0 || playerWizard is null) {
            return;
        }

        foreach (var (questName, goalName) in _volumeGoals) {
            var qInstance = playerWizard.QuestBehavior.CurrentQuestInstances
                .FirstOrDefault(q => q.QuestName == questName);
            if (qInstance is null || !qInstance.IsGoalActive(goalName)) {
                continue;
            }

            var gInstance = qInstance.GoalProgress.FirstOrDefault(g => g.GoalName == goalName);
            if (gInstance is null) {
                continue;
            }

            playerActor.Tell(new ZONE_102_PROTOCOL.MSG_COMPLETEPROXIMITYGOAL {
                QuestID = qInstance.ID,
                GoalID = gInstance.ID,
            });
        }
    }

    private void OnProximityExit(CoreObject playerObj, IActorRef playerActor) {
        foreach (var exitEvent in _volume.m_exitEvents) {
            var postEventMsg = new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = exitEvent,
                PlayerActor = playerActor,
                PlayerGameObject = playerObj
            };

            Entity.ZoneRef.Tell(postEventMsg);
        }
    }

}

/// <summary>CLASSIC: whether a point is inside a client volume (sphere by radius, or a box turned by its yaw).</summary>
/// <remarks>
/// A box is centred on the volume's position: width along its local X, length along local Y, height (unknown_int read
/// as a float) along Z. Its local frame is the world turned by the yaw in m_orientation.Z the way Gamebryo turns
/// objects (clockwise seen from above), so the player's offset is turned by +yaw into it. Checked against the
/// walkable floors in collision.bcd of the 412 boxes with a real yaw (door-volumes report): the turned box covers the
/// room it was drawn in (38 of 40 clear cases near 90 degrees), and +yaw beats -yaw 74 to 9. Pitch and roll are
/// about 1e-8 on every box, and m_fScale is 1 or unset (0), so neither is applied.
/// </remarks>
internal static class VolumeBounds {

    public static bool Contains(Volume volume, float cx, float cy, float cz, float px, float py, float pz) {
        if (string.Equals(volume.m_primitiveType.ToString(), "Box", System.StringComparison.OrdinalIgnoreCase)
            && volume.m_radius <= 0f && volume.m_length > 0f && volume.m_width > 0f) {
            var height = System.BitConverter.Int32BitsToSingle(volume.unknown_int);
            var zOk = !(height > 0f && height < 1e6f) || System.Math.Abs(pz - cz) <= height / 2f;
            if (!zOk) {
                return false;
            }

            var (localX, localY) = ToLocal(volume.m_orientation.Z, px - cx, py - cy);

            return System.Math.Abs(localX) <= volume.m_width / 2f && System.Math.Abs(localY) <= volume.m_length / 2f;
        }

        var dx = px - cx;
        var dy = py - cy;
        var dz = pz - cz;

        return dx * dx + dy * dy + dz * dz <= volume.m_radius * volume.m_radius;
    }

    /// <summary>A world offset in the box's own frame: turned by +yaw (the box's frame is the world turned by -yaw).</summary>
    internal static (float X, float Y) ToLocal(float yaw, float dx, float dy) {
        if (yaw == 0f || !float.IsFinite(yaw)) {
            return (dx, dy);
        }

        var cos = System.MathF.Cos(yaw);
        var sin = System.MathF.Sin(yaw);

        return (cos * dx - sin * dy, sin * dx + cos * dy);
    }

}
