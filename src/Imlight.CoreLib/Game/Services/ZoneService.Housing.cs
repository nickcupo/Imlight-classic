using System;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class ZoneService {
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
}
