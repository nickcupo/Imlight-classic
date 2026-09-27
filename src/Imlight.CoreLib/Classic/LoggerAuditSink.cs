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
 * CLASSIC RULES AUDIT
 * ========================================================================
 * 
 * PURPOSE:
 * Writes classic rules audit entries (denials, fallbacks, refusals, the
 * level cap) to Imlight's log.
 * 
 * USAGE EXAMPLE:
 * ClassicRuntime.Initialize(rules, new LoggerAuditSink(), auditVerbose);
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

#nullable enable

using Imlight.Classic.Audit;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

internal sealed class LoggerAuditSink : IClassicAuditSink {

    public void Write(ClassicAuditEntry entry) {
        if (entry.CharId is { } charId) {
            Logger.Information("Classic {Kind}: {Subject}. {Detail} (character {CharId})",
                Logger.Args(entry.Kind.ToString(), entry.Subject, entry.Detail, charId));

            return;
        }

        Logger.Information("Classic {Kind}: {Subject}. {Detail}",
            Logger.Args(entry.Kind.ToString(), entry.Subject, entry.Detail));
    }

}
