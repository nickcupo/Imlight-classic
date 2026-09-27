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
 */

using System.Collections.Concurrent;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResTeleportHandler : BaseResultHandler<ResTeleport> {

    private static readonly ConcurrentDictionary<string, bool> s_reportedMissingDestinations = new(); // CLASSIC

    public override bool Execute(IResultContext context) {
        if (context.GetPlayerObj() is not WizClientObject playerObj) {
            return false;
        }

        // CLASSIC: a client trigger's teleport has no destination until a SpiralDB ZoneTransfer record gives it one.
        if (ClassicQuestEngine.IsActive && string.IsNullOrEmpty(Result.m_destinationZone)) {
            var source = context is GenericResultContext generic
                ? generic.TriggerName ?? generic.QuestName ?? "?"
                : "?";
            if (s_reportedMissingDestinations.TryAdd(source, true)) {
                Logger.Warning("Skipped a teleport from {Source}: it has no destination zone (no SpiralDB ZoneTransfer record).",
                    Logger.Args(source));
            } else {
                Logger.Debug("Skipped a teleport from {Source}: no destination zone.", Logger.Args(source));
            }

            return true;
        }

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = Result.m_destinationZone,
            DestinationLocation = Result.m_destinationLoc,
            SendToClient = true,
            OwnerCharId = playerObj.m_characterId
        };

        context.GetPlayerRef().Tell(msg);

        return true;
    }

}