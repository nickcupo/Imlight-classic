// CLASSIC: XP banked at an old level cap is reset once when the cap rises (October 2010 notes; owner 2026-10-16
// "reset it"). Plan and per-wizard rule as units; the migration against an isolated embedded RavenDB.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Imlight.Classic.Admin;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Embedded;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class XpCapSettlementTests {

    [Theory]
    [InlineData(null, 50, null, 50)]    // today's live database under october-2010-arc1: record 50, touch nobody
    [InlineData(50, 50, null, null)]    // october again: nothing
    [InlineData(null, 60, 50, 60)]      // first start under celestia-2010: reset level-50 banks
    [InlineData(50, 60, 50, 60)]
    [InlineData(60, 60, null, null)]    // second start under celestia-2010: nothing (once)
    [InlineData(60, 50, null, 50)]      // back to october: record the lower cap, touch nobody
    [InlineData(60, 70, 60, 70)]        // a later raise settles the level-60 banks
    [InlineData(50, null, null, null)]  // dev-unrestricted (no cap): nothing, record kept
    [InlineData(null, null, null, null)]
    public void PlanRecordsTheCapAndSweepsOnlyOnARaise(int? recorded, int? current, int? sweep, int? record) {
        var plan = XpCapSettlement.Plan(recorded, current);
        Assert.Equal(sweep, plan.SweepFromCap);
        Assert.Equal(record, plan.CapToRecord);
    }

    [Theory]
    [InlineData(50, 681129, 636530)]   // a full level-50 bank goes back to the level-50 total
    [InlineData(50, 636531, 636530)]
    [InlineData(50, 636530, null)]     // nothing banked
    [InlineData(49, 636000, null)]     // below the old cap: ordinary progress
    [InlineData(55, 900000, null)]     // above the old cap: earned under a higher cap
    public void OnlyAWizardExactlyAtTheOldCapWithBankedXpChanges(int level, int xp, int? expected)
        => Assert.Equal(expected, XpCapSettlement.Settled(level, xp, 50, 636530));

    [Fact]
    public void AnUnknownLevelTotalChangesNothing()
        => Assert.Null(XpCapSettlement.Settled(50, 681129, 50, 0));

    [Fact]
    public void NoPlayerDataSchemaBumpSoRollbackNeedsNoBackupRestore() {
        // The record is a new document no older build loads, and a wizard changes one existing number: schema 4 stays.
        Assert.Equal(4, PlayerDataSchema.Current);
        Assert.Equal("Meta/XpCapSettlement", XpCapSettlement.DocumentId);
        Assert.Equal(50, XpCapSettlement.UnrecordedCap);
    }

}

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class XpCapMigrationTests(ITestOutputHelper output) {

    private const int Total50 = 636530;
    private static int XpToReach(int level) => level == 50 ? Total50 : level == 60 ? 1205910 : 0;

    private static readonly (string Id, ulong CharId, int Level, int Xp)[] s_wizards = [
        ("xpcap/wizard/full", 1, 50, 681129),       // a full FillBar bank at 50
        ("xpcap/wizard/part", 2, 50, 650000),       // part of a bank
        ("xpcap/wizard/flat", 3, 50, Total50),      // nothing banked
        ("xpcap/wizard/below", 4, 49, 600000),      // below the old cap
        ("xpcap/wizard/fresh", 5, 1, 0),
    ];

    [Fact]
    public void ResetsOnceUnderARaiseAndIsANoOpUnderTheOldCap() {
        using var database = new Database(output);
        database.Seed(s_wizards);

        // october-2010-arc1 (cap 50), the live profile: only the record "50" is written.
        var october = XpCapMigration.Run(database.Store, 50, XpToReach, dryRun: false);
        Assert.Null(october.SweptFromCap);
        Assert.Equal(0, october.Wizards);
        Assert.Equal(s_wizards.ToDictionary(w => w.Id, w => w.Xp), database.Xp());
        Assert.Equal(50, database.Marker()!.Cap);

        // A dry run under celestia-2010 counts and writes nothing.
        var dry = XpCapMigration.Run(database.Store, 60, XpToReach, dryRun: true);
        Assert.Equal(2, dry.Wizards);
        Assert.Equal(681129 - Total50 + 650000 - Total50, dry.XpRemoved);
        Assert.Equal(s_wizards.ToDictionary(w => w.Id, w => w.Xp), database.Xp());
        Assert.Equal(50, database.Marker()!.Cap);

        // celestia-2010 (cap 60): the two banks reset, nobody else changes, the record is 60.
        var celestia = XpCapMigration.Run(database.Store, 60, XpToReach, dryRun: false);
        Assert.Equal(50, celestia.SweptFromCap);
        Assert.Equal(2, celestia.Wizards);
        var after = database.Xp();
        Assert.Equal(Total50, after["xpcap/wizard/full"]);
        Assert.Equal(Total50, after["xpcap/wizard/part"]);
        Assert.Equal(Total50, after["xpcap/wizard/flat"]);
        Assert.Equal(600000, after["xpcap/wizard/below"]);
        Assert.Equal(0, after["xpcap/wizard/fresh"]);
        Assert.Equal(60, database.Marker()!.Cap);
        Assert.Equal(2, database.Marker()!.LastReset);
        // Levels and every other field are untouched.
        Assert.Equal(s_wizards.ToDictionary(w => w.Id, w => w.Level), database.Levels());
        Assert.All(database.Raw().Values, doc => Assert.Equal("kept", doc["Marker"]!.Value<string>()));

        // XP earned at level 50 under the new cap is never taken again: the migration ran once.
        database.SetXp("xpcap/wizard/full", 670000);
        var again = XpCapMigration.Run(database.Store, 60, XpToReach, dryRun: false);
        Assert.True(again.SweptFromCap is null && again.Wizards == 0);
        Assert.Equal(670000, database.Xp()["xpcap/wizard/full"]);
    }

    [Fact]
    public void AnUnknownLevelTotalLeavesEverythingAndTheRecordForTheNextStart() {
        using var database = new Database(output);
        database.Seed(s_wizards);

        var result = XpCapMigration.Run(database.Store, 60, _ => 0, dryRun: false);
        Assert.Equal(0, result.Wizards);
        Assert.Null(database.Marker());
        Assert.Equal(s_wizards.ToDictionary(w => w.Id, w => w.Xp), database.Xp());
    }

    private sealed class Database : IDisposable {
        private readonly EmbeddedServer _server;
        internal readonly IDocumentStore Store;
        private readonly List<string> _ids = [];

        internal Database(ITestOutputHelper output) {
            var root = Path.Combine(Path.GetTempPath(), "w101c-xp-cap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var config = Path.Combine(root, "xp-cap-fixture.ini");
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(root, "fixture.log")}\n"
                + "[Database]\nDatabaseWaitForNonStaleResultsTimeout=30\n");
            ConfigurationManager.Initialize(config);
            output.WriteLine($"XP cap migration database and logs: {root}");
            var constructor = typeof(EmbeddedServer).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, Type.EmptyTypes, modifiers: null);
            Assert.NotNull(constructor);
            _server = (EmbeddedServer) constructor.Invoke(null);
            Assert.NotSame(EmbeddedServer.Instance, _server);
            var serverDirectory = Path.Combine(AppContext.BaseDirectory, "RavenDBServer");
            Assert.True(File.Exists(Path.Combine(serverDirectory, "Raven.Server.dll")), "Bundled Raven server must be available.");
            var userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var options = new ServerOptions {
                DataDirectory = Path.Combine(root, "data"), LogsPath = Path.Combine(root, "logs"),
                ServerUrl = "http://127.0.0.1:0", ServerDirectory = serverDirectory,
                DotNetPath = !string.IsNullOrWhiteSpace(host) ? host : File.Exists(userDotnet) ? userDotnet : "dotnet",
                MaxServerStartupTimeDuration = TimeSpan.FromSeconds(45), GracefulShutdownTimeout = TimeSpan.FromSeconds(10),
            };
            try {
                _server.StartServer(options);
                Store = _server.GetDocumentStore(new DatabaseOptions("xp-cap-" + Guid.NewGuid().ToString("N")) {
                    Conventions = new DocumentConventions { MaxNumberOfRequestsPerSession = 100,
                        WaitForNonStaleResultsTimeout = TimeSpan.FromSeconds(30) },
                });
            }
            catch { _server.Dispose(); throw; }
        }

        // Saved Wizards as the server stores them: the same field paths (MagicSchoolBehavior.Level/ExperiencePoints),
        // plus an unrelated field that must survive the patch.
        internal void Seed(IEnumerable<(string Id, ulong CharId, int Level, int Xp)> wizards) {
            using var session = Store.OpenSession();
            foreach (var (id, charId, level, xp) in wizards) {
                var wizard = new Imlight.CoreLib.WizardData.Models.Player.Wizard {
                    CharId = charId, AccountId = charId + 100, Zone = "WizardCity/WC_Hub",
                    MagicSchoolBehavior = new() { Level = level, ExperiencePoints = xp, TrainingPoints = 3 },
                };
                session.Store(wizard, id);
                session.Advanced.GetMetadataFor(wizard)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardCollection.CollectionName;
                _ids.Add(id);
            }

            session.SaveChanges();
            foreach (var id in _ids) {
                Store.Operations.Send(new Raven.Client.Documents.Operations.PatchOperation(id, null,
                    new Raven.Client.Documents.Operations.PatchRequest { Script = "this.Marker = 'kept';" }));
            }
        }

        internal Dictionary<string, JObject> Raw() {
            using var session = Store.OpenSession();
            using var stream = new MemoryStream();
            session.Advanced.LoadIntoStream(_ids, stream);
            var document = JObject.Parse(Encoding.UTF8.GetString(stream.ToArray()));

            return ((JArray) document["Results"]!).Children<JObject>()
                .ToDictionary(row => row["@metadata"]!["@id"]!.Value<string>()!, row => row);
        }

        internal Dictionary<string, int> Xp()
            => Raw().ToDictionary(p => p.Key, p => p.Value["MagicSchoolBehavior"]!["ExperiencePoints"]!.Value<int>());

        internal Dictionary<string, int> Levels()
            => Raw().ToDictionary(p => p.Key, p => p.Value["MagicSchoolBehavior"]!["Level"]!.Value<int>());

        internal void SetXp(string id, int xp)
            => Store.Operations.Send(new Raven.Client.Documents.Operations.PatchOperation(id, null,
                new Raven.Client.Documents.Operations.PatchRequest {
                    Script = "this.MagicSchoolBehavior.ExperiencePoints = args.xp;", Values = { ["xp"] = xp } }));

        internal XpCapSettlementMarker? Marker() {
            using var session = Store.OpenSession();

            return session.Load<XpCapSettlementMarker>(XpCapSettlement.DocumentId);
        }

        public void Dispose() {
            Store.Dispose();
            _server.Dispose();
        }
    }

}
