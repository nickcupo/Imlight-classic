/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed partial class TutorialService(SessionActor sessionActor) : MessageService(sessionActor) { // CLASSIC: partial for TutorialService.ClassicStart.cs.

    private const string TUTORIAL_QUEST_NAME = "WC-TUT-C05-001";
    private const string TUTORIAL_INTRO_QUEST_NAME = "Tutorial_Intro";
    private const string TUTORIAL_INTRO_GOAL_NAME = "OnlyGoal";
    private const uint TUTORIAL_NAME_STRING_ID = 600062081;
    private const string TUTORIAL_EXTERIOR_ZONE_NAME_CONTENTS = "Tutorial_Exterior";
    private const string TUTORIAL_INTERIOR_ZONE_NAME_CONTENTS = "Tutorial_Interior";
    private const string TUTORIAL_HEALTH_REFILL_QUEST = "WC-TUT-C09-014";
    private const string TUTORIAL_MANA_REFILL_QUEST = "WC-TUT-C09-016";
    private const ulong AMBROSE_TEMPLATE_ID = 39394;
    private const ulong WALKING_AMBROSE_TEMPLATE_ID = 114120;
    private const double WALKING_AMBROSE_DESPAWN_SECONDS = 7.5;

    private static readonly Dictionary<string, string> s_goalEventPosts = new() {
        ["Despawn Malistaire"] = "DespawnM",
        ["Despawn Ambrose Inside"] = "DespawnAmbrose",
    };
    private static readonly string[] s_controlQuests = [
        "WC-TUT-C05-001", "WC-TUT-C08-001", "WC-TUT-C03-001", "WC-TUT-C03-002",
        "WC-TUT-C09-014", "WC-TUT-C09-016",
    ];
    private const uint INVENTORY_ADD_SERIALIZATION_FLAGS =
        (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit);
    private static readonly CoreObjectSerializer s_configureItemSerializer
        = new(behaviors: SerializerFlags.None);

    private readonly ObjectSerializer _serializer = new(
        Versionable: false,
        Behaviors: SerializerFlags.None
    );
    private readonly TutorialInfo _tutorialInfo = new() {
        m_tutorialNameID = TUTORIAL_NAME_STRING_ID,
        m_tutorialStage = 0,
    };

    private static bool IsTutorialZone(string zoneName)
        => zoneName.Contains(TUTORIAL_EXTERIOR_ZONE_NAME_CONTENTS)
            || zoneName.Contains(TUTORIAL_INTERIOR_ZONE_NAME_CONTENTS);

    internal static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TutorialService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ATTACH))]
    private void ReceivePostAttach(GAME_5_PROTOCOL.MSG_ATTACH msg) {
        if (!_serializer.Serialize(_tutorialInfo,
                                   PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit,
                                   out var tutorialInfoBuffer)) {
            Logger.Error("Failed to serialize tutorial info");

            return;
        }

        if (!IsTutorialZone(msg.ZoneName.ToString())) {
            return;
        }
        var tutorialMsg = new GAME_5_PROTOCOL.MSG_TUTORIALS() {
            GlobalID = 1,
            Remove = 0,
            TutorialInfo = tutorialInfoBuffer
        };
        // Sent twice per retail captures: the client drops the first delivery on some attach paths.
        SendToSocket(tutorialMsg);
        SendToSocket(tutorialMsg);

        // We don't want to give the player the tutorial quest. The client's lua script handles that.
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();
        if (StopUncertainTutorialSession(wizard)) return; // CLASSIC
        try { ReceiveAttachCompleteCore(message); }
        catch { if (!StopUncertainTutorialSession(wizard)) throw; }
    }

    private void ReceiveAttachCompleteCore(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        // The starter kit lands on the first completed attach: in the tutorial zones, or on first
        // login anywhere when the tutorial is disabled. MSG_ATTACH is too early (AttachService
        // registers the wizard while handling it), so the grant cannot run there.
        if (IsTutorialZone(wizard.Zone) || ConfigurationManager.Settings["Character.TutorialDisabled"].AsBool()) {
            GrantStarterKitIfNeeded(wizard);
            if (StopUncertainTutorialSession(wizard)) return; // CLASSIC: never send tutorial success after a refused marker save.
        }

        if (!IsTutorialZone(wizard.Zone)) {
            // CLASSIC: retry a pending saved deck after an earlier intro retirement or partial starter write.
            if (ClassicStart.IsActive && wizard.HasRegistryValue(ClassicStart.StarterKitGivenEntry)
                && !wizard.HasRegistryValue(ClassicStart.CompletedEntry) && !CompleteClassicStart(wizard)) {
                StopUncertainTutorialSession(wizard);
                return;
            }
            if (!GrantClassicEnrollment(wizard)) StopUncertainTutorialSession(wizard);
            return;
        }

        if (!_serializer.Serialize(_tutorialInfo,
                                   PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit,
                                   out var tutorialInfoBuffer)) {
            Logger.Error("Failed to serialize tutorial info (AttachComplete)");

            return;
        }

        var tutorialMsg = new GAME_5_PROTOCOL.MSG_TUTORIALS() {
            GlobalID = 1,
            Remove = 0,
            TutorialInfo = tutorialInfoBuffer
        };
        SendToSocket(tutorialMsg);
        SendToSocket(tutorialMsg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND))]
    private void ReceiveServerTutorialCommand(GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND msg) {
        var wizard = GetActiveWizard();
        if (StopUncertainTutorialSession(wizard)) return; // CLASSIC
        try { ReceiveServerTutorialCommandCore(msg); }
        catch { if (!StopUncertainTutorialSession(wizard)) throw; }
    }

    private void ReceiveServerTutorialCommandCore(GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND msg) {
        // The tutorial is a client-side lua script (Root.wad Scripts/Tutorials) that drives every beat through
        // commands: add/remove quest, complete goal, post event, advance stage. The server executes, never directs.

        var wizard = GetActiveWizard();
        if (wizard is null) {
            Logger.Warning("Dropping tutorial command: wizard is not loaded yet.");

            return;
        }

        // Only allow tutorial commands inside the tutorial zones, and only before the tutorial quest is complete.
        if (!IsTutorialZone(wizard.Zone)) {
            return;
        }
        if (wizard.HasCompletedQuest(TUTORIAL_QUEST_NAME)) {
            return;
        }

        var playerObj = GetActiveGameObject();

        // The fields are not mutually exclusive: the client packs QuestToAdd + GoalToComplete into one message.
        // Add must run first so the goal has an instance to resolve against.
        var commandSuccess = true;

        if (msg.QuestToAdd != string.Empty) {
            commandSuccess = commandSuccess && HandleCommandAddQuest(wizard, msg.QuestToAdd);

            // The C09-014/016 levers are ADD->REMOVE with no goal ever completed, so the health/mana refill
            // happens here on ADD.
            if (commandSuccess && msg.QuestToAdd == TUTORIAL_HEALTH_REFILL_QUEST) {
                RefillHealth(wizard);
            }
            else if (commandSuccess && msg.QuestToAdd == TUTORIAL_MANA_REFILL_QUEST) {
                RefillMana(wizard);
            }
        }
        if (msg.GoalToComplete != string.Empty) {
            commandSuccess = commandSuccess && HandleCommandCompleteGoal(wizard, playerObj, msg.GoalToComplete);
        }
        if (msg.QuestToRemove != string.Empty) {
            commandSuccess = commandSuccess && HandleCommandRemoveQuest(wizard, msg.QuestToRemove);
        }
        if (msg.EventToPost != string.Empty) {
            commandSuccess = commandSuccess && HandleCommandPostEvent(msg.EventToPost);
        }
        if (msg.Action != string.Empty) {
            commandSuccess = commandSuccess && HandleCommandAction(msg.Action, msg.Value);
        }

        StopUncertainTutorialSession(wizard); // CLASSIC: no following native command fields after refusal.
        if (!commandSuccess) {
            Logger.Error("Failed to process tutorial command:"
                + "QuestToAdd='{0}' "
                + "QuestToRemove='{1}' "
                + "GoalToComplete='{2}' "
                + "EventToPost='{3}' "
                + "ActionToPerform='{4}' ",
                Logger.Args(
                    msg.QuestToAdd,
                    msg.QuestToRemove,
                    msg.GoalToComplete,
                    msg.EventToPost,
                    msg.Action
                ));
        }
        else {
            Logger.Debug("Processed tutorial command successfully:"
                + "QuestToAdd='{0}', "
                + "QuestToRemove='{1}' "
                + "GoalToComplete='{2}' "
                + "EventToPost='{3}' "
                + "ActionToPerform='{4}' ",
                Logger.Args(
                    msg.QuestToAdd,
                    msg.QuestToRemove,
                    msg.GoalToComplete,
                    msg.EventToPost,
                    msg.Action
                ));
        }

        return;
    }

    private bool HandleCommandAction(string action, int value) {
        // Stage advances are client-driven; remember the stage so the next MSG_TUTORIALS (sent on zone attach)
        // echoes it back, otherwise the client restarts the tutorial from stage 0 after a zone reload.
        if (action == "Stage") {
            _tutorialInfo.m_tutorialStage = value;
            Logger.Information("Tutorial stage advanced to {0}", Logger.Args(value));

            return true;
        }

        Logger.Debug("Unknown tutorial action '{0}' (value {1}), ignoring.", Logger.Args(action, value));

        return true;
    }

    private static bool HandleCommandAddQuest(Wizard wizard, string questName) {
        // The client re-adds quests it already holds (add/remove flickers); re-adds are idempotent.
        if (wizard.QuestBehavior.CurrentQuestInstances.Any(q => q.QuestName == questName)) {
            return true;
        }

        var questTemplate = QuestTemplateCollection.GetQuestByName(questName);
        if (questTemplate == null) {
            Logger.Error("Tutorial quest template not found for '{0}'", Logger.Args(questName));

            return false;
        }

        var qInstance = new QuestInstance(questTemplate, wizard.CharId);

        return wizard.AddQuest(qInstance);
    }

    private bool HandleCommandRemoveQuest(Wizard wizard, string questName)
        => RemoveQuestAndClearFromJournal(wizard, questName);

    private bool RemoveQuestAndClearFromJournal(Wizard wizard, string questName) {
        if (StopUncertainTutorialSession(wizard)) return false;
        var instance = wizard.QuestBehavior.CurrentQuestInstances.FirstOrDefault(q => q.QuestName == questName);
        if (instance is null) return true; // Existing skip commands remove absent control quests harmlessly.
        var packet = new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = instance.ID };
        if (ClassicQuestEngine.IsActive) {
            var status = WizardQuestTransactions.TryRemove(wizard, instance, out _,
                preparePublication: _ => WizardProgressionTransactions.Prepare(packet),
                afterCommit: _ => SendToSocket(packet));
            return status != QuestMutationStatus.Refused && !StopUncertainTutorialSession(wizard);
        }
        if (!wizard.RemoveQuest(questName)) return false;
        SendToSocket(packet);
        return true;
    }

    private bool HandleCommandCompleteGoal(Wizard wizard,
                                           CoreObject playerObj,
                                           string goalName) {
        // Skip: the client fires this when the player presses the Skip button. Mark the tutorial done, clear
        // the control quests so none leak into the normal world, and move the player to the headmaster's office.
        if (goalName == "SkipTutorialGoal") {
            _tutorialInfo.m_tutorialStage = 99;
            if (!RemoveControlQuests(wizard) || !CompleteTutorialIntro(wizard, playerObj)) return false;
            EquipStarterWandAndDeck(wizard);
            return CompleteStarterExit(() => FinishClassicStart(wizard), () => Teleport(TutorialExitZone())); // CLASSIC
        }

        // Teleport: the finale (stage 8) fires this then blocks on OnTeleported; the server must move the
        // player out of the tutorial interior.
        if (goalName == "Teleport") {
            if (!RemoveControlQuests(wizard)) return false;
            return CompleteStarterExit(() => CompleteClassicStart(wizard), () => Teleport(TutorialExitZone())); // CLASSIC
        }

        // ConfigurePlayer: right after firing this the client blocks on OnItemAddedToInventory waiting for an
        // inventory item. C08-001's goals are not active, so the goal lookup below would never unblock it.
        if (goalName == "ConfigurePlayer") {
            SendConfigurePlayerInventoryAdd(wizard);

            return true;
        }

        // Transplant Player: a cinematic-only beat with no template goal. The client expects the server to
        // reposition the player in-zone to the vantage point facing Ambrose's walk.
        if (goalName == "Transplant Player") {
            InZoneTeleport(wizard, 51.59f, -194.80f, 0.02f, 2.77f);

            return true;
        }

        // Walk Ambrose: despawn the stationary Ambrose now so he and the walking one never overlap, then fall
        // through to the normal goal path that posts WalkAmbrose (the client spawns the walking Ambrose).
        if (goalName == "Walk Ambrose") {
            DespawnTutorialObject(AMBROSE_TEMPLATE_ID, 0);
            DespawnTutorialObject(WALKING_AMBROSE_TEMPLATE_ID, WALKING_AMBROSE_DESPAWN_SECONDS);
        }

        // Trigger Wand Effect: the wand glare beat. The starter wand and deck were granted at character
        // creation, so only the glare event is needed.
        if (goalName == "Trigger Wand Effect") {
            HandleCommandPostEvent("WandFX");

            return true;
        }

        // End-of-tutorial trigger beats: post the zone event the interior trigger listens for.
        if (s_goalEventPosts.TryGetValue(goalName, out var eventName)) {
            HandleCommandPostEvent(eventName);

            return true;
        }

        // We need to find the quest that contains this goal.
        // We can search the player for quest/goal instances, as they do carry a name.
        var allWizardQuestInstances = wizard.QuestBehavior.CurrentQuestInstances;
        var goalInstance = allWizardQuestInstances
            .SelectMany(q => q.GoalProgress, (quest, goal) => new { quest, goal })
            .FirstOrDefault(x => x.goal.GoalName == goalName);

        if (goalInstance != null) {
            // We can remove it from the Wizard, but we need to post the completion events as well.
            var status = CompleteAcknowledgedTutorialGoal(wizard, goalInstance.quest, goalInstance.goal);
            if (status == QuestMutationStatus.Refused) return false;
            if (status == QuestMutationStatus.Unchanged) return true; // CLASSIC: do not repeat pip/result/equipment effects.

            // We need the actual goal instance template to get the completion results.
            var questTemplate = QuestTemplateCollection.GetQuestByName(goalInstance.quest.QuestName);
            if (questTemplate == null) {
                Logger.Error("Tutorial quest template not found");

                return false;
            }

            // Find the goal template within the quest template.
            var goalTemplate = questTemplate.m_goals
                .FirstOrDefault(g => g.m_goalName == goalName);
            if (goalTemplate == null) {
                Logger.Error("Tutorial goal template not found");

                return false;
            }

            // The pip goals carry their count in the goal's tally counter: the pip result types have no
            // fields, so the template's m_tallyCounter.m_count is the data source.
            if (goalName.Equals("Give 3 pips to player", StringComparison.OrdinalIgnoreCase)
                || goalName.Equals("give 4 pips to player", StringComparison.OrdinalIgnoreCase)) {
                var pipCount = goalTemplate.m_tallyCounter?.m_count ?? 0;
                if (pipCount > 0) {
                    SessionActor.ActorRef.Tell(new TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS { Count = pipCount });
                }
            }

            ResultDispatcher.ExecuteResults(
                actorContext: Context,
                results: goalTemplate.m_completeResults,
                playerRef: SessionActor.ActorRef,
                playerObj: playerObj,
                questName: goalInstance.quest.QuestName,
                goalName: goalInstance.goal.GoalName,
                zoneActor: SessionActor.GetZoneActor()
            );

            // The intro quest is the shared finale for the tutorial and the skip flow;
            // the starter wand and deck must be equipped in both.
            if (goalInstance.quest.QuestName == TUTORIAL_INTRO_QUEST_NAME) {
                EquipStarterWandAndDeck(wizard);
            }

            return !StopUncertainTutorialSession(wizard);
        }

        // Client-only cinematic beats (e.g. "StopRain") have no template goal; acknowledge them as no-op successes.
        Logger.Debug("Tutorial goal '{0}' not found on any active quest (client-only beat, ignoring).",
            Logger.Args(goalName));

        return true;
    }

    private bool HandleCommandPostEvent(string eventName) {
        ZoneBroadcastNoPlayers(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = eventName,
            PlayerActor = SessionActor.ActorRef
        });

        return true;
    }

    private void RefillHealth(Wizard wizard) {
        var full = wizard.GameStats.m_baseHitpoints;
        var clientMax = wizard.GameStats.GetClientTypeAlternative().m_baseHitpoints;
        wizard.UpdateHealth(full);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = wizard.GameObjectID,
            NewHealth = full,
            NewHealthMax = clientMax,
        });
    }

    private void RefillMana(Wizard wizard) {
        var full = wizard.GameStats.m_baseMana;
        var clientMax = wizard.GameStats.GetClientTypeAlternative().m_baseMana;
        wizard.UpdateMana(full);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
            Mana = full,
            MaxMana = clientMax,
        });
    }

    private bool RemoveControlQuests(Wizard wizard) {
        foreach (var questName in s_controlQuests) {
            if (!RemoveQuestAndClearFromJournal(wizard, questName)) return false;
        }
        return true;
    }

    private bool CompleteTutorialIntro(Wizard wizard, CoreObject playerObj) {
        if (StopUncertainTutorialSession(wizard)) return false;
        // CLASSIC: a separately failed saved-deck fill must be recoverable after intro retirement.
        // Never re-add a completed intro or replay its already completed goal results.
        if (wizard.HasCompletedQuest(TUTORIAL_INTRO_QUEST_NAME)) return true;
        // Skippers never run the client's end-of-tutorial flow, so complete Tutorial_Intro here:
        // OnlyGoal's results are the school spell (ResLearnSpell, school-gated) plus the refills.
        if (!wizard.QuestBehavior.CurrentQuestInstances.Any(q => q.QuestName == TUTORIAL_INTRO_QUEST_NAME)) {
            var introTemplate = QuestTemplateCollection.GetQuestByName(TUTORIAL_INTRO_QUEST_NAME);
            if (introTemplate is null) {
                Logger.Error("Tutorial intro quest template not found for '{0}'", Logger.Args(TUTORIAL_INTRO_QUEST_NAME));

                return false;
            }

            if (!wizard.AddQuest(new QuestInstance(introTemplate, wizard.CharId))) return false;
        }

        var instance = wizard.QuestBehavior.CurrentQuestInstances
            .FirstOrDefault(q => q.QuestName == TUTORIAL_INTRO_QUEST_NAME);
        var goalInstance = instance?.GoalProgress.FirstOrDefault(g => g.GoalName == TUTORIAL_INTRO_GOAL_NAME);
        if (goalInstance is null) {
            return false;
        }

        var status = CompleteAcknowledgedTutorialGoal(wizard, instance, goalInstance);
        if (status == QuestMutationStatus.Refused) return false;
        if (status == QuestMutationStatus.Unchanged) return true;

        var questTemplate = QuestTemplateCollection.GetQuestByName(TUTORIAL_INTRO_QUEST_NAME);
        var goalTemplate = questTemplate?.m_goals.FirstOrDefault(g => g.m_goalName == TUTORIAL_INTRO_GOAL_NAME);
        if (goalTemplate is null) {
            return false;
        }

        ResultDispatcher.ExecuteResults(
            actorContext: Context,
            results: goalTemplate.m_completeResults,
            playerRef: SessionActor.ActorRef,
            playerObj: playerObj,
            questName: TUTORIAL_INTRO_QUEST_NAME,
            goalName: TUTORIAL_INTRO_GOAL_NAME,
            zoneActor: SessionActor.GetZoneActor()
        );

        return !StopUncertainTutorialSession(wizard);
    }

    // CLASSIC: script beats retain their identities; only saving/no-replay/unknown-outcome handling changes.
    private bool StopUncertainTutorialSession(Wizard wizard) {
        if (!WizardCollection.IsInventorySnapshotUncertain(wizard)) return false;
        CloseSession();
        return true;
    }

    private QuestMutationStatus CompleteAcknowledgedTutorialGoal(Wizard wizard, QuestInstance quest, GoalInstance goal) {
        if (StopUncertainTutorialSession(wizard)) return QuestMutationStatus.Refused;
        if (ClassicQuestEngine.IsActive)
            return WizardQuestTransactions.TryCompleteGoal(wizard, quest, goal, out _);
        return wizard.CompleteQuestGoal(quest.QuestName, goal.GoalName)
            ? QuestMutationStatus.Committed : QuestMutationStatus.Refused;
    }

    private void EquipStarterWandAndDeck(Wizard wizard) {
        // The starter kit is config-driven, so resolve the wand and deck by slot rather
        // than template ID; equip only the first instance of each.
        EquipFirstStarterItem(wizard, EquipmentSlotType.Weapon);
        EquipFirstStarterItem(wizard, EquipmentSlotType.Deck);
    }

    private void EquipFirstStarterItem(Wizard wizard, EquipmentSlotType slotType) {
        var item = wizard.InventoryBehavior.Items.FirstOrDefault(item => {
            var template = ItemHelper.GetItemTemplate(item);
            if (template is null) {
                return false;
            }

            return ItemHelper.GetItemSlot(template)?.SlotType == slotType;
        });
        if (item is null || wizard.EquipmentBehavior.HasItemEquipped(item.m_globalID)) {
            return;
        }

        // Move the item into its slot; this also informs the spellbook for decks and
        // persists the change.
        if (!wizard.InventoryToEquipmentTransfer(item.m_globalID, out var equipEffects, out _)) {
            Logger.Warning("Starter equip failed for item {0} on {1}.",
                Logger.Args(item.m_globalID.Full, wizard.PlayerNameBehavior.GetWizardName()));

            return;
        }

        // The client never asked for this equip, so confirm it explicitly.
        SendEquipItem(item, slotType.ToString());
        SendAddEffects(equipEffects);
    }

    private void SendEquipItem(WizClientObjectItem item, string slotName) {
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = item.m_globalID,
            SlotName = slotName,
            IsEquip = 1,
        });

        var pubItem = ItemHelper.GetPublicItem(item);
        if (!s_configureItemSerializer.Serialize(pubItem, 1, out var serializedPubItem)) {
            Logger.Error("Failed to serialize starter item {0} for equip broadcast.",
                Logger.Args(item.m_globalID.Full));

            return;
        }

        var gameObject = GetActiveGameObject();
        if (gameObject is not null) {
            ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICEQUIPITEM {
                GlobalID = gameObject.m_globalID,
                SerializedInfo = serializedPubItem,
            }, false);
        }
    }

    private void SendAddEffects(List<GameEffectBase> effects) {
        if (effects is null || effects.Count == 0) {
            return;
        }

        var gameObjectId = GetActiveGameObject()?.m_globalID ?? 0;
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        foreach (var effect in effects) {
            if (!s_configureItemSerializer.Serialize(effect, flags, out var serializedEffect)) {
                Logger.Error("Failed to serialize starter item effect {0}.",
                    Logger.Args(effect.m_effectNameID));

                continue;
            }

            SendToSocket(new GAME_5_PROTOCOL.MSG_ADDEFFECT {
                GameObjectID = gameObjectId,
                EffectData = serializedEffect,
            });
        }
    }

    private void InZoneTeleport(Wizard wizard, float x, float y, float z, float yaw) {
        var teleport = new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
            Direction = (byte) Math.Round(yaw / Math.PI / 2 * 250),
            LocationX = (ushort) (short) Math.Round(x / 4),
            LocationY = (ushort) (short) Math.Round(y / 4),
            LocationZ = (ushort) (short) Math.Round(z / 4),
            MobileID = wizard.GameObject.m_nMobileID,
        };
        TellOtherServices(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Sender = SessionActor.ActorRef,
            Message = teleport,
            Selfless = false,
        });
    }

    private void DespawnTutorialObject(ulong templateId, double delaySeconds) {
        var zoneActor = SessionActor.GetZoneActor();
        if (zoneActor is null) {
            Logger.Warning("Cannot despawn tutorial object {0}: no zone actor.", Logger.Args(templateId));

            return;
        }

        var despawnMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_REMOVEOBJECT {
                TemplateID = templateId,
            }],
            Targets = ZoneBroadcastTarget.Objects | ZoneBroadcastTarget.Paths,
        };

        if (delaySeconds <= 0) {
            zoneActor.Tell(despawnMsg, SessionActor.ActorRef);
        }
        else {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(delaySeconds), zoneActor, despawnMsg,
                SessionActor.ActorRef);
        }
    }

    private void GrantStarterKitIfNeeded(Wizard wizard) {
        if (!NeedsStarterKit(wizard, ClassicStart.IsActive)) {
            return;
        }

        var templateIds = ClassicStart.IsActive // CLASSIC: the classic kit (the school's own wand) replaces Character.DefaultItems.
            ? ClassicStart.StarterItemTemplateIds(wizard.MagicSchoolBehavior.MagicSchool).ToArray()
            : ConfigurationManager.Settings["Character.DefaultItems"].AsList()
                .Select(id => ulong.TryParse(id, out var parsed) ? parsed : 0)
                .Where(id => id > 0)
                .ToArray();
        if (templateIds.Length == 0) {
            return;
        }

        var grantedItems = wizard.GrantStarterItems(templateIds);
        if (StopUncertainTutorialSession(wizard)) return;
        if (ClassicStart.IsActive && !wizard.SetRegistryValue(ClassicStart.StarterKitGivenEntry, 1)) return; // CLASSIC
        Logger.Information("Granted starter kit ({0} items: {1}) to {2} ({3}).",
            Logger.Args(grantedItems.Count, string.Join(",", templateIds), wizard.PlayerNameBehavior.GetWizardName(),
                wizard.MagicSchoolBehavior.MagicSchool));

        // The attach payload (which carries the inventory) was already sent by the time this
        // runs, so push each item to the client explicitly or the kit stays invisible this session.
        foreach (var item in grantedItems) {
            SendInventoryAdd(wizard, item);
        }

        // CLASSIC: without the tutorial there is no finale to give the school spell and equip the kit.
        if (!IsTutorialZone(wizard.Zone)) {
            CompleteClassicStart(wizard);
        }
    }

    /// <summary>
    /// True when <paramref name="wizard"/> has never had the starter kit: nothing in the backpack or worn and,
    /// under the classic start, no mark that the kit was given. The classic start equips the kit's wand and
    /// deck, which empties the backpack, so an empty backpack alone gave the kit again on the next zone.
    /// </summary>
    internal static bool NeedsStarterKit(Wizard wizard, bool classicStart)
        => wizard is not null
            && wizard.InventoryBehavior.Items.Count == 0
            && wizard.EquipmentBehavior.EquippedItems.Count == 0
            && !(classicStart && (wizard.HasRegistryValue(ClassicStart.StarterKitGivenEntry) // CLASSIC
                                  || wizard.HasRegistryValue(ClassicStart.CompletedEntry)));

    private void SendInventoryAdd(Wizard wizard, WizClientObjectItem item) {
        if (!s_configureItemSerializer.Serialize(item, INVENTORY_ADD_SERIALIZATION_FLAGS, out var serializedItem)) {
            Logger.Error("Failed to serialize starter kit item {0} for inventory-add.",
                Logger.Args(item.m_globalID.Full));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = serializedItem,
        });
    }

    private void SendConfigurePlayerInventoryAdd(Wizard wizard) {
        var kitItem = wizard.InventoryBehavior.Items.FirstOrDefault();
        if (kitItem is null) {
            Logger.Warning("ConfigurePlayer: no inventory item to re-add; the client may stay blocked.");

            return;
        }

        if (!s_configureItemSerializer.Serialize(kitItem, INVENTORY_ADD_SERIALIZATION_FLAGS, out var serializedItem)) {
            Logger.Error("ConfigurePlayer: failed to serialize item {0} for inventory-add.",
                Logger.Args(kitItem.m_globalID.Full));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = serializedItem,
        });
    }

}
