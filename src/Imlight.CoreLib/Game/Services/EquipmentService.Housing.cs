using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class EquipmentService {
    // CLASSIC: Islands are selected deeds, not visible gear. Save original item membership
    // together, then echo only to its owner; a public gear packet would alter their appearance.
    private void SelectHouse(Wizard wizard, ulong deedId, bool equip) {
        var result = HouseCollection.SetEquipped(wizard, deedId, equip);
        if (!result.Saved) { InformGameClient(result.Error); return; }
        if (result.Replaced is { } replaced) SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = replaced.m_globalID, SlotName = "Islands", IsEquip = 0,
        });
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = result.Item.m_globalID, SlotName = "Islands", IsEquip = (byte)(equip ? 1 : 0),
        });
    }
}
