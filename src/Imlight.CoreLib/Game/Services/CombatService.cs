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
 *
 * ========================================================================
 * COMBAT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player combat interactions, including duel mechanics, 
 * mount equipment, and post-combat state management.
 * 
 * USAGE EXAMPLE:
 * Internal service handling combat-related messages and player 
 * state transitions during battles.
 * 
 * NOTE:
 * - Handles duel entry, combat moves, and victory/defeat scenarios
 * - Manages mount equipment and unequipment during combat
 * - Implements no-aggro grace period after combat
 * 
 * TODO:
 * - Improve error handling for equipment transfers
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/19/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.MinionHelper;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Services;

internal class CombatService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const uint NO_AGGRO_EFFECT_STRINGID = 1618528611;
    private const uint NO_AGGRO_EFFECT_DURATION_IN_SECONDS = 3;

    private readonly CoreObjectSerializer _effectSerializer = new(
        behaviors: SerializerFlags.None
    );
    private readonly CoreObjectSerializer _itemSerializer = new(
        behaviors: SerializerFlags.None
    );

    private IActorRef _currentDuelActor;
    private bool _cheatInstantCinematics;
    private bool _cheatNoFizzle;
    private ulong _cachedMountId;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CombatService(parentActor));

    protected override void OnDispose() {
        var message = new GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT();
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
        MinionHelperHub.Shared.UnbindSession(_helperAccountId, SessionActor.ActorRef); // CLASSIC: Minion Helper.
    }

    // CLASSIC: the Crown Shop's henchman hire goes to the duel this player is in; outside a duel it fails at once.
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN))]
    private void ReceiveHireHenchman(COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN message) {
        if (_currentDuelActor is null) {
            SessionActor.ActorRef.Tell(new COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED { CreatureTid = message.CreatureTid, Success = false });

            return;
        }

        message.Actor = SessionActor.ActorRef;
        _currentDuelActor.Tell(message, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL))]
    private void RecieveDuelAdd(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL message) {
        _currentDuelActor = message.DuelActor;
        TellDuelAboutHelper(); // CLASSIC: a Myth owner with a Minion Helper chooses their minions' moves.

        if (_cheatInstantCinematics) {
            _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATINSTANTCINEMATICS {
                Enabled = true
            });
        }

        if (_cheatNoFizzle) {
            _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATNOFIZZLE {
                Enabled = true,
                Actor = SessionActor.ActorRef
            });
        }

        // Unequip mounts if the player has one equipped
        UnEquipMount();

        // Set the persistent location and orientation of the wizard
        var wizard = GetActiveWizard();
        wizard.IsInDuel = true;
        wizard.SetPersistentLocation(message.SlotPosition);

        // Orientation is given in radians. It must be converted to degrees and then to a byte.
        var orientationRadians = message.SlotOrientation;
        var orientationDegrees = (float) (orientationRadians * (180 / Math.PI));
        var orientation = (byte) (orientationDegrees / 360 * 256);
        wizard.SetPersistentOrientation(orientation);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT))]
    private void ReceiveCombatDefeat(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT message) {
        _currentDuelActor = null; // CLASSIC: the duel is over for us; a later logout must not flee it again.
        GetActiveWizard().IsInDuel = false;
        PushHelperIdle();
        EquipMountSubtle();

        // CLASSIC: a defeated wizard comes back with 1 health, which the hub's zone healing (m_healingPerMinute, 20%
        // a minute in the world hubs) refills; at 0 the next fight was lost at once. A flee keeps its health.
        var wizard = GetActiveWizard();
        if (wizard.GameStats.m_currentHitpoints <= 0) {
            wizard.UpdateHealth(1);
            Logger.Debug("{Wizard} was defeated; back with 1 health.", Logger.Args(wizard.CharId));
        }

        // We've fled or have been defeated in this duel. Send us back to the world hub.
        var hubMsg = new ZONE_102_PROTOCOL.MSG_SENDTOHUB();
        TellOtherServices(hubMsg);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatVictory(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        // CLASSIC: the duel is over; a logout after it used to reach the ended duel, which ran flee and defeat on it
        // (a second "Duel ended" and a MSG_SENDTOHUB).
        _currentDuelActor = null;
        GetActiveWizard().IsInDuel = false;
        PushHelperIdle();
        EquipMount();
        SetNoAggroGrace();

        // Send a message to ourselves to end the no aggro grace period.
        var noAggroGraceOverMsg = new COMBAT_106_PROTOCOL.MSG_NOAGGROGRACEOVER();
        Timers.StartSingleTimer(
            "NoAggroGraceOver",
            noAggroGraceOverMsg,
            TimeSpan.FromSeconds(NO_AGGRO_EFFECT_DURATION_IN_SECONDS));

        ClassicBadges.MobsDefeated(GetActiveWizard(), message.MobTemplateIds, SendToSocket); // CLASSIC: kill badges.

        // CLASSIC: under the profile's mob reward rules, XP per pip, gold and drops come from classic-data
        // and show in one loot popup; SpiralDB mob loot (none for Arc 1) still rolls after it.
        if (ClassicProgression.MobRewards is { } classicRewards) {
            var classicGold = GrantClassicCombatRewards(classicRewards, message.UsedPips, message.MobTemplateIds);
            var lootGold = GrantMobLoot(message.MobTemplateIds);
            GrantMobCrowns(classicGold + lootGold);

            return;
        }

        // Gain 3 XP per pip used in the duel.
        var usedPips = message.UsedPips;
        var xpGained = usedPips * 3;

        var msg = new CHARACTER_103_PROTOCOL.MSG_GAINXP {
            XP = xpGained,
        };
        TellOtherServices(msg);

        GrantMobCrowns(GrantMobLoot(message.MobTemplateIds));
    }

    // CLASSIC: on this server a defeated mob pays as many Crowns as gold (owner decision 2026-09-28).
    private void GrantMobCrowns(int gold) {
        if (!ClassicCrowns.CrownsFromMobs || gold <= 0 || GetActiveWizard() is not { } wizard) {
            return;
        }

        ClassicCrowns.Add(wizard.Account, gold);
        SendToSocket(ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId));
    }

    // CLASSIC: XP for the pips this player used, then per defeated mob gold, items, Treasure Cards and reagents
    // (each list entry rolls on its own, capped per mob; see MobRewardRules.Roll).
    private int GrantClassicCombatRewards(MobRewardRules rules, int usedPips, ulong[] defeatedMobTemplateIds) {
        var result = new DropTableResult {
            DropTableId = "classic_mob_rewards",
            ExperienceAmount = rules.CombatXp.Xp(usedPips),
        };

        var random = Random.Shared;
        foreach (var templateId in defeatedMobTemplateIds ?? []) {
            if (ClassicMobInfo.Of(templateId) is not { } mob) {
                continue;
            }

            var loot = rules.Roll(mob, random);
            result.GoldAmount += loot.Gold;
            foreach (var item in loot.Items) {
                if (CoreObjectFactory.GetCoreTemplate(item) is not null) {
                    result.Items.Add(new DropItemResult { ItemId = item.ToString(), ItemName = string.Empty, Quantity = 1 });
                }
            }

            foreach (var card in loot.TreasureCards) {
                if (card <= uint.MaxValue && CoreObjectFactory.GetCoreTemplate(card) is SpellTemplate) {
                    result.TreasureCards.Add((uint) card);
                }
            }

            foreach (var reagent in loot.Reagents) {
                if (CoreObjectFactory.GetCoreTemplate(reagent.Template) is ReagentItemTemplate) {
                    result.Reagents.Add(new DropItemResult {
                        ItemId = reagent.Template.ToString(), ItemName = string.Empty, Quantity = reagent.Quantity,
                    });
                }
            }
        }

        if (!result.HasRewards) {
            return 0;
        }

        LootGranter.GrantAndDisplay(SessionActor.ActorRef, GetActiveWizard(), result);

        return result.GoldAmount;
    }

    // Returns the gold granted.
    private int GrantMobLoot(ulong[] defeatedMobTemplateIds) {
        // Rolls and grants drop-table loot for the defeated mobs (raw template ids). Each mob's tables
        // come from NpcDropTableCollection; a single roll across all of a mob's tables yields one combined
        // loot popup for that mob.
        if (defeatedMobTemplateIds is not { Length: > 0 }) {
            return 0;
        }

        var gold = 0;
        var wizard = GetActiveWizard();
        var playerRef = SessionActor.ActorRef;
        var playerObj = GetActiveGameObject();

        foreach (var templateId in defeatedMobTemplateIds) {
            if (!NpcDropTableCollection.TryGetDropTable(templateId, out var npcDropTable)) {
                continue;
            }

            var loot = DropTableRoller.Roll(npcDropTable.DropTableNames.ToArray(), playerRef, playerObj, wizard);
            LootGranter.GrantAndDisplay(playerRef, wizard, loot);
            gold += loot.GoldAmount;
        }

        return gold;
    }

    [MessageHandler(typeof(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATDRAW))]
    private void ReceiveCombatDraw(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATDRAW message) {
        if (_currentDuelActor == null) {
            return;
        }

        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATDRAW {
            Actor = SessionActor.ActorRef
        };
        _currentDuelActor.Tell(msg);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATINSTAWIN))]
    private void ReceiveCheatInstaWin(COMBAT_106_PROTOCOL.MSG_CHEATINSTAWIN message) {
        if (_currentDuelActor is null) {
            InformGameClient("You are not in a duel.");

            return;
        }

        _currentDuelActor.Tell(message);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATTOGGLECINEMATICS))]
    private void ReceiveCheatToggleCinematics(COMBAT_106_PROTOCOL.MSG_CHEATTOGGLECINEMATICS message) {
        _cheatInstantCinematics = !_cheatInstantCinematics;

        _currentDuelActor?.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATINSTANTCINEMATICS {
            Enabled = _cheatInstantCinematics
        });

        InformGameClient(_cheatInstantCinematics
            ? "Instant spell cinematics enabled for your duels."
            : "Instant spell cinematics disabled for your duels.");
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATTOGGLENOFIZZLE))]
    private void ReceiveCheatToggleNoFizzle(COMBAT_106_PROTOCOL.MSG_CHEATTOGGLENOFIZZLE message) {
        _cheatNoFizzle = !_cheatNoFizzle;

        _currentDuelActor?.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATNOFIZZLE {
            Enabled = _cheatNoFizzle,
            Actor = SessionActor.ActorRef
        });

        InformGameClient(_cheatNoFizzle
            ? "Spell fizzling disabled for your duels."
            : "Spell fizzling enabled for your duels.");
    }

    private bool _minionControlNegotiated;

    [MessageHandler(typeof(EnhancedClassicProtocol.Hello))]
    private void ReceiveEnhancementHello(EnhancedClassicProtocol.Hello message) {
        // Monstrology is stock by default; strict/unsupported Hello withdraws its session permission.
        Imlight.CoreLib.Game.Monstrology.MonstrologySessionPolicy.Bind(GetActiveWizard(), SessionActor.MonstrologySession);
        // Extensions are silent until the client opts in. Unsupported versions never unlock control.
        _minionControlNegotiated = message.ProtocolVersion == EnhancedClassicProtocol.Version
            && !message.StrictClassic && EnhancedGameplaySettings.Enabled;
        if (!_minionControlNegotiated) _currentDuelActor?.Tell(new COMBAT_106_PROTOCOL.MSG_OWNEDMINIONDISABLE {
            OwnerActor = SessionActor.ActorRef
        }, Self);
        var wizard = GetActiveWizard();
        var myth = wizard?.MagicSchoolBehavior?.MagicSchool == MagicSchool.Myth;
        SendToSocket(new EnhancedClassicProtocol.Capabilities {
            Flags = (_minionControlNegotiated && myth ? 1u : 0u)
                | SessionActor.MonstrologySession.Advertise(Imlight.CoreLib.Game.Monstrology.MonstrologyService.Enabled)
        });
    }

    [MessageHandler(typeof(EnhancedClassicProtocol.MinionRequest))]
    private void ReceiveOwnedMinionRequest(EnhancedClassicProtocol.MinionRequest message) {
        if (!_minionControlNegotiated || !EnhancedGameplaySettings.Enabled || _currentDuelActor == null) {
            if (_minionControlNegotiated) SendToSocket(new EnhancedClassicProtocol.MinionState {
                Payload = System.Text.Json.JsonSerializer.Serialize(new {
                    message.DuelID, message.Round, message.MinionID, message.RequestID,
                    Accepted = false, Status = "Unavailable", Snapshots = Array.Empty<object>()
                })
            });
            return;
        }
        _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_OWNEDMINIONREQUEST {
            OwnerActor = SessionActor.ActorRef,
            DuelID = message.DuelID, Round = message.Round, MinionID = message.MinionID,
            RequestID = message.RequestID, Query = message.Query, MoveType = message.MoveType,
            SpellSelection = message.SpellSelection, SpellTarget = message.SpellTarget
        }, Self);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_OWNEDMINIONRESPONSE))]
    private void ReceiveOwnedMinionResponse(COMBAT_106_PROTOCOL.MSG_OWNEDMINIONRESPONSE message) {
        if (message.OwnerActor != SessionActor.ActorRef) return;
        if (message.HelperView is not null && _helperAccountId != 0) {
            MinionHelperHub.Shared.Push(_helperAccountId, message.HelperView); // CLASSIC: Minion Helper.
        }
        if (!_minionControlNegotiated || !EnhancedGameplaySettings.Enabled) return;
        var payload = System.Text.Json.JsonSerializer.Serialize(new {
                message.DuelID, message.Round, message.MinionID, message.RequestID,
                message.Accepted, Status = message.Status.ToString(),
                Snapshots = System.Linq.Enumerable.Select(message.Snapshots, snapshot => new {
                    snapshot.OwnerID, snapshot.MinionID, snapshot.Slot, snapshot.Team, snapshot.Health,
                    snapshot.GenericPips, snapshot.PowerPips, snapshot.HandData, snapshot.ParticipantData,
                    snapshot.HasOrder, snapshot.MoveType, snapshot.SpellSelection, snapshot.SpellTarget
                })
            });
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > EnhancedClassicProtocol.MaximumPayloadBytes) {
            payload = System.Text.Json.JsonSerializer.Serialize(new {
                message.DuelID, message.Round, message.MinionID, message.RequestID,
                Accepted = false, Status = "SnapshotUnavailable", Snapshots = Array.Empty<object>()
            });
        }
        SendToSocket(new EnhancedClassicProtocol.MinionState { Payload = payload });
    }

    // CLASSIC: the Minion Helper (Classic/MinionHelper). The account's helper links to this session while the wizard
    // is in the world; while it is linked and its switch is on, a Myth wizard chooses their minions' moves in duels.
    private ulong _helperAccountId;
    private bool _helperLinked;
    private bool _helperControl = true;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachCompleteForHelper(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var account = GetActiveAccount();
        if (account is null || account.AccountId == 0) return;
        _helperAccountId = account.AccountId;
        MinionHelperHub.Shared.BindSession(_helperAccountId, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_MINIONHELPERLINK))]
    private void ReceiveMinionHelperLink(COMBAT_106_PROTOCOL.MSG_MINIONHELPERLINK message) {
        _helperLinked = message.Connected && EnhancedGameplaySettings.Enabled;
        if (_helperLinked) {
            if (_currentDuelActor is null) PushHelperIdle();
            else TellDuelAboutHelper();
        } else if (_currentDuelActor is not null && !_minionControlNegotiated) {
            _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_OWNEDMINIONDISABLE { OwnerActor = SessionActor.ActorRef }, Self);
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_MINIONHELPERCONTROL))]
    private void ReceiveMinionHelperControl(COMBAT_106_PROTOCOL.MSG_MINIONHELPERCONTROL message) {
        _helperControl = message.Enabled;
        if (_currentDuelActor is null) {
            PushHelperIdle();
        } else if (_helperControl) {
            TellDuelAboutHelper();
        } else {
            _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_OWNEDMINIONDISABLE { OwnerActor = SessionActor.ActorRef }, Self);
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_MINIONHELPERORDER))]
    private void ReceiveMinionHelperOrder(COMBAT_106_PROTOCOL.MSG_MINIONHELPERORDER message) {
        if (!_helperLinked || !EnhancedGameplaySettings.Enabled || _currentDuelActor is null) {
            if (message.RequestID != 0) {
                MinionHelperHub.Shared.Push(_helperAccountId, System.Text.Json.JsonSerializer.Serialize(new {
                    op = "ack", request = message.RequestID, accepted = false, status = "NotInDuel" }));
            }
            PushHelperIdle();
            return;
        }

        if (message.Query) {
            TellDuelAboutHelper();
            return;
        }

        if (!_helperControl) {
            MinionHelperHub.Shared.Push(_helperAccountId, System.Text.Json.JsonSerializer.Serialize(new {
                op = "ack", request = message.RequestID, accepted = false, status = "ControlOff" }));
            return;
        }

        _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_OWNEDMINIONREQUEST {
            OwnerActor = SessionActor.ActorRef,
            DuelID = message.DuelID, Round = message.Round, MinionID = message.MinionID,
            RequestID = message.RequestID, Query = false, MoveType = message.MoveType,
            SpellSelection = message.SpellSelection, SpellTarget = message.SpellTarget
        }, Self);
    }

    private void TellDuelAboutHelper() {
        if (!_helperLinked || _currentDuelActor is null || !EnhancedGameplaySettings.Enabled) return;
        _currentDuelActor.Tell(new COMBAT_106_PROTOCOL.MSG_OWNEDMINIONOPTIN {
            OwnerActor = SessionActor.ActorRef, Enable = _helperControl
        }, Self);
    }

    private void PushHelperIdle() {
        if (!_helperLinked || _helperAccountId == 0) return;
        var wizard = GetActiveWizard();
        MinionHelperHub.Shared.Push(_helperAccountId, System.Text.Json.JsonSerializer.Serialize(new {
            op = "state", phase = "idle",
            wizard = wizard?.PlayerNameBehavior?.GetWizardName() ?? "",
            myth = wizard?.MagicSchoolBehavior?.MagicSchool == MagicSchool.Myth,
            controlling = _helperControl && EnhancedGameplaySettings.Enabled,
        }));
    }

    [MessageHandler(typeof(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE))]
    private void ReceiveCombatMove(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE message) {
        if (_currentDuelActor == null) {
            throw new Exception("Combat move received without a duel actor.");
        }

        _currentDuelActor.Tell(TranslateCombatMove(message, SessionActor.ActorRef));
    }

    internal static COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE TranslateCombatMove(
        DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE message, IActorRef actor) {
        // The native enchant extension carries a raw hand index, not a combatant bitmask.
        var target = message.MoveType == ClassicHandEnchantment.MoveType
            ? message.SpellTarget
            : unchecked((uint) (int) Math.Log(message.SpellTarget, 2));
        return new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
            Actor = actor,
            MoveType = message.MoveType,
            SpellSelection = message.SpellSelection,
            SpellTarget = target,
            TimeLeft = message.TimeLeft,
            RawSpellTarget = message.SpellTarget,
        };
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_NOAGGROGRACEOVER))]
    private void ReceiveNoAggroGraceOver()
        => RemoveNoAggroEffect();

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message) {
        if (_currentDuelActor != null) {
            GetActiveWizard().IsInDuel = false;
        }
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) {
        if (_currentDuelActor != null) {
            GetActiveWizard().IsInDuel = false;
        }
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND))]
    private void ReceiveTutorialRebuildDuelHand(TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND message) {
        // Sender is forced to SessionActor.ActorRef so the duel matches it to this player's sub-circle; null out
        // of combat is a harmless no-op.
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS))]
    private void ReceiveTutorialGrantPips(TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS message) {
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
    }

    private void EquipMount() {
        if (_cachedMountId == 0) {
            // We don't have a cached mount.
            return;
        }

        var wizard = GetActiveWizard();
        var wizEquipmentBehavior = wizard.EquipmentBehavior;
        var slot = wizEquipmentBehavior.GetSlotOfItem(_cachedMountId);

        // We can discard the removed effects because we know for sure they are not present.
        if (!wizard.InventoryToEquipmentTransfer(_cachedMountId, out var addedEffects, out var _)) {
            // If this fails, there is perhaps desync between the server and the client.
            // Send a message to the client to assure them that the server does not have the item equipped.
            SendUnequipItem("Mount", slot, _cachedMountId);

            Logger.Warning("Equip failed on item {0}", Logger.Args(_cachedMountId));
            return;
        }

        var item = wizEquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount);

        SendEquipItem(item, "Mount");
        SendAddEffects(addedEffects);
        _cachedMountId = 0;
    }

    private void EquipMountSubtle() {
        // If we have a cached mount, equip it.
        // Don't inform the client of it.
        if (_cachedMountId == 0) {
            return;
        }

        var wizard = GetActiveWizard();
        if (!wizard.InventoryToEquipmentTransfer(_cachedMountId, out var _, out var _)) {
            Logger.Warning("Equip failed on item {0}", Logger.Args(_cachedMountId));
            return;
        }

        _cachedMountId = 0;
    }

    private void UnEquipMount() {
        var wizard = GetActiveWizard();
        var wizEquipmentBehavior = wizard.EquipmentBehavior;

        var item = wizEquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount);
        if (item == null) {
            // We don't have a mount equipped.
            return;
        }

        _cachedMountId = item.m_globalID;
        var slot = wizEquipmentBehavior.GetSlotOfItem(_cachedMountId);

        if (!wizard.EquipmentToInventoryTransfer(_cachedMountId, out var removedEffects)) {
            // If this fails, there is perhaps desync between the server and the client.
            // Send a message to the client to assure them that the server does not have the item equipped.
            SendUnequipItem("Mount", slot, _cachedMountId);

            Logger.Warning("Unequip failed on item {0}", Logger.Args(_cachedMountId));
            return;
        }

        SendUnequipItem("Mount", slot, _cachedMountId);
        SendRemoveEffects(removedEffects);
    }

    private void SendEquipItem(WizClientObjectItem item, string slotName) {
        // Confirm to the player that we've equipped their item server side.
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM() {
            ItemID = item.m_globalID,
            SlotName = slotName,
            IsEquip = 1
        });

        // Serialize item and broadcast equip action to other players.
        var pubItem = ItemHelper.GetPublicItem(item);

        // As EquipmentService.SendEquipItem. (This check was inverted, so a mount re-equipped after combat was never
        // shown on the wizard.)
        if (!_itemSerializer.Serialize(pubItem, 1, out var data)) {
            Logger.Error("Failed to serialize item {0} for equip broadcast.", Logger.Args(item.m_globalID));

            return;
        }
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICEQUIPITEM() {
            GlobalID = GetActiveGameObject().m_globalID,
            SerializedInfo = data
        }, false);
    }

    private void SendUnequipItem(ByteString slotName, byte slot, ulong itemId) {
        // This one goes to the client.
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM() {
            ItemID = itemId,
            SlotName = slotName,
            IsEquip = 0
        });

        // This one goes to the zone.
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICUNEQUIPITEM() {
            GlobalID = GetActiveGameObject().m_globalID,
            IndexToRemove = slot
        }, false);
    }

    private void SendAddEffects(List<GameEffectBase> effects) {
        if (effects is null || effects.Count == 0) {
            return;
        }

        // This may fail since it is accessed immediately after attach. This means the CharacterService
        // hasn't had enough time to set its Wizard reference yet.
        var wizardObj = GetActiveGameObject();
        if (wizardObj is null) {
            wizardObj = GetActiveWizard()?.GetInitializedGameObject();

            if (wizardObj is null) {
                Logger.Error("Failed to get wizard object for adding effects.");
                return;
            }
        }

        var charObjId = wizardObj.m_globalID;

        foreach (var effect in effects) {
            var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
            if (!_effectSerializer.Serialize(effect, flags, out var effectSerializedData)) {
                Logger.Error("Failed to serialize effect {0}", Logger.Args(effect.m_effectNameID));

                continue;
            }

            SendToSocket(new GAME_5_PROTOCOL.MSG_ADDEFFECT() {
                GameObjectID = charObjId,
                EffectData = effectSerializedData
            });
        }
    }

    private void SendRemoveEffects(List<GameEffectBase> effects) {
        var charObjId = GetActiveGameObject().m_globalID;

        foreach (var effect in effects) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT() {
                GameObjectID = charObjId,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }
    }

    private void SetNoAggroGrace() {
        var wizard = GetActiveWizard();
        var charObjId = GetActiveGameObject().m_globalID;
        var effectInternalId = wizard.GameEffects.Count + 1;

        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var endTime = epoch + NO_AGGRO_EFFECT_DURATION_IN_SECONDS;

        var effect = new NamedEffect {
            m_effectNameID = NO_AGGRO_EFFECT_STRINGID,
            m_internalID = effectInternalId,
            m_endTime = (uint) endTime,
            m_overrideName = "NoAggro"
        };

        wizard.GameEffects.Add(effect);
        wizard.IsInCombatGrace = true;

        if (!_effectSerializer.Serialize(effect, PropertyFlags.Prop_Transmit, out var effectSerializedData)) {
            Logger.Error("Failed to serialize effect {0}", Logger.Args(effect.m_effectNameID));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_ADDEFFECT() {
            GameObjectID = charObjId,
            EffectData = effectSerializedData
        });
    }

    private void RemoveNoAggroEffect() {
        var wizard = GetActiveWizard();
        var charObjId = GetActiveGameObject().m_globalID;

        var effect = wizard.GameEffects.Find(e => e.m_effectNameID == NO_AGGRO_EFFECT_STRINGID);
        if (effect is null) {
            return;
        }

        wizard.GameEffects.Remove(effect);
        wizard.IsInCombatGrace = false;

        SendToSocket(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT() {
            GameObjectID = charObjId,
            EffectNameID = effect.m_effectNameID,
            InternalID = effect.m_internalID,
        });
    }

}
