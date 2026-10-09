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
 * COMBAT PARTICIPANT CONTROLLER
 * ========================================================================
 * 
 * PURPOSE:
 * Manages individual participant data and state within a duel, providing
 * methods to modify health, mana, pips, and track spell effects on the participant.
 * 
 * USAGE EXAMPLE:
 * var subCircle = new CombatDuelSubCircle(duelActor, radius, rotation, color, index);
 * subCircle.AssignParticipant(actor, participantObject);
 * subCircle.DamageParticipant(damage);
 * 
 * NOTE:
 * Each subcircle handles deck management, spell casting costs, and
 * participant-specific stat calculations during effect resolution.
 * 
 * TODO:
 * - This should derive from ZoneEntityComponent
 * - Check if creature can be stunned
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/13/2026
 */

using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.Classic.Rules;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Collections;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Game.Combat;

internal enum CombatSlotType {
    
    Creature,
    Player

}

// CLASSIC: never serialized onto the client protocol; the native packet order is retained by CombatService.
internal sealed record CombatElixirEntryReceipt(Wizard Wizard, ImmutableArray<IMessage> Messages);

/// <summary>
/// Represents a position in the combat duel for a single participant, managing their combat state, spells, and effects.
/// </summary>
/// <remarks>
/// Each subcircle maintains a participant's combat data including their position in the duel, stats, cards, pips, 
/// and hanging effects. It provides methods to manipulate the participant's state through damage, healing, and
/// spell effects. Subcircles are assigned to either the player or monster team and positioned within the duel
/// according to the sigil template.
/// </remarks>
public class CombatDuelSubCircle {
    
    private const float AGGRO_TIME_IN_SECONDS = 0.75f;
    internal const byte MAX_PIP_COUNT = 7;
    private const byte PLAYER_HAND_SIZE = 7;

    internal string SlotName { get; set; }
    internal CombatSlotType SlotType { get; set; }
    internal int SlotIndex { get; private set; }
    internal Vector3 WorldPosition { get; set; }
    internal float WorldRotation { get; set; }
    internal IActorRef ParticipantActor { get; private set; }
    internal CoreObject ParticipantObject { get; private set; }
    internal ServerWizGameStats ParticipantGameStats { get; private set; }
    internal CombatParticipant CombatParticipant { get; private set; }
    internal bool AddedToDuel { get; set;}
    internal bool IsSummonedMinion { get; private set; }
    // CLASSIC: a henchman hired from the Crown Shop is a summoned minion of its buyer that plays a wizard's deck at
    // this level (the shop item's "Level N") with the ally brain. 0: not a henchman.
    internal int HenchmanLevel { get; private set; }
    internal bool IsHenchman => IsSummonedMinion && HenchmanLevel > 0;
    private CoreObject _minionOwnerObject;
    private CombatTeam _minionTeam = CombatTeam.Player;
    private CombatElixirEntryReceipt _pendingElixirReceipt;
    private bool _pendingParticipantNotice;
    // CLASSIC: local queued-action identity survives an authenticated hold/rejoin, but never a replacement occupant.
    private object _deferredActionIdentity = new();
    private CoreObject _continuedActionObject;
    private IActorRef _continuedActionActor;
    private ulong _continuedActionGlobalId, _continuedActionCharacterId;
    private bool _continuedActionRejoined;
    internal object DeferredActionIdentity => _deferredActionIdentity;

    private void ResetDeferredActionIdentity() {
        _deferredActionIdentity = new object();
        _continuedActionObject = null;
        _continuedActionActor = null;
        _continuedActionGlobalId = _continuedActionCharacterId = 0;
        _continuedActionRejoined = false;
    }

    private void ContinueDeferredActions(bool rejoined) {
        _continuedActionObject = ParticipantObject;
        _continuedActionActor = ParticipantActor;
        _continuedActionGlobalId = ParticipantObject?.m_globalID.Full ?? 0;
        _continuedActionCharacterId = _wizard?.CharId ?? 0;
        _continuedActionRejoined |= rejoined;
    }

    internal bool IsDeferredActionContinuation(object identity, ulong characterId, bool requireRejoin)
        => ReferenceEquals(identity, _deferredActionIdentity) && characterId != 0
            && (!requireRejoin || _continuedActionRejoined)
            && characterId == _continuedActionCharacterId && characterId == _wizard?.CharId
            && ReferenceEquals(ParticipantObject, _continuedActionObject)
            && ParticipantObject?.m_globalID.Full == _continuedActionGlobalId
            && Equals(ParticipantActor, _continuedActionActor)
            && (!Disconnected || (HeldCharacterId == characterId && Equals(ParticipantActor, ActorRefs.Nobody)));

    // Capture identity as well as slot: a replacement occupant must not inherit somebody else's minion.
    internal void CaptureMinionOwner(int ownerSlot) {
        var owner = _duelActor.SubCircles.FirstOrDefault(c => c.SlotIndex == ownerSlot && c != this && c.Occupied);
        _minionOwnerObject = owner?.ParticipantObject;
        _minionTeam = owner?.OccupiedTeam ?? CombatTeam.Player;
    }

