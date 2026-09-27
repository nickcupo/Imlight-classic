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
 * SHARED PLAYER STATE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CopyOnWriteList keeps List<T>'s behavior for the inventory's uses, and
 * a snapshot taken while another thread adds is always whole.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Classic.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class CopyOnWriteListTests {

    [Fact]
    public void BehavesLikeAListForTheInventoryCalls() {
        CopyOnWriteList<string> items = [.. new[] { "wand", "deck", "robe" }.Where(name => name != "robe")];

        items.Add("hat");
        items.Add("deck");

        Assert.Equal(["wand", "deck", "hat", "deck"], items);
        Assert.Equal(4, items.Count);
        Assert.Equal("hat", items[2]);
        Assert.Equal("hat", items.Find(name => name.StartsWith('h')));
        Assert.Null(items.Find(name => name == "boots"));
        Assert.True(items.Remove("deck"));
        Assert.Equal(["wand", "hat", "deck"], items);
        Assert.False(items.Remove("boots"));
        Assert.Equal([4, 3, 4], items.ConvertAll(name => name.Length));
        Assert.Empty(new CopyOnWriteList<string>());
    }

    [Fact]
    public void EnumerationIsUnaffectedByWritesDuringIt() {
        CopyOnWriteList<int> items = [1, 2, 3];
        var seen = new List<int>();

        foreach (var item in items) {
            seen.Add(item);
            items.Add(item * 10);
            items.Remove(item);
        }

        Assert.Equal([1, 2, 3], seen);
        Assert.Equal([10, 20, 30], items);
    }

    [Fact]
    public void SnapshotsTakenDuringConcurrentAddsAreWholePrefixes() {
        // A starter kit of 15 items going in on one actor while another serializes the player.
        const int Rounds = 200;
        const int KitSize = 15;
        for (var round = 0; round < Rounds; round++) {
            var items = new CopyOnWriteList<int>();
            using var start = new Barrier(2);
            var writer = Task.Run(() => {
                start.SignalAndWait();
                for (var i = 0; i < KitSize; i++) {
                    items.Add(i);
                }
            });
            var snapshots = new List<List<int>>();
            var reader = Task.Run(() => {
                start.SignalAndWait();
                while (!writer.IsCompleted) {
                    snapshots.Add(items.ConvertAll(item => item));
                    snapshots.Add([.. items]);
                    _ = items.Count;
                }
            });

            Task.WaitAll(writer, reader);

            Assert.Equal(Enumerable.Range(0, KitSize), items);
            Assert.All(snapshots, snapshot => Assert.Equal(Enumerable.Range(0, snapshot.Count), snapshot));
        }
    }

    [Fact]
    public void ConcurrentWritersLoseNothing() {
        var items = new CopyOnWriteList<int>();

        Parallel.For(0, 4, writer => {
            for (var i = 0; i < 500; i++) {
                items.Add(writer * 1000 + i);
            }
        });

        Assert.Equal(2000, items.Count);
        Assert.Equal(2000, items.Distinct().Count());
        for (var writer = 0; writer < 4; writer++) {
            var mine = items.Where(item => item / 1000 == writer).ToList();
            Assert.Equal(Enumerable.Range(writer * 1000, 500), mine);
        }
    }

}
