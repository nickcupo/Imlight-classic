// CLASSIC: bounded arena-only ambient wizards, using the existing in-memory wizard and internal zone paths.
// Their stable school/level IDs keep their Ranked standings across restarts; they never own an account or saved wizard.
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Classic.Pvp;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;

namespace Imlight.CoreLib.Classic.Arena;

internal static class ArenaAmbientParticipants {
    internal const int MaxParticipants = 64;
    private const int VariantsPerSchool = 8;
    private const ulong FirstId = AmbientWizardCollection.CharIdBase + 0x0100_0000;
    private const int IdentityCount = 50 * 7 * VariantsPerSchool;
    private static readonly object s_gate = new();
    private static readonly ConcurrentDictionary<ulong, Entry> s_entries = new();
    private static IActorRef? s_server;
    private static int s_serial;

    internal sealed class Entry(AmbientWizard wizard, ArenaPvpSkill skill = ArenaPvpSkill.Advanced) {
        internal ArenaPvpSkill Skill { get; } = skill;
        internal AmbientWizard Wizard { get; } = wizard;
        internal IActorRef? Actor;
        internal int Retiring;
    }

    internal static bool Enabled => ClassicArena.Enabled && AmbientWizards.Settings.Enabled && s_server is not null
        && ConfigurationManager.GetSetting("Classic.AmbientWizardArena")?.Trim().ToLowerInvariant() is not ("false" or "off" or "0");
    internal static void Configure(ActorSystem system, IActorRef server) {
        s_server = server;
        ClassicArena.Matchmaker(system); // normal clock starts at boot, without a human opening a guard
    }
    internal static ulong IdentityId(int level, int school, int variant)
        => FirstId + (ulong) ((Math.Clamp(level, 1, 50) - 1) * 7 * VariantsPerSchool + Math.Clamp(school, 0, 6) * VariantsPerSchool + Math.Clamp(variant, 0, VariantsPerSchool - 1));
    internal static bool IsIdentity(ulong id) => id >= FirstId && id < FirstId + IdentityCount;
    internal static bool IsReserved(ulong id) => s_entries.ContainsKey(id);
    internal static AmbientWizard? Wizard(ulong id) => s_entries.TryGetValue(id, out var entry) && Volatile.Read(ref entry.Retiring) == 0 ? entry.Wizard : null;
    internal static ArenaPvpSkill SkillOf(ulong id) => s_entries.TryGetValue(id, out var entry) ? entry.Skill : ArenaPvpSkill.Advanced;
    private static AmbientIdentity Identity(int level, int school, int variant) {
        var sizes = WizardNameBank.ClassicCreationNameCounts();
        var tables = new NameTableSizes(sizes.FirstBoy, sizes.FirstGirl, sizes.Middle, sizes.Last);
        var seed = unchecked((int) (IdentityId(level, school, variant) - FirstId) + 0x617200);
        return AmbientIdentity.Generate(seed, ClassicArena.Config!.HallZone, tables, ((byte) level, (byte) level))
            with { School = (AmbientSchool) school, Temper = AmbientTemper.Friendly };
    }
    internal static ArenaPlayer? Preview(int level, int school, ArenaPvpSkill skill) {
        if (!Enabled) return null;
        var variant = (int) skill * 2;
        lock (s_gate) {
            if ((s_entries.ContainsKey(IdentityId(level, school, variant)) || ActiveWizardDirectory.TryGetByCharId(IdentityId(level, school, variant), out _))
                && !s_entries.ContainsKey(IdentityId(level, school, variant + 1)) && !ActiveWizardDirectory.TryGetByCharId(IdentityId(level, school, variant + 1), out _))
                variant++;
        }
        var identity = Identity(level, school, variant);
        var id = IdentityId(level, school, variant);
        var gender = identity.Look.Female ? eGender.Female : eGender.Male;
        var name = new byte[] { (byte) (identity.Look.Female ? 0x80 : 0x82),
            (byte) (identity.NameKeys >> 16), (byte) (identity.NameKeys >> 8), (byte) identity.NameKeys };
        return new ArenaPlayer(id, id, name, WizardNameBank.GetEnglishName(identity.NameKeys, gender),
            level, ((AmbientSchool) school).ToString(), (short) (identity.Look.Female ? 1 : 0), true, skill);
    }
    internal static ArenaPlayer? Reserve(ActorSystem system, int level, int preferredSchool, ArenaPvpSkill? friendlySkill = null) {
        if (!Enabled || s_server is null) return null;
        level = Math.Clamp(level, 1, 50);
        preferredSchool = (preferredSchool % 7 + 7) % 7;
        lock (s_gate) {
            if (s_entries.Count >= MaxParticipants) return null;
            // A chosen friendly school stays exact; only autonomous/general reservations may fall back to another school.
            for (var schoolOffset = 0; schoolOffset < (friendlySkill is null ? 7 : 1); schoolOffset++) {
                var school = (preferredSchool + schoolOffset) % 7;
                var firstVariant = friendlySkill is { } skill ? (int) skill * 2 : 0;
                var endVariant = friendlySkill is not null ? firstVariant + 2 : VariantsPerSchool;
                for (var variant = firstVariant; variant < endVariant; variant++) {
                    var id = IdentityId(level, school, variant);
                    if (s_entries.ContainsKey(id) || ActiveWizardDirectory.TryGetByCharId(id, out _)) continue;
                    var identity = Identity(level, school, variant);
                    var record = AmbientWizardRecord.From(identity, id);
                    var character = AmbientWizards.BuildWizard(record);
                    if (!ArenaPvpLoadout.Prepare(character)) return null; // fail closed when no legal profile deck resolves
                    var wizard = new AmbientWizard(record, character) { Zone = ClassicArena.Config.HallZone, Activity = AmbientActivity.Sparring };
                    var entry = new Entry(wizard, friendlySkill ?? ArenaPvpSkill.Advanced);
                    if (!s_entries.TryAdd(id, entry)) continue;
                    try {
                        entry.Actor = system.ActorOf(ArenaAmbientParticipant.Props(entry, s_server), $"arena-ambient-{id:x}-{Interlocked.Increment(ref s_serial):x}");
                        Logger.Debug("Arena: reserved ambient participant {0} at {1}.", Logger.Args(id, entry.Actor.Path));
                        return ServerArenaWorld.PlayerOf(character) with { Ambient = true, FriendlySkill = friendlySkill };
                    } catch {
                        s_entries.TryRemove(id, out _); throw;
                    }
                }
            }
        }
        return null;
    }
    internal static void Travel(ulong id, string zone, string location, ulong run)
        => s_entries.GetValueOrDefault(id)?.Actor?.Tell(new ArenaAmbientParticipant.Trip(zone, location, run));
    internal static void Release(ulong id) {
        if (s_entries.TryGetValue(id, out var entry) && Interlocked.Exchange(ref entry.Retiring, 1) == 0)
            entry.Actor?.Tell(new ArenaAmbientParticipant.Release());
    }
    internal static void Remove(Entry entry) => s_entries.TryRemove(new KeyValuePair<ulong, Entry>(entry.Wizard.CharId, entry));
}

