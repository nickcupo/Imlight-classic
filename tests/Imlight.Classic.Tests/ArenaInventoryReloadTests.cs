// CLASSIC: resource reloads must retain the dated arena ticket shops and trainer correction.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.Types;
using Imlight.Classic.Pvp;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaInventoryReloadTests {

    public ArenaInventoryReloadTests() {
        var path = Path.Combine(Path.GetTempPath(), $"arena-inventory-tests-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}arena-inventory-tests.log\n");
        ConfigurationManager.Initialize(path);
        // Test configuration is retained in the temporary directory; the owner controls permanent deletion.
    }

    [Fact]
    public void EveryResourceLoadRestoresTicketShopsWithoutDuplicatingTheirStock() {
        var field = typeof(SpiralDB).GetField("s_npcInventories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousInventories = field.GetValue(null);
        var previousArena = ClassicArena.Config;
        try {
            ClassicArena.UseForTests(null);
            ArenaConfig? loadedArena = null;
            for (var load = 0; load < 3; load++) {
                // SpiralDB.Load atomically replaces this map before GameServer calls ClassicArena.Initialize.
                field.SetValue(null, new ConcurrentDictionary<ulong, NPCInventory>());
                ClassicArena.Initialize(ClassicDataFixture.Root, "late-2009");
                var arena = ClassicArena.Config!;
                loadedArena ??= arena;
                Assert.Same(loadedArena, arena);
                foreach (var vendor in arena.TicketVendors) {
                    Assert.True(SpiralDB.TryGetNpcInventory(vendor.Npc, out var inventory));
                    Assert.Equal(vendor.Items.Select(item => (ulong) item.Template), inventory.Inventory.Select(item => item.Full));
                }

                Assert.Equal(123, arena.TicketVendors.Sum(vendor => vendor.Items.Length));
            }
        }
        finally {
            field.SetValue(null, previousInventories);
            ClassicArena.UseForTests(previousArena);
        }
    }

    [Fact]
    public void AShopWithNoInventoryRefusesPurchasesInsteadOfThrowing() {
        var vendor = new InteractVendorComponent(null!);
        Assert.False(vendor.HasItem(new GID(123u)));
    }

    [Fact]
    public void StunBlockMovesToSabrinaIdempotentlyWithoutChangingOtherInventoryEntries() {
        var record = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).FindByName("Stun Block")!;
        var values = record.ValuesFor(ClassicDataFixture.LoadProfile("late-2009").Lineage);
        const ulong block = 1143963608;
        var sabrina = new NPCSpellInventory {
            TemplateID = 38210,
            Spells = [new NPCSpellEntry { TemplateID = 1, RequiredSpellID = 2, Level = 10 }],
        };
        var diego = new NPCSpellInventory {
            TemplateID = 38226,
            Spells = [new NPCSpellEntry { TemplateID = block, Level = 1 }, new NPCSpellEntry { TemplateID = 3, Level = 30 }],
        };

        var first = ClassicTrainerInventories.MoveStunBlock(sabrina, diego, block, values);
        var second = ClassicTrainerInventories.MoveStunBlock(first.Sabrina, first.Diego, block, values);
        Assert.Equal("Sabrina Greenstar", values.Trainer);
        Assert.Equal(new ulong[] { 1, block }, second.Sabrina.Spells.Select(spell => spell.TemplateID));
        var taught = Assert.Single(second.Sabrina.Spells, spell => spell.TemplateID == block);
        Assert.Equal(0, taught.Level);
        Assert.Equal(0ul, taught.RequiredSpellID);
        Assert.Same(sabrina.Spells[0], second.Sabrina.Spells[0]);
        Assert.Same(diego.Spells[1], Assert.Single(second.Diego!.Spells));
        Assert.Single(sabrina.Spells);
        Assert.Equal(2, diego.Spells.Count);
        Assert.True(record.IsInProfile("late-2009"));
        Assert.False(record.IsInProfile("arc1-2009h1"));
    }

    [Theory]
    [InlineData("late-2009", true)]
    [InlineData("arc1-2009h1", false)]
    [InlineData("dev-unrestricted", false)]
    public void TrainerCorrectionOnlyAppliesWhenTheActiveProfileIncludesStunBlock(string profile, bool correct) {
        var field = typeof(SpiralDB).GetField("s_npcSpellInventories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var record = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).FindByName("Stun Block")!;
        const ulong block = 1143963608;
        var sabrina = new NPCSpellInventory { TemplateID = 38210, Spells = [] };
        var diego = new NPCSpellInventory { TemplateID = 38226, Spells = [new NPCSpellEntry { TemplateID = block }] };
        try {
            ClassicRuntime.ResetForTests();
            ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
            field.SetValue(null, new ConcurrentDictionary<ulong, NPCSpellInventory>());
            SpiralDB.RegisterNpcSpellInventory(sabrina);
            SpiralDB.RegisterNpcSpellInventory(diego);
            ClassicTrainerInventories.Refresh(record, _ => block);
            ClassicTrainerInventories.Refresh(record, _ => block);
            Assert.True(SpiralDB.TryGetNpcSpellInventory(38210, out var afterSabrina));
            Assert.True(SpiralDB.TryGetNpcSpellInventory(38226, out var afterDiego));
            Assert.Equal(correct ? 1 : 0, afterSabrina.Spells.Count);
            Assert.Equal(correct ? 0 : 1, afterDiego.Spells.Count);
            if (!correct) {
                Assert.Same(sabrina, afterSabrina);
                Assert.Same(diego, afterDiego);
            }
        }
        finally {
            field.SetValue(null, previous);
            ClassicRuntime.ResetForTests();
        }
    }
}
