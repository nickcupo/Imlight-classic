using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic.Elixirs;

// CLASSIC: r806919 0x142117390 caps active Elixir slots at three; 0x1421c1a30
// rejects any exact shared m_typeList token. Neither display names nor item ids define a family.
internal sealed record ElixirDefinition(uint TemplateId, uint DurationSeconds, IReadOnlyList<string> Families,
    bool CombatEnabled, bool PvpEnabled, string Provenance, IReadOnlyList<ElixirEffect> Effects = null) {
    internal bool Valid => TemplateId > 0 && TemplateId < (1u << 28) && DurationSeconds > 0
        && Families is { Count: > 0 } && Families.All(f => !string.IsNullOrWhiteSpace(f))
        && Families.Distinct(StringComparer.Ordinal).Count() == Families.Count
        && !string.IsNullOrWhiteSpace(Provenance);
}

internal sealed record ElixirEffect(string Name, int LookupIndex, float Value);

internal static class ElixirRules {
    internal const string SlotName = "Elixir";
    internal const int MaximumActive = 3;

    // CLASSIC: intentionally empty pending dated October evidence or an explicit owner ruling.
    // Native modern numbers are fixtures, never a historical allowlist. Adding an offer elsewhere
    // cannot bypass this independent activation gate.
    internal static ElixirDefinition Approved(uint templateId) => null;

    internal static bool IsElixir(WizClientObjectItem item, WizItemTemplate template = null)
        => item?.m_inactiveBehaviors?.Any(b => b is ClientElixirBehavior) == true
            || template?.m_behaviors?.Any(b => b is ElixirBehaviorTemplate) == true;

    internal static bool Overlaps(IEnumerable<string> first, IEnumerable<string> second)
        => first.Intersect(second, StringComparer.Ordinal).Any();

    internal static bool EffectsEnabled(ElixirDefinition definition, bool inCombat, bool pvp)
        => definition?.Valid == true && (!inCombat || definition.CombatEnabled)
            && (!pvp || definition.PvpEnabled);
}
