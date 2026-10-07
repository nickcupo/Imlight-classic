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
*/

using System;
using System.Linq;
using System.Collections.Generic;
using Imlight.CoreLib.Shared.Behaviors;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Math;

namespace Imlight.CoreLib.WizardData.Collections;

public static class WizardCollection {

    public const string CollectionName = "Wizards";
    private static readonly Lazy<IDocumentStore> s_storeSource = new(() => PlayerDatabase.Instance.Store);
    private static IDocumentStore s_store => s_storeSource.Value;

    private const int WriteLaneCount = 1 << 8; // 256 lanes
    private const ulong WriteLaneMask = WriteLaneCount - 1;
    private static readonly object[] s_writeLanes =
        Enumerable.Range(0, WriteLaneCount)
            .Select(_ => new object())
            .ToArray();

    // Tracks the write lane held by the current thread.
    // Prevents acquiring a different Wizard lane while one is already held.
    [ThreadStatic]
    private static int? s_heldWriteLane;
    internal static bool HoldsWriteLane => s_heldWriteLane.HasValue;

    // CLASSIC: a lost save acknowledgement leaves this live instance untrusted. A later whole-inventory
    // save (including logout) must not overwrite a purchase that may already be durable. Relog creates
    // a fresh instance from the database; the old instance stays refused without changing player schema.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Wizard, object> s_uncertainInventory = new();
    internal static bool IsInventorySnapshotUncertain(Wizard wizard)
        => wizard is not null && s_uncertainInventory.TryGetValue(wizard, out _);
    internal static void MarkInventorySnapshotUncertain(Wizard wizard) {
        if (wizard is not null) s_uncertainInventory.GetValue(wizard, _ => new object());
    }

    // CLASSIC: preserve the existing library's free-book server cap; deck-held cards have their own ledger.
    internal static bool CanReceiveTreasureCards(Wizard saved, int quantity)
        => saved?.SpellbookBehavior is not null && quantity > 0
            && (long)(saved.SpellbookBehavior.TreasureCardTemplateIds?.Count ?? 0) + quantity
                <= Imlight.Classic.Rules.TreasureShopRules.BookCapacity;

    private static readonly TimeSpan s_nonStaleWaitTimeout
        = TimeSpan.FromSeconds(ConfigurationManager.Settings["Database.DatabaseWaitForNonStaleResultsTimeout"].AsByte(5));

    private static T WithWriteLane<T>(ulong charId, Func<T> write) {
        var laneIndex = (int) (charId & WriteLaneMask);
        if (s_heldWriteLane is { } heldLane && heldLane != laneIndex)
            throw new InvalidOperationException($"Cannot acquire wizard lane {laneIndex} while holding wizard lane {heldLane}.");

        var writeLane = s_writeLanes[laneIndex];

        lock (writeLane) {
            var previousLane = s_heldWriteLane;
            s_heldWriteLane = laneIndex;

            try {
                return write();
            }
            finally {
                s_heldWriteLane = previousLane;
            }
        }
    }

    /// <summary>
    /// Commits Monstrology's ledger and wizard state under the shared player write lane.
    /// </summary>
    internal static bool TransactMonstrology(ulong charId, Func<IDocumentSession, Wizard, bool> operation)
        => TransactMonstrology(charId, operation, null);

    internal static bool TransactMonstrology(ulong charId, Func<IDocumentSession, Wizard, bool> operation,
                                            Action<Wizard> afterCommit)
        => CommitCharacterMutation(charId, operation, afterCommit);

    /// <summary>
    /// CLASSIC: test seam, scoped to the calling test's async flow (threads and tasks it starts inherit it): the store
    /// the character writes use when no session is passed in.
    /// </summary>
    internal static readonly System.Threading.AsyncLocal<TestStore> TestStoreScope = new();

    internal sealed record TestStore(Func<IDocumentSession> Open, Func<IDocumentSession, ulong, Wizard> Load);

    internal static bool CommitCharacterMutation(ulong charId, Func<IDocumentSession, Wizard, bool> operation,
        Action<Wizard> afterCommit, Func<IDocumentSession> openSession = null,
        Func<IDocumentSession, ulong, Wizard> loadWizard = null, Action<Exception> onSaveFailure = null) {
        if (Classic.Ambient.AmbientWizards.IsAmbientChar(charId)) {
            return false; // CLASSIC: an ambient wizard has no character document.
        }

        if (openSession is null && TestStoreScope.Value is { } test) {
            openSession = test.Open;
            loadWizard ??= test.Load;
        }

        return WithWriteLane(charId, () => {
            using var session = openSession is null ? s_store.OpenSession() : openSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var wizard = loadWizard is null ? GetCharacterByCharId(session, charId) : loadWizard(session, charId);
            if (wizard is null || !operation(session, wizard)) return false;
            try {
                session.SaveChanges();
                afterCommit?.Invoke(wizard);
            }
            catch (Exception error) {
                // CLASSIC: quarantine before releasing the lane, so a queued stale inventory save cannot
                // run between a lost acknowledgement (or failed publication) and its caller's catch.
                onSaveFailure?.Invoke(error);
                throw;
            }
            return true;
        });
    }

