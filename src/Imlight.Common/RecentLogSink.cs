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
 * RECENT LOG EVENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: keeps the last warnings and errors in memory for the admin
 * dashboard ("recent errors").
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using Serilog.Core;
using Serilog.Events;

namespace Imlight.Common;

/// <summary>One kept log event.</summary>
public sealed record RecentLogEvent(DateTimeOffset Time, string Level, string Message);

/// <summary>
/// A Serilog sink that keeps the most recent warnings and errors.
/// </summary>
public sealed class RecentLogSink : ILogEventSink {

    public const int Capacity = 300;

    public static RecentLogSink Instance { get; } = new();

    private readonly Queue<RecentLogEvent> _events = new();
    private readonly object _lock = new();

    /// <summary>Warnings and errors since the server started.</summary>
    public long WarningCount { get; private set; }

    public long ErrorCount { get; private set; }

    public void Emit(LogEvent logEvent) {
        if (logEvent.Level < LogEventLevel.Warning) {
            return;
        }

        var message = logEvent.RenderMessage();
        if (logEvent.Exception is { } exception) {
            message += " | " + exception.GetType().Name + ": " + exception.Message;
        }

        if (message.Length > 2000) {
            message = message[..2000] + "...";
        }

        lock (_lock) {
            if (logEvent.Level >= LogEventLevel.Error) {
                ErrorCount++;
            }
            else {
                WarningCount++;
            }

            _events.Enqueue(new RecentLogEvent(logEvent.Timestamp, logEvent.Level.ToString(), message));
            while (_events.Count > Capacity) {
                _events.Dequeue();
            }
        }
    }

    /// <summary>The kept events, newest first.</summary>
    public IReadOnlyList<RecentLogEvent> Snapshot() {
        lock (_lock) {
            var list = new List<RecentLogEvent>(_events);
            list.Reverse();

            return list;
        }
    }

}
