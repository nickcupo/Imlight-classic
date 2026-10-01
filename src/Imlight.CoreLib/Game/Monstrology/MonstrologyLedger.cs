using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Monstrology;

// Persistence DTOs are server-only, never a replacement for stock MonsterData STR.
internal sealed class MonstrologyLedger {
    public ulong OwnerId { get; set; }
    public int Experience { get; set; }
    public int Level { get; set; } = 1;
    public Dictionary<uint, int> Animus { get; set; } = new();
    public Dictionary<string, ExtractionReceipt> Extractions { get; set; } = new();
    public Dictionary<string, CreationReceipt> Creations { get; set; } = new();
}

internal sealed record ExtractionReceipt(uint Creature, int Animus, int Experience);
internal sealed record CreationReceipt(uint Creature, uint OutputTemplate, int AnimusCost, bool Delivered, int GoldCost = 0, MonstrologyCreationKind Kind = MonstrologyCreationKind.SummonCard, ulong ItemId = 0);
internal sealed record ExtractionAward(string OperationId, ulong OwnerId, uint Creature,
    int Animus, int Experience, bool Eligible, bool SuccessfulHit, bool Victory);
internal sealed record AnimusCreation(string OperationId, ulong OwnerId, uint Creature,
    uint OutputTemplate, int AnimusCost, int GoldCost, bool ValidatedStockRecipe, MonstrologyCreationKind Kind = MonstrologyCreationKind.SummonCard);
internal enum MonstrologyResult { Applied, Replay, Rejected, InsufficientAnimus, UnresolvedGoldContract, InsufficientGold, CommitFailed }

internal static class MonstrologyRules {
    // Thresholds are cumulative and must come from a validated config adapter; no invented defaults.
    internal static MonstrologyResult Award(MonstrologyLedger state, ExtractionAward award,
                                            IReadOnlyList<int> thresholds) {
        if (state.OwnerId == 0 || award.OwnerId != state.OwnerId || string.IsNullOrWhiteSpace(award.OperationId)
            || award.OperationId.Length > 160 || award.Creature == 0 || award.Animus <= 0 || award.Experience < 0
            || !award.Eligible || !award.SuccessfulHit || !award.Victory || !ValidThresholds(thresholds))
            return MonstrologyResult.Rejected;
        var receipt = new ExtractionReceipt(award.Creature, award.Animus, award.Experience);
        if (state.Extractions.TryGetValue(award.OperationId, out var prior))
            return prior == receipt ? MonstrologyResult.Replay : MonstrologyResult.Rejected;
        state.Animus.TryGetValue(award.Creature, out var amount);
        if (amount < 0 || (long)amount + award.Animus > ushort.MaxValue
            || (!state.Animus.ContainsKey(award.Creature) && state.Animus.Count >= MonstrologyTomeCodec.MaximumEntries)
            || (long)state.Experience + award.Experience > int.MaxValue)
            return MonstrologyResult.Rejected;
        state.Animus[award.Creature] = amount + award.Animus;
        state.Experience += award.Experience;
        state.Level = thresholds.Skip(1).Count(x => state.Experience >= x);
        state.Extractions.Add(award.OperationId, receipt);
        return MonstrologyResult.Applied;
    }

    // Atomically reserves Animus and a durable creation entitlement in the same ledger document.
    // It is NOT stock item delivery. Nonzero Gold recipes fail closed until a shared atomic wallet adapter exists.
    internal static MonstrologyResult ReserveCreation(MonstrologyLedger state, AnimusCreation request) {
        if (state.OwnerId == 0 || request.OwnerId != state.OwnerId || string.IsNullOrWhiteSpace(request.OperationId)
            || request.OperationId.Length > 160 || request.Creature == 0 || request.OutputTemplate == 0
            || request.AnimusCost <= 0 || request.GoldCost < 0 || !request.ValidatedStockRecipe)
            return MonstrologyResult.Rejected;
        if (request.GoldCost != 0) return MonstrologyResult.UnresolvedGoldContract;
        if (state.Creations.TryGetValue(request.OperationId, out var prior))
            return prior.Creature == request.Creature && prior.OutputTemplate == request.OutputTemplate
                && prior.AnimusCost == request.AnimusCost ? MonstrologyResult.Replay : MonstrologyResult.Rejected;
        state.Animus.TryGetValue(request.Creature, out var available);
        if (available < request.AnimusCost) return MonstrologyResult.InsufficientAnimus;
        state.Animus[request.Creature] = available - request.AnimusCost;
        state.Creations.Add(request.OperationId,
            new CreationReceipt(request.Creature, request.OutputTemplate, request.AnimusCost, false));
        return MonstrologyResult.Applied;
    }

    internal static MonstrologyResult DeliverCard(MonstrologyLedger state, AnimusCreation request, int availableGold) {
        if (state.OwnerId == 0 || request.OwnerId != state.OwnerId || string.IsNullOrWhiteSpace(request.OperationId)
            || request.OperationId.Length > 160 || request.Creature == 0 || request.OutputTemplate == 0
            || request.AnimusCost <= 0 || request.GoldCost < 0 || !request.ValidatedStockRecipe
            || !MonstrologyCreation.TryKind((int)request.Kind, out _)) return MonstrologyResult.Rejected;
        if (state.Creations.TryGetValue(request.OperationId, out var prior))
            return prior.Delivered && prior.Creature == request.Creature && prior.OutputTemplate == request.OutputTemplate
                && prior.AnimusCost == request.AnimusCost && prior.GoldCost == request.GoldCost && prior.Kind == request.Kind
                ? MonstrologyResult.Replay : MonstrologyResult.Rejected;
        state.Animus.TryGetValue(request.Creature, out var animus);
        if (animus < request.AnimusCost) return MonstrologyResult.InsufficientAnimus;
        if (availableGold < request.GoldCost) return MonstrologyResult.InsufficientGold;
        state.Animus[request.Creature] = animus - request.AnimusCost;
        state.Creations.Add(request.OperationId,
            new CreationReceipt(request.Creature, request.OutputTemplate, request.AnimusCost, true, request.GoldCost, request.Kind));
        return MonstrologyResult.Applied;
    }

    internal static bool ValidThresholds(IReadOnlyList<int> thresholds)
        => thresholds != null && thresholds.Count > 0 && thresholds.Count <= byte.MaxValue
            && thresholds[0] == 0 && Enumerable.Range(1, thresholds.Count - 1)
                .All(i => thresholds[i] > thresholds[i - 1] || (i == 1 && thresholds[i] == 0));
}
