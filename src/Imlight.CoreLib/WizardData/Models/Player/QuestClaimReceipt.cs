// CLASSIC: a separate acknowledgement ledger; old builds never read it or require a player-schema migration.
using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.WizardData.Models.Player;

public sealed class QuestClaimReceipt {
    public ulong CharId { get; set; }
    public ulong QuestId { get; set; }
    public ulong GoalId { get; set; }
    public string QuestName { get; set; } = string.Empty;
    public string GoalName { get; set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; set; }
    public List<ulong> CompletedGoalIds { get; set; } = [];
    public int Gold { get; set; }
    public int Experience { get; set; }
    public int TrainingPoints { get; set; }
    public int PotionSlots { get; set; }
    public List<uint> LearnedSpells { get; set; } = [];
    public List<QuestClaimReward> Rewards { get; set; } = [];
}

public sealed class QuestClaimReward {
    public string Kind { get; set; } = string.Empty;
    public ulong TemplateId { get; set; }
    public ulong ItemId { get; set; }
    public int Count { get; set; }
}
