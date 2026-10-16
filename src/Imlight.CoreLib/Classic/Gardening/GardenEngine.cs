using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Gardening;

internal readonly record struct GardenHarvest(string Error, uint PlantTemplateId = 0, int Xp = 0, bool Final = false) {
    internal bool Done => Error is null;
}

/// <summary>A planter (pot) placed in the room: it holds one plant (December 2010: "a planter in your dorm").</summary>
internal sealed record GardenPot(ulong ItemId, uint TemplateId, float X, float Y, float Z, SoilInfo Soil);

/// <summary>A placed object near the garden (furniture): its template's adjectives feed the plants' likes and dislikes.</summary>
internal readonly record struct GardenNeighbour(uint TemplateId, float X, float Y, float Z);

// CLASSIC: the authoritative garden simulation, on a supplied clock so tests can run days in microseconds.
// Rules from the archived December 2010 notes (un_december2010.txt): five stages seed -> seedling -> young -> mature ->
// elder; needs (water, sun, pollination, magic, music) must be met by growing spells, which "fulfill those needs for a
// period of time"; an unmet need wilts the plant and it "will die before too long"; only mature/elder plants with no
// needs or bad pests can be harvested, "once every day or so" while mature, and an elder "one final time" before it
// disappears. Pests "infest a plant and hinder its growth ... eventually the plant will begin to wilt and die ... to
// eliminate a pest, the spell must be of high enough rank"; pest prevention spells "keep pests away". Likes and
// dislikes: "A plant will grow faster if it is surrounded by things it likes, and it will grow slower if it is
// surrounded by things it dislikes" (Fandom, 2011-03). Timing numbers are the client's templates (owner ruling D7).
// Inferred (marked in the report): growth is paused while a need is unmet or a bad pest is on the plant (the tutorial:
// "Plants that are cared for ... will advance through the stages of life", WizQst576B8_00000010; "growth spells will
// have no effect" on an infested plant, WizQst576B9_00000017); meeting every need of a wilting plant returns it to its
// young template, as the client's patch handler does (0x140f286e0 cases 4 and 6); a pest check rolls
// m_percentPerHour x the hours since the last check; protection rank blocks pests of an equal or lower rank (case 7
// removes such a pest); likes multiply (clamped as the client clamps, 0x141d95da0) and scale time left as it does.
internal sealed class GardenEngine(IGardenCatalog catalog, Random random) {
    internal List<GardenPatch> Patches { get; } = [];
    private const int MaxEventsPerPlant = 64;
    private const float PotReach = 60f;          // a planting point counts as inside a pot within this distance (inferred)

    /// <summary>The planters placed in the room (furniture rows with a GardeningPot template).</summary>
    internal IReadOnlyList<GardenPot> Pots { get; set; } = [];
    /// <summary>Furniture placed in the room (likes and dislikes).</summary>
    internal IReadOnlyList<GardenNeighbour> Neighbours { get; set; } = [];
    /// <summary>Indoors (a dorm or a castle interior): plants only grow in planters there.</summary>
    internal bool Indoors { get; set; }
    internal float LikesRadius { get; set; } = 1000f;
    internal float MinModifier { get; set; } = 0.1f;
    internal float MaxModifier { get; set; } = 5f;
    internal bool PestsEnabled { get; set; }
    internal double PestChanceScale { get; set; } = 1;
    /// <summary>True when a change has no patch command (a pest arriving): the client must reload the garden blob.</summary>
    internal bool NeedsRefresh { get; private set; }

    private void Emit(GardenLedger ledger, byte command, GardenPlantState p, uint value, uint time = 0) {
        ledger.Version++;
        Patches.Add(new GardenPatch(command, p.X, p.Y, p.Z, value, time, ledger.Version));
    }

    // A change the client learns only from the blob: bump the version without a patch and ask for a reload.
    private void Silent(GardenLedger ledger) { ledger.Version++; NeedsRefresh = true; }

    // ---- soil and planting -------------------------------------------------------------------------------------