    internal bool IsOwnedMinionOf(CombatDuelSubCircle caster)
        => IsSummonedMinion && IsAlive && caster is { IsAlive: true, Occupied: true }
            && ReferenceEquals(_duelActor, caster._duelActor)
            && _minionOwnerObject is not null && ReferenceEquals(_minionOwnerObject, caster.ParticipantObject)
            && OccupiedTeam == caster.OccupiedTeam;
    internal List<SpellEffect> _hangingEffects { get {
        if (CombatParticipant is null) {
            return null;
        }
        if (CombatParticipant is not null && CombatParticipant.m_hangingEffects is null) {
            CombatParticipant.m_hangingEffects = [];
        }

        return CombatParticipant.m_hangingEffects;
    }}
    public uint AvailableSpells {
        get {
            if (_combatDeck is null) {
                return 0;
            }

            return (uint) _combatDeck.RemainingCardCount;
        }
    }
    public uint TotalSpells {
        get {
            if (_combatDeck is null) {
                return 0;
            }

            return (uint) _combatDeck.TotalCardCount;
        }
    }
    internal bool Occupied => ParticipantObject is not null;
    internal CombatTeam OccupiedTeam {
        get {
            if (ParticipantObject is null) {
                return CombatTeam.Player;
            }

            if (IsSummonedMinion) {
                return ClassicRuntime.IsActive ? _minionTeam : CombatTeam.Player;
            }

            // CLASSIC: in an open PvP circle the wizards on the first half (slots 0-3) are the other team.
            if (PvpTeam is { } pvpTeam) {
                return pvpTeam;
            }

            return ParticipantObject.m_templateID == 1 ? CombatTeam.Player : CombatTeam.Monster;
        }
    }
    internal bool IsAlive => ParticipantGameStats?.m_currentHitpoints > 0;

    /// <summary>CLASSIC: the open PvP team of this slot (slots 0-3 Monster, 4-7 Player), or null outside PvP.</summary>
    internal CombatTeam? PvpTeam { get; set; }

    /// <summary>CLASSIC: a wizard (a player's or an ambient wizard's seat), not a creature or minion.</summary>
    internal bool IsWizard => ParticipantObject?.m_templateID == 1 && !IsSummonedMinion;

    /// <summary>
    /// Beguile (kMindControl): the number of this combatant's next actions taken for the other side.
    /// </summary>
    internal int BeguiledActions;

    /// <summary>
    /// The side this combatant acts for: its own, or the other one while beguiled.
    /// </summary>
    internal CombatTeam ActingTeam => BeguiledActions > 0
        ? OccupiedTeam == CombatTeam.Player ? CombatTeam.Monster : CombatTeam.Player
        : OccupiedTeam;
    internal bool CheatNoFizzle;
    internal CombatDeck _combatDeck;
    internal readonly CombatDuelComponent _duelActor;
    internal Wizard _wizard;
    internal int _usedPipsForExperienceGain = 0;

    private readonly float _radius;
    private readonly float _rotation;
    private readonly Color _color;

    // ctor
    internal CombatDuelSubCircle(CombatDuelComponent duelActor, float radius, float rotation, Color color, int index) {
        _duelActor = duelActor;
        _radius = radius;
        _rotation = rotation;
        _color = color;
        SlotIndex = index;
    }

    internal CombatParticipant AssignParticipant(IActorRef actor, CoreObject participantObject, bool isSummonedMinion = false,
                                                 int minionOwnerSubCircle = 0, bool deferNotification = false) {
        ResetDeferredActionIdentity();
        ParticipantActor = actor;
        ParticipantObject = participantObject;
        IsSummonedMinion = isSummonedMinion;
        HenchmanLevel = 0; // CLASSIC
        _minionOwnerObject = null;
        _minionTeam = CombatTeam.Player;
        if (isSummonedMinion && ClassicRuntime.IsActive) CaptureMinionOwner(minionOwnerSubCircle);

        var isHumanPlayer = participantObject.m_templateID == 1;
        CombatElixirEntryReceipt elixirReceipt = null;

        // Set the CombatParticipant based on what team they are.
        if (isHumanPlayer) {
            if (!InitializePlayerSubCircle(out elixirReceipt)) {
                RemoveParticipant();
                _wizard = null;
                ParticipantGameStats = null;
                _combatDeck = null;
                return null;
            }
        }
        else {
            InitializeCreatureSubCircle(isSummonedMinion, minionOwnerSubCircle);
        }

        _pendingElixirReceipt = elixirReceipt;
        _pendingParticipantNotice = true;
        if (!deferNotification) PublishAssignedParticipantNotice();
        return CombatParticipant;
    }

