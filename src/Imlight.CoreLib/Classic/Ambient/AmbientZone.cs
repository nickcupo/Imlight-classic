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
 * AMBIENT ZONE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: one actor per zone drives that zone's ambient wizards.
 *   - Arrival: each wizard enters like a player (zone transfer, then
 *     MSG_ADDPLAYER from its endpoint), a few seconds apart, and spawns
 *     itself for every real player there and every later arrival.
 *   - Movement: one timer for the whole zone (every 300 ms). Each walking
 *     wizard takes a step at running speed; all the steps of a tick go to
 *     the zone as one MSG_CLIENTBATCH, which each session writes to its
 *     socket: one zone message per tick, however many wizards walk.
 *   - Behaviour: walk to an NPC and stand at it (shops), wander between
 *     the zone's named locations, follow a friend who is here, hunt street
 *     mobs (Unicorn Way and other streets), offer help at real players'
 *     duels (ask first, join on a yes), and say a canned line now and then
 *     when real players are around. Replies use AmbientChatBrain.
 *   - Friends: a friend request is accepted after a few seconds; the
 *     player's friend list gets an ordinary relationship row, the wizard
 *     remembers the friend in its own record.
 * Nothing here blocks: database writes go to background tasks.
 *
 * USAGE EXAMPLE:
 * Context.ActorOf(AmbientZone.Props(zonePath, wizards, gameServer), name);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A real player's duel in a zone, as ambient wizards hear of it (from CombatDuelComponent).</summary>
internal sealed record AmbientDuelNotice(ulong SigilId, Vector3 Location, ulong[] PlayerCharIds, int FreePlayerSlots,
                                         bool Active, bool Pvp);

/// <summary>Drives one zone's ambient wizards (see the file header).</summary>
internal sealed class AmbientZone : ReceiveActor, IWithTimers {

    private sealed record Tick;
    private sealed record Enter(ulong CharId);
    private sealed record Later(AmbientWizard Wizard, Action<AmbientWizard> Action);
    private sealed record FriendAccepted(AmbientWizard Wizard, ulong Requester, Relationship Relationship, string Name);
    private sealed record Spot(Vector3 At, float FaceYaw, bool Npc);

    private const float Speed = 230f;            // units a second, about a running wizard
    private const double TickSeconds = 0.3;
    private const float Arrive = 30f;
    private const float Neighbourhood = 2600f;   // how far a wizard walks in one go
    private const float HelpRange = 9000f;       // how far away a duel draws an offer (about 40 s at a run)

    private readonly string _zone;
    private readonly IActorRef _server;
    private readonly List<AmbientWizard> _wizards;
    private readonly Random _rng;
    private readonly CoreObjectSerializer _serializer = new(versionable: false, behaviors: SerializerFlags.None);
    private readonly Dictionary<ulong, AmbientDuelNotice> _duels = [];
    private readonly HashSet<ulong> _offeredDuels = [];
    private IActorRef _zoneActor;
    private List<Spot> _spots;
    private List<Vector3> _mobNodes;
    private Vector3 _start;
    private int _realPlayers;
    private DateTime _nextCensus;
    private DateTime _nextOnline;

    public ITimerScheduler Timers { get; set; }

    public AmbientZone(string zone, List<AmbientWizard> wizards, IActorRef server) {
        _zone = zone;
        _wizards = wizards;
        _server = server;
        _rng = new Random(StableSeed(zone));

        Receive<Tick>(_ => OnTick());
        Receive<Enter>(OnEnter);
        Receive<Later>(later => {
            if (_wizards.Contains(later.Wizard)) {
                later.Action(later.Wizard);
            }
        });
        Receive<AmbientInbox>(OnInbox);
        Receive<AmbientDuelNotice>(OnDuelNotice);
        Receive<FriendAccepted>(OnFriendAccepted);
        Receive<Status.Failure>(failure => Logger.Warning("Ambient wizards in {Zone}: {Error}",
            Logger.Args(_zone, failure.Cause?.GetBaseException().Message)));
    }

    private static int StableSeed(string text) {
        var hash = 17;
        foreach (var c in text) {
            hash = unchecked(hash * 31 + c);
        }

        return hash;
    }

    public static Props Props(string zone, List<AmbientWizard> wizards, IActorRef server)
        => Akka.Actor.Props.Create(() => new AmbientZone(zone, wizards, server));

    protected override void PreStart() {
        var i = 0;
        foreach (var wizard in _wizards) {
            wizard.Zone = _zone;
            wizard.Endpoint = Context.ActorOf(AmbientEndpoint.Props(wizard, Self), $"wizard-{wizard.CharId:x}");
            AmbientWizards.Register(wizard);
            ActiveWizardDirectory.SetWizard(wizard.Endpoint, wizard.Wizard);
            // A few seconds apart, like players logging in.
            Timers.StartSingleTimer($"enter-{wizard.CharId}", new Enter(wizard.CharId), TimeSpan.FromSeconds(3 + i * 4 + _rng.Next(4)));
            i++;
        }

        Timers.StartPeriodicTimer("tick", new Tick(), TimeSpan.FromSeconds(TickSeconds));
    }

    protected override void PostStop() {
        foreach (var wizard in _wizards) {
            Leave(wizard);
            AmbientWizards.Unregister(wizard);
            ActiveWizardDirectory.Remove(wizard.Endpoint);
        }

        base.PostStop();
    }

