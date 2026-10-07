// CLASSIC: original saved arena items must acquire repaired runtime effects without replacement or migration.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.States;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Embedded;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaGearSavedReloadTests(ITestOutputHelper output) {
    private const ulong SandalsOwner = 850001, FootgearOwner = 850002, ForeignOwner = 850003;
    private const ulong SandalsId = (1ul << 48) | 850011, DiademId = (1ul << 48) | 850012;
    private const ulong FootgearId = (1ul << 48) | 850021;
    private const ulong BackpackId = (1ul << 48) | 850031, BankId = (1ul << 48) | 850032;
    private const ulong OrphanId = (1ul << 48) | 850033, ForeignId = (1ul << 48) | 850034;
    private const string SandalsWizard = "saved-arena/wizard/sandals", FootgearWizard = "saved-arena/wizard/footgear";

    [Fact]
    public void OriginalSavedGearReloadsRebuildsAndRemovesEffectsWithoutRewritingAnyDocument() {
        Assert.False(PlayerDatabase.IsCreated);
        using var database = new SavedDatabase(output);
        using var resources = new AuthoredResources();
        Assert.Null(WizardReagentCollection.TestRowsScope.Value);
        Assert.All(resources.Items.Values, item => Assert.Empty(item.m_equipEffects));

        // Seed original IDs, ownership, appearance and saved references while cached templates have no effects.
        // No replacement item factory, purchase service, migration or game archive is used by this regression.
        var originals = new[] {
            Item(SandalsId, 100540, SandalsOwner), Item(DiademId, 164173, SandalsOwner),
            Item(FootgearId, 164172, FootgearOwner), Item(BackpackId, 164172, SandalsOwner),
            Item(BankId, 100540, SandalsOwner), Item(OrphanId, 164173, SandalsOwner),
            Item(ForeignId, 100540, ForeignOwner),
        };
        using (var session = database.Store.OpenSession()) {
            Store(session, SavedWizard(SandalsOwner, [(SandalsId, EquipmentSlotType.Shoes), (DiademId, EquipmentSlotType.Amulet)],
                [BackpackId], [BankId]), SandalsWizard, WizardCollection.CollectionName);
            Store(session, SavedWizard(FootgearOwner, [(FootgearId, EquipmentSlotType.Shoes)], [], []),
                FootgearWizard, WizardCollection.CollectionName);
            foreach (var item in originals) Store(session, item, ItemDocument(item.m_globalID.Full), WizardItemCollection.CollectionName);
            Store(session, new Dictionary<string, object> { ["scalar"] = 917, ["vector"] = new[] { 3, 19, 41 },
                ["nested"] = new Dictionary<string, object> { ["retain"] = "authored original" } },
                "saved-arena/unrelated", "AuthoredUnrelated");
            session.SaveChanges();
        }
        var before = database.RawDocuments();
        Assert.Equal(10, before.Count);
        database.RefuseWrites();
        resources.ActivateOctoberProjection();

        var previousStoreScope = WizardCollection.TestStoreScope.Value;
        Assert.Null(previousStoreScope);
        // AfterDatabaseLoad's clean quest reconciliation also reads a fresh wizard. Bind its existing store
        // seam to this real Raven instance; do not replace item, reagent, quest or hydration queries.
        WizardCollection.TestStoreScope.Value = new(database.OpenReadSession,
            (session, owner) => session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
                .Customize(query => query.WaitForNonStaleResults()).Single(wizard => wizard.CharId == owner));
        try {
            var sandals = Reload(database, SandalsWizard);
            Assert.Equal(new[] { SandalsId, DiademId }, sandals.EquipmentBehavior.EquippedItems.Select(item => item.m_globalID.Full).Order());
            Assert.Equal(BackpackId, Assert.Single(sandals.InventoryBehavior.Items).m_globalID.Full);
            Assert.Equal(BankId, Assert.Single(sandals.StorageBehavior.Items).m_globalID.Full);
            Assert.DoesNotContain(sandals.EquipmentBehavior.EquippedItems.Concat(sandals.InventoryBehavior.Items)
                .Concat(sandals.StorageBehavior.Items), item => item.m_globalID.Full is OrphanId or ForeignId);
            AssertOriginalItem(sandals.EquipmentBehavior.GetItem(SandalsId)!, SandalsId, 100540, SandalsOwner);
            AssertOriginalItem(sandals.EquipmentBehavior.GetItem(DiademId)!, DiademId, 164173, SandalsOwner);
            for (var iteration = 0; iteration < 3; iteration++) {
                CharacterHelper.RecalculateGameStats(sandals);
                AssertSandalsAndDiadem(sandals);
                AssertPreservedState(sandals, 75);
            }
            RemoveRuntimeItem(sandals, DiademId);
            Assert.Empty(sandals.SpellbookBehavior.TemporarySpells);
            Assert.DoesNotContain(sandals.GameEffects.Snapshot(), effect => effect is ProvideSpellEffect);
            Assert.Equal(148, sandals.GameStats.m_baseHitpoints);
            CharacterHelper.RecalculateGameStats(sandals);
            Assert.Empty(sandals.SpellbookBehavior.TemporarySpells);
            Assert.Equal(148, sandals.GameStats.m_baseHitpoints);
            RemoveRuntimeItem(sandals, SandalsId);
            CharacterHelper.RecalculateGameStats(sandals);
            Assert.Equal(100, sandals.GameStats.m_baseHitpoints);
            Assert.Equal(100, sandals.GameStats.m_baseMana);
            Assert.Equal(0, sandals.GameStats.m_powerPipBonusPercentAll);
            Assert.Null(sandals.GameStats.m_dmgBonusPercent);
            Assert.Null(sandals.GameStats.m_dmgReducePercent);
            Assert.Empty(sandals.GameEffects.Snapshot());
            AssertPreservedState(sandals, 75);

            var footgear = Reload(database, FootgearWizard);
            AssertOriginalItem(Assert.Single(footgear.EquipmentBehavior.EquippedItems), FootgearId, 164172, FootgearOwner);
            for (var iteration = 0; iteration < 3; iteration++) {
                CharacterHelper.RecalculateGameStats(footgear);
                AssertFootgear(footgear);
                AssertPreservedState(footgear, 0);
            }
            RemoveRuntimeItem(footgear, FootgearId);
            Assert.Equal(100, footgear.GameStats.m_baseMana);
            Assert.Equal(0, footgear.GameStats.m_currentMana); // Removing gear restores capacity, never refills mana.
            for (var iteration = 0; iteration < 2; iteration++) {
                CharacterHelper.RecalculateGameStats(footgear);
                Assert.Equal(100, footgear.GameStats.m_baseMana);
                Assert.Equal(0, footgear.GameStats.m_powerPipBonusPercentAll);
                Assert.Equal(0, footgear.GameStats.m_accBonusPercentAll);
                Assert.Equal(0, footgear.GameStats.m_dmgBonusPercentAll);
                Assert.Equal(0, footgear.GameStats.m_dmgReducePercentAll);
                Assert.Empty(footgear.GameEffects.Snapshot());
                AssertPreservedState(footgear, 0);
            }

            // Fresh sessions restore the still-original saved references: runtime-only removals cannot rewrite them.
            var reloggedSandals = Reload(database, SandalsWizard);
            CharacterHelper.RecalculateGameStats(reloggedSandals);
            AssertSandalsAndDiadem(reloggedSandals);
            AssertPreservedState(reloggedSandals, 75);
            var reloggedFootgear = Reload(database, FootgearWizard);
            CharacterHelper.RecalculateGameStats(reloggedFootgear);
            AssertFootgear(reloggedFootgear);
            AssertPreservedState(reloggedFootgear, 0);

            var after = database.RawDocuments();
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (id, original) in before) {
                Assert.Equal(original["@metadata"]!["@change-vector"]!.Value<string>(),
                    after[id]["@metadata"]!["@change-vector"]!.Value<string>());
                Assert.True(JToken.DeepEquals(original, after[id]), $"Saved document changed: {id}");
            }
            Assert.Equal(0, database.WriteAttempts);
            Assert.False(PlayerDatabase.IsCreated);
        }
        finally { WizardCollection.TestStoreScope.Value = previousStoreScope; }
    }

    private static Wizard Reload(SavedDatabase database, string document) {
        using var session = database.OpenReadSession();
        var wizard = session.Load<Wizard>(document);
        Assert.NotNull(wizard);
        Assert.Empty(wizard.GameEffects.Snapshot());
        Assert.Empty(wizard.SpellbookBehavior.TemporarySpells);
        Assert.Null(wizard.EquipmentBehavior.EquippedItems); // Runtime cache was not serialized into the save.
        wizard.Account = new Account { Characters = [wizard] };
        Assert.Same(wizard, WizardCollection.HydrateLoadedWizard(wizard, session));
        Assert.Equal(1, wizard.GameStats.Level); // Production AfterDatabaseLoad was reached.
        Assert.Equal(1, wizard.GameStats.m_highestCharacterLevelOnAccount);
        Assert.NotNull(wizard.ObjectStateBehavior);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(wizard));
        return wizard;
    }

    private static void AssertSandalsAndDiadem(Wizard wizard) {
        // Expectations are independent concrete dated numbers, not calculated from the projection JSON.
        Assert.Equal(148, wizard.GameStats.m_baseHitpoints);
        Assert.Equal(.03f, wizard.GameStats.m_powerPipBonusPercentAll, 6);
        Assert.Equal(.02f, wizard.GameStats.m_dmgBonusPercent[6], 6);
        Assert.Equal(.06f, wizard.GameStats.m_dmgReducePercent[6], 6);
        Assert.Equal(100, wizard.GameStats.m_baseMana);
        var grant = Assert.Single(wizard.GameEffects.Snapshot().OfType<ProvideSpellEffect>());
        Assert.Equal("Infection", grant.m_spellName.ToString());
        Assert.Equal(2, grant.m_numSpells);
        Assert.Equal(2, wizard.SpellbookBehavior.TemporarySpells.Count);
        Assert.All(wizard.SpellbookBehavior.TemporarySpells, card => {
            Assert.Equal(StringHash.Compute("Infection"), card.m_spellID); Assert.True(card.m_itemCard);
        });
    }

    private static void AssertFootgear(Wizard wizard) {
        Assert.Equal(100, wizard.GameStats.m_baseHitpoints);
        Assert.Equal(0, wizard.GameStats.m_baseMana);
        Assert.Equal(.04f, wizard.GameStats.m_powerPipBonusPercentAll, 6);
        Assert.Equal(.05f, wizard.GameStats.m_accBonusPercentAll, 6);
        Assert.Equal(.06f, wizard.GameStats.m_dmgBonusPercentAll, 6);
        Assert.Equal(.10f, wizard.GameStats.m_dmgReducePercentAll, 6);
        var info = Assert.Single(ItemHelper.GetItemTemplate(wizard.EquipmentBehavior.GetItem(FootgearId)!).m_equipEffects
            .OfType<StatisticEffectInfo>(), effect => effect.m_effectName == "CanonicalMaxManaPercentReduce");
        Assert.Equal(99, info.m_lookupIndex);
        Assert.Empty(wizard.SpellbookBehavior.TemporarySpells);
    }

    private static void AssertPreservedState(Wizard wizard, int currentMana) {
        Assert.Equal(71, wizard.GameStats.m_currentHitpoints); Assert.Equal(currentMana, wizard.GameStats.m_currentMana);
        Assert.Equal(123, wizard.GameStats.m_currentGold); Assert.Equal(456, wizard.GameStats.m_currentArenaPoints);
        Assert.Equal(456, wizard.GameStats.m_currentPvPCurrency); Assert.Equal(13, wizard.GameStats.m_currentEventCurrency1);
        Assert.Equal(17, wizard.GameStats.m_currentEventCurrency2); Assert.Equal(.75f, wizard.GameStats.m_potionCharge);
        Assert.Equal(new[] { 17, 23 }, wizard.GameStats.m_spellChargeBase);
        Assert.True(wizard.GameStats.m_shadowMagicUnlocked);
        Assert.Equal(77, wizard.MagicSchoolBehavior.TrainingPoints); Assert.Equal(0, wizard.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(8, wizard.PetOwnerBehavior.Energy); Assert.Equal(4, wizard.PetOwnerBehavior.MaxSlots);
        Assert.Equal(new uint[] { 99 }, wizard.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.Equal(new uint[] { 98 }, wizard.SpellbookBehavior.TreasureCardTemplateIds);
    }

    private static void RemoveRuntimeItem(Wizard wizard, ulong id) {
        var item = wizard.EquipmentBehavior.GetItem(id)!;
        var template = ItemHelper.GetItemTemplate(item);
        Assert.True(wizard.EquipmentBehavior.UnequipItem(id));
        Assert.NotEmpty(CharacterEffectHelper.RemoveEffectsFromWizard(wizard, template));
        // This exercises the production runtime removal helpers without the intentional reference save of a
        // player unequip command. The test must prove repair/hydration needs no durable write.
    }

    private static WizClientObjectItem Item(ulong id, uint template, ulong owner) => new() {
        m_globalID = id, m_permID = id, m_templateID = template, m_characterId = owner,
        m_debugName = "Authored saved original", m_primaryColor = 7, m_secondaryColor = 11, m_pattern = 3,
        m_inactiveBehaviors = [],
    };
    private static void AssertOriginalItem(WizClientObjectItem item, ulong id, uint template, ulong owner) {
        Assert.Equal(id, item.m_globalID.Full); Assert.Equal(id, item.m_permID.Full);
        Assert.Equal((ulong)template, item.m_templateID.Full); Assert.Equal(owner, item.m_characterId.Full);
        Assert.Equal(7, item.m_primaryColor); Assert.Equal(11, item.m_secondaryColor); Assert.Equal(3, item.m_pattern);
    }
    private static string ItemDocument(ulong id) => $"saved-arena/item/{id}";
    private static void Store<T>(IDocumentSession session, T entity, string id, string collection) where T : class {
        session.Store(entity, id);
        session.Advanced.GetMetadataFor(entity)[Raven.Client.Constants.Documents.Metadata.Collection] = collection;
    }
    private static Wizard SavedWizard(ulong owner, (ulong Id, EquipmentSlotType Slot)[] gear, ulong[] backpack, ulong[] bank) {
        var pet = new ServerPetOwnerBehavior { MaxSlots = 4 }; pet.SetEnergy(8);
        return new Wizard { CharId = owner, AccountId = owner + 100, Zone = "WizardCity/WC_Hub",
            PlayerNameBehavior = new() { NameOverride = "Authored saved wizard" },
            GameStats = new(default, 1) { m_baseHitpoints = 9090, m_baseMana = 8080, m_currentHitpoints = 71,
                m_currentMana = 75, m_currentGold = 123, m_currentArenaPoints = 456, m_currentPvPCurrency = 456,
                m_currentEventCurrency1 = 13, m_currentEventCurrency2 = 17, m_potionCharge = .75f,
                m_spellChargeBase = [17, 23], m_shadowMagicUnlocked = true },
            MagicSchoolBehavior = new() { Level = 1, ExperiencePoints = 0, TrainingPoints = 77 },
            InventoryBehavior = new() { InventoryItemIds = [.. backpack], Items = [] },
            StorageBehavior = new() { BankItemIds = [.. bank], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [.. gear.Select(item => item.Id)], EquippedItems = [],
                SlotList = [.. gear.Select(item => new EquipmentSlot { ItemId = item.Id, SlotType = item.Slot,
                    ItemName = "Authored saved original", EquippedSince = new DateTime(2010, 10, 1) })] },
            SpellbookBehavior = new() { LearnedSpellTemplateIds = [99], TreasureCardTemplateIds = [98], TemporarySpells = [] },
            AlchemyBehavior = new() { ReagentItemIds = [], Reagents = [], Recipes = [], CraftingSlots = [] },
            QuestBehavior = new(), PetSnackBehavior = new() { SnackItemIds = [], Snacks = [] }, PetOwnerBehavior = pet,
        };
    }

    private sealed class SavedDatabase : IDisposable {
        private readonly EmbeddedServer _server;
        private bool _refuseWrites;
        internal readonly IDocumentStore Store;
        internal int WriteAttempts;
        internal SavedDatabase(ITestOutputHelper output) {
            var root = Path.Combine(Path.GetTempPath(), "w101c-arena-saved-reload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var config = Path.Combine(root, "authored-fixture.ini");
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(root, "fixture.log")}\n"
                + "[Character]\nBaseGoldPouch=1000\nPetEnergyTickInSeconds=3600\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=30\n");
            ConfigurationManager.Initialize(config);
            output.WriteLine($"Retained authored arena reload database and logs: {root}");
            // Raven 7.2 has an internal constructor. Never use or dispose the production singleton.
            var constructor = typeof(EmbeddedServer).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, System.Type.EmptyTypes, modifiers: null);
            Assert.NotNull(constructor);
            _server = (EmbeddedServer)constructor.Invoke(null);
            Assert.NotSame(EmbeddedServer.Instance, _server);
            var serverDirectory = Path.Combine(AppContext.BaseDirectory, "RavenDBServer");
            Assert.True(File.Exists(Path.Combine(serverDirectory, "Raven.Server.dll")), "Bundled Raven server must be available.");
            var userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var options = new ServerOptions { DataDirectory = Path.Combine(root, "data"), LogsPath = Path.Combine(root, "logs"),
                ServerUrl = "http://127.0.0.1:0", ServerDirectory = serverDirectory,
                DotNetPath = !string.IsNullOrWhiteSpace(host) ? host : File.Exists(userDotnet) ? userDotnet : "dotnet",
                MaxServerStartupTimeDuration = TimeSpan.FromSeconds(45), GracefulShutdownTimeout = TimeSpan.FromSeconds(10),
            };
            try {
                _server.StartServer(options);
                Store = _server.GetDocumentStore(new DatabaseOptions("authored-arena-" + Guid.NewGuid().ToString("N")) {
                    Conventions = new DocumentConventions { MaxNumberOfRequestsPerSession = 100,
                        WaitForNonStaleResultsTimeout = TimeSpan.FromSeconds(30) },
                });
            }
            catch { _server.Dispose(); throw; }
        }
        internal IDocumentSession OpenReadSession() {
            var session = Store.OpenSession();
            // Only wait for the isolated database's indexes; the production LINQ hydration is unchanged.
            session.Advanced.OnBeforeQuery += (_, args) => args.QueryCustomization.WaitForNonStaleResults(TimeSpan.FromSeconds(30));
            if (_refuseWrites) {
                session.Advanced.OnBeforeStore += (_, _) => { WriteAttempts++; throw new InvalidOperationException("Saved-gear reload attempted a document write."); };
                session.Advanced.OnBeforeDelete += (_, _) => { WriteAttempts++; throw new InvalidOperationException("Saved-gear reload attempted a document delete."); };
            }
            return session;
        }
        internal void RefuseWrites() => _refuseWrites = true;
        internal Dictionary<string, JToken> RawDocuments() {
            var ids = new[] { SandalsWizard, FootgearWizard, ItemDocument(SandalsId), ItemDocument(DiademId),
                ItemDocument(FootgearId), ItemDocument(BackpackId), ItemDocument(BankId), ItemDocument(OrphanId),
                ItemDocument(ForeignId), "saved-arena/unrelated" };
            using var session = OpenReadSession(); using var stream = new MemoryStream();
            session.Advanced.LoadIntoStream(ids, stream);
            var document = JObject.Parse(Encoding.UTF8.GetString(stream.ToArray()));
            return ((JArray)document["Results"]!).Children<JObject>().ToDictionary(
                row => row["@metadata"]!["@id"]!.Value<string>()!, row => row.DeepClone());
        }
        public void Dispose() {
            Store.Dispose(); _server.Dispose(); // Gracefully stop only this instance; retain all fixture files.
        }
    }

    private sealed class AuthoredResources : IDisposable {
        private readonly FieldInfo _canonical = Field(typeof(CanonicalStatEffects), "s_effectTable");
        private readonly FieldInfo _tables = Field(typeof(GameEffectRuleData), "s_statTables");
        private readonly FieldInfo _levels = Field(typeof(MagicLevelsConfig), "s_playerLevelConfig");
        private readonly object? _oldCanonical, _oldTables, _oldLevels;
        private readonly int _oldMaxLevel = MagicLevelsConfig.MaxLevel;
        private readonly int? _oldMaxXp = MagicLevelsConfig.MaxLevelXp;
        private readonly TemplateManifest _oldManifest = CoreObjectFactory.TemplateManifest;
        private readonly IDictionary<ulong, CoreTemplate> _cache;
        private readonly Dictionary<ulong, CoreTemplate?> _prior = [];
        private readonly Dictionary<int, MagicSchoolTemplate> _schools, _oldSchools;
        private readonly Dictionary<uint, SpellTemplate> _spells, _oldSpells;
        private readonly Dictionary<uint, string> _paths, _oldPaths;
        private readonly Dictionary<string, ObjStateSet> _stateSets;
        private readonly ObjStateSet? _oldPlayerStates;
        private readonly JsonDocument _map;
        internal readonly Dictionary<uint, WizItemTemplate> Items = [];
        internal AuthoredResources() {
            ClassicRuntime.ResetForTests();
            _oldCanonical = _canonical.GetValue(null); _oldTables = _tables.GetValue(null); _oldLevels = _levels.GetValue(null);
            _cache = (IDictionary<ulong, CoreTemplate>)Field(typeof(CoreObjectFactory), "s_templateCache").GetValue(null)!;
            _map = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassicDataFixture.Root, ClassicArenaGearTemplates.RelativePath)));
            foreach (var (id, slot) in new[] { (100540u, "Shoes"), (164172u, "Shoes"), (164173u, "Amulet") }) {
                _prior[id] = _cache.TryGetValue(id, out var old) ? old : null;
                Items[id] = new WizItemTemplate { m_templateID = id, m_objectName = "Authored original gear",
                    m_adjectiveList = [slot], m_behaviors = [], m_equipEffects = [] };
                _cache[id] = Items[id];
            }
            _schools = (Dictionary<int, MagicSchoolTemplate>)Field(typeof(MagicSchools), "s_magicSchools").GetValue(null)!;
            _oldSchools = new(_schools);
            _spells = (Dictionary<uint, SpellTemplate>)Field(typeof(SpellFactory), "s_spellTemplates").GetValue(null)!;
            _paths = (Dictionary<uint, string>)Field(typeof(SpellFactory), "s_spellTemplatePaths").GetValue(null)!;
            _oldSpells = new(_spells); _oldPaths = new(_paths);
            _stateSets = (Dictionary<string, ObjStateSet>)Field(typeof(StateFactory), "s_objectStateSets").GetValue(null)!;
            _oldPlayerStates = _stateSets.TryGetValue("PlayerMobileStates", out var states) ? states : null;
        }
        internal void ActivateOctoberProjection() {
            ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
            ClassicArenaGearTemplates.Initialize(ClassicDataFixture.Root, "october-2010-arc1");
            // AfterDatabaseLoad constructs this runtime behavior. Gear hydration needs only a valid empty
            // category list; no authored fixture claims to reproduce movement/state mechanics.
            _stateSets["PlayerMobileStates"] = new ObjStateSet { m_stateSetName = "PlayerMobileStates", m_categories = [] };
            var effects = new List<GameEffectTemplate>(); var tables = new Dictionary<string, WizardStatTable>();
            foreach (var row in _map.RootElement.GetProperty("items").EnumerateArray().Where(row => Items.ContainsKey(row.GetProperty("template_id").GetUInt32()))) {
                foreach (var effect in row.GetProperty("effects").EnumerateArray()) {
                    if (effect.GetProperty("kind").GetString() != "stat") continue;
                    var name = effect.GetProperty("effect_name").GetString()!; var table = effect.GetProperty("stat_table").GetString()!;
                    if (effects.All(existing => existing.m_effectName != name)) effects.Add(new WizStatisticEffectTemplate {
                        m_effectName = name, m_effectCategory = effect.GetProperty("category").GetString()!, m_statTableName = table });
                    if (!tables.TryGetValue(table, out var vector)) tables[table] = vector = new() { m_statVector = [] };
                    var index = effect.GetProperty("lookup_index").GetInt32();
                    while (vector.m_statVector.Count <= index) vector.m_statVector.Add(0);
                    vector.m_statVector[index] = effect.GetProperty("canonical_value").GetSingle();
                }
                ClassicArenaGearTemplates.Apply(Items[row.GetProperty("template_id").GetUInt32()], row.GetProperty("path").GetString());
            }
            _canonical.SetValue(null, new GameEffectTemplateList { m_effectTemplates = effects }); _tables.SetValue(null, tables);
            _schools.Clear(); var names = new[] { "Fire", "Ice", "Storm", "Life", "Myth", "Death", "Balance" };
            for (var index = 0; index < names.Length; index++) _schools[index] = new() { m_schoolName = names[index], m_schoolIndex = index };
            var school = new ServerWizGameStats(default, 1).MagicSchool.ToString();
            _levels.SetValue(null, new Dictionary<string, List<MagicLevelInfo>> { [school] = [
                new() { m_xpToLevel = 0, m_hitpoints = 100, m_mana = 100, m_pipChance = .25f, m_petEnergy = 50 },
                new() { m_xpToLevel = 100, m_hitpoints = 100, m_mana = 100, m_pipChance = .25f, m_petEnergy = 50 },
                new() { m_xpToLevel = 200, m_hitpoints = 200, m_mana = 200, m_pipChance = .30f, m_petEnergy = 55 },
            ] });
            typeof(MagicLevelsConfig).GetProperty(nameof(MagicLevelsConfig.MaxLevel))!.SetValue(null, 2);
            typeof(MagicLevelsConfig).GetProperty(nameof(MagicLevelsConfig.MaxLevelXp))!.SetValue(null, null);
            _spells.Clear(); _paths.Clear(); var hash = StringHash.Compute("Infection");
            _spells[hash] = new SpellTemplate { m_name = "Infection", m_sMagicSchoolName = "Death", m_accuracy = 100,
                m_spellRank = new SpellRank { m_spellRank = 0 } };
            _paths[hash] = "Spells/AuthoredSavedInfection.xml";
            CoreObjectFactory.TemplateManifest = new() { m_serializedTemplates = [
                new TemplateLocation { m_id = 850099u, m_filename = "Spells/AuthoredSavedInfection.xml" },
            ] };
        }
        private static FieldInfo Field(System.Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        public void Dispose() {
            _canonical.SetValue(null, _oldCanonical); _tables.SetValue(null, _oldTables); _levels.SetValue(null, _oldLevels);
            typeof(MagicLevelsConfig).GetProperty(nameof(MagicLevelsConfig.MaxLevel))!.SetValue(null, _oldMaxLevel);
            typeof(MagicLevelsConfig).GetProperty(nameof(MagicLevelsConfig.MaxLevelXp))!.SetValue(null, _oldMaxXp);
            foreach (var (id, original) in _prior) { if (original is null) _cache.Remove(id); else _cache[id] = original; }
            _schools.Clear(); foreach (var entry in _oldSchools) _schools[entry.Key] = entry.Value;
            _spells.Clear(); foreach (var entry in _oldSpells) _spells[entry.Key] = entry.Value;
            _paths.Clear(); foreach (var entry in _oldPaths) _paths[entry.Key] = entry.Value;
            if (_oldPlayerStates is null) _stateSets.Remove("PlayerMobileStates");
            else _stateSets["PlayerMobileStates"] = _oldPlayerStates;
            CoreObjectFactory.TemplateManifest = _oldManifest; _map.Dispose();
            ClassicRuntime.ResetForTests(); ClassicArenaGearTemplates.Initialize(null, "late-2009");
        }
    }
}
