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
 * Reads the zone-trigger results that play cinematics, which the server
 * type registry decodes empty or with guessed fields: ResPlayCinematic,
 * ResCinematic, ResStartStagedCinematic, and the zone timer that waits for
 * a staged cinematic (type hash 1038905797, KingsIsle name unknown).
 *
 * USAGE EXAMPLE:
 * serializer.TypeRegistry = ClassicZoneTypeRegistry.Instance;   // ZoneLoader, classic quest engine only
 * (the registry lives in Classic/ClassicZoneTypes.cs with the Monster_Killed requirement)
 *
 * NOTE:
 * Property hashes were matched against r806919's 2,248 triggers.xml files
 * (57 ResPlayCinematic, 25 ResCinematic, 3 ResStartStagedCinematic, 2 zone
 * timers). ResPlayCinematic and ResCinematic carry the same fields:
 * m_cinematicName, m_router, m_blocking, m_startAtActor,
 * m_startAtTargetActor, m_startLoc, m_endAtActor, m_endAtTargetActor,
 * m_endLoc, m_bDisableInteraction, m_objectTemplateID, m_unique,
 * m_uniqueName and m_uniqueBusyMsg. The generated ResPlayCinematic reads
 * all but m_cinematicName at the wrong types. The timer's m_timerName and
 * m_timerTitle are known by name; its other fields by what they hold.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using Imcodec.IO;
using Imcodec.Math;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Cinematics;

namespace Imlight.CoreLib.Classic.Cinematics;

/// <summary>
/// Reads a versionable object's properties by hash.
/// </summary>
internal static class VersionableProperties {

    /// <summary>
    /// Reads every property of the object at the reader's position; <paramref name="read"/> reads the ones it knows.
    /// </summary>
    internal static bool Read(BitReader reader, Action<uint> read) {
        var objectStart = reader.BitPos();
        var objectSize = reader.ReadUInt32();

        while (reader.BitPos() - objectStart < objectSize) {
            var propertyStart = reader.BitPos();
            var propertySize = reader.ReadUInt32();
            var propertyHash = reader.ReadUInt32();
            if (propertySize == 0) {
                return false;
            }

            read(propertyHash);
            reader.SeekBit((int) (propertyStart + propertySize));
        }

        reader.SeekBit((int) (objectStart + objectSize));

        return true;
    }

}

/// <summary>
/// The fields ResPlayCinematic and ResCinematic share.
/// </summary>
public sealed class CinematicPlayback {

    internal static readonly uint CinematicNameHash = KingsIsleHash.Property("m_cinematicName", "std::string");
    internal static readonly uint RouterHash = KingsIsleHash.Property("m_router", "class ZoneRouter");
    internal static readonly uint BlockingHash = KingsIsleHash.Property("m_blocking", "bool");
    internal static readonly uint StartAtActorHash = KingsIsleHash.Property("m_startAtActor", "bool");
    internal static readonly uint StartAtTargetActorHash = KingsIsleHash.Property("m_startAtTargetActor", "bool");
    internal static readonly uint StartLocHash = KingsIsleHash.Property("m_startLoc", "class Vector3D");
    internal static readonly uint EndAtActorHash = KingsIsleHash.Property("m_endAtActor", "bool");
    internal static readonly uint EndAtTargetActorHash = KingsIsleHash.Property("m_endAtTargetActor", "bool");
    internal static readonly uint EndLocHash = KingsIsleHash.Property("m_endLoc", "class Vector3D");
    internal static readonly uint DisableInteractionHash = KingsIsleHash.Property("m_bDisableInteraction", "bool");
    internal static readonly uint ObjectTemplateIdHash = KingsIsleHash.Property("m_objectTemplateID", "int");
    internal static readonly uint UniqueHash = KingsIsleHash.Property("m_unique", "bool");
    internal static readonly uint UniqueNameHash = KingsIsleHash.Property("m_uniqueName", "std::string");
    internal static readonly uint UniqueBusyMsgHash = KingsIsleHash.Property("m_uniqueBusyMsg", "std::string");

    public string CinematicName { get; set; }
    public ZoneRouter Router { get; set; }
    public bool Blocking { get; set; }
    public bool StartAtActor { get; set; }
    public bool StartAtTargetActor { get; set; }
    public Vector3 StartLoc { get; set; }
    public bool EndAtActor { get; set; }
    public bool EndAtTargetActor { get; set; }
    public Vector3 EndLoc { get; set; }
    public bool DisableInteraction { get; set; }
    public int ObjectTemplateId { get; set; }
    public bool Unique { get; set; }
    public string UniqueName { get; set; }
    public string UniqueBusyMsg { get; set; }

