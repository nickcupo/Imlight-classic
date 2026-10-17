using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.WizardData.Models.World;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic.Gardening;

/// <summary>What a committed garden change publishes, captured before SaveChanges.</summary>
internal sealed record GardenResult(string Error) {
    internal GardenLedger Ledger { get; init; }
    internal List<GardenPatch> Patches { get; init; } = [];
    internal ulong RemovedItemId { get; init; }
    internal int? Energy { get; init; }
    internal uint EnergyTick { get; init; }
    internal int GardeningXp { get; init; }
    internal int GardeningLevel { get; init; }
    internal GardenHarvest Harvest { get; init; }
    internal bool RankUp { get; init; }
    internal List<IMessage> Rewards { get; init; } = [];
    /// <summary>A change no patch can carry (a pest arrived): the clients reload the garden blob instead of patching.</summary>
    internal bool Refresh { get; init; }
    internal bool Saved => Error is null && Ledger is not null;
}

// CLASSIC: every garden change saves the garden ledger together with the character changes it causes (a consumed
// seed, spent Energy, gardening XP) in one character-lane Raven transaction; nothing is published before the save.
internal static class GardenCollection {
    internal static readonly Func<uint> SystemClock = () => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal static readonly System.Threading.AsyncLocal<Func<uint>> TestClock = new();
    internal static uint Now => (TestClock.Value ?? SystemClock)();
    internal static Random Random { get; set; } = Random.Shared;

    internal static GardenLedger Load(ulong owner, ulong deed, string zone, bool create = false) {
        if (owner == 0 || string.IsNullOrEmpty(zone)) return null;
        lock (HousingCollection.PackageLock) {
            using var session = HousingCollection.Open();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var id = GardenLedger.DocumentId(owner, deed, zone);
            var ledger = session.Load<GardenLedger>(id);
            if (ledger is not null || !create) return ledger;
            var allocator = session.Load<HousingPackageAllocator>(HousingPackageAllocator.DocumentId);
            var fresh = allocator is null;
            allocator ??= new HousingPackageAllocator();
            if (allocator.NextPackageNumber <= 0 || allocator.NextPackageNumber == int.MaxValue)
                throw new InvalidOperationException("Housing package ids are exhausted.");
            ledger = new GardenLedger { OwnerId = owner, DeedId = deed, Zone = zone, PackageNumber = allocator.NextPackageNumber++,
                LastAdvanced = Now };
            if (fresh) session.Store(allocator, HousingPackageAllocator.DocumentId);
            session.Store(ledger, id);
            session.SaveChanges();
            return ledger;
        }
    }

    /// <summary>Catches the saved garden up to now (needs, wilting, stages, harvest glow) and saves it.</summary>
    internal static GardenResult Advance(Wizard live, HousingRoomIdentity room, bool indoors = false)
        => Mutate(live, room, (engine, ledger, saved, session, now) => { engine.Advance(ledger, now); return null; },
            requireOwner: false, indoors: indoors);

    internal static GardenResult PrepareSoil(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z,
        bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => engine.PrepareSoil(ledger, spell, x, y, z, now),
            GardenSpellKind.SoilPreparation, indoors: indoors);

