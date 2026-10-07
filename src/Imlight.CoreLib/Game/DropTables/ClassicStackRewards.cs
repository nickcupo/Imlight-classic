// CLASSIC: rolled cards and reagents become rewards only after one acknowledged saved-inventory write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Game.DropTables;

internal sealed record StackTreasureReward(uint TemplateId, uint SpellHash);
internal sealed record StackReagentReward(ClientReagentItem Reagent, int Acquired, ByteString Data);
internal sealed record StackItemReward(WizClientObjectItem Item, ByteString Data);
internal sealed record StackRewardReceipt(IReadOnlyList<StackTreasureReward> Cards,
    IReadOnlyList<StackReagentReward> Reagents, IReadOnlyList<StackItemReward> Items) {
    internal StackRewardReceipt(IReadOnlyList<StackTreasureReward> cards, IReadOnlyList<StackReagentReward> reagents)
        : this(cards, reagents, []) { }
    internal bool BackpackCapacityExceeded { get; init; }
}

// CLASSIC: scoped fixtures replace assets/serialization, never the production tracked-write path.
internal sealed class StackRewardDependencies {
    internal Func<ulong, CoreTemplate> Template;
    internal Func<ulong, CoreObject> Create;
    internal Func<ClientReagentItem, ByteString> SerializeReagent;
    internal Func<WizClientObjectItem, ByteString> SerializeItem;
}

// CLASSIC: the outer quest transaction owns the save and original-row protection. Preparation and
// staging distinguish a valid zero-capacity award from a corrupt/failed award without nested writes.
internal sealed record PreparedStackRewards(ulong OwnerCharId, IReadOnlyList<WizClientObjectItem> Items,
    IReadOnlyList<StackTreasureReward> Cards, IReadOnlyList<ReagentAcquisition> Reagents,
    StackRewardDependencies Dependencies);
internal sealed record StagedStackRewards(StackRewardReceipt Receipt, IReadOnlyList<WizClientObjectItem> Backpack) {
    internal bool HasRewards => Receipt.Items.Count > 0 || Receipt.Cards.Count > 0 || Receipt.Reagents.Count > 0;
}

internal static class ClassicStackRewards {
    internal static readonly AsyncLocal<StackRewardDependencies> TestScope = new();

    internal static bool TryGrant(Wizard live, IReadOnlyList<uint> treasureCards,
        IReadOnlyList<DropItemResult> reagents, out StackRewardReceipt receipt)
        => TryGrant(live, [], treasureCards, reagents, out receipt);

