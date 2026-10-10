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
 * COMBAT DUEL SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages turn-based combat encounters between players and creatures.
 * This component is responsible for handling duel initiation, combat moves,
 * and duel resolution. It also manages the duel state and participant data.
 * 
 * USAGE EXAMPLE:
 * This component may activate from an NpcComponent on a dueling creature.
 * It may also activate as a result of `ResStartDuel` from a trigger.
 * 
 * NOTE:
 * This class is merely the director of the duel. The actual combat logic is
 * all handled within the `Imlight.CoreLib.Game.Combat` namespace.
 * Start at the `CombatResolver` class and work your way down.
 * Tutorial duels are scripted by the `TutorialDuelDirector` in that namespace.
 * Combat positions are determined by sigil templates, with specific subcircle positions.
 * CLASSIC: OWNER RULING 2026-10-10, "take the rewards away": a wizard defeated during a battle their side still wins
 * gets no rewards from it (no XP, gold, Crowns, drops, reagents or quest kill credit) and is still sent home with low
 * health. Wizards standing at the end keep everything. [Classic] DefeatedGetNoRewards (on); see
 * DefeatedWithoutRewards. PvE only: PvP and the arena keep their own rules.
 * 
 * TODO:
 * - Implementation of creature stunning functionality
 * - Calculate the correct damage percent max as a limit function
 * - `IsNewbieZone` and `IsDangerousZone` should be elements of the zone itself
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Shared.Items;
using Imcodec.Cryptography;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Manages combat duels between players and creatures in the game world.
/// Controls the full duel lifecycle including initialization, participant management,
/// turn sequencing, spell casting, and resolution of combat.
/// </summary>
/// <remarks>
/// Acts as the central orchestrator for the combat system, handling messaging between
/// all participants, managing combat state transitions, and coordinating the various
/// phases of combat (planning, execution, resolution). The component creates and 
/// positions subcircles according to sigil templates and maintains combat state
/// including team assignments, turn order, and participant status.
/// </remarks>
internal sealed partial class CombatDuelComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IComponentFactory, IWithTimers, IClientBehaviorProvider<WizardClientDuelBehavior> {

    private const byte PLANNING_TIME = 30;
    // CLASSIC: PERF combat turnaround (see PerfMonitor): when the round's last move came, and whether planning ended early.
    private long _perfAllMovesTicks;
    private bool _ownedMinionEarlyFinishScheduledWas;
    private const float DUEL_GRACE_PERIOD_IN_SECONDS = 3.75f;
    private const float DUEL_NEW_ROUND_DELAY = 2.5f;
    private const float YAW_ERROR_COMPENSATION = 1.58f;
    private const string GRACE_TIME_KEY = "GracePeriod";
    private const string PLANNING_TIME_KEY = "PlanningPhase";
    private const string PREPLANNING_TIME_KEY = "PrePlanningPhase";
    private const string RESOUTION_TIME_KEY = "ResolutionPhase";
    private const double MINION_SUMMON_ANIMATION_DELAY = 5.5;
    // CLASSIC: a deferred summon belongs to the occupant that cast it, not a future occupant of the same seat.
    private sealed record DeferredMinionOwner(CoreObject Identity, ulong GlobalId, IActorRef Actor,
        object OccupantIdentity, ulong CharacterId);
    private readonly Dictionary<ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON, DeferredMinionOwner> _deferredMinionOwners = [];

