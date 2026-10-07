// CLASSIC: preview metadata, real template projection and older-profile preservation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaGearProjectionTests : IDisposable {
    public ArenaGearProjectionTests() {
        var config = Path.Combine(Path.GetTempPath(), "w101c-gear-projection-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-gear-projection.log")}\n");
        ConfigurationManager.Initialize(config); // Retain authored fixtures; no permanent cleanup.
    }
    public void Dispose() {
        ClassicArenaGearTemplates.Initialize(null, "late-2009");
        ClassicRuntime.ResetForTests();
    }

    private static void October() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
        ClassicArenaGearTemplates.Initialize(ClassicDataFixture.Root, "october-2010-arc1");
    }

    [Fact]
    public void ClientAndServerUseExactlyTheSameSourcedProjection() {
        var clientPath = Path.Combine(Path.GetDirectoryName(ClassicDataFixture.Root)!, "tools", "mac", "arena-stats-october-2010.json");
        Assert.Equal(File.ReadAllText(clientPath), File.ReadAllText(Path.Combine(ClassicDataFixture.Root, ClassicArenaGearTemplates.RelativePath)));
    }

    [Theory]
    [InlineData(100478u, "ObjectData/PVP/Tier1/Hats/PvP-T1-Hat-002.xml", "CanonicalIceDamage", 100)]
    [InlineData(100485u, "ObjectData/PVP/Tier1/Robes/PvP-T1-Robe-002.xml", "CanonicalAllReduceDamage", 102)]
    [InlineData(100498u, "ObjectData/PVP/Tier2/Hats/PvP-T2-Hat-001.xml", "CanonicalFireReduceDamage", 101)]
    [InlineData(100501u, "ObjectData/PVP/Tier2/Hats/PvP-T2-Hat-004.xml", "CanonicalLifeReduceDamage", 102)]
    [InlineData(100504u, "ObjectData/PVP/Tier2/Hats/PvP-T2-Hat-007.xml", "CanonicalBalanceReduceDamage", 102)]
    [InlineData(100518u, "ObjectData/PVP/Tier2/Shoes/PvP-T2-Shoe-007.xml", "CanonicalBalanceAccuracy", 100)]
    [InlineData(100541u, "ObjectData/PVP/Tier4/Hats/PvP-T4-Hat-001.xml", "CanonicalPowerPip", 102)]
    public void DatedCorrectionsChangeOnlyEquipmentEffects(uint id, string path, string effect, int index) {
        October();
        var purchase = new RequirementList(); var equip = new RequirementList();
        var item = new WizItemTemplate { m_templateID = id, m_arenaPointCost = 777, m_pvpCurrencyCost = 778,
            m_baseCost = 779, m_purchaseRequirements = purchase, m_equipRequirements = equip,
            m_equipEffects = [] };
        ClassicArenaGearTemplates.Apply(item, path);
        var projected = Assert.Single(item.m_equipEffects.OfType<StatisticEffectInfo>(), e => e.m_effectName == effect);
        Assert.Equal(index, projected.m_lookupIndex);
        Assert.Same(purchase, item.m_purchaseRequirements); Assert.Same(equip, item.m_equipRequirements);
        Assert.Equal(777, item.m_arenaPointCost); Assert.Equal(778, item.m_pvpCurrencyCost); Assert.Equal(779, item.m_baseCost);
        var before = item.m_equipEffects.OfType<StatisticEffectInfo>().Select(e => (e.m_effectName, e.m_lookupIndex)).ToArray();
        ClassicArenaGearTemplates.Apply(item, path);
        Assert.Equal(before, item.m_equipEffects.OfType<StatisticEffectInfo>().Select(e => (e.m_effectName, e.m_lookupIndex)).ToArray());
    }

    [Fact]
    public void DiademRetainsItsTwoNativeInfectionCards() {
        October();
        var item = new WizItemTemplate { m_templateID = 164173u, m_equipEffects = [] };
        ClassicArenaGearTemplates.Apply(item, "ObjectData/PVP/Season01/PvP-S01-Amulet-001.xml");
        var cards = Assert.IsType<ProvideSpellEffectInfo>(Assert.Single(item.m_equipEffects));
        Assert.Equal("Infection", cards.m_spellName); Assert.Equal(2, cards.m_numSpells);
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("dev-unrestricted")]
    public void OlderAndUnrestrictedProfilesKeepTheirExistingEffects(string profile) {
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
        ClassicArenaGearTemplates.Initialize(null, profile);
        var item = new WizItemTemplate { m_templateID = 100477u, m_equipEffects = [new StatisticEffectInfo { m_effectName = "Authored sentinel" }] };
        var original = item.m_equipEffects;
        ClassicArenaGearTemplates.Apply(item, "ObjectData/PVP/Tier1/Hats/PvP-T1-Hat-001.xml");
        Assert.Same(original, item.m_equipEffects);
        Assert.False(ClassicArenaGearTemplates.IsWithheld(100540));
    }

    [Fact]
    public void WrongTemplatePathCannotUseAnApprovedIdentity() {
        October();
        var item = new WizItemTemplate { m_templateID = 100477u, m_equipEffects = [] };
        Assert.Throws<InvalidDataException>(() => ClassicArenaGearTemplates.Apply(item, "ObjectData/Authored.xml"));
        Assert.Empty(item.m_equipEffects);
    }

    [Fact]
    public void CompleteIdentityCoverageAllowsExplicitOwnerApprovalWithoutFixedActiveCount() {
        October();
        var root = ChangedMap(document => {
            var excluded = document["unverified_exclusions"]!.AsArray();
            var approved = excluded[0]!.DeepClone().AsObject(); excluded.RemoveAt(0);
            approved["enabled"] = true; approved["owner_ruling"] = "Authored test ruling for provisional values";
            document["items"]!.AsArray().Add(approved);
        });
        ClassicArenaGearTemplates.Initialize(root, "october-2010-arc1");
        Assert.False(ClassicArenaGearTemplates.IsWithheld(100510));
        Assert.False(ClassicArenaGearTemplates.IsWithheld(100540));
        Assert.False(ClassicArenaGearTemplates.IsWithheld(164172));
        var item = new WizItemTemplate { m_templateID = 100510u, m_equipEffects = [] };
        ClassicArenaGearTemplates.Apply(item, "ObjectData/PVP/Tier2/Robes/PvP-T2-Robe-006.xml");
        Assert.Equal(2, item.m_equipEffects.Count);
    }

    [Fact]
    public void MissingOrConflictingCoverageCutoffSourcesAndBindingsRefuseWholeProjection() {
        October();
        var changes = new (string Name, Action<JsonObject> Change)[] {
            ("missing active", d => d["items"]!.AsArray().RemoveAt(0)),
            ("missing excluded", d => d["unverified_exclusions"]!.AsArray().RemoveAt(0)),
            ("no identities", d => { d["items"] = new JsonArray(); d["unverified_exclusions"] = new JsonArray(); }),
            ("duplicate active", d => d["items"]!.AsArray().Add(d["items"]![0]!.DeepClone())),
            ("ambiguous excluded", d => d["unverified_exclusions"]!.AsArray().Add(d["items"]![0]!.DeepClone())),
            ("future cutoff", d => d["cutoff"] = "2011-10-31T23:59:59Z"),
            ("later source", d => d["items"]![0]!["source_timestamp"] = "2010-11-01T00:00:00Z"),
            ("source revision", d => d["items"]![0]!["source_revision"] = 1),
            ("source host", d => d["items"]![0]!["source"] = "https://example.com/?oldid=114858"),
            ("wrong path", d => d["items"]![0]!["path"] = d["items"]![1]!["path"]!.GetValue<string>()),
            ("unallocated identity", d => d["items"]![0]!["template_id"] = 100521),
            ("unknown excluded", d => d["unverified_exclusions"]![0]!["template_id"] = 1),
            ("duplicate effect", d => d["items"]![0]!["effects"]!.AsArray().Add(d["items"]![0]!["effects"]![0]!.DeepClone())),
            ("empty effects", d => d["items"]![0]!["effects"] = new JsonArray()),
            ("too many effects", d => { var list = d["items"]![0]!["effects"]!.AsArray(); while (list.Count < 9) list.Add(list[0]!.DeepClone()); }),
            ("unsupported binding", d => d["items"]![0]!["effects"]![0]!["effect_name"] = "CanonicalLaterEffect"),
            ("index bound", d => d["items"]![0]!["effects"]![0]!["lookup_index"] = 3000),
            ("category table confusion", d => d["items"]![0]!["effects"]![0]!["stat_table"] = "FireReduceDamage"),
            ("unit confusion", d => d["items"]![0]!["effects"]![0]!["unit"] = "flat"),
            ("display value", d => d["items"]![0]!["effects"]![0]!["value"] = 300),
            ("unapproved candidate", d => {
                var list = d["unverified_exclusions"]!.AsArray(); var candidate = list[0]!.DeepClone(); list.RemoveAt(0);
                candidate!["enabled"] = true; d["items"]!.AsArray().Add(candidate);
            }),
        };
        foreach (var (name, change) in changes) {
            var root = ChangedMap(change);
            Assert.Throws<InvalidDataException>(() => ClassicArenaGearTemplates.Initialize(root, "october-2010-arc1"));
            var item = new WizItemTemplate { m_templateID = 100477u, m_equipEffects = [] };
            ClassicArenaGearTemplates.Apply(item, "ObjectData/PVP/Tier1/Hats/PvP-T1-Hat-001.xml");
            Assert.Empty(item.m_equipEffects); Assert.False(ClassicArenaGearTemplates.IsWithheld(100540));
            Assert.NotEmpty(name);
        }
    }

    [Fact]
    public void ActualResourceValidationAcceptsCompleteAuthoredCanonicalAndCachedProjection() {
        using var resources = new ResourceFixture();
        ClassicArenaGearTemplates.ValidateAfterResources();
        Assert.Same(resources.Items[100477], CoreObjectFactory.GetCoreTemplate(100477));
    }

    [Theory]
    [InlineData("category")]
    [InlineData("table")]
    [InlineData("raw value")]
    [InlineData("missing effect")]
    [InlineData("missing table")]
    [InlineData("missing vector")]
    [InlineData("missing canonical resource")]
    public void ActualResourceValidationRefusesCanonicalMismatch(string fault) {
        using var resources = new ResourceFixture();
        var effect = resources.Effects.OfType<WizStatisticEffectTemplate>().First();
        var table = resources.Tables[effect.m_statTableName];
        var info = Assert.IsType<StatisticEffectInfo>(resources.Items[100477].m_equipEffects[0]);
        switch (fault) {
            case "category": effect.m_effectCategory = "Damage"; break;
            case "table": effect.m_statTableName = "Wrong_Table"; break;
            case "raw value": table.m_statVector[info.m_lookupIndex] += .5f; break;
            case "missing effect": resources.Effects.Remove(effect); break;
            case "missing table": resources.Tables.Remove(effect.m_statTableName); break;
            case "missing vector": table.m_statVector = null!; break;
            case "missing canonical resource": resources.ClearCanonical(); break;
        }
        Assert.Throws<InvalidDataException>(ClassicArenaGearTemplates.ValidateAfterResources);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("index")]
    [InlineData("order")]
    [InlineData("class")]
    [InlineData("body identity")]
    [InlineData("manifest path")]
    [InlineData("null effects")]
    [InlineData("card count")]
    [InlineData("card name")]
    public void ActualResourceValidationRefusesEqualCountWrongCachedContent(string fault) {
        using var resources = new ResourceFixture();
        var item = resources.Items[100477]; var info = Assert.IsType<StatisticEffectInfo>(item.m_equipEffects[0]);
        switch (fault) {
            case "name": item.m_equipEffects[0] = info with { m_effectName = "CanonicalIceDamage" }; break;
            case "index": item.m_equipEffects[0] = info with { m_lookupIndex = info.m_lookupIndex + 1 }; break;
            case "order": item.m_equipEffects.Reverse(); break;
            case "class": item.m_equipEffects[0] = new ProvideSpellEffectInfo(); break;
            case "body identity": item.m_templateID = 100478u; break;
            case "manifest path": resources.Locations[100477].m_filename = "ObjectData/Unexpected.xml"; break;
            case "null effects": item.m_equipEffects = null!; break;
            case "card count": resources.Items[164173].m_equipEffects[0] = new ProvideSpellEffectInfo {
                m_effectName = "ProvideSpell", m_spellName = "Infection", m_numSpells = 3 }; break;
            case "card name": resources.Items[164173].m_equipEffects[0] = new ProvideSpellEffectInfo {
                m_effectName = "ProvideSpell", m_spellName = "OtherSpell", m_numSpells = 2 }; break;
        }
        Assert.Throws<InvalidDataException>(ClassicArenaGearTemplates.ValidateAfterResources);
    }

    private static string ChangedMap(Action<JsonObject> change) {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(ClassicDataFixture.Root, ClassicArenaGearTemplates.RelativePath)))!.AsObject();
        change(document);
        var root = Path.Combine(Path.GetTempPath(), "w101c-gear-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pvp"));
        File.WriteAllText(Path.Combine(root, ClassicArenaGearTemplates.RelativePath), document.ToJsonString());
        return root; // Recoverable authored fixtures remain on disk.
    }

    private sealed class ResourceFixture : IDisposable {
        private readonly FieldInfo _canonical = typeof(CanonicalStatEffects).GetField("s_effectTable", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo _tables = typeof(GameEffectRuleData).GetField("s_statTables", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo _locations = typeof(CoreObjectFactory).GetField("s_templateLocations", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _oldCanonical, _oldTables, _oldLocations;
        private readonly IDictionary<ulong, CoreTemplate> _cache;
        private readonly Dictionary<ulong, CoreTemplate?> _prior = [];
        internal readonly Dictionary<uint, WizItemTemplate> Items = [];
        internal readonly Dictionary<ulong, TemplateLocation> Locations = [];
        internal readonly List<GameEffectTemplate> Effects = [];
        internal readonly Dictionary<string, WizardStatTable> Tables = [];
        internal ResourceFixture() {
            October(); _oldCanonical = _canonical.GetValue(null); _oldTables = _tables.GetValue(null); _oldLocations = _locations.GetValue(null);
            _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassicDataFixture.Root, ClassicArenaGearTemplates.RelativePath)));
            foreach (var row in data.RootElement.GetProperty("items").EnumerateArray()) {
                var id = row.GetProperty("template_id").GetUInt32(); var path = row.GetProperty("path").GetString()!;
                var item = new WizItemTemplate { m_templateID = id, m_equipEffects = [] };
                ClassicArenaGearTemplates.Apply(item, path); Items[id] = item;
                _prior[id] = _cache.TryGetValue(id, out var prior) ? prior : null; _cache[id] = item;
                Locations[id] = new TemplateLocation { m_id = id, m_filename = path };
                foreach (var binding in row.GetProperty("effects").EnumerateArray()) {
                    if (binding.GetProperty("kind").GetString() != "stat") continue;
                    var name = binding.GetProperty("effect_name").GetString()!; var table = binding.GetProperty("stat_table").GetString()!;
                    if (!Effects.Any(effect => effect.m_effectName == name)) Effects.Add(new WizStatisticEffectTemplate {
                        m_effectName = name, m_effectCategory = binding.GetProperty("category").GetString()!, m_statTableName = table });
                    if (!Tables.TryGetValue(table, out var vector)) Tables[table] = vector = new WizardStatTable { m_statVector = [] };
                    var index = binding.GetProperty("lookup_index").GetInt32();
                    while (vector.m_statVector.Count <= index) vector.m_statVector.Add(0);
                    vector.m_statVector[index] = binding.GetProperty("canonical_value").GetSingle();
                }
            }
            _canonical.SetValue(null, new GameEffectTemplateList { m_effectTemplates = Effects });
            _tables.SetValue(null, Tables); _locations.SetValue(null, Locations);
        }
        internal void ClearCanonical() => _canonical.SetValue(null, null);
        public void Dispose() {
            _canonical.SetValue(null, _oldCanonical); _tables.SetValue(null, _oldTables); _locations.SetValue(null, _oldLocations);
            foreach (var (id, prior) in _prior) { if (prior is null) _cache.Remove(id); else _cache[id] = prior; }
        }
    }
}