    internal static bool TryGrant(Wizard live, IReadOnlyList<DropItemResult> items, IReadOnlyList<uint> treasureCards,
        IReadOnlyList<DropItemResult> reagents, out StackRewardReceipt receipt) {
        receipt = new([], []);
        if (live is null || live.CharId == 0 || WizardCollection.IsInventorySnapshotUncertain(live)
            || items is null || treasureCards is null || reagents is null
            || (items.Count == 0 && treasureCards.Count == 0 && reagents.Count == 0)) return false;

        if (!TryPrepare(live, items, treasureCards, reagents, out var prepared)
            || (prepared.Items.Count == 0 && prepared.Cards.Count == 0 && prepared.Reagents.Count == 0)) return false;

        StagedStackRewards staged = null;
        StackRewardReceipt published = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live)
                || !TryStage(session, saved, prepared, out staged)) return false;
            WizardInventoryTransactions.ProtectUnmodifiedRows(session);
            return staged.HasRewards;
        }, saved => published = Publish(live, saved, staged),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (!committed) {
            if (staged is not null) receipt = new([], []) { BackpackCapacityExceeded = staged.Receipt.BackpackCapacityExceeded };
            return false;
        }
        receipt = published;
        return true;
    }

    internal static bool TryPrepare(Wizard live, IReadOnlyList<DropItemResult> items,
        IReadOnlyList<uint> treasureCards, IReadOnlyList<DropItemResult> reagents, out PreparedStackRewards prepared) {
        prepared = null;
        if (live is null || live.CharId == 0 || WizardCollection.IsInventorySnapshotUncertain(live)
            || items is null || treasureCards is null || reagents is null) return false;

        var d = TestScope.Value ?? new();
        var cards = new List<StackTreasureReward>();
        var acquisitions = new List<ReagentAcquisition>();
        var itemCandidates = new List<WizClientObjectItem>();
        try {
            foreach (var drop in items) {
                // CLASSIC: ordinary rolled gear has always granted one copy per entry; do not change its roll.
                if (drop is null || !ulong.TryParse(drop.ItemId, out var id) || Resolve(d, id) is not WizItemTemplate template
                    || template is ReagentItemTemplate) continue; // CLASSIC: reagents have their own bag/delivery path
                if (Create(d, id, live.CharId) is not WizClientObjectItem item) return false;
                var candidate = WizardInventoryTransactions.Prepare(live, item, initializeBehaviors: false);
                if (candidate is null || candidate.m_templateID.Full != id) return false;
                // A prepared pet owns its egg/name/talent state. Other items retain the previous template init.
                if (candidate.m_inactiveBehaviors?.OfType<ClientPetItemBehavior>().Any() != true)
                    CoreObjectFactory.InitializeCoreObjectBehaviors(candidate, template);
                itemCandidates.Add(candidate);
            }
            foreach (var id in treasureCards) {
                if (Resolve(d, id) is SpellTemplate spell)
                    cards.Add(new(id, StringHash.Compute(spell.m_name)));
            }
            foreach (var drop in reagents) {
                if (drop is null || drop.Quantity <= 0 || !ulong.TryParse(drop.ItemId, out var id)
                    || Resolve(d, id) is not ReagentItemTemplate) continue;
                // Never mutate an aliased live stack during preparation or use its snapshot count as a delta.
                var candidate = live.AlchemyBehavior?.GetReagent(id)
                    ?? Create(d, id) as ClientReagentItem;
                if (candidate is null || candidate.m_globalID.Full == 0 || candidate.m_templateID.Full != id
                    || (candidate.m_characterId.Full != 0 && candidate.m_characterId.Full != live.CharId)) return false;
                acquisitions.Add(new(candidate with { m_characterId = live.CharId }, drop.Quantity));
            }
        }
        catch { return false; } // no write or client success when an asset cannot be prepared
        prepared = new(live.CharId, itemCandidates.ToArray(), cards.ToArray(), acquisitions.ToArray(), d);
        return true;
    }

    internal static bool TryStage(IDocumentSession session, Wizard saved, PreparedStackRewards prepared,
        out StagedStackRewards staged) {
        staged = null;
        if (session is null || saved is null || prepared is null || saved.CharId == 0
            || prepared.OwnerCharId != saved.CharId) return false;
        List<StackTreasureReward> stagedCards = [];
        List<StackReagentReward> stagedReagents = [];
        List<StackItemReward> stagedItems = [];
        List<WizClientObjectItem> backpack = [];
        var backpackCapacityExceeded = false;
        IReadOnlyList<WizClientObjectItem> admittedItems = [];
        if (prepared.Items.Count > 0) {
            WizardInventoryTransactions.TryStageGrants(session, saved, prepared.Items, out admittedItems, out var validatedItems, out backpack);
            if (!validatedItems) return false;
            backpackCapacityExceeded = admittedItems.Count < prepared.Items.Count;
        }

        // Validate all reagent identities before admitting even a card-only part of this compound reward.
        IReadOnlyList<ReagentAcquisitionReceipt> reagentReceipts = [];
        if (prepared.Reagents.Count > 0) {
            WizardReagentCollection.TryStageAcquisitions(session, saved, prepared.Reagents, out reagentReceipts, out var validated);
            if (!validated) return false;
        }

        foreach (var card in prepared.Cards) {
            if (!WizardCollection.CanReceiveTreasureCards(saved, 1)) break;
            saved.SpellbookBehavior.AddTreasureCard(card.TemplateId);
            stagedCards.Add(card);
        }
        try {
            foreach (var admitted in admittedItems) {
                var data = SerializeItem(prepared.Dependencies, admitted);
                if (data.Length == 0) return false;
                stagedItems.Add(new(admitted, data));
            }
            foreach (var acquired in reagentReceipts) {
                var data = Serialize(prepared.Dependencies, acquired.Reagent);
                if (data.Length == 0) return false;
                stagedReagents.Add(new(acquired.Reagent, acquired.Acquired, data));
            }
        }
        catch { return false; } // disposing the unsaved outer session discards the entire staged group
        staged = new(new(stagedCards.ToArray(), stagedReagents.ToArray(), stagedItems.ToArray()) {
            BackpackCapacityExceeded = backpackCapacityExceeded,
        }, backpack.ToArray());
        return true;
    }

    // CLASSIC: invoked only by the outer acknowledged write, while it still owns the character lane.
    internal static StackRewardReceipt Publish(Wizard live, Wizard saved, StagedStackRewards staged) {
        if (staged.Receipt.Items.Count > 0) WizardInventoryTransactions.PublishCommittedBackpack(live, saved, staged.Backpack);
        if (staged.Receipt.Cards.Count > 0) WizardCollection.PublishTreasureCards(live, saved);
        var reagents = staged.Receipt.Reagents.Select(acquired => new StackReagentReward(
            WizardReagentCollection.PublishCommittedBag(live, saved, acquired.Reagent), acquired.Acquired, acquired.Data)).ToArray();
        return staged.Receipt with { Reagents = reagents };
    }

    private static CoreTemplate Resolve(StackRewardDependencies d, ulong id)
        => d.Template is null ? CoreObjectFactory.GetCoreTemplate(id) : d.Template(id);
    private static CoreObject Create(StackRewardDependencies d, ulong id, ulong owner = 0)
        => d.Create is not null ? d.Create(id)
            // CLASSIC: quest/combat pet rewards need the same template egg/name state as shop pets.
            : owner != 0 && id <= uint.MaxValue && PetFactory.IsPetTemplate((uint)id)
                ? PetFactory.CreatePet(owner, (uint)id)
                : CoreObjectFactory.FinalizeCoreObject(id);
    private static ByteString Serialize(StackRewardDependencies d, ClientReagentItem reagent)
        => d.SerializeReagent is not null ? d.SerializeReagent(reagent)
            : new CoreObjectSerializer(behaviors: SerializerFlags.None).Serialize(reagent, 27, out var data) ? data : default;
    private static ByteString SerializeItem(StackRewardDependencies d, WizClientObjectItem item)
        => d.SerializeItem is not null ? d.SerializeItem(item)
            : new CoreObjectSerializer(behaviors: SerializerFlags.None).Serialize(item,
                (uint)(PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit), out var data) ? data : default;
}
