using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Monstrology;

internal sealed record MonstrologyCard(uint TemplateId, uint CreatureId, MonstrologyCreationKind Kind, string SpellName);
internal static class MonstrologyCardCatalog {
    private static readonly Lazy<Dictionary<(uint, MonstrologyCreationKind), MonstrologyCard>> Cards = new(() =>
        Build(CoreObjectFactory.TemplateManifest.m_serializedTemplates
            .Where(x => x.m_filename.Contains("MonsterMagic", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.m_id, x.m_filename, CoreObjectFactory.GetCoreTemplate(x.m_id) as SpellTemplate))));
    internal static Dictionary<(uint, MonstrologyCreationKind), MonstrologyCard> Build(
        IEnumerable<(uint Id, string Path, SpellTemplate Template)> templates) {
        var result = new Dictionary<(uint, MonstrologyCreationKind), MonstrologyCard>();
        var ambiguous = new HashSet<(uint, MonstrologyCreationKind)>();
        foreach (var (id, path, template) in templates) {
            if (id == 0 || template == null || !template.m_Treasure || string.IsNullOrEmpty(template.m_name)) continue;
            MonstrologyCreationKind kind;
            kSpellEffects effect;
            if (template.m_adjectives?.Contains("MonsterMagicSummon") == true) { kind = MonstrologyCreationKind.SummonCard; effect = kSpellEffects.kSummonCreature; }
            else if (template.m_adjectives?.Contains("MonsterMagicKill") == true) { kind = MonstrologyCreationKind.KillCard; effect = kSpellEffects.kKillCreature; }
            else continue;
            var effects = template.m_effects?.Where(x => x.m_effectType == effect).ToArray();
            if (effects == null || effects.Length != 1 || effects[0].m_effectParam <= 0) continue;
            var creature = (uint)effects[0].m_effectParam;
            var key = (creature, kind);
            if (!result.TryAdd(key, new(id, creature, kind, template.m_name))) ambiguous.Add(key);
        }
        foreach (var key in ambiguous) result.Remove(key); // Never select an arbitrary duplicate output.
        return result;
    }
    internal static bool TryResolve(uint creature, MonstrologyCreationKind kind, out MonstrologyCard card)
        => Cards.Value.TryGetValue((creature, kind), out card);
}
