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
 * AMBIENT COMPANION GROUP
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): a real player's group of ambient wizards, from the
 * first yes until the last one leaves (owner: "build a group 'naturally'
 * like the old days with randos"). One actor per player; it is its
 * companions' Driver (their endpoints send it their messages).
 *   - Following: each companion keeps its own spot a few steps behind the
 *     player (DungeonManners.FollowSpot), walking only when the player has
 *     moved off. When the player goes into another zone, it walks to where
 *     the player was last seen there (the door) and follows: into the same
 *     public zone, or the player's instance (a dungeon run, by its owner
 *     id), as a friend teleporting in would. It does not follow into a
 *     house, the tutorial, an arena match or a minigame, nor into an
 *     instance its size cannot hold (a tower for one): it waits outside
 *     (ten minutes at most). A companion that cannot walk to the player
 *     teleports to them, as to a friend, at most every two minutes.
 *   - Sigils: when the player steps on a dungeon sigil, the companions in
 *     that zone step on with them (a real player still takes their place)
 *     and go in with the player when the countdown is over.
 *   - Fights: any duel with the player in it, in the player's zone, with a
 *     free slot: the companions walk in without asking (they are a group)
 *     and play their turns with AllyBrain. A real player who reaches a full
 *     circle takes a companion's slot. A defeated companion goes off to heal
 *     ("brb") and comes back to the player a minute or so later. Rewards and
 *     quest credit are each real player's own; a companion has no quests and
 *     completes nothing for anyone.
 *   - Talk: a short line after a won fight or in a new zone now and then,
 *     an answer when the player uses its name, whispers it or talks on the
 *     group channel (AmbientChatBrain, the word filter, a human answer
 *     time), and "u there?" when the player stands still ten minutes.
 *   - Leaving: on the player's "bye" (all, or the one named), "thanks" out
 *     of a fight after a few minutes together, the client's Leave Group or
 *     its removal from the group, when the player logs off (30 s), stays
 *     away (15 minutes still), or its own time is up (StayFor; never in a
 *     fight, and in a dungeon only once out or 20 minutes over): "gotta go,
 *     dinner". It goes back to its home street (in place when it is there).
 *   - The client's group window ([Classic] AmbientWizardGroupWindow): the
 *     group as the client's own group protocol shows it (MSG_PARTYJOIN-
 *     NOTIFICATION, MSG_PARTYUPDATE per companion, MSG_CHANGEGROUPLEADER,
 *     MSG_PARTYLEAVENOTIFICATION, MSG_PARTYDISBAND; the r806919 message
 *     definitions, as upstream Imlight's feat/groups sends them), sent again
 *     when the client asks for its lists after a zone change; companions
 *     answer on its group chat channel (MSG_CHANNELCHAT).
 *
 * USAGE EXAMPLE:
 * var group = Context.ActorOf(AmbientCompanionGroup.Props(leader, channel, party, server));
 * group.Tell(new AmbientCompanionGroup.Take(wizard));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A real player's group of ambient companions (see the file header).</summary>
internal sealed class AmbientCompanionGroup : ReceiveActor, IWithTimers {

    /// <summary>A wizard that said yes joins the group (it has left its zone's care).</summary>
    internal sealed record Take(AmbientWizard Wizard);

    /// <summary>The player used Leave Group in the client: the whole group goes.</summary>
    internal sealed record LeaveGroup;

    /// <summary>The player removed <paramref name="CharId"/> from the group in the client.</summary>
    internal sealed record RemoveMember(ulong CharId);

    /// <summary>The player said <paramref name="Text"/> on the group's chat channel.</summary>
    internal sealed record GroupChat(string Text);

    /// <summary>Send the client's group window again (it asked for its lists after a zone change).</summary>
    internal sealed record Refresh;

    private sealed record Step;
    private sealed record ShowNow;
    private sealed record Later(Companion Companion, Action<Companion> Action);
    private sealed record NavReady(string Zone, NavGrid Grid);

    private enum Voice { Near, Whisper, Group }

    private sealed class Companion(AmbientWizard wizard, int index, DateTime joined, DateTime stayUntil) {
        public AmbientWizard Wizard { get; } = wizard;
        public int Index { get; } = index;
        public DateTime Joined { get; } = joined;
        public DateTime StayUntil { get; } = stayUntil;
        public ulong CharId => Wizard.CharId;
        public string ZonePath { get; set; }
        public ulong Instance { get; set; }
        public IActorRef ZoneActor { get; set; }
        public bool Present { get; set; }
        public bool Transferring { get; set; }
        public DateTime TransferAt { get; set; }
        public int Failures { get; set; }
        public string DoorTo { get; set; }
        public DateTime DoorBy { get; set; }
        public DateTime? Unreachable { get; set; }
        public DateTime? Far { get; set; }
        public DateTime HelpBy { get; set; }
        public DateTime NextFish { get; set; }
        public Vector3? ArriveNear { get; set; }
        public DateTime? AwayUntil { get; set; }
        public DateTime? WaitingSince { get; set; }
        public DateTime LastPort { get; set; }
        public AmbientSigilNotice SigilComing { get; set; }
        public SigilGroup Sigil { get; set; }
        public bool Defeated { get; set; }
        public string Key => KeyOf(ZonePath, Instance);
    }

    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(100);
    private const int DecideEvery = 3; // steps (300 ms)

    private readonly ulong _leader;
    private readonly ulong _channel;
    private readonly ulong _party;
    private readonly IActorRef _server;
    private readonly List<Companion> _companions = [];
    private readonly Dictionary<string, NavGrid> _navs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _navAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> _leaderLastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, AmbientDuelNotice> _duels = [];
    private readonly LineHistory _heard = new();
    private readonly Random _rng;
    private readonly CoreObjectSerializer _serializer = new(versionable: false, behaviors: SerializerFlags.None);
    private DateTime? _leaderGone;
    private string _leaderKey;
    private Vector3 _stillAt;
    private DateTime _stillSince = DateTime.UtcNow;
    private bool _askedAfk;
    private DateTime _nextTalk = DateTime.UtcNow.AddMinutes(2);
    private (string Text, DateTime At) _lastHeard;
    private bool _windowShown;
    private bool _over;
    private readonly DateTime _born = DateTime.UtcNow;
    private int _steps;
    private int _seq;

    public ITimerScheduler Timers { get; set; }

    public AmbientCompanionGroup(ulong leader, ulong channel, ulong party, IActorRef server) {
        _leader = leader;
        _channel = channel;
        _party = party;
        _server = server;
        _rng = new Random(unchecked((int) (leader ^ (leader >> 32) ^ channel)));
        Receive<Take>(OnTake);
        Receive<Step>(_ => OnStep());
        Receive<Later>(later => {
            if (_companions.Contains(later.Companion)) {
                later.Action(later.Companion);
            }
        });
        Receive<NavReady>(ready => _navs[ready.Zone] = ready.Grid);
        Receive<AmbientInbox>(OnInbox);
        Receive<AmbientDuelNotice>(OnDuelNotice);
        Receive<AmbientSigilNotice>(OnSigilNotice);
        Receive<LeaveGroup>(_ => EndGroup(GroupLines.ByeBack, quick: true));
        Receive<RemoveMember>(remove => {
            if (_companions.FirstOrDefault(c => c.CharId == remove.CharId) is { } companion) {
                GoHome(companion, GroupLines.ByeBack);
            }
        });
        Receive<GroupChat>(chat => Heard(_leader, chat.Text, Voice.Group, null));
        Receive<Refresh>(_ => Timers.StartSingleTimer("window", new ShowNow(), TimeSpan.FromSeconds(1)));
        Receive<ShowNow>(_ => ShowWindow());
    }

