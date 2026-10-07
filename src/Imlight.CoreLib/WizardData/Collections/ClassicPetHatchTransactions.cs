// CLASSIC: hatch payment, original egg, saved backpack and cooldown/slot publish after one acknowledgement.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.WizardData.Collections;

internal sealed record PetHatchReceipt(WizClientObjectItem Egg, ByteString Data, uint Finish, uint Seconds);

internal static class ClassicPetHatchTransactions {
    internal static bool TryCreate(Wizard live, ulong parentId, HatchParent mine, HatchParent theirs,
        WizClientObjectItem egg, long now, out PetHatchReceipt receipt,
        Func<WizClientObjectItem, ByteString> serialize = null) {
        receipt = null;
        if (live is null || parentId == 0 || mine is null || theirs is null || now < 0 || now > int.MaxValue
            || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        var prepared = WizardInventoryTransactions.Prepare(live, egg, false);
        var behavior = PetProgress.Behavior(prepared);
        if (behavior is null || behavior.m_level != 0 || prepared.m_templateID.Full == 0) return false;
        var finish = Math.Max(now, (long)behavior.m_hatchedTimeSecs);
        if (finish > int.MaxValue) return false;
        var cost = PetHatchRules.GoldCost(mine.Pedigree + theirs.Pedigree);
        List<WizClientObjectItem> backpack = [];
        PetHatchReceipt acknowledged = null;
        var success = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.GameStats is null
                || saved.GameStats.m_currentGold < cost || saved.PetOwnerBehavior is null
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, parentId, out var parent)) return false;
            var parentBehavior = PetProgress.Behavior(parent);
            if (parentBehavior is null || parent.m_templateID.Full != mine.TemplateId
                || parentBehavior.m_overallRating != mine.Pedigree) return false;
            var last = saved.PetOwnerBehavior.PetHatchTimes?.GetValueOrDefault(parentId) ?? 0;
            if (PetHatchRules.Check(mine with { Level = parentBehavior.m_level, LastHatchUnix = last }, theirs, now) is not null
                || saved.PetOwnerBehavior.Eggs?.Any(slot => slot.GlobalId == prepared.m_globalID.Full) == true) return false;
            if (!WizardInventoryTransactions.TryStageGrants(session, saved, [prepared], out var admitted,
                out _, out backpack) || admitted.Count != 1) return false;
            ByteString data;
            try { data = serialize is null ? Serialize(prepared) : serialize(prepared); }
            catch (Exception) { return false; }
            if (data.Length == 0) return false;
            saved.GameStats.m_currentGold -= cost;
            saved.PetOwnerBehavior.PetHatchTimes ??= [];
            saved.PetOwnerBehavior.PetHatchTimes[parentId] = now;
            saved.PetOwnerBehavior.Eggs ??= [];
            saved.PetOwnerBehavior.Eggs.Add(new PetEggData { GlobalId = prepared.m_globalID.Full,
                PetTemplateId = prepared.m_templateID.Full, HatchTimeEpoch = (int)finish });
            acknowledged = new(prepared, data, (uint)finish, (uint)(finish - now));
            WizardInventoryTransactions.ProtectUnmodifiedRows(session);
            return true;
        }, saved => {
            WizardInventoryTransactions.PublishCommittedBackpack(live, saved, backpack);
            live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
            live.PetOwnerBehavior.PetHatchTimes = new(saved.PetOwnerBehavior.PetHatchTimes);
            live.PetOwnerBehavior.Eggs = saved.PetOwnerBehavior.Eggs.Select(slot => slot with { }).ToList();
            live.PetOwnerBehavior.RebuildRuntimeSlots();
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (success) receipt = acknowledged;
        return success;
    }

    private static ByteString Serialize(WizClientObjectItem item) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        return serializer.Serialize(item, (PropertyFlags)24, out var data) ? data : default;
    }
}
