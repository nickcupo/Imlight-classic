// CLASSIC: arena decisions use visible battle state, never opponents' hands or future combat rolls.
// Scores and thresholds below are AI preferences, not historical spell/stat overrides.
using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Ambient;

namespace Imlight.Classic.Pvp;

public enum ArenaCardRole { Damage, Heal, Blade, Trap, Shield, Weakness, RemoveWard, RemoveCharm, Reshuffle, Other }
public enum ArenaModifierKind { OutgoingDamage, IncomingDamage, Absorb, OutgoingHeal, IncomingHeal }

public sealed record ArenaModifier(uint TemplateId, ArenaModifierKind Kind, string School, double Amount);
public sealed record ArenaDamagePart(string School, double Amount, bool OverTime = false, double DrainShare = 0);
public sealed record ArenaDamageBranch(double Probability, IReadOnlyList<ArenaDamagePart> Parts);

public sealed record ArenaPvpCard(int HandIndex, uint TemplateId, string Name, string School, int Pips,
    int AvailablePips, bool Castable, double Accuracy, ArenaCardRole Role,
    IReadOnlyList<ArenaDamagePart> Damage, IReadOnlyList<ArenaModifier> Modifiers,
    double Heal = 0, bool HealOverTime = false, bool SelfOnly = false, bool AllEnemies = false, bool AllAllies = false,
    IReadOnlyList<ArenaDamageBranch>? DamageBranches = null, bool Discardable = true, double DirectHeal = 0, double SelfDamage = 0);

public sealed record ArenaPvpCombatant(int Slot, bool Ally, string School, int Health, int MaxHealth, int Pips,
    IReadOnlyList<ArenaModifier> Modifiers, int TeamFocus = 0,
    IReadOnlyDictionary<string, double>? DamageBonus = null, IReadOnlyDictionary<string, double>? Resist = null,
    double OutgoingHeal = 0, double IncomingHeal = 0, bool Revivable = false) {
    public bool Alive => Health > 0;
    public double HealthFraction => (double) Health / Math.Max(1, MaxHealth);
}

public sealed record ArenaPvpView(int SelfSlot, bool Stunned, int RemainingCards,
    IReadOnlyList<ArenaPvpCard> Hand, IReadOnlyList<ArenaPvpCombatant> Combatants);

public static class ArenaPvpBrain {
    private sealed record Candidate(ArenaPvpCard Card, int Target, double Score, string Reason);
    private static bool Matches(string effectSchool, string school)
        => string.Equals(effectSchool, "all", StringComparison.OrdinalIgnoreCase)
           || string.Equals(effectSchool, school, StringComparison.OrdinalIgnoreCase);

    /// <summary>Expected damage for planning, consuming visible wards between separate hits on a copy of the view.</summary>
    public static (double Immediate, double Total, double Drain) DamageTo(ArenaPvpCard card,
                                                                        ArenaPvpCombatant self, ArenaPvpCombatant target) {
        double immediate = 0, total = 0, drain = 0;
        foreach (var branch in card.DamageBranches ?? [new ArenaDamageBranch(1, card.Damage)]) {
          // Random alternatives each meet the original wards. They are not successive hits.
          var wards = target.Modifiers.ToList();
          foreach (var part in branch.Parts) {
            var amount = part.Amount * (1 + (self.DamageBonus?.GetValueOrDefault(part.School) ?? 0));
            var charms = self.Modifiers.Where(m => m.Kind == ArenaModifierKind.OutgoingDamage && Matches(m.School, part.School))
                .DistinctBy(m => m.TemplateId);
            foreach (var charm in charms) amount *= Math.Max(0, 1 + charm.Amount / 100.0);
            var used = wards.Where(m => (m.Kind is ArenaModifierKind.IncomingDamage or ArenaModifierKind.Absorb)
                                       && Matches(m.School, part.School)).DistinctBy(m => m.TemplateId).ToList();
            foreach (var ward in used) {
                wards.Remove(ward);
                if (ward.Kind == ArenaModifierKind.Absorb) {
                    var remaining = Math.Max(0, ward.Amount - amount);
                    amount = Math.Max(0, amount - ward.Amount);
                    if (remaining > 0) wards.Add(ward with { Amount = remaining });
                }
                else amount *= Math.Max(0, 1 + ward.Amount / 100.0);
            }
            amount *= Math.Max(0, 1 - (target.Resist?.GetValueOrDefault(part.School) ?? 0));
            amount *= Math.Clamp(card.Accuracy, 0, 1) * branch.Probability;
            total += amount;
            if (!part.OverTime) immediate += amount;
            drain += amount * part.DrainShare;
          }
        }
        return (immediate, total, drain);
    }