    // ---- arrival and departure --------------------------------------------------------------

    private void OnEnter(Enter enter) {
        var wizard = _wizards.FirstOrDefault(w => w.CharId == enter.CharId);
        if (wizard is null || wizard.Present) {
            return;
        }

        if (!ClassicGate.Decide(_zone).Allowed) {
            Logger.Warning("Ambient wizard {Name} stays out of {Zone}: the profile closes it.", Logger.Args(wizard.Name, _zone));
            return;
        }

        wizard.Activity = AmbientActivity.Arriving;
        _server.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = _zone, DestinationLocation = "Start", SendToClient = false, OwnerCharId = wizard.CharId,
        }, wizard.Endpoint);
    }

    private void Arrived(AmbientWizard wizard, ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP rsp) {
        if (rsp.ErrorCode != 0 || rsp.ZoneActorRef is null) {
            Logger.Warning("Ambient wizard {Name} could not enter {Zone} ({Error}); trying again in a minute.",
                Logger.Args(wizard.Name, _zone, rsp.ErrorMessage ?? rsp.ErrorCode.ToString()));
            Timers.StartSingleTimer($"enter-{wizard.CharId}", new Enter(wizard.CharId), TimeSpan.FromMinutes(1));
            return;
        }

        if (_zoneActor is null || !_zoneActor.Equals(rsp.ZoneActorRef)) {
            _zoneActor = rsp.ZoneActorRef;
            AmbientWizards.SetGroup(_zoneActor, Self);
            _start = rsp.Location;
            _spots = null;
        }

        wizard.ZoneActor = rsp.ZoneActorRef;
        wizard.ZoneDisplayName = rsp.ZoneDisplayName ?? "";
        var angle = _rng.NextDouble() * Math.PI * 2;
        var distance = 60 + _rng.NextDouble() * 180;
        wizard.Position = new Vector3(rsp.Location.X + (float) (Math.Cos(angle) * distance),
            rsp.Location.Y + (float) (Math.Sin(angle) * distance), rsp.Location.Z);
        wizard.Yaw = (float) (_rng.NextDouble() * Math.PI * 2);

        var character = wizard.Wizard;
        character.Zone = _zone;
        character.ZoneDisplayName = wizard.ZoneDisplayName;
        character.Location = wizard.Position;
        character.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        var gameObject = WizardObjectLoader.GetPlayerGameObject(character);
        gameObject.m_nMobileID = rsp.MobileId;
        gameObject.m_location = wizard.Position;
        gameObject.m_orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        character.GameObject = gameObject;
        character.IsInDuel = false;
        ActiveWizardDirectory.SetGameObject(wizard.Endpoint, gameObject);

        _zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYER {
            PlayerActor = wizard.Endpoint, PlayerObject = gameObject, Wizard = character, ActualWizardName = wizard.Name,
        }, wizard.Endpoint);
    }

    private void Added(AmbientWizard wizard) {
        if (wizard.Present) {
            return; // The zone and its player supervisor each answer MSG_ADDPLAYER.
        }

        wizard.Present = true;
        wizard.Activity = AmbientActivity.Idle;
        wizard.Until = DateTime.UtcNow.AddSeconds(2 + _rng.Next(6));
        wizard.NextIdleLine = DateTime.UtcNow.AddSeconds(30 + _rng.Next(90));
        SpawnFor(wizard, null);
        SetOnline(wizard);
        Logger.Information("Ambient wizard {Name} (level {Level} {School}) is in {Zone}.",
            Logger.Args(wizard.Name, wizard.Wizard.MagicSchoolBehavior.Level, wizard.Identity.School, _zone));
    }

    private void Leave(AmbientWizard wizard) {
        if (!wizard.Present) {
            return;
        }

        wizard.Present = false;
        wizard.Moving = false;
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(wizard.CharId);
        wizard.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            PlayerActor = wizard.Endpoint, GlobalId = wizard.Wizard.GameObjectID,
            MobileId = wizard.Wizard.GameObject.m_nMobileID, IsPlayerStillConnected = false,
        }, wizard.Endpoint);
    }

    private void SetOnline(AmbientWizard wizard)
        => OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            SessionId = 0, AccountId = wizard.CharId, CharacterId = wizard.CharId, CurrentZone = _zone,
            CurrentZoneDisplayName = wizard.ZoneDisplayName, CurrentRealm = "Centaur", ActorPath = wizard.Endpoint.Path.ToString(),
        });

    /// <summary>Spawns the wizard for one player (<paramref name="player"/>), or for every player in the zone (null).</summary>
    private void SpawnFor(AmbientWizard wizard, IActorRef player) {
        var character = wizard.Wizard;
        character.Location = wizard.Position;
        character.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        var gameObject = WizardObjectLoader.GetPlayerGameObject(character);
        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_serializer.Serialize(gameObject, flags, out var data)) {
            Logger.Error("Ambient wizard {Name}: its object did not serialize.", Logger.Args(wizard.Name));
            return;
        }

        var spawn = new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = data };
        if (player is not null) {
            player.Tell(spawn);
            return;
        }

        _zoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = spawn, Selfless = true, Sender = wizard.Endpoint, Targets = ZoneBroadcastTarget.Players,
        });
    }

    // ---- the tick -------------------------------------------------------------------------------

    private void OnTick() {
        var now = DateTime.UtcNow;
        if (now >= _nextCensus) {
            _nextCensus = now.AddSeconds(5);
            _realPlayers = OnlinePlayerCollection.GetPlayersInZone(_zone).Count(p => !AmbientWizards.IsAmbientChar(p.CharacterId));
            foreach (var wizard in _wizards) {
                wizard.Offers.Expire(now);
            }

            foreach (var notice in _duels.Values.ToList()) {
                TryOffer(notice);
            }
        }

        if (now >= _nextOnline) {
            _nextOnline = now.AddMinutes(1);
            foreach (var wizard in _wizards.Where(w => w.Present)) {
                SetOnline(wizard); // a real session sharing id 0 may have cleared it
            }
        }

        var batch = new List<IMessage>();
        foreach (var wizard in _wizards) {
            if (!wizard.Present || wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Away) {
                continue;
            }

            if (wizard.Moving) {
                Step(wizard, batch);
            }
            else if (now >= wizard.Until) {
                Decide(wizard, now);
            }

            if (wizard.DuelSigil == ulong.MaxValue && wizard.Activity == AmbientActivity.Walking && now >= wizard.NextLook
                && _zoneActor is not null) {
                // Hunting (one hunter a zone): every second, walking the path or waiting on it, fish a move to the zone as
                // a player's MoveService does, so a street mob whose aggro range covers this spot starts a duel with it (its
                // permit for a fight of its own is set). Static duelists answer the duel-target question instead.
                wizard.NextLook = now.AddSeconds(1);
                wizard.Wizard.IsInCombatGrace = false;
                Fish(wizard);
                _zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET { PlayerGameObject = wizard.Wizard.GameObject },
                    wizard.Endpoint);
            }

            if (now >= wizard.NextIdleLine && _realPlayers == 0) {
                wizard.NextIdleLine = now.AddSeconds(IdleGap(wizard)); // nobody to talk to; keep the lines spread out
            }
            else if (now >= wizard.NextIdleLine && AmbientWizards.Settings.Chat) {
                wizard.NextIdleLine = now.AddSeconds(IdleGap(wizard));
                if (wizard.Limiter.TryTake(now) && AmbientChatBrain.Idle(ChatFor(wizard, 0), wizard.Turn++ + wizard.Identity.Seed) is { } line) {
                    AmbientChat.Say(wizard, line);
                }
            }
        }

        if (batch.Count > 0 && _zoneActor is not null && _realPlayers > 0) {
            _zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_CLIENTBATCH { Messages = [.. batch] }],
                Targets = ZoneBroadcastTarget.Players,
                Sender = Self,
            });
        }
    }

    private double IdleGap(AmbientWizard wizard) => wizard.Identity.Temper switch {
        AmbientTemper.Chatty => 60 + _rng.Next(60),
        AmbientTemper.Friendly => 120 + _rng.Next(120),
        _ => 300 + _rng.Next(300),
    };

    private void Step(AmbientWizard wizard, List<IMessage> batch) {
        if (wizard.Target is not { } target) {
            wizard.Moving = false;
            return;
        }

        var dx = target.X - wizard.Position.X;
        var dy = target.Y - wizard.Position.Y;
        var dz = target.Z - wizard.Position.Z;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        var step = (float) (Speed * TickSeconds);
        wizard.Yaw = MathF.Atan2(dy, dx);
        if (distance <= Math.Max(step, Arrive)) {
            wizard.Position = target;
            wizard.Moving = false;
            if (wizard.ArriveYaw is { } face) {
                wizard.Yaw = face;
                wizard.ArriveYaw = null;
            }

            Arrived(wizard);
        }
        else {
            var t = step / distance;
            wizard.Position = new Vector3(wizard.Position.X + dx * t, wizard.Position.Y + dy * t, wizard.Position.Z + dz * t);
        }

        wizard.Wizard.Location = wizard.Position;
        wizard.Wizard.Orientation = new Vector3(0, 0, AmbientWizards.ClientYaw(wizard.Yaw));
        batch.Add(Move(wizard));
        if (!wizard.Moving) {
            batch.Add(new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 });
        }
    }

    /// <summary>MSG_SERVERMOVE as MoveService relays a client's move: position over 4, yaw as a packed byte.</summary>
    internal static GAME_5_PROTOCOL.MSG_SERVERMOVE Move(AmbientWizard wizard) {
        var degrees = AmbientWizards.ClientYaw(wizard.Yaw) * 180f / MathF.PI; // CLASSIC: the client's clockwise yaw
        degrees = ((degrees % 360f) + 360f) % 360f;
        return new GAME_5_PROTOCOL.MSG_SERVERMOVE {
            LocationX = unchecked((ushort) (short) MathF.Round(wizard.Position.X / 4)),
            LocationY = unchecked((ushort) (short) MathF.Round(wizard.Position.Y / 4)),
            LocationZ = unchecked((ushort) (short) MathF.Round(wizard.Position.Z / 4)),
            Direction = (byte) Math.Clamp(MathF.Round(degrees / (360f / byte.MaxValue) / 1.035f), 0, 255),
            MobileID = wizard.Wizard.GameObject.m_nMobileID,
        };
    }

    private void Arrived(AmbientWizard wizard) {
        var now = DateTime.UtcNow;
        switch (wizard.Activity) {
            case AmbientActivity.Helping:
                // At the circle: walk in (the duel takes it only with the player's yes and a free slot).
                Fish(wizard);
                wizard.Until = now.AddSeconds(10);
                break;
            case AmbientActivity.Shopping:
                wizard.Until = now.AddSeconds(15 + _rng.Next(45));
                break;
            case AmbientActivity.Walking when wizard.DuelSigil == ulong.MaxValue:
                // Hunting: wait on the creatures' path a while (the tick keeps asking for a duel target).
                wizard.Until = now.AddSeconds(15 + _rng.Next(20));
                break;
            default:
                wizard.Activity = AmbientActivity.Idle;
                wizard.Until = now.AddSeconds(4 + _rng.Next(20));
                break;
        }
    }

    // ---- what to do next ---------------------------------------------------------------------

    private void Decide(AmbientWizard wizard, DateTime now) {
        if (wizard.Activity == AmbientActivity.Helping || wizard.DuelSigil == ulong.MaxValue) {
            // The yes was not followed by a seat in time (full, or the duel ended), or the hunt is over.
            AmbientWizards.RevokeJoin(wizard.Endpoint);
        }

        wizard.DuelSigil = 0;
        EnsureSpots();
        if (wizard.FollowCharId != 0 && Follow(wizard, now)) {
            return;
        }

        var roll = _rng.NextDouble();
        if (AmbientWizards.Settings.StreetFights && _mobNodes is { Count: > 0 } && roll < 0.35
            && !_wizards.Any(w => w != wizard && w.DuelSigil == ulong.MaxValue)
            && wizard.Wizard.GameStats.m_currentHitpoints >= wizard.Wizard.GameStats.m_baseHitpoints * 0.6) {
            // Anywhere on the creatures' paths: the mobs keep to parts of a street, so a hunt may be a long walk.
            var node = _mobNodes.Count == 0 ? (Vector3?) null : _mobNodes[_rng.Next(_mobNodes.Count)];
            if (node is { } at) {
                wizard.DuelSigil = ulong.MaxValue; // hunting
                AmbientWizards.PermitJoin(wizard.Endpoint, 0); // a street mob may pull it into a fight of its own
                Logger.Debug("Ambient wizard {Name} goes hunting in {Zone}.", Logger.Args(wizard.Name, _zone));
                WalkTo(wizard, at, AmbientActivity.Walking);
                return;
            }
        }

        if (_spots is { Count: > 0 } && roll < 0.85) {
            var npcs = _spots.Where(s => s.Npc && Distance(s.At, wizard.Position) < Neighbourhood).ToList();
            var pool = npcs.Count > 0 && roll < 0.65 ? npcs : _spots.Where(s => Distance(s.At, wizard.Position) < Neighbourhood).ToList();
            if (pool.Count > 0) {
                var spot = pool[_rng.Next(pool.Count)];
                WalkTo(wizard, spot.At, spot.Npc ? AmbientActivity.Shopping : AmbientActivity.Walking);
                wizard.ArriveYaw = spot.Npc ? spot.FaceYaw : null; // turn to the NPC on arrival

                return;
            }
        }

        // Stay a while, turning now and then.
        wizard.Activity = AmbientActivity.Idle;
        wizard.Until = now.AddSeconds(5 + _rng.Next(20));
    }

    private bool Follow(AmbientWizard wizard, DateTime now) {
        if (!ActiveWizardDirectory.TryGetByCharId(wizard.FollowCharId, out var friend)
            || !string.Equals(friend.Zone, _zone, StringComparison.OrdinalIgnoreCase) || now > wizard.Until.AddMinutes(5)) {
            wizard.FollowCharId = 0;
            return false;
        }

        var at = friend.Location;
        if (Distance(at, wizard.Position) > 220) {
            var angle = _rng.NextDouble() * Math.PI * 2;
            WalkTo(wizard, new Vector3(at.X + (float) Math.Cos(angle) * 120, at.Y + (float) Math.Sin(angle) * 120, at.Z),
                AmbientActivity.Following);
        }
        else {
            wizard.Activity = AmbientActivity.Following;
            wizard.Until = now.AddSeconds(3);
        }

        return true;
    }

    private void WalkTo(AmbientWizard wizard, Vector3 target, AmbientActivity activity) {
        wizard.ArriveYaw = null;
        wizard.Target = target;
        wizard.Activity = activity;
        wizard.Moving = true;
    }

    private Vector3? Nearby(List<Vector3> points, Vector3 from) {
        var near = points.Where(p => Distance(p, from) < Neighbourhood).ToList();
        return near.Count == 0 ? null : near[_rng.Next(near.Count)];
    }

    private static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Places to go: in front of the zone's NPCs, its named locations, and (streets) its mob path nodes.</summary>
    private void EnsureSpots() {
        if (_spots is not null || _zoneActor is null || !ZoneDataDirectory.TryGet(_zoneActor, out var data)) {
            return;
        }

        var spots = new List<Spot> { new(_start, 0, false) };
        foreach (var info in data.m_objectList ?? []) {
            if (info is null || CoreObjectFactory.GetCoreTemplate(info.m_templateID) is not GameObjectTemplate template
                || !template.m_behaviors.Any(b => b is NPCBehaviorTemplate) || template.m_behaviors.Any(b => b is DuelistBehaviorTemplate)) {
                continue;
            }

            // A few steps in front of the NPC, facing it (the object's yaw is the client's; Heading turns it back).
            var facing = AmbientWizards.Heading(info.m_orientation.Z);
            var at = new Vector3(info.m_location.X + MathF.Cos(facing) * 90, info.m_location.Y + MathF.Sin(facing) * 90, info.m_location.Z);
            spots.Add(new Spot(at, facing + MathF.PI, true));
        }

        foreach (var location in data.m_locationList ?? []) {
            if (location is not null) {
                spots.Add(new Spot(location.m_location, AmbientWizards.Heading(location.m_direction), false));
            }
        }

        // A hub: keep to the part around the start (its far corners can be other floors or closed areas). A street: its
        // creature path nodes are walkable ground along the whole street, so wizards roam (and hunt) along them too.
        var hub = _zone.Contains("Hub", StringComparison.OrdinalIgnoreCase);
        _mobNodes = !hub && ZoneDataDirectory.TryGetNodes(_zoneActor, out var nodes)
            ? [.. nodes.Where(n => MathF.Abs(n.Z - _start.Z) < 800)]
            : [];
        _spots = hub
            ? [.. spots.Where(s => Distance(s.At, _start) < 6000 && MathF.Abs(s.At.Z - _start.Z) < 400)]
            : [.. spots.Where(s => MathF.Abs(s.At.Z - _start.Z) < 800), .. _mobNodes.Select(n => new Spot(n, 0, false))];
        Logger.Information("Ambient wizards in {Zone}: {Spots} places to go ({Npcs} NPCs), {Nodes} mob path nodes.",
            Logger.Args(_zone, _spots.Count, _spots.Count(s => s.Npc), _mobNodes.Count));
    }

    /// <summary>A move fished to the zone at the wizard's spot, as MoveService does for a player (duel circles hear it).</summary>
    private void Fish(AmbientWizard wizard)
        => _zoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE {
            PlayerObject = wizard.Wizard.GameObject, PlayerActor = wizard.Endpoint, PlayerWizard = wizard.Wizard,
        }, wizard.Endpoint);

    // ---- messages from the endpoints ----------------------------------------------------------

    private void OnInbox(AmbientInbox inbox) {
        var wizard = inbox.Wizard;
        switch (inbox.Message) {
            case ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP rsp:
                Arrived(wizard, rsp);
                break;
            case ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP:
                Added(wizard);
                break;
            case ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE added when wizard.Present && added.PlayerActor is not null:
                if (!AmbientWizards.IsAmbient(added.PlayerActor)) {
                    SpawnFor(wizard, added.PlayerActor);
                    if (ActiveWizardDirectory.TryGet(added.PlayerActor, out var arrival, out _)) {
                        FriendArrived(wizard, arrival);
                    }
                }

                break;
            case ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGETRSP target:
                StreetFight(wizard, target);
                break;
            case GAME_5_PROTOCOL.MSG_RADIALCHAT say:
                Heard(wizard, say.SourceID, (byte[]) say.SourceName, AmbientChat.Text((byte[]) say.Message), whisper: false);
                break;
            case GAME_5_PROTOCOL.MSG_DIRECTEDCHAT text:
                Heard(wizard, Wizard.GetGameObjectId(text.SourceID), (byte[]) text.SourceName, (string) text.Message ?? "", whisper: true);
                break;
            case CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD request when request.Remove == 0:
                FriendRequested(wizard, request);
                break;
            case CHARACTER_103_PROTOCOL.MSG_BUDDYDROPFWD drop:
                Unfriended(wizard, drop.RequesterCharId);
                break;
            case GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE status when status.Status == 4:
                FriendOnline(wizard, status.EntryGID);
                break;
            case COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added:
                InDuel(wizard, added);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATWIN:
                DuelOver(wizard, won: true);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEATH:
                DuelOver(wizard, won: false);
                break;
            case AmbientSparringSeat seat:
                Sparring(wizard, seat.SigilId);
                break;
        }
    }

    // ---- fights ---------------------------------------------------------------------------------

    private void StreetFight(AmbientWizard wizard, ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGETRSP target) {
        if (!AmbientWizards.Settings.StreetFights || wizard.DuelSigil != ulong.MaxValue
            || wizard.Activity != AmbientActivity.Walking || target.CreatureActor is null || target.CreatureObject is null) {
            return;
        }

        Logger.Debug("Ambient wizard {Name} starts a street fight.", Logger.Args(wizard.Name));
        wizard.DuelSigil = 0;
        if (wizard.Moving) {
            wizard.Moving = false; // stop where the mob noticed it
        }

        AmbientWizards.PermitJoin(wizard.Endpoint, 0); // its own fight with a creature
        wizard.Activity = AmbientActivity.Helping;     // waiting for the duel to take it
        wizard.Until = DateTime.UtcNow.AddSeconds(8);
        _zoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL {
            StartingParticipants = new Dictionary<IActorRef, CoreObject> {
                { wizard.Endpoint, wizard.Wizard.GameObject },
                { target.CreatureActor, target.CreatureObject },
            },
        }, wizard.Endpoint);
    }

    private void InDuel(AmbientWizard wizard, COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added) {
        wizard.Activity = AmbientWizards.IsSparringIn(wizard.Endpoint, added.Duel?.SigilId ?? 0)
            ? AmbientActivity.Sparring : AmbientActivity.Fighting;
        wizard.Moving = false;
        wizard.DuelSigil = added.Duel?.SigilId ?? 0;
        wizard.Wizard.IsInDuel = true;
        wizard.Position = added.SlotPosition;
        wizard.Wizard.Location = added.SlotPosition;
        // CLASSIC (2026-10-02): face the circle's middle, as a joining client does by moving onto its slot (an
        // ambient wizard kept the heading it walked in with: the owner saw it facing the wrong way in the duel).
        wizard.Yaw = AmbientWizards.Heading(added.SlotOrientation);
        wizard.Wizard.Orientation = new Vector3(0, 0, added.SlotOrientation);
        _zoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_CLIENTBATCH { Messages = [
                Move(wizard), new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = wizard.Wizard.GameObjectID, NewState = 0 },
            ] }],
            Targets = ZoneBroadcastTarget.Players,
            Sender = Self,
        });
        if (_duels.TryGetValue(wizard.DuelSigil, out var notice)) {
            foreach (var player in notice.PlayerCharIds) {
                Remember(wizard, player, helped: false);
            }
        }
    }

    private void DuelOver(AmbientWizard wizard, bool won) {
        if (wizard.Activity is not (AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Helping)) {
            return;
        }

        var sigil = wizard.DuelSigil;
        if (_duels.TryGetValue(sigil, out var notice)) {
            foreach (var player in notice.PlayerCharIds) {
                Remember(wizard, player, helped: true);
            }
        }

        wizard.DuelSigil = 0;
        wizard.Wizard.IsInDuel = false;
        AmbientWizards.RevokeJoin(wizard.Endpoint);
        var stats = wizard.Wizard.GameStats;
        var defeated = stats.m_currentHitpoints <= 0;
        stats.m_currentHitpoints = stats.m_baseHitpoints; // a potion or the hub's healing, off screen
        stats.m_currentMana = stats.m_baseMana;
        if (wizard.Activity == AmbientActivity.Sparring) {
            AmbientWizards.EndSparring(wizard.Endpoint);
        }

        if (!won && defeated) {
            // A defeated wizard goes home to heal (2009: back to the commons) and comes back a little later.
            wizard.Activity = AmbientActivity.Away;
            Leave(wizard);
            Timers.StartSingleTimer($"enter-{wizard.CharId}", new Enter(wizard.CharId), TimeSpan.FromSeconds(45 + _rng.Next(45)));
            return;
        }

        wizard.Activity = AmbientActivity.Idle;
        wizard.Until = DateTime.UtcNow.AddSeconds(6 + _rng.Next(10));
        if (won && notice is not null && AmbientWizards.Settings.Chat && wizard.Limiter.TryTake(DateTime.UtcNow)) {
            AmbientChat.Say(wizard, (wizard.Turn++ % 3) switch { 0 => "gg!", 1 => "nice one", _ => "that was fun" });
        }
    }

    private void Sparring(AmbientWizard wizard, ulong sigil) {
        if (sigil == 0) {
            if (wizard.Activity == AmbientActivity.Sparring) {
                wizard.Activity = AmbientActivity.Idle;
                wizard.Until = DateTime.UtcNow.AddSeconds(5);
            }

            return;
        }

        wizard.Activity = AmbientActivity.Sparring;
        wizard.Moving = false;
        wizard.DuelSigil = sigil;
    }

    private void OnDuelNotice(AmbientDuelNotice notice) {
        if (!notice.Active) {
            _duels.Remove(notice.SigilId);
            _offeredDuels.Remove(notice.SigilId);
            return;
        }

        _duels[notice.SigilId] = notice;
        Logger.Debug("Ambient wizards in {Zone}: duel {Sigil} with {Players} player(s), {Free} free slot(s).",
            Logger.Args(_zone, notice.SigilId, notice.PlayerCharIds.Length, notice.FreePlayerSlots));
        TryOffer(notice);
    }

    /// <summary>
    /// One ambient wizard nearby (a friend of a player in it first) asks a player of this duel whether they want help.
    /// Called when the duel is announced and every few seconds while it runs and nobody has asked yet.
    /// </summary>
    private void TryOffer(AmbientDuelNotice notice) {
        // No chat, no offer: an ambient wizard never joins a real player's duel without asking first.
        if (!AmbientWizards.Settings.Battles || !AmbientWizards.Settings.Chat || notice.Pvp || notice.FreePlayerSlots <= 0 || notice.PlayerCharIds.Length == 0
            || _offeredDuels.Contains(notice.SigilId)) {
            return;
        }

        var now = DateTime.UtcNow;
        var player = notice.PlayerCharIds[0];
        var friendOfSomeone = _wizards.FirstOrDefault(w => w.Present && w.Activity is not (AmbientActivity.Fighting
                or AmbientActivity.Sparring or AmbientActivity.Away or AmbientActivity.Helping)
            && notice.PlayerCharIds.Any(p => w.FriendOf(p) is not null) && Distance(w.Position, notice.Location) < HelpRange * 2);
        var helper = friendOfSomeone ?? _wizards.Where(w => w.Present && w.Activity is AmbientActivity.Idle or AmbientActivity.Walking
                                                            or AmbientActivity.Shopping or AmbientActivity.Following
                                                        && Distance(w.Position, notice.Location) < HelpRange)
            .OrderBy(w => Distance(w.Position, notice.Location)).FirstOrDefault();
        if (helper is null) {
            {
                Logger.Debug("Ambient wizards in {Zone}: none free near duel {Sigil}: {Who}", Logger.Args(_zone, notice.SigilId,
                    string.Join("; ", _wizards.Select(w => $"{w.Name} {w.Activity} {(int) Distance(w.Position, notice.Location)}"))));
            }

            return;
        }

        if (friendOfSomeone is not null) {
            player = notice.PlayerCharIds.First(p => helper.FriendOf(p) is not null);
        }

        if (!helper.Offers.MayOffer(player, now) || !helper.Limiter.TryTake(now)) {
            return;
        }

        _offeredDuels.Add(notice.SigilId);
        Logger.Debug("Ambient wizard {Name} offers help in duel {Sigil}.", Logger.Args(helper.Name, notice.SigilId));
        helper.Offers.Offered(player, notice.SigilId, now);
        var facts = AmbientKnowledge.Facts(player);
        var line = AmbientChatBrain.HelpOffer(ChatFor(helper, player, facts), helper.Turn++);
        if (helper.FriendOf(player) is null || !AmbientChat.Whisper(helper, player, line)) {
            AmbientChat.Say(helper, line);
        }
    }

    private void JoinWithYes(AmbientWizard wizard, HelpAnswer answer, ulong player) {
        if (!_duels.TryGetValue(answer.DuelId, out var notice) || notice.FreePlayerSlots <= 0
            || wizard.Activity is AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Away) {
            Say(wizard, player, "aw, it's full. good luck!");
            return;
        }

        Say(wizard, player, AmbientChatBrain.HelpAnswered(true, wizard.Turn++));
        AmbientWizards.PermitJoin(wizard.Endpoint, notice.SigilId);
        wizard.DuelSigil = notice.SigilId;
        WalkTo(wizard, notice.Location, AmbientActivity.Helping);
        wizard.Until = DateTime.UtcNow.AddSeconds(25);
    }

    // ---- chat -----------------------------------------------------------------------------------

    private ChatContext ChatFor(AmbientWizard wizard, ulong speaker, (string Name, string Zone, string Quest) facts = default)
        => new(wizard.Name, wizard.Identity.School, wizard.Wizard.MagicSchoolBehavior.Level,
            AmbientKnowledge.ZoneName(_zone) ?? _zone, facts.Name, facts.Zone, facts.Quest,
            speaker == 0 ? null : wizard.FriendOf(speaker), AmbientKnowledge.WhereIs, DateTime.UtcNow);

    private void Heard(AmbientWizard wizard, ulong sourceGid, byte[] sourceName, string text, bool whisper) {
        if (!Wizard.TryGetCharacterId(sourceGid, out var speaker) || AmbientWizards.IsAmbientChar(speaker) || !wizard.Present) {
            return;
        }

        var now = DateTime.UtcNow;
        var answer = wizard.Offers.Hear(speaker, text, now);
        if (answer.Kind == HelpAnswerKind.Yes) {
            JoinWithYes(wizard, answer, speaker);
            return;
        }

        if (answer.Kind == HelpAnswerKind.No) {
            Say(wizard, speaker, AmbientChatBrain.HelpAnswered(false, wizard.Turn++));
            return;
        }

        if (!AmbientWizards.Settings.Chat || !wizard.Limiter.TryTake(now, speaker)) {
            return;
        }

        var facts = AmbientKnowledge.Facts(speaker);
        var reply = AmbientChatBrain.Reply(text, ChatFor(wizard, speaker, facts), whisper, wizard.Turn);
        if (reply is null) {
            return;
        }

        wizard.Turn++;
        // A short pause, as a person types.
        var delay = TimeSpan.FromMilliseconds(1200 + Math.Min(4000, reply.Length * 90));
        Timers.StartSingleTimer($"reply-{wizard.CharId}-{wizard.Turn}", new Later(wizard, w => {
            if (whisper) {
                AmbientChat.Whisper(w, speaker, reply);
            }
            else {
                AmbientChat.Say(w, reply);
            }
        }), delay);
        if (wizard.FriendOf(speaker) is not null) {
            Remember(wizard, speaker, helped: false);
        }
    }

    private void Say(AmbientWizard wizard, ulong player, string text) {
        if (wizard.FriendOf(player) is null || !AmbientChat.Whisper(wizard, player, text)) {
            AmbientChat.Say(wizard, text);
        }
    }

    // ---- friends --------------------------------------------------------------------------------

    private void FriendRequested(AmbientWizard wizard, CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD request) {
        var requester = request.RequesterCharId;
        var name = (string) request.OwnerName ?? "";
        // A human-feeling pause before the yes: 3 to 8 seconds.
        var delay = TimeSpan.FromSeconds(3 + (int) ((requester ^ wizard.CharId) % 6));
        Timers.StartSingleTimer($"friend-{wizard.CharId}-{requester}", new Later(wizard, w => {
            var self = Self;
            Task.Run(() => {
                var character = w.Wizard;
                character.AddPendingFriendRequest(requester);
                character.RemovePendingFriendRequest(requester);
                character.AddOrRepairRelationship(requester); // the relationship row (the player's friend list)
                character.FriendsBehavior.TryGetRelationship(requester, out var relationship);

                return new FriendAccepted(w, requester, relationship, name);
            }).PipeTo(self);
        }), delay);
    }

    private void OnFriendAccepted(FriendAccepted accepted) {
        var wizard = accepted.Wizard;
        if (accepted.Relationship is null) {
            Logger.Warning("Ambient wizard {Name} could not add {Friend} as a friend.", Logger.Args(wizard.Name, accepted.Requester));
            return;
        }

        if (OnlinePlayerCollection.GetOnlinePlayer(accepted.Requester) is { ActorPath: { } path }) {
            Context.ActorSelection(path).Tell(new CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD {
                RequesterCharId = accepted.Requester, RecipientCharId = wizard.CharId, Accept = true,
                NewRelationship = accepted.Relationship,
            });
        }

        var facts = AmbientKnowledge.Facts(accepted.Requester);
        var name = facts.Name ?? accepted.Name;
        if (wizard.Record.Friends.All(f => f.CharId != accepted.Requester)) {
            wizard.Record.Friends.Add(new AmbientFriendRecord {
                CharId = accepted.Requester, Name = name, LastZone = facts.Zone, LastQuest = facts.Quest,
                LastPlayedTogether = DateTime.UtcNow,
            });
            AmbientWizardCollection.Save(wizard.Record);
        }

        Logger.Information("Ambient wizard {Name} is now friends with {Friend}.", Logger.Args(wizard.Name, name));
        if (AmbientWizards.Settings.Chat) {
            var first = name?.Split(' ').FirstOrDefault() ?? "";
            Timers.StartSingleTimer($"thanks-{wizard.CharId}-{accepted.Requester}", new Later(wizard, w =>
                AmbientChat.Whisper(w, accepted.Requester, $"thanks for the add {first}!".Replace("  ", " "))), TimeSpan.FromSeconds(2));
        }
    }

    private void Unfriended(AmbientWizard wizard, ulong friend) {
        wizard.Record.Friends.RemoveAll(f => f.CharId == friend);
        AmbientWizardCollection.Save(wizard.Record);
        var character = wizard.Wizard;
        Task.Run(() => character.RemoveFriend(friend));
        if (wizard.FollowCharId == friend) {
            wizard.FollowCharId = 0;
        }
    }

    private void FriendOnline(AmbientWizard wizard, ulong friend) {
        if (wizard.FriendOf(friend) is not { } memory || !AmbientWizards.Settings.Chat || !wizard.Limiter.TryTake(DateTime.UtcNow, friend)) {
            return;
        }

        var facts = AmbientKnowledge.Facts(friend);
        if (AmbientChatBrain.GreetFriend(ChatFor(wizard, friend, facts), wizard.Turn++) is { } line) {
            Timers.StartSingleTimer($"hello-{wizard.CharId}-{friend}", new Later(wizard, w => AmbientChat.Whisper(w, friend, line)),
                TimeSpan.FromSeconds(4 + _rng.Next(6)));
        }

        _ = memory;
    }

    private void FriendArrived(AmbientWizard wizard, Wizard arrival) {
        if (wizard.FriendOf(arrival.CharId) is null) {
            return;
        }

        // Friends get a hello and, now and then, company for a few minutes.
        if (wizard.Activity is not (AmbientActivity.Fighting or AmbientActivity.Sparring or AmbientActivity.Helping)
            && _rng.NextDouble() < 0.6) {
            wizard.FollowCharId = arrival.CharId;
            wizard.Until = DateTime.UtcNow.AddSeconds(4);
        }

        if (AmbientWizards.Settings.Chat && wizard.Limiter.TryTake(DateTime.UtcNow, arrival.CharId)) {
            var facts = AmbientKnowledge.Facts(arrival.CharId);
            if (AmbientChatBrain.GreetFriend(ChatFor(wizard, arrival.CharId, facts), wizard.Turn++) is { } line) {
                Timers.StartSingleTimer($"greet-{wizard.CharId}-{arrival.CharId}", new Later(wizard, w => AmbientChat.Say(w, line)),
                    TimeSpan.FromSeconds(5 + _rng.Next(5)));
            }
        }

        Remember(wizard, arrival.CharId, helped: false);
    }

    private void Remember(AmbientWizard wizard, ulong player, bool helped) {
        var friend = wizard.Record.Friends.FirstOrDefault(f => f.CharId == player);
        if (friend is null) {
            return;
        }

        var facts = AmbientKnowledge.Facts(player);
        friend.Name = facts.Name ?? friend.Name;
        friend.LastZone = AmbientKnowledge.ZoneName(_zone) ?? friend.LastZone;
        friend.LastQuest = facts.Quest ?? friend.LastQuest;
        friend.LastPlayedTogether = DateTime.UtcNow;
        if (helped) {
            friend.TimesHelped++;
        }

        AmbientWizardCollection.Save(wizard.Record);
    }

}
