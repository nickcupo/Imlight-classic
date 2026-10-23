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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Classic.Admin;
using Imlight.Classic.Ambient;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
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

    [Fact]
    public void OnlineListUsesAmbientRegistryRatherThanAnAccountNameOrIdPattern() {
        // A human can have an ambient-looking id or account name. An ambient wizard can have an ordinary id.
        const ulong humanId = 0xA3B1E00000000991;
        const ulong simulatedId = 782345;
        var record = AmbientWizardRecord.From(AmbientIdentity.Generate(991, "WizardCity/WC_Hub",
            new NameTableSizes(100, 100, 50, 50), (1, 12)), simulatedId);
        var ambient = new AmbientWizard(record, new Wizard { CharId = simulatedId });
        AmbientWizards.Register(ambient);
        try {
            var lookups = new List<ulong>();
            var online = ServerAdmin.OnlineWizards([
                new OnlinePlayer { CharacterId = simulatedId, AccountId = simulatedId, CurrentZone = "WizardCity/WC_Hub" },
                new OnlinePlayer { CharacterId = humanId, AccountId = 7, CurrentZone = "WizardCity/WC_Unicorn", CurrentZoneDisplayName = "Unicorn Way" },
                new OnlinePlayer { CharacterId = 0, AccountId = 8 }, // character selection is not a wizard in the world
            ], new HashSet<ulong> { simulatedId, humanId }, (id, _) => {
                lookups.Add(id);
                return ("ambient-bot", "Zelda");
            });

            Assert.Equal(2, online.Count);
            Assert.Equal(new[] { humanId }, lookups);
            Assert.Equal(humanId, online[0].CharacterId);
            Assert.False(online[0].IsSimulated);
            Assert.Equal("ambient-bot", online[0].Account);
            Assert.Equal("Unicorn Way", online[0].ZoneName);
            Assert.True(online[0].InDuel);
            Assert.True(online[1].IsSimulated);
            Assert.Equal("—", online[1].Account);
            Assert.True(online[1].InDuel);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(online,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Assert.False(json.RootElement[0].GetProperty("isSimulated").GetBoolean());
            Assert.True(json.RootElement[1].GetProperty("isSimulated").GetBoolean());
        }
        finally { AmbientWizards.Unregister(ambient); }
    }

    [Fact]
    public void OnlineListKeepsHumanNamesAndSortingAndAllowsAnEmptyWorld() {
        Assert.Empty(ServerAdmin.OnlineWizards([], new HashSet<ulong>(), (_, _) => throw new Exception("no names to resolve")));
        var online = ServerAdmin.OnlineWizards([
            new OnlinePlayer { CharacterId = 810001, AccountId = 1 },
            new OnlinePlayer { CharacterId = 810002, AccountId = 2 },
        ], new HashSet<ulong>(), (_, account) => ("account " + account, account == 1 ? "Zelda" : "amber"));
        Assert.Equal(new[] { "amber", "Zelda" }, online.Select(w => w.Wizard));
        Assert.All(online, wizard => { Assert.False(wizard.IsSimulated); Assert.False(wizard.InDuel); });
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
