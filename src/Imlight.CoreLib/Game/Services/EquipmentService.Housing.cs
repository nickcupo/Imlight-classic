using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class EquipmentService {
    // CLASSIC: Islands are selected deeds, not visible gear. Save original item membership
    // together, then echo only to its owner; a public gear packet would alter their appearance.
    private void SelectHouse(Wizard wizard, ulong deedId, bool equip) {
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
        GAME_5_PROTOCOL.MSG_EQUIPITEM replacement = null, selection = null;
        var result = HouseCollection.SetEquipped(wizard, deedId, equip, preparePublication: prepared => {
            if (prepared.Replaced is { } replaced) replacement = new() {
                ItemID = replaced.m_globalID, SlotName = "Islands", IsEquip = 0,
            };
            selection = new() {
                ItemID = prepared.Item.m_globalID, SlotName = "Islands", IsEquip = (byte)(equip ? 1 : 0),
            };
            return true;
        }, afterCommit: _ => {
            if (replacement is not null) SendToSocket(replacement);
            SendToSocket(selection);
        });
        if (!result.Saved) {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
            InformGameClient(result.Error);
        }
    }
}