    public static AllyMove Choose(ArenaPvpView view, Random rng) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(rng);
        var self = view.Combatants.FirstOrDefault(c => c.Slot == view.SelfSlot && c.Alive);
        if (view.Stunned || self is null) return AllyMove.Pass("stunned or defeated");
        var enemies = view.Combatants.Where(c => !c.Ally && c.Alive).ToList();
        var allies = view.Combatants.Where(c => c.Ally && c.Alive).ToList();
        if (enemies.Count == 0) return AllyMove.Pass("no enemy standing");
        var castable = view.Hand.Where(c => c.Castable && (c.SelfDamage <= 0 || c.SelfDamage < self.Health)).ToList();
        var attacks = castable.Where(c => c.Role == ArenaCardRole.Damage).ToList();
        var candidates = new List<Candidate>();

        // A reliable immediate finish takes precedence over setup; a DoT is never an immediate kill.
        var kills = (from card in attacks.Where(c => c.Accuracy >= .6)
                     from target in enemies
                     let damage = DamageTo(card, self, target)
                     where damage.Immediate >= target.Health
                     select new Candidate(card, target.Slot, target.TeamFocus, "finish opponent"))
            .OrderBy(c => c.Card.Pips).ThenByDescending(c => c.Score).ThenBy(c => c.Card.HandIndex).ToList();
        if (kills.Count > 0) return Cast(kills[0]);

