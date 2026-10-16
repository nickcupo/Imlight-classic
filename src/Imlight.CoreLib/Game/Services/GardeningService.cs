using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic.Gardening;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

// CLASSIC: gardening (December 2010) in a castle's outdoor garden, wintertusk-2011 only (GardenRules.Enabled). Off,
// this service does nothing at all: no manifest, no blob, no reply. The owner and visitors see the same saved garden;
// only the owner changes it. Identity comes from the trusted housing attach, never from a client id.
internal sealed class GardeningService(SessionActor sessionActor) : MessageService(sessionActor) {
    private HousingAttachContext _attach;
    private HousingRoomIdentity Room => new(_attach.OwnerId, _attach.HousingDeedId, _attach.Zone);
    private const string Tick = "garden-tick";

    protected static Props Props(SessionActor parentActor) => Akka.Actor.Props.Create(() => new GardeningService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttached(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        _attach = null;
        Timers?.Cancel(Tick);
        if (!GardenRules.Enabled) return;
        var context = SessionActor.HousingAttach;
        var wizard = GetActiveWizard();
        if (context is null || wizard is null || context.CharacterId != wizard.CharId || !GardenRoom(context)) return;
        _attach = context;
        var ledger = GardenCollection.Load(context.OwnerId, context.HousingDeedId, context.Zone, create: true);
        if (ledger is null) return;
        var advanced = GardenCollection.Advance(wizard, Room);
        SendManifest(advanced.Ledger ?? ledger);
        Timers?.StartPeriodicTimer(Tick, Tick, TimeSpan.FromSeconds(60));
    }

    protected override void ConfigureReceivers() {
        Receive<string>(value => value == Tick, _ => ReceiveTick());
        base.ConfigureReceivers();
    }

    private void ReceiveTick() {
        if (!Ready(out var wizard)) { Timers?.Cancel(Tick); return; }
        Publish(GardenCollection.Advance(wizard, Room), wizard);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS))]
    private void ReceiveRequest(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS message) {
        if (!Ready(out _)) return;
        var ledger = GardenCollection.Load(_attach.OwnerId, _attach.HousingDeedId, _attach.Zone);
        if (ledger is null || !GardenCodec.AcceptRequest(message.Data, _attach.ZoneId, ledger)) return;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB { Data = GardenCodec.Blob(ledger, _attach.ZoneId), UserData = GardenRules.PackageUserData });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMAND))]
    private void ReceiveCommand(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMAND message) {
        if (!Ready(out var wizard)) return;
        var result = message.Command switch {
            GardenRules.CommandPlant => GardenCollection.Plant(wizard, Room, message.TemplateID, message.ObjectGID,
                message.PositionX, message.PositionY, message.PositionZ),
            GardenRules.CommandPlow => GardenCollection.Plow(wizard, Room, message.PositionX, message.PositionY, message.PositionZ),
            _ => new GardenResult("That gardening action is not available yet."),
        };
        if (result.Saved && result.RemovedItemId != 0) SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID, ItemID = result.RemovedItemId,
        });
        Finish(result, wizard);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL))]
    private void ReceiveCast(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL message) {
        if (!Ready(out var wizard)) return;
        var info = GardenCatalog.Current.Spell(message.SpellTemplateID);
        var result = info?.Kind switch {
            GardenSpellKind.SoilPreparation => GardenCollection.PrepareSoil(wizard, Room, message.SpellTemplateID,
                message.PositionX, message.PositionY, message.PositionZ),
            GardenSpellKind.Growing => GardenCollection.CastGrowing(wizard, Room, message.SpellTemplateID,
                message.PositionX, message.PositionY, message.PositionZ),
            _ => new GardenResult("That gardening spell is not available yet."),
        };
        if (result.Saved) {
            // Native ClientHousingManager::MSG_GardeningCastSpell (0x140f5a930) plays the cast for PlayerGID at the point.
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL {
                SpellTemplateID = message.SpellTemplateID, PlayerGID = wizard.GameObjectID,
                PositionX = message.PositionX, PositionY = message.PositionY, PositionZ = message.PositionZ,
            }, isSelfless: false);
            if (result.Energy is { } energy) SendToSocket(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
                GlobalID = wizard.GameObjectID, Energy = energy, MaxEnergy = wizard.GameStats?.m_energyMax ?? energy,
                TickTime = (int)result.EnergyTick,
            });
        }
        Finish(result, wizard);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANT))]
    private void ReceiveHarvest(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANT message) {
        if (!Ready(out var wizard)) return;
        var result = GardenCollection.Harvest(wizard, Room, message.PositionX, message.PositionY, message.PositionZ);
        if (result.Saved) SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEGARDENINGXP {
            GlobalID = wizard.GameObjectID, XP = result.GardeningXp, Level = result.GardeningLevel, IsPet = 0,
        });
        Finish(result, wizard);
    }

    private void Finish(GardenResult result, Wizard wizard) {
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
        if (result.Saved) Publish(result, wizard);
        else {
            InformGameClient(result.Error ?? "That didn't work.");
            // Restore the client's view from the saved garden.
            if (GardenCollection.Load(_attach.OwnerId, _attach.HousingDeedId, _attach.Zone) is { } ledger) SendManifest(ledger);
        }
        // Native 0x140f5aa60: the gardening window stays busy until this reply.
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
    }

    private void Publish(GardenResult result, Wizard wizard) {
        if (result?.Ledger is not { } ledger) return;
        foreach (var p in result.Patches) ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHGARDENING {
            Command = p.Command, PositionX = p.X, PositionY = p.Y, PositionZ = p.Z, PackageNumber = (uint)ledger.PackageNumber,
            VersionNumber = p.Version, GIDID = GardenRules.PackageUserData, TemplateID = p.TemplateOrFlags, Time = p.Time,
        }, isSelfless: false);
    }

    private void SendManifest(GardenLedger ledger) => SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS {
        Data = HousingCodec.Encode(GardenCodec.Manifest(ledger, _attach.ZoneId)),
    });

    // Stage 1 gardens are a castle's outdoor lot (prepared soil). Dorm/indoor planters are a later stage.
    private static bool GardenRoom(HousingAttachContext context) => context.HousingDeedId != 0
        && HouseCollection.TryGetOwned(context.OwnerId, context.HousingDeedId, out var house)
        && HouseCatalog.Same(house.ExteriorZone, context.Zone)
        && HouseCatalog.TryRoom(house.TemplateId, context.Zone, out _);

    private bool Ready(out Wizard wizard) {
        wizard = null;
        if (_attach is null || SessionActor.TransferringOut || !GardenRules.Enabled || SessionActor.HousingAttach != _attach) return false;
        wizard = GetActiveWizard();
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return false; }
        return wizard is not null && wizard.CharId == _attach.CharacterId && HouseCatalog.Same(wizard.Zone, _attach.Zone);
    }
}
