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
 * OPERATIONS PERSISTENCE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Second Chance daily uses survive a restart (saved per wizard and game
 * day) and old days are dropped; the nightly backup's request file is
 * taken once by the server.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Game.SecondChance;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class OpsPersistenceTests {

    private sealed class MemoryUseStore : ISecondChanceUseStore {
        public readonly Dictionary<ulong, (DateOnly Day, Dictionary<ulong, int> Uses)> Docs = [];
        public int Loads;

        public IReadOnlyDictionary<ulong, int> Load(ulong charId, DateOnly day) {
            Loads++;
            return Docs.TryGetValue(charId, out var doc) && doc.Day == day ? new Dictionary<ulong, int>(doc.Uses) : [];
        }

        public void Save(ulong charId, DateOnly day, IReadOnlyDictionary<ulong, int> uses)
            => Docs[charId] = (day, new Dictionary<ulong, int>(uses)); // one document per wizard: the latest day only
    }

    public OpsPersistenceTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}ops-persistence-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    private static SecondChanceRules Chests()
        => SecondChanceRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "second-chance-2009.yaml"));

    [Fact]
    public void ARestartDoesNotGiveTheDaysUsesBack() {
        var rules = Chests();
        var chest = rules.ChestByName("DS_MonsterChest_Malistaire")!;
        const string zone = "DragonSpire/DS_A3_Kings/Interiors/DS_MalistaireLair";
        var now = new DateTime(2009, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var store = new MemoryUseStore();

        var before = new SecondChanceChests(() => now, store);
        before.RecordWin(5, zone, 3, [126504]);
        before.Open(5, 42, chest, zone, 3, rules);
        Assert.Equal(ChestRefusal.None, before.TryUse(5, 42, zone, 3, rules, _ => true, out _, out var first));
        Assert.Equal(ChestRefusal.None, before.TryUse(5, 42, zone, 3, rules, _ => true, out _, out _));
        Assert.Equal(50, first);

        // The server restarts: a new state reads the saved uses (the win must be earned again).
        var after = new SecondChanceChests(() => now, store);
        Assert.Equal(rules.DailyUses - 2, after.UsesLeft(5, chest, rules));
        Assert.Equal(rules.CostOfUse(2), after.NextCost(5, chest, rules));
        after.RecordWin(5, zone, 3, [126504]);
        after.Open(5, 42, chest, zone, 3, rules);
        Assert.Equal(ChestRefusal.None, after.TryUse(5, 42, zone, 3, rules, _ => true, out _, out var third));
        Assert.Equal(rules.CostOfUse(2), third);
        Assert.Equal(3, store.Docs[5].Uses[chest.Template]);

        // Read once per wizard and day, not on every look.
        var loads = store.Loads;
        after.UsesLeft(5, chest, rules);
        after.NextCost(5, chest, rules);
        Assert.Equal(loads, store.Loads);
    }

    [Fact]
    public void ANewGameDayStartsFreshAndDropsTheOldDays() {
        var rules = Chests();
        var chest = rules.ChestByName("KT_MonsterChest_Krokopatra")!;
        var now = new DateTime(2009, 10, 17, 23, 0, 0, DateTimeKind.Utc);
        var store = new MemoryUseStore();
        var newYork = Imlight.Classic.Admin.GameClock.Resolve("America/New_York", out _);
        var state = new SecondChanceChests(() => now, store, utc => Imlight.Classic.Admin.GameClock.DayOf(utc, newYork));

        foreach (ulong wizard in new ulong[] { 1, 2, 3 }) {
            state.RecordWin(wizard, chest.Zone, 0, [35433]);
            state.Open(wizard, 7, chest, chest.Zone, 0, rules);
            Assert.Equal(ChestRefusal.None, state.TryUse(wizard, 7, chest.Zone, 0, rules, _ => true, out _, out _));
        }

        Assert.Equal(3, state.UsesHeld);

        // 01:00 UTC is still Oct 17 in New York: the same game day.
        now = new DateTime(2009, 10, 18, 1, 0, 0, DateTimeKind.Utc);
        Assert.Equal(rules.DailyUses - 1, state.UsesLeft(1, chest, rules));

        // 05:00 UTC is Oct 18 in New York: every wizard has the day's uses again, and the old day is gone from memory.
        now = new DateTime(2009, 10, 18, 5, 0, 0, DateTimeKind.Utc);
        Assert.Equal(rules.DailyUses, state.UsesLeft(1, chest, rules));
        Assert.Equal(0, state.UsesHeld);

        // A save on the new day replaces the wizard's old day in the store.
        state.RecordWin(1, chest.Zone, 0, [35433]);
        state.Open(1, 7, chest, chest.Zone, 0, rules);
        state.TryUse(1, 7, chest.Zone, 0, rules, _ => true, out _, out _);
        Assert.Equal(new DateOnly(2009, 10, 18), store.Docs[1].Day);
        Assert.Equal(1, store.Docs[1].Uses[chest.Template]);
    }

    [Fact]
    public void AStoreThatFailsNeverBlocksAUse() {
        var rules = Chests();
        var chest = rules.ChestByName("KT_MonsterChest_Krokopatra")!;
        var state = new SecondChanceChests(store: new FailingStore());
        state.RecordWin(9, chest.Zone, 0, [35433]);
        state.Open(9, 1, chest, chest.Zone, 0, rules);
        Assert.Equal(ChestRefusal.None, state.TryUse(9, 1, chest.Zone, 0, rules, _ => true, out _, out _));
        Assert.Equal(rules.DailyUses - 1, state.UsesLeft(9, chest, rules));
    }

    private sealed class FailingStore : ISecondChanceUseStore {
        public IReadOnlyDictionary<ulong, int> Load(ulong charId, DateOnly day) => throw new IOException("database down");
        public void Save(ulong charId, DateOnly day, IReadOnlyDictionary<ulong, int> uses) => throw new IOException("database down");
    }

    [Fact]
    public void TheNightlyBackupRequestIsTakenOnce() {
        var dir = Directory.CreateTempSubdirectory("w101c-control-").FullName;
        try {
            Assert.False(ServerAdmin.TakeScheduleRequest(dir, out _));

            File.WriteAllText(Path.Combine(dir, ServerAdmin.ScheduleRequestFile), "10\n");
            Assert.True(ServerAdmin.TakeScheduleRequest(dir, out var warning));
            Assert.Equal(TimeSpan.FromMinutes(10), warning);
            Assert.False(File.Exists(Path.Combine(dir, ServerAdmin.ScheduleRequestFile))); // the script sees it taken
            Assert.False(ServerAdmin.TakeScheduleRequest(dir, out _));

            File.WriteAllText(Path.Combine(dir, ServerAdmin.ScheduleRequestFile), "junk");
            Assert.True(ServerAdmin.TakeScheduleRequest(dir, out warning));
            Assert.Equal(TimeSpan.FromMinutes(5), warning);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

}
