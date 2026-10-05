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
 * CLASSIC TRIGGER OBJECT TYPES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: reads the zone-trigger classes that name trigger objects and
 * trigger states, which the generated registries read empty: a trigger's
 * own object (TriggerObjectInfo), ResRemoveTriggerObject,
 * ResAddTriggerObject, ResStateChange and ReqTriggerState.
 *
 * USAGE EXAMPLE:
 * Registered in ClassicZoneTypeRegistry; ZoneTriggerPlans reads them.
 *
 * NOTE:
 * The classes are server-only (not in the r806919 type dump). Their names
 * are KingsIsle's: "class TriggerObjectInfo" hashes to 558354832,
 * "class ResStateChange" to 1900934283 and "class ReqTriggerState" to
 * 563618986, and the properties hash as m_triggerObjName (std::string),
 * m_triggerObjState (std::string), m_triggerName (std::string) and
 * m_triggerState (enum ReqTriggerState::TRIGGER_STATE, "TRIGGER_STATE_ACTIVE"
 * in the Grizzleheim library and the Marleybone gauntlet). Hashes:
 * Imlight.Classic.Quests.TriggerObjectIds. TriggerObjectInfo carries
 * CoreObjectInfo's fields; Imcodec's own TriggerObjectInfo decodes none of
 * them, so m_triggerObjInfo used to read as an empty object.
 *
 * TODO:
 * - ResStateChange's and ResModifyTriggerObject's third string (hash 2498414691) is empty in all Arc 1 data; unnamed.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic.Cinematics;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// A trigger's own object (m_triggerObjInfo): a gate, a collision wall, a teleporter pad.
/// </summary>
public sealed record ClassicTriggerObjectInfo : TriggerObjectInfo {

    public override uint GetHash() => TriggerObjectIds.TriggerObjectInfoHash;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return false;
        }

        var decoded = VersionableProperties.Read(reader, hash => {
            switch (hash) {
                case TriggerObjectIds.LocationXHash: m_locationX = reader.ReadFloat(); break;
                case TriggerObjectIds.LocationYHash: m_locationY = reader.ReadFloat(); break;
                case TriggerObjectIds.LocationZHash: m_locationZ = reader.ReadFloat(); break;
                case TriggerObjectIds.TemplateIdHash: m_templateID = reader.ReadUInt64(); break;
                case TriggerObjectIds.TriggerTemplateIdHash: m_templateID = reader.ReadUInt64(); break;
                case TriggerObjectIds.ObjectIdHash: m_nObjectID = reader.ReadUInt32(); break;
                case TriggerObjectIds.LocationHash: m_location = reader.ReadVector3(); break;
                case TriggerObjectIds.OrientationHash: m_orientation = reader.ReadVector3(); break;
                case TriggerObjectIds.ScaleHash: m_fScale = reader.ReadFloat(); break;
                case TriggerObjectIds.ZoneTagHash: m_zoneTag = reader.ReadString(); break;
                case TriggerObjectIds.StartStateHash: m_startState = reader.ReadString(); break;
                case TriggerObjectIds.OverrideNameHash: m_overrideName = reader.ReadString(); break;
                case TriggerObjectIds.GlobalDynamicHash: m_globalDynamic = reader.ReadBit(); break;
                case TriggerObjectIds.UndetectableHash: m_bUndetectable = reader.ReadBit(); break;
                case TriggerObjectIds.SpawnRequirementsHash:
                    serializer.PreloadObject(reader, out var requirements);
                    if (requirements is RequirementList list) {
                        list.Decode(reader, serializer);
                        m_spawnRequirements = list;
                    }
                    break;
                case TriggerObjectIds.LoadingTypeHash:
                    if (serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)) {
                        var raw = (string) reader.ReadString() ?? "";
                        var name = raw.Replace('-', '_');
                        name = name[(name.LastIndexOf(':') + 1)..];
                        m_loadingType = Enum.TryParse<LoadingType>(name, true, out var loading) ? loading : default;
                    } else {
                        m_loadingType = (LoadingType) Enum.ToObject(typeof(LoadingType), reader.ReadUInt32());
                    }
                    break;
            }
        });

        // The 2014 packages leave m_location at zero and place the object by m_locationX/Y/Z (the Tomb of the Beguiler).
        if (m_location.X == 0 && m_location.Y == 0 && m_location.Z == 0) {
            m_location = new Imcodec.Math.Vector3(m_locationX, m_locationY, m_locationZ);
        }

        return decoded;
    }

}

