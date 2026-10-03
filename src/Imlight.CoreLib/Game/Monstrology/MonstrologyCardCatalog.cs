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
    /// <summary>Every Arc 1 creature the installed Monstrology cards name (QA: the max-out command).</summary>
    internal static IEnumerable<uint> Creatures => Cards.Value.Keys.Select(key => key.Item1).Distinct().Where(IsArc1Creature);
    internal static bool TryResolve(uint creature, MonstrologyCreationKind kind, out MonstrologyCard card) {
        card = default!;
        return IsArc1Creature(creature) && Cards.Value.TryGetValue((creature, kind), out card);
    }

    // CLASSIC (owner ruling 2026-10-02): Monstrology covers the Arc 1 worlds only. A creature counts when the client
    // files its template under an Arc 1 world folder (ObjectData/WC, KT, MB, MS, DS, GH); later worlds and later
    // events placed in Arc 1 zones (Community Event, Weaving, Gauntlet, SkeletonKeys...) are left out.
    private static readonly HashSet<string> Arc1Folders = new(StringComparer.OrdinalIgnoreCase) {
        "WC", "KT", "MB", "MS", "DS", "GH", "WizardCity", "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Grizzleheim",
    };

    private static readonly Lazy<Dictionary<uint, string>> TemplateFolders = new(() => {
        var folders = new Dictionary<uint, string>();
        foreach (var entry in CoreObjectFactory.TemplateManifest.m_serializedTemplates) {
            folders[entry.m_id] = FolderOf(entry.m_filename);
        }
        return folders;
    });

    internal static string FolderOf(string path) {
        var parts = (path ?? "").Split('/');
        return parts.Length >= 3 && parts[0].Equals("ObjectData", StringComparison.OrdinalIgnoreCase) ? parts[1] : "";
    }

    internal static bool IsArc1Folder(string folder) => Arc1Folders.Contains(folder ?? "");

    /// <summary>CLASSIC: false for a Monstrology card (summon, kill or guest) whose creature is not an Arc 1 creature; true
    /// for every other spell. Cards made before the Arc 1 limit (a later world's summon) stay out of duels.</summary>
    internal static bool IsUsableCard(uint spellTemplate)
        => !CardCreatures.Value.TryGetValue(spellTemplate, out var creature) || IsArc1Creature(creature);

    private static readonly Lazy<Dictionary<uint, uint>> CardCreatures = new(() => {
        var map = new Dictionary<uint, uint>();
        foreach (var card in Cards.Value.Values) {
            map[card.TemplateId] = card.CreatureId;
        }
        return map;
    });

    internal static bool IsArc1Creature(uint creature)
        => TemplateFolders.Value.TryGetValue(creature, out var folder) && IsArc1Folder(folder);
}
