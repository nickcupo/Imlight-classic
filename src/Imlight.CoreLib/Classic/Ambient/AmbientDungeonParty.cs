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
 * AMBIENT DUNGEON PARTY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): the ambient wizards in one dungeon run (a sigil
 * run's instance), from the moment they leave the street with a player
 * until they come back. One actor per run, a child of the AmbientZone the
 * helpers live in; it is their Driver (their endpoints send it their
 * messages) and the run's zones tell it about duels.
 *   - In: each helper enters the run's instance like a player (a private
 *     zone transfer keyed by the run id, then MSG_ADDPLAYER), a moment
 *     after the player, and spawns for the players there.
 *   - Following: each keeps its own spot a few steps behind the leader (a
 *     real player of the run), walking only when the leader has moved off,
 *     so it never runs ahead, never pulls a mob (a creature may not engage
 *     an ambient wizard without a permit) and waits in rooms and at doors.
 *     It runs at a player's pace, a step every 100 ms (as AmbientZone).
 *   - Doors: when the leader goes through a door into another zone of the
 *     run, the helper walks to where the leader was last seen (the door),
 *     then follows into that zone of the same instance, behind the leader.
 *   - Fights: when a duel with a player of the run starts in its zone and
 *     has a free slot, the helper (permitted for that duel only) walks in
 *     and plays its turns (AllyBrain); a real player who reaches a full
 *     circle still takes its slot. Rewards and quest credit go to each
 *     player's own session; ambient wizards have none.
 *   - Out: when no real player of the run is left inside (the dungeon is
 *     done or the player left) each says thanks ("ty for the group") and
 *     goes back to the street at the sigil; a helper stuck (no route to the
 *     leader 25 s), lost (far for a minute), defeated (2009: back to the
 *     commons) or told to leave goes on its own, and one leaves when real
 *     players fill the instance (a friend teleporting in). Never more than
 *     75 minutes.
 *
 * USAGE EXAMPLE:
 * var party = Context.ActorOf(AmbientDungeonParty.Props(notice, hardLimit, server));
 * party.Tell(new AmbientDungeonParty.Take(wizard));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>The ambient wizards in one dungeon run (see the file header).</summary>
internal sealed class AmbientDungeonParty : ReceiveActor, IWithTimers {

    /// <summary>A helper the party takes in (it has left the street).</summary>
    internal sealed record Take(AmbientWizard Wizard);

    private sealed record Step;
    private sealed record Later(Helper Helper, Action<Helper> Action);
    private sealed record NavReady(string Zone, NavGrid Grid);

    private sealed class Helper(AmbientWizard wizard, int index, DateTime joined) {
        public AmbientWizard Wizard { get; } = wizard;
        public int Index { get; } = index;
        public DateTime Joined { get; } = joined;
        public string ZonePath { get; set; }
        public IActorRef ZoneActor { get; set; }
        public bool Present { get; set; }
        public bool Transferring { get; set; }
        public string DoorTo { get; set; }
        public DateTime DoorBy { get; set; }
        public DateTime? Unreachable { get; set; }
        public DateTime? Far { get; set; }
        public DateTime HelpBy { get; set; }
        public DateTime NextFish { get; set; }
        public Vector3? ArriveNear { get; set; }
    }

    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(100);
    private const int DecideEvery = 3; // steps (300 ms)

    private readonly AmbientSigilNotice _notice;
    private readonly ulong _runId;
    private readonly int _hardLimit;
    private readonly IActorRef _server;
    private readonly List<Helper> _helpers = [];
    private readonly Dictionary<string, NavGrid> _navs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _navAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> _leaderLastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, AmbientDuelNotice> _duels = [];
    private readonly HashSet<IActorRef> _zones = [];
    private readonly Random _rng;
    private readonly CoreObjectSerializer _serializer = new(versionable: false, behaviors: SerializerFlags.None);
    private ulong _leader;
    private DateTime? _nobodyInside;
    private int _steps;
    private int _nextIndex;

    public ITimerScheduler Timers { get; set; }

