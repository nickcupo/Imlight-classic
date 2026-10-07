// CLASSIC: the same dated equipment effects are used by the shop preview and equipped server items.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Arena;

internal static class ClassicArenaGearTemplates {
    internal const string RelativePath = "pvp/arena-stats-october-2010.json";
    private const string CutoffText = "2010-10-31T23:59:59Z";
    private static readonly DateTimeOffset s_cutoff = new(2010, 10, 31, 23, 59, 59, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<uint, string> s_paths = CatalogPaths();
    private static IReadOnlyDictionary<uint, Gear>? s_items;
    private static HashSet<uint> s_excluded = [];
    internal sealed record Gear(uint Id, string Path, Effect[] Effects);
    internal sealed record Effect(string Kind, string Name, int Index, string Category, string Table, float CanonicalValue,
        string Spell, int Copies);

    internal static void Initialize(string? root, string profile) {
        s_items = null;
        s_excluded = [];
        if (profile != "october-2010-arc1") return;
        if (root is null) throw new InvalidDataException("October arena gear data root is missing.");
        var filename = System.IO.Path.Combine(root, RelativePath);
        using var stream = File.OpenRead(filename);
        if (stream.Length > 512 * 1024) throw new InvalidDataException("October arena gear projection exceeds its bound.");
        using var data = JsonDocument.Parse(stream);
        var document = data.RootElement;
        if (document.GetProperty("format").GetInt32() != 1
            || document.GetProperty("profile").GetString() != profile
            || document.GetProperty("vendor_template").GetUInt32() != 38226
            || document.GetProperty("cutoff").GetString() != CutoffText)
            throw new InvalidDataException("Unexpected October arena gear data identity.");
        var items = new Dictionary<uint, Gear>();
        var excluded = new HashSet<uint>();
        foreach (var row in document.GetProperty("items").EnumerateArray()) {
            var id = row.GetProperty("template_id").GetUInt32();
            var path = row.GetProperty("path").GetString()!;
            var confidence = row.GetProperty("confidence").GetString();
            if (!row.GetProperty("enabled").GetBoolean()
                || !s_paths.TryGetValue(id, out var expectedPath) || path != expectedPath
                || !HasDatedSource(row)
                || !(confidence == "verified" || confidence == "unverified"
                    && row.TryGetProperty("owner_ruling", out var ruling) && !string.IsNullOrWhiteSpace(ruling.GetString())))
                throw new InvalidDataException($"Unapproved October gear projection {id}.");
            var effects = row.GetProperty("effects").EnumerateArray().Select(ReadEffect).ToArray();
            if (effects.Length is < 1 or > 8
                || effects.Select(effect => effect.Name).Distinct(StringComparer.Ordinal).Count() != effects.Length
                || effects.Any(effect => effect.Kind == "spell") && (id != 164173 || effects.Length != 1)
                || !items.TryAdd(id, new Gear(id, path, effects)))
                throw new InvalidDataException($"Empty or duplicated October gear projection {id}.");
        }
        foreach (var row in document.GetProperty("unverified_exclusions").EnumerateArray()) {
            var id = row.GetProperty("template_id").GetUInt32();
            if (row.GetProperty("enabled").GetBoolean() || !s_paths.TryGetValue(id, out var path)
                || row.GetProperty("path").GetString() != path || !HasDatedSource(row)
                || row.GetProperty("confidence").GetString() != "unverified"
                || string.IsNullOrWhiteSpace(row.GetProperty("reason").GetString())
                || items.ContainsKey(id) || !excluded.Add(id))
                throw new InvalidDataException($"Ambiguous excluded October gear projection {id}.");
        }
        if (!s_paths.Keys.ToHashSet().SetEquals(items.Keys.Concat(excluded)))
            throw new InvalidDataException("October arena gear projection does not account for every legacy identity.");
        s_excluded = excluded;
        s_items = items;
        Logger.Information("Classic arena gear: {0} sourced items, {1} withheld pending historical ruling.",
            Logger.Args(items.Count, s_excluded.Count));
    }

    private static IReadOnlyDictionary<uint, string> CatalogPaths() {
        var paths = new Dictionary<uint, string>();
        // Native/catalog identity metadata only: the unallocated Tier3 id100521 is skipped.
        for (uint id = 100477; id <= 100582; id++) {
            if (id == 100521) continue;
            var ordinal = id - 100477 - (id > 100521 ? 1u : 0u);
            var tier = ordinal / 21 + 1; var section = ordinal % 21 / 7; var number = ordinal % 7 + 1;
            var (folder, suffix) = section switch { 0 => ("Hats", "Hat"), 1 => ("Robes", "Robe"), _ => ("Shoes", "Shoe") };
            paths[id] = $"ObjectData/PVP/Tier{tier}/{folder}/PvP-T{tier}-{suffix}-{number:000}.xml";
        }
        foreach (var (id, suffix) in new[] { (164160u, "Hat-001"), (164168u, "Hat-002"),
            (164169u, "Robe-001"), (164170u, "Robe-002"), (164171u, "Shoe-001"),
            (164172u, "Shoe-002"), (164173u, "Amulet-001") })
            paths[id] = $"ObjectData/PVP/Season01/PvP-S01-{suffix}.xml";
        return paths;
    }

    private static bool HasDatedSource(JsonElement row) {
        if (!DateTimeOffset.TryParseExact(row.GetProperty("source_timestamp").GetString(),
            "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) || date > s_cutoff
            || !Uri.TryCreate(row.GetProperty("source").GetString(), UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps || source.Host != "wizard101.fandom.com"
            || source.UserInfo.Length != 0 || source.Fragment.Length != 0 || !source.IsDefaultPort)
            return false;
        var revision = row.GetProperty("source_revision").GetUInt32();
        var references = source.Query.TrimStart('?').Split('&').Where(part => part.StartsWith("oldid=", StringComparison.Ordinal)).ToArray();
        return revision > 0 && references.Length == 1 && references[0] == "oldid=" + revision.ToString(CultureInfo.InvariantCulture);
    }

    private static Effect ReadEffect(JsonElement effect) {
        var kind = effect.GetProperty("kind").GetString()!;
        var name = effect.GetProperty("effect_name").GetString()!;
        if (kind == "spell" && name == "ProvideSpell") {
            var spell = effect.GetProperty("spell_name").GetString()!;
            var copies = effect.GetProperty("copies").GetInt32();
            if (spell != "Infection" || copies != 2 || effect.GetProperty("unit").GetString() != "cards"
                || effect.GetProperty("value").GetInt32() != 2)
                throw new InvalidDataException("Unexpected arena item card binding.");
            return new Effect(kind, name, 0, "", "", 0, spell, copies);
        }
        if (kind != "stat" || !Regex.IsMatch(name, "^Canonical(?:(?:All|Fire|Ice|Storm|Life|Myth|Death|Balance)(?:Accuracy|ReduceDamage|Damage|FlatDamage)|MaxHealth|PowerPip|MaxManaPercentReduce)$"))
            throw new InvalidDataException("Unexpected arena stat binding.");
        var index = effect.GetProperty("lookup_index").GetInt32();
        var category = effect.GetProperty("category").GetString()!;
        var table = effect.GetProperty("stat_table").GetString()!;
        var value = effect.GetProperty("canonical_value").GetSingle();
        var bare = name["Canonical".Length..];
        var unit = bare == "MaxHealth" || bare.EndsWith("FlatDamage", StringComparison.Ordinal) ? "flat" : "percent";
        var displayed = effect.GetProperty("value").GetDouble();
        var expectedValue = displayed * (unit == "flat" ? 1 : .01) * (bare == "MaxManaPercentReduce" ? -1 : 1);
        var identity = StatIdentity(bare);
        if (index is < 0 or > 2999 || category != identity.Category || table != identity.Table
            || !float.IsFinite(value) || !double.IsFinite(displayed) || effect.GetProperty("unit").GetString() != unit
            || Math.Abs(value - expectedValue) > .000001)
            throw new InvalidDataException("Invalid arena canonical stat binding.");
        return new Effect(kind, name, index, category, table, value, "", 0);
    }

    private static (string Category, string Table) StatIdentity(string name) {
        if (name == "MaxHealth") return (name, "MaxHealth_AllSchools");
        if (name == "PowerPip") return ("PowerPips", "PowerPips_AllSchools");
        if (name == "MaxManaPercentReduce") return (name, "ManaReduce_AllSchools");
        var parts = Regex.Match(name, "^(All|Fire|Ice|Storm|Life|Myth|Death|Balance)(.+)$");
        var school = parts.Groups[1].Value; var family = parts.Groups[2].Value;
        var category = name is "LifeFlatDamage" or "BalanceFlatDamage" ? name : name.Replace("FlatDamage", "Damage");
        return (category, family + "_" + (school == "All" ? "AllSchools" : school));
    }

    private static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.Profile.Id == "october-2010-arc1";

    internal static bool IsWithheld(uint id) => Active && s_excluded.Contains(id);

    internal static void Apply(CoreTemplate? template, string? path) {
        if (!Active || template is not WizItemTemplate item || s_items is null
            || !s_items.TryGetValue(item.m_templateID, out var gear)) return;
        if (!string.Equals(path, gear.Path, StringComparison.Ordinal))
            throw new InvalidDataException($"October arena gear {gear.Id} resolved to an unexpected template path.");
        // Only these effects change. Native requirements, prices, cards outside this effect list and item identity stay intact.
        item.m_equipEffects = gear.Effects.Select(MakeEffect).ToList();
    }

    private static GameEffectInfo MakeEffect(Effect effect) => effect.Kind == "stat"
        ? new StatisticEffectInfo { m_effectName = effect.Name, m_lookupIndex = effect.Index }
        : new ProvideSpellEffectInfo { m_effectName = effect.Name, m_spellName = effect.Spell, m_numSpells = effect.Copies };

    // Fail before login starts if this server's actual cached canonical tables differ from the client's verified bindings.
    internal static void ValidateAfterResources() {
        if (!Active || s_items is null) return;
        foreach (var gear in s_items.Values) {
            foreach (var effect in gear.Effects.Where(e => e.Kind == "stat")) {
                WizStatisticEffectTemplate? native;
                try { native = CanonicalStatEffects.GetEffectTemplate(effect.Name) as WizStatisticEffectTemplate; }
                catch (NullReferenceException exception) {
                    throw new InvalidDataException("October arena canonical effect resources were not loaded.", exception);
                }
                var table = native is null || native.m_statTableName != effect.Table
                    ? null : GameEffectRuleData.GetWizardStatTable(native.m_statTableName);
                if (native is null || native.m_effectCategory != effect.Category || native.m_statTableName != effect.Table || table?.m_statVector is null
                    || effect.Index >= table.m_statVector.Count
                    || !float.IsFinite(table.m_statVector[effect.Index])
                    || Math.Abs(table.m_statVector[effect.Index] - effect.CanonicalValue) > 0.000001f)
                    throw new InvalidDataException($"October arena gear {gear.Id}: canonical binding {effect.Name}[{effect.Index}] differs from the verified client.");
            }
            var template = CoreObjectFactory.GetCoreTemplate(gear.Id) as WizItemTemplate;
            if (template is null || template.m_templateID != gear.Id || CoreObjectFactory.GetTemplatePath(gear.Id) != gear.Path
                || template.m_equipEffects is null || template.m_equipEffects.Count != gear.Effects.Length
                || !template.m_equipEffects.Select((effect, index) => MatchesEffect(effect, gear.Effects[index])).All(match => match))
                throw new InvalidDataException($"October arena gear {gear.Id} failed its actual server template projection.");
        }
        Logger.Information("Classic arena gear: {0} actual server templates and canonical bindings verified.", Logger.Args(s_items.Count));
    }

    private static bool MatchesEffect(GameEffectInfo? actual, Effect expected) => expected.Kind == "stat"
        ? actual?.GetType() == typeof(StatisticEffectInfo) && actual is StatisticEffectInfo statistic
            && statistic.m_effectName == expected.Name && statistic.m_lookupIndex == expected.Index
        : actual?.GetType() == typeof(ProvideSpellEffectInfo) && actual is ProvideSpellEffectInfo spell
            && spell.m_effectName == expected.Name && spell.m_spellName == expected.Spell && spell.m_numSpells == expected.Copies;
}