    // CLASSIC: the starter prepares its player before the creature, then retains the original notification order.
    internal bool PublishAssignedParticipantNotice() {
        if (!_pendingParticipantNotice || !Occupied || ParticipantActor is null) return false;
        var elixirReceipt = _pendingElixirReceipt;
        _pendingElixirReceipt = null;
        _pendingParticipantNotice = false;
        // Inform the actor that they've been added to a duel.
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {
            DuelActor = _duelActor.ActorRef,
            Duel = _duelActor,
            SubCircle = this,
            SlotPosition = WorldPosition,
            SlotOrientation = WorldRotation,
            ElixirReceipt = elixirReceipt,
        };
        ParticipantActor.Tell(msg);

        // We don't need to await this.
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        PlayEntranceAnimation(ParticipantObject, ParticipantActor);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

        return true;
    }

    // CLASSIC: a prepared player whose paired admission failed cannot resume with the changed combat offsets.
    internal void RefusePreparedAdmission() {
        var actor = ParticipantActor;
        if (_wizard is { } wizard) WizardCollection.WithCharacterLock(wizard.CharId, () => {
            WizardCollection.MarkInventorySnapshotUncertain(wizard);
            return true;
        });
        RemoveParticipant();
        _wizard = null;
        ParticipantGameStats = null;
        _combatDeck = null;
        actor?.Tell("Close");
    }

    // CLASSIC: combat rejoin. A wizard whose client dropped keeps the seat for a while (ClassicSettings
    // CombatRejoinSeconds): the seat has no actor, passes every round, and their next login takes it back.
    internal bool Disconnected { get; private set; }
    internal ulong HeldCharacterId { get; private set; }
    internal DateTime DisconnectedAtUtc { get; private set; }

    internal void HoldSeat(DateTime nowUtc) {
        HeldCharacterId = _wizard?.CharId ?? 0;
        Disconnected = true;
        DisconnectedAtUtc = nowUtc;
        ParticipantActor = ActorRefs.Nobody;
        ContinueDeferredActions(rejoined: false);
    }

    internal void RejoinSeat(IActorRef actor, CoreObject participantObject, Wizard wizard)
        => TryRejoinSeat(actor, participantObject, wizard, out _);