        foreach (var card in castable) {
            if (card.Role == ArenaCardRole.Damage) {
                foreach (var target in enemies) {
                    var damage = DamageTo(card, self, target);
                    var score = Math.Min(target.Health, damage.Immediate + (damage.Total - damage.Immediate) * .65);
                    if (card.AllEnemies) {
                        score = enemies.Sum(e => {
                            var hit = DamageTo(card, self, e);
                            return Math.Min(e.Health, hit.Immediate + (hit.Total - hit.Immediate) * .65);
                        });
                    }
                    score += Math.Min(self.MaxHealth - self.Health, damage.Drain) * (self.HealthFraction < .4 ? 1.8 : .6);
                    score *= 1 + Math.Min(3, target.TeamFocus) * .18;
                    // Prefer an inexpensive hit to clear a shield/weakness rather than wasting a large hit into it.
                    if (card.Pips <= 2 && card.Damage.Any(p => HasShield(target, p.School))) score += 55;
                    if (card.Pips <= 2 && self.Modifiers.Any(m => m.Kind == ArenaModifierKind.OutgoingDamage
                                                                 && m.Amount < 0 && Matches(m.School, card.School))) score += 45;
                    score -= card.Pips * 8;
                    candidates.Add(new Candidate(card, target.Slot, score, card.AllEnemies ? "team damage" : "focus damage"));
                    if (card.AllEnemies) break;
                }
            }
            else if (card.Role == ArenaCardRole.Heal) {
                var healAllies = view.Combatants.Where(a => a.Ally && (a.Alive || a.Revivable && DirectHeal(card) > 0)).ToList();
                var eligible = card.SelfOnly ? healAllies.Where(a => a.Slot == self.Slot) : healAllies;
                foreach (var target in eligible) {
                    var amount = EffectiveHeal(card, self, target);
                    var urgency = !target.Alive ? 4 : target.HealthFraction < .25 ? 3.2 : target.HealthFraction < .4 ? 2.2
                        : target.HealthFraction < .6 ? 1.1 : .25;
                    var score = amount * urgency - card.Pips * 10;
                    if (card.AllAllies) score = healAllies.Sum(a => EffectiveHeal(card, self, a)
                        * (!a.Alive ? 4 : a.HealthFraction < .4 ? 2.2 : a.HealthFraction < .6 ? 1.1 : .25)) - card.Pips * 10;
                    if (amount > 0) candidates.Add(new Candidate(card, card.AllAllies ? self.Slot : target.Slot, score,
                        target.Alive ? "heal endangered ally" : "revive defeated teammate"));
                    if (card.AllAllies) break;
                }
            }
            else if (card.Role is ArenaCardRole.Blade or ArenaCardRole.Trap) {
                foreach (var target in card.Role == ArenaCardRole.Blade ? allies : enemies) {
                    if (card.SelfOnly && target.Slot != self.Slot) continue;
                    if (Duplicate(card, target)) continue;
                    var owner = card.Role == ArenaCardRole.Blade ? target : self;
                    var setupHits = view.Hand.Where(h => h.Role == ArenaCardRole.Damage && h.Pips <= h.AvailablePips + 1
                        && h.AvailablePips - card.Pips + 1 >= h.Pips
                        && card.Modifiers.Any(m => Matches(m.School, h.School))).ToList();
                    if (owner.Slot != self.Slot) continue; // Do not assume an ally's unseen hand contains a particular card.
                    var strongest = setupHits.Select(h => h.Damage.Sum(p => p.Amount) * h.Accuracy).DefaultIfEmpty(0).Max();
                    var percent = card.Modifiers.Where(m => m.Amount > 0).Select(m => m.Amount).DefaultIfEmpty(0).Max();
                    var score = strongest * percent / 100.0 + (strongest >= 300 ? 60 : 0) - card.Pips * 10;
                    if (card.Role == ArenaCardRole.Trap) score *= 1 + Math.Min(3, target.TeamFocus) * .18;
                    if (strongest > 0) candidates.Add(new Candidate(card, target.Slot, score, "prepare affordable hit"));
                }
            }
            else if (card.Role == ArenaCardRole.Shield) {
                foreach (var target in allies) {
                    if (card.SelfOnly && target.Slot != self.Slot || Duplicate(card, target)) continue;
                    var threat = enemies.Where(e => card.Modifiers.Any(m => Matches(m.School, e.School)))
                        .Select(e => e.Pips * 35.0 * Product(e.Modifiers, ArenaModifierKind.OutgoingDamage, e.School))
                        .DefaultIfEmpty(0).Max();
                    var protection = card.Modifiers.Where(m => m.Amount < 0).Select(m => -m.Amount / 100.0).DefaultIfEmpty(0).Max();
                    var score = threat * protection * (target.HealthFraction < .4 ? 1.5 : .65) - card.Pips * 8;
                    if (threat > 0) candidates.Add(new Candidate(card, target.Slot, score, "shield visible school and pips"));
                }
            }
            else if (card.Role == ArenaCardRole.Weakness) {
                foreach (var target in enemies.Where(e => !Duplicate(card, e))) {
                    var strength = card.Modifiers.Where(m => Matches(m.School, target.School) && m.Amount < 0)
                        .Select(m => -m.Amount / 100.0).DefaultIfEmpty(0).Max();
                    var score = target.Pips * 35 * strength * Product(target.Modifiers, ArenaModifierKind.OutgoingDamage, target.School);
                    if (score > 0) candidates.Add(new Candidate(card, target.Slot, score - card.Pips * 8, "weaken prepared opponent"));
                }
            }
            else if (card.Role == ArenaCardRole.RemoveWard) {
                foreach (var target in enemies.Where(e => e.Modifiers.Any(m =>
                             m.Kind == ArenaModifierKind.IncomingDamage && m.Amount < 0 || m.Kind == ArenaModifierKind.Absorb))) {
                    var potential = view.Hand.Where(h => h.Role == ArenaCardRole.Damage && h.Pips <= h.AvailablePips + 1)
                        .Select(h => h.Damage.Sum(p => p.Amount) * h.Accuracy).DefaultIfEmpty(0).Max();
                    if (potential > 0) candidates.Add(new Candidate(card, target.Slot, potential * .5 - card.Pips * 10, "remove shield before hit"));
                }
            }
            else if (card.Role == ArenaCardRole.RemoveCharm) {
                foreach (var target in enemies.Where(e => e.Modifiers.Any(m => m.Kind == ArenaModifierKind.OutgoingDamage && m.Amount > 0)))
                    candidates.Add(new Candidate(card, target.Slot, target.Pips * 35 * .5 - card.Pips * 10, "remove opponent blade"));
            }
            else if (card.Role == ArenaCardRole.Reshuffle && view.RemainingCards <= 2
                     && view.Hand.Count(h => h.Role == ArenaCardRole.Damage) <= 1) {
                candidates.Add(new Candidate(card, self.Slot, 350, "recover finite deck"));
            }
        }

