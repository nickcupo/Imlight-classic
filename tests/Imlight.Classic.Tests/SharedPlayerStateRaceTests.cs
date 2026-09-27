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
 * Stress tests for the three player-state races: quest result lists
 * writing the quest registry and quest lists from their own actors,
 * concurrent equips, and the login session key.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter SharedPlayerStateRaceTests
 *
 * NOTE:
 * Every round runs writers on several threads while another thread does
 * what a RavenDB save does to the same behavior (Newtonsoft serialization
 * of the live object, which RavenDB's client uses) and what other actors
 * read. Before the fixes these threw "Collection was modified",
 * IndexOutOfRangeException or lost writes within a few rounds.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Net;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;
using Action = System.Action;

namespace Imlight.Classic.Tests;

public sealed class SharedPlayerStateRaceTests {

    private const int Rounds = 60;

    [Fact]
    public void QuestResultListsWriteTheRegistryWhileASaveSerializesIt() {
        for (var round = 0; round < Rounds; round++) {
            var quests = new ServerQuestBehavior();
            var errors = new ConcurrentQueue<Exception>();
            using var stop = new CancellationTokenSource();

            // A save on another actor: UpdateCharacterQuestBehavior serializes the live behavior.
            var saver = Task.Run(() => Capture(errors, () => {
                while (!stop.IsCancellationRequested) {
                    _ = JsonConvert.SerializeObject(quests);
                    _ = quests.HasCompletedQuest("WC-ST01-C01-001");
                    _ = quests.Registry.Count;
                }
            }));

            // Four result lists at once (goal, quest and two zone triggers), each on its own executor actor.
            Parallel.For(0, 4, list => Capture(errors, () => {
                for (var i = 0; i < 250; i++) {
                    quests.SetRegistryValue($"list{list}_entry{i}", (ulong) i + 1);
                    quests.SetQuestRegistryValue($"WC-TEST-{list}", $"Goal{i}", 1);
                    quests.AddToRegistry("shared_first_wins", (ulong) list + 1);
                    if (i % 10 == 0) {
                        quests.RemoveFromRegistry($"list{list}_entry{i - 10}");
                    }
                }
            }));
            stop.Cancel();
            saver.Wait();

            Assert.Empty(errors);
            for (var list = 0; list < 4; list++) {
                Assert.Equal(250UL, quests.GetRegistryValue($"list{list}_entry249"));
                Assert.Equal(1UL, quests.GetQuestRegistryValue($"WC-TEST-{list}", "Goal249"));
            }
            Assert.InRange(quests.GetRegistryValue("shared_first_wins"), 1UL, 4UL);
        }
    }

    [Fact]
    public void QuestListsStayWholeWhileQuestsStartAndCompleteOnSeveralActors() {
        for (var round = 0; round < Rounds; round++) {
            var quests = new ServerQuestBehavior();
            var errors = new ConcurrentQueue<Exception>();
            using var stop = new CancellationTokenSource();

            var reader = Task.Run(() => Capture(errors, () => {
                while (!stop.IsCancellationRequested) {
                    _ = JsonConvert.SerializeObject(quests);
                    _ = quests.CurrentQuestInstances.Where(q => q.QuestName.StartsWith("WC-", StringComparison.Ordinal)).ToList();
                    _ = quests.HasQuest("WC-A-0-10");
                }
            }));

            Parallel.For(0, 4, actor => Capture(errors, () => {
                for (var i = 0; i < 100; i++) {
                    var quest = new QuestInstance { ID = (ulong) (actor * 1000 + i + 1), QuestName = $"WC-A-{actor}-{i}" };
                    Assert.True(quests.AddQuest(quest));
                    if (i % 2 == 0) {
                        Assert.True(quests.CompleteQuest(quest.QuestName));
                    }
                }
            }));
            stop.Cancel();
            reader.Wait();

            Assert.Empty(errors);
            Assert.Equal(200, quests.CurrentQuestInstances.Count);
            Assert.Equal(quests.CurrentQuestInstances.Select(q => q.ID).OrderBy(id => id),
                         quests.CurrentQuestIDs.OrderBy(id => id));
            Assert.Equal(200, quests.Registry.Keys.Count(key => key.EndsWith("_Complete", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void TheQuestBehaviorStillRoundTripsThroughJson() {
        var quests = new ServerQuestBehavior();
        quests.SetRegistryValue("WC-ST01-C01-001_Complete", 1);
        quests.AddQuest(new QuestInstance { ID = 7, QuestName = "WC-ST01-C01-002" });

        var json = JsonConvert.SerializeObject(quests);
        var loaded = JsonConvert.DeserializeObject<ServerQuestBehavior>(json)!;

        Assert.Contains("\"Registry\":{\"WC-ST01-C01-001_Complete\":1}", json);
        Assert.Contains("\"CurrentQuestIDs\":[7]", json);
        Assert.True(loaded.HasCompletedQuest("WC-ST01-C01-001"));
        Assert.Equal([7UL], loaded.CurrentQuestIDs);
        Assert.Empty(loaded.CurrentQuestInstances); // JsonIgnore: loaded from the quest instance collection.
    }

    [Fact]
    public void ConcurrentEquipsKeepOneItemPerSlot() {
        var slots = new[] { EquipmentSlotType.Hat, EquipmentSlotType.Robe, EquipmentSlotType.Shoes, EquipmentSlotType.Weapon };
        for (var round = 0; round < Rounds; round++) {
            var equipment = NewEquipment();
            var errors = new ConcurrentQueue<Exception>();
            using var stop = new CancellationTokenSource();

            var saver = Task.Run(() => Capture(errors, () => {
                while (!stop.IsCancellationRequested) {
                    _ = JsonConvert.SerializeObject(equipment);
                    foreach (var slot in slots) {
                        _ = equipment.GetItemInSlot(slot);
                    }
                    _ = equipment.EquippedItems.Count;
                    _ = equipment.GetEquippedPetId();
                }
            }));

            // Equip service, starter kit and reward equips on different actors, all on the same slots.
            Parallel.For(0, 4, actor => Capture(errors, () => {
                for (var i = 0; i < 150; i++) {
                    var slot = slots[(actor + i) % slots.Length];
                    var item = new WizClientObjectItem { m_globalID = (ulong) (actor * 100_000 + i + 1), m_debugName = $"item-{actor}-{i}" };
                    equipment.EquipItem(item, slot);
                    if (i % 3 == 0) {
                        equipment.UnequipItem(item.m_globalID);
                    }
                }
            }));
            stop.Cancel();
            saver.Wait();

            Assert.Empty(errors);
            Assert.Equal(equipment.SlotList.Count, equipment.SlotList.Select(s => s.SlotType).Distinct().Count());
            Assert.Equal(equipment.SlotList.Count, equipment.EquippedItems.Count);
            Assert.Equal(equipment.EquippedItems.Select(item => (ulong) item.m_globalID).OrderBy(id => id),
                         equipment.EquippedItemIds.OrderBy(id => id));
            Assert.All(equipment.SlotList, slot => Assert.NotNull(equipment.GetItem(slot.ItemId)));
        }
    }

    [Fact]
    public void ForceEquipNeverDuplicatesAnItem() {
        var equipment = NewEquipment();
        var item = new WizClientObjectItem { m_globalID = 42UL, m_debugName = "pet" };

        Parallel.For(0, 8, _ => equipment.ForceEquipItem(item));

        Assert.Single(equipment.EquippedItems);
        Assert.Equal([42UL], equipment.EquippedItemIds);
        Assert.True(equipment.UnequipItem(42UL)); // A force-equipped item has no slot.
        Assert.Empty(equipment.EquippedItems);
    }

    [Fact]
    public void AnAccountHasOneSessionKeyDocument() {
        Assert.Equal("SessionKeys/1234567890123", SessionKeyDocument.IdFor(1234567890123UL));
        Assert.Equal(SessionKeyDocument.IdFor(5), SessionKeyDocument.IdFor(5));
        Assert.NotEqual(SessionKeyDocument.IdFor(5), SessionKeyDocument.IdFor(6));
        Assert.StartsWith(SessionKeyDocument.CollectionName + "/", SessionKeyDocument.IdFor(ulong.MaxValue));
    }

    [Theory]
    [InlineData(5UL, 77UL, "key", 5UL, 77UL, true)]
    [InlineData(5UL, 77UL, "key", 5UL, 78UL, false)]  // another machine
    [InlineData(5UL, 77UL, "key", 6UL, 77UL, false)]  // another account
    [InlineData(5UL, 77UL, "", 5UL, 77UL, false)]
    [InlineData(5UL, 77UL, null, 5UL, 77UL, false)]
    public void AStoredKeyAnswersOnlyItsAccountAndMachine(ulong storedAccount, ulong storedMachine, string? key,
                                                         ulong account, ulong machine, bool answers)
        => Assert.Equal(answers, SessionKeyDocument.Answers(storedAccount, storedMachine, key, account, machine));

    private static ServerWizEquipmentBehavior NewEquipment() => new() {
        SlotList = [],
        EquippedItemIds = [],
        EquippedItems = [],
    };

    private static void Capture(ConcurrentQueue<Exception> errors, Action action) {
        try {
            action();
        }
        catch (Exception ex) {
            errors.Enqueue(ex);
        }
    }

}