// One actor owns all mutable state and its endpoint, just as AmbientZone owns a street wizard.
internal sealed class ArenaAmbientParticipant : ReceiveActor, IWithTimers {
    internal sealed record Trip(string Zone, string Location, ulong Run);
    internal sealed record Release;
    private sealed record Fish;
    private readonly ArenaAmbientParticipants.Entry _entry;
    private readonly IActorRef _server;
    private readonly CoreObjectSerializer _serializer = new(versionable: false, behaviors: SerializerFlags.None);
    private ulong _run;
    private string _destination = "";
    private bool _transferring;
    private IActorRef? _duelActor;
    // CLASSIC: Props.Create's new-expression uses Akka's public-constructor activator, even for an internal actor type.
    public ArenaAmbientParticipant(ArenaAmbientParticipants.Entry entry, IActorRef server) {
        _entry = entry; _server = server;
        Receive<Trip>(Transfer);
        Receive<Release>(_ => Context.Stop(Self));
        Receive<Fish>(_ => FishPosition());
        Receive<AmbientInbox>(Inbox);
    }
    internal static Props Props(ArenaAmbientParticipants.Entry entry, IActorRef server)
        => Akka.Actor.Props.Create(() => new ArenaAmbientParticipant(entry, server));
    public ITimerScheduler Timers { get; set; } = null!;
    private AmbientWizard Wizard => _entry.Wizard;

    protected override void PreStart() {
        Wizard.Group = Self; Wizard.Driver = Self;
        Wizard.Endpoint = Context.ActorOf(AmbientEndpoint.Props(Wizard, Self), "endpoint");
        AmbientWizards.Register(Wizard);
        ActiveWizardDirectory.SetWizard(Wizard.Endpoint, Wizard.Wizard);
        ActiveWizardDirectory.SetGameObject(Wizard.Endpoint, Wizard.Wizard.GameObject);
        Timers.StartPeriodicTimer("fish", new Fish(), TimeSpan.FromSeconds(1));
        Logger.Debug("Arena: ambient participant {0} ready at {1}; game server {2}.",
            Logger.Args(Wizard.CharId, Wizard.Endpoint.Path, _server.Path));
    }