    internal string PrepareSoil(GardenLedger ledger, GardenSpellInfo spell, float x, float y, float z, uint now) {
        Advance(ledger, now);
        if (spell is not { Kind: GardenSpellKind.SoilPreparation } || catalog.Soil(spell.SoilTemplateId) is not { } soil)
            return "That spell does not prepare soil.";
        // December 2010 notes: "To garden in your dorm room or an indoor space in your Castle, you must buy a planter."
        if (Indoors) return "Indoors, plants grow in planters.";
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
        SoilInfo soil; GardenPot pot = null;
        if (pile is not null && catalog.Soil(pile.TemplateId) is { } pileSoil) soil = pileSoil;
        else if (PotAt(x, y, z) is { } planter) { pot = planter; soil = planter.Soil; pile = null; }
        else return Indoors ? "Plant the seed in a planter." : "Prepare the soil first.";
        if (pot is not null ? ledger.Plants.Any(p => !p.IsSoil && p.PotItemId == pot.ItemId)
                : ledger.At(pile.X, pile.Y, pile.Z, soil: false) is not null) return "Something is already growing there.";
        if (soil.Size < seed.Size) return "That seed needs a larger plot.";
        if (seed.RequiresEnchantedSoil && !soil.Enchanted || seed.RequiresEnrichedSoil && !soil.Enriched)
            return "That seed needs special soil.";
        if (ledger.Plants.Count >= GardenRules.MaxPlants) return "Your garden is full.";
        if (!Finite(x, y, z)) return "You can't garden there.";
        // CLASSIC: on a soil pile the plant sits exactly on the pile; in a pot it sits where the client put it (the pot's
        // plant node, computed client-side from the pot model, 0x140f750b0).
        var plant = new GardenPlantState { X = pile?.X ?? x, Y = pile?.Y ?? y, Z = pile?.Z ?? z, TemplateId = first.TemplateId,
            SoilTemplateId = soil.TemplateId, SeedItemId = seedItemId, SeedItemDocumentId = seedDocument ?? "",
            PlantId = ledger.NextPlantId++, PotItemId = pot?.ItemId ?? 0 };
        ledger.Plants.Add(plant);
        StartStage(ledger, plant, first, now);
        Emit(ledger, GardenRules.PatchAddPlant, plant, plant.TemplateId);
        if (plant.Flags != 0) Emit(ledger, GardenRules.PatchFlags, plant, plant.Flags);
        RescaleNeighbours(ledger, plant, now);
        return null;
    }

    internal string Plow(GardenLedger ledger, float x, float y, float z, uint now) {
        Advance(ledger, now);
        var target = ledger.At(x, y, z, soil: false) ?? ledger.At(x, y, z, soil: true);
        if (target is null) return "There is nothing to plow there.";
        ledger.Plants.Remove(target);
        Emit(ledger, GardenRules.PatchRemove, target, 0);
        if (!target.IsSoil) RescaleNeighbours(ledger, target, now);
        return null;
    }

    private GardenPot PotAt(float x, float y, float z) => Pots
        .Where(p => MathF.Abs(p.Z - z) <= 200f && Dist2(p.X, p.Y, x, y) <= PotReach * PotReach)
        .OrderBy(p => Dist2(p.X, p.Y, x, y)).FirstOrDefault();

    // ---- growing spells ----------------------------------------------------------------------------------------

    /// <summary>Meets the spell's needs for every plant within its radius (May 2011: area spells hit all plants).</summary>
    internal int CastGrowing(GardenLedger ledger, GardenSpellInfo spell, float x, float y, float z, uint now) {
        Advance(ledger, now);
        if (spell is not { Kind: GardenSpellKind.Growing } || !spell.ProvidesAny) return 0;
        var touched = 0;
        foreach (var p in InRadius(ledger, spell, x, y)) {
            if (catalog.Seed(p.TemplateId) is not { } info) continue;
            touched++;
            // The tutorial: "When a plant is infested by pests ... growth spells will have no effect."
            if (BadPest(p)) continue;
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
            if (!changed) continue;
            if (!Blocked(p)) Resume(ledger, p, info, now);
            Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
        }
        if (touched > 0) Advance(ledger, now); // growth that was waiting on this need happens now
        return touched;
    }

    // ---- pests ---------------------------------------------------------------------------------------------------

