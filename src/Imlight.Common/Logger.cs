/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * LOGGING
 * ========================================================================
 * 
 * PURPOSE:
 * Provides a comprehensive logging mechanism using Serilog, with configurable 
 * log levels, multiple output destinations, and rich contextual information.
 * 
 * USAGE EXAMPLE:
 * Logger.Information("User logged in", Logger.Args(userId, username));
 * Logger.Error("Database connection failed", Logger.Args(connectionString));
 * 
 * NOTE:
 * - Requires ConfigurationManager to be initialized before use
 * - Supports console, file, and Seq logging
 * - Automatically captures caller information (class, method, line number)
 * 
 * TODO:
 * - 
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace Imlight.Common;

/// <summary>
/// Provides a static logging utility with multiple log levels and automatic contextual information capture.
/// </summary>
public class Logger {

    private const byte MaxCallerNameLength = 40;
    private static readonly string s_path = ConfigurationManager.Settings["Logging.LogPath"].AsString()
        ?? Path.Combine(Directory.GetCurrentDirectory(), "logs", "log.txt");
    private static readonly string s_logFormat = ConfigurationManager.Settings["Logging.LogFormat"].AsString()
        ?? "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
    private static readonly string s_logLevel = ConfigurationManager.Settings["Logging.LogLevel"].AsString()
        ?? "INFO";
    private static readonly int s_retainedFiles
        = int.TryParse(ConfigurationManager.Settings["Logging.RetainedFileCount"].AsString(), out var files) && files > 0 ? files : 31;
    private static readonly long s_fileSizeLimitMb
        = long.TryParse(ConfigurationManager.Settings["Logging.FileSizeLimitMB"].AsString(), out var mb) && mb > 0 ? mb : 1024;
    private static readonly string s_seqUrl = ConfigurationManager.Settings["Logging.SeqSinkUrl"].AsString()
        ?? "http://localhost:5341";
    private static readonly LoggingLevelSwitch s_levelSwitch = new() { MinimumLevel = GetLogLevel(s_logLevel) };

