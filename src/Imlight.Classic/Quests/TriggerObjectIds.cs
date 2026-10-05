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
 * ZONE TRIGGERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the class and property hashes of the trigger-object and
 * trigger-state classes of KingsIsle's zone triggers.
 *
 * USAGE EXAMPLE:
 * case TriggerObjectIds.ObjectNameHash: ObjectName = reader.ReadString(); break;
 *
 * NOTE:
 * Each name was recovered by hashing a candidate with KingsIsle's type and
 * property hashes (KingsIsleHash) until it matched the hash in the r806919
 * triggers.xml BiND data; TriggerObjectIdsTests re-derives them.
 * TriggerObjectInfo's CoreObjectInfo properties are the r806919 type dump's.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

namespace Imlight.Classic.Quests;

/// <summary>
/// Hashes of the trigger-object and trigger-state classes of the zone trigger data.
/// </summary>
public static class TriggerObjectIds {

    /// <summary>"class TriggerObjectInfo", a trigger's m_triggerObjInfo.</summary>
    public const uint TriggerObjectInfoHash = 558354832;

    /// <summary>"class ReqTriggerState".</summary>
    public const uint ReqTriggerStateHash = 563618986;

    /// <summary>"class ResRemoveTriggerObject".</summary>
    public const uint ResRemoveTriggerObjectHash = 803366521;

    /// <summary>"class ResAddTriggerObject".</summary>
    public const uint ResAddTriggerObjectHash = 1893729846;

    /// <summary>"class ResStateChange".</summary>
    public const uint ResStateChangeHash = 1900934283;

    /// <summary>m_triggerObjName (std::string): the object's zone tag.</summary>
    public const uint ObjectNameHash = 0xC6E6048B;

    /// <summary>m_triggerName (std::string).</summary>
    public const uint TriggerNameHash = 0xB8C90C10;

    /// <summary>m_triggerState (enum ReqTriggerState::TRIGGER_STATE).</summary>
    public const uint TriggerStateHash = 2237549821;

    /// <summary>CoreObjectInfo.m_templateID (m_templateID.m_full, unsigned __int64).</summary>
    public const uint TemplateIdHash = 0x25C8A9AF;

    /// <summary>TriggerObjectInfo's own m_templateID (gid): the r806919 trigger data writes the template here.</summary>
    public const uint TriggerTemplateIdHash = 0x40183401;

    /// <summary>TriggerObjectBase.m_locationX (float); the 2014 packages place trigger objects here, not in m_location.</summary>
    public const uint LocationXHash = 0x7DB3F828;

    /// <summary>TriggerObjectBase.m_locationY (float).</summary>
    public const uint LocationYHash = 0x7DB3F829;

    /// <summary>TriggerObjectBase.m_locationZ (float).</summary>
    public const uint LocationZHash = 0x7DB3F82A;

    /// <summary>CoreObjectInfo.m_nObjectID.</summary>
    public const uint ObjectIdHash = 0x2C9D281F;

    /// <summary>CoreObjectInfo.m_location.</summary>
    public const uint LocationHash = 0x857EDC1B;

    /// <summary>CoreObjectInfo.m_orientation.</summary>
    public const uint OrientationHash = 0x8BB77F8E;

    /// <summary>CoreObjectInfo.m_fScale.</summary>
    public const uint ScaleHash = 0x1DFD45A5;

    /// <summary>CoreObjectInfo.m_zoneTag.</summary>
    public const uint ZoneTagHash = 0xCAFA03F3;

    /// <summary>CoreObjectInfo.m_startState.</summary>
    public const uint StartStateHash = 0x8127F8CA;

    /// <summary>CoreObjectInfo.m_overrideName.</summary>
    public const uint OverrideNameHash = 0x76A7C81C;

    /// <summary>CoreObjectInfo.m_globalDynamic.</summary>
    public const uint GlobalDynamicHash = 0x74547109;

    /// <summary>CoreObjectInfo.m_bUndetectable.</summary>
    public const uint UndetectableHash = 0x71B17185;

    /// <summary>CoreObjectInfo.m_spawnRequirements.</summary>
    public const uint SpawnRequirementsHash = 0x89A26366;

    /// <summary>CoreObjectInfo.m_loadingType.</summary>
    public const uint LoadingTypeHash = 0xD1F3E716;

}
