using System;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class ZoneService {
    private ulong _pendingDoorId; // CLASSIC: the house door (House_Teleport_01) the client was asked to load for
    private string _pendingDoorZone = "";
    private DateTime _pendingDoorUntil;
    private ulong _pendingHomeDeed;
    private ulong _pendingHomeCharacter;
    private DateTime _pendingHomeUntil;

    // Housing oldid68623 (2010-05-10): selected Island is home; pressing home while
    // inside one's house goes to the dorm. The existing dormant fallback handles that case.
    private bool TryGoToHouse(Wizard wizard) {
        if (wizard is null || wizard.IsInDuel || SessionActor.TransferringOut || _isTransferQueued) return false;
        var attach = SessionActor.HousingAttach;
        if (attach?.OwnerId == wizard.CharId && attach.HousingDeedId != 0
            && HouseCatalog.IsApprovedRoom(wizard.Zone)) return false;
        if (!HouseCollection.TryGetEquipped(wizard, out var house)) return false;
        _pendingHomeDeed = house.DeedId; _pendingHomeCharacter = wizard.CharId;
        _pendingHomeUntil = DateTime.UtcNow.AddMinutes(2);
        // Native DeedBehavior0x1421bc940 preloads lot+structure interior. It stores/echoes
        // Teleport and Arguments unchanged; they never authorize a server destination.
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTDEEDZONE {
            GlobalID = house.DeedId, Status = 0, Teleport = 1, Arguments = "",
        });
        return true;
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTDEEDZONE))]
    private void ReceiveDeedZone(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTDEEDZONE message) {
        var wizard = GetActiveWizard();
        if (wizard is null || _pendingHomeDeed == 0 || message.GlobalID != _pendingHomeDeed
            || wizard.CharId != _pendingHomeCharacter || DateTime.UtcNow > _pendingHomeUntil
            || wizard.IsInDuel || SessionActor.TransferringOut || _isTransferQueued) return;
        if (message.Status == 2) return; // native PatchingCompleteEvent callback0x1421bcf30 replies when ready
        var deed = _pendingHomeDeed; _pendingHomeDeed = 0; _pendingHomeCharacter = 0;
        if (message.Status != 0) { InformGameClient("Your house could not be loaded. Please try again."); return; }
        if (!HouseCollection.TryGetEquipped(wizard, out var house) || house.DeedId != deed
            || !HouseCatalog.TryRoom(house.TemplateId, house.ExteriorZone, out _)) return;
        SendTeleportEffects(); wizard.SetTimeHomeLastClicked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Timers.StartSingleTimer("zonetransfer", new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = house.ExteriorZone, DestinationLocation = "Start", SendToClient = true,
            IsPrivate = true, OwnerCharId = wizard.CharId, HousingDeedId = house.DeedId,
        }, TimeSpan.FromSeconds(TELEPORT_EFFECTS_TIME));
    }

    /// <summary>
    /// CLASSIC: where a house's door leads from <paramref name="zone"/>, and the TemplateGID the r806919 client loads the
    /// destination from (its DeedBehavior m_lotWorldZone): outside, the structure (160432 for the Wooded Cottage, whose
    /// lot world zone is the inside); inside, the deed (160431, whose lot world zone is the lot). Null when the zone is
    /// neither side of the house.
    /// </summary>
    internal static (string Zone, uint TemplateId)? DoorDestination(HouseDefinition house, string zone)
        => HouseCatalog.Same(zone, house.ExteriorZone) ? (house.InteriorZone, house.StructureTemplateId)
         : HouseCatalog.Same(zone, house.InteriorZone) ? (house.ExteriorZone, house.TemplateId)
         : null;

    /// <summary>The arrival point at the other side: its own House_Teleport_01 location (outside the door's radius).</summary>
    internal const string DOOR_LOCATION = "House_Teleport_01";

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_HOUSEDOOR))]
    private void ReceiveHouseDoor(CLASSIC_FEATURES_PROTOCOL.MSG_HOUSEDOOR message) {
        var wizard = GetActiveWizard();
        var refused = wizard is null ? "no wizard"
            : wizard.IsInDuel || SessionActor.TransferringOut || _isTransferQueued ? "busy (duel or transfer)"
            : !HouseCatalog.Same(wizard.Zone, message.Zone) ? $"in {wizard.Zone}, not {message.Zone}"
            : _currentHousingDeedId == 0 ? "no lot on this session" : null;
        HouseRecord record = null;
        HouseDefinition house = null;
        if (refused is null && !HouseCollection.TryGetOwned(_currentInstanceOwner, _currentHousingDeedId, out record))
            refused = $"lot {_currentInstanceOwner}/{_currentHousingDeedId} not owned";
        if (refused is null && !HouseCatalog.TryGet(record.TemplateId, out house)) refused = $"house {record.TemplateId} not approved";
        var door = refused is null ? DoorDestination(house, message.Zone) : null;
        if (refused is null && door is null) refused = $"{message.Zone} is neither side of house {record.TemplateId}";
        if (refused is not null) {
            Logger.Debug("House door {0} for {1}: not opened ({2}).", Logger.Args(message.DoorId, wizard?.CharId, refused));
            return;
        }
        Logger.Debug("House door {0} for {1}: asking the client to load {2} (template {3}).",
            Logger.Args(message.DoorId, wizard.CharId, door.Value.Zone, door.Value.TemplateId));
        _pendingDoorId = message.DoorId; _pendingDoorZone = message.Zone;
        _pendingDoorUntil = DateTime.UtcNow.AddMinutes(2);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONE {
            GlobalID = message.DoorId, Status = 0, TemplateGID = door.Value.TemplateId,
        });
    }

    // The client's TeleportProximityBehavior answers with the door's id: Status 0 when the destination is loaded (after
    // patching it, if needed), 1 when the template has no DeedBehavior.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONE))]
    private void ReceiveHousingZone(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONE message) {
        var wizard = GetActiveWizard();
        if (wizard is null || _pendingDoorId == 0 || message.GlobalID != _pendingDoorId || DateTime.UtcNow > _pendingDoorUntil
            || !HouseCatalog.Same(wizard.Zone, _pendingDoorZone)) return;
        var zone = _pendingDoorZone; _pendingDoorId = 0; _pendingDoorZone = "";
        Logger.Debug("House door {0} for {1}: the client answered status {2}.", Logger.Args(message.GlobalID, wizard.CharId, message.Status));
        if (message.Status != 0) { InformGameClient("Your house could not be loaded. Please try again."); return; }
        if (wizard.IsInDuel || SessionActor.TransferringOut || _isTransferQueued || _currentHousingDeedId == 0
            || !HouseCollection.TryGetOwned(_currentInstanceOwner, _currentHousingDeedId, out var record)
            || !HouseCatalog.TryGet(record.TemplateId, out var house)
            || DoorDestination(house, zone) is not { } door) return;
        // KeepInstance inside a validated lot keeps its owner and deed (ReceiveZoneTransfer; visitors are checked there
        // with HouseTransferEntries.MayEnter).
        Timers.StartSingleTimer("zonetransfer", new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = door.Zone, DestinationLocation = DOOR_LOCATION, SendToClient = true, KeepInstance = true,
        }, TimeSpan.Zero);
    }
}