    public bool NoTransfer { get; set; } = false;
    public ITimerScheduler Timers { get; set; }
    public Duel Duel { get; private set; }
    public Combat.CombatResolver CombatResolver { get; private set; }
    public CombatDuelSubCircle[] SubCircles { get; private set; }
    public CombatDuelSubCircle[] ActiveSubCircles => [.. SubCircles.Where(x => x.Occupied)];
    // Human players only; a minion must not let an extra enemy scale into the fight.
    public byte PlayerCount => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player
                                                            && !x.IsSummonedMinion);
    public byte CreatureCount => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster);
    // Exclude minions from the loss condition; they must not hold a fight open.
    public byte AlivePlayerCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player && x.IsAlive
                                        && !x.IsSummonedMinion);
    public byte AliveCreatureCount
        // CLASSIC: both PvP sides lose when their last wizard falls, even with a living summoned minion.
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && x.IsAlive
                                        && (!_pvp || !x.IsSummonedMinion));
    public byte PlayersInDuel
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player && x.AddedToDuel);
    public byte CreaturesInDuel
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && x.AddedToDuel);
    public byte AliveAndInDuelPlayerCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player && x.IsAlive && x.AddedToDuel
                                        && !x.IsSummonedMinion);
    public byte AliveAndInDuelCreatureCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && x.IsAlive && x.AddedToDuel
                                        && (!_pvp || !x.IsSummonedMinion));
    public ulong SigilId => Entity.ActiveGameObject.m_globalID;

    // CLASSIC: keyed by the object instance. CoreObject is a record whose hash follows its location, so the default
    // comparer missed the entry after any move: enter events re-fired on every move and exit never fired.
    private readonly Dictionary<CoreObject, IActorRef> _entitiesInRange = new(ReferenceEqualityComparer.Instance);
    private readonly ObjectSerializer _serializer = new(
        Versionable: false,
        Behaviors: SerializerFlags.None
    );
    private readonly PropertyFlags _combatParticipantFlags = (PropertyFlags) 4;
    private readonly PropertyFlags _combatParticipantStatFlags = (PropertyFlags) 5;
    private readonly PropertyFlags _combatParticipantHandFlags = (PropertyFlags) 5;
    private readonly PropertyFlags _upFirstFlags = PropertyFlags.Prop_Transmit
                                                 | PropertyFlags.Prop_AuthorityTransmit
                                                 | PropertyFlags.Prop_Public;

    private CombatSigilObjectInfo _combatSigilObjectInfo;
    private RenderComponent _renderComponent;
    private CombatSigilTemplate _sigilTemplate;
    private bool _isActive;
    private bool _awaitingCombatMoves;
    private TutorialDuelDirector _tutorialDirector;
    // Seconds of cinematics before a caster's cast; SummonMinion adds the animation delay to it.
    internal float CurrentActionCinematicOffsetSeconds;
    internal bool CheatInstantCinematics { get; set; }

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is not null && x.m_behaviorName == "DuelBehavior");

    public override void OnStart() {
        // Disable the RenderComponent. We'll activate it when the sigil is activated.
        _renderComponent = Entity.GetComponentOfType<RenderComponent>();
        _renderComponent?.Disable();

        _tutorialDirector = new TutorialDuelDirector(this, Entity.Zone?.ZonePath ?? "");
    }

    public WizardClientDuelBehavior GetClientBehaviorInstance() => new() {
        m_pDuel = Duel,
        // CLASSIC: the native cinematic resolves this ID independently of our seating template.
        // Advertise the same selected sigil, preserving the street default before sigil details arrive.
        m_sigilTemplateID = StringHash.Compute(_sigilTemplate?.m_sigilName ?? "CombatSigil8Actor"),
    };

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // CLASSIC: a wizard of an arena match arrives: their seat (or their held seat after a drop).
        if (_arena) {
            if (!(_isActive && TryRejoin(playerObj, playerActor, playerWizard))
                && (!ElixirService.PreparesCombatSnapshots
                    || SubCircles?.Any(circle => circle is { Occupied: true, Disconnected: true }
                        && circle.HeldCharacterId == playerWizard?.CharId) != true)) {
                ArenaOnPlayer(playerObj, playerActor, playerWizard);
            }

            return;
        }

        if (!_isActive) {
            return;
        }

        // CLASSIC: a wizard who dropped mid-fight and logged back in takes their held seat again.
        if (TryRejoin(playerObj, playerActor, playerWizard)) {
            return;
        }

        // Check if this player is in the duel. If they are, remove them from the duel.
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == playerActor);
        if (subCircle is null) {
            return;
        }

        // Handle this as if it were the flee action.
        HandleFleeAction(subCircle);
    }

    // CLASSIC: forget a player who leaves the zone, so the same object coming back in range counts as an enter again.
    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        foreach (var key in _entitiesInRange.Where(x => x.Value.Equals(playerActor)).Select(x => x.Key).ToList()) {
            _entitiesInRange.Remove(key);
        }
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // CLASSIC: an open PvP circle opens when a wizard walks in.
        if (_pvp) {
            if (_combatSigilObjectInfo is not null) {
                if (_arena) {
                    ArenaOnPlayer(playerObj, playerActor, playerWizard); // CLASSIC: an arena match circle
                }
                else {
                    PvpOnPlayerMove(playerObj, playerActor, playerWizard);
                }
            }

            return;
        }

        if (!_isActive) {
            return;
        }

        // CLASSIC: the post-combat grace also keeps a wizard out of fights they walk into; they join on their next
        // move after it ends, if they are still in the circle.
        if (playerWizard?.IsInCombatGrace == true) {
            return;
        }

        // Check if the player is now in range of the object.
        // If there's a slot available, add the player to the duel.
        if (IsInRadius(playerObj, _combatSigilObjectInfo.m_radius) && !_entitiesInRange.ContainsKey(playerObj)) {
            // CLASSIC: an ambient wizard walks in only with a permit for this duel (see CombatDuelComponent.Ambient.cs).
            if (!AmbientWizards.MayJoin(playerActor, SigilId)) {
                return;
            }

            _entitiesInRange.Add(playerObj, playerActor);

            // CLASSIC: a real player at a full circle takes an ambient wizard's slot.
            if (IsSlotAvailable(CombatTeam.Player) || MakeRoomForRealPlayer(playerActor)) {
                AddParticipant(playerObj, playerActor);
                NotifyAmbientWizards();
            }
        }
        else if (!IsInRadius(playerObj, _combatSigilObjectInfo.m_radius) && _entitiesInRange.ContainsKey(playerObj)) {
            _entitiesInRange.Remove(playerObj);
        }
    }

    public override void OnCreatureMove(CoreObject creature, IActorRef suspect, ZoneEntity entity) {
        if (!_isActive || _pvp) { // CLASSIC: no creature joins an open PvP circle
            return;
        }

        // Check if the creature is now in range of the object.
        // If there's a slot available, add the creature to the duel.
        if (IsInRadius(creature, _combatSigilObjectInfo.m_radius) && !_entitiesInRange.ContainsKey(creature)) {
            var npcComponent = entity.GetComponentOfType<NpcComponent>();
            var isMonster = npcComponent == null || npcComponent.IsMonster;

            // A roaming enemy reaching a full or cap-limited circle despawns through the existing creature lifecycle.
            _entitiesInRange.Add(creature, suspect);

            if (!isMonster) {
                return;
            }

            if (IsSlotAvailable(CombatTeam.Monster)) {
                AddParticipant(creature, suspect);
            }
            else {
                suspect.Tell(new COMBAT_106_PROTOCOL.MSG_REJECTEDROAMINGCREATURE {
                    ExpectedCreature = creature
                });
            }
        }
        else if (!IsInRadius(creature, _combatSigilObjectInfo.m_radius) && _entitiesInRange.ContainsKey(creature)) {
            _entitiesInRange.Remove(creature);
        }
    }

    internal void ZoneBroadcast(IMessage message) => Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
        Selfless = false,
        Sender = Self,
        Message = message
    });

    internal void DuelBroadcast(IMessage message) {
        EnactActionOnSubCircles(circle => circle.ParticipantActor.Tell(message));
        ArenaOnlookerBroadcast(message); // CLASSIC: the arena's public phase/stats, never private hands.
    }

    internal void CreatureBroadcast(IMessage message) => EnactActionOnSubCircles(circle => {
        if (circle.OccupiedTeam == CombatTeam.Monster) {
            circle.ParticipantActor.Tell(message);
        }
    });

    // CLASSIC: every wizard in the duel, whatever team (open PvP).
    internal void WizardBroadcast(IMessage message) => EnactActionOnSubCircles(circle => {
        if (circle.IsWizard) {
            circle.ParticipantActor.Tell(message);
        }
    });

    internal new void PlayerBroadcast(IMessage message) => EnactActionOnSubCircles(circle => {
        if (circle.OccupiedTeam == CombatTeam.Player) {
            circle.ParticipantActor.Tell(message);
        }
    });

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SIGILDETAILS))]
    private void ReceiveSigilDetails(ZONE_102_PROTOCOL.MSG_SIGILDETAILS message) {
        _combatSigilObjectInfo = message.CombatSigilObjectInfo;

        // Get the sigil template.
        _sigilTemplate = (CombatSigilTemplate) SigilFactory.GetSigilTemplate(_combatSigilObjectInfo.m_sigilType);

        // CLASSIC: one of the server's open PvP circles (classic-data/pvp).
        _pvp = ClassicPvp.IsPvpCircle(Entity.Zone?.ZonePath, _combatSigilObjectInfo.m_zoneTag);

        // CLASSIC: an arena's match circle (Classic/Arena) runs as a PvP circle too.
        _arena = Classic.Arena.ClassicArena.IsArenaCircle(Entity.Zone?.ZonePath, _combatSigilObjectInfo.m_zoneTag);
        _pvp |= _arena;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL))]
    private void ReceiveDuelStart(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL message) {
        if (_isActive) {
            return;
        }

        // CLASSIC: a safe restart's countdown is over; it waits for the fights in progress, so no new one starts. An
        // open PvP circle is never started by a creature.
        if (Classic.Admin.ServerAdmin.BlockNewDuels || _pvp) {
            return;
        }

        if (_renderComponent is null) {
            Logger.Error("RenderComponent is null for duel {0}! Deleting sigil.",
                Logger.Args(SigilId));

            Entity.DespawnObject();

            return;
        }

        // Activate the sigil.
        InitializeDuel(message.StartingParticipants);
        // CLASSIC: refused prepared admission must not announce or schedule an empty duel.
        if (ElixirService.PreparesCombatSnapshots && !_isActive) return;
        _renderComponent.Enable();
        NotifyAmbientWizards(); // CLASSIC

        // Broadcast MSG_DUEL to inform all clients a duel is now active.
        // The live server sends this to enable 3D combat targeting.
        var duelBehavior = GetClientBehaviorInstance();
        if (_serializer.Serialize(duelBehavior, _combatParticipantFlags, out var duelData)) {
            ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DUEL {
                Data = duelData,
            });
        }

        // Fire a message to self to start the duel after the grace period has ended.
        var delay = TimeSpan.FromSeconds(DUEL_GRACE_PERIOD_IN_SECONDS);
        Timers.StartSingleTimer(GRACE_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_NEWROUND(), delay);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_NEWROUND))]
    private void ReceiveNewRound(COMBAT_106_PROTOCOL.MSG_NEWROUND message) {
        if (!_isActive) {
            return;
        }

        // CLASSIC: every wizard in the fight dropped; it waits for one to log back in (or for the holds to run out).
        if (ShouldWaitForRejoin()) {
            _waitingForRejoin = true;
            Logger.Information("Duel {0} | every wizard has dropped; waiting for one to rejoin.",
                Logger.Args(Duel.m_duelID.Full));
            PublishActiveDuel();

            return;
        }

        Logger.Debug("Duel {0} | New round {1} at {2}",
            Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, DateTime.Now.ToString("HH:mm:ss")));

        // Add the circles to combat if they are not already.
        AddWaitingCombatParticipants();
        CombatResolver.Reset();
        _ownedMinionControl.NewRound();
        _ownedMinionFallbacks.Clear();
        _ownedMinionHeldAiMoves.Clear();
        ResetMinionHand();
        _ownedMinionEarlyFinishScheduled = false;
        _awaitingCombatMoves = true;

        // Determine the power pip gain for each participant.
        DoPipGain();

        // Echo the new round message to all actors.
        EnactActionOnSubCircles(circle => circle.ParticipantActor.Tell(message));

        // Pre-planning phase just wants to send who is up first.
        Duel.m_duelPhase = kDuelPhase.kPhase_PrePlanning;
        Duel.m_roundNum++;
        BeginBossCheatRound(Duel.m_roundNum); // CLASSIC: Briskbreeze Tower's scripted bosses
        ApplyFullTeamGoesFirst();
        SendCombatPhase((byte) Duel.m_duelPhase);
        SendUpFirst(Duel.m_roundNum);

        _tutorialDirector.OnNewRound(Duel.m_roundNum);

        var delay = TimeSpan.FromSeconds(DUEL_NEW_ROUND_DELAY);
        Timers.StartSingleTimer(PREPLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEBEGIN(), delay);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEBEGIN))]
    private void ReceivePlanningPhaseBegin() {
        // Planning phase is when each participant notices their new stats and "plans" accordingly.
        Duel.m_duelPhase = kDuelPhase.kPhase_Planning;
        _ownedMinionPlanningDeadline = DateTime.UtcNow.AddSeconds(PLANNING_TIME);
        SendCombatPhase((byte) Duel.m_duelPhase);

        SendCombatStats();
        SendCombatHand();
        SendCombatPips();
        SendCombatHealth();
        SendCombatUI(PLANNING_TIME);

        // Re-telegraph minion AI moves now that the planning HUD exists.
        ResendMinionMoveSelections();

        // CLASSIC: a seat held for a dropped wizard passes.
        PassHeldSeats();
        // CLASSIC: ambient wizards in the duel pick their cards a few seconds in.
        ScheduleAmbientTurns();
        ScheduleHenchmanTurns(); // CLASSIC: hired henchmen too

        // Tutorial duels flush queued card grants and re-script the golems before planning.
        _tutorialDirector.OnPlanningPhaseBegin();
        PublishOwnedMinionSnapshots();

        // Tutorial fights have no planning countdown (m_disableTimer): the client drives pacing. Planning still
        // ends normally once every participant has enqueued a move (ReceiveCombatMove).
        if (!_tutorialDirector.IsActive) {
            var delay = TimeSpan.FromSeconds(PLANNING_TIME);
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), delay);
        }
        ReevaluateOwnedMinionPlanning();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATDRAW))]
    private void ReceiveCombatDraw(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATDRAW message) {
        // Find which sub circle this draw request is for.
        var caster = SubCircles.FirstOrDefault(x => x.ParticipantActor == message.Actor);
        if (caster is null) {
            Logger.Warning("Duel {0} | Combat draw received from an actor that is not in the duel.",
                Logger.Args(Duel.m_duelID.Full));

            return;
        }

        // CLASSIC: a treasure-card draw while a minion's hand is showing would draw into the wrong hand.
        if (BlockMinionHandDraw(caster)) {
            return;
        }

        // Draw a random treasure card from the vault.
        var drawnSpell = caster.DrawFromVault();
        if (drawnSpell != null) {
            Logger.Debug("Duel {0} | Slot {1} | Drew treasure card {2} from vault.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, drawnSpell.m_templateID));
        }

        // Send the hand as-is: a refilling send would also pull real deck cards into the open slots.
        SendCurrentCombatHand(caster);
    }

    [MessageHandler(typeof(TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND))]
    private void ReceiveTutorialRebuildDuelHand(TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND message) {
        if (!_isActive) {
            return;
        }

        _tutorialDirector.ReceiveRebuildDuelHand(Sender, message);
    }

    [MessageHandler(typeof(TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS))]
    private void ReceiveTutorialGrantPips(TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS message) {
        if (!_isActive) {
            return;
        }

        _tutorialDirector.ReceiveGrantPips(Sender, message);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE))]
    private void ReceiveCombatMove(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE message) {
        // Find which sub circle this is.
        // CLASSIC: a move from an actor not in this duel is dropped; throwing restarted the duel's actor (security
        // audit 2026-10-04).
        var caster = SubCircles.FirstOrDefault(x => x.ParticipantActor == message.Actor);
        if (caster is null) {
            Logger.Warning("Duel {0} | Combat move received from an actor that is not in the duel; dropped.",
                Logger.Args(Duel.m_duelID.Full));

            return;
        }

        // Tutorial duels script the golems' moves server-side; drop their own AI moves so a pass cannot
        // overwrite the scripted attack. Player moves still flow through normally.
        if (_tutorialDirector.IsActive && caster.OccupiedTeam == CombatTeam.Monster) {
            return;
        }

        // CLASSIC: a henchman's moves come from the ally brain (ReceiveHenchmanTurn), never from its creature AI.
        if (caster.IsHenchman && !_choosingHenchmanMove) {
            return;
        }

        if (!EnhancedGameplaySettings.Enabled) DisableAllOwnedMinionControl();
        // CLASSIC: while a Myth wizard's card window shows their minion's hand, their moves are the minion's.
        if (!caster.IsSummonedMinion && TryHandleMinionHandMove(caster, message)) {
            ReevaluateOwnedMinionPlanning();
            return;
        }
        if (caster.IsSummonedMinion && IsOwnerChosenMinionMove(caster)) {
            // CLASSIC: the owner's order stands; keep the AI's move in case the owner hands the round back to it.
            _ownedMinionHeldAiMoves[caster.ParticipantObject] = message;
            return;
        }
        if (!_awaitingCombatMoves && !(_ownedMinionEarlyFinishScheduled
            && !caster.IsSummonedMinion && _ownedMinionControl.IsOptedIn(caster.ParticipantObject))) {
            Logger.Debug("Duel {0} | Slot {1} | Received combat move while not expecting it.", // CLASSIC: a late click
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        // CLASSIC: the stock client enchants by "casting" the enchantment card at a card in its hand (an Attack or
        // Discard move whose card is an enchantment; the target names the hand card). Upstream Imlight's
        // feat/enchantments branch reads it the same way. Run it through the hand-enchant transaction.
        if (ClassicRuntime.IsActive && !caster.IsSummonedMinion
            && message.MoveType is (byte) CombatMoveType.Attack or (byte) CombatMoveType.Discard
            && IsEnchantmentCard(caster.GetSpellFromLastHand(message.SpellSelection))) {
            var enchantTarget = StockEnchantTarget(caster, message);
            Logger.Information("Duel {0} | Slot {1} | Stock enchant: card {2} onto hand card {3} (move {4}, raw target {5})",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, message.SpellSelection, enchantTarget, message.MoveType, message.RawSpellTarget));
            if (enchantTarget >= 0) HandleEnchantMove(caster, message.SpellSelection, (uint) enchantTarget);
            else SendCurrentCombatHand(caster);
            return;
        }

        if (message.MoveType == ClassicHandEnchantment.MoveType) {
            if (ClassicRuntime.IsActive) {
                HandleEnchantMove(caster, message.SpellSelection, message.SpellTarget);
            }
            return; // Hand manipulation never queues a round action or advances the phase.
        }

        var moveType = (CombatMoveType) message.MoveType;

        switch (moveType) {
            case CombatMoveType.Discard:
                HandleDiscardMove(caster, message.SpellSelection);
                break;
            case CombatMoveType.Pass:
                HandlePassMove(caster);
                break;
            case CombatMoveType.Attack:
                HandleAttackMove(caster, message.SpellSelection, message.SpellTarget);
                break;
            case CombatMoveType.Flee:
                HandleFleeAction(caster);
                break;
            case CombatMoveType.ChangeMind:
                HandleChangeMindAction(caster);
                break;
            default:
                Logger.Warning("Duel {0} | Slot {1} | Invalid combat move type: {2}",
                    Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, moveType));
                break;
        }

        _tutorialDirector.OnCombatMoveQueued();

        if (caster.IsSummonedMinion && CombatResolver.GetQueuedAction(caster) is { } fallback) {
            _ownedMinionFallbacks[caster.ParticipantObject] = fallback;
        }
        // CLASSIC: a Myth wizard who has picked their own move now picks for their minions, one at a time.
        if (!caster.IsSummonedMinion) MaybeBeginMinionHand(caster, message.MoveType);
        ReevaluateOwnedMinionPlanning();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER))]
    private void ReceivePlanningPhaseOver(COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER message) {
        if (!_isActive) {
            return;
        }

        Logger.Debug("Duel {0} | Round {1} over at {2}",
            Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, DateTime.Now.ToString("HH:mm:ss")));

        // The execution phase begins. This is where combat actions take place and we actually see spell cinematics.
        _awaitingCombatMoves = false;
        _ownedMinionEarlyFinishScheduledWas = _ownedMinionEarlyFinishScheduled;
        _ownedMinionEarlyFinishScheduled = false;
        ResetMinionHand();
        PrepareOwnedMinionExecution();
        Duel.m_duelPhase = kDuelPhase.kPhase_Execution;
        SendCombatPhase((byte) Duel.m_duelPhase);

        // Determine how long the cinematics will take.
        var cinematicTimeInSeconds = CombatResolver.ApplyQueuedCombatActions(out var actions);
        // CLASSIC: [Classic] SpellAnimationSpeed shortens the server's wait for the spell animations.
        cinematicTimeInSeconds /= (float) ClassicSettings.SpellAnimationSpeed;
        var actionExecutionTime = TimeSpan.FromSeconds(cinematicTimeInSeconds);
        Duel.m_executionPhaseTimer = (float) actionExecutionTime.TotalSeconds;

        // Serialize the combat actions and send them to the clients.
        if (!_serializer.Serialize(actions, _combatParticipantHandFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat actions for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATACTIONS {
            DuelID = SigilId,
            ActionData = buffer,
        };
        ZoneBroadcast(msg);
        if (_perfAllMovesTicks != 0 && _ownedMinionEarlyFinishScheduledWas) {
            // CLASSIC: PERF combat turnaround: the last move to the actions going out, less the 1 s early-finish delay.
            Classic.PerfMonitor.CombatTurnaround(Classic.PerfMonitor.Ms(_perfAllMovesTicks, System.Diagnostics.Stopwatch.GetTimestamp()) - 1000);
        }

        _perfAllMovesTicks = 0;

        Timers.StartSingleTimer(RESOUTION_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION(), actionExecutionTime);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION))]
    private void ReceiveRoundResolution(COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION message) {
        if (!_isActive) {
            return;
        }

        // Iterate through dead creature participants and remove them from the duel.
        // Players can be healed and therefore don't need to be removed.
        EnactActionOnSubCircles(circle => {
            // A dead minion can't be revived; remove it like an enemy.
            if ((circle.OccupiedTeam == CombatTeam.Monster || circle.IsSummonedMinion) && !circle.IsAlive && !circle.IsWizard) {
                var removeMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEATH();
                circle.ParticipantActor.Tell(removeMsg);
            }
        });

        // All spells have been called. Inform the client whether this duel continues or ends.
        Duel.m_duelPhase = kDuelPhase.kPhase_Resolution;
        _ownedMinionControl.FinishRound();
        PublishOwnedMinionSnapshots();
        SendCombatPhase((byte) Duel.m_duelPhase);

        var playersWin = AliveCreatureCount <= 0;
        var creaturesWin = AlivePlayerCount <= 0;
        if (!playersWin && !creaturesWin) {
            // Continue. Start a new round.
            Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());

            return;
        }

        EndDuel();
    }

    [MessageHandler(typeof(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL))]
    private void DespawnDuel() {
        // CLASSIC: the duel leaves the server-wide view; seats still held are let go.
        if (SubCircles is not null) {
            foreach (var held in SubCircles.Where(circle => circle is { Occupied: true, Disconnected: true })) {
                Timers.Cancel(REJOIN_TIMER_PREFIX + held.HeldCharacterId);
                ActiveDuels.Release(held.HeldCharacterId);
            }
        }

        ActiveDuels.Remove(SigilId);
        _waitingForRejoin = false;
        _isActive = false;
        _ownedMinionControl.Clear();
        _ownedMinionFallbacks.Clear();
        _ownedMinionHeldAiMoves.Clear();
        _ownedControllableSummons.Clear();
        _minionHandStages.Clear();
        _summonOrder.Clear();
        _deferredMinionOwners.Clear();

        // Minions are children of this sigil entity, which persists between fights; MSG_COMBATDEATH
        // deletes them outright.
        EnactActionOnSubCircles(circle => {
            if (circle.IsSummonedMinion && circle.ParticipantActor is not null) {
                circle.ParticipantActor.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());
            }
        });

        _renderComponent?.Disable();
        Entity.DespawnObject();
        _entitiesInRange.Clear();
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message) {
        // Find the sub circle that the client was in and remove them from the duel.
        var subCircle = SubCircles?.FirstOrDefault(x => x.ParticipantActor == Sender);
        if (subCircle is null) {
            return;
        }

        // CLASSIC: a dropped client keeps its seat for a while, so the wizard can log back in to this fight.
        if (TryHoldSeat(subCircle)) {
            return;
        }

        // Handle this as if it were the flee action.
        HandleFleeAction(subCircle);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) {
        // Find the sub circle that the client was in and remove them from the duel.
        var subCircle = SubCircles.FirstOrDefault(x => x.ParticipantActor == Sender);
        if (subCircle is null) {
            return;
        }

        // Handle this as if it were the flee action.
        HandleFleeAction(subCircle);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON))]
    private void ReceiveDeferredMinionSummon(ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON message) {
        // CLASSIC: also drop a summon if its caster fled or this seat changed occupants during the animation.
        // Health is deliberately not checked: an already cast summon keeps its existing resolution timing.
        if (!_deferredMinionOwners.Remove(message, out var owner) || !_isActive || message.Caster is null
            || Array.IndexOf(SubCircles, message.Caster) < 0 || !message.Caster.Occupied
            || !DeferredMinionOwnerMatches(message.Caster, owner)) {
            Logger.Information("Duel {0} | deferred minion summon (tid {1}) dropped, duel or caster seat changed.",
                Logger.Args(Duel.m_duelID.Full, message.CreatureTid));

            return;
        }

        SpawnAndAssignMinion(message.CreatureTid, message.Caster, controllableSummon: true);
    }

    private static bool DeferredMinionOwnerMatches(CombatDuelSubCircle caster, DeferredMinionOwner owner) {
        if (!ReferenceEquals(caster.DeferredActionIdentity, owner.OccupantIdentity)) return false;
        var originalObject = ReferenceEquals(caster.ParticipantObject, owner.Identity)
            && caster.ParticipantObject.m_globalID.Full == owner.GlobalId;
        return (originalObject && (Equals(caster.ParticipantActor, owner.Actor)
                || (caster.Disconnected && caster.IsDeferredActionContinuation(owner.OccupantIdentity, owner.CharacterId,
                    requireRejoin: false))))
            || caster.IsDeferredActionContinuation(owner.OccupantIdentity, owner.CharacterId, requireRejoin: true);
    }

    private void InitializeDuel(Dictionary<IActorRef, CoreObject> startingParticipants) {
        Duel = CreateDuelWithDefaults();
        SubCircles = CreateDuelActorSubCircles(_sigilTemplate);
        CombatResolver = new Combat.CombatResolver(Duel, SubCircles);

        // Every roll of this duel comes from its seed, so the duel can be replayed.
        DuelSeed = CombatRng.NewDuelSeed();
        _rng = CombatRng.Stream(DuelSeed, CombatRng.DuelStream);
        Logger.Information("[COMBAT-SEED] Duel {0} | seed {1}", Logger.Args(Duel.m_duelID.Full, DuelSeed));

        // Determine which team goes up first. Tutorial duels override this with the golems first.
        _randomFirstTeam = DetermineFirstTeam();
        Duel.m_firstTeamToAct = (int) _randomFirstTeam;
        _tutorialDirector.OnDuelCreated(Duel);

        // When the duel is created, it must be created by two suspects: the player and the creature.
        // The creatures will always be team A and the players will always be team B. Assign the first
        // participants to their respective sub circles.
        var startingCreatureActor = startingParticipants.First(x => x.Value.m_templateID != 1);
        var startingPlayerActor = startingParticipants.First(x => x.Value.m_templateID == 1);
        var startingCreatureObject = startingCreatureActor.Value;
        var startingPlayerObject = startingPlayerActor.Value;

        var availableCreatureSubCircle = GetAvailableSubCircleTeamCreature();
        var availablePlayerSubCircle = GetAvailableSubCircleTeamPlayer();

        if (availableCreatureSubCircle == null || availablePlayerSubCircle == null) {
            Logger.Error("Failed to find available sub circles for duel {0}", Logger.Args(SigilId));

            return;
        }

        if (ElixirService.PreparesCombatSnapshots) {
            // CLASSIC: prepare the player first, then preserve creature->player notification/entrance order.
            if (!AssignParticipantToSubCircle(availablePlayerSubCircle, startingPlayerActor.Key, startingPlayerObject,
                deferNotification: true)) return;
            try {
                if (!AssignParticipantToSubCircle(availableCreatureSubCircle, startingCreatureActor.Key, startingCreatureObject)
                    || !availablePlayerSubCircle.PublishAssignedParticipantNotice()) {
                    availableCreatureSubCircle.RemoveParticipant();
                    availablePlayerSubCircle.RefusePreparedAdmission();
                    return;
                }
            }
            catch {
                availableCreatureSubCircle.RemoveParticipant();
                availablePlayerSubCircle.RefusePreparedAdmission();
                return;
            }
        }
        else {
            AssignParticipantToSubCircle(availableCreatureSubCircle, startingCreatureActor.Key, startingCreatureObject);
            AssignParticipantToSubCircle(availablePlayerSubCircle, startingPlayerActor.Key, startingPlayerObject);
        }

        _isActive = true;
        _startedUtc = DateTime.UtcNow; // CLASSIC
        PublishActiveDuel();

        Logger.Debug("Duel {0} | Created. Grace period over in {1}",
            Logger.Args(Duel.m_duelID.Full, DUEL_GRACE_PERIOD_IN_SECONDS));
    }

    private Duel CreateDuelWithDefaults() {
        var duel = CreateDuelWithPvEDefaults();
        if (_pvp) {
            ApplyPvpDuelSettings(duel); // CLASSIC
        }

        return duel;
    }

    private Duel CreateDuelWithPvEDefaults() => new() {
        m_duelID = SigilId,
        m_planningTimer = PLANNING_TIME,

        m_scalarDamage = _sigilTemplate.m_scalarDamagePvE,
        m_scalarResist = _sigilTemplate.m_scalarResistPvE,
        m_scalarPierce = _sigilTemplate.m_scalarPiercePvE,
        m_damageLimit = _sigilTemplate.m_damageLimitPvE,
        m_dK0 = _sigilTemplate.m_dK0PvE,
        m_dN0 = _sigilTemplate.m_dN0PvE,
        m_resistLimit = _sigilTemplate.m_resistLimitPvE,
        m_rK0 = _sigilTemplate.m_rK0PvE,
        m_rN0 = _sigilTemplate.m_rN0PvE,
        m_flatParticipantList = [],
        m_duelModifier = new DuelModifier() {
            m_battlefieldEffects = [],
            m_combatTriggers = [],
            m_gameEffects = [],
        }
    };

    private CombatDuelSubCircle[] CreateDuelActorSubCircles(CombatSigilTemplate template) {
        var subCircles = template.m_subCircles;
        var subCircleObjs = new CombatDuelSubCircle[8];
        var sigilOrientation = Entity.ActiveGameObject.m_orientation;
        var sigilLocation = Entity.ActiveGameObject.m_location;

        // The sigil rotation is stored between -pi and pi. We need it to be between 0 and 2pi.
        var sigilRotation = sigilOrientation.Z;
        if (sigilRotation < 0) {
            sigilRotation = (2 * MathF.PI) + sigilRotation;
        }

        for (int i = 0; i < subCircles.Count; i++) {
            var rotation = subCircles[i].m_rotation;
            var radius = subCircles[i].m_radius;
            var color = subCircles[i].m_color;
            var rotationRadians = rotation * (MathF.PI / 180f);

            // The sigil is rotated by some degree. We need to find our x and y coordinates based on this rotation.
            var rotatedX = radius * MathF.Cos(rotationRadians - sigilRotation);
            var rotatedY = radius * MathF.Sin(rotationRadians - sigilRotation);
            var x = sigilLocation.X + rotatedX;
            var y = sigilLocation.Y + rotatedY;
            var rotatedSigilPos = new Vector3(x, y, sigilLocation.Z);

            // Now we know where the sigil is located, we need to calculate the facing direction of the sub circle.
            // Calculate the direction vector towards the center of the duel (only Z-axis in radians)
            var duelCenter = new Vector3(sigilLocation.X - x, sigilLocation.Y - y, 0);
            var faceTowardsYaw = MathF.Atan2(duelCenter.Y, duelCenter.X);
            // The yaw must be between 0 and 2PI. It must also be reversed as the client rotates clockwise.
            // The translation isn't perfect because of Gamebyro engine bullshit. We need to compensate for this.
            faceTowardsYaw = (2 * MathF.PI) - faceTowardsYaw - YAW_ERROR_COMPENSATION;
            if (faceTowardsYaw < 0) {
                faceTowardsYaw += 2 * MathF.PI;
            }

            // Cretae the sub circle object and add it to the array.
            var subCircle = new CombatDuelSubCircle(this, radius, rotation, color, i) {
                // CLASSIC: open PvP seats wizards on both halves; the first half is the other team.
                PvpTeam = _pvp ? (i < 4 ? CombatTeam.Monster : CombatTeam.Player) : null,
                WorldPosition = rotatedSigilPos,
                WorldRotation = faceTowardsYaw,
                SlotName = subCircles[i].m_locationPreference,
                SlotType = subCircles[i].m_locationType == "MonsterCircle" ? CombatSlotType.Creature : CombatSlotType.Player
            };
            subCircleObjs[i] = subCircle;
        }

        return subCircleObjs;
    }

    private void EnactActionOnSubCircles(Action<CombatDuelSubCircle> action) {
        foreach (var subCircle in ActiveSubCircles) {
            action(subCircle);
        }
    }

    private CombatDuelSubCircle GetAvailableSubCircleTeamCreature() {
        for (int i = 0; i < 4; i++) {
            if (!SubCircles[i].Occupied) {
                return SubCircles[i];
            }
        }

        return null;
    }

    private CombatDuelSubCircle GetAvailableSubCircleTeamPlayer() {
        for (int i = 4; i < 8; i++) {
            if (!SubCircles[i].Occupied) {
                return SubCircles[i];
            }
        }

        return null;
    }

    private bool AssignParticipantToSubCircle(CombatDuelSubCircle subCircle, IActorRef actorRef, CoreObject coreObject,
                                              bool isSummonedMinion = false, int minionOwnerSubCircle = 0,
                                              bool deferNotification = false) {
        if (subCircle.ParticipantActor != null) {
            return false;
        }

        return subCircle.AssignParticipant(actorRef, coreObject, isSummonedMinion, minionOwnerSubCircle, deferNotification) is not null;
    }

    internal void SummonMinion(uint creatureTid, CombatDuelSubCircle caster) {
        if (caster is null || !caster.Occupied || caster.ParticipantActor is null
            || Array.IndexOf(SubCircles, caster) < 0) {
            return;
        }

        if (CoreObjectFactory.GetCoreTemplate(creatureTid) is null) {
            Logger.Warning("Duel {0} | minion summon: no template for creature tid {1}.",
                Logger.Args(Duel.m_duelID.Full, creatureTid));

            return;
        }

        // Cinematics before this cast plus the summon animation: the minion appears mid-cast.
        var castOffset = CurrentActionCinematicOffsetSeconds;
        var spawnDelay = castOffset + MINION_SUMMON_ANIMATION_DELAY;
        var message = new ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON { CreatureTid = creatureTid, Caster = caster };
        _deferredMinionOwners.Add(message, new DeferredMinionOwner(caster.ParticipantObject,
            caster.ParticipantObject.m_globalID.Full, caster.ParticipantActor, caster.DeferredActionIdentity,
            caster._wizard?.CharId ?? 0));
        Timers.StartSingleTimer(
            $"minionSummon_{Guid.NewGuid():N}",
            message,
            TimeSpan.FromSeconds(spawnDelay));
    }

    internal static void OnMinionRemoved(CombatDuelSubCircle circle) {
        var identity = circle.ParticipantObject;
        circle.RemoveParticipant();
        circle._duelActor.ForgetOwnedMinion(circle, identity);
    }

    // CLASSIC: returns the minion's slot (null when none joined), so a hired henchman can be set up after it joins.
    private CombatDuelSubCircle SpawnAndAssignMinion(uint creatureTid, CombatDuelSubCircle caster, bool controllableSummon = false) {
        // CLASSIC: PvP minions occupy their caster's physical half, which the creature AI uses for enemy targets.
        // PvE summons retain the existing player-half selection.
        var slot = _pvp && caster.SlotIndex < 4
            ? GetAvailableSubCircleTeamCreature()
            : GetAvailableSubCircleTeamPlayer();
        if (slot is null) {
            Logger.Information("Duel {0} | minion summon (tid {1}) skipped, no free summon-team slot.",
                Logger.Args(Duel.m_duelID.Full, creatureTid));

            return null;
        }

        var template = CoreObjectFactory.GetCoreTemplate(creatureTid);
        if (template is null) {
            Logger.Warning("Duel {0} | minion summon: no template for creature tid {1}.",
                Logger.Args(Duel.m_duelID.Full, creatureTid));

            return null;
        }

        var centre = Entity.ActiveGameObject?.m_location ?? (caster?.ParticipantObject?.m_location ?? default);
        var info = new CoreObjectInfo {
            m_templateID = creatureTid,
            m_location = centre,
            m_fScale = 1.0f,
        };
        var minionObj = CoreObjectFactory.FinalizeCoreObject(info, template);
        minionObj = CoreObjectFactory.InitializeCoreObjectBehaviors(minionObj, template);

        var minionActor = Entity.SpawnCombatMinionActor(minionObj, template);
        if (minionActor is null) {
            return null;
        }

        try {
            AssignParticipantToSubCircle(slot, minionActor, minionObj, isSummonedMinion: true,
                                         minionOwnerSubCircle: caster.SlotIndex);
            if (controllableSummon) RegisterOwnedMinionForControl(slot, caster);
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | minion summon: failed to assign tid {1} to slot {2}: {3}",
                Logger.Args(Duel.m_duelID.Full, creatureTid, slot.SlotIndex, ex));
            slot.RemoveParticipant();
            minionActor.Tell(PoisonPill.Instance);

            return null;
        }

        Logger.Information("Duel {0} | summoned minion tid {1} into summon-team slot {2} (caught up next round).",
            Logger.Args(Duel.m_duelID.Full, creatureTid, slot.SlotIndex));

        return slot;
    }

    private void AddParticipant(CoreObject participantObject, IActorRef participantActor) {
        var isPlayer = participantObject.m_templateID == 1;

        var alreadyInDuel = SubCircles.Any(x => x.ParticipantObject == participantObject);
        if (alreadyInDuel) {
            return;
        }

        var subCircle = isPlayer ? GetAvailableSubCircleTeamPlayer() : GetAvailableSubCircleTeamCreature();
        if (subCircle is null) {
            Logger.Warning("Duel {0} | No available sub circles for participant {1}",
                Logger.Args(Duel.m_duelID.Full, participantObject.m_globalID));

            return;
        }

        if (!AssignParticipantToSubCircle(subCircle, participantActor, participantObject)) return;
        PublishActiveDuel(); // CLASSIC
        // CLASSIC: a wizard walking into a duel that waits for dropped wizards gets it going again (zombie duel fix).
        if (isPlayer && _isActive) {
            ResumeIfNoLongerWaiting();
        }

        Logger.Debug("Duel {0} | Slot {1} | Participant {2} joined",
            Logger.Args(Duel.m_duelID.Full, subCircle.SlotIndex, participantObject.m_debugName));
    }

    private void SendCombatPhase(byte phase, IActorRef recipient = null) {
        // Determine which sigil slot the client should point its turn indicator at.
        var upFirstSigilSlot = GetUpFirstSigilSlot();

        // Serialize the up first data and send it to all the combat participants.
        // This is the one instance where the client sends a versionable object to the client.
        var versionableSerializer = new ObjectSerializer(
            Versionable: true,
            Behaviors: SerializerFlags.None
        );

        var upFirst = new UpFirstData {
            m_resultType = 122, // Always recorded as 122, per packet captures.
            m_upFirst = upFirstSigilSlot,
            m_roundNum = Duel.m_roundNum,
        };
        if (!versionableSerializer.Serialize(upFirst, _upFirstFlags, out var upFirstData)) {
            Logger.Error("Failed to serialize up first data for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE() {
            DuelID = SigilId,
            NewPhase = phase,
            PlayerID = 0, // Always recorded as 0
            Data = phase == 1 ? upFirstData : "",
        };

        if (recipient is not null) recipient.Tell(msg);
        else DuelBroadcast(msg);
    }

    private void SendUpFirst(int roundNum) {
        var upFirstSigilSlot = GetUpFirstSigilSlot();

        var upFirstMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATUPFIRST {
            DuelID = SigilId,
            RoundNum = (ushort) roundNum,
            FirstTeamToAct = (byte) Duel.m_firstTeamToAct,
            UpFirst = upFirstSigilSlot,
        };
        ZoneBroadcast(upFirstMsg);
    }

    private void SendCombatUI(byte planningPhaseTimer) {
        var combatUiMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI {
            DuelID = SigilId
        };
        var planningMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SETPLANNINGPHASETIMER {
            DuelID = SigilId,
            Time = planningPhaseTimer,
        };

        // CLASSIC: in open PvP both teams are wizards.
        if (_pvp) {
            WizardBroadcast(combatUiMsg);
            WizardBroadcast(planningMsg);

            return;
        }

        PlayerBroadcast(combatUiMsg);
        PlayerBroadcast(planningMsg);
    }

    private void SendCombatStats() => EnactActionOnSubCircles(circle => {
        // Serialize participant stats and send them to the participant, locally.
        var participantStats = circle.ParticipantGameStats;
        var participantCombatsStats = participantStats?.GetCombatGameStats();
        if (participantCombatsStats is null) {
            Logger.Error("Failed to get combat stats for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        if (!_serializer.Serialize(participantCombatsStats, _combatParticipantStatFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat stats for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATSTATS {
            DuelID = SigilId,
            PartID = circle.ParticipantObject.m_globalID,
            StatsData = buffer,
        };

        DuelBroadcast(msg);
    });

    private void SendCombatHand() {
        // Serialize the combat hand and send it to the participant, locally.
        // We're skipping creatures for now.
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster && !circle.IsWizard) { // CLASSIC: PvP wizards on that team
                return;
            }

            var newHand = circle.DrawHand();

            if (!_serializer.Serialize(newHand, _combatParticipantHandFlags, out var buffer)) {
                Logger.Error("Failed to serialize combat hand for duel {0}",
                    Logger.Args(SigilId));

                return;
            }

            var participantActor = circle.ParticipantActor;
            var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
                DeckCount = DeckCounter(circle.AvailableSpells),
                TotalDeckCount = DeckCounter(circle.TotalSpells),
                TreasureCardCount = DeckCounter((uint) circle.VaultRemainingCount),
                ParticipantID = circle.ParticipantObject.m_globalID,
                HandData = buffer,
            };

            participantActor.Tell(msg);
        });
    }

    /// <summary>
    /// MSG_COMBATHAND's counters are USHRT; the client's GUI_DeckCounter shows DeckCount (CARDSREMAINING, the cards left
    /// to draw) of TotalDeckCount (CARDSTOTAL, the whole deck). DeckCount was cast to a byte, which wrapped at 256.
    /// </summary>
    internal static ushort DeckCounter(uint count) => (ushort) Math.Min(count, ushort.MaxValue);

    internal void SendCurrentCombatHand(CombatDuelSubCircle circle) {
        // As-is, no draw or refill, so a discarded slot stays visibly open for the vault draw.
        if (circle is null || (circle.OccupiedTeam == CombatTeam.Monster && !circle.IsWizard)) {
            return;
        }

        var hand = circle.GetCurrentHand();
        if (!_serializer.Serialize(hand, _combatParticipantHandFlags, out var buffer)) {
            Logger.Error("Failed to serialize current combat hand for duel {0}", Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
            DeckCount = DeckCounter(circle.AvailableSpells),
            TotalDeckCount = DeckCounter(circle.TotalSpells),
            TreasureCardCount = DeckCounter((uint) circle.VaultRemainingCount),
            ParticipantID = circle.ParticipantObject.m_globalID,
            HandData = buffer,
        };

        circle.ParticipantActor.Tell(msg);
    }

    internal void SendCombatPips() {
        var pips = new CombatPipListObj {
            m_pipList = new List<ParticipantPipData>(),
            m_duelID = SigilId
        };

        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel || !circle.IsAlive) {
                return;
            }

            var genericPips = circle.CombatParticipant.m_pipCount.m_genericPips;
            var powerPips = circle.CombatParticipant.m_pipCount.m_powerPips;
            var participantPipData = new ParticipantPipData {
                m_acq = 1,
                m_partID = (GID) circle.ParticipantObject.m_globalID,
                m_pips = new PipCount() {
                    m_genericPips = genericPips,
                    m_powerPips = powerPips,
                }
            };
            pips.m_pipList.Add(participantPipData);
        });

        // Serialize the combat pips and send it to each participant.
        if (!_serializer.Serialize(pips, _combatParticipantStatFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat pips for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS {
            DuelID = SigilId,
            PipData = buffer,
        });
    }

    private void SendCombatHealth() {
        var healthList = new CombatHealthListObj {
            m_healthList = new List<ParticipantParameter>(),
            m_duelID = SigilId
        };

        // Iterate through each sub circle and add the participant's health to the list.
        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel || !circle.IsAlive) {
                return;
            }

            var participantHealth = new ParticipantParameter {
                m_data = (uint) circle.ParticipantGameStats.m_currentHitpoints,
                m_partID = (GID) circle.ParticipantObject.m_globalID,
            };
            healthList.m_healthList.Add(participantHealth);
        });

        // Serialize the combat health and send it to each participant.
        if (!_serializer.Serialize(healthList, _combatParticipantStatFlags, out var healthBuffer)) {
            Logger.Error("Failed to serialize combat health for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHEALTH {
            DuelID = SigilId,
            HealthData = healthBuffer,
        };
        ZoneBroadcast(msg);
    }

    private void SendCombatMoveSelection(ulong participantId, byte moveType, Spell spell, byte targetIndex) {
        byte isItemCard = (byte) (spell?.m_itemCard ?? false ? 1 : 0);
        byte isTreasureCard = (byte) (spell?.m_treasureCard ?? false ? 1 : 0);
        byte isBattleCard = (byte) (spell?.m_battleCard ?? false ? 1 : 0);

        var actualIndex = (byte) Math.Pow(2, targetIndex);

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVESELECTION {
            DuelID = SigilId,
            ParticipantID = participantId,
            MoveType = moveType,
            SpellID = (int) (spell?.m_templateID ?? 0),
            EnchantmentID = (int) (spell?.m_enchantment ?? 0),
            SpellTargetIndex = actualIndex,
            IsItemCard = isItemCard,
            IsTreasureCard = isTreasureCard,
            IsBattleCard = isBattleCard,
        };

        // CLASSIC: in open PvP a pick is shown only to the picker's own side.
        if (_pvp) {
            var team = SubCircles.FirstOrDefault(c => c is { Occupied: true } && c.ParticipantObject.m_globalID == participantId)?.OccupiedTeam;
            EnactActionOnSubCircles(circle => {
                if (circle.IsWizard && circle.OccupiedTeam == team) {
                    circle.ParticipantActor.Tell(msg);
                }
            });

            return;
        }

        PlayerBroadcast(msg);
    }

    /// <summary>CLASSIC: a card that only changes another card (an enchantment, an Extract Animus card).</summary>
    private static bool IsEnchantmentCard(Spell spell) {
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template
            || template.m_effects is not { Count: > 0 }) return false;
        return string.Equals(template.m_sTypeName, "Enchantment", StringComparison.OrdinalIgnoreCase)
               || template.m_effects.All(e => e?.m_effectTarget is kEffectTarget.kSpell or kEffectTarget.kSpecificSpells);
    }

    /// <summary>CLASSIC: which hand card a stock enchant move names: the raw target, its decoded bit, or the bit index.</summary>
    private static int StockEnchantTarget(CombatDuelSubCircle caster, COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE message) {
        var hand = caster._combatDeck?.LastGivenHand;
        var source = caster.GetSpellFromLastHand(message.SpellSelection);
        if (hand is null || source is null) return -1;
        var raw = message.RawSpellTarget;
        var bit = raw > 0 && (raw & (raw - 1)) == 0 ? (int) Math.Log2(raw) : -1;
        foreach (var candidate in new[] { (int) Math.Min(raw, int.MaxValue), (int) Math.Min(message.SpellTarget, int.MaxValue), bit }) {
            if (candidate < 0 || candidate >= hand.Count || candidate == message.SpellSelection) continue;
            if (ClassicHandEnchantment.TryPrepare(source, hand[candidate], out _, out _, caster._duelActor.Duel.m_bPVP)) return candidate;
        }

        return -1;
    }

    private void HandleEnchantMove(CombatDuelSubCircle caster, int sourceIndex, uint targetIndex) {
        RunMonstrologyEnchantment(caster, sourceIndex, targetIndex, () => {
            // A queued cast owns its selected card until ChangeMind. Do not invalidate its references.
            // CLASSIC: October Cloak uses the same validated hand transaction on either player's PvP side.
            var pvpCloak = Duel.m_bPVP && ClassicHandEnchantment.AllowsPvpEnchantment(caster.GetSpellFromLastHand((byte) sourceIndex));
            if ((!Duel.m_bPVP || pvpCloak) && caster.AddedToDuel && caster.IsAlive && !caster.IsSummonedMinion
                && (caster.OccupiedTeam == CombatTeam.Player || pvpCloak)
                && CombatResolver.GetQueuedAction(caster) is null
                && caster._combatDeck.TryEnchant(sourceIndex, targetIndex, out var consumedId, Duel.m_bPVP)
                && consumedId != 0 && caster._wizard is { } wizard) {
                // CLASSIC: only the deck's copy (it left the book when it went into the deck); see DoSpellCastConsequences.
                var deckSlot = wizard.EquipmentBehavior.SlotList.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
                if (deckSlot?.ItemId is { } deckId) {
                    wizard.ConsumeDeckTreasureCard(consumedId, deckId); // CLASSIC: from the deck's Treasure Card ledger
                    if (CoreObjectFactory.GetCoreTemplate(consumedId) is SpellTemplate template) {
                        caster.ParticipantActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK {
                            SpellID = (int) StringHash.Compute(template.m_name), EnchantmentID = 0,
                            DeckID = deckId, Success = 1, Destroy = 1, // spent: not back into the client's book
                        });
                    }
                }
            }
        });
        SendCurrentCombatHand(caster); // authoritative response on success or rejection; never refill
    }

    private void HandleDiscardMove(CombatDuelSubCircle caster, int spellSelection) {
        var spell = caster.GetSpellFromLastHand((byte) spellSelection);
        if (spell is null) {
            Logger.Warning("Duel {0} | Slot {1} | Discard received for an empty hand slot ({2}).",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spellSelection));

            return;
        }

        if (!caster.DiscardCard(spell)) {
            // CLASSIC: a treasure card cannot be discarded (2009: it leaves only when cast or deleted from the book). The
            // r806919 client blocks a discard only for m_noDiscard templates (GUI2_NoDiscardSpell) and has no failure
            // reply for a refused one, so the authoritative answer is the hand as it stands, the card still in it.
            Logger.Debug("Duel {0} | Slot {1} | Discard of treasure card {2} refused.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID));
            SendCurrentCombatHand(caster);
            return;
        }

        Logger.Debug("Duel {0} | Slot {1} | Discarded a card: {2}",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID.ToString() ?? "None"));

        // Re-send as-is so the freed slot stays open; SendCombatHand would refill it from the deck.
        SendCurrentCombatHand(caster);
    }

    private void HandlePassMove(CombatDuelSubCircle caster) {
        // If the participant passes, we don't need to know what spell they were casting.
        CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);

        if (caster.OccupiedTeam == CombatTeam.Player || caster.IsWizard) { // CLASSIC: PvP
            SendCombatMoveSelection(caster.ParticipantObject.m_globalID, (byte) CombatMoveType.Pass, null, 0);
        }
    }

    private void HandleAttackMove(CombatDuelSubCircle caster, int spellSelection, uint spellTarget) {
        var spell = caster.GetSpellFromLastHand((byte) spellSelection);
        if (spell is null) {
            Logger.Warning("Duel {0} | Slot {1} | Attack received for an empty hand slot ({2}).",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spellSelection));

            CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);

            return;
        }

        if (!AllowsMonstrologyCast(caster, spell)) {
            CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);
            return;
        }

        if (!caster.HasPipsForSpell(spell)) {
            Logger.Debug("Duel {0} | Slot {1} | Participant does not have enough pips for spell {2}", // CLASSIC: validation working
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID));

            CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);

            // CLASSIC: a card the caster cannot pay for is not cast.
            if (ClassicRuntime.IsActive) {
                return;
            }
        }

        // Find the target sub circle. Valid targets are 1-8.
        // If the spell doesn't have a target like for AoE spells or self-heals,
        // the value will be the integer cap.
        var target = caster;
        // The client sends raw sigil slots for spell targets (see GetUpFirstSigilSlot). CLASSIC: an empty circle is no
        // target (the caster, as for a card without one); the client only offers wizards and creatures (security audit).
        var targetSlot = Imlight.Classic.Security.CombatTargets.Resolve(spellTarget, SubCircles.Length,
            slot => SubCircles[slot].Occupied || SubCircles[slot].CombatParticipant is not null);
        if (targetSlot >= 0) {
            target = SubCircles[targetSlot];
        }
        else if (spellTarget < SubCircles.Length) {
            Logger.Warning("Duel {0} | Slot {1} | Spell target {2} is an empty circle; cast on the caster instead.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spellTarget));
        }

        // CLASSIC: a card the classic values made single-target (Orthrus) is cast by a client without the card overlay
        // with no target, and a card they made area or self may still carry one; the card's own effects decide.
        if (ClassicRuntime.IsActive) {
            target = ClassicCastTarget(spell, caster, target);
        }

        CombatResolver.AddCombatMove(CombatMoveType.Attack, caster, target, spell);
        if (ClassicRuntime.IsActive && CombatResolver.GetQueuedAction(caster) is { } queued) {
            queued.SpellTemplate = caster._combatDeck.CastTemplateFor(spell, queued.SpellTemplate);
        }

        // Minions are AI-driven; their telegraph comes from ResendMinionMoveSelections, not an echo.
        if (!caster.IsSummonedMinion && (caster.OccupiedTeam == CombatTeam.Player || caster.IsWizard)) { // CLASSIC: PvP
            SendCombatMoveSelection(caster.ParticipantObject.m_globalID, (byte) CombatMoveType.Attack, spell, (byte) spellTarget);
        }
    }

    private CombatDuelSubCircle ClassicCastTarget(Spell spell, CombatDuelSubCircle caster, CombatDuelSubCircle target) {
        if (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template || template.m_effects is null) {
            return target;
        }

        var side = CombatActionResolver.CardSide(template);
        if (side == CastTargetSide.None) {
            return target;
        }

        static CastCircle CircleOf(CombatDuelSubCircle circle)
            => new(circle.SlotIndex, (int) circle.OccupiedTeam, circle.IsAlive && circle.AddedToDuel);

        var circles = SubCircles.Where(circle => circle.Occupied).Select(CircleOf).ToList();
        var slot = CastTargeting.Choose(side, CircleOf(caster), CircleOf(target), circles);
        if (slot == target.SlotIndex) {
            return target;
        }

        Logger.Debug("Duel {0} | Slot {1} | Classic target for spell {2}: slot {3} instead of {4}.",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID, slot, target.SlotIndex));

        return SubCircles.First(circle => circle.SlotIndex == slot);
    }

    private void HandleChangeMindAction(CombatDuelSubCircle caster) {
        // Send the action director a null spell to indicate that the participant has changed their mind.
        CombatResolver.AddCombatMove(CombatMoveType.ChangeMind, caster, null, null);

        // Echo the change mind action to each player participant
        if (caster.OccupiedTeam == CombatTeam.Player || caster.IsWizard) { // CLASSIC: PvP
            SendCombatMoveSelection(caster.ParticipantObject.m_globalID, (byte) CombatMoveType.ChangeMind, null, 0);
        }
    }

    private void HandleFleeAction(CombatDuelSubCircle caster) {
        // CLASSIC: leaving an open PvP fight costs nothing; the other side wins once one side is empty.
        if (_pvp && caster.IsWizard) {
            DisableOwnedMinionControl(caster.ParticipantObject);
            ArenaMarkFled(caster); // CLASSIC: fleeing an arena match is a loss
            PvpReleaseSeat(caster, won: false, fought: !_pvpLobby);
            if (_pvpLobby) {
                if (PvpSeats() == (0, 0)) {
                    PvpClose("everyone left");
                }
            }
            else if (AliveAndInDuelPlayerCount <= 0 || AliveAndInDuelCreatureCount <= 0) {
                EndDuel();
            }
            else {
                PvpPublish();
            }

            return;
        }

        DisableOwnedMinionControl(caster.ParticipantObject);
        var actor = caster.ParticipantActor;
        var participantObjId = caster.ParticipantObject.m_globalID;

        // Fleeing drains the player's mana; creature participants have no wizard.
        if (caster._wizard is not null) {
            var clientMaxMana = caster._wizard.GameStats.GetClientTypeAlternative().m_baseMana;
            caster._wizard.UpdateMana(0);
            actor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
                Mana = 0,
                MaxMana = clientMaxMana,
            });
        }

        // Inform the client that they've been removed from this duel.
        var defeatMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT();
        actor.Tell(defeatMsg);

        caster.RemoveParticipant();

        Logger.Debug("Duel {0} | Slot {1} | Participant fled",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex));

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
            DuelID = SigilId,
            ParticipantID = participantObjId,
        });

        // If no more players are left in the duel because of this, end the duel.
        if (AliveAndInDuelPlayerCount <= 0) {
            EndDuel();
        }
        else {
            PublishActiveDuel(); // CLASSIC
            NotifyAmbientWizards(); // CLASSIC: a slot opened.
        }
    }

    private CombatTeam DetermineFirstTeam()
        => (CombatTeam) Rng.Next(0, 2);

    private CombatTeam _randomFirstTeam;

    // CLASSIC: the side that acts first is set once, when combat starts (the first round, after the join grace
    // period), and never changes: the wizards if four of them are in the duel then, else the duel's random side
    // (owner ruling 2026-09-28).
    private void ApplyFullTeamGoesFirst() {
        if (!ClassicRuntime.IsActive || IsScriptedDuel() || Duel.m_roundNum != 1 || _pvp) {
            return;
        }

        // CLASSIC: ambient wizards are not real wizards for this rule (owner, 2026-10-01).
        var wizards = SubCircles.Count(circle => circle is { Occupied: true, AddedToDuel: true, IsSummonedMinion: false }
            && circle.OccupiedTeam == CombatTeam.Player && !AmbientWizards.IsAmbient(circle.ParticipantActor));
        var first = wizards >= 4 ? CombatTeam.Player : _randomFirstTeam;
        Duel.m_firstTeamToAct = (int) first;
        Logger.Debug("Duel {0} | combat starts with {1} wizards: team {2} acts first for the whole duel.",
            Logger.Args(Duel.m_duelID.Full, wizards, first));
    }

    private Random _rng;

    /// <summary>The seed every roll of the current duel derives from (logged as [COMBAT-SEED]).</summary>
    internal ulong DuelSeed { get; private set; }

    /// <summary>The duel's own rolls: first team, accuracy, random effects, criticals, power pips.</summary>
    internal Random Rng => _rng ??= CombatRng.Stream(DuelSeed, CombatRng.DuelStream);

    /// <summary>A separate stream of this duel's seed, for one slot's deck or AI.</summary>
    internal Random StreamFor(int stream) => CombatRng.Stream(DuelSeed, stream);

    internal bool IsScriptedDuel()
        => _tutorialDirector?.IsActive == true;

    private byte GetUpFirstSigilSlot() {
        // Prefer the acting team's first living participant, else any living participant.
        var upFirst = SubCircles.FirstOrDefault(s => s is not null && s.AddedToDuel && s.IsAlive
            && s.SlotType == (Duel.m_firstTeamToAct == (int) CombatTeam.Player
                ? CombatSlotType.Player
                : CombatSlotType.Creature))
            ?? SubCircles.FirstOrDefault(s => s is not null && s.AddedToDuel && s.IsAlive);
        if (upFirst is null) {
            return 0;
        }

        // The modern client resolves the indicator against its sigil slots, not a list index.
        return (byte) upFirst.SlotIndex;
    }

    private void AddWaitingCombatParticipants() => EnactActionOnSubCircles(AddCircleToCombat);

    // CLASSIC: one circle's MSG_COMBATADD, so a henchman hired during card selection joins the round it was hired in.
    private void AddCircleToCombat(CombatDuelSubCircle circle) {
        if (circle.AddedToDuel || !circle.Occupied) {
            return;
        }

        // Serialize the combat participant and send it to every client in the zone.
        var participant = circle.CombatParticipant;
        if (!_serializer.Serialize(participant, _combatParticipantFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat participant for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATADD {
            DuelID = SigilId,
            ParticipantData = buffer,
        };
        ZoneBroadcast(msg);

        Logger.Debug("Duel {0} | Slot {1} | Serialized participant sent",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex));

        circle.AddedToDuel = true;
        Duel.m_flatParticipantList.Add(participant);
    }

    private void DoPipGain() => EnactActionOnSubCircles(circle => {
        if (!circle.AddedToDuel || !circle.IsAlive) {
            return;
        }

        circle.DoPipGain();
    });

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATINSTAWIN))]
    private void ReceiveCheatInstaWin(COMBAT_106_PROTOCOL.MSG_CHEATINSTAWIN message) {
        if (!_isActive) {
            return;
        }

        // EndDuel derives the winner from the alive creature count, so zero them first.
        Timers.CancelAll();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster) {
                circle.DamageParticipant(int.MaxValue);
            }
        });

        EndDuel();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATINSTANTCINEMATICS))]
    private void ReceiveCheatInstantCinematics(COMBAT_106_PROTOCOL.MSG_CHEATINSTANTCINEMATICS message)
        => CheatInstantCinematics = message.Enabled;

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATNOFIZZLE))]
    private void ReceiveCheatNoFizzle(COMBAT_106_PROTOCOL.MSG_CHEATNOFIZZLE message) {
        var subCircle = SubCircles.FirstOrDefault(x => x.ParticipantActor == message.Actor);
        if (subCircle is null) {
            return;
        }

        subCircle.CheatNoFizzle = message.Enabled;
    }

    private void EndDuel() {
        // CLASSIC: an open PvP fight ends with no rewards and no penalty.
        if (_pvp) {
            PvpEndDuel();

            return;
        }

        // The duel has ended. Inform the clients of the result.
        var playersWin = AliveAndInDuelCreatureCount <= 0;
        var creaturesWin = AliveAndInDuelPlayerCount <= 0;

        // CLASSIC: who was down when the fight ended, taken before anything (a defeated wizard's session sets 1 health
        // once it hears of the defeat). Owner ruling 2026-10-10: they get no rewards from a fight their side wins.
        var unrewarded = DefeatedWithoutRewards();

        FinishMonstrologyDuel(playersWin, unrewarded);
        AwardMonstrologyExtractions(); // CLASSIC: Animus and Monstrology XP for the winners' extractions

        // A queued tutorial card grant must not leak into the next duel on this sigil.
        _tutorialDirector.OnDuelEnded();

        // CLASSIC: wizards still away when the fight ends lose their seat (no rewards; a defeat sends them home).
        foreach (var held in SubCircles.Where(circle => circle is { Occupied: true, Disconnected: true })) {
            ReleaseHeldSeat(held);
        }

        RemovePlayersFromDuel();

        if (playersWin) {
            PlayerWin(unrewarded);
        }
        else if (creaturesWin) {
            CreatureWin();
        }

        // Broadcast to the zone of the result.
        var combatMatchResult = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT {
            DuelID = SigilId,
            WinningTeam = playersWin ? (byte) CombatTeam.Player : (byte) CombatTeam.Monster,
        };
        ZoneBroadcast(combatMatchResult);

        // Inform the zone of the final phase, and the end of the duel.
        Duel.m_duelPhase = kDuelPhase.kPhase_Ended;
        SendCombatPhase((byte) Duel.m_duelPhase);

        var duelEndedMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId };
        ZoneBroadcast(duelEndedMsg);

        DespawnDuel();
        _isActive = false;
        NotifyAmbientWizards(active: false); // CLASSIC
    }

    /// <summary>
    /// CLASSIC: [Classic] DefeatedGetNoRewards (owner ruling 2026-10-10, "take the rewards away"). The player-side
    /// circles down (0 health) when the fight ends. Their side may still win, but they get nothing from it: no
    /// MSG_COMBATWIN, so no XP, gold, Crowns, drops, reagents, Treasure Cards, kill badges, Second Chance chest or
    /// quest kill credit (CombatService and QuestService hand all of those out on that message), and no Monstrology
    /// extraction. They are still sent home as defeated (RemovePlayersFromDuel). A wizard healed back up before the
    /// end (a direct heal revives in this game, March 2009) is standing and keeps every reward. Empty when the switch
    /// is off or without a classic profile. PvP never gets here (PvpEndDuel has its own rules).
    /// </summary>
    private HashSet<CombatDuelSubCircle> DefeatedWithoutRewards() {
        var defeated = new HashSet<CombatDuelSubCircle>();
        if (!ClassicRuntime.IsActive || !ClassicSettings.DefeatedGetNoRewards) {
            return defeated;
        }

        foreach (var circle in SubCircles) {
            if (circle is { Occupied: true, OccupiedTeam: CombatTeam.Player } && !circle.IsAlive) {
                defeated.Add(circle);
            }
        }

        return defeated;
    }

    private void PlayerWin(HashSet<CombatDuelSubCircle> unrewarded) {
        Logger.Debug("Duel {0} | Duel ended. Players win.", Logger.Args(Duel.m_duelID.Full));

        Duel.m_duelPhase = kDuelPhase.kPhase_Victory;
        SendCombatPhase((byte) Duel.m_duelPhase);

        var adjectivesOfDefeatedMobs = new List<string>();
        var templateIdsOfDefeatedMobs = new List<ulong>();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster) {
                var mobTemplateId = circle.ParticipantObject.m_templateID;
                var mobTemplate = CoreObjectFactory.GetCoreTemplate(mobTemplateId);
                if (mobTemplate is null) {
                    return;
                }

                if (mobTemplate is not GameObjectTemplate gameObjectTemplate) {
                    return;
                }

                var mobAdjectives = gameObjectTemplate.m_adjectiveList;
                adjectivesOfDefeatedMobs.AddRange(mobAdjectives);
                templateIdsOfDefeatedMobs.Add(gameObjectTemplate.m_templateID);
            }
        });

        // Send the final messages to the participants.
        var combatVictoryMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATVICTORY();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster) {
                return;
            }

            circle.ParticipantActor.Tell(combatVictoryMsg);

            // CLASSIC: defeated during the fight: no rewards and no kill credit (DefeatedWithoutRewards).
            if (unrewarded.Contains(circle)) {
                Logger.Debug("Duel {0} | Slot {1} | Defeated before the win: no rewards.",
                    Logger.Args(Duel.m_duelID.Full, circle.SlotIndex));

                return;
            }

            var victoryMsg = new COMBAT_106_PROTOCOL.MSG_COMBATWIN() {
                UsedPips = circle._usedPipsForExperienceGain,
                MobAdjectives = [.. adjectivesOfDefeatedMobs],
                MobTemplateIds = [.. templateIdsOfDefeatedMobs],
            };
            circle.ParticipantActor.Tell(victoryMsg);
        });
    }

    private void CreatureWin() {
        Logger.Debug("Duel {0} | Duel ended. Creatures win.", Logger.Args(Duel.m_duelID.Full));

        // CLASSIC: the creatures that won stay in the world at full health (a dungeon keeps its guards and boss), and
        // an instanced zone resets once the defeated party has left it. Stock Imlight deleted them.
        if (ClassicRuntime.IsActive) {
            ResetCreatures();
            ReportPartyLost();
        }
        else {
            // Send combat death to all creatures anyways. This will get rid of their game object.
            var deathMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEATH();
            EnactActionOnSubCircles(circle => circle.ParticipantActor.Tell(deathMsg));
        }

        // Inform each player that they've been defeated.
        var defeatMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT();
        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel) {
                return;
            }

            circle.ParticipantActor.Tell(defeatMsg);
        });
    }

    private void RemovePlayersFromDuel() => EnactActionOnSubCircles(circle => {
        if (circle.OccupiedTeam != CombatTeam.Player) {
            return;
        }

        // Minions have no hub to return to; DespawnDuel tears them down.
        if (circle.IsSummonedMinion) {
            return;
        }

        // Send any dead players back to the hub.
        if (!circle.IsAlive) {
            var defeatMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT();
            circle.ParticipantActor.Tell(defeatMsg);

            return;
        }

        // Get the players back into the idle state, so they can move around again.
        var stateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = circle.ParticipantObject.m_globalID,
            State = (uint) NPCStates.Idle
        };
        circle.ParticipantActor.Tell(stateMsg);
    });

    // CLASSIC: Wizard City streets where a random fight had at most one enemy per player (MMORPG.com "Combat
    // Primer", 2009-01-22: "In the 'beginner' zones, you'll face a maximum of one enemy per player"). Owner
    // decision 2026-09-28: every Wizard City street is a beginner zone except Colossus Boulevard, which allows
    // one more enemy than there are players. (The article says "from that point forward" after Colossus; the
    // owner's recollection wins for Olde Town and Sunken City.) Towers and instances are not listed.
    private static readonly HashSet<string> s_classicBeginnerStreets = new(StringComparer.OrdinalIgnoreCase) {
        "WizardCity/WC_Streets/WC_Unicorn",
        "WizardCity/WC_Streets/WC_Triton",
        "WizardCity/WC_Streets/WC_Cyclops",
        "WizardCity/WC_Streets/WC_Firecat",
        "WizardCity/WC_Streets/WC_HauntedCave",
        "WizardCity/WC_Streets/WC_OldeTown",
        "WizardCity/WC_Streets/WC_Sunken_City",
    };

    private bool IsSlotAvailable(CombatTeam team) {
        var IsNewbieZone = ClassicRuntime.IsActive && s_classicBeginnerStreets.Contains(Entity.Zone?.ZonePath ?? "");
        var IsDangerousZone = false;

        // Newbie zones (like Unicorn Way) can only have 1 creature as a base.
        // Dangerous zones (like Sunken City) can have 3 creatures as a base.
        // Every player in the duel also allocates 1 more creature slot.
        var baseCreatureCount = IsNewbieZone ? 1 : IsDangerousZone ? 3 : 2;
        var maxCreatures = baseCreatureCount + (PlayerCount - 1);

        var slotAvailable = (team == CombatTeam.Player)
            ? PlayerCount < 4
            : CreatureCount < 4 && (CreatureCount < maxCreatures);

        return slotAvailable;
    }

    private void ResendMinionMoveSelections() {
        TelegraphMinionMoves();
        var delay = TimeSpan.FromSeconds(PLANNING_TIME);
        Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), delay);
    }

    // CLASSIC: shows every minion's queued move again without touching the planning timer. An owner's mid-round
    // change of a Myth minion's order used to restart the 30 s countdown (ResendMinionMoveSelections).
    private void TelegraphMinionMoves() {
        EnactActionOnSubCircles(circle => {
            if (!circle.IsSummonedMinion || !circle.IsAlive || circle.ParticipantObject is null) {
                return;
            }

            var action = CombatResolver.GetQueuedAction(circle);
            if (action is null || action.Spell is null || action.SelectedTarget is null) {
                SendCombatMoveSelection(circle.ParticipantObject.m_globalID, (byte) CombatMoveType.Pass, null, 0);
                return;
            }

            SendCombatMoveSelection(circle.ParticipantObject.m_globalID, (byte) CombatMoveType.Attack,
                action.Spell, (byte) action.SelectedTarget.SlotIndex);
        });
    }

}