    /// <summary>An insect-fighting spell removes every bad pest of its rank or lower in its radius (client case 6).
    /// Returns the plants in reach, or -1 for a spell of another kind.</summary>
    internal int Zap(GardenLedger ledger, GardenSpellInfo spell, float x, float y, float z, uint now) {
        Advance(ledger, now);
        if (spell is not { Kind: GardenSpellKind.InsectFighting }) return -1;
        var touched = 0;
        foreach (var p in InRadius(ledger, spell, x, y)) {
            touched++;
            if (!BadPest(p) || catalog.Pest(p.PestTemplateId) is { } pest && pest.Rank > spell.BugZapLevel) continue;
            RemovePest(p, now);
            Emit(ledger, GardenRules.PatchPestRemoved, p, spell.TemplateId);
            AfterPestGone(ledger, p, now);
        }
        if (touched > 0) Advance(ledger, now);
        return touched;
    }

    /// <summary>A pest protection spell guards every plant in its radius for the protection's time and removes a pest it
    /// outranks (client case 7). Returns the plants in reach, or -1 for a spell this cannot cast.</summary>
    internal int Protect(GardenLedger ledger, GardenSpellInfo spell, float x, float y, float z, uint now) {
        Advance(ledger, now);
        if (spell is not { Kind: GardenSpellKind.PlantProtection } || catalog.Pest(spell.ProtectionTemplateId) is not { } guard
            || guard.ProvidesAny) return -1; // the need-providing protections (Monsoon and others) are later spells
        var touched = 0;
        foreach (var p in InRadius(ledger, spell, x, y)) {
            if (catalog.Seed(p.TemplateId) is not { Stage: not GardenStage.Dead }) continue;
            touched++;
            p.PestProtectionTemplateId = guard.TemplateId;
            p.ProtectionUntil = now + Math.Max(60u, GardenRules.Between(guard.WiltMinSeconds, guard.WiltMaxSeconds, random));
            p.Flags |= GardenRules.FlagProtected;
            Emit(ledger, GardenRules.PatchProtection, p, guard.TemplateId);
            if (BadPest(p) && catalog.Pest(p.PestTemplateId) is { } pest && pest.Rank <= guard.Rank) {
                RemovePest(p, now); // the client removes it itself on case 7
                AfterPestGone(ledger, p, now);
            }
        }
        if (touched > 0) Advance(ledger, now);
        return touched;
    }

    private void RemovePest(GardenPlantState p, uint now) {
        p.PestTemplateId = 0;
        p.PestWiltAt = 0;
        p.Flags &= ~(GardenRules.FlagPest | GardenRules.FlagGoodPest);
        p.PestCheckAt = PestsEnabled && catalog.Seed(p.TemplateId) is { PestCheckSeconds: > 0 } info && info.Pests.Count > 0
            ? now + info.PestCheckSeconds : 0;
    }

    // Client cases 6 and 7: a wilting plant left without needs or pests becomes its young template on the client by
    // itself; the server follows without a patch. Otherwise growth resumes.
    private void AfterPestGone(GardenLedger ledger, GardenPlantState p, uint now) {
        if (catalog.Seed(p.TemplateId) is not { } info || Blocked(p)) return;
        if (info.Stage == GardenStage.Wilting) {
            if (catalog.Seed(info.YoungTemplateId) is { } young) {
                p.TemplateId = young.TemplateId;
                StartStage(ledger, p, young, now);
            }
            return;
        }
        Resume(ledger, p, info, now);
    }

    // ---- utility spells ------------------------------------------------------------------------------------------

    /// <summary>Revive: a dead plant becomes a young plant of the same type (December 2010 notes; client case 8 swaps a
    /// dead plant to its m_youngTemplateID).</summary>
    internal string Revive(GardenLedger ledger, float x, float y, float z, uint now) {
        Advance(ledger, now);
        var p = ledger.At(x, y, z, soil: false);
        if (p is null || catalog.Seed(p.TemplateId) is not { } info) return "There is no plant there.";
        if (info.Stage != GardenStage.Dead || catalog.Seed(info.YoungTemplateId) is not { } young) return "Only a dead plant can be revived.";
        p.TemplateId = young.TemplateId;
        p.Flags &= 0xfffc0e01; // the client's own mask in case 8
        p.PestTemplateId = 0; p.PestWiltAt = 0; p.HarvestCount = 0;
        StartStage(ledger, p, young, now);
        Emit(ledger, GardenRules.PatchRevive, p, young.TemplateId);
        if ((p.Flags & ~GardenRules.FlagSoil) != 0) Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
        return null;
    }