        // Saving is considered alongside useful setup/defence; it never overrides an urgent heal.
        var bestDamage = candidates.Where(c => c.Card.Role == ArenaCardRole.Damage).Select(c => c.Score).DefaultIfEmpty(0).Max();
        var soon = view.Hand.Where(c => c.Role == ArenaCardRole.Damage && !c.Castable && c.Pips == c.AvailablePips + 1)
            .Select(c => enemies.Max(e => DamageTo(c, self, e).Total)).DefaultIfEmpty(0).Max();
        var useful = candidates.Where(c => c.Score > 0).ToList();
        if (soon > bestDamage * 1.6 && soon > 0 && allies.All(a => a.HealthFraction >= .6)
            && useful.All(c => c.Card.Role == ArenaCardRole.Damage)) return AllyMove.Pass("save one pip for stronger hit");
        if (useful.Count == 0) return AllyMove.Pass("build pips or cycle unusable cards");
        var highest = useful.Max(c => c.Score);
        var tied = useful.Where(c => Math.Abs(c.Score - highest) < .001).OrderBy(c => c.Card.HandIndex).ThenBy(c => c.Target).ToList();
        return Cast(tied[rng.Next(tied.Count)]);
    }

    public static int? DiscardChoice(ArenaPvpView view) {
        if (view.Stunned || view.RemainingCards <= 0 || view.Hand.Any(c => c.Role == ArenaCardRole.Damage)) return null;
        // A hand of seven different support cards also needs to advance to its attacks. Keep Reshuffle for recovery.
        return view.Hand.Where(c => c.Discardable && c.Role != ArenaCardRole.Reshuffle)
            .OrderByDescending(c => c.Role == ArenaCardRole.Other)
            .ThenByDescending(c => view.Hand.Count(h => h.TemplateId == c.TemplateId) > 1)
            .ThenByDescending(c => c.HandIndex).Select(c => (int?) c.HandIndex).FirstOrDefault();
    }

    private static bool Duplicate(ArenaPvpCard card, ArenaPvpCombatant target)
        => target.Modifiers.Any(m => m.TemplateId == card.TemplateId);
    private static bool HasShield(ArenaPvpCombatant target, string school)
        => target.Modifiers.Any(m => m.Kind == ArenaModifierKind.IncomingDamage && m.Amount < 0 && Matches(m.School, school));
    private static double Product(IEnumerable<ArenaModifier> modifiers, ArenaModifierKind kind, string school)
        => modifiers.Where(m => m.Kind == kind && Matches(m.School, school)).DistinctBy(m => m.TemplateId)
            .Aggregate(1.0, (amount, m) => amount * Math.Max(0, 1 + m.Amount / 100.0));
    private static double EffectiveHeal(ArenaPvpCard card, ArenaPvpCombatant self, ArenaPvpCombatant target)
        => Math.Min(target.MaxHealth - target.Health, (target.Alive ? card.Heal * (card.HealOverTime ? .65 : 1) : DirectHeal(card)) * card.Accuracy
            * (1 + self.OutgoingHeal) * (1 + target.IncomingHeal)
            * Product(self.Modifiers, ArenaModifierKind.OutgoingHeal, "life")
            * Product(target.Modifiers, ArenaModifierKind.IncomingHeal, "life"));
    private static double DirectHeal(ArenaPvpCard card) => card.DirectHeal > 0 ? card.DirectHeal : card.HealOverTime ? 0 : card.Heal;
    private static AllyMove Cast(Candidate candidate)
        => new(AllyMoveKind.Cast, candidate.Card.HandIndex, candidate.Target, candidate.Reason + ": " + candidate.Card.Name);
}
