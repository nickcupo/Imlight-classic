// CLASSIC: actor construction errors must reach the same recent-error sink as ordinary server errors.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ServerLogger = Imlight.Common.Logger;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ActorLoggingTests {
    [Fact]
    public async Task ActorConstructorErrorReachesTheServersRecentErrorSink() {
        var restoreConfiguration = SaveConfiguration();
        var previousGlobal = Serilog.Log.Logger;
        LoggingLevelSwitch? levelSwitch = null;
        LogEventLevel previousLevel = default;
        ActorSystem? system = null;
        using var unconfigured = new LoggerConfiguration().CreateLogger();
        try {
            var path = Path.Combine(Path.GetTempPath(), "imlight-actor-logging-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(path, "[Logging]\nLogLevel=ERROR\nLogPath=" +
                Path.Combine(Path.GetTempPath(), "imlight-actor-logging.log") + "\n");
            ConfigurationManager.Initialize(path);
            // Existing runtime fixtures may already have initialized the logger at a higher minimum.
            levelSwitch = (LoggingLevelSwitch) typeof(ServerLogger).GetField("s_levelSwitch",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            previousLevel = levelSwitch.MinimumLevel;
            levelSwitch.MinimumLevel = LogEventLevel.Error;
            Serilog.Log.Logger = unconfigured;
            ServerLogger.ConfigureGlobal();
            var before = RecentLogSink.Instance.ErrorCount;
            var started = DateTimeOffset.UtcNow;
            system = ActorSystem.Create("actor-logging-" + Guid.NewGuid().ToString("N"), """
                akka {
                  actor.provider = local
                  loglevel = ERROR
                  loggers = ["Akka.Logger.Serilog.SerilogLogger, Akka.Logger.Serilog"]
                  log-dead-letters = off
                  log-dead-letters-during-shutdown = off
                }
                """);
            system.ActorOf(Props.Create<ConstructorErrorActor>(), "constructor-error-fixture");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!RecordedConstructorError() && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(RecentLogSink.Instance.ErrorCount > before,
                "Akka actor construction errors did not reach the server error sink.");
            Assert.True(RecordedConstructorError(),
                "The server error sink did not retain the actor construction exception.");

            bool RecordedConstructorError() => RecentLogSink.Instance.Snapshot().Any(entry =>
                entry.Time >= started && entry.Level == "Error" &&
                entry.Message.Contains(nameof(ActorInitializationException), StringComparison.Ordinal));
        }
        finally {
            try {
                if (system is not null) await system.Terminate().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally {
                Serilog.Log.Logger = previousGlobal;
                if (levelSwitch is not null) levelSwitch.MinimumLevel = previousLevel;
                restoreConfiguration();
            }
        }
    }

    private sealed class ConstructorErrorActor : ReceiveActor {
        public ConstructorErrorActor() => throw new InvalidOperationException("Expected constructor error fixture.");
    }

    private static Action SaveConfiguration() {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var settings = (Dictionary<string, string>) typeof(ConfigurationManager).GetField("s_settings", flags)!.GetValue(null)!;
        var sections = (Dictionary<string, Dictionary<string, string>>) typeof(ConfigurationManager).GetField("s_sections", flags)!.GetValue(null)!;
        var oldSettings = new Dictionary<string, string>(settings, settings.Comparer);
        var oldSections = new Dictionary<string, Dictionary<string, string>>(sections, sections.Comparer);
        var initialized = typeof(ConfigurationManager).GetField("s_isInitialized", flags)!;
        var path = typeof(ConfigurationManager).GetField("s_configFilePath", flags)!;
        var oldInitialized = initialized.GetValue(null);
        var oldPath = path.GetValue(null);
        return () => {
            settings.Clear(); foreach (var row in oldSettings) settings.Add(row.Key, row.Value);
            sections.Clear(); foreach (var row in oldSections) sections.Add(row.Key, row.Value);
            initialized.SetValue(null, oldInitialized);
            path.SetValue(null, oldPath);
        };
    }
}
