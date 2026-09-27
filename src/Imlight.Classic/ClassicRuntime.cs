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
 * CLASSIC RULES
 * ========================================================================
 * 
 * PURPOSE:
 * The process-wide holder of the active ClassicRules and audit sink,
 * written once at startup and read by every hook.
 * 
 * USAGE EXAMPLE:
 * ClassicRuntime.Initialize(rules, sink, auditVerbose: false);   // once, in Program.Main
 * if (!ClassicRuntime.Rules.CriticalAndBlockEnabled) { ... }    // anywhere after
 * 
 * NOTE:
 * Rules throws until Initialize runs, so an ordering mistake fails loudly
 * instead of silently dropping the level cap. Initialize runs before the
 * actor system starts and everything it holds is immutable, so reads need
 * no locking.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Threading;
using Imlight.Classic.Audit;

namespace Imlight.Classic;

/// <summary>
/// The active classic rules for this process.
/// </summary>
public static class ClassicRuntime {

    private static readonly Lock s_initLock = new();
    private static volatile ClassicRules? s_rules;
    private static volatile IClassicAuditSink s_audit = NullAuditSink.Instance;
    private static volatile bool s_auditVerbose;

    /// <summary>
    /// The active rules.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="Initialize"/>.</exception>
    public static ClassicRules Rules
        => s_rules ?? throw new InvalidOperationException(
            "ClassicRuntime.Rules was read before ClassicRuntime.Initialize; the classic profile must load first.");

    /// <summary>
    /// True once <see cref="Initialize"/> has run.
    /// </summary>
    public static bool IsInitialized => s_rules is not null;

    /// <summary>
    /// True when the active profile restricts anything.
    /// </summary>
    public static bool IsActive => Rules.IsRestricted;

    /// <summary>
    /// True when allowed zone decisions are audited too.
    /// </summary>
    public static bool AuditVerbose => s_auditVerbose;

    /// <summary>
    /// Sets the active rules. Call once, before anything reads <see cref="Rules"/>.
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <param name="audit">Where audit entries go; null drops them.</param>
    /// <param name="auditVerbose">Also audit allowed zone decisions.</param>
    /// <exception cref="InvalidOperationException">Called a second time.</exception>
    public static void Initialize(ClassicRules rules, IClassicAuditSink? audit = null, bool auditVerbose = false) {
        ArgumentNullException.ThrowIfNull(rules);
        lock (s_initLock) {
            if (s_rules is not null) {
                throw new InvalidOperationException("ClassicRuntime is already initialized.");
            }

            s_audit = audit ?? NullAuditSink.Instance;
            s_auditVerbose = auditVerbose;
            s_rules = rules;
        }
    }

    /// <summary>
    /// Records a rules decision. Verbose entries are dropped unless <see cref="AuditVerbose"/> is on.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public static void Audit(ClassicAuditEntry entry) {
        if (entry.Verbose && !s_auditVerbose) {
            return;
        }

        s_audit.Write(entry);
    }

    internal static void ResetForTests() {
        lock (s_initLock) {
            s_rules = null;
            s_audit = NullAuditSink.Instance;
            s_auditVerbose = false;
        }
    }

}
