using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Gardening;

internal readonly record struct GardenHarvest(string Error, uint PlantTemplateId = 0, int Xp = 0, bool Final = false) {
    internal bool Done => Error is null;
}

// CLASSIC: the authoritative garden simulation, on a supplied clock so tests can run days in microseconds.
// Rules from the archived December 2010 notes (un_december2010.txt): five stages seed -> seedling -> young -> mature ->
// elder; needs (water, sun, pollination, magic, music) must be met by growing spells, which "fulfill those needs for a
// period of time"; an unmet need wilts the plant and it "will die before too long"; only mature/elder plants with no
// needs or bad pests can be harvested, "once every day or so" while mature, and an elder "one final time" before it
// disappears. Timing numbers are the client's SeedBehaviorTemplate rates (owner ruling D7, unverified).
// Inferred (marked in the report): growth is paused while a need is unmet; meeting every need of a wilting plant
// returns it to its young template, as the client's own patch handler does (0x140f286e0 case 4).
internal sealed class GardenEngine(IGardenCatalog catalog, Random random) {
    internal List<GardenPatch> Patches { get; } = [];
    private const int MaxEventsPerPlant = 64;

    private void Emit(GardenLedger ledger, byte command, GardenPlantState p, uint value, uint time = 0) {
        ledger.Version++;
        Patches.Add(new GardenPatch(command, p.X, p.Y, p.Z, value, time, ledger.Version));
    }

    // ---- soil and planting -------------------------------------------------------------------------------------

    internal string PrepareSoil(GardenLedger ledger, GardenSpellInfo spell, float x, float y, float z, uint now) {
        Advance(ledger, now);
        if (spell is not { Kind: GardenSpellKind.SoilPreparation } || catalog.Soil(spell.SoilTemplateId) is not { } soil)
            return "That spell does not prepare soil.";
        if (!Finite(x, y, z)) return "You can't garden there.";
        if (ledger.Any(x, y, z) is not null) return "Something is already growing there.";
        if (ledger.Plants.Count >= GardenRules.MaxPlants) return "Your garden is full.";
        var pile = new GardenPlantState { X = x, Y = y, Z = z, TemplateId = soil.TemplateId, Flags = GardenRules.FlagSoil,
            StageStart = now, PlantId = ledger.NextPlantId++ };
        ledger.Plants.Add(pile);
        Emit(ledger, GardenRules.PatchAddSoil, pile, soil.TemplateId);
        return null;
    }

    internal string Plant(GardenLedger ledger, uint seedTemplate, float x, float y, float z, uint now, int gardeningLevel,
        ulong seedItemId = 0, string seedDocument = "") {
        Advance(ledger, now);
        if (catalog.Seed(seedTemplate) is not { Stage: GardenStage.Seed } seed
            || catalog.Seed(seed.NextStageTemplateId) is not { } first) return "That is not a seed.";
        if (seed.GardeningLevelRequirement > Math.Max(1, gardeningLevel)) return "Your gardening rank is too low for that seed.";
        var pile = ledger.At(x, y, z, soil: true);
        if (pile is null || catalog.Soil(pile.TemplateId) is not { } soil) return "Prepare the soil first.";
        if (ledger.At(pile.X, pile.Y, pile.Z, soil: false) is not null) return "Something is already growing there.";
        if (soil.Size < seed.Size) return "That seed needs a larger plot.";
        if (seed.RequiresEnchantedSoil && !soil.Enchanted || seed.RequiresEnrichedSoil && !soil.Enriched)
            return "That seed needs special soil.";
        // CLASSIC: the plant sits exactly on the soil pile the client targeted.
        var plant = new GardenPlantState { X = pile.X, Y = pile.Y, Z = pile.Z, TemplateId = first.TemplateId,
            SoilTemplateId = soil.TemplateId, SeedItemId = seedItemId, SeedItemDocumentId = seedDocument ?? "",
            PlantId = ledger.NextPlantId++ };
        ledger.Plants.Add(plant);
        StartStage(plant, first, now);
        Emit(ledger, GardenRules.PatchAddPlant, plant, plant.TemplateId);
        if (plant.Flags != 0) Emit(ledger, GardenRules.PatchFlags, plant, plant.Flags);
        return null;
    }

    internal string Plow(GardenLedger ledger, float x, float y, float z, uint now) {
        Advance(ledger, now);
        var target = ledger.At(x, y, z, soil: false) ?? ledger.At(x, y, z, soil: true);
        if (target is null) return "There is nothing to plow there.";
        ledger.Plants.Remove(target);
        Emit(ledger, GardenRules.PatchRemove, target, 0);
        return null;
    }

    // ---- growing spells ----------------------------------------------------------------------------------------

