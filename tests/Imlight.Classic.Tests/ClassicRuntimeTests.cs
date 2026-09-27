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
 * CLASSIC RULES TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * ClassicRuntime's contract: no reads before Initialize, one Initialize,
 * and verbose audit entries only with AuditVerbose.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * ClassicRuntime is process-wide, so these tests run in a collection that
 * never runs in parallel with anything else.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using Imlight.Classic.Audit;
using Xunit;

namespace Imlight.Classic.Tests;

[CollectionDefinition(nameof(ClassicRuntimeCollection), DisableParallelization = true)]
public sealed class ClassicRuntimeCollection;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicRuntimeTests : IDisposable {

    public ClassicRuntimeTests() {
        ClassicRuntime.ResetForTests();
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Fact]
    public void RulesThrowBeforeInitialize() {
        Assert.False(ClassicRuntime.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => ClassicRuntime.Rules);
        Assert.Throws<InvalidOperationException>(() => ClassicRuntime.IsActive);
    }

    [Fact]
    public void SecondInitializeThrows() {
        ClassicRuntime.Initialize(ClassicRules.Stock);

        Assert.Same(ClassicRules.Stock, ClassicRuntime.Rules);
        Assert.False(ClassicRuntime.IsActive);
        Assert.Throws<InvalidOperationException>(() => ClassicRuntime.Initialize(ClassicRules.Stock));
    }

    [Fact]
    public void VerboseEntriesNeedAuditVerbose() {
        var sink = new RecordingSink();
        ClassicRuntime.Initialize(ClassicRules.Stock, sink, auditVerbose: false);

        ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneAllowed, 1, "WizardCity/WC_Hub", "open", Verbose: true));
        ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneDenied, 1, "Celestia/CL_Hub", "closed"));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(ClassicAuditKind.ZoneDenied, entry.Kind);
    }

    [Fact]
    public void VerboseEntriesAreKeptWithAuditVerbose() {
        var sink = new RecordingSink();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"), sink, auditVerbose: true);

        ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneAllowed, 1, "WizardCity/WC_Hub", "open", Verbose: true));
        ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.LevelCapReached, 1, "level 50", "capped"));

        Assert.Equal(2, sink.Entries.Count);
        Assert.True(ClassicRuntime.IsActive);
        Assert.True(ClassicRuntime.AuditVerbose);
    }

    private sealed class RecordingSink : IClassicAuditSink {

        public List<ClassicAuditEntry> Entries { get; } = [];

        public void Write(ClassicAuditEntry entry) => Entries.Add(entry);

    }

}