    public AmbientDungeonParty(AmbientSigilNotice notice, int hardLimit, IActorRef server) {
        _notice = notice;
        _runId = notice.Group.RunId;
        _hardLimit = hardLimit;
        _server = server;
        _leader = notice.PlayerCharId;
        _rng = new Random(unchecked((int) _runId));
        Receive<Take>(OnTake);
        Receive<Step>(_ => OnStep());
        Receive<Later>(later => {
            if (_helpers.Contains(later.Helper)) {
                later.Action(later.Helper);
            }
        });
        Receive<NavReady>(ready => _navs[ready.Zone] = ready.Grid);
        Receive<AmbientInbox>(OnInbox);
        Receive<AmbientDuelNotice>(OnDuelNotice);
    }

    public static Props Props(AmbientSigilNotice notice, int hardLimit, IActorRef server)
        => Akka.Actor.Props.Create(() => new AmbientDungeonParty(notice, hardLimit, server));

    protected override void PreStart() => Timers.StartPeriodicTimer("step", new Step(), StepInterval);

    protected override void PostStop() {
        foreach (var helper in _helpers.ToList()) {
            RemoveFromZone(helper);
            AmbientWizards.RevokeJoin(helper.Wizard.Endpoint);
        }

        foreach (var zone in _zones) {
            AmbientWizards.ClearGroup(zone, Self);
        }

        base.PostStop();
    }

    // ---- coming in ------------------------------------------------------------------------------

    private void OnTake(Take take) {
        var wizard = take.Wizard;
        var helper = new Helper(wizard, _nextIndex++, DateTime.UtcNow);
        _helpers.Add(helper);
        wizard.Activity = AmbientActivity.Dungeon;
        wizard.Moving = false;
        wizard.Leg = null;
        wizard.Route.Clear();
        wizard.Target = null;
        // A moment after the player, as the next wizard on the sigil arrives.
        Timers.StartSingleTimer($"in-{wizard.CharId}", new Later(helper, h => Transfer(h, _notice.DestinationZone,
            _notice.DestinationLoc, null)), TimeSpan.FromMilliseconds(400 + _rng.Next(1400)));
    }

