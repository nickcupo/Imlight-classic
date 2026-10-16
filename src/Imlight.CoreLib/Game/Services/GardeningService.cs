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

// CLASSIC: gardening (December 2010) in a castle's garden and rooms and in the dorm, wintertusk-2011 only
// (GardenRules.Enabled). Off, this service does nothing at all: no manifest, no blob, no reply. The owner and visitors see
// the same saved garden; only the owner changes it. Identity comes from the trusted housing attach, never from a client id.
//
// The r806919 client's gardening window (Ghidra wgc.gpr) works in two steps for every tool: on the click it plays the
// cast and, only when other players are in the zone (0x140f28270: player count > 1), tells them with
// MSG_GARDENINGCASTSPELL; when the cast animation ends it sends MSG_GARDENINGCOMMAND with its tool mode as the command
// (0x140fb4df0; modes from the selected spell's type, 0x140fbe7b0) and waits, busy, for MSG_GARDENINGCOMMANDRESPONSE
// (0x140f5aa60 -> 0x140fb51a0 clears the busy flag 0x76c). So the cast is only relayed; the command does the work and is
// answered exactly once.
internal sealed class GardeningService(SessionActor sessionActor) : MessageService(sessionActor) {
    private HousingAttachContext _attach;
    private bool _indoors;
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
        if (context is null || wizard is null || context.CharacterId != wizard.CharId || !GardenRoom(context, out var indoors)) return;
        _attach = context;
        _indoors = indoors;
        var ledger = GardenCollection.Load(context.OwnerId, context.HousingDeedId, context.Zone, create: true);
        if (ledger is null) return;
        var advanced = GardenCollection.Advance(wizard, Room, _indoors);
        SendManifest(advanced.Ledger ?? ledger);
        Timers?.StartPeriodicTimer(Tick, Tick, TimeSpan.FromSeconds(60));
    }

    protected override void ConfigureReceivers() {
        Receive<string>(value => value == Tick, _ => ReceiveTick());
        base.ConfigureReceivers();
    }

    private void ReceiveTick() {
        if (!Ready(out var wizard)) { Timers?.Cancel(Tick); return; }
        Publish(GardenCollection.Advance(wizard, Room, _indoors), wizard);
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
        if (!Ready(out var wizard)) { NotHere(); return; }
        float x = message.PositionX, y = message.PositionY, z = message.PositionZ;
        var t = message.TemplateID;
        var result = message.Command switch {
            GardenRules.CommandSoil => GardenCollection.PrepareSoil(wizard, Room, t, x, y, z, _indoors),
            // Plow All arrives as plows (0x140fb4df0 mode 11); a plain plow without a spell is the K1 path.
            GardenRules.CommandPlow => GardenCatalog.Current.Spell(t) is not null
                ? GardenCollection.PlowWithSpell(wizard, Room, t, x, y, z, _indoors)
                : GardenCollection.Plow(wizard, Room, x, y, z, _indoors),
            GardenRules.CommandPlant => GardenCollection.Plant(wizard, Room, t, message.ObjectGID, x, y, z, _indoors),
            GardenRules.CommandGrowing => GardenCollection.CastGrowing(wizard, Room, t, x, y, z, _indoors),
            GardenRules.CommandInsect => GardenCollection.Zap(wizard, Room, t, x, y, z, _indoors),
            GardenRules.CommandProtect => GardenCollection.Protect(wizard, Room, t, x, y, z, _indoors),
            GardenRules.CommandRevive => GardenCollection.Revive(wizard, Room, t, x, y, z, _indoors),
            GardenRules.CommandInspect => GardenCollection.Inspect(wizard, Room, t, x, y, z, _indoors),
            // Second Spring (stasis, rank 15), Hasty Harvest and the multi-cast are later gardening spells.
            _ => new GardenResult("That gardening spell is not part of this world yet."),
        };
        if (result.Saved && result.RemovedItemId != 0) SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID, ItemID = result.RemovedItemId,
        });
        if (result.Saved) SendEnergy(result, wizard);
        Finish(result, wizard);
    }

    // The cast animation for the other players in the zone (the client sends this only when it has company). No effect,
    // no reply: the command that follows does the work.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL))]
    private void ReceiveCast(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL message) {
        if (!Ready(out var wizard) || wizard.CharId != _attach.OwnerId) return;
        if (GardenCatalog.Current.Spell(message.SpellTemplateID) is null || wizard.SpellbookBehavior?.HasSpell(message.SpellTemplateID) != true) return;
        // Native ClientHousingManager::MSG_GardeningCastSpell (0x140f5a930) plays the cast for PlayerGID at the point.
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCASTSPELL {
            SpellTemplateID = message.SpellTemplateID, PlayerGID = wizard.GameObjectID,
            PositionX = message.PositionX, PositionY = message.PositionY, PositionZ = message.PositionZ,
        }, isSelfless: true);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANT))]
    private void ReceiveHarvest(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANT message) {
        if (!Ready(out var wizard)) { NotHere(); return; }
        var result = GardenCollection.Harvest(wizard, Room, message.PositionX, message.PositionY, message.PositionZ, _indoors);
        if (result.Saved) {
            foreach (var m in result.Rewards) SendToSocket(m);
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEGARDENINGXP {
                GlobalID = wizard.GameObjectID, XP = result.GardeningXp, Level = result.GardeningLevel, IsPet = 0,
            });
            if (result.RankUp) ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENLEVELUP {
                GlobalID = wizard.GameObjectID, NewLevel = result.GardeningLevel,
            }, isSelfless: false);
        }
        Finish(result, wizard);
    }

    // CLASSIC: Second Spring (a later spell, rank 15) is offered by the server's MSG_GARDENINGHARVESTPLANTSECONDSPRING and
    // confirmed by the client with the same message (0x140f621b0 -> 0x140f62970). This server never offers it, so a
    // confirmation is answered and does nothing.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANTSECONDSPRING))]
    private void ReceiveSecondSpring(WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGHARVESTPLANTSECONDSPRING message) {
        if (!GardenRules.Enabled) return;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
    }

    // CLASSIC: the gardening shed is later furniture (2012+); there is none to move seeds into or out of.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDTOGARDENINGSHED))]
    private void ReceiveToShed(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDTOGARDENINGSHED message) => NoShed();

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDFROMGARDENINGSHED))]
    private void ReceiveFromShed(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDFROMGARDENINGSHED message) => NoShed();

    private void NoShed() {
        if (!GardenRules.Enabled) return;
        InformGameClient("There is no gardening shed here.");
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
    }

    // A request outside a garden this session may change: answer it so the window does not stay busy.
    private void NotHere() {
        if (!GardenRules.Enabled || WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) return;
        InformGameClient("You can only garden in your own house or dorm.");
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_GARDENINGCOMMANDRESPONSE());
    }

    // CLASSIC: Energy is shared by pets and gardening (December 2010); the client's bar is the pet energy tick.
    private void SendEnergy(GardenResult result, Wizard wizard) {
        if (result.Energy is { } energy) SendToSocket(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
            GlobalID = wizard.GameObjectID, Energy = energy, MaxEnergy = wizard.GameStats?.m_energyMax ?? energy,
            TickTime = (int)result.EnergyTick,
        });
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
        if (result.Refresh) {
            // A pest arrived (no patch command adds one): every client in the room reloads the garden blob.
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS {
                Data = HousingCodec.Encode(GardenCodec.Manifest(ledger, _attach.ZoneId)),
            }, isSelfless: false);
            return;
        }
        foreach (var p in result.Patches) ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHGARDENING {
            Command = p.Command, PositionX = p.X, PositionY = p.Y, PositionZ = p.Z, PackageNumber = (uint)ledger.PackageNumber,
            VersionNumber = p.Version, GIDID = GardenRules.PackageUserData, TemplateID = p.TemplateOrFlags, Time = p.Time,
        }, isSelfless: false);
    }

    private void SendManifest(GardenLedger ledger) => SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS {
        Data = HousingCodec.Encode(GardenCodec.Manifest(ledger, _attach.ZoneId)),
    });

    /// <summary>
    /// A room that gardens: a castle's outdoor lot (soil piles and planters) and its rooms, and the dorm (planters only).
    /// December 2010 notes: "Students at Ravenwood can plant and grow amazing plants in their Dorms and Castles"; "To garden
    /// in your dorm room or an indoor space in your Castle, you must buy a planter".
    /// </summary>
    internal static bool GardenRoom(HousingAttachContext context, out bool indoors) {
        indoors = true;
        if (context.OwnerId == 0) return false;
        if (context.HousingDeedId == 0) return HousingRules.IsDorm(context.Zone);
        if (!HouseCollection.TryGetOwned(context.OwnerId, context.HousingDeedId, out var house)
            || !HouseCatalog.TryRoom(house.TemplateId, context.Zone, out _)) return false;
        indoors = !HouseCatalog.Same(house.ExteriorZone, context.Zone);
        return true;
    }

    private bool Ready(out Wizard wizard) {
        wizard = null;
        if (_attach is null || SessionActor.TransferringOut || !GardenRules.Enabled || SessionActor.HousingAttach != _attach) return false;
        wizard = GetActiveWizard();
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return false; }
        return wizard is not null && wizard.CharId == _attach.CharacterId && HouseCatalog.Same(wizard.Zone, _attach.Zone);
    }
}