    public static ILogger Log { get; } = new LoggerConfiguration()
        .MinimumLevel.ControlledBy(s_levelSwitch)
        .Enrich.FromLogContext()
        .Enrich.WithThreadId()
        .Enrich.WithThreadName()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate: s_logFormat)
        // CLASSIC: bounded file logs ([Logging] RetainedFileCount, FileSizeLimitMB).
        .WriteTo.File(s_path, rollingInterval: RollingInterval.Day, retainedFileCountLimit: s_retainedFiles,
            fileSizeLimitBytes: s_fileSizeLimitMb * 1024L * 1024L, rollOnFileSizeLimit: true)
        .WriteTo.Seq(s_seqUrl)
        .WriteTo.Sink(RecentLogSink.Instance, LogEventLevel.Warning) // CLASSIC: the admin dashboard's recent errors.
        .CreateLogger();

    /// <summary>CLASSIC: routes Akka and other global Serilog users through the server's existing sinks.</summary>
    public static void ConfigureGlobal() => Serilog.Log.Logger = Log;

    /// <summary>
    /// Returns an array of objects that can be used to format a log message.
    /// </summary>
    /// <param name="args">The objects to include in the log message.</param>
    /// <returns>An array of objects that can be used to format a log message.</returns>
    public static object[] Args(params object[] args) => args;

    /// <summary>
    /// Logs a verbose message with optional arguments, along with the calling class, method, and line number.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional arguments to include in the message.</param>
    /// <param name="callingClass">The name of the calling class (automatically populated by the compiler).</param>
    /// <param name="callingMethod">The name of the calling method (automatically populated by the compiler).</param>
    /// <param name="lineNumber">The line number of the calling method (automatically populated by the compiler).</param>
    public static void Verbose(string message,
                               object[]? args = null,
                               [CallerFilePath] string callingClass = "",
                               [CallerMemberName] string callingMethod = "",
                               [CallerLineNumber] int lineNumber = 0) {
        WriteLog(callingClass, callingMethod, lineNumber, LogEventLevel.Verbose, message, args ?? Array.Empty<object>());
    }

    /// <summary>
    /// Logs a debug message with the specified message and optional arguments.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional arguments to include in the log message.</param>
    /// <param name="callingClass">The name of the class that called this method (automatically populated by the compiler).</param>
    /// <param name="callingMethod">The name of the method that called this method (automatically populated by the compiler).</param>
    /// <param name="lineNumber">The line number in the source code where this method was called (automatically populated by the compiler).</param>
    public static void Debug(string message,
                              object[]? args = null,
                              [CallerFilePath] string callingClass = "",
                              [CallerMemberName] string callingMethod = "",
                              [CallerLineNumber] int lineNumber = 0) {
        WriteLog(callingClass, callingMethod, lineNumber, LogEventLevel.Debug, message, args ?? Array.Empty<object>());
    }

    /// <summary>
    /// Logs an information message with the specified message and optional arguments.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional arguments to include in the log message.</param>
    /// <param name="callingClass">The name of the class that called the logger method (automatically populated by the compiler).</param>
    /// <param name="callingMethod">The name of the method that called the logger method (automatically populated by the compiler).</param>
    /// <param name="lineNumber">The line number in the source code where the logger method was called (automatically populated by the compiler).</param>
    public static void Information(string message,
                                   object[]? args = null,
                                   [CallerFilePath] string callingClass = "",
                                   [CallerMemberName] string callingMethod = "",
                                   [CallerLineNumber] int lineNumber = 0) {
        WriteLog(callingClass, callingMethod, lineNumber, LogEventLevel.Information, message, args ?? Array.Empty<object>());
    }

    /// <summary>
    /// Logs a warning message along with the calling class, method and line number.
    /// </summary>
    /// <param name="message">The warning message to log.</param>
    /// <param name="args">Optional arguments to format the warning message.</param>
    /// <param name="callingClass">The calling class name (automatically populated by the compiler).</param>
    /// <param name="callingMethod">The calling method name (automatically populated by the compiler).</param>
    /// <param name="lineNumber">The line number where the warning was logged (automatically populated by the compiler).</param>
    public static void Warning(string message,
                               object[]? args = null,
                               [CallerFilePath] string callingClass = "",
                               [CallerMemberName] string callingMethod = "",
                               [CallerLineNumber] int lineNumber = 0) {
        WriteLog(callingClass, callingMethod, lineNumber, LogEventLevel.Warning, message, args ?? Array.Empty<object>());
    }

    /// <summary>
    /// Logs an error message along with the calling class, method and line number.
    /// </summary>
    /// <param name="message">The error message to log.</param>
    /// <param name="args">Optional arguments to format the error message.</param>
    /// <param name="callingClass">The name of the calling class (automatically populated by the compiler).</param>
    /// <param name="callingMethod">The name of the calling method (automatically populated by the compiler).</param>
    /// <param name="lineNumber">The line number of the calling method (automatically populated by the compiler).</param>
    public static void Error(string message,
                             object[]? args = null,
                             [CallerFilePath] string callingClass = "",
                             [CallerMemberName] string callingMethod = "",
                             [CallerLineNumber] int lineNumber = 0) {
        WriteLog(callingClass, callingMethod, lineNumber, LogEventLevel.Error, message, args ?? Array.Empty<object>());
    }

    /// <summary>
    /// Logs a message with the Fatal log level.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional arguments to format the message.</param>
    /// <param name="callingClass">The name of the class that called this method. This parameter is optional and is automatically populated by the compiler.</param>
    /// <param name="callingMethod">The name of the method that called this method. This parameter is optional and is automatically populated by the compiler.</param>
    /// <param name="lineNumber">The line number in the source code file where this method was called. This parameter is optional and is automatically populated by the compiler.</param>
    public static void Fatal(string message,
                             object[]? args = null,
                             [CallerFilePath] string callingClass = "",
                             [CallerMemberName] string callingMethod = "",
                             [CallerLineNumber] int lineNumber = 0) {
        WriteLog(callingClass, callingMethod, lineNumber, LogEventLevel.Fatal, message, args ?? Array.Empty<object>());
    }

    private static void WriteLog(string callingClass,
                                 string callingMethod,
                                 int lineNumber,
                                 LogEventLevel logLevel,
                                 string message,
                                 params object[] values) {
        // Trim the calling class to just the class name.
        callingClass = TrimCallingClass(callingClass);
        var callingSpace = $"{callingClass}.{callingMethod}@L{lineNumber}";

        // If the calling space is too long, trim it.
        callingSpace = GetConsistentSpacedName(callingSpace);

        // CLASSIC: exception/actor graphs can contain unfinished Task<T> objects. Destructuring them calls
        // Task.Result and can deadlock session supervision before it closes the socket or releases its slot.
        // Preserve ordinary scalar formatting; use bounded diagnostics for exceptions and status only for tasks.
        var safeValues = new object[values.Length];
        for (var i = 0; i < values.Length; i++) safeValues[i] = SafeLogValue(values[i]);
        var scopes = new List<IDisposable>(safeValues.Length + 1);
        try {
            scopes.Add(LogContext.PushProperty("CallingSpace", callingSpace));
            for (var i = 0; i < safeValues.Length; i++) {
                var propertyName = values[i]?.GetType().Name ?? "Unknown";
                // Never reflect arbitrary reference objects into ambient context. Exceptions and tasks are already
                // scalar summaries; the other arguments retain Serilog's ordinary non-destructured formatting.
                scopes.Add(LogContext.PushProperty(propertyName, safeValues[i], destructureObjects: false));
            }

            Log.Write(logLevel, message, safeValues);
        }
        finally {
            // CLASSIC: every push belongs to this event, not subsequent actor messages on the same context.
            for (var i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose();
        }
    }

    private static object SafeLogValue(object value) => value switch {
        Exception exception => ExceptionDiagnostic(exception),
        Task task => $"{task.GetType().Name}(Id={task.Id}, Status={task.Status})",
        _ => value,
    };

    private static string ExceptionDiagnostic(Exception exception) {
        const int maxCharacters = 8192;
        const int maxExceptions = 8;
        var text = new StringBuilder();
        var pending = new Queue<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        var count = 0;
        while (pending.Count > 0 && count < maxExceptions && text.Length < maxCharacters) {
            var current = pending.Dequeue();
            if (!seen.Add(current)) continue;
            if (count++ > 0) Append("\n ---> ");
            Append(current.GetType().FullName ?? current.GetType().Name);
            // Read only the standard exception diagnostic fields. Never inspect Data or subtype properties
            // (ActorRef, Task, Result, etc.), and never call an arbitrary exception's ToString override.
            try { Append(": " + current.Message); } catch (Exception) { Append(": (message unavailable)"); }
            try {
                if (current.StackTrace is { } stack) { Append("\n"); Append(stack); }
            }
            catch (Exception) { Append("\n(stack unavailable)"); }
            if (current is AggregateException aggregate) {
                for (var i = 0; i < aggregate.InnerExceptions.Count && i < maxExceptions; i++)
                    pending.Enqueue(aggregate.InnerExceptions[i]);
            }
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
        if (pending.Count > 0 || text.Length == maxCharacters) {
            const string suffix = "\n(diagnostic truncated)";
            if (text.Length > maxCharacters - suffix.Length) text.Length = maxCharacters - suffix.Length;
            text.Append(suffix);
        }
        return text.ToString();

        void Append(string value) {
            var available = maxCharacters - text.Length;
            if (available > 0) text.Append(value, 0, Math.Min(value.Length, available));
        }
    }

    private static LogEventLevel GetLogLevel(string logLevelString) {
        // Clean the log level string.
        logLevelString = logLevelString.Trim();
        logLevelString = logLevelString.Replace("\"", string.Empty);
        return logLevelString.ToUpperInvariant() switch {
            "TRACE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "INFO" => LogEventLevel.Information,
            "WARNING" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "FATAL" => LogEventLevel.Fatal,
            _ => throw new Exception($"Invalid log level: {logLevelString}"),
        };
    }

    private static string TrimCallingClass(string filePath) {
        // If the file path doesn't contain a '.cs', then it's not a C# file.
        if (!filePath.Contains(".cs")) {
            return filePath;
        }

        // Scope to the area between the final directory separator character and the '.cs' extension.
        var separatorChar = Path.DirectorySeparatorChar;
        var startIndex = filePath.LastIndexOf(separatorChar) + 1;
        var length = filePath.LastIndexOf(".cs", StringComparison.Ordinal) - startIndex;
        return filePath.Substring(startIndex, length);
    }

    private static string GetConsistentSpacedName(string name) {
        if (!string.IsNullOrEmpty(name)) {
            return name.Length > MaxCallerNameLength
                ? name[..MaxCallerNameLength]
                : name.PadRight(MaxCallerNameLength);
        }

        return "Main".PadRight(MaxCallerNameLength);
    }
    
}