    /// <summary>Sends the helper into <paramref name="zone"/> of the run's instance (like a player's private transfer).</summary>
    private void Transfer(Helper helper, string zone, string location, Vector3? near) {
        helper.ZonePath = zone;
        helper.Transferring = true;
        helper.ArriveNear = near;
        helper.DoorTo = null;
        LoadNav(zone);
        _server.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone, DestinationLocation = string.IsNullOrEmpty(location) ? "Start" : location,
            SendToClient = false, OwnerCharId = _runId, IsPrivate = true, ResetInstance = false,
        }, helper.Wizard.Endpoint);
    }

    private void Arrived(Helper helper, ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP rsp) {
        if (rsp.ErrorCode != 0 || rsp.ZoneActorRef is null) {
            Logger.Warning("Ambient wizard {Name} could not enter {Zone} of run {Run} ({Error}).",
                Logger.Args(helper.Wizard.Name, helper.ZonePath, _runId, rsp.ErrorMessage ?? rsp.ErrorCode.ToString()));
            GoHome(helper, null, TimeSpan.FromSeconds(5));
            return;
        }

        var wizard = helper.Wizard;
        helper.ZoneActor = rsp.ZoneActorRef;
        if (_zones.Add(rsp.ZoneActorRef)) {
            AmbientWizards.SetGroup(rsp.ZoneActorRef, Self); // the run's duels in this zone come here
        }

        var at = helper.ArriveNear ?? rsp.Location;
        var angle = _rng.NextDouble() * Math.PI * 2;
        var spot = new Vector3(at.X + (float) Math.Cos(angle) * 90, at.Y + (float) Math.Sin(angle) * 90, at.Z);
        if (Nav(helper)?.Snap(Num(spot)) is { } ground) {
            spot = new Vector3(ground.X, ground.Y, ground.Z);
        }

        wizard.Zone = helper.ZonePath;
        wizard.ZoneActor = rsp.ZoneActorRef;
        wizard.ZoneDisplayName = rsp.ZoneDisplayName ?? "";
        wizard.Position = spot;
        wizard.Yaw = AmbientWizards.Heading(rsp.Orientation);
        var character = wizard.Wizard;
        character.Zone = helper.ZonePath;
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

    private void Added(Helper helper) {
        if (helper.Present) {
            return; // the zone and its player supervisor each answer MSG_ADDPLAYER
        }

        helper.Present = true;
        helper.Transferring = false;
        helper.Unreachable = null;
        helper.Far = null;
        var wizard = helper.Wizard;
        wizard.Present = true;
        wizard.Activity = AmbientActivity.Dungeon;
        SpawnFor(helper, null);
        OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            SessionId = 0, AccountId = wizard.CharId, CharacterId = wizard.CharId, CurrentZone = helper.ZonePath,
            CurrentZoneDisplayName = wizard.ZoneDisplayName, CurrentRealm = "Centaur", ActorPath = wizard.Endpoint.Path.ToString(),
        });
        Logger.Information("Ambient wizard {Name} is in {Zone} with run {Run}.", Logger.Args(wizard.Name, helper.ZonePath, _runId));
    }

    private void SpawnFor(Helper helper, IActorRef player) {
        var wizard = helper.Wizard;
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

        helper.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = spawn, Selfless = true, Sender = wizard.Endpoint, Targets = ZoneBroadcastTarget.Players,
        });
    }

    // ---- the step -------------------------------------------------------------------------------

    private void OnStep() {
        var now = DateTime.UtcNow;
        var moves = new Dictionary<IActorRef, List<IMessage>>();
        foreach (var helper in _helpers.Where(h => h.Present && h.Wizard.Moving)) {
            Walk(helper, now, moves);
        }

        foreach (var (zone, batch) in moves) {
            Send(zone, batch);
        }

        if (++_steps % DecideEvery == 0) {
            Decide(now);
        }
    }

    /// <summary>One 100 ms step of a helper's run: the spot 100 ms ahead (a player's move pace), corners, the stop.</summary>
    private void Walk(Helper helper, DateTime now, Dictionary<IActorRef, List<IMessage>> moves) {
        var wizard = helper.Wizard;
        if (!moves.TryGetValue(helper.ZoneActor, out var batch)) {
            moves[helper.ZoneActor] = batch = [];
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
                Stopped(helper, now);
                return;
            }

            var started = WalkLeg.Begin(Num(wizard.Position), Num(next), now, AmbientNav.RunSpeed);
            wizard.Leg = started;
            wizard.Yaw = started.Heading;
        }

        var current = wizard.Leg.Value;
        var here = current.At(now);
        var floor = Nav(helper)?.FloorAt(here);
        wizard.Position = new Vector3(here.X, here.Y, floor ?? here.Z);
        wizard.Wizard.Location = wizard.Position;
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        var ahead = current.At(now + StepInterval);
        batch.Add(AmbientZone.Move(wizard, new Vector3(ahead.X, ahead.Y, ahead.Z)));
    }

    /// <summary>The helper got where it was going.</summary>
    private void Stopped(Helper helper, DateTime now) {
        var wizard = helper.Wizard;
        if (wizard.Activity == AmbientActivity.Helping) {
            Fish(helper); // at the circle: the duel takes it (with its permit) if a slot is free
            helper.NextFish = now.AddSeconds(1);
        }
        else if (_leader != 0 && ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)) {
            Face(helper, leader.Location);
        }
    }

    private void Send(IActorRef zone, List<IMessage> batch) {
        if (batch.Count > 0 && zone is not null) {
            zone.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_CLIENTBATCH { Messages = [.. batch] }],
                Targets = ZoneBroadcastTarget.Players,
                Sender = Self,
            });
        }
    }

    // ---- deciding -------------------------------------------------------------------------------

    private void Decide(DateTime now) {
        // The run's real players inside (any of its zones).
        var inside = OnlinePlayerCollection.GetOnlinePlayers()
            .Where(p => p.InstanceOwnerId == _runId && !AmbientWizards.IsAmbientChar(p.CharacterId)).ToList();
        if (inside.Count == 0) {
            _nobodyInside ??= now;
            if (now - _nobodyInside.Value > TimeSpan.FromSeconds(6)) {
                EndRun("the players left");
            }

            return;
        }

        _nobodyInside = null;
        if (inside.All(p => p.CharacterId != _leader)) {
            _leader = inside[0].CharacterId; // the leader left; follow another player of the run
        }

        // Real players come first: when they fill the instance, the newest helper goes.
        var present = _helpers.Count(h => !h.Transferring || h.Present);
        if (inside.Count + present > DungeonManners.Capacity(_hardLimit) && _helpers.LastOrDefault() is { } newest) {
            GoHome(newest, DungeonLines.Pick(DungeonLines.MakeRoom, newest.Wizard.Identity.Seed), TimeSpan.FromSeconds(4));
            return;
        }

        if (!ActiveWizardDirectory.TryGetByCharId(_leader, out var leader) || leader.Zone is null) {
            return;
        }

        var leaderZone = inside.First(p => p.CharacterId == _leader).CurrentZone ?? leader.Zone;
        _leaderLastSeen[leaderZone] = leader.Location;
        foreach (var helper in _helpers.ToList()) {
            if (!helper.Present || helper.Transferring) {
                continue;
            }

            var wizard = helper.Wizard;
            var reason = DungeonManners.GiveUpReason(Since(helper.Unreachable, now), Since(helper.Far, now), now - helper.Joined);
            if (reason is not null) {
                Logger.Information("Ambient wizard {Name} leaves run {Run}: {Reason}.", Logger.Args(wizard.Name, _runId, reason));
                GoHome(helper, DungeonLines.Pick(DungeonLines.Leave, wizard.Identity.Seed + wizard.Turn++), TimeSpan.FromSeconds(5));
                continue;
            }

            switch (wizard.Activity) {
                case AmbientActivity.Fighting:
                    continue;
                case AmbientActivity.Helping:
                    if (now > helper.HelpBy) {
                        StopHelping(helper); // no seat in time: back to following
                    }
                    else if (!wizard.Moving && now >= helper.NextFish) {
                        Fish(helper);
                        helper.NextFish = now.AddSeconds(1);
                    }

                    continue;
            }

            if (!string.Equals(leaderZone, helper.ZonePath, StringComparison.OrdinalIgnoreCase)) {
                FollowThroughDoor(helper, leaderZone, leader.Location, now);
                continue;
            }

            Follow(helper, leader, now);
        }
    }

    private static TimeSpan Since(DateTime? at, DateTime now) => at is { } t ? now - t : TimeSpan.Zero;

    /// <summary>Keeps the helper at its spot behind the leader (DungeonManners.FollowSpot), walking only when it is off.</summary>
    private void Follow(Helper helper, Wizard leader, DateTime now) {
        var wizard = helper.Wizard;
        var heading = AmbientWizards.Heading(leader.Orientation.Z);
        var spot2 = DungeonManners.FollowSpot(new System.Numerics.Vector2(leader.Location.X, leader.Location.Y), heading, helper.Index);
        var spot = new Vector3(spot2.X, spot2.Y, leader.Location.Z);
        var distance = Distance(wizard.Position, leader.Location);
        helper.Far = distance > DungeonManners.FarDistance ? helper.Far ?? now : null;
        var goal = wizard.Moving ? wizard.Route.Count > 0 ? wizard.Route.Last() : wizard.Target ?? wizard.Position : wizard.Position;
        if (!DungeonManners.ShouldWalk(new System.Numerics.Vector2(goal.X, goal.Y), spot2)) {
            return; // where it stands (or is going) is still a fine spot
        }

        if (WalkTo(helper, spot)) {
            helper.Unreachable = null;
        }
        else if (WalkTo(helper, leader.Location)) {
            helper.Unreachable = null; // its own spot is in a wall: next to the leader then
        }
        else {
            helper.Unreachable ??= now;
        }
    }

    /// <summary>The leader went into another zone of the run: walk to the door, then follow through it.</summary>
    private void FollowThroughDoor(Helper helper, string zone, Vector3 leaderAt, DateTime now) {
        var wizard = helper.Wizard;
        if (helper.DoorTo is null || !string.Equals(helper.DoorTo, zone, StringComparison.OrdinalIgnoreCase)) {
            helper.DoorTo = zone;
            helper.DoorBy = now + DungeonManners.DoorFollowLimit;
            if (_leaderLastSeen.TryGetValue(helper.ZonePath, out var door)) {
                WalkTo(helper, door);
            }

            return;
        }

        if (wizard.Moving && now < helper.DoorBy) {
            return;
        }

        // Through the door: into the leader's zone of the same instance, behind the leader.
        Logger.Debug("Ambient wizard {Name} follows into {Zone} (run {Run}).", Logger.Args(wizard.Name, zone, _runId));
        RemoveFromZone(helper);
        Transfer(helper, zone, "Start", leaderAt);
    }

    // ---- fights ---------------------------------------------------------------------------------

    private void OnDuelNotice(AmbientDuelNotice notice) {
        if (!notice.Active) {
            _duels.Remove(notice.SigilId);
            foreach (var helper in _helpers.Where(h => h.Wizard.DuelSigil == notice.SigilId)) {
                var sigil = notice.SigilId;
                Timers.StartSingleTimer($"closed-{helper.Wizard.CharId}", new Later(helper, h => {
                    if (h.Wizard.DuelSigil == sigil && h.Wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Helping) {
                        DuelOver(h, won: true);
                    }
                }), TimeSpan.FromSeconds(3));
            }

            return;
        }

        _duels[notice.SigilId] = notice;
        if (notice.Pvp || notice.FreePlayerSlots <= 0) {
            return;
        }

        // A player of the run is in it: helpers in that zone (and not busy) come and help, a few at most.
        var ours = notice.PlayerCharIds.Where(id => OnlinePlayerCollection.GetOnlinePlayer(id) is { } p && p.InstanceOwnerId == _runId)
            .ToList();
        if (ours.Count == 0 || !ActiveWizardDirectory.TryGetByCharId(ours[0], out var fighter)) {
            return;
        }

        var free = notice.FreePlayerSlots - _helpers.Count(h => h.Wizard.DuelSigil == notice.SigilId
                                                                && h.Wizard.Activity == AmbientActivity.Helping);
        foreach (var helper in _helpers.Where(h => h.Present && !h.Transferring && h.Wizard.Activity == AmbientActivity.Dungeon
                                                   && string.Equals(h.ZonePath, fighter.Zone, StringComparison.OrdinalIgnoreCase)
                                                   && Distance(h.Wizard.Position, notice.Location) < DungeonManners.FarDistance)
                     .OrderBy(h => Distance(h.Wizard.Position, notice.Location)).ToList()) {
            if (free-- <= 0) {
                break;
            }

            var wizard = helper.Wizard;
            AmbientWizards.PermitJoin(wizard.Endpoint, notice.SigilId);
            wizard.DuelSigil = notice.SigilId;
            wizard.Activity = AmbientActivity.Helping;
            helper.HelpBy = DateTime.UtcNow.AddSeconds(25);
            var location = notice.Location;
            // A short beat to see the fight start, then in.
            Timers.StartSingleTimer($"help-{wizard.CharId}", new Later(helper, h => {
                if (h.Wizard.Activity == AmbientActivity.Helping && !WalkTo(h, location)) {
                    Fish(h);
                }
            }), TimeSpan.FromMilliseconds(400 + _rng.Next(1100)));
        }
    }

    private void StopHelping(Helper helper) {
        AmbientWizards.RevokeJoin(helper.Wizard.Endpoint);
        helper.Wizard.DuelSigil = 0;
        helper.Wizard.Activity = AmbientActivity.Dungeon;
    }

    private void InDuel(Helper helper, COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added) {
        var wizard = helper.Wizard;
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
        Send(helper.ZoneActor, [AmbientZone.Move(wizard), new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 }]);
    }

    private void DuelOver(Helper helper, bool won) {
        var wizard = helper.Wizard;
        if (wizard.Activity is not (AmbientActivity.Fighting or AmbientActivity.Helping)) {
            return;
        }

        var stats = wizard.Wizard.GameStats;
        var defeated = !won && stats.m_currentHitpoints <= 0;
        stats.m_currentHitpoints = stats.m_baseHitpoints; // a potion between fights, off screen
        stats.m_currentMana = stats.m_baseMana;
        wizard.Wizard.IsInDuel = false;
        StopHelping(helper);
        Send(helper.ZoneActor, [
            new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = wizard.Wizard.GameObjectID, State = (uint) NPCStates.Idle },
            AmbientZone.Move(wizard),
        ]);
        if (defeated) {
            // 2009: a defeated wizard wakes in the commons; a helper goes back to its street a while later.
            GoHome(helper, DungeonLines.Pick(DungeonLines.Defeated, wizard.Identity.Seed + wizard.Turn++), TimeSpan.FromSeconds(45 + _rng.Next(45)));
        }
    }

    // ---- messages -------------------------------------------------------------------------------

    private void OnInbox(AmbientInbox inbox) {
        var helper = _helpers.FirstOrDefault(h => h.Wizard == inbox.Wizard);
        if (helper is null) {
            return;
        }

        switch (inbox.Message) {
            case ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP rsp:
                Arrived(helper, rsp);
                break;
            case ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP:
                Added(helper);
                break;
            case ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE added when helper.Present && added.PlayerActor is not null
                                                                    && !AmbientWizards.IsAmbient(added.PlayerActor):
                SpawnFor(helper, added.PlayerActor);
                break;
            case COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added:
                InDuel(helper, added);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATWIN:
                DuelOver(helper, won: true);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEATH:
                DuelOver(helper, won: false);
                break;
            case GAME_5_PROTOCOL.MSG_RADIALCHAT say:
                Heard(Wizard.TryGetCharacterId(say.SourceID, out var who) ? who : 0, AmbientChat.Text((byte[]) say.Message));
                break;
            case GAME_5_PROTOCOL.MSG_DIRECTEDCHAT text:
                Heard(text.SourceID, (string) text.Message ?? "");
                break;
        }
    }

    /// <summary>A player of the run telling the helpers to go: they all go, and none comes to that player for a while.</summary>
    private void Heard(ulong speaker, string text) {
        if (speaker == 0 || AmbientWizards.IsAmbientChar(speaker) || !AmbientZone.SaysNoToHelpers(text)
            || OnlinePlayerCollection.GetOnlinePlayer(speaker) is not { } player || player.InstanceOwnerId != _runId) {
            return;
        }

        AmbientDungeons.SaidNo(speaker, DateTime.UtcNow);
        foreach (var helper in _helpers.ToList()) {
            GoHome(helper, helper == _helpers[0] ? DungeonLines.Pick(DungeonLines.Declined, helper.Wizard.Identity.Seed) : null,
                TimeSpan.FromSeconds(4));
        }
    }

    // ---- going home -----------------------------------------------------------------------------

    private void EndRun(string why) {
        Logger.Information("Ambient dungeon run {Run} is over ({Why}).", Logger.Args(_runId, why));
        foreach (var helper in _helpers.ToList()) {
            GoHome(helper, DungeonLines.Pick(DungeonLines.Thanks, helper.Wizard.Identity.Seed + helper.Index), TimeSpan.FromSeconds(3 + _rng.Next(8)));
        }
    }

    /// <summary>The helper says <paramref name="line"/> (if any), leaves the run and comes back into its street later.</summary>
    private void GoHome(Helper helper, string line, TimeSpan after) {
        var wizard = helper.Wizard;
        if (line is not null && AmbientWizards.Settings.Chat) {
            var near = helper.Present && ActiveWizardDirectory.TryGetByCharId(_leader, out var leader)
                       && string.Equals(leader.Zone, helper.ZonePath, StringComparison.OrdinalIgnoreCase)
                       && Distance(leader.Location, wizard.Position) <= HelpManners.HearingDistance;
            if (near) {
                AmbientChat.Say(wizard, line);
            }
            else if (_leader != 0) {
                AmbientChat.Whisper(wizard, _leader, line);
            }
        }

        _helpers.Remove(helper);
        AmbientWizards.RevokeJoin(wizard.Endpoint);
        // Off after a breath, so the line is read before the wizard vanishes.
        RemoveFromZoneLater(helper, TimeSpan.FromMilliseconds(line is null ? 0 : 1500));
        Context.Parent.Tell(new AmbientHelperBack(wizard, _notice.Pad, after + TimeSpan.FromSeconds(2)));
        if (_helpers.Count == 0) {
            Context.Parent.Tell(new AmbientPartyOver(_runId));
            Timers.StartSingleTimer("stop", PoisonPill.Instance, TimeSpan.FromSeconds(3));
        }

    }

    private void RemoveFromZoneLater(Helper helper, TimeSpan after) {
        if (after <= TimeSpan.Zero) {
            RemoveFromZone(helper);
            return;
        }

        // Not in _helpers any more, so a Later would be dropped: the zone is told directly after the pause.
        var zone = helper.ZoneActor;
        var wizard = helper.Wizard;
        if (!helper.Present || zone is null) {
            return;
        }

        helper.Present = false;
        wizard.Present = false;
        wizard.Moving = false;
        wizard.Leg = null;
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(wizard.CharId);
        Context.System.Scheduler.ScheduleTellOnce(after, zone, new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            PlayerActor = wizard.Endpoint, GlobalId = wizard.Wizard.GameObjectID,
            MobileId = wizard.Wizard.GameObject.m_nMobileID, IsPlayerStillConnected = false,
        }, wizard.Endpoint);
    }

    private void RemoveFromZone(Helper helper) {
        var wizard = helper.Wizard;
        if (!helper.Present) {
            return;
        }

        helper.Present = false;
        wizard.Present = false;
        wizard.Moving = false;
        wizard.Leg = null;
        wizard.Route.Clear();
        wizard.Target = null;
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(wizard.CharId);
        helper.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            PlayerActor = wizard.Endpoint, GlobalId = wizard.Wizard.GameObjectID,
            MobileId = wizard.Wizard.GameObject.m_nMobileID, IsPlayerStillConnected = false,
        }, wizard.Endpoint);
    }

    // ---- walking --------------------------------------------------------------------------------

    private void LoadNav(string zone) {
        if (!_navAsked.Add(zone)) {
            return;
        }

        var self = Self;
        AmbientNav.ForZone(zone).ContinueWith(task => {
            if (task.IsCompletedSuccessfully && task.Result is { } grid) {
                self.Tell(new NavReady(zone, grid));
            }
        }, TaskScheduler.Default);
    }

    private NavGrid Nav(Helper helper) => helper.ZonePath is { } zone && _navs.TryGetValue(zone, out var grid) ? grid : null;

    /// <summary>A walk on the zone's walkable ground (a straight line while its grid loads); false when none reaches it.</summary>
    private bool WalkTo(Helper helper, Vector3 target) {
        var wizard = helper.Wizard;
        var nav = Nav(helper);
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

    private void Face(Helper helper, Vector3 at) {
        var wizard = helper.Wizard;
        if (Distance(at, wizard.Position) < 1 || wizard.Moving) {
            return;
        }

        wizard.Yaw = MathF.Atan2(at.Y - wizard.Position.Y, at.X - wizard.Position.X);
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        Send(helper.ZoneActor, [AmbientZone.Move(wizard)]);
    }

    /// <summary>A move fished to the zone at the helper's spot, as MoveService does for a player (a duel circle hears it).</summary>
    private static void Fish(Helper helper) {
        var wizard = helper.Wizard;
        wizard.Wizard.IsInCombatGrace = false;
        wizard.Wizard.GameObject.m_location = wizard.Position;
        helper.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE {
            PlayerObject = wizard.Wizard.GameObject, PlayerActor = wizard.Endpoint, PlayerWizard = wizard.Wizard,
        }, wizard.Endpoint);
    }

    private static System.Numerics.Vector3 Num(Vector3 v) => new(v.X, v.Y, v.Z);

    private static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

}
