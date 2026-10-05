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
 * CROWNS ON THE PRIVATE SERVER
 * ========================================================================
 *
 * PURPOSE:
 * Crowns are an account balance. On this server every account starts with
 * Classic.StartingCrowns (default 100,000), granted once, and every defeated
 * mob pays as many Crowns as it pays gold (Classic.CrownsFromMobs, default on).
 * Owner decision 2026-09-28; not a 2009 rule (in 2009 Crowns were bought).
 *
 * Every change is a read-modify-write of the saved account under its write
 * lane (AccountCollection.CommitAccountMutation), then published to the live
 * Account: a second session's stale balance can no longer be written back.
 *
 * USAGE EXAMPLE:
 * ClassicCrowns.EnsureStartingCrowns(account);
 * ClassicCrowns.Add(account, goldFromMobs);
 * SendToSocket(ClassicCrowns.BalanceMessage(account, wizard.CharId));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The server's Crowns economy: a starting balance and Crowns from mobs.
/// </summary>
public static class ClassicCrowns {

    /// <summary>The Crowns every account is given once.</summary>
    public static int StartingCrowns { get; } = Math.Max(0, Setting("Classic.StartingCrowns") is { } crowns
        && int.TryParse(crowns, out var value) ? value : 100_000);

    /// <summary>True when a defeated mob pays as many Crowns as gold.</summary>
    public static bool CrownsFromMobs { get; } = Setting("Classic.CrownsFromMobs") is not { } fromMobs
        || !bool.TryParse(fromMobs, out var on) || on;

    // The setting's text, or null when the ini does not set it. (AsInt/AsBool with a default return 0/false for a
    // missing key: the default only applies when parsing throws.)
    private static string Setting(string key) => ConfigurationManager.Settings[key].AsString() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Gives <paramref name="account"/> its starting Crowns if it has never had them. Safe to call on every login.
    /// </summary>
    /// <returns>True if Crowns were given now.</returns>
    public static bool EnsureStartingCrowns(Account account) => EnsureStartingCrowns(account, null, null);

    internal static bool EnsureStartingCrowns(Account account, Func<IDocumentSession> openSession,
        Func<IDocumentSession, ulong, Account> loadAccount) {
        if (account is null) {
            return false;
        }

        // CLASSIC: decided on the saved account, so two sessions (or a restart mid-grant) never give it twice. Tops an
        // account up to the starting amount, so raising the setting (or an earlier grant of 0) is made good.
        var owed = 0;
        var given = Commit(account, persisted => {
            owed = StartingCrowns - persisted.StartingCrownsGiven;
            if (owed <= 0) {
                return false;
            }

            persisted.Crowns = Cap((long) persisted.Crowns + owed);
            persisted.StartingCrownsGiven = StartingCrowns;

            return true;
        }, openSession, loadAccount);
        if (!given) {
            return false;
        }

        Logger.Information("[CROWNS] Account {0}: starting Crowns +{1}, balance {2}.",
            Logger.Args(account.AccountId, owed, account.Crowns));

        return true;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> Crowns to the saved balance (capped at int.MaxValue) and publishes it. A negative
    /// amount is a spend: it goes through <see cref="TrySpend(Account, int)"/> and changes nothing when the account
    /// cannot pay (no clamping at zero, which let a spend elsewhere make an item cheaper).
    /// </summary>
    /// <returns>The balance afterwards.</returns>
    public static int Add(Account account, int amount) => Add(account, amount, null, null);

    internal static int Add(Account account, int amount, Func<IDocumentSession> openSession,
        Func<IDocumentSession, ulong, Account> loadAccount) {
        if (account is null || amount == 0) {
            return account?.Crowns ?? 0;
        }

        if (amount < 0) {
            TrySpend(account, amount == int.MinValue ? int.MaxValue : -amount, openSession, loadAccount);

            return account.Crowns;
        }

        Commit(account, persisted => {
            persisted.Crowns = Cap((long) persisted.Crowns + amount);

            return true;
        }, openSession, loadAccount);

        return account.Crowns;
    }

    /// <summary>
    /// CLASSIC: spends <paramref name="amount"/> Crowns only if the saved balance holds them, checked and debited in one
    /// save under the account's write lane; the live balance is set from the saved one afterwards.
    /// </summary>
    /// <returns>True if they were spent.</returns>
    public static bool TrySpend(Account account, int amount) => TrySpend(account, amount, null, null);

    internal static bool TrySpend(Account account, int amount, Func<IDocumentSession> openSession,
        Func<IDocumentSession, ulong, Account> loadAccount) {
        if (account is null || amount < 0) {
            return false;
        }

        if (amount == 0) {
            return true;
        }

        return Commit(account, persisted => {
            if (persisted.Crowns < amount) {
                return false;
            }

            persisted.Crowns -= amount;

            return true;
        }, openSession, loadAccount);
    }

    /// <summary>CLASSIC: sets the balance (the QA .mod crowns command).</summary>
    public static int Set(Account account, int balance) {
        if (account is null) {
            return 0;
        }

        Commit(account, persisted => {
            persisted.Crowns = Math.Max(0, balance);

            return true;
        }, null, null);

        return account.Crowns;
    }

    // One saved read-modify-write; the live account is updated from the saved copy only after the save.
    private static bool Commit(Account account, Func<Account, bool> operation, Func<IDocumentSession> openSession,
        Func<IDocumentSession, ulong, Account> loadAccount)
        => AccountCollection.CommitAccountMutation(account.AccountId, operation, persisted => {
            account.Crowns = persisted.Crowns;
            account.StartingCrownsGiven = persisted.StartingCrownsGiven;
        }, openSession, loadAccount);

    /// <summary>
    /// The message that shows <paramref name="account"/>'s balance on the character page and in the Crown Shop.
    /// </summary>
    public static WIZARD_12_PROTOCOL.MSG_CROWNBALANCE BalanceMessage(Account account, ulong characterId) => new() {
        Failure = 0,
        TotalCrowns = account?.Crowns ?? 0,
        CharacterID = characterId,
        // Also updates the client's Crown Shop cache used for affordability.
        CacheBalanceForCSSegmentation = 1,
    };

    private static int Cap(long value) => (int) Math.Clamp(value, 0, int.MaxValue);

}
