using System;
using Akka.Actor;
using System.Linq;
using System.Collections.Generic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Shared.Resources;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {
    private readonly MonstrologyPendingExtraction _pendingEssence = new();
    private PendingEssence[] _victoryEssence = [];
    private ulong _extractingOwner;
    private string _extractingFamily;
    private sealed class ExtractingDot(ulong owner, uint creature) {
        internal readonly ulong Owner = owner;
        internal readonly uint Creature = creature;
        internal bool HitCounted;
    }
    private readonly Dictionary<SpellEffect, ExtractingDot> _extractingDots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatDuelSubCircle, ExtractingDot> _castDotTargets = new();

    internal Dictionary<CombatDuelSubCircle, int> BeginMonstrologyCast(QueuedCombatAction action) {
        _extractingOwner = 0; _extractingFamily = null; _castDotTargets.Clear();
        if (!MonstrologySessionPolicy.AllowsWizard(action.SpellCaster?._wizard, MonstrologyService.Enabled)
            || action.SpellCaster.IsSummonedMinion || action.Spell == null || action.Spell.m_enchantment == 0
            || CoreObjectFactory.GetCoreTemplate(action.Spell.m_enchantment) is not SpellTemplate enchantment
            || !MonstrologyPendingExtraction.TryFamily(enchantment, out var family)) return null;
        _extractingOwner = action.SpellCaster._wizard.CharId; _extractingFamily = family;
        return SubCircles.Where(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && !x.IsSummonedMinion && x.IsAlive)
            .ToDictionary(x => x, x => x.ParticipantGameStats.m_currentHitpoints);
    }

    internal void ObserveMonstrologyCast(QueuedCombatAction action, Dictionary<CombatDuelSubCircle, int> before) {
        if (before == null) return;
        if (!MonstrologySessionPolicy.AllowsWizard(action.SpellCaster._wizard, MonstrologyService.Enabled)) {
            _extractingOwner = 0; _extractingFamily = null; _castDotTargets.Clear(); return;
        }
        foreach (var (target, health) in before) {
            if (CoreObjectFactory.GetCoreTemplate(target.ParticipantObject.m_templateID) is not GameObjectTemplate template
                || template.m_adjectiveList?.Contains(_extractingFamily) != true) continue;
            var metadata = template.m_behaviors?.OfType<MobMonsterMagicBehaviorTemplate>().SingleOrDefault();
            if (metadata == null) continue;
            // Rank/resistance/XP decisions belong to the validated reward adapter, not this damage observation.
            var collected = metadata.m_collectedAsTemplateID != 0 ? metadata.m_collectedAsTemplateID : (uint)target.ParticipantObject.m_templateID.Full;
            _pendingEssence.Observe(action.SpellCaster._wizard.CharId, target.ParticipantObject.m_globalID,
                collected, true, health, target.ParticipantGameStats.m_currentHitpoints);
            if (target.ParticipantGameStats.m_currentHitpoints < health && _castDotTargets.TryGetValue(target, out var appliedDot))
                appliedDot.HitCounted = true;
        }
        _extractingOwner = 0; _extractingFamily = null;
    }

    internal void RegisterMonstrologyDot(CombatDuelSubCircle target, SpellEffect effect) {
        if (_extractingOwner == 0 || target.OccupiedTeam != CombatTeam.Monster || target.IsSummonedMinion
            || CoreObjectFactory.GetCoreTemplate(target.ParticipantObject.m_templateID) is not GameObjectTemplate template
            || template.m_adjectiveList?.Contains(_extractingFamily) != true) return;
        var metadata = template.m_behaviors?.OfType<MobMonsterMagicBehaviorTemplate>().SingleOrDefault();
        if (metadata == null) return;
        var collected = metadata.m_collectedAsTemplateID != 0 ? metadata.m_collectedAsTemplateID : (uint)target.ParticipantObject.m_templateID.Full;
        if (!_castDotTargets.TryGetValue(target, out var attribution))
            _castDotTargets.Add(target, attribution = new(_extractingOwner, collected));
        _extractingDots.Add(effect, attribution);
    }
    internal void ObserveMonstrologyDot(CombatDuelSubCircle target, SpellEffect effect, int healthBefore) {
        if (!_extractingDots.TryGetValue(effect, out var attribution)) return;
        var owner = SubCircles.FirstOrDefault(x => x._wizard?.CharId == attribution.Owner && !x.IsSummonedMinion);
        if (!MonstrologySessionPolicy.AllowsWizard(owner?._wizard, MonstrologyService.Enabled)) {
            _extractingDots.Remove(effect); return;
        }
        var healthAfter = target.ParticipantGameStats.m_currentHitpoints;
        _pendingEssence.Observe(attribution.Owner,target.ParticipantObject.m_globalID,attribution.Creature,
            true,healthBefore,healthAfter,!attribution.HitCounted);
        if (healthAfter < healthBefore) attribution.HitCounted = true;
        if (healthAfter <= 0 || effect.m_numRounds <= 1) _extractingDots.Remove(effect);
    }

    private void FinishMonstrologyDuel(bool victory, HashSet<CombatDuelSubCircle> unrewarded = null) {
        // CLASSIC: a wizard defeated during a won fight extracts nothing from it (owner ruling 2026-10-10).
        var owners = SubCircles.Where(x => x.AddedToDuel && !x.IsSummonedMinion && unrewarded?.Contains(x) != true
                && MonstrologySessionPolicy.AllowsWizard(x._wizard, MonstrologyService.Enabled))
            .Select(x => x._wizard.CharId).ToHashSet();
        _victoryEssence = _pendingEssence.Finish(victory, owners);
        _extractingDots.Clear(); _castDotTargets.Clear(); _extractingOwner = 0; _extractingFamily = null;
    }
    // CLASSIC: after a won duel, each wizard's Extract Animus hits become Animus and Monstrology XP in their ledger
    // (MonstrologyRewards), and their session shows the result (MonstrologyService.ExtractionCommitted).
    private void AwardMonstrologyExtractions() {
        var observations = TakeMonstrologyVictoryObservations();
        if (observations.Length == 0) return;
        int[] thresholds;
        try {
            thresholds = MonstrologyProgression.InstalledThresholds;
        } catch (Exception ex) {
            Imlight.Common.Logger.Warning("Duel {0} | Monstrology rewards skipped: {1}", Imlight.Common.Logger.Args(Duel?.m_duelID.Full, ex.Message));
            return;
        }

        var repository = MonstrologyRepository.ForPlayers();
        foreach (var essence in observations) {
            var owner = SubCircles.FirstOrDefault(circle => circle._wizard?.CharId == essence.Owner && !circle.IsSummonedMinion);
            var policy = MonstrologySessionPolicy.ForWizard(owner?._wizard);
            if (owner is null || policy is null) continue;
            var creature = CoreObjectFactory.GetCoreTemplate(essence.Creature) as GameObjectTemplate;
            var npc = creature?.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault();
            var metadata = creature?.m_behaviors?.OfType<MobMonsterMagicBehaviorTemplate>().FirstOrDefault();
            var rank = npc?.m_nLevel ?? 1;
            var level = repository.Read(essence.Owner).Level;
            var outcome = MonstrologyRewards.Decide(level, rank, essence.Animus, metadata?.m_isBoss == true || npc?.m_bossMob == true,
                () => Random.Shared.Next(1, 101), MonstrologyRewards.XpPerRank);
            Imlight.Common.Logger.Information("Monstrology extraction: wizard {0} creature {1} rank {2} at level {3}: {4} ({5} Animus, {6} XP{7})",
                Imlight.Common.Logger.Args(essence.Owner.ToString(), essence.Creature.ToString(), rank.ToString(), level.ToString(),
                    outcome.Success ? "extracted" : "failed", outcome.Animus.ToString(), outcome.Experience.ToString(), outcome.Bonus ? ", bonus" : ""));
            if (!outcome.Success) continue;
            var operation = $"{Duel.m_duelID.Full}:{essence.Owner}:{essence.Target}";
            var award = new ExtractionAward(operation, essence.Owner, essence.Creature, outcome.Animus, outcome.Experience, true, true, true);
            MonstrologyResult result;
            try {
                result = MonstrologyContracts.CommitExtraction(repository, MonstrologyService.Enabled, policy, essence.Owner, award, thresholds);
            } catch (Exception ex) {
                Imlight.Common.Logger.Warning("Monstrology extraction for {0} not saved: {1}", Imlight.Common.Logger.Args(essence.Owner.ToString(), ex.Message));
                continue;
            }

            if (result == MonstrologyResult.Applied) {
                owner.ParticipantActor?.Tell(new MonstrologyExtractionCommitted { OwnerId = essence.Owner, OperationId = operation });
            }
        }
    }

    // Consume once, only after victory. Pending observations are not persistence or XP eligibility.
    internal PendingEssence[] TakeMonstrologyVictoryObservations() {
        var result = _victoryEssence.Where(x => SubCircles.Any(circle => circle._wizard?.CharId == x.Owner
            && MonstrologySessionPolicy.AllowsWizard(circle._wizard, MonstrologyService.Enabled))).ToArray();
        _victoryEssence = [];
        return result;
    }
}