    // ---- harvest -----------------------------------------------------------------------------------------------

    internal GardenHarvest Harvest(GardenLedger ledger, float x, float y, float z, uint now) {
        Advance(ledger, now);
        var p = ledger.At(x, y, z, soil: false);
        if (p is null || catalog.Seed(p.TemplateId) is not { } info) return new("There is no plant there.");
        if (info.Stage is not (GardenStage.Mature or GardenStage.Elder)) return new("That plant is not ready to harvest.");
        if (Blocked(p)) return new("Take care of the plant's needs first.");
        if (p.HarvestAt == 0 || now < p.HarvestAt) return new("That plant is not ready to harvest.");
        if (info.Stage == GardenStage.Mature && info.MaxHarvests > 0 && p.HarvestCount >= info.MaxHarvests)
            return new("That plant has nothing more to give until it grows.");
        p.HarvestCount++;
        if (info.Stage == GardenStage.Elder) {
            ledger.Plants.Remove(p);
            Emit(ledger, GardenRules.PatchRemove, p, 0);
            RescaleNeighbours(ledger, p, now);
            return new(null, info.TemplateId, info.HarvestXp, Final: true);
        }
        p.Flags &= ~GardenRules.FlagHarvestReady;
        p.HarvestAt = now + Math.Max(60u, Scaled(GardenRules.Between(info.HarvestMin, info.HarvestMax, random), p.Modifier));
        Emit(ledger, GardenRules.PatchHarvested, p, p.Flags, p.HarvestAt);
        return new(null, info.TemplateId, info.HarvestXp);
    }

    // ---- time --------------------------------------------------------------------------------------------------

