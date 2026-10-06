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
        if (AtticEnabled() && context.OwnerId == wizard.CharId)
            SendAttic(HousingAtticCollection.Load(wizard.CharId, create: true), wizard);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS))]
    private void ReceiveRequest(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS message) {
        if (!Ready(out var wizard)) return;
        if (AtticEnabled() && _attach.OwnerId == wizard.CharId
            && HousingAtticCollection.Load(wizard.CharId) is { } attic
            && HousingAtticCodec.TryRequests(message.Data, attic, out var packages)) {
            foreach (var index in packages) SendAtticBlob(attic, attic.Packages[index]);
            return;
        }
        var ledger = HousingCollection.Load(_attach.OwnerId);
        if (ledger is null || !HousingCodec.AcceptRequest(message.Data, _attach.ZoneId, ledger.PackageNumber)) return;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB { Data = HousingCodec.Encode(HousingCodec.Blob(ledger, _attach.ZoneId)), UserData = 0 });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGOBJECT))]
    private void ReceivePlace(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGOBJECT message) {
        if (!Editable(out var wizard)) return;
        if (message.SwitchCastleBlock != 0) { Refuse(); return; }
        if (AtticEnabled() && HousingAtticCollection.Load(wizard.CharId) is { } attic
            && attic.TryFind(message.ObjectID, _attach.DynamicServerProcId, out _, out _)) {
            var placed = HousingAtticCollection.PlaceFromAttic(wizard, wizard.CharId, message.ObjectID,
                _attach.DynamicServerProcId, HousingRules.ApprovedAtticCapacity, message.LocX, message.LocY, message.LocZ, message.Yaw);
            if (!placed.Saved) { Refuse(placed.Error); return; }
            SendAtticPatches(placed, wizard);
            SendRoomAdd(placed.Room, placed.Entry, placed.RoomSlot);
            return;
        }
        var result = HousingCollection.Place(wizard, _attach.OwnerId, message.ObjectID, message.LocX, message.LocY, message.LocZ, message.Yaw);
        if (!result.Saved) { Refuse(result.Error); return; }
        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = result.Entry.ItemId });
        SendRoomAdd(result.Ledger, result.Entry, result.Slot);
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

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTATTIC))]
    private void ReceiveAttic(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTATTIC message) {
        if (!AtticEditable(out var wizard) || message.GlobalID != wizard.GameObjectID) return;
        var attic = HousingAtticCollection.Load(wizard.CharId, create: true);
        SendAttic(attic, wizard);
        // Native0x140f15fc0 raises OpenAtticWindow only after this server reply.
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTATTIC { GlobalID = wizard.GameObjectID });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETOATTIC))]
    private void ReceiveMoveToAttic(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETOATTIC message) {
        if (!AtticEditable(out var wizard) || message.GlobalID != wizard.GameObjectID) return;
        var result = HousingAtticCollection.MoveToAttic(wizard, wizard.CharId, message.ItemID, _attach.DynamicServerProcId,
            HousingRules.ApprovedAtticCapacity);
        if (!result.Saved) { Refuse(result.Error); return; }
        if (result.FromBackpack) SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID, ItemID = result.Entry.ItemId,
        });
        SendAtticPatches(result, wizard);
        SendRoomDeletes(result);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEFROMATTIC))]
    private void ReceiveMoveFromAttic(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEFROMATTIC message) {
        if (!AtticEditable(out var wizard) || message.GlobalID != wizard.GameObjectID) return;
        var result = HousingAtticCollection.MoveFromAttic(wizard, wizard.CharId, message.ItemID, _attach.DynamicServerProcId,
            HousingRules.ApprovedAtticCapacity);
        if (!result.Saved) { Refuse(result.Error); return; }
        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID, SerializedItem = result.ItemData,
        });
        SendAtticPatches(result, wizard);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFROMATTIC))]
    private void ReceiveDiscardAttic(WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFROMATTIC message) {
        if (!AtticEditable(out var wizard) || message.GlobalID != wizard.GameObjectID) return;
        var result = HousingAtticCollection.Discard(wizard, wizard.CharId, message.ItemID, _attach.DynamicServerProcId,
            HousingRules.ApprovedAtticCapacity);
        if (!result.Saved) { Refuse(result.Error); return; }
        SendAtticPatches(result, wizard);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PICKUPALL))]
    private void ReceivePickUpAll(WIZARDHOUSING_50_PROTOCOL.MSG_PICKUPALL message) {
        if (!AtticEditable(out var wizard)) return;
        var result = HousingAtticCollection.PickUpAll(wizard, wizard.CharId, _attach.DynamicServerProcId,
            HousingRules.ApprovedAtticCapacity, message.Exceptions);
        if (result.Saved) {
            SendAtticPatches(result, wizard);
            SendRoomDeletes(result);
        }
        else Refuse(result.Error);
        // Native0x140f59080 treats any nonzero error as attic-full. The count is an
        // acknowledgement, not a cache invalidation; every change above has its own version.
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_PICKUPALL {
            ItemCount = result.Saved ? (uint)result.Added.Count : 0,
            ErrorCode = (sbyte)(result.CapacityExceeded ? 1 : 0), Exceptions = "",
        });
    }

    private void SendRoomAdd(HousingLedger ledger, HousingEntry entry, int slot) {
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHADDHOUSINGOBJECT {
            ObjectID = entry.TemplateId, LocX = entry.X, LocY = entry.Y, LocZ = entry.Z, Yaw = entry.Yaw,
            SubType = HousingRules.SubType, PackageNumber = (uint)ledger.PackageNumber, VersionNumber = ledger.Version,
            GIDID = (uint)slot, Data = "", ColorBits = 0,
        }, isSelfless: false);
    }

    private void SendRoomDeletes(HousingAtticResult result) {
        foreach (var patch in result.RoomDeleted) ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHDELETEHOUSINGOBJECT {
            SubType = HousingRules.SubType, PackageNumber = (uint)result.Room.PackageNumber,
            VersionNumber = patch.Version, GIDID = (uint)patch.Slot,
        }, isSelfless: false);
    }

    private void SendAttic(AtticLedger attic, Wizard wizard) {
        if (attic is null || !attic.Valid()) return;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SETATTICID { GlobalID = wizard.GameObjectID, AtticID = attic.ContainerId });
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEATTICCOUNT { GlobalID = wizard.GameObjectID, ItemCount = attic.Count });
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS { Data = HousingCodec.Encode(HousingAtticCodec.Manifest(attic)) });
        foreach (var package in attic.Packages) SendAtticBlob(attic, package);
    }

    private void SendAtticBlob(AtticLedger attic, AtticPackage package) => SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB {
        Data = HousingCodec.Encode(HousingAtticCodec.Blob(attic, package)), UserData = package.UserData,
    });

    private void SendAtticPatches(HousingAtticResult result, Wizard wizard) {
        foreach (var patch in result.Added) SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHADDATTIC {
            BlobGID = result.Attic.ContainerId, ObjectID = patch.Entry.TemplateId, SubType = HousingRules.AtticSubType,
            PackageNumber = (uint)patch.PackageNumber, VersionNumber = patch.Version, GIDID = patch.CacheIndex,
            Data = "", PrimaryColorIndex = 0,
        });
        foreach (var patch in result.Deleted) SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHDELETEATTIC {
            BlobGID = result.Attic.ContainerId, ObjectID = patch.Entry.TemplateId, SubType = HousingRules.AtticSubType,
            PackageNumber = (uint)patch.PackageNumber, VersionNumber = patch.Version, GIDID = patch.CacheIndex,
        });
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEATTICCOUNT { GlobalID = wizard.GameObjectID, ItemCount = result.Attic.Count });
    }

    private static bool AtticEnabled() => HousingRules.ApprovedAtticCapacity > 0;
    private bool AtticEditable(out Wizard wizard) {
        if (!Editable(out wizard)) return false;
        if (AtticEnabled()) return true;
        Refuse("Attic storage is awaiting its approved Classic capacity.");
        return false;
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
        if (AtticEnabled() && Ready(out var wizard) && _attach.OwnerId == wizard.CharId)
            SendAttic(HousingAtticCollection.Load(wizard.CharId), wizard);
    }
}
