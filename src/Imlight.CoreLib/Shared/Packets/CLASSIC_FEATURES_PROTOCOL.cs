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
 * CLASSIC FEATURE MESSAGES (INTERNAL)
 * ========================================================================
 *
 * PURPOSE:
 * Internal actor messages for the owner's server features: combat rejoin,
 * dungeon resets, safe restarts and the open PvP circles. None of these
 * reach a game client.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Packets;

internal sealed class CLASSIC_FEATURES_PROTOCOL : IServerProtocol {

    public byte ServiceID { get; } = 110;
    public string ProtocolType { get; } = "CLASSICFEATURES";
    public int ProtocolVersion { get; } = 1;
    public string ProtocolDescription { get; } = "Internal messages for the classic server features.";

    /// <summary>
    /// CLASSIC: to a creature whose duel ended without its defeat (the wizards lost, fled or dropped): it leaves the
    /// duel with full health and goes back to what it was doing, instead of being deleted.
    /// </summary>
    public sealed class MSG_COMBATRESET : IServerMessage {

        public byte MessageOrder { get; } = 1;
        public byte ServiceID { get; } = 110;

    }

    /// <summary>
    /// CLASSIC: to a zone: every wizard in one of its duels was defeated. An instanced zone (a dungeon) drops itself
    /// once it is empty, so the next entry builds it fresh.
    /// </summary>
    public sealed class MSG_INSTANCEPARTYLOST : IServerMessage {

        public byte MessageOrder { get; } = 2;
        public byte ServiceID { get; } = 110;

    }

    /// <summary>
    /// CLASSIC: a duel's own timer: a dropped wizard's seat is held no longer.
    /// </summary>
    public sealed class MSG_REJOINEXPIRED : IServerMessage {

        public byte MessageOrder { get; } = 3;
        public byte ServiceID { get; } = 110;

        public ulong CharacterId;

    }

    /// <summary>
    /// CLASSIC: an instance container asks its parent (the game world) to forget it, or a zone asks its container to
    /// drop it.
    /// </summary>
    public sealed class MSG_DROPSELF : IServerMessage {

        public byte MessageOrder { get; } = 4;
        public byte ServiceID { get; } = 110;

        public string ZoneName;

    }

    /// <summary>
    /// CLASSIC: an ambient wizard (claude/perf-ambient) is asked to take a side in an open PvP circle.
    /// </summary>
    public sealed class MSG_PVPSEATREQUEST : IServerMessage {

        public byte MessageOrder { get; } = 5;
        public byte ServiceID { get; } = 110;

        public IActorRef ParticipantActor;
        public CoreObject ParticipantObject;
        public Wizard Wizard;
        public int Side;

    }

    /// <summary>
    /// CLASSIC: the open PvP circle's own countdown tick.
    /// </summary>
    public sealed class MSG_PVPCOUNTDOWN : IServerMessage {

        public byte MessageOrder { get; } = 6;
        public byte ServiceID { get; } = 110;

    }

    /// <summary>
    /// CLASSIC: to a wizard's session when their open PvP fight ends or they leave it: no defeat penalty, they stay in
    /// the arena (with 1 health if they were defeated).
    /// </summary>
    public sealed class MSG_PVPRELEASE : IServerMessage {

        public byte MessageOrder { get; } = 7;
        public byte ServiceID { get; } = 110;

        public bool Won;
        public bool Fought;

    }

    /// <summary>
    /// CLASSIC: ".pvp ready" or ".pvp leave" from a wizard seated in an open PvP circle, through their session.
    /// </summary>
    public sealed class MSG_PVPCOMMAND : IServerMessage {

        public byte MessageOrder { get; } = 8;
        public byte ServiceID { get; } = 110;

        public IActorRef Actor;
        public bool Leave;

    }

}
