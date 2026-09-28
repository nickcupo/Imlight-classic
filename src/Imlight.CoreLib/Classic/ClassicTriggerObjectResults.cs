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
 * CLASSIC TRIGGER OBJECT RESULTS
 * ========================================================================
 *
 * PURPOSE:
 * Decodes classic object state changes and their fractional-second waits.
 *
 * USAGE EXAMPLE:
 * Registered by ClassicZoneTypeRegistry for classic zone data.
 *
 * NOTE:
 * Unknown nonempty state-change fields are not executable.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Cinematics;

namespace Imlight.CoreLib.Classic;

public sealed record ClassicResModifyTriggerObject : Result {
    public const uint TypeHash = 1263108441;
    internal const uint NameHash = 0xC6E6048B;
    internal const uint StateHash = 0x7B3D75AB;
    internal const uint UnknownStateHash = 0x94EAC863;

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
                case NameHash: ObjectName = reader.ReadString(); break;
                case StateHash: State = reader.ReadString(); break;
                case UnknownStateHash: UnknownState = reader.ReadString(); break;
                default: HasUnknownProperties = true; break;
            }
        });
    }
}

public sealed record ClassicResWait : ResWait {
    public const uint TypeHash = 526762782;
    internal const uint SecondsHash = 0x8F3C69B4;
    public double Seconds { get; set; }
    public override bool Encode(BitWriter writer, ObjectSerializer serializer) => false;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        if (!serializer.Versionable) {
            return false;
        }
        var found = false;
        var decoded = VersionableProperties.Read(reader, hash => {
            if (hash == SecondsHash) {
                Seconds = reader.ReadDouble();
                found = true;
            }
        });
        return decoded && found && double.IsFinite(Seconds) && Seconds >= 0;
    }
}
