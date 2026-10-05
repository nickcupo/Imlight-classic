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
 * ACCOUNT PASSWORDS
 * ========================================================================
 *
 * PURPOSE:
 * Sets and checks an account's password records (see PasswordHashing for the
 * scheme and why the protocol hash H has to be kept for the in-client login).
 *
 * SETTINGS ([Classic]):
 * InClientPasswordLogin (default true): the r806919 login screen may log in
 *   with a password. False: only launcher/session-key logins; H is dropped
 *   from each account at its next password login and never stored again.
 * PasswordKeyFile (default empty): a file holding Base64 of 32 random bytes.
 *   When set, H is stored sealed (AES-256-GCM) and plain H is sealed at the
 *   next login. Created with mode 600 if missing. Keep it out of backups that
 *   leave the machine, and never lose it while H is needed (a lost key only
 *   costs the in-client login until the next launcher login).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

#nullable enable

using System;
using System.IO;
using Imlight.Classic.Net;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Auth;

internal static class PasswordStore {

    private static readonly Lazy<bool> s_inClient = new(() => SecuritySettings.Bool("Classic.InClientPasswordLogin", true));
    private static readonly Lazy<byte[]?> s_key = new(LoadKey);

    internal static bool InClientLoginAllowed => s_inClient.Value;

    internal static byte[]? Key => s_key.Value;

    /// <summary>The two records for a new or changed password.</summary>
    internal static (string Hash, string Verifier) Records(string password)
        => Records(password, InClientLoginAllowed, Key);

    internal static (string Hash, string Verifier) Records(string password, bool inClient, byte[]? key) {
        var verifier = PasswordHashing.CreateVerifier(password);
        if (!inClient) {
            return ("", verifier);
        }

        var h = PasswordHashing.ProtocolHash(password);
        return (key is null ? h : PasswordHashing.Seal(h, key), verifier);
    }

    /// <summary>H for the in-client login check, or null when this account cannot log in that way.</summary>
    internal static string? ProtocolHashOf(Account account)
        => InClientLoginAllowed ? PasswordHashing.Open(account.PasswordHash, Key) : null;

    /// <summary>
    /// Checks a plaintext password (launcher, admin page). Prefers the PBKDF2 verifier; an account without one is
    /// checked against H and upgraded on success.
    /// </summary>
    internal static bool Verify(Account account, string password)
        => Verify(account.AccountId, account.PasswordHash, account.PasswordVerifier, password);

    internal static bool Verify(ulong accountId, string? storedHash, string? verifier, string password) {
        bool ok;
        if (!string.IsNullOrEmpty(verifier)) {
            ok = PasswordHashing.Verify(verifier, password);
        }
        else {
            var h = PasswordHashing.Open(storedHash, Key);
            ok = h is not null && PasswordHashing.SameHash(PasswordHashing.ProtocolHash(password), h);
        }

        if (ok && NeedsUpgrade(storedHash, verifier, InClientLoginAllowed, Key)) {
            try {
                var (hash, newVerifier) = Records(password);
                AccountCollection.UpdatePasswordRecords(accountId, hash, newVerifier);
                Logger.Information("Account {0}: password records upgraded.", Logger.Args(accountId));
            }
            catch (Exception ex) {
                Logger.Warning("Account {0}: password upgrade failed: {1}", Logger.Args(accountId, ex.Message));
            }
        }

        return ok;
    }

    /// <summary>After an in-client login: seal a plain H when a key is configured.</summary>
    internal static void AfterProtocolLogin(Account account) {
        var key = Key;
        if (key is null || PasswordHashing.IsSealed(account.PasswordHash)
                || !PasswordHashing.LooksLikeProtocolHash(account.PasswordHash)) {
            return;
        }

        try {
            AccountCollection.UpdatePasswordRecords(account.AccountId, PasswordHashing.Seal(account.PasswordHash, key),
                account.PasswordVerifier);
        }
        catch (Exception ex) {
            Logger.Warning("Account {0}: sealing the password hash failed: {1}", Logger.Args(account.AccountId, ex.Message));
        }
    }

    internal static bool NeedsUpgrade(string? storedHash, string? verifier, bool inClient, byte[]? key) {
        if (string.IsNullOrEmpty(verifier) || PasswordHashing.VerifierNeedsRehash(verifier)) {
            return true;
        }

        if (!inClient) {
            return !string.IsNullOrEmpty(storedHash);
        }

        if (PasswordHashing.Open(storedHash, key) is null) {
            return true; // H missing (in-client login was off) or sealed with a lost key
        }

        return key is not null && !PasswordHashing.IsSealed(storedHash);
    }

    private static byte[]? LoadKey() {
        var path = SecuritySettings.Text("Classic.PasswordKeyFile")?.Trim();
        if (string.IsNullOrEmpty(path)) {
            return null;
        }

        try {
            if (!File.Exists(path)) {
                var text = PasswordHashing.NewKeyText();
                var options = new FileStreamOptions {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write,
                };
                if (!OperatingSystem.IsWindows()) {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(path, options))
                using (var writer = new StreamWriter(stream)) {
                    writer.WriteLine(text);
                }

                Logger.Information("Created the password key file {0}.", Logger.Args(path));
            }

            var key = PasswordHashing.ParseKey(File.ReadAllText(path));
            if (key is null) {
                Logger.Error("Classic.PasswordKeyFile {0} does not hold Base64 of 32 bytes; password hashes stay unsealed.",
                    Logger.Args(path));
            }

            return key;
        }
        catch (Exception ex) {
            Logger.Error("Classic.PasswordKeyFile {0} could not be read: {1}", Logger.Args(path, ex.Message));
            return null;
        }
    }
}
