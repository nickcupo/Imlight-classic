#nullable enable

using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// CLASSIC: the door between a house's lot and its inside. Both house zones place House_Teleport_01 (template 160429:
/// only a TeleportProximityBehavior, radius 108, invisible). The r806919 client has no distance check of its own: the
/// behavior only answers the server's Housing MSG_REQUESTHOUSINGZONE (0x14222cc30: it loads the TemplateGID's
/// DeedBehavior m_lotWorldZone, starts the transition, 0x1416df260, and replies with its GlobalID and a Status). So the
/// server notices the wizard stepping into the radius and tells the session (MSG_HOUSEDOOR); ZoneService.Housing sends
/// the request and, on the client's Status 0, the transfer that keeps the lot. Only in an approved house room of an
/// active classic profile (classic-data/housing/houses-october-2010.yaml).
/// </summary>
internal sealed class HouseDoorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory {

    private readonly PlayersInRange _inRange = new();
    private TeleportProximityBehaviorTemplate? _door;
    private bool _active;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(behavior => behavior is TeleportProximityBehaviorTemplate);

    public override void OnStart() {
        _door = (Entity.Template as GameObjectTemplate)?.m_behaviors.OfType<TeleportProximityBehaviorTemplate>().FirstOrDefault();
        _active = _door is not null && _door.m_radius > 0 && ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
            && Zone?.ZoneName is { } zone && HouseCatalog.IsApprovedRoom(zone);
    }

    // A wizard who arrives (or logs in) inside the radius does not walk through the door: only stepping in does.
    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_active) _inRange.Update(playerObj.m_globalID.Full, playerActor, InRange(playerObj.m_location));
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) => _inRange.Remove(playerActor);

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (!_active) return;
        if (_inRange.Update(playerObj.m_globalID.Full, playerActor, InRange(playerObj.m_location)) != RangeChange.Entered) return;

        playerActor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_HOUSEDOOR {
            DoorId = Entity.ActiveGameObject.m_globalID.Full,
            Zone = Zone.ZoneName,
        });
    }

    private bool InRange(Vector3 player)
        => Within(player, Entity.ActiveGameObject.m_location, _door!);

    /// <summary>True when <paramref name="player"/> is inside the door's radius (around its location plus offset).</summary>
    internal static bool Within(Vector3 player, Vector3 door, TeleportProximityBehaviorTemplate template) {
        var offset = new Vector3(player.X - door.X - template.m_offsetX, player.Y - door.Y - template.m_offsetY,
            player.Z - door.Z - template.m_offsetZ);
        return offset.LengthSquared() <= template.m_radius * template.m_radius;
    }

}
