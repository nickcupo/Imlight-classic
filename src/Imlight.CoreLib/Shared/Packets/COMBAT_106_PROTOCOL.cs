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

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Shared.Packets; 

public sealed class COMBAT_106_PROTOCOL : IServerProtocol {

    public byte ServiceID => 106;
    public string ProtocolType => "Wizard dueling messages";
    public int ProtocolVersion => 1;
    public string ProtocolDescription => "Internal messages for dueling.";

    public sealed class MSG_STARTDUEL : IServerMessage {

        public byte MessageOrder => 1;
        public byte ServiceID => 106;

        public Dictionary<IActorRef, CoreObject> Participants;
        public IActorRef SigilActor;
        public ulong SigilId;
        public Vector3 SigilLocation;
        public Vector3 SigilOrientation;
        public CombatSigilTemplate SigilTemplate;

    }

    public sealed class MSG_ENDDUEL : IServerMessage {

        public byte MessageOrder => 2;
        public byte ServiceID => 106;

        public Dictionary<IActorRef, CoreObject> Participants;

    }

    public sealed class MSG_DUELDETAILS : IServerMessage {

        public byte MessageOrder => 3;
        public byte ServiceID => 106;

        public IActorRef DuelActor;
        public Duel Duel;
        public byte CreatureCount;
        public byte PlayerCount;

    }

    public sealed class MSG_ADDPARTICIPANT : IServerMessage {

        public byte MessageOrder => 4;
        public byte ServiceID => 106;

        public IActorRef Participant;
        public CoreObject ParticipantObject;

    }

    public sealed class MSG_COMBATDEATH : IServerMessage {

        public byte MessageOrder => 5;
        public byte ServiceID => 106;

    }

    public sealed class MSG_NEWROUND : IServerMessage {

        public byte MessageOrder => 8;
        public byte ServiceID => 106;

        public int Round;

    }

    internal sealed class MSG_ACTORADDEDTODUEL : IServerMessage {

        public byte MessageOrder => 9;
        public byte ServiceID => 106;

        public IActorRef DuelActor;
        public CombatDuelComponent Duel;
        public CombatDuelSubCircle SubCircle;
        public Vector3 SlotPosition;
        public float SlotOrientation;

    }

    public sealed class MSG_ACTORCOMBATMOVE : IServerMessage {

        public byte MessageOrder => 10;
        public byte ServiceID => 106;

        public IActorRef Actor;
        public byte MoveType;
        public byte SpellSelection;
        public uint SpellTarget;
        public int TimeLeft;

    }

    public sealed class MSG_PLANNINGPHASEOVER : IServerMessage {

        public byte MessageOrder => 11;
        public byte ServiceID => 106;

    }

    public sealed class MSG_ROUNDRESOLUTION : IServerMessage {

        public byte MessageOrder => 12;
        public byte ServiceID => 106;

    }

    public sealed class MSG_QUERYCREATURESTATS : IServerMessage {

        public byte MessageOrder => 13;
        public byte ServiceID => 106;

    }

    public sealed class MSG_CREATURESTATS : IServerMessage {

        public byte MessageOrder => 14;
        public byte ServiceID => 106;

        public ServerWizGameStats GameStats;
        public float CombatIntelligence;
        public float CombatSelfishFactor;
        public float CombatAggressionFactor;
        public int CombatLevel;
        public MagicSchool MagicSchool;
        public List<SpellData> SpellList;

    }

    public sealed class MSG_COMBATDEFEAT : IServerMessage {

        public byte MessageOrder => 15;
        public byte ServiceID => 106;

    }

    public sealed class MSG_COMBATWIN : IServerMessage {

        public byte MessageOrder => 16;
        public byte ServiceID => 106;

        public int UsedPips;
        public string[] MobAdjectives;
        public ulong[] MobTemplateIds;

    }

    public sealed class MSG_COMBATEFFECT : IServerMessage {
        
        public byte MessageOrder => 17;
        public byte ServiceID => 106;

        public CombatDuelSubCircle Caster;
        public CombatDuelSubCircle[] Targets;
        public SpellEffect Effect;
        
    }

    public sealed class MSG_PLANNINGPHASEBEGIN : IServerMessage {
        
        public byte MessageOrder => 18;
        public byte ServiceID => 106;
        
    }

    public sealed class MSG_NOAGGROGRACEOVER : IServerMessage {
        
        public byte MessageOrder => 19;
        public byte ServiceID => 106;
        
    }

    public sealed class MSG_ACTORCOMBATDRAW : IServerMessage {

        public byte MessageOrder => 20;
        public byte ServiceID => 106;

        public IActorRef Actor;

    }

    internal sealed class MSG_CHEATINSTAWIN : IServerMessage {

        public byte MessageOrder => 21;
        public byte ServiceID => 106;

    }

    internal sealed class MSG_CHEATTOGGLECINEMATICS : IServerMessage {

        public byte MessageOrder => 22;
        public byte ServiceID => 106;

    }

    internal sealed class MSG_CHEATINSTANTCINEMATICS : IServerMessage {

        public byte MessageOrder => 23;
        public byte ServiceID => 106;

        public bool Enabled;

    }

    internal sealed class MSG_CHEATTOGGLENOFIZZLE : IServerMessage {