/// <summary>
/// ResRemoveTriggerObject / ResAddTriggerObject: take an object away from the zone, or bring it back, by its zone tag.
/// </summary>
public abstract record ClassicResTriggerObjectPresence : Result {

    public string ObjectName { get; set; }

    /// <summary>True for ResAddTriggerObject.</summary>
    public abstract bool Adds { get; }

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return false;
        }

        return VersionableProperties.Read(reader, hash => {
            if (hash == TriggerObjectIds.ObjectNameHash) {
                ObjectName = reader.ReadString();
            }
        });
    }

}

/// <summary>ResRemoveTriggerObject.</summary>
public sealed record ClassicResRemoveTriggerObject : ClassicResTriggerObjectPresence {

    public const uint TypeHash = TriggerObjectIds.ResRemoveTriggerObjectHash;

    public override bool Adds => false;

    public override uint GetHash() => TypeHash;

}

/// <summary>ResAddTriggerObject.</summary>
public sealed record ClassicResAddTriggerObject : ClassicResTriggerObjectPresence {

    public const uint TypeHash = TriggerObjectIds.ResAddTriggerObjectHash;

    public override bool Adds => true;

    public override uint GetHash() => TypeHash;

}

/// <summary>
/// ResStateChange: like ResModifyTriggerObject, puts a trigger object into a state (the Tomb of the Beguiler's frost
/// prison doors "Idle_Open").
/// </summary>
public sealed record ClassicResStateChange : Result {

    public const uint TypeHash = TriggerObjectIds.ResStateChangeHash;

    public string ObjectName { get; set; }
    public string State { get; set; }
    public string UnknownState { get; set; }
    public bool HasUnknownProperties { get; private set; }
    public bool CanExecute => !string.IsNullOrWhiteSpace(ObjectName)
        && !string.IsNullOrWhiteSpace(State) && string.IsNullOrEmpty(UnknownState) && !HasUnknownProperties;

    public override uint GetHash() => TypeHash;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return false;
        }

        return VersionableProperties.Read(reader, hash => {
            switch (hash) {
                case ClassicResModifyTriggerObject.NameHash: ObjectName = reader.ReadString(); break;
                case ClassicResModifyTriggerObject.StateHash: State = reader.ReadString(); break;
                case ClassicResModifyTriggerObject.UnknownStateHash: UnknownState = reader.ReadString(); break;
                default: HasUnknownProperties = true; break;
            }
        });
    }

}

/// <summary>
/// ReqTriggerState: the zone's trigger named <see cref="m_triggerName"/> is active (or, for
/// TRIGGER_STATE_INACTIVE, inactive). The Grizzleheim library door opens once its four checks are enabled.
/// </summary>
public sealed record ClassicReqTriggerState : Requirement {

    public string m_triggerName { get; set; } = "";
    public string m_triggerState { get; set; } = "";

    /// <summary>True when the requirement asks for an active trigger (TRIGGER_STATE_ACTIVE, the only state the Arc 1 data
    /// asks for).</summary>
    public bool WantsActive => !m_triggerState.Contains("INACTIVE", StringComparison.OrdinalIgnoreCase);

    public override uint GetHash() => TriggerObjectIds.ReqTriggerStateHash;

    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return false;
        }

        return VersionableProperties.Read(reader, hash => {
            switch (hash) {
                case ReqStateIds.ApplyNotHash:
                    m_applyNOT = reader.ReadBit();
                    break;
                case ReqStateIds.OperatorHash:
                    if (serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)) {
                        var name = ((string) reader.ReadString() ?? "").Replace('-', '_');
                        name = name[(name.LastIndexOf(':') + 1)..];
                        m_operator = Enum.TryParse<Operator>(name, true, out var op) ? op : default;
                    } else {
                        m_operator = (Operator) Enum.ToObject(typeof(Operator), reader.ReadUInt32());
                    }
                    break;
                case TriggerObjectIds.TriggerNameHash:
                    m_triggerName = (string) reader.ReadString() ?? "";
                    break;
                case TriggerObjectIds.TriggerStateHash:
                    m_triggerState = serializer.SerializerFlags.HasFlag(SerializerFlags.StringEnums)
                        ? (string) reader.ReadString() ?? ""
                        : reader.ReadUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        });
    }

}
