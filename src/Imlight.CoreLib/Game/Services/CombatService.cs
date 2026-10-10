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
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
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

    // [Classic] PostCombatGraceSeconds (default 5): the fixed grace outside the classic profile.
    private static readonly uint NO_AGGRO_EFFECT_DURATION_IN_SECONDS = GraceSeconds();

    private static uint GraceSeconds()
        => uint.TryParse(ConfigurationManager.Settings["Classic.PostCombatGraceSeconds"].AsString(), out var seconds)
            && seconds is > 0 and <= 60 ? seconds : 5;

    // CLASSIC (2026-10-04): under the classic profile the grace is KingsIsle's two effects (PostCombatGrace): up to 30 s
    // while the wizard stands still after the duel, then 6 s from its first move. [Classic] PostCombatStillSeconds and
    // PostCombatMoveGraceSeconds override them (tests, playbot).
    private static readonly TimeSpan StillSeconds = Seconds("Classic.PostCombatStillSeconds", PostCombatGrace.DefaultStill);
    private static readonly TimeSpan MoveGraceSeconds = Seconds("Classic.PostCombatMoveGraceSeconds", PostCombatGrace.DefaultMoving);

    private static TimeSpan Seconds(string key, TimeSpan fallback)
        => double.TryParse(ConfigurationManager.Settings[key].AsString(), System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 600
            ? TimeSpan.FromSeconds(seconds) : fallback;

    private readonly PostCombatGrace _grace = new(StillSeconds, MoveGraceSeconds); // CLASSIC

    private readonly CoreObjectSerializer _effectSerializer = new(
        behaviors: SerializerFlags.None
    );
    private readonly CoreObjectSerializer _itemSerializer = new(
        behaviors: SerializerFlags.None
    );

    private IActorRef _currentDuelActor;
    private bool _cheatInstantCinematics;
    private bool _cheatNoFizzle;

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
            SessionActor.ActorRef.Tell(new COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED {
                CreatureTid = message.CreatureTid, Success = false,
                Refusal = Imlight.Classic.Rules.HenchmanRefusal.NotInCombat,
            });

            return;
        }

        message.Actor = SessionActor.ActorRef;
        _currentDuelActor.Tell(message, SessionActor.ActorRef);
    }

    // CLASSIC: the client's henchman Dismiss button (GUI_DismissHenchmen, "Crowns will not be refunded") sends
    // MSG_DISMISS_SUMMON with the henchman's sub-circle; the duel checks that it is this player's henchman.
    [MessageHandler(typeof(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DISMISS_SUMMON))]
    private void ReceiveDismissSummon(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DISMISS_SUMMON message)
        => _currentDuelActor?.Tell(new COMBAT_106_PROTOCOL.MSG_DISMISSHENCHMAN {
            Actor = SessionActor.ActorRef, SubCircle = (int) message.Subcircle,
        }, SessionActor.ActorRef);

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

        var wizard = GetActiveWizard();
        try {
            if (!CompleteCombatEntry(wizard, message, () => {
                // Unequip mounts if the player has one equipped, before the captured native elixir receipt.
                UnEquipMount();
                if (ClassicQuestEngine.IsActive && wizard.IsInCombatGrace) {
                    RemoveNoAggroEffect(); // CLASSIC: in a duel again: no fade at the table.
                }
            }, SendToSocket)) { CloseSession(); return; }
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
            throw;
        }
        // Set the persistent location and orientation of the wizard.
        wizard.SetPersistentLocation(message.SlotPosition);

        // Orientation is given in radians. It must be converted to degrees and then to a byte.
        var orientationRadians = message.SlotOrientation;
        var orientationDegrees = (float) (orientationRadians * (180 / Math.PI));
        var orientation = (byte) (orientationDegrees / 360 * 256);
        wizard.SetPersistentOrientation(orientation);
    }

    // CLASSIC: prepared messages are immutable internal metadata, emitted at the original post-mount-stow position.
    // A later idempotent runtime refresh cannot recreate the removal packets captured before the stat snapshot.
    internal static bool CompleteCombatEntry(Wizard wizard, COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL message,
        System.Action beforeEffects, System.Action<Imcodec.MessageLayer.IMessage> send) {
        if (wizard is null || WizardCollection.IsInventorySnapshotUncertain(wizard)
            || message.ElixirReceipt is { } captured && (!ReferenceEquals(captured.Wizard, wizard) || captured.Messages.IsDefault)) return false;
        beforeEffects();
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) return false;
        if (message.ElixirReceipt is { } receipt) {
            foreach (var packet in receipt.Messages) send(packet);
        }
        else {
            foreach (var packet in ElixirService.PublishCombatTransition(wizard, true, message.Duel?.Duel?.m_bPVP ?? true))
                send(packet); // Stock/inactive and legacy internal callers retain their existing late transition.
        }
        return true;
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT))]
    private void ReceiveCombatDefeat(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT message) {
        _currentDuelActor = null; // CLASSIC: the duel is over for us; a later logout must not flee it again.
        if (!PublishCombatState(GetActiveWizard(), false, false)) return;
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

    // CLASSIC: an open PvP fight ended, or this wizard left the circle: no rewards, no penalty, no trip home. A defeated
    // wizard keeps 1 health, which regenerates.
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE))]
    private void ReceivePvpRelease(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE message) {
        _currentDuelActor = null;
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        if (!PublishCombatState(wizard, false, false)) return;
        EquipMount(); // CLASSIC: the mount taken off for the fight goes back on, as after any duel.
        if (wizard.GameStats.m_currentHitpoints <= 0) {
            wizard.UpdateHealth(1);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
                CharacterID = wizard.GameObjectID,
                NewHealth = 1,
                NewHealthMax = wizard.GameStats.m_baseHitpoints,
                DisplayDiff = 0,
            });
        }

        SetNoAggroGrace();
        if (message.Fought) {
            // CLASSIC: the native duel/result UI already shows this; a non-modal notice creates a "!" alert.
            Logger.Information("PvP result for {0} (log only): {1}",
                Logger.Args(wizard.CharId, message.Won ? "Your side won the duel!" : "Your side lost the duel."));
        }
    }

    // CLASSIC: ".pvp ready" / ".pvp leave" go to the open PvP circle this wizard sits in.
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND))]
    private void ReceivePvpCommand(CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND message) {
        if (_currentDuelActor is null) {
            InformGameClient("You are not in an open PvP circle.", isImportant: true);

            return;
        }

        message.Actor = SessionActor.ActorRef;
        _currentDuelActor.Tell(message, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatVictory(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        // CLASSIC: the duel is over; a logout after it used to reach the ended duel, which ran flee and defeat on it
        // (a second "Duel ended" and a MSG_SENDTOHUB).
        _currentDuelActor = null;
        if (!PublishCombatState(GetActiveWizard(), false, false)) return;
        // CLASSIC: the health the wizard walks away with, before any reward (a level-up refill comes after it).
        if (HealthAfterDuel(GetActiveWizard()) is { } health) SendToSocket(health);
        PushHelperIdle();
        EquipMount();
        SetNoAggroGrace();

        ClassicBadges.MobsDefeated(GetActiveWizard(), message.MobTemplateIds, SendToSocket); // CLASSIC: kill badges.
        if (WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) { CloseSession(); return; }
        RecordSecondChanceWin(message.MobTemplateIds); // CLASSIC: opens a beaten boss's Second Chance chest.

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

    /// <summary>
    /// CLASSIC: the wizard's health after a won duel, for the HUD. The duel changed health on the server only (the
    /// client saw it in MSG_COMBATHEALTH); nothing else told the client after the duel, so a wizard's own health stat
    /// stayed at its pre-duel value until the next zone change (playbot DS 2026-10-05: wizards chained fights at
    /// 100-300 health believing they were full). The maximum is the level table's, as in every other MSG_UPDATEHEALTH:
    /// the client adds its equipment effects itself. DisplayDiff 0: no floating number. Null without a level table.
    /// </summary>
    internal static WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH HealthAfterDuel(Wizard wizard) {
        if (wizard?.GameStats is not { } stats || wizard.MagicSchoolBehavior is not { } school
            || WizardProgressionTransactions.LevelInfo(school.MagicSchool, school.Level) is not { m_hitpoints: > 0 } table) {
            return null;
        }

        return new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = wizard.GameObjectID,
            NewHealth = Math.Max(stats.m_currentHitpoints, 0),
            NewHealthMax = table.m_hitpoints,
            DisplayDiff = 0,
        };
    }

    // CLASSIC: the bosses this wizard beat here open their Second Chance chests (October 2009).
    private void RecordSecondChanceWin(ulong[] mobTemplateIds) {
        if (ClassicProgression.SecondChance is null || mobTemplateIds is not { Length: > 0 } || GetActiveWizard() is not { } wizard) {
            return;
        }

        var instance = WizardData.Collections.OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId)?.InstanceOwnerId ?? 0;
        Game.SecondChance.SecondChanceChests.Instance.RecordWin(wizard.CharId, wizard.Zone, instance, mobTemplateIds);
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
            AddHolidayDrops(rules, templateId, result, random); // CLASSIC: classic-data/holidays boss drops in season
            AddClassicMobLoot(rules, templateId, result, random);
        }

        if (!result.HasRewards) {
            return 0;
        }

        LootGranter.GrantAndDisplay(SessionActor.ActorRef, GetActiveWizard(), result);

        return result.GoldAmount;
    }

    /// <summary>
    /// CLASSIC: one defeated mob's gold, equipment, treasure cards and reagents under the profile's mob reward rules,
    /// added to <paramref name="result"/> (also a Second Chance chest's roll).
    /// </summary>
    internal static void AddClassicMobLoot(MobRewardRules rules, ulong templateId, DropTableResult result, Random random) {
        if (ClassicMobInfo.Of(templateId) is not { } mob) {
            return;
        }

        var loot = rules.Roll(mob, random, ClassicSettings.DropRateMultiplier); // CLASSIC: dashboard switch
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

    // CLASSIC: while a holiday event runs, its bosses also roll their 2009 drops (classic-data/holidays), each list at
    // the 2009 boss rate for a list that long (MobRewardRules), times [Classic] DropRateMultiplier.
    private static void AddHolidayDrops(MobRewardRules rules, ulong templateId, DropTableResult result, Random random) {
        if (ClassicHolidays.DropsFor(templateId) is not { } drops) {
            return;
        }

        var multiplier = ClassicSettings.DropRateMultiplier;
        var gear = drops.Items.Select(item => new DropEntry(item, null)).ToImmutableArray();
        foreach (var item in rules.ItemDrops.Roll(gear, MobKind.Boss, gear.Length, tallied: false, random, multiplier)) {
            if (CoreObjectFactory.GetCoreTemplate(item) is not null) {
                result.Items.Add(new DropItemResult { ItemId = item.ToString(), ItemName = string.Empty, Quantity = 1 });
            }
        }

        var cards = drops.TreasureCards.Select(card => new DropEntry(card, null)).ToImmutableArray();
        foreach (var card in rules.TreasureCardDrops.Roll(cards, MobKind.Boss, cards.Length, tallied: false, random, multiplier)) {
            if (card <= uint.MaxValue && CoreObjectFactory.GetCoreTemplate(card) is SpellTemplate) {
                result.TreasureCards.Add((uint) card);
            }
        }
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
            // CLASSIC: a stray move (a late packet after the duel, or a forged one) is dropped; throwing closed the
            // session (security audit 2026-10-04).
            Logger.Debug("Combat move received without a duel actor; dropped.");

            return;
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
    private void ReceiveNoAggroGraceOver() {
        // CLASSIC: the still effect running out swaps in the 6 s one ("PostCombatRemoved"); that one ends the grace
        // ("ReAggro").
        if (ClassicQuestEngine.IsActive && _grace.Protected) {
            if (_grace.Advance(DateTime.UtcNow) == PostCombatPhase.Moving) {
                PutGraceEffect();
                return;
            }
        }

        RemoveNoAggroEffect();
    }

    // CLASSIC: the first move off the duel spot ends the still effect; the wizard stays safe 6 s more.
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENTMOVE))]
    private void ReceiveClientMoveForGrace(GAME_5_PROTOCOL.MSG_CLIENTMOVE message) {
        if (!ClassicQuestEngine.IsActive || _grace.Phase != PostCombatPhase.Still) {
            return;
        }

        // As WizardService reads it: compressed by 4, signed.
        var location = new System.Numerics.Vector3(unchecked((short) message.LocationX * 4),
            unchecked((short) message.LocationY * 4), unchecked((short) message.LocationZ * 4));
        if (_grace.Moved(location, DateTime.UtcNow)) {
            Logger.Debug("{Wizard} moved after the duel; translucent {Seconds} s more.",
                Logger.Args(GetActiveWizard()?.CharId, MoveGraceSeconds.TotalSeconds));
            if (_grace.Protected) {
                PutGraceEffect();
            }
            else {
                RemoveNoAggroEffect();
            }
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message) {
        if (_currentDuelActor != null) {
            DetachCombatSession(GetActiveWizard());
        }
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) {
        if (_currentDuelActor != null) {
            DetachCombatSession(GetActiveWizard());
        }
        _currentDuelActor?.Tell(message, SessionActor.ActorRef);
    }

    // CLASSIC: the trusted duel message and its canonical elixir effects become visible together.
    // A partial runtime failure closes the quarantined session before rewards can use stale offsets.
    private bool PublishCombatState(Wizard wizard, bool inCombat, bool pvp) {
        if (wizard is null) return false;
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return false; }
        try {
            var messages = ElixirService.PublishCombatTransition(wizard, inCombat, pvp);
            foreach (var message in messages) SendToSocket(message);
            return true;
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return false; }
            throw;
        }
    }

    private void DetachCombatSession(Wizard wizard) {
        if (wizard is null) return;
        try { ElixirService.DetachCombatSession(wizard); }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
            throw;
        }
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

    // CLASSIC: the mount taken off for the duel is saved on the wizard (CombatStowedMountId), not only held here: a
    // session that ended mid-fight (logout, a dropped client, a restart) lost it, and the wizard found the mount in
    // the backpack at every next login. EquipmentService puts a saved one back at attach.
    private void EquipMount() {
        var wizard = GetActiveWizard();
        if (wizard is null || wizard.CombatStowedMountId == 0) {
            return;
        }

        var mountId = wizard.CombatStowedMountId;
        if (!wizard.RestoreDuelStowedMount(ZoneDisallowsMounts(), out var addedEffects)) {
            if (wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount) is null) {
                // The client may still show it worn; tell it the server does not have it equipped.
                SendUnequipItem("Mount", 255, mountId);
            }

            return;
        }

        var item = wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount);
        if (item is not null) {
            SendEquipItem(item, "Mount");
        }
        SendAddEffects(addedEffects);
    }

    private void EquipMountSubtle() {
        // Put the stowed mount back without telling the client (a defeat sends the wizard home, a fresh attach).
        GetActiveWizard()?.RestoreDuelStowedMount(false, out _);
    }

    private void UnEquipMount() {
        var wizard = GetActiveWizard();
        if (wizard is null || wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount) is null) {
            return; // not mounted (or a rejoin: the dropped session's stow is already saved)
        }

        var mountId = wizard.StowMountForDuel(out var slot, out var removedEffects);
        if (mountId == 0) {
            Logger.Warning("Unequip failed on the mount of {0}", Logger.Args(wizard.CharId));

            return;
        }

        SendUnequipItem("Mount", slot, mountId);
        if (removedEffects is not null) {
            SendRemoveEffects(removedEffects);
        }
    }

    private bool ZoneDisallowsMounts() {
        var zoneActor = SessionActor.GetZoneActor();
        return zoneActor is not null && Classic.ZoneDataDirectory.TryGet(zoneActor, out var data) && (data?.m_noMounts ?? false);
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

    /// <summary>
    /// CLASSIC: the post-combat grace: the client's "PostCombatEffect" (GameEffectData/WizardEffects.xml: category
    /// PostCombat, AddTranslucentEffect / RemoveTranslucentEffect, public) makes the wizard translucent. The effect is
    /// looked up by name, so it carries no override name (the old "NoAggro" override named no template, and the
    /// client dropped the effect: no fade). It is public, so everyone in the zone sees the wizard fade, as retail did.
    /// Under the classic profile it lasts while the wizard stands still (up to 30 s), then PostCombatEffect2 (6 s)
    /// from its first move; elsewhere it is a fixed PostCombatGraceSeconds.
    /// </summary>
    private void SetNoAggroGrace() {
        var now = DateTime.UtcNow;
        if (ClassicQuestEngine.IsActive) {
            var at = GetActiveWizard().Location;
            _grace.Start(now, new System.Numerics.Vector3(at.X, at.Y, at.Z));
        }
        else {
            _grace.Clear();
        }

        var end = ClassicQuestEngine.IsActive ? _grace.EndsUtc : now.AddSeconds(NO_AGGRO_EFFECT_DURATION_IN_SECONDS);
        PutEffect(PostCombatGrace.StillEffectName, end);
    }

    // CLASSIC: the effect for the grace's phase, and a timer for its end.
    private void PutGraceEffect() => PutEffect(_grace.EffectName, _grace.EndsUtc);

    private void PutEffect(string name, DateTime endUtc) {
        var wizard = GetActiveWizard();
        foreach (var message in PostCombatEffects.Put(wizard, GetActiveGameObject().m_globalID, name, endUtc)) {
            ZoneBroadcast(message, isSelfless: false);
        }

        Timers.StartSingleTimer("NoAggroGraceOver", new COMBAT_106_PROTOCOL.MSG_NOAGGROGRACEOVER(),
            endUtc - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    private void RemoveNoAggroEffect() {
        _grace.Clear();
        Timers.Cancel("NoAggroGraceOver");
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        foreach (var message in PostCombatEffects.Take(wizard, GetActiveGameObject().m_globalID)) {
            ZoneBroadcast(message, isSelfless: false);
        }
    }

}
