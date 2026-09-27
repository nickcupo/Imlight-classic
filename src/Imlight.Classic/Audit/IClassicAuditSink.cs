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
 * The audit seam: every rules decision worth a log line (denials, fallbacks,
 * refusals, the level cap) goes through an IClassicAuditSink.
 * 
 * USAGE EXAMPLE:
 * ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneDenied, charId, zone, reason));
 * 
 * NOTE:
 * The library has no logger of its own; CoreLib supplies a sink backed by
 * Imlight's Logger. Verbose entries are dropped unless AuditVerbose is on.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

namespace Imlight.Classic.Audit;

/// <summary>
/// What an audit entry records.
/// </summary>
public enum ClassicAuditKind {
    ZoneDenied,
    ZoneFallback,
    FeatureRefused,
    LevelCapReached,
    ZoneAllowed,
    Startup,
    WorldLocked, // CLASSIC travel: a Spiral Door request for a world the wizard has not unlocked
}

/// <summary>
/// One audited rules decision.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="CharId">The character involved, if any.</param>
/// <param name="Subject">The zone, feature or level concerned.</param>
/// <param name="Detail">Why, including the rule source.</param>
/// <param name="Verbose">True for entries that are only logged with AuditVerbose.</param>
public sealed record ClassicAuditEntry(ClassicAuditKind Kind, ulong? CharId, string Subject, string Detail, bool Verbose = false);

/// <summary>
/// Receives audited rules decisions.
/// </summary>
public interface IClassicAuditSink {

    /// <summary>
    /// Records one entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    void Write(ClassicAuditEntry entry);

}

/// <summary>
/// A sink that drops everything.
/// </summary>
public sealed class NullAuditSink : IClassicAuditSink {

    private NullAuditSink() { }

    public static NullAuditSink Instance { get; } = new();

    public void Write(ClassicAuditEntry entry) { }

}
