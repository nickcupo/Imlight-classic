/*
 * ========================================================================
 * MOUNT DUEL STOW TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The owner's "every time I log in my mount is unequipped" (10/05/2026): the server takes the mount off for every
 * duel and remembered it only in the session's CombatService, so a session that ended mid-fight (logout, a dropped
 * client, a restart) left the mount in the backpack for good. The stow is now saved on the wizard
 * (CombatStowedMountId) and put back after the duel, or at the next attach.
 *
 * NOTE:
 * A fake character store (EconomyFixesTests' pattern) stands in for RavenDB; a private template cache entry supplies
 * the mount's template without a world archive.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.Common;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class MountDuelStowTests : IDisposable {

    private const ulong Char = 4242;
    private const ulong MountId = 9001;
    private const uint MountTemplateId = uint.MaxValue - 4242;

    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly Store _store;

    public MountDuelStowTests() {
        EquipmentAttachConcurrencyTests.Configure();
        _cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache[MountTemplateId] = new WizItemTemplate {
            m_templateID = MountTemplateId,
            m_objectName = "Mount-Test",
            m_adjectiveList = ["Mount"],
            m_equipEffects = [],
            m_behaviors = [],
        };
        _store = new Store();
    }

    public void Dispose() => _cache.Remove(MountTemplateId);

    [Fact]
    public void AMountStowedForADuelThatTheSessionNeverFinishedIsWornAgainAtTheNextLogin() {
        using (_store.Scope()) {
            var session = _store.Login();
            Assert.True(session.InventoryToEquipmentTransfer(MountId, out _, out _));
            Assert.Equal(MountId, _store.Saved.EquipmentBehavior.SlotList.Single(s => s.SlotType == EquipmentSlotType.Mount).ItemId.Full);

            // The duel starts: the server takes the mount off and saves which one it was.
            Assert.Equal(MountId, session.StowMountForDuel(out _, out _));
            Assert.Null(session.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount));
            Assert.Contains(MountId, _store.Saved.InventoryBehavior.InventoryItemIds);
            Assert.Equal(MountId, _store.Saved.CombatStowedMountId);

            // The client drops mid-fight; the session's memory is gone. The next login reads the saved wizard.
            var next = _store.Login();
            Assert.Null(next.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount));
            Assert.True(next.RestoreDuelStowedMount(zoneDisallowsMounts: false, out _));

            Assert.Equal(MountId, next.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount)!.m_globalID.Full);
            Assert.Equal(0ul, _store.Saved.CombatStowedMountId);
            Assert.Contains(MountId, _store.Saved.EquipmentBehavior.EquippedItemIds);
            Assert.DoesNotContain(MountId, _store.Saved.InventoryBehavior.InventoryItemIds);

            // A third login has nothing left to restore and keeps the mount on.
            var third = _store.Login();
            Assert.False(third.RestoreDuelStowedMount(false, out _));
            Assert.Equal(MountId, third.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount)!.m_globalID.Full);
        }
    }

    [Fact]
    public void AFinishedDuelPutsTheMountBackAndARejoinedDuelKeepsTheSavedStow() {
        using (_store.Scope()) {
            var session = _store.Login();
            Assert.True(session.InventoryToEquipmentTransfer(MountId, out _, out _));
            Assert.Equal(MountId, session.StowMountForDuel(out _, out _));

            // A rejoin (no mount worn, the dropped session's stow saved) neither loses nor doubles it.
            var rejoined = _store.Login();
            Assert.Equal(MountId, rejoined.StowMountForDuel(out _, out var removed));
            Assert.Null(removed);
            Assert.Equal(MountId, _store.Saved.CombatStowedMountId);

            // The duel ends.
            Assert.True(rejoined.RestoreDuelStowedMount(false, out _));
            Assert.Equal(MountId, rejoined.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount)!.m_globalID.Full);
            Assert.Equal(0ul, _store.Saved.CombatStowedMountId);
        }
    }

    [Fact]
    public void AStowRestoredInAZoneWithoutMountsWaitsForTheOutdoors() {
        using (_store.Scope()) {
            var session = _store.Login();
            Assert.True(session.InventoryToEquipmentTransfer(MountId, out _, out _));
            session.StowMountForDuel(out _, out _);

            var next = _store.Login();
            Assert.False(next.RestoreDuelStowedMount(zoneDisallowsMounts: true, out _));
            Assert.Null(next.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount));
            Assert.Equal(0ul, _store.Saved.CombatStowedMountId);
            Assert.Equal(MountId, _store.Saved.InteriorStowedMountId); // EquipmentService re-equips it outdoors
        }
    }

    [Fact]
    public void AStowedMountThatLeftTheBackpackIsForgotten() {
        using (_store.Scope()) {
            var session = _store.Login();
            Assert.True(session.InventoryToEquipmentTransfer(MountId, out _, out _));
            session.StowMountForDuel(out _, out _);
            Assert.True(session.InventoryBehavior.RemoveItem(session.InventoryBehavior.GetItem(MountId)));
            WizardCollection.UpdateCharacterItems(session);

            var next = _store.Login();
            Assert.False(next.RestoreDuelStowedMount(false, out _));
            Assert.Equal(0ul, _store.Saved.CombatStowedMountId);
        }
    }

    // ---------------------------------------------------------------- fakes

    /// <summary>One saved wizard; a login reads it as LoadWizard does (the item documents by id).</summary>
    private sealed class Store {

        internal Wizard Saved;
        private readonly WizClientObjectItem _mount = new() { m_globalID = MountId, m_templateID = MountTemplateId, m_characterId = Char };

        internal Store() {
            Saved = new Wizard {
                CharId = Char,
                PlayerNameBehavior = new ServerWizPlayerNameBehavior { NameOverride = "Mount Tester" },
                GameStats = new ServerWizGameStats(default, 10),
                MountOwnerBehavior = new ServerMountOwnerBehavior(),
                PetOwnerBehavior = new ServerPetOwnerBehavior(),
                InventoryBehavior = new ServerWizInventoryBehavior { InventoryItemIds = [MountId], Items = new() },
                EquipmentBehavior = new ServerWizEquipmentBehavior { SlotList = [], EquippedItemIds = [], EquippedItems = new() },
            };
        }

        internal Wizard Login() {
            var wizard = Clone(Saved);
            if (wizard.InventoryBehavior.InventoryItemIds.Contains(MountId)) {
                wizard.InventoryBehavior.Items.Add(_mount);
            }
            if (wizard.EquipmentBehavior.EquippedItemIds.Contains(MountId)) {
                wizard.EquipmentBehavior.EquippedItems.Add(_mount);
            }

            return wizard;
        }

        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, Load);
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }

        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, TreasureTradeTests.MultiSessionProxy>();
            ((TreasureTradeTests.MultiSessionProxy) (object) session).Commit = working => {
                foreach (var (_, wizard) in working) {
                    Saved = Clone(wizard);
                }
            };

            return session;
        }

        private Wizard Load(IDocumentSession session, ulong id) {
            Assert.Equal(Char, id);
            var working = Clone(Saved);
            ((TreasureTradeTests.MultiSessionProxy) (object) session).Working[id] = working;

            return working;
        }

        // What a save writes: the character document's fields (the item objects live in their own collection).
        private static Wizard Clone(Wizard wizard) => new() {
            CharId = wizard.CharId,
            PlayerNameBehavior = wizard.PlayerNameBehavior,
            GameStats = wizard.GameStats,
            MountOwnerBehavior = wizard.MountOwnerBehavior,
            PetOwnerBehavior = wizard.PetOwnerBehavior,
            InteriorStowedMountId = wizard.InteriorStowedMountId,
            CombatStowedMountId = wizard.CombatStowedMountId,
            InventoryBehavior = new ServerWizInventoryBehavior {
                InventoryItemIds = [.. wizard.InventoryBehavior.InventoryItemIds], Items = new(),
            },
            EquipmentBehavior = new ServerWizEquipmentBehavior {
                SlotList = [.. wizard.EquipmentBehavior.SlotList.Select(s => new EquipmentSlot { SlotType = s.SlotType, ItemId = s.ItemId, ItemName = s.ItemName })],
                EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds],
                EquippedItems = new(),
            },
        };

    }

    private sealed class Restore(System.Action undo) : IDisposable {
        public void Dispose() => undo();
    }
}
