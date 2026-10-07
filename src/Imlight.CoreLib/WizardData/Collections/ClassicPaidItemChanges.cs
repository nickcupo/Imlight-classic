// CLASSIC: dye and pet-name changes save the original tracked row and its gold payment together.
using System;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.WizardData.Collections;

internal sealed record EquippedDyeUpdate(string SlotName, ByteString LocalData, ByteString PublicData);
internal sealed record PaidItemChangeReceipt(WizClientObjectItem Item, int Cost, int Gold, int MaxGold,
    EquippedDyeUpdate EquippedDye = null);

internal static class ClassicPaidItemChanges {
    // CLASSIC: the existing public equipment colors are five-bit values.
    private const int MaxDye = 31;
    internal const PropertyFlags EquippedItemMask = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;

    internal static bool Dye(Wizard live, ulong id, int primary, int secondary, int pattern,
        out PaidItemChangeReceipt receipt, Func<ulong, WizItemTemplate> templates = null,
        Func<WizItemTemplate, int, int, int> price = null, Func<uint, bool> isPet = null,
        Func<PropertyClass, PropertyFlags, ByteString> serialize = null) {
        receipt = null;
        if (live is null || id == 0 || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        WizClientObjectItem stored = null;
        EquippedDyeUpdate update = null;
        PaidItemChangeReceipt acknowledged = null;
        var cost = 0;
        var success = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.GameStats is null
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, id, out stored)
                || !WizardInventoryTransactions.CanPublishOwnedItem(live, saved, stored)) return false;
            var template = templates is null ? CoreObjectFactory.GetCoreTemplate(stored.m_templateID) as WizItemTemplate
                : templates(stored.m_templateID.Full);
            if (template is null || !IsValidDye(stored, template, primary, secondary, pattern, isPet)) return false;
            cost = price is null ? PriceModifiersConfig.GetDyeCost(template, primary, secondary) : price(template, primary, secondary);
            if (cost < 0 || saved.GameStats.m_currentGold < cost) return false;
            // CLASSIC: this is the independent tracked original, never a shallow copy of a live behavior list.
            stored.m_primaryColor = primary; stored.m_secondaryColor = secondary; stored.m_pattern = pattern;
            if (saved.EquipmentBehavior?.EquippedItemIds?.Contains(id) == true) {
                try {
                    var slot = ItemHelper.GetItemSlot(template);
                    if (slot is null) return false;
                    var serializer = serialize is null ? new CoreObjectSerializer(behaviors: SerializerFlags.None) : null;
                    ByteString Prepare(PropertyClass item, PropertyFlags mask)
                        => serialize is null ? (serializer.Serialize(item, mask, out var data) ? data : default) : serialize(item, mask);
                    var local = Prepare(stored, EquippedItemMask);
                    var publicData = Prepare(ItemHelper.GetPublicItem(stored), (PropertyFlags)1);
                    if (local.Length == 0 || publicData.Length == 0) return false;
                    update = new(slot.SlotType.ToString(), local, publicData);
                }
                catch (Exception) { return false; } // CLASSIC: no save was attempted, so preparation can safely refuse.
            }
            saved.GameStats.m_currentGold -= cost;
            return true;
        }, saved => {
            var item = WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, stored);
            live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
            acknowledged = new(item, cost, saved.GameStats.m_currentGold, saved.GameStats.m_baseGoldPouch, update);
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (success) receipt = acknowledged;
        return success;
    }

    internal static bool Rename(Wizard live, ulong id, uint nameKeys, out PaidItemChangeReceipt receipt,
        Func<uint, bool> validName = null, Func<int> price = null) {
        receipt = null;
        if (live is null || id == 0 || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        WizClientObjectItem stored = null;
        PaidItemChangeReceipt acknowledged = null;
        var cost = 0;
        var success = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.GameStats is null
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, id, out stored)
                || !WizardInventoryTransactions.CanPublishOwnedItem(live, saved, stored)
                || !CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(stored, out _)
                || !(validName is null ? WizardNameBank.IsValidPetName(nameKeys) : validName(nameKeys))) return false;
            cost = price is null ? PriceModifiersConfig.GetPetRenameCost() : price();
            if (cost < 0 || saved.GameStats.m_currentGold < cost || !PetFactory.TrySetPetName(stored, nameKeys)) return false;
            saved.GameStats.m_currentGold -= cost;
            return true;
        }, saved => {
            var item = WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, stored);
            live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
            acknowledged = new(item, cost, saved.GameStats.m_currentGold, saved.GameStats.m_baseGoldPouch);
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (success) receipt = acknowledged;
        return success;
    }

    // CLASSIC: exactly the existing shop policy: every layer in range; pets keep a current color or use
    // their template choices. The third layer does not participate in the existing price formula.
    internal static bool IsValidDye(WizClientObjectItem item, WizItemTemplate template,
        int primary, int secondary, int pattern, Func<uint, bool> isPet = null) {
        if (primary is < 0 or > MaxDye || secondary is < 0 or > MaxDye || pattern is < 0 or > MaxDye) return false;
        if (!(isPet is null ? PetFactory.IsPetTemplate(template.m_templateID) : isPet(template.m_templateID))) return true;
        static bool ValidLayer(int dye, int current, int count) => dye == current || count > 1 && dye < count;
        return ValidLayer(primary, item.m_primaryColor, template.m_numPrimaryColors)
            && ValidLayer(secondary, item.m_secondaryColor, template.m_numSecondaryColors)
            && ValidLayer(pattern, item.m_pattern, template.m_numPatterns);
    }
}
