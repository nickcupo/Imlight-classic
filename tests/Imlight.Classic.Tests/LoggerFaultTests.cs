// CLASSIC: logging a failed actor must not wait on Task.Result or retain its argument graph in ambient context.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Imlight.Common;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ServerLogger = Imlight.Common.Logger;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LoggerFaultTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingTaskAndCancellationFaultReturnWithoutRetainingContext(bool directTask) {
        using var configuration = new LoggingFixture();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception fault;
        try { throw new InvalidOperationException("expected inner fault fixture", new TaskCanceledException(pending.Task)); }
        catch (Exception exception) { fault = exception; }
        object value = directTask ? pending.Task : fault;
        var marker = "logger-fault-fixture-" + Guid.NewGuid().ToString("N");
        var sink = new CaptureSink();
        using var following = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var logged = Task.Run(() => {
            using var outer = LogContext.PushProperty("OuterLogContext", marker);
            following.Error("before fixture");
            ServerLogger.Error("{Marker}: {Diagnostic}", ServerLogger.Args(marker, value));
            following.Error("after fixture");
        });
        try {
            await logged.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(pending.Task.IsCompleted, "Logging must finish while the task is still pending.");
            var recorded = Assert.Single(RecentLogSink.Instance.Snapshot().Where(entry =>
                entry.Message.Contains(marker, StringComparison.Ordinal)));
            Assert.Contains(directTask ? "Status=WaitingForActivation" : nameof(TaskCanceledException), recorded.Message);
            if (!directTask) Assert.Contains("expected inner fault fixture", recorded.Message);
            Assert.Equal(2, sink.Events.Count);
            var before = sink.Events[0].Properties;
            var after = sink.Events[1].Properties;
            Assert.Equal(before.Keys.OrderBy(key => key), after.Keys.OrderBy(key => key));
            foreach (var property in before) Assert.Equal(property.Value.ToString(), after[property.Key].ToString());
            Assert.Equal(marker, Assert.IsType<ScalarValue>(after["OuterLogContext"]).Value);
        }
        finally {
            // A regression can block on Result; release only the synthetic task so the test host can finish safely.
            pending.TrySetResult(1);
            await logged.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void ExceptionDiagnosticsAreBoundedAndDoNotCallSubtypeToString() {
        using var configuration = new LoggingFixture();
        var format = typeof(ServerLogger).GetMethod("ExceptionDiagnostic", BindingFlags.NonPublic | BindingFlags.Static)!;
        var exception = new UnsafeToStringException("expected cause", new InvalidOperationException("inner cause"));
        var diagnostic = Assert.IsType<string>(format.Invoke(null, [exception]));
        Assert.Contains(nameof(UnsafeToStringException), diagnostic);
        Assert.Contains("expected cause", diagnostic);
        Assert.Contains("inner cause", diagnostic);
        var longDiagnostic = Assert.IsType<string>(format.Invoke(null, [new InvalidOperationException(new string('x', 20000))]));
        Assert.InRange(longDiagnostic.Length, 1, 8192);
        Assert.EndsWith("(diagnostic truncated)", longDiagnostic);
        var many = new AggregateException("many causes", Enumerable.Range(0, 20)
            .Select(index => new InvalidOperationException("cause " + index)));
        var manyDiagnostic = Assert.IsType<string>(format.Invoke(null, [many]));
        Assert.EndsWith("(diagnostic truncated)", manyDiagnostic);
    }

    private sealed class UnsafeToStringException(string message, Exception inner) : Exception(message, inner) {
        public override string ToString() => throw new InvalidOperationException("Subtype ToString must not run.");
    }

    private sealed class CaptureSink : ILogEventSink {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class LoggingFixture : IDisposable {
        private readonly Action _restoreConfiguration;
        private readonly LoggingLevelSwitch _levelSwitch;
        private readonly LogEventLevel _previousLevel;
        public LoggingFixture() {
            _restoreConfiguration = SaveConfiguration();
            var path = Path.Combine(Path.GetTempPath(), "imlight-logger-fault-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(path, "[Logging]\nLogLevel=ERROR\nLogPath=" +
                Path.Combine(Path.GetTempPath(), "imlight-logger-fault.log") + "\n");
            ConfigurationManager.Initialize(path);
            _levelSwitch = (LoggingLevelSwitch) typeof(ServerLogger).GetField("s_levelSwitch",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _previousLevel = _levelSwitch.MinimumLevel;
            _levelSwitch.MinimumLevel = LogEventLevel.Error;
        }
        public void Dispose() {
            _levelSwitch.MinimumLevel = _previousLevel;
            _restoreConfiguration();
        }
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