    private void Transfer(Trip trip) {
        if (_run == trip.Run && (Wizard.Present || _transferring)) return;
        RemoveFromZone();
        _run = trip.Run; _destination = trip.Zone; _transferring = true;
        Logger.Debug("Arena: ambient participant {0} requests {1} instance {2} through {3}.",
            Logger.Args(Wizard.CharId, trip.Zone, trip.Run, _server.Path));
        _server.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = trip.Zone, DestinationLocation = trip.Location, SendToClient = false,
            OwnerCharId = trip.Run, IsPrivate = true, ResetInstance = false,
        }, Wizard.Endpoint);
    }

    private void Inbox(AmbientInbox inbox) {
        switch (inbox.Message) {
            case ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP response:
                Arrived(response); break;
            case ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP:
                if (Wizard.Present) break;
                Wizard.Present = true; _transferring = false;
                Logger.Debug("Arena: ambient participant {0} entered {1} instance {2}.",
                    Logger.Args(Wizard.CharId, Wizard.Zone, _run));
                Spawn(null); FishPosition(); break;
            case ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE added when Wizard.Present && added.PlayerActor is not null:
                Spawn(added.PlayerActor); break;
            case COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL added:
                _duelActor = added.DuelActor;
                Wizard.Wizard.IsInDuel = true; Wizard.DuelSigil = added.Duel?.SigilId ?? 0;
                Wizard.Position = added.SlotPosition; Wizard.Wizard.Location = added.SlotPosition;
                Wizard.Wizard.Orientation = new Vector3(0, 0, added.SlotOrientation);
                Wizard.Yaw = AmbientWizards.Heading(added.SlotOrientation);
                break;
            case COMBAT_106_PROTOCOL.MSG_COMBATWIN:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEATH:
                Wizard.Wizard.IsInDuel = false;
                break; // the matchmaker owns result delivery and release, including no-contest and failed arrivals
        }
    }

    private void Arrived(ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP response) {
        if (response.ErrorCode != 0 || response.ZoneActorRef is null) {
            Logger.Warning("Arena: ambient participant {0} could not enter its match.", Logger.Args(Wizard.CharId));
            Context.Stop(Self); return;
        }
        Wizard.Zone = _destination; Wizard.ZoneActor = response.ZoneActorRef; Wizard.ZoneDisplayName = response.ZoneDisplayName ?? "";
        Wizard.Position = response.Location; Wizard.Yaw = AmbientWizards.Heading(response.Orientation);
        var character = Wizard.Wizard;
        character.Zone = Wizard.Zone; character.ZoneDisplayName = Wizard.ZoneDisplayName;
        character.Location = response.Location; character.Orientation = new Vector3(0, 0, response.Orientation);
        var gameObject = WizardObjectLoader.GetPlayerGameObject(character);
        gameObject.m_nMobileID = response.MobileId;
        character.GameObject = gameObject;
        ActiveWizardDirectory.SetGameObject(Wizard.Endpoint, gameObject);
        response.ZoneActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYER {
            PlayerActor = Wizard.Endpoint, PlayerObject = gameObject, Wizard = character, ActualWizardName = Wizard.Name,
        }, Wizard.Endpoint);
    }

    private void Spawn(IActorRef? player) {
        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_serializer.Serialize(WizardObjectLoader.GetPlayerGameObject(Wizard.Wizard), flags, out var data)) return;
        var message = new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = data };
        if (player is not null) player.Tell(message);
        else Wizard.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = message, Selfless = true, Sender = Wizard.Endpoint, Targets = ZoneBroadcastTarget.Players,
        });
    }
    private void FishPosition() {
        if (!Wizard.Present || Wizard.Wizard.IsInDuel) return;
        Wizard.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_PLAYERMOVE {
            PlayerObject = Wizard.Wizard.GameObject, PlayerActor = Wizard.Endpoint, PlayerWizard = Wizard.Wizard,
        }, Wizard.Endpoint);
    }
    private void RemoveFromZone() {
        if (Wizard.Present) Wizard.ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            PlayerActor = Wizard.Endpoint, GlobalId = Wizard.Wizard.GameObjectID,
            MobileId = Wizard.Wizard.GameObject.m_nMobileID, IsPlayerStillConnected = false,
        }, Wizard.Endpoint);
        Wizard.Present = false;
        OnlinePlayerCollection.RemoveVirtualOnlinePlayer(Wizard.CharId);
    }
    protected override void PostStop() {
        // CLASSIC: release also closes an active circle after no-contest/timeout. A normally reported duel ignores this.
        if (_run != 0)
            _duelActor?.Tell(new ArenaAmbientFailed(_run));
        RemoveFromZone();
        ActiveWizardDirectory.Remove(Wizard.Endpoint);
        AmbientWizards.Unregister(Wizard);
        ArenaAmbientParticipants.Remove(_entry);
        ArenaMatchmaker.Instance?.AmbientLost(Wizard.CharId);
        base.PostStop();
    }
    protected override void PostRestart(Exception reason) => Context.Stop(Self); // failed participants are cancelled, never resurrected as ghosts
}
