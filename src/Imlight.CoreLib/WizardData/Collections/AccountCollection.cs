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

using System.Linq;
using Raven.Client.Documents;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using System;

namespace Imlight.CoreLib.WizardData.Collections;

public static class AccountCollection {
    
    public const string CollectionName = "Accounts";
    // CLASSIC: lazy, as WizardCollection's, so the Crowns transactions can be tested with a session double.
    private static readonly Lazy<IDocumentStore> s_storeSource = new(() => PlayerDatabase.Instance.Store);
    private static IDocumentStore s_store => s_storeSource.Value;
    // CLASSIC: uniqueness spans account IDs/write lanes. Registration always takes this gate before its account lane.
    private static readonly object s_registrationGate = new();

    private const int WriteLaneCount = 1 << 6; // 64 lanes
    private const ulong WriteLaneMask = WriteLaneCount - 1;
    private static readonly object[] s_writeLanes =
        Enumerable.Range(0, WriteLaneCount)
            .Select(_ => new object())
            .ToArray();

    // Tracks the write lane held by the current thread.
    // Enforces that an account lock is acquired before a character lock
    // and prevents acquiring multiple Account lanes at the same time.
    [ThreadStatic]
    private static int? s_heldWriteLane;

    private static T WithWriteLane<T>(ulong accountId, Func<T> write) {
        if (WizardCollection.HoldsWriteLane)
            throw new InvalidOperationException("Cannot acquire an account write lane while holding a wizard write lane.");

        var laneIndex = (int) (accountId & WriteLaneMask);
        if (s_heldWriteLane is { } heldLane && heldLane != laneIndex)
            throw new InvalidOperationException($"Cannot acquire account lane {laneIndex} while holding account lane {heldLane}.");

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

    // CLASSIC: housing purchases commit the account, wizard and original deed together.
    // Reuse the existing account-before-character lock order; never a debit/refund split.
    internal static T WithAccountWriteLane<T>(ulong accountId, Func<T> write)
        => WithWriteLane(accountId, write);

    private static bool UpdateAccount(ulong accountId, Action<Account> update) {
        return WithWriteLane(accountId, () => {
            using var session = s_store.OpenSession();

            var existingAccount = session.Query<Account>(collectionName: CollectionName)
                .FirstOrDefault(account => account.AccountId == accountId);

            if (existingAccount is null)
                return false;

            update(existingAccount);

            session.SaveChanges();

            return true;
        });
    }

    /// <summary>
    /// CLASSIC: one read-modify-write of the saved account under its write lane (optimistic concurrency on): the
    /// saved copy is loaded, <paramref name="operation"/> changes it (false: nothing is saved), and only after the
    /// save does <paramref name="afterCommit"/> publish the result to the live objects. Crowns go through here, as
    /// gold goes through WizardCollection.CommitCharacterMutation, so no live copy is ever written over the database.
    /// </summary>
    internal static bool CommitAccountMutation(ulong accountId, Func<Account, bool> operation, Action<Account> afterCommit,
        Func<IDocumentSession> openSession = null, Func<IDocumentSession, ulong, Account> loadAccount = null) {
        return WithWriteLane(accountId, () => {
            using var session = openSession is null ? s_store.OpenSession() : openSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var persisted = loadAccount is null
                ? session.Query<Account>(collectionName: CollectionName).FirstOrDefault(account => account.AccountId == accountId)
                : loadAccount(session, accountId);
            if (persisted is null || !operation(persisted)) {
                return false;
            }

            session.SaveChanges();
            afterCommit?.Invoke(persisted);

            return true;
        });
    }

    private static ulong? GetAccountId(string username) {
        using var session = s_store.OpenSession();

        return session.Query<Account>(collectionName: CollectionName)
            .Where(account => account.Username == username)
            .Select(account => (ulong?) account.AccountId)
            .FirstOrDefault();
    }

    private static bool UpdateAccount(string username, Action<Account> update) {
        var accountId = GetAccountId(username);

        if (accountId is null)
            return false;

        return UpdateAccount(accountId.Value, update);
    }

    private static Account LoadAccountDetails(IDocumentSession session, Account account) {
        WizardCollection.LoadWizardsOntoAccount(account.AccountId, ref account);

        var infractions = session.Query<Infraction>(collectionName: InfractionCollection.CollectionName)
            .Where(i => i.AccountId == account.AccountId)
            .ToList();

        account.InfractionHistory = new InfractionHistory(account.AccountId, infractions);

        return account;
    }

    /// <summary>
    /// Creates a new account in the database.
    /// </summary>
    /// <param name="account">The account to be created.</param>
    /// <returns>True if the account is successfully created, false if the account already exists.</returns>
    public static bool CreateAccount(Account account)
        => CreateAccount(account, () => s_store.OpenSession(), (session, username) =>
            // CLASSIC: a previous registration must be visible before the next uniqueness check. Raven's default
            // string equality is case-insensitive, so an existing case variant is reserved as well.
            session.Query<Account>(collectionName: CollectionName)
                .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Any(existing => existing.Username == username));

    internal static bool CreateAccount(Account account, Func<IDocumentSession> openSession,
        Func<IDocumentSession, string, bool> usernameExists) {
        ArgumentNullException.ThrowIfNull(account);
        if (s_heldWriteLane is not null || WizardCollection.HoldsWriteLane)
            throw new InvalidOperationException("Account registration must start outside account and wizard write lanes.");
        lock (s_registrationGate) {
            return WithWriteLane(account.AccountId, () => {
                using var session = openSession();

                // Return false if the account already exists.
                if (usernameExists(session, account.Username)) {
                    return false;
                }

                // Foreach character in the account, add it to the database.
                foreach (var character in account.Characters) {
                    WizardCollection.AddCharacter(character);
                }

                session.Store(account);
                var metadata = session.Advanced.GetMetadataFor(account);
                metadata[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

                session.SaveChanges();

                return true;
            });
        }
    }

    /// <summary>
    /// Deletes an account and all associated characters from the database.
    /// </summary>
    /// <param name="username">The username of the account to delete.</param>
    /// <returns>True if the account was successfully deleted, false otherwise.</returns>
    public static bool DeleteAccount(string username) {
        var id = GetAccountId(username);

        if (id is null)
            return false;

        var accountId = id.Value;

        return WithWriteLane(accountId, () => {
            using var session = s_store.OpenSession();

            var account = session.Query<Account>(collectionName: CollectionName)
                .FirstOrDefault(a => a.AccountId == accountId);

            if (account is null)
                return false;

            // Delete the characters.
            foreach (var characterId in account.CharacterIds) {
                WizardCollection.DeleteCharacter(characterId);
            }

            // Delete the account.
            session.Delete(account);
            session.SaveChanges();

            return true;
        });
    }

    /// <summary>
    /// Gets an account from the database by its ID.
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public static Account GetAccount(ulong id) {
        using var session = s_store.OpenSession();

        var account = session.Query<Account>(collectionName: CollectionName)
            .FirstOrDefault(a => a.AccountId == id);

        return account is null
            ? null
            : LoadAccountDetails(session, account);
    }

    /// <summary>
    /// Gets an account from the database by its username.
    /// </summary>
    /// <param name="username"></param>
    /// <returns></returns>
    public static Account GetAccount(string username) {
        using var session = s_store.OpenSession();

        var account = session.Query<Account>(collectionName: CollectionName)
            .FirstOrDefault(a => a.Username == username);

        return account is null
            ? null
            : LoadAccountDetails(session, account);
    }

    /// <summary>
    /// Locks the specified account by setting its IsLocked property to true.
    /// </summary>
    /// <param name="account">The account to be locked.</param>
    /// <returns>True if the account was successfully locked, false otherwise.</returns>
    public static bool LockAccount(string username) {
        return UpdateAccount(username, account => {
            account.IsLocked = true;
        });
    }

    /// <summary>
    /// Unlocks the account with the specified username.
    /// </summary>
    /// <param name="username">The username of the account to unlock.</param>
    /// <returns>True if the account was successfully unlocked, false otherwise.</returns>
    public static bool UnlockAccount(string username) {
        return UpdateAccount(username, account => {
            account.IsLocked = false;
        });
    }

    /// <summary>
    /// Changes the password for the specified account.
    /// </summary>
    /// <param name="account">The account to change the password for.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>True if the password was successfully changed, false otherwise.</returns>
    public static bool ChangePassword(string username, string newPassword) {
        // CLASSIC: a PBKDF2 verifier plus the (sealed) client protocol hash, see Auth/PasswordStore.
        var (passwordHash, verifier) = Imlight.CoreLib.Auth.PasswordStore.Records(newPassword);
        var changed = UpdateAccount(username, account => {
            account.PasswordHash = passwordHash;
            account.PasswordVerifier = verifier;
        });

        // CLASSIC: a new password ends the old login and attach keys.
        if (changed && GetAccountId(username) is { } accountId) {
            try {
                ClientKeyCollection.Revoke(accountId);
                Imlight.CoreLib.Auth.SecuritySettings.GameKeys.Value.Revoke(accountId);
            }
            catch (Exception) {
                // the keys expire on their own
            }
        }

        return changed;
    }

    /// <summary>CLASSIC: stores upgraded password records (Auth/PasswordStore).</summary>
    public static bool UpdatePasswordRecords(ulong accountId, string passwordHash, string verifier)
        => UpdateAccount(accountId, account => {
            account.PasswordHash = passwordHash;
            account.PasswordVerifier = verifier;
        });

    /// <summary>
    /// Updates the authentication level of an account.
    /// </summary>
    /// <param name="account">The account to update.</param>
    /// <param name="authLevel">The new authentication level.</param>
    /// <returns>True if the update was successful, false otherwise.</returns>
    public static bool UpdateAuthLevel(string username, AuthLevel authLevel) {
        return UpdateAccount(username, account => {
            account.AuthLevel = authLevel;
        });
    }

    /// <summary>
    /// Adds a character to an account.
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="characterId"></param>
    /// <returns></returns>
    public static bool AddCharacterToAccount(ulong accountId, ulong characterId) {
        return UpdateAccount(accountId, account => {
            account.CharacterIds.Add(characterId);
        });
    }

    /// <summary>
    /// Removes a character from an account.
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="characterId"></param>
    /// <returns></returns>
    public static bool DeleteCharacterFromAccount(ulong accountId, ulong characterId) {
        return UpdateAccount(accountId, account => {
            account.CharacterIds.Remove(characterId);
        });
    }

    /// <summary>
    /// Adds an infraction to the account with the specified account ID.
    /// </summary>
    /// <param name="accountId">The ID of the account to add the infraction to.</param>
    /// <param name="infractionId">The ID of the infraction to add.</param>
    /// <returns></returns>
    public static bool AddInfractionToAccount(ulong accountId, ulong infractionId) {
        return UpdateAccount(accountId, account => {
            account.InfractionIds.Add(infractionId);
        });
    }

    /// <summary>
    /// Removes an infraction from an account.
    /// </summary>
    /// <param name="accountId">The ID of the account.</param>
    /// <param name="infractionId">The ID of the infraction to remove.</param>
    /// <returns></returns>
    public static bool RemoveInfractionFromAccount(ulong accountId, ulong infractionId) {
        return UpdateAccount(accountId, account => {
            account.InfractionIds.Remove(infractionId);
        });
    }

    /// <summary>
    /// Adds a character slot to an account.
    /// </summary>
    /// <param name="accountId">The ID of the account.</param>
    /// <returns></returns>
    public static bool AddPurchasedCharacterSlot(ulong accountId) {
        return UpdateAccount(accountId, account =>
            ++account.PurchasedCharacterSlots);
    }

    /// <summary>
    /// Remove a character slot from an account.
    /// </summary>
    /// <param name="accountId">The ID of the account.</param>
    /// <returns></returns>
    public static bool RemovePurchasedCharacterSlot(ulong accountId) {
        return UpdateAccount(accountId, account => {
            if (account.PurchasedCharacterSlots > 0)
                --account.PurchasedCharacterSlots;
        });
    }

}