    /// <summary>Meets the spell's needs for every plant within its radius (May 2011: area spells hit all plants).</summary>
    internal int CastGrowing(GardenLedger ledger, GardenSpellInfo spell, float x, float y, float z, uint now) {
        Advance(ledger, now);
        if (spell is not { Kind: GardenSpellKind.Growing } || !spell.ProvidesAny) return 0;
        var radius = Math.Max(spell.Radius, (int)GardenRules.SamePointDistance);
        var touched = 0;
        foreach (var p in ledger.Plants.Where(p => !p.IsSoil && Near(p, x, y, radius)).ToList()) {
            if (catalog.Seed(p.TemplateId) is not { } info) continue;
            var changed = false;
            for (var i = 0; i < 5; i++) {
                if (!spell.Provides[i]) continue;
                var stageInfo = Grower(p, info);
                if (stageInfo.NeedMax[i] == 0 && p.NeedSince[i] == 0) continue; // this plant never needs it now
                changed |= p.NeedSince[i] != 0;
                p.NeedSince[i] = 0;
                p.Flags &= ~GardenRules.NeedFlags[i];
                p.NeedAt[i] = stageInfo.NeedMax[i] == 0 ? 0 : now + GardenRules.Between(stageInfo.NeedMin[i], stageInfo.NeedMax[i], random);
            }
            touched++;
            if (!changed) continue;
            if ((p.Flags & GardenRules.NeedMask) == 0) Resume(ledger, p, info, now);
            Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
        }
        if (touched > 0) Advance(ledger, now); // growth that was waiting on this need happens now
        return touched;
    }

    // ---- harvest -----------------------------------------------------------------------------------------------

    internal GardenHarvest Harvest(GardenLedger ledger, float x, float y, float z, uint now) {
        Advance(ledger, now);
        var p = ledger.At(x, y, z, soil: false);
        if (p is null || catalog.Seed(p.TemplateId) is not { } info) return new("There is no plant there.");
        if (info.Stage is not (GardenStage.Mature or GardenStage.Elder)) return new("That plant is not ready to harvest.");
        if ((p.Flags & (GardenRules.NeedMask | GardenRules.FlagPest)) != 0) return new("Take care of the plant's needs first.");
        if (p.HarvestAt == 0 || now < p.HarvestAt) return new("That plant is not ready to harvest.");
        if (info.Stage == GardenStage.Mature && info.MaxHarvests > 0 && p.HarvestCount >= info.MaxHarvests)
            return new("That plant has nothing more to give until it grows.");
        p.HarvestCount++;
        if (info.Stage == GardenStage.Elder) {
            ledger.Plants.Remove(p);
            Emit(ledger, GardenRules.PatchRemove, p, 0);
            return new(null, info.TemplateId, info.HarvestXp, Final: true);
        }
        p.Flags &= ~GardenRules.FlagHarvestReady;
        p.HarvestAt = now + Math.Max(60u, GardenRules.Between(info.HarvestMin, info.HarvestMax, random));
        Emit(ledger, GardenRules.PatchHarvested, p, p.Flags, p.HarvestAt);
        return new(null, info.TemplateId, info.HarvestXp);
    }

    // ---- time --------------------------------------------------------------------------------------------------

    /// <summary>Applies every need, wilt, stage and harvest event due by <paramref name="now"/>, in time order.</summary>
    internal void Advance(GardenLedger ledger, uint now) {
        foreach (var p in ledger.Plants.Where(p => !p.IsSoil).ToList()) {
            for (var guard = 0; guard < MaxEventsPerPlant; guard++) {
                if (!ledger.Plants.Contains(p) || !Step(ledger, p, now)) break;
            }
            if (ledger.Plants.Contains(p)) ReadyCheck(ledger, p, now);
        }
        ledger.LastAdvanced = Math.Max(ledger.LastAdvanced, now);
    }

    private bool Step(GardenLedger ledger, GardenPlantState p, uint now) {
        if (catalog.Seed(p.TemplateId) is not { } info) return false;
        // The earliest due event.
        uint best = uint.MaxValue; var kind = 0; var need = -1;
        // A stage change due at the same instant as a new need happens first (ties favour growth).
        var growing = info.Stage == GardenStage.Wilting || (p.Flags & GardenRules.NeedMask) == 0;
        if (growing && p.NextStageAt != 0 && p.NextStageAt <= now) { best = p.NextStageAt; kind = 3; }
        for (var i = 0; i < 5; i++) {
            if (p.NeedAt[i] != 0 && p.NeedSince[i] == 0 && p.NeedAt[i] <= now && p.NeedAt[i] < best) { best = p.NeedAt[i]; kind = 1; need = i; }
            var wilt = Grower(p, info).WiltSeconds[i];
            if (info.Stage is not (GardenStage.Wilting or GardenStage.Dead) && p.NeedSince[i] != 0 && wilt > 0
                && p.NeedSince[i] + wilt <= now && p.NeedSince[i] + wilt < best) { best = p.NeedSince[i] + wilt; kind = 2; need = i; }
        }
        switch (kind) {
            case 1:
                p.NeedSince[need] = best;
                p.Flags |= GardenRules.NeedFlags[need];
                p.Flags &= ~GardenRules.FlagHarvestReady;
                if (p.PausedSince == 0 && info.Stage != GardenStage.Wilting) p.PausedSince = best;
                Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
                return true;
            case 2:
                if (catalog.Seed(info.WiltTemplateId) is not { } wilt) { p.NeedSince[need] = 0; return true; }
                Replace(ledger, p, wilt, best);
                return true;
            case 3:
                if (catalog.Seed(info.NextStageTemplateId) is not { } next) { p.NextStageAt = 0; return true; }
                if (info.Stage == GardenStage.Mature && next.Stage == GardenStage.Elder) {
                    // Native case 13 moves a mature plant to m_nextStageTemplateID and sets flag 0x2.
                    p.TemplateId = next.TemplateId; p.Flags |= 0x2;
                    StartStage(p, next, best, keepNeeds: false);
                    Emit(ledger, GardenRules.PatchAdvanceMature, p, next.TemplateId);
                    if ((p.Flags & GardenRules.NeedMask) != 0) Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
                }
                else Replace(ledger, p, next, best);
                return true;
            default:
                return false;
        }
    }