    internal static GardenResult CastGrowing(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z,
        bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now)
            => engine.CastGrowing(ledger, spell, x, y, z, now) > 0 ? null : "There is no plant there.", GardenSpellKind.Growing,
            indoors: indoors);

    internal static GardenResult Plow(Wizard live, HousingRoomIdentity room, float x, float y, float z, bool indoors = false)
        => Mutate(live, room, (engine, ledger, saved, session, now) => engine.Plow(ledger, x, y, z, now), indoors: indoors);

    /// <summary>CLASSIC: Plow with the Plow spell (utility "Zap", 1 Energy in the client template).</summary>
    internal static GardenResult PlowWithSpell(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z,
        bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => engine.Plow(ledger, x, y, z, now),
            GardenSpellKind.Utility, (int)GardenUtility.Zap, indoors);

    internal static GardenResult Zap(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z, bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => engine.Zap(ledger, spell, x, y, z, now) switch {
            < 0 => "That spell can't be cast here.", 0 => "There is no plant there.", _ => null,
        }, GardenSpellKind.InsectFighting, indoors: indoors);

    internal static GardenResult Protect(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z, bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => engine.Protect(ledger, spell, x, y, z, now) switch {
            < 0 => "That spell can't be cast here.", 0 => "There is no plant there.", _ => null,
        }, GardenSpellKind.PlantProtection, indoors: indoors);

    internal static GardenResult Revive(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z, bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => engine.Revive(ledger, x, y, z, now),
            GardenSpellKind.Utility, (int)GardenUtility.Revive, indoors);

    /// <summary>Inspect costs nothing and changes nothing (the client shows the plant's details itself).</summary>
    internal static GardenResult Inspect(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z, bool indoors = false)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => { engine.Advance(ledger, now); return null; },
            GardenSpellKind.Utility, (int)GardenUtility.Inspect, indoors);

    internal static GardenResult Plant(Wizard live, HousingRoomIdentity room, uint seedTemplate, ulong seedItemId, float x, float y, float z,
        bool indoors = false) {
        // CLASSIC: Plant All sends the "Plant Seed" spell as the template and the seed item as the object (0x140fb4df0 mode 12).
        var bySpell = GardenCatalog.Current.Spell(seedTemplate) is not null;
        List<WizClientObjectItem> backpack = [];
        ulong removed = 0;
        int? energy = null; uint tick = 0;
        // CLASSIC: "You will need Energy to plant a seed" (December 2010 notes); the amount is the client's "Plant Seed"
        // spell (rules.gardening plant_seed_energy, unverified).
        var cost = Math.Max(0, ClassicGardening.Active.PlantSeedEnergy);
        var result = Mutate(live, room, (engine, ledger, saved, session, now) => {
            if (!HouseCollection.TryReadHoldings(session, saved, out _, out backpack)) return "Your backpack could not be read.";
            var doc = bySpell ? backpack.FirstOrDefault(i => i.m_globalID.Full == seedItemId && seedItemId != 0)
                : backpack.FirstOrDefault(i => i.m_globalID.Full == seedItemId && (uint)i.m_templateID.Full == seedTemplate)
                ?? (seedItemId == 0 ? backpack.FirstOrDefault(i => (uint)i.m_templateID.Full == seedTemplate) : null);
            if (doc is null || doc.m_characterId != live.CharId) return "You don't have that seed.";
            var seed = (uint)doc.m_templateID.Full;
            var owner = saved.PetOwnerBehavior;
            if (cost > 0 && (owner is null || owner.Energy < cost)) return "You don't have enough Energy.";
            var error = engine.Plant(ledger, seed, x, y, z, now, GardenProgress.Rank(saved),
                doc.m_globalID.Full, session.Advanced.GetDocumentId(doc));
            if (error is not null) return error;
            // The seed leaves the backpack; its original item document is retained (as placed furniture is).
            removed = doc.m_globalID.Full;
            saved.InventoryBehavior.InventoryItemIds = saved.InventoryBehavior.InventoryItemIds.Where(id => id != removed).ToList();
            backpack.Remove(doc);
            if (cost > 0) { owner.SetEnergy(owner.Energy - cost); energy = owner.Energy; tick = owner.LastEnergyTickEpoch; }
            return null;
        }, afterCommit: saved => {
            WizardInventoryTransactions.PublishCommittedBackpack(live, saved, backpack);
            if (cost > 0) live.PetOwnerBehavior?.PublishCommittedEnergy(saved.PetOwnerBehavior);
        }, indoors: indoors);
        return result.Saved ? result with { RemovedItemId = removed, Energy = energy, EnergyTick = tick } : result;
    }

    internal static GardenResult Harvest(Wizard live, HousingRoomIdentity room, float x, float y, float z, bool indoors = false) {
        GardenHarvest harvest = default;
        int xp = 0, level = 0; var rankUp = false;
        StagedStackRewards staged = null; List<ClientPetSnackItem> snackBag = null;
        var snacks = new List<(ClientPetSnackItem Snack, ByteString Data)>();
        var loot = new DropTableResult { DropTableId = "garden-harvest" };
        var gold = 0;
        var tables = ClassicGardening.Active;
        var result = Mutate(live, room, (engine, ledger, saved, session, now) => {
            harvest = engine.Harvest(ledger, x, y, z, now);
            if (!harvest.Done) return harvest.Error;
            if (saved.GameStats is null) return "Your gardening experience could not be saved.";
            // CLASSIC: gardening XP is the plant template's m_harvestXP (client value, D7); the rank follows from the XP
            // (rules.gardening ranks; December 2010: ten ranks, Novice to Grandmaster).
            var before = GardenProgress.RankFor(saved.GameStats.m_gardeningXP, saved.GameStats.m_gardeningLevel);
            saved.GameStats.m_gardeningXP = checked(saved.GameStats.m_gardeningXP + Math.Max(0, harvest.Xp));
            var after = GardenProgress.RankFor(saved.GameStats.m_gardeningXP, saved.GameStats.m_gardeningLevel);
            saved.GameStats.m_gardeningLevel = (byte)Math.Clamp(after, 0, byte.MaxValue);
            rankUp = after > before;
            xp = saved.GameStats.m_gardeningXP; level = saved.GameStats.m_gardeningLevel;
            // CLASSIC: harvest rewards from the profile's dated tables (rules.gardening harvests); a plant without a
            // table gives its XP only. December 2010: "reagents, pet snacks, gold, treasure cards, or more seeds".
            if (tables.HarvestFor(harvest.PlantTemplateId) is not { } table) return null;
            var items = new List<DropItemResult>(); var cards = new List<uint>(); var reagents = new List<DropItemResult>();
            var lines = (table.Elder ? table.Table.ElderRewards : table.Table.MatureRewards).ToList();
            if (table.Table.PickOne) {
                var others = lines.Where(l => l.Kind != GardenRewardKind.Gold).ToList();
                lines = lines.Where(l => l.Kind == GardenRewardKind.Gold).ToList();
                if (others.Count > 0) lines.Add(others[Random.Next(others.Count)] with { Chance = 1 });
            }
            foreach (var line in lines) {
                if (line.Chance <= 0 || Random.NextDouble() >= line.Chance) continue;
                var count = line.Max > line.Min ? Random.Next(line.Min, line.Max + 1) : line.Min;
                if (count <= 0) continue;
                switch (line.Kind) {
                    case GardenRewardKind.Gold: gold += count; break;
                    case GardenRewardKind.Reagent:
                        reagents.Add(new DropItemResult { ItemId = line.Template.ToString(), ItemName = line.Name, Quantity = count }); break;
                    case GardenRewardKind.Card: for (var i = 0; i < count; i++) cards.Add(line.Template); break;
                    case GardenRewardKind.Snack:
                        for (var i = 0; i < count; i++) {
                            if (!WizardPetSnackTransactions.TryStageGrant(session, saved, live, line.Template, out var snack, out var data, out snackBag))
                                return "Your pet snack bag could not take the harvest.";
                            snacks.Add((snack, data));
                        }
                        break;
                    default: // items and seeds go to the backpack
                        for (var i = 0; i < count; i++) items.Add(new DropItemResult { ItemId = line.Template.ToString(), ItemName = line.Name, Quantity = 1 });
                        break;
                }
            }
            if (items.Count + cards.Count + reagents.Count > 0) {
                if (!ClassicStackRewards.TryPrepare(live, items, cards, reagents, out var prepared)
                    || !ClassicStackRewards.TryStage(session, saved, prepared, out staged)) return "Your harvest could not be gathered. Please try again.";
            }
            if (gold > 0) {
                var balance = (long)saved.GameStats.m_currentGold + WizardCollection.CappedGoldDelta(saved.GameStats, gold);
                if (balance < 0 || balance > int.MaxValue) return "Your harvest could not be gathered. Please try again.";
                gold = (int)balance - saved.GameStats.m_currentGold;
                saved.GameStats.m_currentGold = (int)balance;
            }
            return null;
        }, afterCommit: saved => {
            if (live.GameStats is not null) {
                live.GameStats.m_gardeningXP = saved.GameStats.m_gardeningXP;
                live.GameStats.m_gardeningLevel = saved.GameStats.m_gardeningLevel;
                if (gold != 0) live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
            }
            if (staged is not null) {
                var receipt = ClassicStackRewards.Publish(live, saved, staged);
                foreach (var item in receipt.Items) loot.Items.Add(new DropItemResult { ItemId = item.Item.m_templateID.Full.ToString(), Quantity = 1 });
                foreach (var card in receipt.Cards) { loot.TreasureCards.Add(card.TemplateId); }
                foreach (var reagent in receipt.Reagents) loot.Reagents.Add(new DropItemResult {
                    ItemId = reagent.Reagent.m_templateID.Full.ToString(), Quantity = reagent.Acquired });
                staged = staged with { Receipt = receipt };
            }
            if (snackBag is not null) WizardPetSnackTransactions.PublishCommittedBag(live, saved, snackBag);
            loot.GoldAmount = gold;
        }, indoors: indoors);
        if (!result.Saved) return result;
        return result with { Harvest = harvest, GardeningXp = xp, GardeningLevel = level, RankUp = rankUp,
            Rewards = RewardMessages(live, staged, snacks, gold, loot) };
    }

    // The client messages of a harvest's rewards, in the order the quest rewards send them.
    private static List<IMessage> RewardMessages(Wizard live, StagedStackRewards staged,
        List<(ClientPetSnackItem Snack, ByteString Data)> snacks, int gold, DropTableResult loot) {
        var messages = new List<IMessage>();
        if (staged is not null) {
            foreach (var item in staged.Receipt.Items) messages.Add(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = live.GameObjectID, SerializedItem = item.Data });
            foreach (var card in staged.Receipt.Cards) messages.Add(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                SpellID = (int)card.SpellHash, EnchantmentID = 0 });
            foreach (var reagent in staged.Receipt.Reagents) {
                messages.Add(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = live.GameObjectID, Data = reagent.Data });
                // CLASSIC: no MSG_ITEMACQUISITION: r806919 feeds only the Crown Shop delivery invoice with it (0x140492f30), which 2010 lacked.
            }
            if (staged.Receipt.BackpackCapacityExceeded)
                messages.Add(ClassicChat.Line("Your backpack is full, so part of the harvest could not be added."));
        }
        foreach (var (snack, data) in snacks) {
            if (data.Length > 0) messages.Add(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = live.GameObjectID, Data = data });
            messages.Add(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = live.GameObjectID, ItemID = snack.m_globalID, Quantity = snack.m_quantity });
        }
        if (gold != 0 && live.GameStats is not null)
            messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = live.GameStats.m_currentGold, MaxGold = live.GameStats.m_baseGoldPouch });
        if (loot.Items.Count + loot.TreasureCards.Count + loot.Reagents.Count > 0 || loot.GoldAmount > 0) {
            try {
                if (new ObjectSerializer(Versionable: false).Serialize(DropTableConverter.ToLootInfoList(loot), 4, out var data))
                    messages.Add(new WIZARD_12_PROTOCOL.MSG_LOOT { GlobalID = live.GameObjectID, LootList = data });
            }
            catch (Exception) { /* the popup is cosmetic; the rewards are saved and sent above */ }
        }
        return messages;
    }

    private delegate string SpellApply(GardenEngine engine, GardenLedger ledger, GardenSpellInfo spell, uint now);

    private static GardenResult Spell(Wizard live, HousingRoomIdentity room, uint spellTemplate, SpellApply apply, GardenSpellKind kind,
        int? utility = null, bool indoors = false) {
        int? energy = null; uint tick = 0;
        var info = GardenCatalog.Current.Spell(spellTemplate);
        if (info is null || info.Kind != kind || utility is { } u && info.Utility != u) return new("That spell can't be cast here.");
        var result = Mutate(live, room, (engine, ledger, saved, session, now) => {
            if (saved.SpellbookBehavior?.HasSpell(spellTemplate) != true) return "You don't know that gardening spell.";
            var owner = saved.PetOwnerBehavior;
            if (owner is null || owner.Energy < info.EnergyCost) return "You don't have enough Energy.";
            var error = apply(engine, ledger, info, now);
            if (error is not null) return error;
            // CLASSIC: Energy is shared by pets and gardening (December 2010 notes).
            if (info.EnergyCost > 0) owner.SetEnergy(owner.Energy - info.EnergyCost);
            energy = owner.Energy; tick = owner.LastEnergyTickEpoch;
            return null;
        }, afterCommit: saved => live.PetOwnerBehavior?.PublishCommittedEnergy(saved.PetOwnerBehavior), indoors: indoors);
        return result.Saved ? result with { Energy = energy, EnergyTick = tick } : result;
    }

    private delegate string Change(GardenEngine engine, GardenLedger ledger, Wizard saved, IDocumentSession session, uint now);

    // CLASSIC: an engine for this room: its planters and furniture (the room's housing ledger, read in the same session),
    // indoors or out, and the profile's likes and pest rules (rules.gardening; off without the tables).
    private static GardenEngine Engine(IDocumentSession session, HousingRoomIdentity room, bool indoors) {
        var engine = new GardenEngine(GardenCatalog.Current, Random) { Indoors = indoors };
        var tables = ClassicGardening.Active;
        engine.LikesRadius = tables.Likes.Radius; engine.MinModifier = tables.Likes.MinModifier; engine.MaxModifier = tables.Likes.MaxModifier;
        engine.PestsEnabled = tables.Pests.Enabled; engine.PestChanceScale = tables.Pests.ChanceScale;
        HousingLedger furniture = null;
        try { furniture = session.Load<HousingLedger>(HousingLedger.DocumentId(room)); }
        catch (Exception) { furniture = null; } // a room without furniture yet
        if (furniture is not null && furniture.OwnerId == room.OwnerId) {
            var placed = furniture.Entries.Where(e => !e.Removed && e.TemplateId != 0).ToList();
            engine.Neighbours = placed.Select(e => new GardenNeighbour(e.TemplateId, e.X, e.Y, e.Z)).ToList();
            engine.Pots = placed.Select(e => GardenCatalog.Current.Soil(e.TemplateId) is { Pot: true } soil
                    ? new GardenPot(e.ItemId, e.TemplateId, e.X, e.Y, e.Z, soil) : null)
                .Where(p => p is not null).ToList();
        }
        return engine;
    }

    private static GardenResult Mutate(Wizard live, HousingRoomIdentity room, Change change, Action<Wizard> afterCommit = null,
        bool requireOwner = true, bool indoors = false) {
        if (live is null || room is null || live.IsInDuel || WizardCollection.IsInventorySnapshotUncertain(live)) return new("You can't garden right now.");
        if (requireOwner && live.CharId != room.OwnerId) return new("Only the owner can garden here.");
        var now = Now;
        GardenResult result = new("Your garden could not be changed.");
        var charId = requireOwner ? live.CharId : room.OwnerId;
        try {
            var saved = WizardCollection.CommitCharacterMutation(charId, (session, wizard) => {
                var ledger = session.Load<GardenLedger>(GardenLedger.DocumentId(room.OwnerId, room.DeedId, room.Zone));
                if (ledger is null || ledger.OwnerId != room.OwnerId || wizard.CharId != charId) return false;
                var before = ledger.Version;
                var engine = Engine(session, room, indoors);
                var error = change(engine, ledger, wizard, session, now);
                if (error is not null) { result = new(error); return false; }
                if (!requireOwner && ledger.Version == before) { result = new GardenResult(null) { Ledger = ledger.Copy() }; return false; }
                result = new GardenResult(null) { Ledger = ledger.Copy(), Patches = engine.Patches.ToList(), Refresh = engine.NeedsRefresh };
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                if (!requireOwner) session.Advanced.IgnoreChangesFor(wizard); // a visitor's catch-up never touches the owner
                return true;
            }, committed => afterCommit?.Invoke(committed), onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            if (!saved && result.Error is null && result.Ledger is not null && result.Patches.Count == 0) return result; // nothing due
            return saved ? result : result.Error is null ? new("Your garden could not be changed.") : result;
        }
        catch (Exception ex) {
            Logger.Error("Classic garden change for {0} failed: {1}", Logger.Args(live.CharId, ex.Message));
            return new("Your garden could not be saved. Please try again.");
        }
    }
}