    internal bool Decode(BitReader reader, ObjectSerializer serializer)
        => VersionableProperties.Read(reader, hash => {
            if (hash == CinematicNameHash) CinematicName = reader.ReadString();
            else if (hash == RouterHash) {
                serializer.PreloadObject(reader, out var router);
                if (router is ZoneRouter zoneRouter) {
                    zoneRouter.Decode(reader, serializer);
                    Router = zoneRouter;
                }
            }
            else if (hash == BlockingHash) Blocking = reader.ReadBit();
            else if (hash == StartAtActorHash) StartAtActor = reader.ReadBit();
            else if (hash == StartAtTargetActorHash) StartAtTargetActor = reader.ReadBit();
            else if (hash == StartLocHash) StartLoc = reader.ReadVector3();
            else if (hash == EndAtActorHash) EndAtActor = reader.ReadBit();
            else if (hash == EndAtTargetActorHash) EndAtTargetActor = reader.ReadBit();
            else if (hash == EndLocHash) EndLoc = reader.ReadVector3();
            else if (hash == DisableInteractionHash) DisableInteraction = reader.ReadBit();
            else if (hash == ObjectTemplateIdHash) ObjectTemplateId = reader.ReadInt32();
            else if (hash == UniqueHash) Unique = reader.ReadBit();
            else if (hash == UniqueNameHash) UniqueName = reader.ReadString();
            else if (hash == UniqueBusyMsgHash) UniqueBusyMsg = reader.ReadString();
        });

}

/// <summary>
/// ResPlayCinematic with its fields read (type hash 16312488).
/// </summary>
public sealed record ClassicResPlayCinematic : ResPlayCinematic {

    public CinematicPlayback Playback { get; } = new();

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return base.Decode(reader, serializer);
        }

        var decoded = Playback.Decode(reader, serializer);
        m_cinematicName = Playback.CinematicName;

        return decoded;
    }

}

/// <summary>
/// ResCinematic with its fields read (type hash 82637767).
/// </summary>
public sealed record ClassicResCinematic : ResCinematic {

    public CinematicPlayback Playback { get; } = new();

    public override bool Decode(BitReader reader, ObjectSerializer serializer)
        => serializer.Versionable ? Playback.Decode(reader, serializer) : base.Decode(reader, serializer);

}

/// <summary>
/// ResStartStagedCinematic with its fields read (type hash 145615551).
/// </summary>
public sealed record ClassicResStartStagedCinematic : ResStartStagedCinematic {

    internal static readonly uint IncludeAllPlayersHash = KingsIsleHash.Property("m_bIncludeAllPlayersInZone", "bool");
    internal static readonly uint StageNameHash = KingsIsleHash.Property("m_stageName", "std::string");

    public bool IncludeAllPlayersInZone { get; set; }
    public string CinematicName { get; set; }
    public string StageName { get; set; }

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return base.Decode(reader, serializer);
        }

        return VersionableProperties.Read(reader, hash => {
            if (hash == IncludeAllPlayersHash) IncludeAllPlayersInZone = reader.ReadBit();
            else if (hash == CinematicPlayback.CinematicNameHash) CinematicName = reader.ReadString();
            else if (hash == StageNameHash) StageName = reader.ReadString();
        });
    }

}

/// <summary>
/// The zone timer KingsIsle's triggers start with a staged cinematic (type hash 1038905797).
/// </summary>
public sealed record ClassicResZoneTimer : Result {

    public const uint TypeHash = 1038905797;

    internal static readonly uint TimerNameHash = KingsIsleHash.Property("m_timerName", "std::string");
    internal static readonly uint TimerTitleHash = KingsIsleHash.Property("m_timerTitle", "std::string");
    internal const uint CinematicHash = 1863059738;   // "MalistaireFightIntro" in r806919
    internal const uint LimitSecondsHash = 1276927527; // float: 300 and 180 in r806919
    internal const uint ConditionHash = 1712836159;    // "CLIENTEVENT.SawMalistaireFightIntro"

    public string TimerName { get; set; }
    public string TimerTitle { get; set; }
    public string Cinematic { get; set; }
    public float LimitSeconds { get; set; }
    public string Condition { get; set; }

    public override uint GetHash() => TypeHash;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return false;
        }

        return VersionableProperties.Read(reader, hash => {
            if (hash == TimerNameHash) TimerName = reader.ReadString();
            else if (hash == TimerTitleHash) TimerTitle = reader.ReadString();
            else if (hash == CinematicHash) Cinematic = reader.ReadString();
            else if (hash == LimitSecondsHash) LimitSeconds = reader.ReadFloat();
            else if (hash == ConditionHash) Condition = reader.ReadString();
        });
    }

}
