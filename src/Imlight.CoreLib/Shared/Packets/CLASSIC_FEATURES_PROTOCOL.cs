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

    /// <summary>
    /// CLASSIC: a sigil run's instance zone has been empty for its whole lifetime (30 minutes in 2009); the zone sends
    /// this to itself and then asks its container to drop it.
    /// </summary>
    public sealed class MSG_EMPTYRUNEXPIRED : IServerMessage {

        public byte MessageOrder { get; } = 9;
        public byte ServiceID { get; } = 110;

    }

    /// <summary>
    /// CLASSIC: a sigil run's instance container holds no zone any more; the game world forgets and stops it.
    /// </summary>
    public sealed class MSG_RUNCONTAINEREMPTY : IServerMessage {

        public byte MessageOrder { get; } = 10;
        public byte ServiceID { get; } = 110;

        public ulong OwnerId;

    }

    /// <summary>
    /// CLASSIC: the wizard used the dorm bank chest; the session's BankService loads the shared bank and opens the
    /// bank window (InteractBankComponent runs on the zone actor, which must not wait on the database).
    /// </summary>
    public sealed class MSG_BANKOPEN : IServerMessage {

        public byte MessageOrder { get; } = 11;
        public byte ServiceID { get; } = 110;

        /// <summary>The bank chest's global id (MSG_OPENBANK.GlobalID).</summary>
        public ulong BankObjectId;

        /// <summary>The zone the chest is in; the bank closes when the wizard leaves it.</summary>
        public string Zone;

    }

    /// <summary>
    /// CLASSIC: QuestService has queued the held quests for the socket (its MSG_PRELOGIN work); AttachService sends
    /// MSG_LOGINCOMPLETE on this instead of waiting a fixed 500 ms. The quest messages were told to the session before
    /// this one, so they still reach the client first.
    /// </summary>
    public sealed class MSG_PRELOGINREADY : IServerMessage {

        public byte MessageOrder { get; } = 12;
        public byte ServiceID { get; } = 110;

    }

    /// <summary>
    /// CLASSIC: a wizard used a Practice or Ranked guard in the arena (InteractPvpKioskComponent, on the zone actor); the
    /// session's ArenaService opens the client's PvP window.
    /// </summary>
    public sealed class MSG_ARENAKIOSK : IServerMessage {

        public byte MessageOrder { get; } = 13;
        public byte ServiceID { get; } = 110;

        public bool Ranked;
        public ulong KioskGid;

    }

    /// <summary>CLASSIC: the arena matchmaker sends this wizard into their match's arena instance.</summary>
    public sealed class MSG_ARENATRAVEL : IServerMessage {

        public byte MessageOrder { get; } = 14;
        public byte ServiceID { get; } = 110;

        public string Zone;
        public string Location;
        public ulong RunId;

    }

    /// <summary>
    /// CLASSIC: the arena matchmaker's result for this wizard: Arena Tickets into the game stats, the result window,
    /// and the trip back to the arena hall.
    /// </summary>
    public sealed class MSG_ARENAOUTCOME : IServerMessage {

        public byte MessageOrder { get; } = 15;
        public byte ServiceID { get; } = 110;

        public Imlight.CoreLib.Classic.Arena.ArenaOutcome Outcome;

    }

    /// <summary>CLASSIC: an ArenaService timer: back to the arena hall after a match.</summary>
    public sealed class MSG_ARENARETURN : IServerMessage {

        public byte MessageOrder { get; } = 16;
        public byte ServiceID { get; } = 110;

        public string Zone;
        public string Location;

        /// <summary>Seconds to wait first (a login into an arena waits for the attach to finish).</summary>
        public int DelaySeconds;

    }

    /// <summary>CLASSIC: a committed activation requests a timer/effect refresh; never on the wire.</summary>
    public sealed class MSG_ELIXIRCHANGED : IServerMessage {
        public byte MessageOrder { get; } = 17;
        public byte ServiceID { get; } = 110;
        public ulong CharacterId;
    }
}
