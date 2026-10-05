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
 * ADMIN DASHBOARD TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The dashboard's password check and the page's safety rules.
 *
 * NOTE:
 * Login, broadcast and safe restart (waiting for a fight) were run against
 * the rig; see playbot-reports/features.md.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.IO;
using Imlight.Classic.Admin;
using Imlight.CoreLib.Classic.Admin;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ServerAdminTests {

    public ServerAdminTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}server-admin-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PasswordMustMatchTheStoredHash() {
        var stored = DatabaseUtilities.CreateHashedPassword("correct horse");
        Assert.True(AdminDashboard.PasswordMatches(stored, "correct horse"));
        Assert.False(AdminDashboard.PasswordMatches(stored, "correct horse "));
        Assert.False(AdminDashboard.PasswordMatches(stored, ""));
        Assert.False(AdminDashboard.PasswordMatches(null, "correct horse"));
    }

    [Fact]
    public void PageNeverWritesServerTextAsHtml() {
        // Server values go through textContent; the page has no innerHTML at all.
        Assert.DoesNotContain("innerHTML", AdminDashboardPage.Html);
        Assert.Contains("'X-W101C':'1'", AdminDashboardPage.Html);
    }

    // A backup's plan stays (the dashboard shows it), but it is carried out once: the 1 s tick used to run it again
    // every ~7 s, closing every session each time, for as long as the process lived.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackupIsCarriedOutOnce(bool requestCanBeWritten) {
        var dir = Path.Combine(Path.GetTempPath(), "w101c-admin-" + Guid.NewGuid().ToString("N"));
        if (!requestCanBeWritten) {
            File.WriteAllText(dir, "a file where the control directory should be");
        }

        ServerAdmin.ControlDirectoryOverride = dir;
        try {
            var before = ServerAdmin.Executions;
            ServerAdmin.Schedule(RestartKind.Backup, TimeSpan.Zero, "test", "tests");
            Assert.Equal(before + 1, ServerAdmin.Executions);

            ServerAdmin.Tick();
            ServerAdmin.Tick();
            Assert.Equal(before + 1, ServerAdmin.Executions);
            Assert.StartsWith(requestCanBeWritten ? "backup requested" : "backup request failed", ServerAdmin.Status()!.State);
            Assert.Equal(requestCanBeWritten, File.Exists(Path.Combine(dir, "backup-request")));

            Assert.True(ServerAdmin.Cancel("tests"));
            Assert.Null(ServerAdmin.Status());
        }
        finally {
            ServerAdmin.ControlDirectoryOverride = null;
            ServerAdmin.Cancel("tests");
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, true);
            }
            else if (File.Exists(dir)) {
                File.Delete(dir);
            }
        }
    }

}