    // CLASSIC: two wizards' lanes for one atomic write (a treasure card trade). Lanes are taken in index order, so two
    // trades never deadlock, and nothing else ever holds two lanes.
    private static T WithWriteLanes<T>(ulong firstCharId, ulong secondCharId, Func<T> write) {
        var a = (int) (firstCharId & WriteLaneMask);
        var b = (int) (secondCharId & WriteLaneMask);
        if (a == b) {
            return WithWriteLane(firstCharId, write);
        }

        if (s_heldWriteLane is { } heldLane) {
            throw new InvalidOperationException($"Cannot acquire wizard lanes {a} and {b} while holding wizard lane {heldLane}.");
        }

        var (low, high) = a < b ? (a, b) : (b, a);
        lock (s_writeLanes[low]) {
            lock (s_writeLanes[high]) {
                s_heldWriteLane = low;
                try {
                    return write();
                }
                finally {
                    s_heldWriteLane = null;
                }
            }
        }
    }

    /// <summary>
    /// CLASSIC: swaps treasure cards between two wizards in one save: <paramref name="firstGives"/> leave the first
    /// wizard's book for the second's, <paramref name="secondGives"/> the other way. Every card is checked against the
    /// saved books (not the live caches), so a card spent, deleted or put in a deck after it was offered fails the
    /// whole trade; nothing is changed unless both books are saved together.
    /// </summary>
    /// <returns>True if the trade was saved and both live books now show it.</returns>
    internal static bool CommitTreasureCardTrade(Wizard first, IReadOnlyList<uint> firstGives,
        Wizard second, IReadOnlyList<uint> secondGives,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (first is null || second is null || first.CharId == second.CharId || firstGives is null || secondGives is null
            || firstGives.Any(card => card == 0) || secondGives.Any(card => card == 0)
            || Classic.Ambient.AmbientWizards.IsAmbientChar(first.CharId) || Classic.Ambient.AmbientWizards.IsAmbientChar(second.CharId)) {
            return false;
        }

        return WithWriteLanes(first.CharId, second.CharId, () => {
            // CLASSIC: an unacknowledged write cannot be retried from either participant's stale book.
            if (IsInventorySnapshotUncertain(first) || IsInventorySnapshotUncertain(second)) return false;
            using var session = openSession is null ? s_store.OpenSession() : openSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var a = loadWizard is null ? GetCharacterByCharId(session, first.CharId) : loadWizard(session, first.CharId);
            var b = loadWizard is null ? GetCharacterByCharId(session, second.CharId) : loadWizard(session, second.CharId);
            if (a?.SpellbookBehavior is null || b?.SpellbookBehavior is null
                || !HasCards(a, firstGives) || !HasCards(b, secondGives)
                || !FitsTrade(a, firstGives.Count, secondGives.Count)
                || !FitsTrade(b, secondGives.Count, firstGives.Count)) {
                return false;
            }

            foreach (var card in firstGives) {
                a.SpellbookBehavior.RemoveTreasureCard(card);
                b.SpellbookBehavior.AddTreasureCard(card);
            }

            foreach (var card in secondGives) {
                b.SpellbookBehavior.RemoveTreasureCard(card);
                a.SpellbookBehavior.AddTreasureCard(card);
            }

            try {
                session.SaveChanges();
                PublishTreasureCards(first, a);
                PublishTreasureCards(second, b);
            } catch (Exception) {
                // CLASSIC: both books may already be durable; refuse further writes until authoritative reload.
                MarkInventorySnapshotUncertain(first);
                MarkInventorySnapshotUncertain(second);
                throw;
            }

            return true;
        });

        static bool HasCards(Wizard wizard, IReadOnlyList<uint> cards)
            => cards.GroupBy(card => card).All(group => wizard.SpellbookBehavior.TreasureCardCount(group.Key) >= group.Count());
        // CLASSIC: a valid legacy overfull book may shrink, but may not gain additional free-book copies.
        static bool FitsTrade(Wizard wizard, int gives, int receives)
            => receives <= gives || (long)(wizard.SpellbookBehavior.TreasureCardTemplateIds?.Count ?? 0)
                - gives + receives <= Imlight.Classic.Rules.TreasureShopRules.BookCapacity;
    }

    internal static bool TryPurchaseTreasureCards(Wizard liveWizard, uint templateId, int quantity, int unitPrice,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        var total = (long) quantity * unitPrice;
        if (liveWizard is null || templateId == 0 || quantity <= 0 || unitPrice < 0 || total > int.MaxValue) return false;
        return CommitCharacterMutation(liveWizard.CharId, (_, persisted) => {
            // CLASSIC: capacity is checked against the saved book in the same commit as payment, rather
            // than relying on the library's earlier live snapshot or allowing Bazaar to bypass the cap.
            if (IsInventorySnapshotUncertain(liveWizard) || !CanReceiveTreasureCards(persisted, quantity)
                || persisted.GameStats.m_currentGold < total) return false;
            persisted.GameStats.m_currentGold -= (int) total;
            for (var i = 0; i < quantity; i++) persisted.SpellbookBehavior.AddTreasureCard(templateId);
            return true;
        }, persisted => {
            liveWizard.GameStats.m_currentGold = persisted.GameStats.m_currentGold;
            PublishTreasureCards(liveWizard, persisted);
        }, openSession, loadWizard, onSaveFailure: _ => MarkInventorySnapshotUncertain(liveWizard));
    }