    private void Replace(GardenLedger ledger, GardenPlantState p, SeedInfo next, uint at) {
        var wilting = next.Stage == GardenStage.Wilting;
        Emit(ledger, GardenRules.PatchRemove, p, 0);
        p.TemplateId = next.TemplateId;
        StartStage(p, next, at, keepNeeds: wilting);
        Emit(ledger, GardenRules.PatchAddPlant, p, p.TemplateId);
        if (p.Flags != 0) Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
    }

    private void StartStage(GardenPlantState p, SeedInfo info, uint at, bool keepNeeds = false) {
        p.StageStart = at;
        p.NextStageAt = info.NextStageTemplateId != 0 && info.NextStageSeconds > 0 ? at + info.NextStageSeconds : 0;
        p.PausedSince = 0;
        p.Flags &= ~GardenRules.FlagHarvestReady;
        if (!keepNeeds) {
            for (var i = 0; i < 5; i++) {
                p.NeedSince[i] = 0;
                p.NeedAt[i] = info.NeedMax[i] == 0 ? 0 : at + GardenRules.Between(info.NeedMin[i], info.NeedMax[i], random);
            }
            p.Flags &= ~GardenRules.NeedMask;
        }
        p.HarvestAt = info.Stage switch {
            GardenStage.Mature => at + Math.Max(60u, GardenRules.Between(info.HarvestMin, info.HarvestMax, random)),
            GardenStage.Elder => at + GardenRules.Between(info.HarvestMin, info.HarvestMax, random),
            _ => 0,
        };
        if (info.Stage == GardenStage.Dead) { p.NextStageAt = 0; p.HarvestAt = 0; Array.Clear(p.NeedAt); }
    }

    private void Resume(GardenLedger ledger, GardenPlantState p, SeedInfo info, uint now) {
        if (info.Stage == GardenStage.Wilting) {
            // Native case 4: needs met on a wilting plant -> its m_youngTemplateID.
            if (catalog.Seed(info.YoungTemplateId) is { } young) Replace(ledger, p, young, now);
            return;
        }
        if (p.PausedSince != 0 && now > p.PausedSince) {
            var paused = now - p.PausedSince;
            if (p.NextStageAt != 0) p.NextStageAt += paused;
            if (p.HarvestAt != 0) p.HarvestAt += paused;
        }
        p.PausedSince = 0;
    }

    private void ReadyCheck(GardenLedger ledger, GardenPlantState p, uint now) {
        if (catalog.Seed(p.TemplateId) is not { Stage: GardenStage.Mature or GardenStage.Elder } info) return;
        var ready = p.HarvestAt != 0 && now >= p.HarvestAt && (p.Flags & (GardenRules.NeedMask | GardenRules.FlagPest)) == 0
            && !(info.Stage == GardenStage.Mature && info.MaxHarvests > 0 && p.HarvestCount >= info.MaxHarvests);
        if (!ready || (p.Flags & GardenRules.FlagHarvestReady) != 0) return;
        p.Flags |= GardenRules.FlagHarvestReady;
        Emit(ledger, GardenRules.PatchHarvestReady, p, 0);
    }

    // The template whose need rates apply: a wilting plant keeps the needs of the stage it wilted from (its young).
    private SeedInfo Grower(GardenPlantState p, SeedInfo info)
        => info.Stage is GardenStage.Wilting && catalog.Seed(info.YoungTemplateId) is { } young ? young : info;

    private static bool Near(GardenPlantState p, float x, float y, int radius) {
        var dx = p.X - x; var dy = p.Y - y;
        return dx * dx + dy * dy <= (float)radius * radius;
    }

    private static bool Finite(float x, float y, float z) => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
        && MathF.Abs(x) <= 32766 && MathF.Abs(y) <= 32766;
}