    public static Props Props(ulong leader, ulong channel, ulong party, IActorRef server)
        => Akka.Actor.Props.Create(() => new AmbientCompanionGroup(leader, channel, party, server));

    protected override void PreStart() => Timers.StartPeriodicTimer("step", new Step(), StepInterval);

    protected override void PostStop() {
        // Stopped with companions still in it (the server is shutting down): each is released to its home zone.
        foreach (var companion in _companions.ToList()) {
            var wizard = companion.Wizard;
            AmbientWizards.RevokeJoin(wizard.Endpoint);
            companion.Sigil?.Leave(companion.CharId);
            wizard.Driver = null;
            RemoveFromZone(companion);
            wizard.Group?.Tell(new AmbientCompanionBack(wizard, null, TimeSpan.FromSeconds(10)));
        }

        _companions.Clear();
        AmbientGroups.Unregister(_leader, Self, _channel);
        base.PostStop();
    }

    private static string KeyOf(string zone, ulong instance) => $"{zone}|{instance}";

    // ---- joining --------------------------------------------------------------------------------

    private void OnTake(Take take) {
        var wizard = take.Wizard;
        var settings = AmbientGroups.Settings;
        if (_over || _companions.Any(c => c.Wizard == wizard)
            || GroupManners.OpenSlots(1, _companions.Count, settings.MaxSize) <= 0) {
            // Full (or ending) before it got here: it says so and goes back to its day.
            Logger.Information("Ambient wizard {Name} could not join {Leader}'s group (full).", Logger.Args(wizard.Name, _leader));
            Say(wizard, GroupLines.For(GroupLines.Full, ChatPersona.For(wizard.Identity), wizard.Identity.Seed + wizard.Turn++), Voice.Near);
            wizard.Driver = null;
            wizard.Group?.Tell(new AmbientCompanionBack(wizard, wizard.Present ? wizard.ZoneActor : null, TimeSpan.FromSeconds(5)));
            return;
        }

        var now = DateTime.UtcNow;
        var index = Enumerable.Range(0, 3).FirstOrDefault(i => _companions.All(c => c.Index != i));
        var stay = GroupManners.StayFor(wizard.Identity.Seed ^ (int) now.Ticks, settings.StayMinutes);
        var companion = new Companion(wizard, index, now, now + stay) { LastPort = now };
        _companions.Add(companion);
        wizard.Driver = Self;
        wizard.Activity = AmbientActivity.Grouped;
        wizard.FollowCharId = 0;
        wizard.DuelSigil = 0;
        if (wizard.Present && wizard.ZoneActor is not null) {
            // In place: it stays where it stands and starts following from there.
            companion.Present = true;
            companion.ZonePath = wizard.Zone;
            companion.Instance = 0;
            companion.ZoneActor = wizard.ZoneActor;
            LoadNav(wizard.Zone);
        }
        else {
            companion.AwayUntil = now; // it comes to the player (the next decision)
        }

        Logger.Information("Ambient wizard {Name} (level {Level} {School}) joins {Leader}'s group for about {Minutes} minutes ({Count} companion(s)).",
            Logger.Args(wizard.Name, wizard.Wizard.MagicSchoolBehavior?.Level ?? 0, wizard.Identity.School, _leader, (int) stay.TotalMinutes,
                _companions.Count));
        MembersChanged();
        Self.Tell(new Refresh());
    }

    private void MembersChanged() => AmbientGroups.SetMembers(_leader, [.. _companions.Select(c => c.CharId)]);

    // ---- the step -------------------------------------------------------------------------------

    private void OnStep() {
        var now = DateTime.UtcNow;
        var moves = new Dictionary<IActorRef, List<IMessage>>();
        foreach (var companion in _companions.Where(c => c.Present && c.Wizard.Moving && c.ZoneActor is not null)) {
            Walk(companion, now, moves);
        }

        foreach (var (zone, batch) in moves) {
            Send(zone, batch);
        }

        if (++_steps % DecideEvery == 0) {
            Decide(now);
        }
    }

    private void Walk(Companion companion, DateTime now, Dictionary<IActorRef, List<IMessage>> moves) {
        var wizard = companion.Wizard;
        if (!moves.TryGetValue(companion.ZoneActor, out var batch)) {
            moves[companion.ZoneActor] = batch = [];
        }

        if (wizard.Leg is { } leg && now >= leg.End) {
            wizard.Position = wizard.Target ?? wizard.Position;
            wizard.Leg = null;
            wizard.Target = wizard.Route.Count > 0 ? wizard.Route.Dequeue() : null;
        }

        if (wizard.Leg is null) {
            if (wizard.Target is not { } next) {
                wizard.Moving = false;
                wizard.Wizard.Location = wizard.Position;
                batch.Add(AmbientZone.Move(wizard));
                batch.Add(new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 });
                Stopped(companion, now);
                return;
            }

            var started = WalkLeg.Begin(Num(wizard.Position), Num(next), now, AmbientNav.RunSpeed);
            wizard.Leg = started;
            wizard.Yaw = started.Heading;
        }

