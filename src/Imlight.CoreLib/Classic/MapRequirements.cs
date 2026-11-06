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
 * MAP REQUIREMENTS
 * ========================================================================
 *
 * PURPOSE:
 * The spellbook map's requirement-gated doodles. When the map page opens,
 * r806919 (0x1409c02f0) sends WIZARD2_53 MSG_MAPREQUIREMENTS whose Data is a
 * MapInfoRequirementsList of {m_id, m_serializedRequirements}, one per doodle
 * with m_displayRequirements; m_serializedRequirements holds that
 * RequirementList's own binary serialization. The server answers with the
 * same message carrying a MapInfoRequirementsPassedList {m_idList} of the ids
 * the player meets (applied at 0x1409c06f0). Both layers: non-versionable,
 * no serializer flags, property mask 4 (Public) (ios-parity, 2026-11-06).
 *
 * USAGE EXAMPLE:
 * if (MapRequirements.TryReadRequests(data, out var requests)) { ... }
 *
 * NOTE:
 * m_serializedRequirements is a std::string, which Imcodec decodes as UTF-8;
 * the request list is read here with a BitReader so those bytes stay exact.
 * A doodle whose requirements do not decode, or name a requirement type with
 * no evaluator, is left out (not shown).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 11/06/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>The map page's display-requirement exchange (see the file header).</summary>
internal static class MapRequirements {

    /// <summary>r806919's mask on both layers: Public.</summary>
    internal const PropertyFlags Mask = PropertyFlags.Prop_Public;

    private const uint ListHash = 2015100750;     // MapInfoRequirementsList
    private const uint RequestHash = 206696061;   // MapInfoRequirementsRequest

    /// <summary>A gated doodle: its id and its RequirementList's raw serialization.</summary>
    internal sealed record Request(uint Id, byte[] Requirements);

    /// <summary>How each doodle came out.</summary>
    internal enum Verdict { Passed, Failed, Unevaluable }

    /// <summary>The MapInfoRequirementsList in a request's Data, its requirement bytes exact. False when it does not parse.</summary>
    internal static bool TryReadRequests(byte[] data, out List<Request> requests) {
        requests = [];
        if (data is not { Length: >= 8 }) {
            return false;
        }

        try {
            var reader = new BitReader(data);
            if (reader.ReadUInt32() != ListHash) {
                return false;
            }

            var count = reader.ReadUInt32();
            if (count > 4096) {
                return false;
            }

            for (var i = 0; i < count; i++) {
                var hash = reader.ReadUInt32();
                if (hash == 0) {
                    continue; // a null entry
                }

                if (hash != RequestHash) {
                    return false;
                }

                var id = reader.ReadUInt32();
                requests.Add(new Request(id, (byte[]) reader.ReadString() ?? []));
            }

            return true;
        }
        catch (Exception) {
            requests = [];
            return false;
        }
    }

    /// <summary>A doodle's RequirementList, or null when its bytes do not decode.</summary>
    internal static RequirementList DecodeRequirements(byte[] bytes) {
        if (bytes is not { Length: > 0 }) {
            return null;
        }

        try {
            var reader = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
            return reader.Deserialize<RequirementList>(bytes, Mask, out var list) ? list : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Each request's verdict. <paramref name="canEvaluate"/> says whether a requirement type has an evaluator;
    /// <paramref name="evaluate"/> evaluates a whole list for the player.
    /// </summary>
    internal static List<(uint Id, Verdict Verdict)> Judge(IEnumerable<Request> requests, Func<Requirement, bool> canEvaluate,
                                                           Func<RequirementList, bool> evaluate) {
        var verdicts = new List<(uint, Verdict)>();
        foreach (var request in requests) {
            var list = DecodeRequirements(request.Requirements);
            if (list is null || !Evaluable(list, canEvaluate)) {
                verdicts.Add((request.Id, Verdict.Unevaluable));
                continue;
            }

            verdicts.Add((request.Id, evaluate(list) ? Verdict.Passed : Verdict.Failed));
        }

        return verdicts;
    }

    private static bool Evaluable(RequirementList list, Func<Requirement, bool> canEvaluate) {
        foreach (var requirement in list.m_requirements ?? []) {
            if (requirement is null) {
                return false; // a type the codec does not know
            }

            if (requirement is RequirementList nested ? !Evaluable(nested, canEvaluate) : !canEvaluate(requirement)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The answer's Data: a MapInfoRequirementsPassedList of <paramref name="passed"/>.</summary>
    internal static byte[] WriteAnswer(IEnumerable<uint> passed) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        var answer = new MapInfoRequirementsPassedList { m_idList = [.. passed] };
        return serializer.Serialize(answer, Mask, out var data) ? (byte[]) data : null;
    }
}