    internal static bool ChangeGold(Wizard liveWizard, long delta, bool capToPouch,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        return CommitCharacterMutation(liveWizard.CharId, (_, persisted) => {
            var gold = persisted.GameStats.m_currentGold + delta;
            if (capToPouch && gold > persisted.GameStats.m_baseGoldPouch) gold = persisted.GameStats.m_baseGoldPouch;
            // CLASSIC: gold never goes below zero; a debit the saved balance cannot cover changes nothing (fails).
            if (gold < 0 || gold > int.MaxValue) return false;
            persisted.GameStats.m_currentGold = (int) gold;
            return true;
        }, persisted => liveWizard.GameStats.m_currentGold = persisted.GameStats.m_currentGold,
            openSession, loadWizard);
    }

    /// <summary>
    /// CLASSIC: spends <paramref name="amount"/> gold only if the saved balance holds it, checked and debited in one
    /// save under the character's write lane; the live balance follows. Every purchase debits through here before it
    /// grants anything, so two purchases sent together from different actors cannot both pass a check of the live
    /// gold and drive it below zero.
    /// </summary>
    internal static bool TrySpendGold(Wizard liveWizard, int amount,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null || amount < 0) return false;
        if (amount == 0) return true;
        return ChangeGold(liveWizard, -(long) amount, capToPouch: false, openSession, loadWizard);
    }

    // CLASSIC: tickets are a saved wallet, never a balance copied back from an old health/mana snapshot.
    internal static bool ChangeArenaTickets(Wizard liveWizard, long delta, bool clampToZero = false,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null) return false;
        return ChangeArenaTickets(liveWizard.CharId, delta, liveWizard, clampToZero, openSession, loadWizard);
    }

    internal static bool ChangeArenaTickets(ulong charId, long delta, Wizard liveWizard = null, bool clampToZero = false,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is not null && liveWizard.CharId != charId) return false;
        return CommitCharacterMutation(charId, (_, persisted) => {
            long points;
            try { points = checked((long) persisted.GameStats.m_currentArenaPoints + delta); }
            catch (OverflowException) { return false; }
            if (clampToZero && points < 0) points = 0; // CLASSIC: the existing QA addtickets negative clamp only.
            if (points < 0 || points > int.MaxValue) return false;
            persisted.GameStats.m_currentArenaPoints = (int) points;
            persisted.GameStats.m_currentPvPCurrency = (int) points; // CLASSIC: explicit ticket mutations retain the original alias.
            return true;
        }, persisted => {
            if (liveWizard is null) return;
            liveWizard.GameStats.m_currentArenaPoints = persisted.GameStats.m_currentArenaPoints;
            liveWizard.GameStats.m_currentPvPCurrency = persisted.GameStats.m_currentPvPCurrency;
        }, openSession, loadWizard);
    }

    internal static bool TrySpendArenaTickets(Wizard liveWizard, int amount,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null || amount < 0) return false;
        return ChangeArenaTickets(liveWizard, -(long) amount, false, openSession, loadWizard);
    }

    /// <summary>
    /// CLASSIC: adds <paramref name="delta"/> training points to the saved count (a negative delta spends them and
    /// fails, changing nothing, if the count cannot cover it), then publishes the count to the live wizard. Loot,
    /// quest rewards and training run on different actors; each used to write its own read of the live count back.
    /// </summary>
    internal static bool ChangeTrainingPoints(Wizard liveWizard, int delta,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null) return false;
        return CommitCharacterMutation(liveWizard.CharId, (_, persisted) => {
            var points = (long) persisted.MagicSchoolBehavior.TrainingPoints + delta;
            if (points < 0 || points > int.MaxValue) return false;
            persisted.MagicSchoolBehavior.TrainingPoints = (int) points;
            return true;
        }, persisted => liveWizard.MagicSchoolBehavior.TrainingPoints = persisted.MagicSchoolBehavior.TrainingPoints,
            openSession, loadWizard);
    }

    /// <summary>
    /// CLASSIC: runs <paramref name="change"/> holding the character's write lane, so changes to the live wizard that
    /// are then saved whole (XP and level) are made one at a time even when they come from different actors; the
    /// saves inside take the same lane again.
    /// </summary>
    internal static T WithCharacterLock<T>(ulong charId, Func<T> change) => WithWriteLane(charId, change);

    private static bool UpdateCharacter(ulong charId, Action<Wizard> update) {
        if (Classic.Ambient.AmbientWizards.IsAmbientChar(charId)) {
            return false; // CLASSIC: an ambient wizard has no character document.
        }

        var test = TestStoreScope.Value;
        return WithWriteLane(charId, () => {
            using var session = test is null ? s_store.OpenSession() : test.Open();
            var existingCharacter = test is null ? GetCharacterByCharId(session, charId) : test.Load(session, charId);
            if (existingCharacter is null) {
                return false;
            }

            update(existingCharacter);
            session.SaveChanges();
            return true;
        });
    }

    /// <summary>
    /// Creates a character in the database.
    /// </summary>
    /// <param name="character"></param>
    public static bool AddCharacter(Wizard character) {
        return WithWriteLane(character.CharId, () => {
            using var session = s_store.OpenSession();

            // Return false if the character already exists in the database.
            var existingCharacter = GetCharacterByCharId(session, character.CharId);
            if (existingCharacter is not null) {
                return false;
            }

            session.Store(character);
            var metadata = session.Advanced.GetMetadataFor(character);
            metadata[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

            session.SaveChanges();

            return true;
        });
    }

    /// <summary>
    /// Updates a character in the database.
    /// </summary>
    /// <param name="id"></param>
    public static bool DeleteCharacter(ulong id) {
        return WithWriteLane(id, () => {
            using var session = s_store.OpenSession();

            var character = GetCharacterByCharId(session, id);
            if (character is null) {
                return false;
            }

            // Delete related items/dynamods/quests data/etc.
            WizardItemCollection.DeleteInventory(id);
            WizardPetSnackCollection.DeleteSnackBag(id);
            DynamodCollection.DeleteAllDynamodSets(id);
            WizardReagentCollection.DeleteReagentBag(id);

            session.Delete(character);
            session.SaveChanges();

            return true;
        });
    }

    /// <summary>
    /// Retrieves a character from the database based on the specified ID.
    /// 
    /// Completely loads the character, including their inventory, equipment, etc.
    /// This is a blocking call and may take some time to complete. Alternatively, use
    /// `GetCharacterUnloaded` to retrieve a character without loading their data.
    /// </summary>
    /// <param name="id">The ID of the character to retrieve.</param>
    /// <returns>The character with the specified ID, or null if not found.</returns>
    public static Wizard GetCharacter(ulong id) {
        // CLASSIC: an ambient wizard lives in memory (Classic/Ambient), not in this collection.
        if (Classic.Ambient.AmbientWizards.TryGetWizard(id, out var ambient)) {
            return ambient;
        }

        using var session = s_store.OpenSession();

        var character = GetCharacterByCharId(session, id);

        if (character is null) {
            return null;
        }

        // Query for the account.
        var account = session.Query<Account>(collectionName: AccountCollection.CollectionName)
            .FirstOrDefault(x => x.AccountId == character.AccountId);
        if (account is not null) {
            character.Account = account;
            account.Characters.Add(character);
        }

        return LoadWizard(character);
    }

    /// <summary>
    /// Loads all characters based on the specified account ID. Returns the characters loaded, so
    /// additional queries are made to load the character's inventory, equipment, etc.
    /// </summary>
    /// <param name="accountId">The account ID of the character to retrieve.</param>
    /// <param name="account">The account associated with the character. Characters will be added
    /// to the account.</param>
    /// <returns>The character with the specified account ID, or null if not found.</returns>
    public static Wizard[] LoadWizardsOntoAccount(ulong accountId, ref Account account) {
        using var session = s_store.OpenSession();

        var characters = session.Query<Wizard>(collectionName: CollectionName)
            .Customize(query => query.WaitForNonStaleResults(s_nonStaleWaitTimeout))
            .Where(x => x.AccountId == accountId)
            .ToList();

        for (var i = 0; i < characters.Count; i++) {
            characters[i].Account = account;
            account.Characters.Add(characters[i]);
        }

        // Load all of the characters. We must do this here because
        // `Wizard` initialization may require the account to be in full; aka, we need
        // all the characters to be on the account ahead of time.
        for (var i = 0; i < characters.Count; i++) {
            characters[i] = LoadWizard(characters[i]);
        }

        return [.. characters];
    }

    /// <summary>
    /// Retrieves all characters based on the specified account ID. Returns the characters unloaded, so no
    /// additional queries are made to load the character's inventory, equipment, etc.
    /// </summary>
    /// <param name="accountId">The account ID of the character to retrieve.</param>
    /// <param name="getAccount">Whether to retrieve the account associated with the character.</param>
    /// <returns>The character with the specified account ID, or null if not found.</returns>
    public static Wizard GetCharacterUnloaded(ulong charId) {
        if (Classic.Ambient.AmbientWizards.TryGetWizard(charId, out var ambient)) {
            return ambient; // CLASSIC
        }

        using var session = s_store.OpenSession();

        var character = GetCharacterByCharId(session, charId);

        return character;
    }

    /// <summary>
    /// Updates the zone information for a character.
    /// </summary>
    /// <param name="character">The character to update.</param>
    /// <param name="zoneName">The name of the zone.</param>
    /// <param name="zoneDisplayName">The display name of the zone.</param>
    public static void UpdateCharacterZone(Wizard character, string zoneName, string zoneDisplayName) {
        UpdateCharacter(character.CharId, existingCharacter => {
            existingCharacter.PreviousZone = existingCharacter.Zone;
            existingCharacter.Zone = zoneName;
            existingCharacter.ZoneDisplayName = zoneDisplayName;
        });
    }

    /// <summary>
    /// Updates the location and orientation of a character.
    /// </summary>
    /// <param name="character">The character to update.</param>
    /// <param name="location">The new location of the character.</param>
    /// <param name="orientation">The new orientation of the character.</param>
    public static void UpdateCharacterLocation(Wizard character, Vector3 location, float orientation) {
        UpdateCharacter(character.CharId, existingCharacter => {
            existingCharacter.Location = location;
            existingCharacter.Orientation = new Vector3(0, 0, orientation);
        });
    }

    /// <summary>
    /// Updates the marked location, orientation, and zone of a character.
    /// <paramref name="character"/>The character to update.</param>
    /// <param name="location">The new location of the character.</param>
    /// <param name="orientation">The new orientation of the character.</param>
    /// <param name="ZoneName">The new zone of the character.</param>
    /// <param name="zoneDisplayName">The display name of the new zone.</param>
    public static void UpdateCharacterMarkedLocation(Wizard character,
                                                     Vector3 location,
                                                     Vector3 orientation,
                                                     string ZoneName,
                                                     string zoneDisplayName) {
        UpdateCharacter(character.CharId, existingCharacter => {
            existingCharacter.MarkedLocation = location;
            existingCharacter.MarkedOrientation = orientation;
            existingCharacter.MarkedZone = ZoneName;
            existingCharacter.MarkedZoneDisplayName = zoneDisplayName;
        });
    }

    /// <summary>
    /// Updates the equipment of a character in the wizard collection.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated equipment.</param>
    public static void UpdateCharacterItems(Wizard wizard) {
        if (wizard is null) return;
        // CLASSIC: use the same lane as uncertain-commit marking; a failed purchase may be present in
        // the database even though its old live backpack/bag was never published.
        WithCharacterLock(wizard.CharId, () => {
            if (IsInventorySnapshotUncertain(wizard)) return false;
            return UpdateCharacter(wizard.CharId, existingCharacter => {
                existingCharacter.InventoryBehavior = wizard.InventoryBehavior;
                existingCharacter.EquipmentBehavior = wizard.EquipmentBehavior;
                existingCharacter.StorageBehavior = wizard.StorageBehavior; // CLASSIC: the dorm bank
                existingCharacter.PetSnackBehavior = wizard.PetSnackBehavior;
                existingCharacter.AlchemyBehavior = wizard.AlchemyBehavior;
            });
        });
    }

    /// <summary>
    /// Updates the character level of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated level.</param>
    public static void UpdateCharacterLevel(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter => {
            existingCharacter.MagicSchoolBehavior.Level = wizard.MagicSchoolBehavior.Level;
            existingCharacter.MagicSchoolBehavior.ExperiencePoints = wizard.MagicSchoolBehavior.ExperiencePoints;
        });
    }

    /// <summary>
    /// Updates the character mount for a wizard.
    /// </summary>
    /// <param name="wizard">The wizard whose character mount is being updated.</param>
    public static void UpdateCharacterMount(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.MountOwnerBehavior = wizard.MountOwnerBehavior);
    }

    /// <summary>
    /// Updates the character name override for a wizard.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated character name override.</param>
    public static void UpdateCharacterNameOverride(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.PlayerNameBehavior.NameOverride = wizard.PlayerNameBehavior.NameOverride);
    }

    /// <summary>
    /// Persists <see cref="Wizard.InteriorStowedMountId"/>: the mount auto-stowed for an interior, to be
    /// re-equipped outdoors. Written the moment it changes because a zone transfer is a disconnect.
    /// </summary>
    public static void UpdateCharacterInteriorStowedMount(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.InteriorStowedMountId = wizard.InteriorStowedMountId);
    }

    /// <summary>
    /// CLASSIC: persists <see cref="Wizard.CombatStowedMountId"/>, the mount taken off for a duel, the moment it
    /// changes: a session can end mid-fight.
    /// </summary>
    public static void UpdateCharacterCombatStowedMount(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.CombatStowedMountId = wizard.CombatStowedMountId);
    }

    /// <summary>
    /// Updates the character badge override for a wizard.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated character badge override.</param>
    public static void UpdateCharacterBadgeOverride(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.PlayerNameBehavior.BadgeTitle = wizard.PlayerNameBehavior.BadgeTitle);
    }

    /// <summary>
    /// Updates the character spellbook behavior for a wizard; persists the
    /// excluded item spell list and other spellbook state to the database.
    /// </summary>
    /// <param name="wizard">The wizard whose spellbook behavior should be persisted.</param>
    public static void UpdateCharacterSpellbookBehavior(Wizard wizard)
        => UpdateCharacterSpellbookBehavior(wizard, null, null);

    internal static bool UpdateCharacterSpellbookBehavior(Wizard wizard, Func<IDocumentSession> openSession,
        Func<IDocumentSession, ulong, Wizard> loadWizard) {
        return CommitCharacterMutation(wizard.CharId, (_, persisted) => {
            // Treasure-card quantities belong to the persisted add/remove operations.
            persisted.SpellbookBehavior.LearnedSpellTemplateIds = wizard.SpellbookBehavior.LearnedSpellTemplateIds?.ToList() ?? [];
            persisted.SpellbookBehavior.ExcludedItemSpellIds = wizard.SpellbookBehavior.ExcludedItemSpellIds?
                .ToDictionary(pair => pair.Key, pair => new HashSet<uint>(pair.Value)) ?? [];
            return true;
        }, persisted => PublishTreasureCards(wizard, persisted), openSession, loadWizard);
    }

    /// <summary>
    /// Updates the character game stats for a wizard.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated game stats</param>
    public static void UpdateCharacterGameStats(Wizard wizard)
        => UpdateCharacterGameStats(wizard, null, null);

    internal static bool UpdateCharacterGameStats(Wizard wizard, Func<IDocumentSession> openSession,
        Func<IDocumentSession, ulong, Wizard> loadWizard) {
        return CommitCharacterMutation(wizard.CharId, (_, persisted) => {
            var savedStats = persisted.GameStats;
            var snapshot = wizard.GameStats.CloneSnapshotWithGold(savedStats.m_currentGold);
            // CLASSIC: preserve each stored field independently; ordinary stats saves never normalize a wallet.
            snapshot.m_currentArenaPoints = savedStats.m_currentArenaPoints;
            snapshot.m_currentPvPCurrency = savedStats.m_currentPvPCurrency;
            persisted.GameStats = snapshot;
            return true;
        }, persisted => {
            wizard.GameStats.m_currentGold = persisted.GameStats.m_currentGold;
            wizard.GameStats.m_currentArenaPoints = persisted.GameStats.m_currentArenaPoints;
            wizard.GameStats.m_currentPvPCurrency = persisted.GameStats.m_currentPvPCurrency;
        }, openSession, loadWizard);
    }

    /// <summary>
    /// Updates the character pet owner behavior for a wizard.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated pet owner behavior.</param>
    public static void UpdateCharacterPetOwnerBehavior(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.PetOwnerBehavior = wizard.PetOwnerBehavior);
    }

    /// <summary>
    /// Updates the character's last time they clicked the "go to ___ (ex. commons)" button.
    /// </summary>
    /// <param name="wizard">The wizard to update the time for</param>
    public static void UpdateCharacterTimeWentHome(Wizard wizard, long time) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.TimeHomeLastClicked = time);
    }

    /// <summary>
    /// Updates the last time a wizard was selected from the character screen.
    /// </summary>
    public static void UpdateCharacterLastLoginTime(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.LastLoginTime = wizard.LastLoginTime);
    }

    /// <summary>
    /// Updates the training points of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard object containing the updated training points count.</param>
    public static void UpdateCharacterTrainingPoints(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.MagicSchoolBehavior.TrainingPoints = wizard.MagicSchoolBehavior.TrainingPoints);
    }

    /// <summary>
    /// Updates the friend behavior for a wizard; persists pending friend requests
    /// and other in-memory friend state to the database.
    /// </summary>
    /// <param name="wizard">The wizard whose friend behavior should be persisted.</param>
    public static void UpdateCharacterFriendBehavior(Wizard wizard) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.FriendsBehavior = wizard.FriendsBehavior);
    }

    /// <summary>
    /// Adds a spell to the spellbook of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard to add the spell to.</param>
    /// <param name="spellTemplateId">The ID of the spell template to add.</param>
    public static void LearnSpell(Wizard wizard, uint spellTemplateId) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.SpellbookBehavior.LearnedSpellTemplateIds.Add(spellTemplateId));
    }

    /// <summary>
    /// Removes a spell from the spellbook of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard whose spellbook will be modified.</param>
    /// <param name="spellTemplateId">The ID of the spell template to be removed.</param>
    public static void UnlearnSpell(Wizard wizard, uint spellTemplateId) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.SpellbookBehavior.LearnedSpellTemplateIds.Remove(spellTemplateId));
    }

    /// <summary>
    /// Adds a treasure card to the spellbook of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard to add the treasure card to.</param>
    /// <param name="spellTemplateId">The template ID of the spell to add as a treasure card.</param>
    public static void AddTreasureCard(Wizard wizard, uint spellTemplateId)
        => ChangeTreasureCard(wizard, spellTemplateId, add: true);

    internal static bool ChangeTreasureCard(Wizard wizard, uint spellTemplateId, bool add,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        // CLASSIC: the void legacy wrappers cannot turn a refused mutation into an acknowledged grant.
        if (!add) return TryRemoveTreasureCards(wizard, spellTemplateId, 1, out _, openSession, loadWizard);
        if (wizard is null || spellTemplateId == 0) return false;
        return CommitCharacterMutation(wizard.CharId, (_, persisted) => {
            if (IsInventorySnapshotUncertain(wizard) || !CanReceiveTreasureCards(persisted, 1)) return false;
            persisted.SpellbookBehavior.AddTreasureCard(spellTemplateId);
            return true;
        }, persisted => PublishTreasureCards(wizard, persisted), openSession, loadWizard,
            onSaveFailure: _ => MarkInventorySnapshotUncertain(wizard));
    }

    // CLASSIC: native deletion removes the fitting saved count in one acknowledged write, rather than
    // trusting the live book or saving each copy. The returned count alone authorizes the native receipt.
    internal static bool TryRemoveTreasureCards(Wizard liveWizard, uint templateId, int requested, out int removed,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        removed = 0;
        if (liveWizard is null || templateId == 0) return false;
        var staged = 0;
        var committed = CommitCharacterMutation(liveWizard.CharId, (_, saved) => {
            if (IsInventorySnapshotUncertain(liveWizard) || saved.SpellbookBehavior is null) return false;
            staged = Math.Min(Math.Max(1, requested), saved.SpellbookBehavior.TreasureCardCount(templateId));
            if (staged == 0) return false;
            for (var i = 0; i < staged; i++) saved.SpellbookBehavior.RemoveTreasureCard(templateId);
            return true;
        }, saved => PublishTreasureCards(liveWizard, saved), openSession, loadWizard,
            onSaveFailure: _ => MarkInventorySnapshotUncertain(liveWizard));
        if (committed) removed = staged;
        return committed;
    }

    internal static void PublishTreasureCards(Wizard liveWizard, Wizard persisted) {
        liveWizard.SpellbookBehavior.TreasureCardTemplateIds = persisted.SpellbookBehavior.TreasureCardTemplateIds?.ToList() ?? [];
        // CLASSIC: the deck Treasure Card ledger is saved with the book and published with it.
        liveWizard.SpellbookBehavior.DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(persisted.SpellbookBehavior.DeckTreasureCards);
        liveWizard.SpellbookBehavior.DeckTreasureLedgerVersion = persisted.SpellbookBehavior.DeckTreasureLedgerVersion;
    }

    /// <summary>
    /// CLASSIC: moves one Treasure Card from the book into a deck's Treasure Cards, in one save: refused when the saved
    /// book has none of it or the deck already holds <paramref name="maxInDeck"/> Treasure Cards.
    /// </summary>
    internal static bool MoveTreasureCardToDeck(Wizard liveWizard, ulong deckId, uint templateId, int maxInDeck,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null || templateId == 0) return false;
        return CommitCharacterMutation(liveWizard.CharId, (_, persisted) => {
            var book = persisted.SpellbookBehavior;
            if (IsInventorySnapshotUncertain(liveWizard) || book is null) return false; // CLASSIC: reload after uncertain save.
            if (book.TreasureCardCount(templateId) < 1 || book.DeckTreasureTotal(deckId) >= maxInDeck) return false;
            book.RemoveTreasureCard(templateId);
            return book.ChangeDeckTreasure(deckId, templateId, +1);
        }, persisted => PublishTreasureCards(liveWizard, persisted), openSession, loadWizard,
            onSaveFailure: _ => MarkInventorySnapshotUncertain(liveWizard));
    }

    /// <summary>
    /// CLASSIC: takes one Treasure Card out of a deck's Treasure Cards, in one save: back into the book, or spent
    /// (<paramref name="destroy"/>: cast, or used as an enchantment). Refused when the saved ledger has none of it in
    /// that deck, so only a card that went in as a Treasure Card can come out as one.
    /// </summary>
    internal static bool MoveTreasureCardFromDeck(Wizard liveWizard, ulong deckId, uint templateId, bool destroy,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null || templateId == 0) return false;
        return CommitCharacterMutation(liveWizard.CharId, (_, persisted) => {
            var book = persisted.SpellbookBehavior;
            // CLASSIC: reload after uncertain saves. Returning an already-owned deck card remains valid,
            // including for an older overfull book; this does not grant a new acquisition.
            if (IsInventorySnapshotUncertain(liveWizard) || book is null) return false;
            if (!book.ChangeDeckTreasure(deckId, templateId, -1)) return false;
            if (!destroy) book.AddTreasureCard(templateId);
            return true;
        }, persisted => PublishTreasureCards(liveWizard, persisted), openSession, loadWizard,
            onSaveFailure: _ => MarkInventorySnapshotUncertain(liveWizard));
    }

    /// <summary>
    /// CLASSIC: once per wizard, records the Treasure Cards older saves kept inside deck card lists (see
    /// Wizard.MigrateDeckTreasureCards) in the ledger. No-op when already done.
    /// </summary>
    internal static bool RecordMigratedDeckTreasureCards(Wizard liveWizard,
        IReadOnlyDictionary<ulong, Dictionary<uint, int>> found,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Wizard> loadWizard = null) {
        if (liveWizard is null) return false;
        return CommitCharacterMutation(liveWizard.CharId, (_, persisted) => {
            var book = persisted.SpellbookBehavior;
            if (book.DeckTreasureLedgerVersion >= 1) return false;
            foreach (var (deckId, cards) in found) {
                foreach (var (templateId, copies) in cards) {
                    if (copies > 0) book.ChangeDeckTreasure(deckId, templateId, copies);
                }
            }

            book.DeckTreasureLedgerVersion = 1;
            return true;
        }, persisted => PublishTreasureCards(liveWizard, persisted), openSession, loadWizard);
    }

    /// <summary>
    /// Removes a treasure card from the spellbook of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard whose spellbook will be modified.</param>
    /// <param name="spellTemplateId">The template ID of the treasure card to remove.</param>
    public static void RemoveTreasureCard(Wizard wizard, uint spellTemplateId)
        => ChangeTreasureCard(wizard, spellTemplateId, add: false);

    /// <summary>
    /// Adds a new relationship to the friends behavior of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard to add the relationship to.</param>
    /// <param name="relationship">The relationship to add.</param>
    public static void AddOrUpdateRelationship(Wizard wizard, Relationship relationship) {
        UpdateCharacter(wizard.CharId, existingCharacter => {
            existingCharacter.FriendsBehavior ??= new();
            existingCharacter.FriendsBehavior.AddOrUpdateRelationship(relationship);
        });
    }

    /// <summary>
    /// Removes a relationship from the friends behavior of a wizard.
    /// </summary>
    /// <param name="wizard">The wizard to remove the relationship from.</param>
    /// <param name="friendId">The ID of the friend to remove.</param>
    public static void RemoveRelationship(Wizard wizard, ulong friendId) {
        UpdateCharacter(wizard.CharId, existingCharacter =>
            existingCharacter.FriendsBehavior?.Breakup(friendId));
    }

    /// <summary>
    /// Update the quest behavior for a given wizard in the database.
    /// </summary>
    /// <param name="wizard">The wizard whose quest behavior needs to be updated.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    public static bool UpdateCharacterQuestBehavior(Wizard wizard) {
        if (wizard is null || wizard.QuestBehavior is null) {
            return false;
        }

        return UpdateCharacter(wizard.CharId, dbWizard =>
            dbWizard.QuestBehavior = wizard.QuestBehavior);
    }

    private static Wizard LoadWizard(Wizard wizard) {
        using var session = s_store.OpenSession();

        // `Wizard` only keeps track of the IDs of the items in the inventory.
        // The actual items are stored in the `WizClientObjectItem` collection.
        // Load the items in the inventory.
        var items = session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
            .Where(x => x.m_characterId == wizard.CharId)
            .ToList();

        // CLASSIC: a rental (a 1- or 7-day mount) whose time has run out is gone when the wizard next loads.
        var now = DateTimeOffset.UtcNow;
        var expired = items.Where(item => WizardItemCollection.IsExpired(item, now)).Select(item => item.m_globalID).ToHashSet();
        if (expired.Count > 0) {
            items = [.. items.Where(item => !expired.Contains(item.m_globalID))];
            wizard.InventoryBehavior.InventoryItemIds = [.. wizard.InventoryBehavior.InventoryItemIds.Where(id => !expired.Contains(id))];
            wizard.EquipmentBehavior.EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds.Where(id => !expired.Contains(id))];
            Logger.Information("Wizard {0}: {1} rental item(s) expired.", Logger.Args(wizard.CharId, expired.Count));
        }

        wizard.InventoryBehavior.Items = [.. items
            .Where(i => wizard.InventoryBehavior.InventoryItemIds
            .Contains(i.m_globalID))
        ];

        // CLASSIC: the dorm bank keeps its items under the wizard's id too (BankService); older characters have none.
        wizard.StorageBehavior ??= new();
        wizard.StorageBehavior.BankItemIds ??= [];
        if (expired.Count > 0) {
            wizard.StorageBehavior.BankItemIds = [.. wizard.StorageBehavior.BankItemIds.Where(id => !expired.Contains(id))];
        }
        var bankIds = wizard.StorageBehavior.BankItemIds.ToHashSet();
        wizard.StorageBehavior.Items = [.. items.Where(i => bankIds.Contains(i.m_globalID))];

        // Load the character's equipment.
        // The equipped items are stored as global IDs in the character's EquipmentBehavior.
        // Find any items in the inventory that match the equipped item IDs.
        wizard.EquipmentBehavior.EquippedItems = [.. items
            .Where(i => wizard.EquipmentBehavior.EquippedItemIds
            .Any(e => i.m_globalID == e))
        ];

        // CLASSIC: the snack bag lives in its own collection and was never read back, so bought snacks were gone at the
        // next zone. Load it like the backpack.
        var snacks = session.Query<ClientPetSnackItem>(collectionName: WizardPetSnackCollection.CollectionName)
            .Where(x => x.m_characterId == wizard.CharId)
            .ToList();
        wizard.PetSnackBehavior ??= new();
        var snackIds = wizard.PetSnackBehavior.SnackItemIds;
        wizard.PetSnackBehavior.Snacks = [.. snacks.Where(s => s.m_quantity > 0 && (snackIds is null || snackIds.Contains(s.m_globalID)))];

        // CLASSIC: reagent rows live in a separate collection too. Restore only this wizard's exact
        // saved references; orphan rows and existing counts are retained without adopting or rewriting them.
        if (!WizardReagentCollection.TryReadOwnedBag(session, wizard, out var reagents))
            throw new InvalidOperationException("Saved reagent bag contains ambiguous or missing owned rows.");
        wizard.AlchemyBehavior ??= new();
        wizard.AlchemyBehavior.Reagents = reagents;

        // The friends list is expanded to include a 'relationship' model
        // which helps keep track of the relationship between two players for moderation purposes.
        // `Wizard` only keeps track of the IDs of the relationships.
        // The actual relationships are stored in the `BuddyRelationshipCollection`.
        var relationships = session
            .Query<Relationship>(collectionName: BuddyRelationshipCollection.CollectionName)
            .Where(x => x.FirstPlayerId == wizard.CharId || x.SecondPlayerId == wizard.CharId)
            .ToList();
        relationships.ForEach(r => Imlight.CoreLib.Classic.IgnoreRules.MigrateLegacy(r)); // CLASSIC: in memory (schema 3)
        wizard.FriendsBehavior ??= new();
        wizard.FriendsBehavior.Relationships = relationships;

        // Load character dynamic modifications.
        var dynamods = session
            .Query<DynamodSet>(collectionName: DynamodCollection.CollectionName)
            .Where(d => d.CharId == wizard.CharId)
            .ToList();
        wizard.DynamodSet = dynamods.FirstOrDefault() ?? new DynamodSet(wizard.CharId);

        // Load the actual quests the character has.
        if (wizard.QuestBehavior != null) {
            var currentQuestIDs = wizard.QuestBehavior.CurrentQuestIDs;
            var myQuests = session
                .Query<QuestInstance>(collectionName: QuestInstanceCollection.CollectionName)
                .Where(q => q.OwnerCharId == wizard.CharId)
                .ToList();

            // CLASSIC: an instance still under an old (made-up) quest name (the start-up migration was off or
            // failed) is read under the quest's KingsIsle name; it is saved that way on its next update.
            foreach (var quest in myQuests) {
                if (quest is not null && Imlight.Classic.Quests.QuestNameAliases.Current.NewNameOf(quest.QuestName) is { } renamed) {
                    quest.QuestName = renamed;
                }
            }

            wizard.QuestBehavior.CurrentQuestInstances = myQuests;
        }

        // The wizard may still have some initialization to do. Inform the wizard
        // that their data has been loaded from the database.
        wizard.AfterDatabaseLoad();

        return wizard;
    }

    private static Wizard GetCharacterByCharId(IDocumentSession session, ulong charId)
        => session.Query<Wizard>(collectionName: CollectionName)
            .Customize(query => query.WaitForNonStaleResults(s_nonStaleWaitTimeout))
            .FirstOrDefault(character => character.CharId == charId);

}
