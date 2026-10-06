using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

// CLASSIC: native ordinary furniture blobs for private dorms. All owner/zone identity comes from
// the successful server attach, not a character id or package supplied by a client.
internal sealed class HousingService(SessionActor sessionActor) : MessageService(sessionActor) {
    private HousingAttachContext _attach;

    protected static Props Props(SessionActor parentActor) => Akka.Actor.Props.Create(() => new HousingService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttached(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var context = SessionActor.HousingAttach;
        var wizard = GetActiveWizard();
        if (context is null || wizard is null || context.CharacterId != wizard.CharId || context.OwnerId == 0
            || !HousingRules.IsDorm(context.Zone) || !string.Equals(context.Zone, wizard.Zone, System.StringComparison.OrdinalIgnoreCase)
            || !Enabled()) return;
        _attach = context;
        // A first visitor may arrive before the owner has ever furnished the room. Both use the
        // same owner ledger; a visitor never receives a separate mutable copy.
        SendManifest(HousingCollection.Load(context.OwnerId, create: true));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS))]
    private void ReceiveRequest(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS message) {
        if (!Ready(out _)) return;
        var ledger = HousingCollection.Load(_attach.OwnerId);
        if (ledger is null || !HousingCodec.AcceptRequest(message.Data, _attach.ZoneId, ledger.PackageNumber)) return;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB { Data = HousingCodec.Encode(HousingCodec.Blob(ledger, _attach.ZoneId)), UserData = 0 });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGOBJECT))]
    private void ReceivePlace(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGOBJECT message) {
        if (!Editable(out var wizard)) return;
        if (message.SwitchCastleBlock != 0) { Refuse(); return; }
        var result = HousingCollection.Place(wizard, _attach.OwnerId, message.ObjectID, message.LocX, message.LocY, message.LocZ, message.Yaw);
        if (!result.Saved) { Refuse(result.Error); return; }
        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = result.Entry.ItemId });
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHADDHOUSINGOBJECT {
            ObjectID = result.Entry.TemplateId, LocX = result.Entry.X, LocY = result.Entry.Y, LocZ = result.Entry.Z, Yaw = result.Entry.Yaw,
            SubType = HousingRules.SubType, PackageNumber = (uint)result.Ledger.PackageNumber, VersionNumber = result.Ledger.Version,
            GIDID = (uint)result.Slot, Data = "", ColorBits = 0,
        }, isSelfless: false);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEHOUSINGOBJECT))]
    private void ReceiveUpdate(WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEHOUSINGOBJECT message) {
        if (!Editable(out var wizard)) return;
        // Native ordinary scale is1; MSG_UpdateHousingObject rounds brightness*100 (+.005).
        // Later scale/brightness and castle-block changes are not part of this room implementation.
        if (message.SwitchCastleBlock != 0 || message.Scale != 1 || message.Brightness != 100) { Refuse(); return; }
        var result = HousingCollection.Update(wizard, _attach.OwnerId, message.ObjectGID, _attach.DynamicServerProcId,
            message.LocX, message.LocY, message.LocZ, message.Yaw);
        if (!result.Saved) { Refuse(result.Error); return; }
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHUPDATEHOUSINGOBJECT {
            ObjectID = result.Entry.TemplateId, LocX = result.Entry.X, LocY = result.Entry.Y, LocZ = result.Entry.Z, Yaw = result.Entry.Yaw,
            SubType = HousingRules.SubType, PackageNumber = (uint)result.Ledger.PackageNumber, VersionNumber = result.Ledger.Version,
            GIDID = (uint)result.Slot, SwitchTemplateID = 0, UseExtendedYaw = message.UseExtendedYaw, Scale = 1, Brightness = 100,
        }, isSelfless: false);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_PICKUPOBJECT))]
    private void ReceivePickup(GAME_5_PROTOCOL.MSG_PICKUPOBJECT message) {
        if (!Editable(out var wizard)) return;
        var result = HousingCollection.Pickup(wizard, _attach.OwnerId, message.GameObjectID, _attach.DynamicServerProcId);
        if (!result.Saved) { Refuse(result.Error); return; }
        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.GameObjectID, SerializedItem = result.ItemData });
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHDELETEHOUSINGOBJECT {
            SubType = HousingRules.SubType, PackageNumber = (uint)result.Ledger.PackageNumber,
            VersionNumber = result.Ledger.Version, GIDID = (uint)result.Slot,
        }, isSelfless: false);
    }

    private void SendManifest(HousingLedger ledger) {
        if (ledger is not null) SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS {
            Data = HousingCodec.Encode(HousingCodec.Manifest(ledger, _attach.ZoneId)),
        });
    }

    private static bool Enabled() => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Housing);

    private bool Ready(out Wizard wizard) {
        wizard = null;
        if (_attach is null || SessionActor.TransferringOut || !Enabled() || SessionActor.HousingAttach != _attach) return false;
        wizard = GetActiveWizard();
        return wizard is not null && wizard.CharId == _attach.CharacterId && HousingRules.IsDorm(wizard.Zone);
    }

    private bool Editable(out Wizard wizard) {
        if (!Ready(out wizard)) return false;
        if (HousingRules.CanEdit(wizard.CharId, _attach.OwnerId, wizard.Zone) && !wizard.IsInDuel) return true;
        Refuse("Only the owner can change this room's furniture.");
        return false;
    }

    private void Refuse(string reason = null) {
        InformGameClient(reason ?? "That furniture change is not supported in the dorm.");
        // A refused placement/move must restore the real client's optimistic view from saved state.
        if (_attach is not null && HousingCollection.Load(_attach.OwnerId) is { } ledger) {
            SendManifest(ledger);
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB { Data = HousingCodec.Encode(HousingCodec.Blob(ledger, _attach.ZoneId)), UserData = 0 });
        }
    }
}