    /// <summary>Applies every need, pest, wilt, stage and harvest event due by <paramref name="now"/>, in time order.</summary>
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
        var growing = info.Stage == GardenStage.Wilting || !Blocked(p);
        if (growing && p.NextStageAt != 0 && p.NextStageAt <= now) { best = p.NextStageAt; kind = 3; }
        for (var i = 0; i < 5; i++) {
            if (p.NeedAt[i] != 0 && p.NeedSince[i] == 0 && p.NeedAt[i] <= now && p.NeedAt[i] < best) { best = p.NeedAt[i]; kind = 1; need = i; }
            var wilt = Grower(p, info).WiltSeconds[i];
            if (info.Stage is not (GardenStage.Wilting or GardenStage.Dead) && p.NeedSince[i] != 0 && wilt > 0
                && p.NeedSince[i] + wilt <= now && p.NeedSince[i] + wilt < best) { best = p.NeedSince[i] + wilt; kind = 2; need = i; }
        }
        var alive = info.Stage is not (GardenStage.Wilting or GardenStage.Dead);
        if (alive && PestsEnabled && p.PestCheckAt != 0 && p.PestCheckAt <= now && p.PestCheckAt < best) { best = p.PestCheckAt; kind = 4; }
        if (alive && p.PestWiltAt != 0 && p.PestWiltAt <= now && p.PestWiltAt < best) { best = p.PestWiltAt; kind = 5; }
        if (p.ProtectionUntil != 0 && p.ProtectionUntil <= now && p.ProtectionUntil < best) { best = p.ProtectionUntil; kind = 6; }
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
                    StartStage(ledger, p, next, best, keepNeeds: false);
                    Emit(ledger, GardenRules.PatchAdvanceMature, p, next.TemplateId);
                    if ((p.Flags & GardenRules.NeedMask) != 0) Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
                }
                else Replace(ledger, p, next, best);
                return true;
            case 4:
                RollPest(ledger, p, info, best);
                return true;
            case 5:
                p.PestWiltAt = 0;
                if (catalog.Seed(info.WiltTemplateId) is { } pestWilt) Replace(ledger, p, pestWilt, best);
                return true;
            case 6:
                p.ProtectionUntil = 0;
                p.PestProtectionTemplateId = 0;
                p.Flags &= ~GardenRules.FlagProtected;
                Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
                return true;
            default:
                return false;
        }
    }

    private void RollPest(GardenLedger ledger, GardenPlantState p, SeedInfo info, uint at) {
        var interval = info.PestCheckSeconds;
        p.PestCheckAt = interval > 0 ? at + interval : 0;
        if (p.PestTemplateId != 0 || info.Pests.Count == 0) return;
        var hours = interval / 3600.0;
        foreach (var (percent, pestId) in info.Pests) {
            if (catalog.Pest(pestId) is not { } pest) continue;
            if (random.NextDouble() >= Math.Clamp(percent / 100.0 * hours * PestChanceScale, 0, 1)) continue;
            // A protection keeps away pests of its rank or lower.
            if (p.ProtectionUntil > at && catalog.Pest(p.PestProtectionTemplateId) is { } guard && guard.Rank >= pest.Rank) return;
            p.PestTemplateId = pest.TemplateId;
            p.Flags |= GardenRules.FlagPest | (pest.Good ? GardenRules.FlagGoodPest : 0);
            p.Flags &= ~GardenRules.FlagHarvestReady;
            if (!pest.Good) {
                p.PestWiltAt = at + Math.Max(60u, GardenRules.Between(pest.WiltMinSeconds, pest.WiltMaxSeconds, random));
                if (p.PausedSince == 0) p.PausedSince = at;
            }
            p.PestCheckAt = 0; // no further checks while the pest is there
            Silent(ledger);    // no patch adds a pest: the client reloads the garden
            return;
        }
    }

    private void Replace(GardenLedger ledger, GardenPlantState p, SeedInfo next, uint at) {
        var wilting = next.Stage == GardenStage.Wilting;
        Emit(ledger, GardenRules.PatchRemove, p, 0);
        p.TemplateId = next.TemplateId;
        StartStage(ledger, p, next, at, keepNeeds: wilting);
        Emit(ledger, GardenRules.PatchAddPlant, p, p.TemplateId);
        if (p.Flags != 0) Emit(ledger, GardenRules.PatchFlags, p, p.Flags);
        if (p.PestTemplateId != 0) NeedsRefresh = true; // a new plant on the client carries no pest: reload
    }

    private void StartStage(GardenLedger ledger, GardenPlantState p, SeedInfo info, uint at, bool keepNeeds = false) {
        p.StageStart = at;
        p.Modifier = LikesModifier(ledger, p, info);
        p.NextStageAt = info.NextStageTemplateId != 0 && info.NextStageSeconds > 0 ? at + Scaled(info.NextStageSeconds, p.Modifier) : 0;
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
            GardenStage.Mature => at + Math.Max(60u, Scaled(GardenRules.Between(info.HarvestMin, info.HarvestMax, random), p.Modifier)),
            GardenStage.Elder => at + Scaled(GardenRules.Between(info.HarvestMin, info.HarvestMax, random), p.Modifier),
            _ => 0,
        };
        if (p.PestTemplateId == 0) p.PestCheckAt = PestsEnabled && info.PestCheckSeconds > 0 && info.Pests.Count > 0 ? at + info.PestCheckSeconds : 0;
        else if (BadPest(p) && p.PausedSince == 0 && info.Stage is not (GardenStage.Wilting or GardenStage.Dead)) p.PausedSince = at;
        if (info.Stage == GardenStage.Dead) { p.NextStageAt = 0; p.HarvestAt = 0; Array.Clear(p.NeedAt); p.PestCheckAt = 0; p.PestWiltAt = 0; }
        if (info.Stage == GardenStage.Wilting) { p.PestCheckAt = 0; p.PestWiltAt = 0; }
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
        var ready = p.HarvestAt != 0 && now >= p.HarvestAt && !Blocked(p)
            && !(info.Stage == GardenStage.Mature && info.MaxHarvests > 0 && p.HarvestCount >= info.MaxHarvests);
        if (!ready || (p.Flags & GardenRules.FlagHarvestReady) != 0) return;
        p.Flags |= GardenRules.FlagHarvestReady;
        Emit(ledger, GardenRules.PatchHarvestReady, p, 0);
    }

    // ---- likes and dislikes ----------------------------------------------------------------------------------------

    /// <summary>
    /// The plant's likes/dislikes factor on time: the product of every entry of its template whose adjective is on
    /// something within <see cref="LikesRadius"/> (furniture, other plants and their pests, as the client lists them in
    /// 0x140f2c430), clamped as the client clamps (0x141d95da0). 1 with no entry active.
    /// </summary>
    internal float LikesModifier(GardenLedger ledger, GardenPlantState p, SeedInfo info) {
        if (info.Likes.Count == 0) return 1f;
        var near = new HashSet<string>(StringComparer.Ordinal);
        var r2 = LikesRadius * LikesRadius;
        foreach (var n in Neighbours) {
            if (Dist3(n.X, n.Y, n.Z, p) >= r2) continue;
            foreach (var a in catalog.Adjectives(n.TemplateId)) near.Add(a);
        }
        foreach (var other in ledger.Plants) {
            if (other.IsSoil || Dist3(other.X, other.Y, other.Z, p) >= r2) continue;
            if ((ReferenceEquals(other, p) ? info : catalog.Seed(other.TemplateId)) is { } o) foreach (var a in o.Adjectives) near.Add(a);
            if (other.PestTemplateId != 0) foreach (var a in catalog.Adjectives(other.PestTemplateId)) near.Add(a);
        }
        var factor = 1f;
        foreach (var (adjective, modifier) in info.Likes) if (modifier > 0 && near.Contains(adjective)) factor *= modifier;
        return Math.Clamp(factor, MinModifier, MaxModifier);
    }

    // A plant planted, plowed or harvested away changes its neighbours' likes: rescale their time left the client's way
    // (0x141d95da0: stage time from the stage start, harvest time from now).
    private void RescaleNeighbours(GardenLedger ledger, GardenPlantState changed, uint now) {
        var r2 = LikesRadius * LikesRadius;
        foreach (var p in ledger.Plants.Where(p => !p.IsSoil && !ReferenceEquals(p, changed)
                     && Dist3(changed.X, changed.Y, changed.Z, p) < r2).ToList()) {
            if (catalog.Seed(p.TemplateId) is not { } info) continue;
            var next = LikesModifier(ledger, p, info);
            if (MathF.Abs(next - p.Modifier) < 1e-4f || p.Modifier <= 0) continue;
            var ratio = next / p.Modifier;
            if (p.NextStageAt > now && p.NextStageAt > p.StageStart)
                p.NextStageAt = p.StageStart + (uint)Math.Max(1, Math.Round((p.NextStageAt - p.StageStart) * (double)ratio));
            if (p.HarvestAt > now) p.HarvestAt = now + (uint)Math.Max(1, Math.Round((p.HarvestAt - now) * (double)ratio));
            p.Modifier = next;
        }
    }

    private static uint Scaled(uint seconds, float modifier)
        => modifier is > 0 and not 1f ? (uint)Math.Min(uint.MaxValue / 4, Math.Round(seconds * (double)modifier)) : seconds;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    // The template whose need rates apply: a wilting plant keeps the needs of the stage it wilted from (its young).
    private SeedInfo Grower(GardenPlantState p, SeedInfo info)
        => info.Stage is GardenStage.Wilting && catalog.Seed(info.YoungTemplateId) is { } young ? young : info;

    /// <summary>A bad pest is on the plant (a good pest, flag 0x400, does no harm).</summary>
    internal static bool BadPest(GardenPlantState p)
        => (p.Flags & GardenRules.FlagPest) != 0 && (p.Flags & GardenRules.FlagGoodPest) == 0;

    /// <summary>Needs or a bad pest: the plant does not grow and cannot be harvested.</summary>
    internal static bool Blocked(GardenPlantState p) => (p.Flags & GardenRules.NeedMask) != 0 || BadPest(p);

    private static List<GardenPlantState> InRadius(GardenLedger ledger, GardenSpellInfo spell, float x, float y) {
        var radius = Math.Max(spell.Radius, (int)GardenRules.SamePointDistance);
        return ledger.Plants.Where(p => !p.IsSoil && Dist2(p.X, p.Y, x, y) <= (float)radius * radius).ToList();
    }

    private static float Dist2(float ax, float ay, float bx, float by) { var dx = ax - bx; var dy = ay - by; return dx * dx + dy * dy; }
    private static float Dist3(float x, float y, float z, GardenPlantState p) {
        var dx = p.X - x; var dy = p.Y - y; var dz = p.Z - z; return dx * dx + dy * dy + dz * dz;
    }

    private static bool Finite(float x, float y, float z) => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
        && MathF.Abs(x) <= 32766 && MathF.Abs(y) <= 32766;
}
