using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Game.Spells;

namespace Imlight.CoreLib.Game.Zone.Components;
internal sealed partial class CombatDuelComponent {
    private MonstrologySessionPolicy CastOwnerPolicy(CombatDuelSubCircle caster) {
        var owner = caster?.IsSummonedMinion == true
            ? SubCircles.FirstOrDefault(circle => !circle.IsSummonedMinion && caster.IsOwnedMinionOf(circle))
            : caster;
        return MonstrologySessionPolicy.ForWizard(owner?._wizard);
    }
    internal bool AllowsMonstrologyCast(CombatDuelSubCircle caster, Spell card, SpellTemplate template = null)
        => !MonstrologyCastGuard.RequiresPermission(card,template)
            || CastOwnerPolicy(caster)?.Allows(MonstrologyService.Enabled) == true;
    internal bool RunMonstrologyCast(CombatDuelSubCircle caster, Spell card, SpellTemplate template, System.Action action) {
        var required = MonstrologyCastGuard.RequiresPermission(card,template);
        return MonstrologyCastGuard.Run(required,required ? CastOwnerPolicy(caster) : null,
            required && MonstrologyService.Enabled,action);
    }
    private bool RunMonstrologyEnchantment(CombatDuelSubCircle caster, int sourceIndex, uint targetIndex, System.Action action) {
        var hand = caster?._combatDeck?.LastGivenHand;
        var source = hand != null && sourceIndex >= 0 && sourceIndex < hand.Count ? hand[sourceIndex] : null;
        var target = hand != null && targetIndex < hand.Count ? hand[(int)targetIndex] : null;
        var required = MonstrologyCastGuard.RequiresPermission(source) || MonstrologyCastGuard.RequiresPermission(target);
        return MonstrologyCastGuard.Run(required,required ? CastOwnerPolicy(caster) : null,
            required && MonstrologyService.Enabled,action);
    }
}