        var current = wizard.Leg.Value;
        var here = current.At(now);
        var floor = Nav(companion)?.FloorAt(here);
        wizard.Position = new Vector3(here.X, here.Y, floor ?? here.Z);
        wizard.Wizard.Location = wizard.Position;
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        var ahead = current.At(now + StepInterval);
        batch.Add(AmbientZone.Move(wizard, new Vector3(ahead.X, ahead.Y, ahead.Z)));
    }

    private void Stopped(Companion companion, DateTime now) {
        var wizard = companion.Wizard;
        if (wizard.Activity == AmbientActivity.Helping) {
            Fish(companion); // at the circle: the duel takes it (with its permit) if a slot is free
            companion.NextFish = now.AddSeconds(1);
        }
        else if (companion.SigilComing is not null) {
            StepOnSigil(companion, now);
        }
        else if (ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)) {
            Face(companion, leader.Location);
        }
    }

    private static void Send(IActorRef zone, List<IMessage> batch) {
        if (batch.Count > 0 && zone is not null) {
            zone.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_CLIENTBATCH { Messages = [.. batch] }],
                Targets = ZoneBroadcastTarget.Players,
            });
        }
    }

    // ---- deciding -------------------------------------------------------------------------------

    private void Decide(DateTime now) {
        if (_over) {
            return;
        }

        if (_companions.Count == 0 && now - _born > TimeSpan.FromSeconds(30)) {
            // Nobody joined after all (or the actor was restarted after a fault and released them): it ends.
            _over = true;
            AmbientGroups.Unregister(_leader, Self, _channel);
            Context.Stop(Self);
            return;
        }

        var online = OnlinePlayerCollection.GetOnlinePlayer(_leader);
        if (online is null || !ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)) {
            _leaderGone ??= now;
            if (now - _leaderGone.Value >= GroupManners.LeaderGone) {
                Logger.Information("Ambient group of {Leader}: the player logged off; the group goes.", Logger.Args(_leader));
                EndGroup(null, quick: true);
            }

            return;
        }

        _leaderGone = null;
        var zone = online.CurrentZone ?? leader.Zone;
        var instance = online.InstanceOwnerId;
        var key = KeyOf(zone, instance);
        _leaderLastSeen[key] = leader.Location;
        if (key != _leaderKey) {
            var first = _leaderKey is null;
            _leaderKey = key;
            if (!first && now >= _nextTalk && _rng.NextDouble() < 0.15 && _companions.FirstOrDefault(c => c.Present) is { } talker) {
                _nextTalk = now.AddMinutes(2);
                var line = GroupLines.For(GroupLines.NewZone, Persona(talker), talker.Wizard.Identity.Seed + talker.Wizard.Turn++);
                SayLater(talker, line, Voice.Near, TimeSpan.FromSeconds(6 + _rng.Next(6)));
            }
        }

        if (Distance(leader.Location, _stillAt) > 40 || leader.IsInDuel) {
            _stillAt = leader.Location;
            _stillSince = now;
            _askedAfk = false;
        }
        else if (now - _stillSince >= GroupManners.AfkLeave) {
            Logger.Information("Ambient group of {Leader}: the player has been away 15 minutes; the group goes.", Logger.Args(_leader));
            EndGroup(GroupLines.AfkLeave, quick: false);
            return;
        }
        else if (now - _stillSince >= GroupManners.AfkAsk && !_askedAfk) {
            _askedAfk = true;
            if (_companions.FirstOrDefault(c => c.Present) is { } asker) {
                Say(asker.Wizard, GroupLines.For(GroupLines.AfkAsk, Persona(asker), asker.Wizard.Identity.Seed + asker.Wizard.Turn++), Voice.Near);
            }
        }

        foreach (var companion in _companions.ToList()) {
            DecideFor(companion, leader, online, zone, instance, key, now);
        }

        if (now >= _nextTalk && !leader.IsInDuel && _rng.NextDouble() < 0.004) {
            IdleTalk(now);
        }
    }

    private void DecideFor(Companion companion, Wizard leader, OnlinePlayer online, string zone, ulong instance, string key, DateTime now) {
        var wizard = companion.Wizard;
        if (companion.Transferring) {
            if (now - companion.TransferAt > TimeSpan.FromSeconds(30)) {
                // No answer from the zone: as a failed transfer (it tries again in a while, three times at most).
                Arrived(companion, new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1, ErrorMessage = "no answer" });
            }

            return;
        }

        if (companion.AwayUntil is { } away) {
            if (now < away) {
                return;
            }

            if (!CanFollow(companion, zone, instance, online)) {
                Wait(companion, now);
                return;
            }

            companion.AwayUntil = null;
            Transfer(companion, zone, instance, leader.Location);
            return;
        }

        if (!companion.Present) {
            return;
        }

        switch (wizard.Activity) {
            case AmbientActivity.Fighting:
                return;
            case AmbientActivity.Helping:
                if (now > companion.HelpBy) {
                    StopHelping(companion); // no seat in time: back to following
                }
                else if (!wizard.Moving && now >= companion.NextFish) {
                    Fish(companion);
                    companion.NextFish = now.AddSeconds(1);
                }

                return;
        }

        // Its time is up: never in a fight, in a dungeon only once out of it (or well over time).
        if (now >= companion.StayUntil && !wizard.Wizard.IsInDuel && (instance == 0 || now >= companion.StayUntil + GroupManners.DungeonGrace)) {
            Logger.Information("Ambient wizard {Name} leaves {Leader}'s group: its time is up.", Logger.Args(wizard.Name, _leader));
            GoHome(companion, GroupLines.OwnLeave);
            return;
        }

        if (companion.Sigil is { } sigil) {
            if (!sigil.IsMember(companion.CharId) || (!sigil.IsMember(_leader) && now < sigil.EndsUtc)) {
                // Bumped by a real player, or the player stepped off before zero: it steps off too.
                sigil.Leave(companion.CharId);
                companion.Sigil = null;
            }
            else if (instance != 0 && instance == sigil.RunId) {
                companion.Sigil = null;
                if (CanFollow(companion, zone, instance, online)) {
                    RemoveFromZone(companion);
                    Transfer(companion, zone, instance, leader.Location); // in with the player, from the sigil
                }

                return;
            }
            else if (now < sigil.EndsUtc.AddSeconds(20)) {
                return; // on the sigil until the countdown is over
            }
            else {
                sigil.Leave(companion.CharId);
                companion.Sigil = null;
            }
        }

        if (companion.SigilComing is { } coming && (now > coming.Group.EndsUtc || !coming.Group.IsMember(_leader))) {
            companion.SigilComing = null;
        }

        if (companion.SigilComing is not null) {
            return; // walking to the sigil
        }

        if (companion.Key != key) {
            FollowAcross(companion, leader, online, zone, instance, key, now);
            return;
        }

        companion.WaitingSince = null;
        companion.DoorTo = null;
        Follow(companion, leader, now);
    }

    /// <summary>Keeps the companion at its spot behind the player, walking only when it is off.</summary>
    private void Follow(Companion companion, Wizard leader, DateTime now) {
        var wizard = companion.Wizard;
        var heading = AmbientWizards.Heading(leader.Orientation.Z);
        var spot2 = DungeonManners.FollowSpot(new System.Numerics.Vector2(leader.Location.X, leader.Location.Y), heading, companion.Index);
        var spot = new Vector3(spot2.X, spot2.Y, leader.Location.Z);
        var distance = Distance(wizard.Position, leader.Location);
        companion.Far = distance > DungeonManners.FarDistance ? companion.Far ?? now : null;
        if ((companion.Unreachable is { } stuck && now - stuck >= DungeonManners.StuckLimit)
            || (companion.Far is { } far && now - far >= DungeonManners.FarLimit)) {
            // As a player teleports to a friend it lost: at most every two minutes, else it gives up.
            if (now - companion.LastPort >= GroupManners.PortEvery) {
                Logger.Information("Ambient wizard {Name} teleports to {Leader} (lost them).", Logger.Args(wizard.Name, _leader));
                companion.LastPort = now;
                companion.Unreachable = null;
                companion.Far = null;
                var (zone, instance) = (companion.ZonePath, companion.Instance);
                RemoveFromZone(companion);
                Transfer(companion, zone, instance, leader.Location);
            }
            else {
                GoHome(companion, DungeonLines.Leave);
            }

            return;
        }

        var goal = wizard.Moving ? wizard.Route.Count > 0 ? wizard.Route.Last() : wizard.Target ?? wizard.Position : wizard.Position;
        if (!DungeonManners.ShouldWalk(new System.Numerics.Vector2(goal.X, goal.Y), spot2)) {
            return;
        }

        if (WalkTo(companion, spot) || WalkTo(companion, leader.Location)) {
            companion.Unreachable = null;
        }
        else {
            companion.Unreachable ??= now;
        }
    }

    /// <summary>The player is in another zone (or instance): walk to the door they used, then follow them through.</summary>
    private void FollowAcross(Companion companion, Wizard leader, OnlinePlayer online, string zone, ulong instance, string key, DateTime now) {
        if (!CanFollow(companion, zone, instance, online)) {
            Wait(companion, now);
            return;
        }

        companion.WaitingSince = null;
        var wizard = companion.Wizard;
        if (companion.DoorTo != key) {
            companion.DoorTo = key;
            companion.DoorBy = now + DungeonManners.DoorFollowLimit;
            if (_leaderLastSeen.TryGetValue(companion.Key, out var door) && Distance(door, wizard.Position) > 150 && WalkTo(companion, door)) {
                return;
            }
        }

        if (wizard.Moving && now < companion.DoorBy) {
            return;
        }

        Logger.Debug("Ambient wizard {Name} follows {Leader} into {Zone} ({Instance}).", Logger.Args(wizard.Name, _leader, zone, instance));
        RemoveFromZone(companion);
        Transfer(companion, zone, instance, leader.Location);
    }

    /// <summary>It cannot go where the player is: it waits (and says so once), then gives up.</summary>
    private void Wait(Companion companion, DateTime now) {
        if (companion.WaitingSince is null) {
            companion.WaitingSince = now;
            if (companion.Present && companion == _companions.FirstOrDefault(c => c.Present)) {
                SayLater(companion, GroupLines.For(GroupLines.WaitOutside, Persona(companion), companion.Wizard.Identity.Seed + companion.Wizard.Turn++),
                    Voice.Whisper, TimeSpan.FromSeconds(4 + _rng.Next(5)));
            }

            return;
        }

        if (now - companion.WaitingSince.Value >= GroupManners.WaitOutside) {
            GoHome(companion, GroupLines.WaitedTooLong, Voice.Whisper);
        }
    }

    /// <summary>True when the companion may follow the player into <paramref name="zone"/> of <paramref name="instance"/>.</summary>
    private bool CanFollow(Companion companion, string zone, ulong instance, OnlinePlayer online) {
        if (!GroupManners.Followable(zone) || online.HousingDeedId != 0 || Game.Minigames.MinigameConfig.IsMinigameZone(zone ?? "")
            || Arena.ArenaMatchmaker.Instance?.IsRun(instance) == true || !ClassicGate.Decide(zone).Allowed) {
            return false;
        }

        if (instance == 0) {
            return true;
        }

        // An instance holds four, or its hard limit (a tower for one takes no companion); real players first, then the
        // companions in the order they joined.
        var key = KeyOf(zone, instance);
        var realPlayers = GroupInstances.CountIn(OnlinePlayerCollection.GetOnlinePlayers().Where(p => !AmbientWizards.IsAmbientChar(p.CharacterId)),
            p => p.CurrentZone, p => p.InstanceOwnerId, zone, instance);
        var ahead = _companions.Count(c => c != companion && (c.Key == key || (c.Transferring && c.Key == key)) && c.Joined <= companion.Joined);
        return Math.Max(1, realPlayers) + ahead + 1 <= DungeonManners.Capacity(online.ZoneHardLimit);
    }

    // ---- zones ----------------------------------------------------------------------------------

    /// <summary>Into <paramref name="zone"/> (the player's instance when <paramref name="instance"/> is not 0), near the player.</summary>
    private void Transfer(Companion companion, string zone, ulong instance, Vector3? near) {
        companion.ZonePath = zone;
        companion.Instance = instance;
        companion.Transferring = true;
        companion.TransferAt = DateTime.UtcNow;
        companion.ArriveNear = near;
        companion.DoorTo = null;
        companion.Unreachable = null;
        companion.Far = null;
        LoadNav(zone);
        _server.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone, DestinationLocation = "Start", SendToClient = false,
            OwnerCharId = instance != 0 ? instance : companion.CharId, IsPrivate = instance != 0, ResetInstance = false,
        }, companion.Wizard.Endpoint);
    }

    private void Arrived(Companion companion, ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP rsp) {
        if (!companion.Transferring) {
            return;
        }

        if (rsp.ErrorCode != 0 || rsp.ZoneActorRef is null) {
            companion.Transferring = false;
            companion.Failures++;
            Logger.Warning("Ambient wizard {Name} could not follow {Leader} into {Zone} ({Error}).",
                Logger.Args(companion.Wizard.Name, _leader, companion.ZonePath, rsp.ErrorMessage ?? rsp.ErrorCode.ToString()));
            if (companion.Failures >= 3) {
                GoHome(companion, GroupLines.WaitedTooLong, Voice.Whisper);
            }
            else {
                companion.AwayUntil = DateTime.UtcNow.AddSeconds(20);
            }

            return;
        }

        var wizard = companion.Wizard;
        companion.ZoneActor = rsp.ZoneActorRef;
        var at = companion.ArriveNear ?? rsp.Location;
        var angle = _rng.NextDouble() * Math.PI * 2;
        var spot = new Vector3(at.X + (float) Math.Cos(angle) * 110, at.Y + (float) Math.Sin(angle) * 110, at.Z);
        if (Nav(companion)?.Snap(Num(spot)) is { } ground) {
            spot = new Vector3(ground.X, ground.Y, ground.Z);
        }

        wizard.Zone = companion.ZonePath;
        wizard.ZoneActor = rsp.ZoneActorRef;
        wizard.ZoneDisplayName = rsp.ZoneDisplayName ?? "";
        wizard.Position = spot;
        wizard.Yaw = AmbientWizards.Heading(rsp.Orientation);
        var character = wizard.Wizard;
        character.Zone = companion.ZonePath;
        character.ZoneDisplayName = wizard.ZoneDisplayName;
        character.Location = spot;
        character.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        character.IsInDuel = false;
        var gameObject = WizardObjectLoader.GetPlayerGameObject(character);
        gameObject.m_nMobileID = rsp.MobileId;
        gameObject.m_location = spot;
        gameObject.m_orientation = character.Orientation;
        character.GameObject = gameObject;
        ActiveWizardDirectory.SetGameObject(wizard.Endpoint, gameObject);
        rsp.ZoneActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYER {
            PlayerActor = wizard.Endpoint, PlayerObject = gameObject, Wizard = character, ActualWizardName = wizard.Name,
        }, wizard.Endpoint);
    }

    private void Added(Companion companion) {
        if (companion.Present || !companion.Transferring) {
            return; // the zone and its player supervisor each answer MSG_ADDPLAYER
        }

        companion.Present = true;
        companion.Transferring = false;
        companion.Failures = 0;
        var wizard = companion.Wizard;
        wizard.Present = true;
        wizard.Activity = AmbientActivity.Grouped;
        SpawnFor(companion, null);
        OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            SessionId = 0, AccountId = wizard.CharId, CharacterId = wizard.CharId, CurrentZone = companion.ZonePath,
            CurrentZoneDisplayName = wizard.ZoneDisplayName, CurrentRealm = "Centaur", ActorPath = wizard.Endpoint.Path.ToString(),
            InstanceOwnerId = companion.Instance, // a friend teleporting to it lands in the same instance
        });
        if (companion.Defeated) {
            companion.Defeated = false;
            SayLater(companion, GroupLines.For(GroupLines.BackAfterDefeat, Persona(companion), wizard.Identity.Seed + wizard.Turn++),
                Voice.Near, TimeSpan.FromSeconds(2 + _rng.Next(3)));
        }

        Logger.Information("Ambient wizard {Name} is in {Zone} with {Leader}'s group.", Logger.Args(wizard.Name, companion.ZonePath, _leader));
        Self.Tell(new Refresh()); // its zone changed
    }

    private void SpawnFor(Companion companion, IActorRef player) {
        var wizard = companion.Wizard;
        var character = wizard.Wizard;
        character.Location = wizard.Position;
        character.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        var gameObject = WizardObjectLoader.GetPlayerGameObject(character);
        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_serializer.Serialize(gameObject, flags, out var data)) {
            return;
        }

        var spawn = new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = data };
        if (player is not null) {
            player.Tell(spawn);
            return;
        }

        companion.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = spawn, Selfless = true, Sender = wizard.Endpoint, Targets = ZoneBroadcastTarget.Players,
        });
    }

    private void RemoveFromZone(Companion companion) {
        var wizard = companion.Wizard;
        companion.Transferring = false;
        if (!companion.Present) {
            return;
        }

        companion.Present = false;
        wizard.Present = false;
        wizard.Moving = false;
        wizard.Leg = null;
        wizard.Route.Clear();
        wizard.Target = null;
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(wizard.CharId);
        companion.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            PlayerActor = wizard.Endpoint, GlobalId = wizard.Wizard.GameObjectID,
            MobileId = wizard.Wizard.GameObject.m_nMobileID, IsPlayerStillConnected = false,
        }, wizard.Endpoint);
    }

    /// <summary>Off the zone after <paramref name="after"/> (so its last line is read before it vanishes).</summary>
    private void RemoveFromZoneLater(Companion companion, TimeSpan after) {
        var wizard = companion.Wizard;
        var zone = companion.ZoneActor;
        companion.Transferring = false;
        if (!companion.Present || zone is null) {
            return;
        }

        companion.Present = false;
        wizard.Present = false;
        wizard.Moving = false;
        wizard.Leg = null;
        wizard.Route.Clear();
        wizard.Target = null;
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(wizard.CharId);
        Context.System.Scheduler.ScheduleTellOnce(after, zone, new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            PlayerActor = wizard.Endpoint, GlobalId = wizard.Wizard.GameObjectID,
            MobileId = wizard.Wizard.GameObject.m_nMobileID, IsPlayerStillConnected = false,
        }, wizard.Endpoint);
    }

    // ---- sigils ---------------------------------------------------------------------------------

    /// <summary>The player stepped on a dungeon sigil: the companions in that zone walk over and step on too.</summary>
    private void OnSigilNotice(AmbientSigilNotice notice) {
        if (_leaderKey is null) {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var companion in _companions.Where(c => c.Present && !c.Transferring && c.Key == _leaderKey && c.Sigil is null
                                                         && c.Wizard.Activity == AmbientActivity.Grouped)) {
            if (!notice.Group.IsOpen(now.AddSeconds(2))) {
                break;
            }

            companion.SigilComing = notice;
            var wizard = companion.Wizard;
            var pad = notice.Pad;
            var away = Distance(wizard.Position, pad);
            var rim = away < 1 ? pad
                : new Vector3(pad.X + (wizard.Position.X - pad.X) / away * 230, pad.Y + (wizard.Position.Y - pad.Y) / away * 230, pad.Z);
            var delay = TimeSpan.FromMilliseconds(500 + _rng.Next(900) + companion.Index * 400);
            Timers.StartSingleTimer($"sigil-{companion.CharId}", new Later(companion, c => {
                if (c.SigilComing != notice) {
                    return;
                }

                if (Distance(c.Wizard.Position, pad) <= 260 || !WalkTo(c, rim)) {
                    StepOnSigil(c, DateTime.UtcNow);
                }
            }), delay);
        }
    }

    private void StepOnSigil(Companion companion, DateTime now) {
        var notice = companion.SigilComing;
        companion.SigilComing = null;
        if (notice is null || !companion.Present || !notice.Group.IsMember(_leader)
            || notice.Group.Join(companion.CharId, now, ambient: true) is not { } ticket) {
            return;
        }

        var wizard = companion.Wizard;
        var entry = new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
            SigilLoc = Util.GetCompactStringFromVector(new Vector4(notice.Pad.X, notice.Pad.Y, notice.Pad.Z, notice.PadHeading)),
            SigilType = notice.SigilType, Slot = ticket.Slot,
        };
        if (ZoneService.TryGetSigilFaceSlot(entry, out var face, out var faceYaw)) {
            wizard.Position = face;
            wizard.Yaw = AmbientWizards.Heading(faceYaw);
            wizard.Wizard.Location = face;
            wizard.Wizard.Orientation = new Vector3(0, 0, faceYaw);
            Send(companion.ZoneActor, [new WIZARD_12_PROTOCOL.MSG_AGGRO {
                GlobalID = wizard.Wizard.GameObjectID, LocX = face.X, LocY = face.Y, LocZ = face.Z, Yaw = faceYaw,
            }]);
        }

        companion.Sigil = notice.Group;
        Logger.Information("Ambient wizard {Name} steps on the sigil with {Leader} (wizard {Slot}, run {Run}).",
            Logger.Args(wizard.Name, _leader, ticket.Slot + 1, ticket.RunId));
    }

    // ---- fights ---------------------------------------------------------------------------------

    private void OnDuelNotice(AmbientDuelNotice notice) {
        if (!notice.Active) {
            _duels.Remove(notice.SigilId);
            foreach (var companion in _companions.Where(c => c.Wizard.DuelSigil == notice.SigilId)) {
                var sigil = notice.SigilId;
                Timers.StartSingleTimer($"closed-{companion.CharId}", new Later(companion, c => {
                    if (c.Wizard.DuelSigil == sigil && c.Wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Helping) {
                        DuelOver(c, won: true);
                    }
                }), TimeSpan.FromSeconds(3));
            }

            return;
        }

        _duels[notice.SigilId] = notice;
        if (notice.Pvp || notice.FreePlayerSlots <= 0 || !notice.PlayerCharIds.Contains(_leader)
            || !ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)) {
            return;
        }

        // The group fights together: companions in the player's zone come in, the nearest first, as slots allow.
        var free = notice.FreePlayerSlots - _companions.Count(c => c.Wizard.DuelSigil == notice.SigilId && c.Wizard.Activity == AmbientActivity.Helping);
        foreach (var companion in _companions.Where(c => c.Present && !c.Transferring && c.Wizard.Activity == AmbientActivity.Grouped
                                                         && c.Sigil is null && c.Key == _leaderKey
                                                         && string.Equals(c.ZonePath, leader.Zone, StringComparison.OrdinalIgnoreCase)
                                                         && Distance(c.Wizard.Position, notice.Location) < DungeonManners.FarDistance)
                     .OrderBy(c => Distance(c.Wizard.Position, notice.Location)).ToList()) {
            if (free-- <= 0) {
                break;
            }

            var wizard = companion.Wizard;
            AmbientWizards.PermitJoin(wizard.Endpoint, notice.SigilId);
            wizard.DuelSigil = notice.SigilId;
            wizard.Activity = AmbientActivity.Helping;
            companion.SigilComing = null;
            companion.HelpBy = DateTime.UtcNow.AddSeconds(25);
            var location = notice.Location;
            Timers.StartSingleTimer($"help-{wizard.CharId}", new Later(companion, c => {
                if (c.Wizard.Activity == AmbientActivity.Helping && !WalkTo(c, location)) {
                    Fish(c);
                }
            }), TimeSpan.FromMilliseconds(400 + _rng.Next(1100)));
        }
    }

    private static void StopHelping(Companion companion) {
        AmbientWizards.RevokeJoin(companion.Wizard.Endpoint);
        companion.Wizard.DuelSigil = 0;
        companion.Wizard.Activity = AmbientActivity.Grouped;
    }

    private void InDuel(Companion companion, COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added) {
        var wizard = companion.Wizard;
        wizard.Activity = AmbientActivity.Fighting;
        wizard.Moving = false;
        wizard.Leg = null;
        wizard.Route.Clear();
        wizard.Target = null;
        wizard.DuelSigil = added.Duel?.SigilId ?? wizard.DuelSigil;
        wizard.Wizard.IsInDuel = true;
        wizard.Position = added.SlotPosition;
        wizard.Wizard.Location = added.SlotPosition;
        wizard.Yaw = AmbientWizards.Heading(added.SlotOrientation);
        wizard.Wizard.Orientation = new Vector3(0, 0, added.SlotOrientation);
        Send(companion.ZoneActor, [AmbientZone.Move(wizard), new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 }]);
    }

    private void DuelOver(Companion companion, bool won) {
        var wizard = companion.Wizard;
        if (wizard.Activity is not (AmbientActivity.Fighting or AmbientActivity.Helping)) {
            return;
        }

        var fought = wizard.Activity == AmbientActivity.Fighting;
        var stats = wizard.Wizard.GameStats;
        var defeated = !won && stats.m_currentHitpoints <= 0;
        stats.m_currentHitpoints = stats.m_baseHitpoints; // a potion between fights, off screen (as every ambient wizard)
        stats.m_currentMana = stats.m_baseMana;
        wizard.Wizard.IsInDuel = false;
        StopHelping(companion);
        Send(companion.ZoneActor, [
            new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = wizard.Wizard.GameObjectID, State = (uint) NPCStates.Idle },
            AmbientZone.Move(wizard),
        ]);
        if (defeated) {
            // 2010: a defeated wizard wakes up at the world's hub; a companion heals and comes back to the player.
            Say(wizard, GroupLines.For(GroupLines.Defeated, Persona(companion), wizard.Identity.Seed + wizard.Turn++), Voice.Near);
            RemoveFromZoneLater(companion, TimeSpan.FromSeconds(1.5));
            companion.Defeated = true;
            companion.AwayUntil = DateTime.UtcNow.AddSeconds(45 + _rng.Next(45));
            return;
        }

        var now = DateTime.UtcNow;
        if (won && fought && now >= _nextTalk && _rng.NextDouble() < 0.3
            && _companions.Where(c => c.Present && c.Wizard.Activity == AmbientActivity.Grouped).OrderBy(_ => _rng.Next()).FirstOrDefault() == companion) {
            _nextTalk = now.AddSeconds(90);
            var line = Persona(companion).Channel == ChatChannel.Menu ? null
                : AmbientChatBrain.AfterWin(ChatFor(companion), wizard.Turn++) ?? GroupLines.Pick(GroupLines.AfterWin, wizard.Identity.Seed + wizard.Turn++);
            SayLater(companion, line, Voice.Near, TimeSpan.FromSeconds(1.5 + _rng.NextDouble() * 2.5));
        }
    }

    // ---- messages -------------------------------------------------------------------------------

    private void OnInbox(AmbientInbox inbox) {
        var companion = _companions.FirstOrDefault(c => c.Wizard == inbox.Wizard);
        if (companion is null) {
            // No longer ours (it went home a moment ago): its home zone takes it.
            inbox.Wizard.Group?.Tell(inbox);
            return;
        }

        switch (inbox.Message) {
            case ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP rsp:
                Arrived(companion, rsp);
                break;
            case ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP:
                Added(companion);
                break;
            case ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE added when companion.Present && added.PlayerActor is not null
                                                                    && !AmbientWizards.IsAmbient(added.PlayerActor):
                SpawnFor(companion, added.PlayerActor);
                break;
            case COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added:
                InDuel(companion, added);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATWIN:
                DuelOver(companion, won: true);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEATH:
                DuelOver(companion, won: false);
                break;
            case GAME_5_PROTOCOL.MSG_RADIALCHAT say:
                Heard(Wizard.TryGetCharacterId(say.SourceID, out var who) ? who : 0, AmbientChat.Text((byte[]) say.Message), Voice.Near, null);
                break;
            case GAME_5_PROTOCOL.MSG_DIRECTEDCHAT text:
                Heard(text.SourceID, (string) text.Message ?? "", Voice.Whisper, companion);
                break;
            case CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD:
            case CHARACTER_103_PROTOCOL.MSG_BUDDYDROPFWD:
            case GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE:
                inbox.Wizard.Group?.Tell(inbox); // friends are its home zone's business
                break;
        }
    }

    // ---- talk -----------------------------------------------------------------------------------

    /// <summary>A line from the player: goodbye, thanks, or talk to a companion (by name, whisper or group channel).</summary>
    private void Heard(ulong speaker, string text, Voice voice, Companion whispered) {
        if (speaker != _leader || string.IsNullOrWhiteSpace(text)) {
            return;
        }

        var now = DateTime.UtcNow;
        _stillSince = now; // talking is not being away
        _askedAfk = false;
        if (GroupManners.ParseCall(text).Kind != RecruitKind.None && voice != Voice.Whisper) {
            return; // "anyone else wanna come?": for the strangers around (AmbientZone.Groups), not the group
        }

        if (voice != Voice.Whisper) {
            // A Say reaches every companion's endpoint in the zone: act on it once.
            if (_lastHeard.Text == text && now - _lastHeard.At < TimeSpan.FromSeconds(2)) {
                return;
            }

            _lastHeard = (text, now);
            if (voice == Voice.Near && ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)
                && !_companions.Any(c => c.Present && string.Equals(c.ZonePath, leader.Zone, StringComparison.OrdinalIgnoreCase)
                                         && Distance(c.Wizard.Position, leader.Location) <= HelpManners.HearingDistance)) {
                return;
            }
        }

        var named = _companions.Where(c => c == whispered || AmbientChatBrain.Mentions(text, FirstName(c))).ToList();
        var dismiss = GroupManners.ParseDismiss(text);
        var inDuel = ActiveWizardDirectory.TryGetByCharId(_leader, out var me) && me.IsInDuel;
        var together = _companions.Count == 0 ? TimeSpan.Zero : now - _companions.Min(c => c.Joined);
        if (dismiss == DismissKind.Bye || (dismiss == DismissKind.Thanks && GroupManners.ThanksEnds(inDuel, together))) {
            var going = named.Count > 0 ? named : [.. _companions];
            Logger.Information("Ambient group of {Leader}: \"{Text}\": {Count} companion(s) leave.", Logger.Args(_leader, text, going.Count));
            var i = 0;
            foreach (var companion in going) {
                var line = i++ < 2 ? GroupLines.For(GroupLines.ByeBack, Persona(companion), companion.Wizard.Identity.Seed + companion.Wizard.Turn++) : null;
                var wait = ChatTiming.Answer(text, line ?? "bye", Persona(companion), busy: false, _rng) + TimeSpan.FromSeconds(i * 1.5);
                Timers.StartSingleTimer($"bye-{companion.CharId}", new Later(companion, c => GoHome(c, line, voice)), wait);
            }

            return;
        }

        if (dismiss == DismissKind.Thanks) {
            var who = named.FirstOrDefault() ?? (_rng.NextDouble() < 0.4 ? _companions.FirstOrDefault(c => c.Present) : null);
            if (who is not null) {
                var line = GroupLines.For(GroupLines.ThanksBack, Persona(who), who.Wizard.Identity.Seed + who.Wizard.Turn++);
                SayLater(who, line, voice, ChatTiming.Answer(text, line ?? "np", Persona(who), inDuel, _rng));
            }

            return;
        }

        // Talk: the companion named (or whispered), or on the group channel or an open question, now and then one of them.
        var answerer = named.FirstOrDefault()
                       ?? ((voice == Voice.Group || AmbientChatBrain.IsOpenCall(text)) && _rng.NextDouble() < 0.5
                           ? _companions.Where(c => c.Present || voice == Voice.Group).OrderBy(_ => _rng.Next()).FirstOrDefault() : null);
        if (answerer is null || Persona(answerer).Channel == ChatChannel.Menu || !answerer.Wizard.Limiter.TryTake(now, _leader)) {
            return;
        }

        var reply = AmbientChatBrain.Reply(text, ChatFor(answerer), direct: true, answerer.Wizard.Turn++);
        if (reply is null) {
            return;
        }

        var styled = ChatStyle.Apply(reply, Persona(answerer), _rng, ChatWordFilter.Current);
        var busy = answerer.Wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Helping;
        SayLater(answerer, styled, voice, ChatTiming.Answer(text, styled, Persona(answerer), busy, _rng), styledAlready: true);
    }

    /// <summary>Small talk now and then between fights (AmbientChatBrain.Idle).</summary>
    private void IdleTalk(DateTime now) {
        _nextTalk = now.AddMinutes(4 + _rng.Next(5));
        var talker = _companions.Where(c => c.Present && c.Wizard.Activity == AmbientActivity.Grouped && Persona(c).Channel != ChatChannel.Menu)
            .OrderBy(_ => _rng.Next()).FirstOrDefault();
        if (talker is null || AmbientChatBrain.Idle(ChatFor(talker), talker.Wizard.Turn++) is not { } line) {
            return;
        }

        Say(talker.Wizard, ChatStyle.Apply(line, Persona(talker), _rng, ChatWordFilter.Current), Voice.Near, styledAlready: true);
    }

    private ChatContext ChatFor(Companion companion) {
        var wizard = companion.Wizard;
        var facts = AmbientKnowledge.Facts(_leader);
        return new ChatContext(wizard.Name, wizard.Identity.School, wizard.Wizard.MagicSchoolBehavior.Level,
            AmbientKnowledge.ZoneName(companion.ZonePath) ?? "", facts.Name, facts.Zone, facts.Quest, wizard.FriendOf(_leader),
            AmbientKnowledge.WhereIs, DateTime.UtcNow, ZoneKey: companion.ZonePath, Hour: DateTime.Now.Hour, History: _heard,
            Audience: [_leader]);
    }

    private static ChatPersona Persona(Companion companion) => ChatPersona.For(companion.Wizard.Identity);

    private static string FirstName(Companion companion)
        => companion.Wizard.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private void SayLater(Companion companion, string line, Voice voice, TimeSpan after, bool styledAlready = false) {
        if (string.IsNullOrEmpty(line)) {
            return;
        }

        Timers.StartSingleTimer($"say-{companion.CharId}-{++_seq}", new Later(companion, c => Say(c.Wizard, line, voice, styledAlready)),
            after);
    }

    /// <summary>
    /// The companion says <paramref name="line"/>: out loud when the player is near enough to hear it, on the group
    /// channel when the player used it (and the group window is on), else as a whisper. Nothing with chat off.
    /// </summary>
    private void Say(AmbientWizard wizard, string line, Voice voice, bool styledAlready = false) {
        if (string.IsNullOrEmpty(line) || !AmbientWizards.Settings.Chat) {
            return;
        }

        var text = styledAlready ? line : ChatStyle.Apply(line, ChatPersona.For(wizard.Identity), _rng, ChatWordFilter.Current);
        if (voice == Voice.Group && AmbientGroups.Settings.Window && _windowShown && ToLeader(new GAME_5_PROTOCOL.MSG_CHANNELCHAT {
                SourceName = AmbientChat.NameBytes(wizard.Wizard), SourceID = wizard.CharId, Message = text, TargetID = _channel,
                Filter = 0, Flags = GroupChatFlags,
            })) {
            Logger.Debug("[Group {Leader}] {Name} (ambient): {Text}", Logger.Args(_leader, wizard.Name, text));
            return;
        }

        var near = wizard.Present && ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)
                   && string.Equals(leader.Zone, wizard.Zone, StringComparison.OrdinalIgnoreCase)
                   && Distance(leader.Location, wizard.Position) <= HelpManners.HearingDistance;
        if (voice != Voice.Whisper && near) {
            AmbientChat.Say(wizard, text);
        }
        else {
            AmbientChat.Whisper(wizard, _leader, text);
        }
    }

    // ---- the client's group window -----------------------------------------------------------------

    /// <summary>The group-chat flags upstream Imlight sends on MSG_CHANNELCHAT for a group channel.</summary>
    private const uint GroupChatFlags = 0x50;

    private bool ToLeader(IMessage message) {
        if (OnlinePlayerCollection.GetOnlinePlayer(_leader) is not { ActorPath: { Length: > 0 } path }) {
            return false;
        }

        Context.ActorSelection(path).Tell(message);
        return true;
    }

    /// <summary>The whole group as the client's group window shows it (sent again on every change and zone).</summary>
    private void ShowWindow() {
        if (!AmbientGroups.Settings.Window || _over || _companions.Count == 0) {
            return;
        }

        var size = (uint) (1 + _companions.Count);
        if (!ToLeader(new GAME_5_PROTOCOL.MSG_PARTYJOINNOTIFICATION {
                DestinationCharacterID = _leader, ChannelID = _channel, PartyID = _party, PartyTotalSize = size, FromAdventureParty = 0,
            })) {
            return;
        }

        foreach (var companion in _companions) {
            var wizard = companion.Wizard;
            ToLeader(new GAME_5_PROTOCOL.MSG_PARTYUPDATE {
                DestinationCharacterID = _leader,
                PlayerNameBlob = AmbientChat.NameBytes(wizard.Wizard),
                CharacterID = wizard.CharId,
                GlobalID = wizard.Wizard.GameObjectID,
                SchoolID = GroupManners.SocialSchool(wizard.Identity.School),
                Level = (uint) wizard.Wizard.MagicSchoolBehavior.Level,
                ZoneDisplayName = wizard.ZoneDisplayName ?? "",
                HasFilteredChat = 0,
                PartyTotalSize = size,
                FromAdventureParty = 0,
                SigilSlot = 0,
                LeaderGID = _leader,
                QuestGID = 0,
                GoalGID = 0,
            });
        }

        ToLeader(new WIZARD3_56_PROTOCOL.MSG_CHANGEGROUPLEADER { PlayerGID = _leader, RequestingPlayerGID = _leader });
        _windowShown = true;
    }

    private void WindowLeft(ulong charId) {
        if (!AmbientGroups.Settings.Window || !_windowShown) {
            return;
        }

        if (_companions.Count == 0) {
            ToLeader(new GAME_5_PROTOCOL.MSG_PARTYDISBAND { DestinationCharacterID = _leader });
            _windowShown = false;
            return;
        }

        ToLeader(new GAME_5_PROTOCOL.MSG_PARTYLEAVENOTIFICATION {
            DestinationCharacterID = _leader, CharacterID = charId, PartyTotalSize = (uint) (1 + _companions.Count), FromAdventureParty = 0,
        });
    }

    // ---- leaving --------------------------------------------------------------------------------

    /// <summary>Everyone goes; the first two say a line from <paramref name="lines"/> (none when null).</summary>
    private void EndGroup(System.Collections.Immutable.ImmutableArray<string>? lines, bool quick) {
        var i = 0;
        foreach (var companion in _companions.ToList()) {
            var line = lines is { } pool && i++ < 2 ? GroupLines.For(pool, Persona(companion), companion.Wizard.Identity.Seed + companion.Wizard.Turn++) : null;
            if (quick || line is null) {
                GoHome(companion, line);
            }
            else {
                Timers.StartSingleTimer($"end-{companion.CharId}", new Later(companion, c => GoHome(c, line)),
                    TimeSpan.FromSeconds(1 + i * 2 + _rng.NextDouble() * 2));
            }
        }
    }

    private void GoHome(Companion companion, System.Collections.Immutable.ImmutableArray<string> lines, Voice voice = Voice.Near)
        => GoHome(companion, GroupLines.For(lines, Persona(companion), companion.Wizard.Identity.Seed + companion.Wizard.Turn++), voice);

    /// <summary>The companion says <paramref name="line"/> (if any), leaves the group and goes back to its home street.</summary>
    private void GoHome(Companion companion, string line, Voice voice = Voice.Near) {
        if (!_companions.Remove(companion)) {
            return;
        }

        var wizard = companion.Wizard;
        Timers.Cancel($"sigil-{companion.CharId}");
        Timers.Cancel($"help-{companion.CharId}");
        AmbientWizards.RevokeJoin(wizard.Endpoint);
        companion.Sigil?.Leave(companion.CharId);
        if (line is not null && (companion.Present || voice != Voice.Near)) {
            Say(wizard, line, voice);
        }

        var inHome = companion.Present && companion.Instance == 0 && !wizard.Wizard.IsInDuel
                     && string.Equals(companion.ZonePath, wizard.Record.HomeZone, StringComparison.OrdinalIgnoreCase);
        wizard.Driver = null;
        wizard.DuelSigil = 0;
        if (inHome) {
            // Still in its own street: it just carries on with its day from where it stands.
            if (wizard.Moving) {
                wizard.Leg = null;
                wizard.Route.Clear();
                wizard.Target = null;
                wizard.Moving = false;
                Send(companion.ZoneActor, [AmbientZone.Move(wizard), new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 }]);
            }

            wizard.Group?.Tell(new AmbientCompanionBack(wizard, companion.ZoneActor, TimeSpan.Zero));
        }
        else {
            RemoveFromZoneLater(companion, TimeSpan.FromMilliseconds(line is null ? 0 : 1500));
            var after = companion.AwayUntil is { } away && away > DateTime.UtcNow ? away - DateTime.UtcNow : TimeSpan.FromSeconds(4 + _rng.Next(5));
            wizard.Group?.Tell(new AmbientCompanionBack(wizard, null, after));
        }

        Logger.Information("Ambient wizard {Name} left {Leader}'s group{Home}.", Logger.Args(wizard.Name, _leader, inHome ? " (stays in its street)" : ""));
        MembersChanged();
        WindowLeft(wizard.CharId);
        if (_companions.Count == 0 && !_over) {
            _over = true;
            AmbientGroups.Unregister(_leader, Self, _channel);
            Timers.StartSingleTimer("stop", PoisonPill.Instance, TimeSpan.FromSeconds(3));
        }
    }

    // ---- walking --------------------------------------------------------------------------------

    private void LoadNav(string zone) {
        if (string.IsNullOrEmpty(zone) || !_navAsked.Add(zone)) {
            return;
        }

        var self = Self;
        AmbientNav.ForZone(zone).ContinueWith(task => {
            if (task.IsCompletedSuccessfully && task.Result is { } grid) {
                self.Tell(new NavReady(zone, grid));
            }
        }, TaskScheduler.Default);
    }

    private NavGrid Nav(Companion companion) => companion.ZonePath is { } zone && _navs.TryGetValue(zone, out var grid) ? grid : null;

    /// <summary>A walk on the zone's walkable ground (a straight line while its grid loads); false when none reaches it.</summary>
    private bool WalkTo(Companion companion, Vector3 target) {
        var wizard = companion.Wizard;
        var nav = Nav(companion);
        var route = new List<Vector3>();
        if (nav is null) {
            route.Add(target);
        }
        else if (nav.TryRoute(Num(wizard.Position), Num(target), out var waypoints) && waypoints.Count > 0) {
            route.AddRange(waypoints.Select(p => new Vector3(p.X, p.Y, p.Z)));
        }
        else {
            return false;
        }

        if (wizard.Leg is { } leg) {
            var at = leg.At(DateTime.UtcNow);
            wizard.Position = new Vector3(at.X, at.Y, at.Z);
            wizard.Leg = null;
        }

        wizard.Route.Clear();
        foreach (var point in route.Skip(1)) {
            wizard.Route.Enqueue(point);
        }

        wizard.Target = route[0];
        wizard.Moving = true;
        return true;
    }

    private void Face(Companion companion, Vector3 at) {
        var wizard = companion.Wizard;
        if (Distance(at, wizard.Position) < 1 || wizard.Moving) {
            return;
        }

        wizard.Yaw = MathF.Atan2(at.Y - wizard.Position.Y, at.X - wizard.Position.X);
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        Send(companion.ZoneActor, [AmbientZone.Move(wizard)]);
    }

    /// <summary>A move fished to the zone at the companion's spot, as MoveService does for a player (a duel circle hears it).</summary>
    private static void Fish(Companion companion) {
        var wizard = companion.Wizard;
        wizard.Wizard.IsInCombatGrace = false;
        wizard.Wizard.GameObject.m_location = wizard.Position;
        companion.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE {
            PlayerObject = wizard.Wizard.GameObject, PlayerActor = wizard.Endpoint, PlayerWizard = wizard.Wizard,
        }, wizard.Endpoint);
    }

    private static System.Numerics.Vector3 Num(Vector3 v) => new(v.X, v.Y, v.Z);

    private static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

}