        public byte MessageOrder => 24;
        public byte ServiceID => 106;

    }

    internal sealed class MSG_CHEATNOFIZZLE : IServerMessage {

        public byte MessageOrder => 25;
        public byte ServiceID => 106;

        public bool Enabled;
        public IActorRef Actor;

    }

    /// <summary>
    /// Server-internal (CLASSIC): the Crown Shop asks the player's duel to bring in a hired henchman for them.
    /// </summary>
    internal sealed class MSG_HIREHENCHMAN : IServerMessage {

        public byte MessageOrder => 26;
        public byte ServiceID => 106;

        public uint CreatureTid;
        public IActorRef Actor;

    }

    /// <summary>
    /// Server-internal (CLASSIC): the duel's answer to <see cref="MSG_HIREHENCHMAN"/>.
    /// </summary>
    internal sealed class MSG_HENCHMANHIRED : IServerMessage {

        public byte MessageOrder => 27;
        public byte ServiceID => 106;

        public uint CreatureTid;
        public bool Success;

    }

    internal enum OwnedMinionStatus : byte {
        Accepted = 0,
        InvalidDuel = 1,
        InvalidRound = 2,
        NotPlanning = 3,
        InvalidOwner = 4,
        NotMyth = 5,
        NotOwnedMinion = 6,
        NotOptedIn = 7,
        InvalidMove = 8,
        InvalidCard = 9,
        InsufficientPips = 10,
        InvalidTarget = 11,
        RequestReplay = 12,
        SnapshotUnavailable = 13,
        UnsupportedSummon = 14,
        Disabled = 15,
    }

    internal sealed class OwnedMinionSnapshot {
        public ulong OwnerID;
        public ulong MinionID;
        public byte Slot;
        public byte Team;
        public int Health;
        public byte GenericPips;
        public byte PowerPips;
        public byte[] HandData = [];
        public byte[] ParticipantData = [];
        public bool HasOrder;
        public byte MoveType;
        public byte SpellSelection;
        public uint SpellTarget;
    }

    internal sealed class MSG_OWNEDMINIONREQUEST : IServerMessage {
        public byte MessageOrder => 28;
        public byte ServiceID => 106;

        // Only the authenticated service supplies OwnerActor; it is never a wire field.
        public IActorRef OwnerActor;
        public ulong DuelID;
        public int Round;
        public ulong MinionID;
        public uint RequestID;
        public byte MoveType;
        public byte SpellSelection;
        public uint SpellTarget;
        public bool Query;
    }

    internal sealed class MSG_OWNEDMINIONRESPONSE : IServerMessage {
        public byte MessageOrder => 29;
        public byte ServiceID => 106;

        public IActorRef OwnerActor;
        public ulong DuelID;
        public int Round;
        public ulong MinionID;
        public uint RequestID;
        public bool Accepted;
        public OwnedMinionStatus Status;
        public OwnedMinionSnapshot[] Snapshots = [];
        // CLASSIC: the same state in plain words (names, castable cards, legal targets) for the Minion Helper.
        public string HelperView;
    }

    internal sealed class MSG_OWNEDMINIONDISABLE : IServerMessage {
        public byte MessageOrder => 30;
        public byte ServiceID => 106;
        public IActorRef OwnerActor;
    }

    /// <summary>
    /// CLASSIC: the move type of a Minion Helper order that hands this round back to the minion's own AI. It counts
    /// as the owner's order (the round need not wait for one) and never reaches the wire.
    /// </summary>
    internal const byte OwnedMinionAiMove = 0x20;

    /// <summary>CLASSIC: the hub tells a session its account's Minion Helper connected (true) or left (false).</summary>
    internal sealed class MSG_MINIONHELPERLINK : IServerMessage {
        public byte MessageOrder => 32;
        public byte ServiceID => 106;
        public bool Connected;
    }

    /// <summary>CLASSIC: an order or a refresh from the account's Minion Helper, for the session's combat service.</summary>
    internal sealed class MSG_MINIONHELPERORDER : IServerMessage {
        public byte MessageOrder => 33;
        public byte ServiceID => 106;
        public ulong DuelID;
        public int Round;
        public ulong MinionID;
        public uint RequestID;
        public byte MoveType;
        public byte SpellSelection;
        public uint SpellTarget;
        public bool Query;
    }

    /// <summary>CLASSIC: the Minion Helper's "control my minions" switch.</summary>
    internal sealed class MSG_MINIONHELPERCONTROL : IServerMessage {
        public byte MessageOrder => 34;
        public byte ServiceID => 106;
        public bool Enabled;
    }

    /// <summary>
    /// CLASSIC: asks a duel for the owner's current state, and with Enable, to let this owner choose their minions'
    /// moves for the rest of the duel (any phase). Only the authenticated session supplies OwnerActor.
    /// </summary>
    internal sealed class MSG_OWNEDMINIONOPTIN : IServerMessage {
        public byte MessageOrder => 35;
        public byte ServiceID => 106;
        public IActorRef OwnerActor;
        public bool Enable;
    }

    // Internal roaming rejection, never a combat death or a wire message.
    internal sealed class MSG_REJECTEDROAMINGCREATURE : IServerMessage {
        public byte MessageOrder => 31;
        public byte ServiceID => 106;
        public CoreObject ExpectedCreature;
    }

}