    // CLASSIC: validate/capture before replacing a held seat. A failed preparation leaves all held aliases intact.
    internal bool TryRejoinSeat(IActorRef actor, CoreObject participantObject, Wizard wizard,
        out CombatElixirEntryReceipt receipt) {
        receipt = null;
        if (!ElixirService.PreparesCombatSnapshots) {
            BindRejoinedSeat(actor, participantObject, wizard);
            return true;
        }
        CombatElixirEntryReceipt prepared = null;
        var accepted = wizard?.GameStats is not null && WizardCollection.WithCharacterLock(wizard.CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)
                || WizardCollection.IsInventorySnapshotUncertain(_wizard) || !Disconnected
                || HeldCharacterId != wizard.CharId || _wizard?.CharId != wizard.CharId
                || ParticipantGameStats is null || CombatParticipant is null) return false;
            try {
                var messages = ElixirService.PublishCombatTransition(wizard, true, _duelActor?.Duel?.m_bPVP ?? true);
                ElixirService.TestRuntimeScope.Value?.BeforeCombatSnapshot?.Invoke(wizard);
                // The held duel's current HP is authoritative; reconnect never refills from a stale loaded alias.
                wizard.GameStats.m_currentHitpoints = ParticipantGameStats.m_currentHitpoints;
                var stats = wizard.GameStats.GetCombatGameStats();
                var ownedMinions = _duelActor.SubCircles.Where(circle => circle is not null && circle != this
                    && ReferenceEquals(circle._minionOwnerObject, ParticipantObject)).ToArray();
                prepared = new(wizard, messages.ToImmutableArray());
                BindRejoinedSeat(actor, participantObject, wizard, stats, ownedMinions);
                return true;
            }
            catch {
                WizardCollection.MarkInventorySnapshotUncertain(wizard);
                return false;
            }
        });
        if (!accepted) { actor?.Tell("Close"); return false; }
        receipt = prepared;
        return true;
    }

    private void BindRejoinedSeat(IActorRef actor, CoreObject participantObject, Wizard wizard,
        WizGameStats capturedStats = null, IReadOnlyList<CombatDuelSubCircle> ownedMinions = null) {
        var previous = ParticipantObject;
        ParticipantActor = actor;
        ParticipantObject = participantObject;
        _wizard = wizard;
        ParticipantGameStats = wizard.GameStats;
        if (CombatParticipant is not null) {
            CombatParticipant.m_playerHealth = wizard.GameStats.m_currentHitpoints;
            if (capturedStats is not null) {
                CombatParticipant.m_pGameStats = capturedStats;
                CombatParticipant.m_maxPlayerHealth = capturedStats.m_baseHitpoints;
                CombatParticipant.m_mobLevel = wizard.GameStats.Level;
            }
        }

        Disconnected = false;
        HeldCharacterId = 0;
        ContinueDeferredActions(rejoined: true);

        // Minions summoned by this wizard name the old object as their owner.
        foreach (var circle in ownedMinions ?? _duelActor.SubCircles.Where(circle => circle is not null && circle != this).ToArray()) {
            if (ReferenceEquals(circle._minionOwnerObject, previous)) {
                circle._minionOwnerObject = participantObject;
            }
        }
    }

    internal void RemoveParticipant() {
        ResetDeferredActionIdentity();
        _pendingParticipantNotice = false;
        _pendingElixirReceipt = null;
        Disconnected = false;
        HeldCharacterId = 0;
        ParticipantActor = null;
        ParticipantObject = null;
        CombatParticipant = null;
        AddedToDuel = false;
        IsSummonedMinion = false;
        HenchmanLevel = 0; // CLASSIC
        _minionOwnerObject = null;
        _minionTeam = CombatTeam.Player;
    }

    /// <summary>
    /// CLASSIC: this summoned minion is a hired henchman of <paramref name="level"/>: it plays <paramref name="deck"/>
    /// (a wizard's deck for its school and level) instead of its creature template's (T2-T4 have none), and the duel
    /// picks its moves with the ally brain. An empty deck keeps the creature deck.
    /// </summary>
    internal void BecomeHenchman(int level, IReadOnlyList<SpellData> deck) {
        if (!IsSummonedMinion || level <= 0) return;
        HenchmanLevel = level;
        if (deck is { Count: > 0 }) {
            var spellData = deck.Select(spell => new CombatDeckSpellData {
                TemplateId = spell.m_templateID, Quantity = spell.m_quantity,
            }).ToList();
            _combatDeck = new CombatDeck(spellData, [], PLAYER_HAND_SIZE, _duelActor?.StreamFor(CombatRng.DeckStream(SlotIndex)));
        }
    }

    internal Hand DrawHand() {
        var newHand = _combatDeck.GetHand();
        CombatParticipant.m_pHand = newHand;

        return newHand;
    }

    internal Hand GetCurrentHand() {
        // As-is, no draw or refill: a discard frees a slot this turn and the client must see it open.
        return new() { m_spellList = _combatDeck.LastGivenHand ?? [] };
    }

    /// <summary>
    /// Appends the given spells to the current hand, created from the template IDs.
    /// </summary>
    internal void AddSpellsToHand(uint[] spellTemplateIds) {
        if (_combatDeck is null) {
            return;
        }

        foreach (var tid in spellTemplateIds ?? []) {
            var spell = SpellFactory.GetSpell(tid);
            if (spell is not null) {
                _combatDeck.AddCardToHand(spell);
            }
        }
    }

    /// <summary>
    /// Empties the current hand.
    /// </summary>
    internal void ClearHand() {
        _combatDeck?.ClearHand();
    }

    /// <summary>
    /// Sets this participant's pips to exactly the given count of generic pips, no power pips, capped at
    /// the max.
    /// </summary>
    internal void SetPips(int count) {
        if (CombatParticipant is null) {
            return;
        }

        CombatParticipant.m_pipCount.m_genericPips = (byte) Math.Clamp(count, 0, MAX_PIP_COUNT);
        CombatParticipant.m_pipCount.m_powerPips = 0;
    }

    internal bool DiscardCard(Spell spell) {
        return _combatDeck.Discard(spell);
    }

    internal Spell GetSpellFromLastHand(byte index) {
        if (_combatDeck.LastGivenHand is null || index >= _combatDeck.LastGivenHand.Count) {
            return null;
        }

        return _combatDeck.LastGivenHand[index];
    }

    internal void DoPipGain() {
        // If the participant has the maximum amount of pips, do not gain any more.
        var genericPips = CombatParticipant.m_pipCount.m_genericPips;
        var powerPips = CombatParticipant.m_pipCount.m_powerPips;
        if (genericPips + powerPips >= MAX_PIP_COUNT) {
            return;
        }

        var gainedPowerPip = DeterminePowerPipGain(CombatParticipant);
        if (gainedPowerPip) {
            CombatParticipant.m_pipCount.m_powerPips++;
        }
        else {
            CombatParticipant.m_pipCount.m_genericPips++;
        }
    }

    internal bool HasSchoolMastery(uint magicSchoolID) {
        if (ParticipantGameStats.m_schoolID == magicSchoolID) {
            return true;
        }

        // CLASSIC: a summoned minion's power pips count double for its own school, as a wizard's do. A creature's
        // stats carry no m_schoolID (JsonIgnore, set only for wizards), so a Life minion's Centaur cost its power
        // pips one each here while the Myth owner's card window (MinionPipsForOwnerWindow) counted them twice:
        // the card showed castable and every pick came back "not enough pips" (owner, 2026-10-05 01:52).
        if (IsSummonedMinion && CombatParticipant is { } participant && (uint) participant.m_primaryMagicSchoolID == magicSchoolID) {
            return true;
        }

        return (MagicSchool) magicSchoolID switch {
            MagicSchool.Storm   => ParticipantGameStats.m_stormMastery   > 0,
            MagicSchool.Fire    => ParticipantGameStats.m_fireMastery    > 0,
            MagicSchool.Ice     => ParticipantGameStats.m_iceMastery     > 0,
            MagicSchool.Myth    => ParticipantGameStats.m_mythMastery    > 0,
            MagicSchool.Life    => ParticipantGameStats.m_lifeMastery    > 0,
            MagicSchool.Death   => ParticipantGameStats.m_deathMastery   > 0,
            MagicSchool.Balance => ParticipantGameStats.m_balanceMastery > 0,
            _ => false,
        };
    }

    internal bool HasSchoolMastery(string school) {
        if (Enum.TryParse<MagicSchool>(school, out var magicSchool)) {
            return HasSchoolMastery((uint)magicSchool);
        }

        Logger.Warning("Failed to parse magic school \"{0}\" from string.", Logger.Args(school));

        return false;
    }

    internal T GetStatBySchool<T>(List<T> list, string magicSchool) {
        if (list is null) {
            return default;
        }
        if (!typeof(T).IsPrimitive && !typeof(T).IsEnum) {
            throw new ArgumentException("List items must be primitive types or enums");
        }

        // Check if we're over the max index.
        var maxIndex = MagicSchools.GetMaxMagicSchoolIndex();
        if (list.Count <= maxIndex) { // CLASSIC: count must include the highest zero-based index.
            throw new ArgumentException("List must contain an entry for every defined magic school index.");
        }

        var index = (int) MagicSchools.GetMagicSchool(magicSchool).m_schoolIndex;

        return list[index];
    }

    internal bool HasPipsForSpell(Spell spell) {
        // X-pip spells scale to any pip count; no fixed minimum.
        if (CombatActionResolver.IsXPipSpell(spell)) {
            return true;
        }

        var spellRank = spell.m_pipCost.m_spellRank;
        var genericPips = CombatParticipant.m_pipCount.m_genericPips;
        var powerPips = CombatParticipant.m_pipCount.m_powerPips;
        var isMastered = HasSchoolMastery(spell.m_magicSchoolID);

        // Power pips count as 2 generic pips if the spell is mastered.
        var totalPips = isMastered
            ? genericPips + (powerPips * 2)
            : genericPips + powerPips;

        return totalPips >= spellRank;
    }

    internal bool TryStun() {
        // todo: check if this creature can be stunned.
        CombatParticipant.m_stunned = 1;

        return true;
    }

    internal void DamageParticipant(int damage) {
        // If the participant is a player, update their health.
        if (_wizard is not null) {
            var currentHealth = ParticipantGameStats.m_currentHitpoints;
            var newHealth = currentHealth - damage;

            // Make sure the health doesn't go below 0.
            if (newHealth < 0) {
                newHealth = 0;
            }

            // Update the health of the player.
            _wizard.UpdateHealth(newHealth);
        }
        else {
            // Make sure the health doesn't go below 0.
            if (ParticipantGameStats.m_currentHitpoints - damage < 0) {
                ParticipantGameStats.m_currentHitpoints = 0;
            }
            else {
                ParticipantGameStats.m_currentHitpoints -= damage;
            }
        }
    }

    internal void HealParticipant(int heal) {
        // If the participant is a player, update their health.
        if (_wizard is not null) {
            var currentHealth = ParticipantGameStats.m_currentHitpoints;
            var newHealth = currentHealth + heal;

            // Make sure the health doesn't go above the max health.
            if (newHealth > ParticipantGameStats.m_baseHitpoints) {
                newHealth = ParticipantGameStats.m_baseHitpoints;
            }

            // Update the health of the player.
            _wizard.UpdateHealth(newHealth);
        }
        else {
            // Make sure the health doesn't go above the max health.
            if (ParticipantGameStats.m_currentHitpoints + heal > ParticipantGameStats.m_baseHitpoints) {
                ParticipantGameStats.m_currentHitpoints = ParticipantGameStats.m_baseHitpoints;
            }
            else {
                ParticipantGameStats.m_currentHitpoints += heal;
            }
        }
    }

    internal void DeductMana(int mana) {
        // If the participant is a player, update their mana.
        // Creature's don't have mana.
        if (_wizard is not null) {
            var currentMana = ParticipantGameStats.m_currentMana;
            var newMana = currentMana - mana;

            // Make sure the mana doesn't go below 0.
            if (newMana < 0) {
                newMana = 0;
            }

            // Update the mana of the player.
            _wizard.UpdateMana(newMana);
        }
    }

    internal void DeductPips(MagicSchool school, byte spellRank) {
        var isMastered = HasSchoolMastery((uint) school);
        var pipCount = CombatParticipant.m_pipCount;

        // Deduct pips based on the spell rank.
        // We have a second conditional here incase of byte overflow.
        while (spellRank is > 0 and < (MAX_PIP_COUNT * 2)) {
            if (isMastered && pipCount.m_powerPips > 0) {
                pipCount.m_powerPips--;
                spellRank -= 2;
            }
            else if (!isMastered && pipCount.m_powerPips > 0) {
                pipCount.m_powerPips--;
                spellRank--;
            }
            else if (pipCount.m_powerPips == 0 && pipCount.m_genericPips > 0) {
                pipCount.m_genericPips--;
                spellRank--;
            }
            else if (pipCount.m_powerPips == 0 && pipCount.m_genericPips == 0) {
                break;
            }
        }
    }

    internal void DeductAllPips() {
        var ourPipCount = CombatParticipant.m_pipCount;
        ourPipCount.m_powerPips = 0;
        ourPipCount.m_genericPips = 0;
    }

    internal void Reshuffle() => _combatDeck.Reshuffle();

    /// <summary>
    /// Draws a random treasure card from the vault and adds it to the current hand.
    /// </summary>
    /// <returns>The drawn spell, or null if no vault cards available or hand is full.</returns>
    internal Spell DrawFromVault() => _combatDeck.DrawFromVault();

    /// <summary>
    /// Gets the number of treasure cards remaining in the vault.
    /// </summary>
    internal int VaultRemainingCount => _combatDeck.VaultRemainingCount;

    /// <summary>
    /// Gets the number of treasure cards currently in the hand.
    /// </summary>
    internal int TreasureCardsInHand => _combatDeck.TreasureCardsInHand;

    /// <summary>
    /// Permanently consumes a successfully cast treasure card from the vault.
    /// </summary>
    internal uint ConsumeFromVault(Spell spell) => _combatDeck.ConsumeFromVault(spell);

    private bool InitializePlayerSubCircle(out CombatElixirEntryReceipt receipt) {
        receipt = null;
        var prepareSnapshot = ElixirService.PreparesCombatSnapshots;
        // todo: this method is a mess.
        // CLASSIC: the session's pushed wizard when it has one; else the (blocking) question as before.
        try {
            if (!ActiveWizardDirectory.TryGet(ParticipantActor, out _wizard, out _)) {
                var queryCharacterMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
                _wizard = ParticipantActor
                    .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryCharacterMsg, PlayerQuery.Timeout) // CLASSIC: timeout
                    .Result
                    .Wizard;
            }
        }
        catch when (prepareSnapshot) {
            // CLASSIC: an unavailable session has not changed runtime state and cannot be admitted.
            ParticipantActor?.Tell("Close");
            return false;
        }

        if (!prepareSnapshot) {
            InitializePlayerSubCircleState();
            return true;
        }
        CombatElixirEntryReceipt prepared = null;
        var accepted = _wizard?.GameStats is not null && WizardCollection.WithCharacterLock(_wizard.CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(_wizard)) return false;
            try {
                var messages = ElixirService.PublishCombatTransition(_wizard, true, _duelActor?.Duel?.m_bPVP ?? true);
                // The outer lane remains held after the runtime helper returns, through every derived stat copy.
                ElixirService.TestRuntimeScope.Value?.BeforeCombatSnapshot?.Invoke(_wizard);
                InitializePlayerSubCircleState();
                prepared = new(_wizard, messages.ToImmutableArray());
                return true;
            }
            catch {
                WizardCollection.MarkInventorySnapshotUncertain(_wizard);
                return false;
            }
        });
        if (!accepted) { ParticipantActor?.Tell("Close"); return false; }
        receipt = prepared;
        return true;
    }

    private void InitializePlayerSubCircleState() {

        // Dyanmic symbols start at 9 for players.
        var dynamicSymbol = (DynamicSigilSymbol) (SlotIndex + 9);

        ParticipantGameStats = _wizard.GameStats;
        var combatStats = ParticipantGameStats.GetCombatGameStats();

        // Collage spells the player has learned and temporary spells (perhaps from equipment)
        // into one list to create the combat hand. Treasure cards go into a separate vault.
        // CLASSIC: the deck's card list holds regular cards only; the deck's Treasure Cards are this wizard's ledger for
        // the equipped deck (SpellbookBehavior.DeckTreasureCards), so a Treasure Card of a known spell is still a
        // Treasure Card. A card-list entry the wizard has not learned (a deck another wizard filled, through the shared
        // bank; or one an older save kept as a Treasure Card, moved to the ledger at login) is left out instead of
        // becoming a free Treasure Card; so is a Treasure Card template in the card list. An empty learned list (no
        // record of learned spells) keeps the deck as it is.
        var allSpells = new List<CombatDeckSpellData>();
        var vaultSpells = new List<CombatDeckSpellData>();
        var learned = _wizard.SpellbookBehavior.LearnedSpellTemplateIds;
        if (_wizard.SpellbookBehavior.SpellList is not null) {
            foreach (var spell in _wizard.SpellbookBehavior.SpellList) {
                if (spell is null || Wizard.IsLegacyDeckTreasure(spell, learned)) {
                    continue;
                }

                allSpells.Add(new CombatDeckSpellData {
                    TemplateId = spell.m_templateID,
                    Quantity = spell.m_quantity,
                });
            }
        }

        var deckSlotId = _wizard.EquipmentBehavior.SlotList
            .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
        if (deckSlotId is { } vaultDeckId) {
            foreach (var (templateId, copies) in _wizard.SpellbookBehavior.DeckTreasureCardsOf(vaultDeckId)) {
                if (copies <= 0 || !Monstrology.MonstrologyCardCatalog.IsUsableCard(templateId)) {
                    continue; // CLASSIC: a later world's Monstrology card (Monstrology is Arc 1 only).
                }

                vaultSpells.Add(new CombatDeckSpellData {
                    TemplateId = templateId,
                    Quantity = (uint) copies,
                    IsTreasureCard = true
                });
            }
        }

        // Count temporary spells as 1 quantity, skipping any that the player
        // has excluded via the spell deck UI (MSG_UPDATEITEMSPELLEXCLUSIONLIST).
        var temporarySpells = new List<CombatDeckSpellData>();
        var equippedDeckId = _wizard.EquipmentBehavior.SlotList
            .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
        foreach (var tempSpell in _wizard.SpellbookBehavior.TemporarySpells) {
            // Check if this item spell is excluded for the equipped deck.
            if (equippedDeckId != null
                && _wizard.SpellbookBehavior.IsItemSpellExcluded(equippedDeckId.Value, tempSpell.m_templateID)) {
                continue;
            }

            // If the spell data already exists, increase the quantity.. otherwise add it.
            var existingSpell = temporarySpells.Find(s => s.TemplateId == tempSpell.m_templateID);
            if (existingSpell is not null) {
                existingSpell.Quantity++;
            }
            else {
                temporarySpells.Add(new CombatDeckSpellData {
                    TemplateId = tempSpell.m_templateID,
                    Quantity = 1,
                    IsItemCard = true
                });
            }
        }
        allSpells.AddRange(temporarySpells);
        _combatDeck = new CombatDeck(allSpells, vaultSpells, PLAYER_HAND_SIZE, _duelActor?.StreamFor(CombatRng.DeckStream(SlotIndex)));

        CombatParticipant = new CombatParticipant {
            m_ownerID = ParticipantObject.m_globalID,
            m_templateID = 219902325553, // recorded from live
            m_isPlayer = true,
            m_zoneID = _duelActor.SigilId,
            m_isMonster = 0,
            m_teamID = (int) (PvpTeam ?? CombatTeam.Player), // CLASSIC: open PvP seats wizards on both teams
            m_primaryMagicSchoolID = (int) _wizard.MagicSchoolBehavior.MagicSchool,
            m_pipCount = DetermineStartingPips(),
            m_pipRoundRates = new(),
            m_originalTeam = 0,
            m_maxHandSize = PLAYER_HAND_SIZE,
            m_playerHealth = ParticipantGameStats.m_currentHitpoints,
            m_maxPlayerHealth = ParticipantGameStats.m_baseHitpoints,
            // The client's crit sim reads the participant level from m_mobLevel (it zeroes its
            // crit chance below the level threshold when this is missing).
            m_mobLevel = ParticipantGameStats.Level,
            m_myTeamTurn = _duelActor.Duel.m_firstTeamToAct == (int) (PvpTeam ?? CombatTeam.Player),
            m_pGameStats = combatStats,
            // CLASSIC: no shadow-pip meter (Shadow magic is 2012). The client's combatant control shows it only when
            // m_pGameStats.m_shadowPipMax > 0 and this is false (r806919 0x14078bab3 -> 0x142035a30).
            m_shadowSpellsDisabled = true,
            m_pPlayDeck = new PlayDeck(),
            m_subcircle = SlotIndex,
            m_dynamicSymbol = dynamicSymbol,
            m_PipsSuspended = false,

            m_color = _color,
            m_rotation = _rotation,
            m_radius = _radius,
        };
    }

    private void InitializeCreatureSubCircle(bool asMinion = false, int minionOwnerSubCircle = 0) {
        // CLASSIC: a started creature's stats from the directory; one still starting is asked (blocking) as before.
        var creatureStats = CreatureStatsDirectory.TryGet(ParticipantActor)
            ?? ParticipantActor
                .Ask<COMBAT_106_PROTOCOL.MSG_CREATURESTATS>(new COMBAT_106_PROTOCOL.MSG_QUERYCREATURESTATS(), PlayerQuery.Timeout) // CLASSIC: timeout
                .Result;

        // Dynamic symbols start 1-4 for creatures.
        var dynamicSymbol = (DynamicSigilSymbol) (SlotIndex + 1);

        // Convert the creature stats to a combat deck.
        var spellData = new List<CombatDeckSpellData>();
        foreach (var spell in creatureStats.SpellList) {
            if (spell is null) {
                continue;
            }

            spellData.Add(new CombatDeckSpellData {
                TemplateId = spell.m_templateID,
                Quantity = spell.m_quantity
            });
        }

        _combatDeck = new CombatDeck(spellData, [], PLAYER_HAND_SIZE, _duelActor?.StreamFor(CombatRng.DeckStream(SlotIndex)));

        ParticipantGameStats = creatureStats.GameStats;
        CombatParticipant = new CombatParticipant {
            m_ownerID = ParticipantObject.m_globalID,
            m_templateID = 2199023290637, // Captured 2199023290637 from live
            m_isPlayer = false,
            m_zoneID = _duelActor.SigilId,
            m_isMonster = 0, // Live server sends 0
            // Minions are creatures on the player team; m_isPlayer stays false.
            m_teamID = asMinion ? (ClassicRuntime.IsActive ? (int) _minionTeam : 0) : 1,
            m_originalTeam = 0,
            m_isMinion = asMinion,
            m_maxHandSize = PLAYER_HAND_SIZE,
            m_primaryMagicSchoolID = (int) creatureStats.MagicSchool,
            m_pipCount = DetermineStartingPips(),
            m_pipRoundRates = new(),
            m_playerHealth = creatureStats.GameStats.m_currentHitpoints,
            m_maxPlayerHealth = creatureStats.GameStats.m_baseHitpoints,
            m_myTeamTurn = _duelActor.Duel.m_firstTeamToAct == (asMinion && ClassicRuntime.IsActive ? (int) _minionTeam : 1),
            m_pGameStats = creatureStats.GameStats.GetCombatGameStats(),
            m_shadowSpellsDisabled = true, // CLASSIC: no shadow-pip meter; see the player's
            m_mobLevel = creatureStats.CombatLevel,

            m_minionStartingHealth = asMinion ? creatureStats.GameStats.m_currentHitpoints : 0,
            m_curMaxHP = asMinion ? creatureStats.GameStats.m_baseHitpoints : 0,
            // The client links a minion to its owner through this sub-circle.
            m_minionSubCircle = asMinion ? minionOwnerSubCircle : 0,

            m_subcircle = SlotIndex,
            m_dynamicSymbol = dynamicSymbol,

            m_color = _color,
            m_rotation = _rotation,
            m_radius = _radius,
        };
    }

    private async Task PlayEntranceAnimation(CoreObject participantObject, IActorRef participantActor) {
        // Send the "Sigil" state to the zone so the client transitions the object.
        _duelActor.ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = participantObject.m_globalID,
            State = StringHash.Compute("Sigil")
        });

        // Send aggro to the participant.
        _duelActor.ZoneBroadcast(new WIZARD_12_PROTOCOL.MSG_AGGRO {
            GlobalID = participantObject.m_globalID,
            LocX = WorldPosition.X,
            LocY = WorldPosition.Y,
            LocZ = WorldPosition.Z,
            Yaw = WorldRotation,
            SigilGID = _duelActor.SigilId
        });

        // Set the actual position of the game object to the sigil.
        participantObject.m_location = new Vector3(WorldPosition.X, WorldPosition.Y, WorldPosition.Z);
        participantObject.m_orientation = new Vector3(0, 0, WorldRotation);

        // Wait the amount of time it takes for the actor to enter the sigil, then set
        // their state to stationary.
        await Task.Delay((int) (AGGRO_TIME_IN_SECONDS * 1000));

        // Broadcast the "Stationary" state to the zone so the client knows the
        // entrance animation is complete and the participant is now at the sigil.
        _duelActor.ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = participantObject.m_globalID,
            State = StringHash.Compute("Stationary")
        });
    }

    private PipCount DetermineStartingPips() {
        var pipCount = new PipCount() {
            m_powerPips = ParticipantGameStats.m_startingPowerPips,
            m_genericPips = ParticipantGameStats.m_startingPips
        };

        // Ensure that the total number of pips does not exceed MAX_PIP_COUNT.
        if (pipCount.m_genericPips + pipCount.m_powerPips > MAX_PIP_COUNT) {
            int excessPips = pipCount.m_genericPips + pipCount.m_powerPips - MAX_PIP_COUNT;

            // Reduce generic pips first if there is an excess
            if (excessPips <= pipCount.m_genericPips) {
                pipCount.m_genericPips -= (byte) excessPips;
            }
            else {
                // If excess pips are more than generic pips, set generic pips to 0
                // and adjust power pips accordingly
                excessPips -= pipCount.m_genericPips;
                pipCount.m_genericPips = 0;
                pipCount.m_powerPips -= (byte) excessPips;
            }
        }

        return pipCount;
    }

    private bool DeterminePowerPipGain(CombatParticipant participant)
        => DeterminePowerPipGain(participant, _duelActor.Duel, _duelActor.Rng.NextDouble());

    // The production decision with an explicit roll makes probability boundaries testable without random tests.
    // Base chance already comes from the participant's school/level stats. Pip capacity, starting pips and
    // school mastery remain in their existing paths; a global changes chance, not the value of a power pip.
    internal static bool DeterminePowerPipGain(CombatParticipant participant, Duel duel, double roll) {
        var stats = participant.m_pGameStats;
        // Read the current battlefield each round. ApplyGlobalEffect replaces its contents, so the bonus
        // vanishes immediately when Power Play is replaced; never cache it in the participant's stats.
        var global = duel?.m_duelModifier?.m_battlefieldEffects?
            .FirstOrDefault(effect => effect.m_effectType == kSpellEffects.kModifyPowerPipChance);
        return PowerPipRules.GainsPowerPip(stats.m_powerPipBase, stats.m_powerPipBonusPercentAll,
            global?.m_effectParam ?? 0, roll);
    }

}
