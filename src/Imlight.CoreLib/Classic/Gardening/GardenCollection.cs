using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
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
    internal static GardenResult Advance(Wizard live, HousingRoomIdentity room)
        => Mutate(live, room, (engine, ledger, saved, session, now) => { engine.Advance(ledger, now); return null; },
            requireOwner: false);

    internal static GardenResult PrepareSoil(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now) => engine.PrepareSoil(ledger, spell, x, y, z, now),
            GardenSpellKind.SoilPreparation);

    internal static GardenResult CastGrowing(Wizard live, HousingRoomIdentity room, uint spellTemplate, float x, float y, float z)
        => Spell(live, room, spellTemplate, (engine, ledger, spell, now)
            => engine.CastGrowing(ledger, spell, x, y, z, now) > 0 ? null : "There is no plant there.", GardenSpellKind.Growing);

    internal static GardenResult Plow(Wizard live, HousingRoomIdentity room, float x, float y, float z)
        => Mutate(live, room, (engine, ledger, saved, session, now) => engine.Plow(ledger, x, y, z, now));

    internal static GardenResult Plant(Wizard live, HousingRoomIdentity room, uint seedTemplate, ulong seedItemId, float x, float y, float z) {
        List<WizClientObjectItem> backpack = [];
        ulong removed = 0;
        var result = Mutate(live, room, (engine, ledger, saved, session, now) => {
            if (!HouseCollection.TryReadHoldings(session, saved, out _, out backpack)) return "Your backpack could not be read.";
            var doc = backpack.FirstOrDefault(i => i.m_globalID.Full == seedItemId && (uint)i.m_templateID.Full == seedTemplate)
                ?? (seedItemId == 0 ? backpack.FirstOrDefault(i => (uint)i.m_templateID.Full == seedTemplate) : null);
            if (doc is null || doc.m_characterId != live.CharId) return "You don't have that seed.";
            var error = engine.Plant(ledger, seedTemplate, x, y, z, now, saved.GameStats?.m_gardeningLevel ?? 0,
                doc.m_globalID.Full, session.Advanced.GetDocumentId(doc));
            if (error is not null) return error;
            // The seed leaves the backpack; its original item document is retained (as placed furniture is).
            removed = doc.m_globalID.Full;
            saved.InventoryBehavior.InventoryItemIds = saved.InventoryBehavior.InventoryItemIds.Where(id => id != removed).ToList();
            backpack.Remove(doc);
            return null;
        }, afterCommit: saved => WizardInventoryTransactions.PublishCommittedBackpack(live, saved, backpack));
        return result.Saved ? result with { RemovedItemId = removed } : result;
    }

    internal static GardenResult Harvest(Wizard live, HousingRoomIdentity room, float x, float y, float z) {
        GardenHarvest harvest = default;
        int xp = 0, level = 0;
        var result = Mutate(live, room, (engine, ledger, saved, session, now) => {
            harvest = engine.Harvest(ledger, x, y, z, now);
            if (!harvest.Done) return harvest.Error;
            if (saved.GameStats is null) return "Your gardening experience could not be saved.";
            // CLASSIC: gardening XP is the plant template's m_harvestXP (client value, D7). Ranks and rank-gated
            // seeds are stage 4; harvest rewards come from dated harvest tables (none are authored yet).
            saved.GameStats.m_gardeningXP = checked(saved.GameStats.m_gardeningXP + Math.Max(0, harvest.Xp));
            xp = saved.GameStats.m_gardeningXP; level = saved.GameStats.m_gardeningLevel;
            return null;
        }, afterCommit: saved => {
            if (live.GameStats is not null) { live.GameStats.m_gardeningXP = saved.GameStats.m_gardeningXP; }
        });
        return result.Saved ? result with { Harvest = harvest, GardeningXp = xp, GardeningLevel = level } : result;
    }

    private delegate string SpellApply(GardenEngine engine, GardenLedger ledger, GardenSpellInfo spell, uint now);

    private static GardenResult Spell(Wizard live, HousingRoomIdentity room, uint spellTemplate, SpellApply apply, GardenSpellKind kind) {
        int? energy = null; uint tick = 0;
        var info = GardenCatalog.Current.Spell(spellTemplate);
        if (info is null || info.Kind != kind) return new("That spell can't be cast here.");
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
        }, afterCommit: saved => live.PetOwnerBehavior?.PublishCommittedEnergy(saved.PetOwnerBehavior));
        return result.Saved ? result with { Energy = energy, EnergyTick = tick } : result;
    }

    private delegate string Change(GardenEngine engine, GardenLedger ledger, Wizard saved, IDocumentSession session, uint now);

    private static GardenResult Mutate(Wizard live, HousingRoomIdentity room, Change change, Action<Wizard> afterCommit = null,
        bool requireOwner = true) {
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
                var engine = new GardenEngine(GardenCatalog.Current, Random);
                var error = change(engine, ledger, wizard, session, now);
                if (error is not null) { result = new(error); return false; }
                if (!requireOwner && ledger.Version == before) { result = new GardenResult(null) { Ledger = ledger.Copy() }; return false; }
                result = new GardenResult(null) { Ledger = ledger.Copy(), Patches = engine.Patches.ToList() };
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
