// CLASSIC: no-saving reward preparation/staging for the outer terminal quest claim.
using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic;

internal static partial class ClassicQuestClaims {
    private sealed record RewardSource(Result Result, bool GoalPhase, DropTableResult Drop = null) {
        internal DropTableResult Accepted { get; } = new();
        internal List<QuestClaimAction> Numeric { get; } = [];
        internal List<IMessage> StackMessages { get; } = [];
        internal PotionReceipt Potion { get; set; }
    }

    private static bool TryStageRewards(IDocumentSession session, Wizard live, Wizard saved, QuestInstance quest,
        GoalTemplate goal, QuestTemplate template, ServerQuestBehavior goalState, ResultList goalResults, ResultList endResults,
        IActorRef playerRef, CoreObject playerObj, QuestClaimDependencies dependencies,
        out StagedStackRewards stack, out ProgressionReceipt progression, out List<PotionReceipt> potions,
        out DynamodSet dynamods, out bool goldChanged, out bool trainingChanged, out bool learnedChanged,
        out TerminalQuestClaim prepared) {
        stack = null; progression = null; potions = []; dynamods = null;
        goldChanged = trainingChanged = learnedChanged = false; prepared = null;
        var sources = new List<RewardSource>();
        // The inner item gates observe their own phase's fresh journal, before any reward mutation.
        var completedState = saved.QuestBehavior;
        try {
            saved.QuestBehavior = goalState;
            foreach (var result in goalResults.m_results) sources.Add(PrepareSource(result, true));
            saved.QuestBehavior = completedState;
            foreach (var result in endResults.m_results) sources.Add(PrepareSource(result, false));
        }
        finally { saved.QuestBehavior = completedState; }

        RewardSource PrepareSource(Result result, bool goalPhase) {
            if (result is not ResDropTable table) return new(result, goalPhase);
            if (string.IsNullOrWhiteSpace(table.m_tableName)) return new(result, goalPhase, new());
            var rolled = dependencies.RollQuestReward is { } roll ? roll(table.m_tableName, saved)
                : DropTableRoller.RollQuestReward([table.m_tableName], playerRef, playerObj, saved);
            if (rolled is null || rolled.GoldAmount < 0 || rolled.ExperienceAmount < 0 || rolled.TrainingPoints < 0
                || rolled.Items is null || rolled.TreasureCards is null || rolled.Reagents is null)
                throw new InvalidOperationException("A quest reward could not be prepared.");
            // Preserve each source's existing scaling; aggregate the scaled XP into one level/refill calculation.
            var copy = new DropTableResult { DropTableId = rolled.DropTableId, MagicSchool = rolled.MagicSchool,
                GoldAmount = ClassicSettings.Scale(rolled.GoldAmount, ClassicSettings.GoldMultiplier),
                ExperienceAmount = ClassicSettings.Scale(rolled.ExperienceAmount, ClassicSettings.XpMultiplier),
                TrainingPoints = rolled.TrainingPoints, GrantsPotionSlot = rolled.GrantsPotionSlot,
                Items = [.. rolled.Items], TreasureCards = [.. rolled.TreasureCards], Reagents = [.. rolled.Reagents] };
            RouteStackCategories(copy);
            return new(result, goalPhase, copy);
        }

        var drops = sources.Where(source => source.Drop is not null).ToArray();
        var requestedXp = checked(drops.Sum(source => source.Drop.ExperienceAmount));
        if (requestedXp > 0 && !WizardProgressionTransactions.TryStageExperience(live, saved, requestedXp,
            out progression, session: session)) return false; // before pending item stores/reference changes.
        var xpRemaining = progression?.AppliedXp ?? 0;
        var xpEmitted = false;
        foreach (var source in drops) {
            var reward = source.Drop; var accepted = source.Accepted;
            if (reward.GoldAmount > 0) {
                if (saved.GameStats is null) return false;
                var before = saved.GameStats.m_currentGold;
                if (before < 0 || saved.GameStats.m_baseGoldPouch < 0) return false;
                var balance = Math.Min((long)before + reward.GoldAmount, saved.GameStats.m_baseGoldPouch);
                if (balance < 0 || balance > int.MaxValue) return false;
                saved.GameStats.m_currentGold = (int)balance;
                accepted.GoldAmount = Math.Max(0, (int)balance - before);
                goldChanged |= balance != before;
                source.Numeric.Add(new(Message: new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = (int)balance,
                    MaxGold = saved.GameStats.m_baseGoldPouch }));
            }
            accepted.ExperienceAmount = Math.Min(reward.ExperienceAmount, xpRemaining);
            accepted.MagicSchool = reward.MagicSchool;
            xpRemaining -= accepted.ExperienceAmount;
            if (!xpEmitted && reward.ExperienceAmount > 0 && progression is not null) {
                source.Numeric.AddRange(progression.LevelMessages.Select(message => new QuestClaimAction(Message: message)));
                if (progression.OldLevel != progression.Level && progression.Level == MagicLevelsConfig.MaxLevel
                    && MagicLevelsConfig.MaxLevelXp is not null) source.Numeric.Add(new(LevelCapAudit: progression.Level));
                if (progression.XpMessage is not null) source.Numeric.Add(new(Message: progression.XpMessage));
                xpEmitted = true;
            }
            if (reward.TrainingPoints > 0) {
                if (saved.MagicSchoolBehavior is null) return false;
                var training = (long)saved.MagicSchoolBehavior.TrainingPoints + reward.TrainingPoints;
                if (training < 0 || training > ushort.MaxValue) return false;
                saved.MagicSchoolBehavior.TrainingPoints = (int)training;
                trainingChanged = true;
                accepted.TrainingPoints = reward.TrainingPoints;
                source.Numeric.Add(new(Message: new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = (ushort)training }));
            }
            if (reward.GrantsPotionSlot) {
                if (!WizardPotionTransactions.TryStageAddSlotAndFill(live, saved, out var potion)) return false;
                source.Potion = potion;
                potions.Add(potion); // exactly one slot/fill for each passing drop result, never just an aggregate bool.
            }
        }

        var classicCards = ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.TreasureCards)
            && ClassicProgression.QuestCards is { } cardRules
                ? cardRules.CardsFor(quest.QuestName, saved.MagicSchoolBehavior?.MagicSchool.ToString())
                    .SelectMany(card => Enumerable.Repeat(card.Template, card.Count)).ToArray() : [];
        if (!ClassicStackRewards.TryPrepare(live, drops.SelectMany(source => source.Drop.Items).ToArray(),
            drops.SelectMany(source => source.Drop.TreasureCards).Concat(classicCards).ToArray(),
            drops.SelectMany(source => source.Drop.Reagents).ToArray(), out var stackPlan)
            || !ClassicStackRewards.TryStage(session, saved, stackPlan, out stack)) return false;
        var remainingItems = stack.Receipt.Items.ToList();
        var remainingCards = stack.Receipt.Cards.ToList();
        var stackReceipt = stack.Receipt;
        var remainingReagents = stack.Receipt.Reagents.ToDictionary(reagent => reagent.Reagent.m_templateID.Full, reagent => reagent.Acquired);
        var reagentPackets = new HashSet<ulong>();
        foreach (var source in drops) AllocateStack(source);

        void AllocateStack(RewardSource source) {
            foreach (var requested in source.Drop.Items) {
                if (requested is null || !ulong.TryParse(requested.ItemId, out var id)) continue;
                var index = remainingItems.FindIndex(item => item.Item.m_templateID.Full == id);
                if (index < 0) continue;
                var admitted = remainingItems[index]; remainingItems.RemoveAt(index);
                source.Accepted.Items.Add(new DropItemResult { ItemId = requested.ItemId, ItemName = requested.ItemName, Quantity = 1 });
                source.StackMessages.Add(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                    GlobalID = live.GameObjectID, SerializedItem = admitted.Data });
            }
            foreach (var id in source.Drop.TreasureCards) {
                var index = remainingCards.FindIndex(card => card.TemplateId == id);
                if (index < 0) continue;
                var admitted = remainingCards[index]; remainingCards.RemoveAt(index);
                source.Accepted.TreasureCards.Add(id); source.Accepted.TreasureCardSpellIds.Add(admitted.SpellHash);
                source.StackMessages.Add(TreasureMessage(admitted));
            }
            foreach (var requested in source.Drop.Reagents) {
                if (requested is null || requested.Quantity <= 0 || !ulong.TryParse(requested.ItemId, out var id)
                    || !remainingReagents.TryGetValue(id, out var remaining) || remaining <= 0) continue;
                var acquired = Math.Min(requested.Quantity, remaining);
                remainingReagents[id] -= acquired;
                source.Accepted.Reagents.Add(new DropItemResult { ItemId = requested.ItemId, ItemName = requested.ItemName, Quantity = acquired });
                if (!reagentPackets.Add(id)) continue;
                var admitted = stackReceipt.Reagents.Single(reagent => reagent.Reagent.m_templateID.Full == id);
                source.StackMessages.Add(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = live.GameObjectID, Data = admitted.Data });
                source.StackMessages.Add(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                    ItemGlobalID = admitted.Reagent.m_globalID, ItemTemplateID = (uint)admitted.Reagent.m_templateID, ItemLocation = 1 });
            }
        }

        var goalActions = new List<QuestClaimAction>(); var endActions = new List<QuestClaimAction>();
        var learned = new List<uint>(); var cinematicLoot = new LootInfoList { m_loot = [] };
        var dynamodSources = sources.Where(source => source.Result is ResAddDynaMod mod
            && !LegacyDoorBindings.IsAuthoritativeAlias(mod.m_dynaModClientTag)).ToArray();
        if (dynamodSources.Length > 0) {
            var rows = session.Query<DynamodSet>(collectionName: DynamodCollection.CollectionName)
                .Customize(query => query.WaitForNonStaleResults()).Where(set => set.CharId == saved.CharId).Take(2).ToList();
            if (rows.Count > 1 || rows.Count == 1 && rows[0].CharId != saved.CharId) return false;
            dynamods = rows.SingleOrDefault() ?? new DynamodSet(saved.CharId);
            if (rows.Count == 0) {
                session.Store(dynamods);
                session.Advanced.GetMetadataFor(dynamods)[Raven.Client.Constants.Documents.Metadata.Collection] = DynamodCollection.CollectionName;
            }
        }
        var capacityMessageAdded = false;
        foreach (var source in sources) {
            var actions = source.GoalPhase ? goalActions : endActions;
            if (source.Drop is not null) {
                actions.AddRange(source.Numeric);
                actions.AddRange(source.StackMessages.Select(message => new QuestClaimAction(Message: message)));
                if (!capacityMessageAdded && stack.Receipt.BackpackCapacityExceeded) {
                    actions.Add(new(Message: ClassicChat.Line("Your backpack is full, so a reward item could not be added. Make room and try again later.")));
                    capacityMessageAdded = true;
                }
                if (source.Accepted.HasRewards) {
                    var data = SerializeLoot(dependencies, DropTableConverter.ToLootInfoList(source.Accepted), 4);
                    if (data.Length == 0) return false;
                    actions.Add(new(Message: new WIZARD_12_PROTOCOL.MSG_LOOT { GlobalID = live.GameObjectID, LootList = data }));
                }
                if (source.Potion is not null) actions.AddRange(source.Potion.Messages.Select(message => new QuestClaimAction(Message: message)));
            }
            else if (source.Result is ResLearnSpell spellResult) {
                if (spellResult.m_templateID == 0) continue;
                var spell = dependencies.Spell is { } resolve ? resolve(spellResult.m_templateID) : SpellFactory.GetSpell(spellResult.m_templateID);
                if (spell is null) continue; // retain the existing unmanifested-spell exclusion, including TEST1000.
                if (spell.m_templateID != spellResult.m_templateID || saved.SpellbookBehavior?.LearnedSpellTemplateIds is null) return false;
                if (saved.SpellbookBehavior.HasSpell(spellResult.m_templateID)) continue;
                saved.SpellbookBehavior.AddSpellToBook(spell); // no saving Wizard.LearnSpell.
                learned.Add(spellResult.m_templateID); learnedChanged = true;
                actions.Add(new(Message: new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int)spellResult.m_templateID }));
                cinematicLoot.m_loot.Add(new AddSpellLootInfo { m_lootType = LOOT_TYPE.LOOT_TYPE_ADD_SPELL, m_spellID = spellResult.m_templateID });
            }
            else if (source.Result is ResModifyEntry entry) {
                var value = (ulong)entry.m_value; // same existing result interpretation, including zero registry values.
                if (!(entry.m_isQuestRegistry
                    ? saved.QuestBehavior.SetQuestRegistryValue(entry.m_questName, entry.m_entryName, value)
                    : saved.QuestBehavior.SetRegistryValue(entry.m_entryName, value))) return false;
            }
            else if (source.Result is ResAddDynaMod mod) {
                if (LegacyDoorBindings.IsAuthoritativeAlias(mod.m_dynaModClientTag)) continue;
                if (mod.m_dynaModRemove || string.IsNullOrEmpty(mod.m_zoneName) || string.IsNullOrEmpty(mod.m_dynaModClientTag)
                    || string.IsNullOrEmpty(mod.m_dynaModState) || dynamods is null
                    || !dynamods.AddDynamod(new Dynamod { ZoneName = mod.m_zoneName, ClientTag = mod.m_dynaModClientTag, ModState = mod.m_dynaModState })) return false;
                actions.Add(new(Dynamod: mod, StateChange: new ZONE_102_PROTOCOL.MSG_ENTERSTATE {
                    ObjectName = mod.m_dynaModClientTag, StateName = mod.m_dynaModState,
                    ExclusiveToSender = true, Sender = playerRef }, PostEvent: new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                    EventName = $"{mod.m_dynaModClientTag}.{mod.m_dynaModState}.EnterState",
                    PlayerActor = playerRef, PlayerGameObject = playerObj }));
            }
            else actions.Add(new(Transient: source.Result));
        }

        var cinematicMessages = new List<IMessage>();
        foreach (var card in remainingCards) cinematicMessages.Add(TreasureMessage(card));
        foreach (var cards in remainingCards.GroupBy(card => card.TemplateId)) cinematicLoot.m_loot.Add(new TreasureCardLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_TREASURE_CARD, m_spellID = cards.Key, m_numItems = cards.Count() });
        if (cinematicLoot.m_loot.Count > 0) {
            var data = SerializeLoot(dependencies, cinematicLoot, 1); // existing quest cinematic template-ID convention.
            if (data.Length == 0) return false;
            cinematicMessages.Add(new WIZARD_12_PROTOCOL.MSG_QUESTREWARDS { QuestID = quest.ID, LootList = data });
        }
        var badges = ClassicBadges.StageQuestCompleted(saved, Canonical(quest.QuestName));
        if (goldChanged && live.GameStats is null || trainingChanged && live.MagicSchoolBehavior is null
            || learnedChanged && live.SpellbookBehavior is null || stack.Receipt.Items.Count > 0 && live.InventoryBehavior is null
            || stack.Receipt.Cards.Count > 0 && live.SpellbookBehavior is null
            || stack.Receipt.Reagents.Count > 0 && live.AlchemyBehavior is null) return false;
        var finalGoalId = quest.GoalProgress.Single(instance => instance.GoalName == goal.m_goalName).ID;
        var goalCompletion = new List<IMessage> { new QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL { QuestID = quest.ID, GoalID = finalGoalId } };
        var questCompletion = new List<IMessage> { new QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEQUEST { QuestID = quest.ID },
            new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = quest.ID } };
        if (!TryPrepareDialog(goal.m_dialogList, "Completion", "Completion", quest.ID, finalGoalId, dependencies, out var goalDialog)
            || !TryPrepareDialog(template.m_dialogList, "Complete", "QuestComplete", 0, 0, dependencies, out var questDialog)) return false;
        if (goalDialog is not null) goalCompletion.Add(goalDialog);
        if (questDialog is not null) questCompletion.Add(questDialog);
        // StateChange/PostEvent are already prepared internal actor messages, not serialized client packets.
        // Their required DynaMod fields were validated above; every actual client packet still goes through Prepare.
        var native = goalActions.Concat(endActions).Where(action => action.Message is not null).Select(action => action.Message)
            .Concat(cinematicMessages).Concat(badges.Select(badge => (IMessage)badge.Message)).Concat(goalCompletion).Concat(questCompletion);
        if (native.Any(message => !(dependencies.Prepare?.Invoke(message) ?? WizardProgressionTransactions.Prepare(message)))) return false;
        var receipt = new QuestClaimReceipt { CharId = saved.CharId, QuestId = quest.ID,
            GoalId = finalGoalId,
            QuestName = Canonical(quest.QuestName), GoalName = goal.m_goalName, CompletedAtUtc = DateTimeOffset.UtcNow,
            CompletedGoalIds = quest.GoalProgress.Where(instance => instance.IsGoalCompleted()).Select(instance => instance.ID).ToList(),
            Gold = drops.Sum(source => source.Accepted.GoldAmount), Experience = progression?.AppliedXp ?? 0,
            TrainingPoints = drops.Sum(source => source.Accepted.TrainingPoints), PotionSlots = potions.Count, LearnedSpells = learned };
        receipt.Rewards.AddRange(stack.Receipt.Items.Select(item => new QuestClaimReward { Kind = "Item", TemplateId = item.Item.m_templateID.Full, ItemId = item.Item.m_globalID.Full, Count = 1 }));
        receipt.Rewards.AddRange(stack.Receipt.Cards.Select(card => new QuestClaimReward { Kind = "TreasureCard", TemplateId = card.TemplateId, Count = 1 }));
        receipt.Rewards.AddRange(stack.Receipt.Reagents.Select(reagent => new QuestClaimReward { Kind = "Reagent", TemplateId = reagent.Reagent.m_templateID.Full, ItemId = reagent.Reagent.m_globalID.Full, Count = reagent.Acquired }));
        prepared = new(quest, goal, receipt, stack.Receipt, goalActions.ToArray(), endActions.ToArray(), cinematicMessages.ToArray(), badges,
            goalCompletion.ToArray(), questCompletion.ToArray());
        return true;
    }

    private static void RouteStackCategories(DropTableResult reward) {
        var gear = new List<DropItemResult>();
        foreach (var item in reward.Items) {
            if (item is null || !ulong.TryParse(item.ItemId, out var id)) { gear.Add(item); continue; }
            var template = ClassicStackRewards.TestScope.Value?.Template is { } resolve ? resolve(id) : CoreObjectFactory.GetCoreTemplate(id);
            if (template is ReagentItemTemplate) reward.Reagents.Add(item);
            else if (template is SpellTemplate && id <= uint.MaxValue && item.Quantity > 0)
                reward.TreasureCards.AddRange(Enumerable.Repeat((uint)id, item.Quantity));
            else gear.Add(item);
        }
        reward.Items = gear;
    }

    private static WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK TreasureMessage(StackTreasureReward card)
        => new() { SpellID = (int)card.SpellHash, EnchantmentID = 0 };
    private static ByteString SerializeLoot(QuestClaimDependencies dependencies, LootInfoList loot, uint flags)
        => dependencies.SerializeLoot is { } serialize ? serialize(loot, flags)
            : new ObjectSerializer(Versionable: false).Serialize(loot, flags, out var data) ? data : default;

    private static bool TryPrepareDialog(ActorDialogListBase list, string tag, string completion, ulong questId,
        ulong goalId, QuestClaimDependencies dependencies, out IMessage message) {
        message = null;
        if (list is not ActorDialogList dialogs) return true;
        var dialog = dialogs.m_dialogs?.FirstOrDefault(candidate => candidate.m_dialogTag == tag);
        if (dialog is null) return true;
        var clientDialog = ClassicDialogCamera.ForClient(dialog);
        var data = dependencies.SerializeDialog is { } serialize ? serialize(clientDialog, 16)
            : new ObjectSerializer(Versionable: false).Serialize(clientDialog, 16, out var prepared) ? prepared : default;
        if (data.Length == 0) return false;
        message = new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG { MobileID = 0, QuestID = questId, GoalID = goalId,
            CompletionType = completion, ActorDialog = data, Persona = "", PersonaName = "", PersonaIcon = "" };
        return true;
    }
}
