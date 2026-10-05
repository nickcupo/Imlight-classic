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
 * PASSWORD STORAGE
 * ========================================================================
 *
 * PURPOSE:
 * The account's password records.
 *
 * NOTE:
 * The r806919 client never sends the password. Its MSG_USER_AUTHEN_V3 proves
 * ClientKey1 = Base64(SHA-512(H || salt)), where H = Base64(SHA-512(password))
 * and the salt is the session id and offer time from the (public) handshake.
 * To check that, the server must hold H itself: H is password-equivalent for
 * the in-client login, and no salted slow hash of the password can stand in
 * for it. So there are two records:
 *  - PasswordVerifier: PBKDF2-HMAC-SHA512 (random 16-byte salt, 210,000
 *    iterations) of the password. Every path that sees the password (launcher
 *    login, admin page, account creation, password change) checks this one.
 *  - PasswordHash: H, only while the in-client login is allowed, sealed with
 *    AES-256-GCM under a server key kept outside the database ("enc1:" prefix).
 *    A database or backup without the key file yields neither H nor anything
 *    crackable faster than PBKDF2.
 * Old accounts (plain H, no verifier) are upgraded on their next login that
 * sees the password; an in-client login seals a plain H.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Security.Cryptography;
using System.Text;

namespace Imlight.Classic.Net;

public static class PasswordHashing {

    public const string VerifierPrefix = "pbkdf2-sha512";
    public const string SealedPrefix = "enc1:";
    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 64;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>H: Base64(SHA-512(UTF-8 password)), what the client's ClientKey1 is built on.</summary>
    public static string ProtocolHash(string password)
        => Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(password)));

    /// <summary>A new PBKDF2 verifier: "pbkdf2-sha512$iterations$salt$hash" (Base64 parts).</summary>
    public static string CreateVerifier(string password, int iterations = DefaultIterations) {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA512, HashBytes);
        return $"{VerifierPrefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Whether <paramref name="password"/> matches a verifier (constant-time); false for a malformed one.</summary>
    public static bool Verify(string? verifier, string password) {
        if (string.IsNullOrEmpty(verifier)) {
            return false;
        }

        var parts = verifier.Split('$');
        if (parts.Length != 4 || parts[0] != VerifierPrefix || !int.TryParse(parts[1], out var iterations)
                || iterations is < 1_000 or > 10_000_000) {
            return false;
        }

        byte[] salt, expected;
        try {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) {
            return false;
        }

        if (expected.Length is 0 or > 128) {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Whether a stored verifier is weaker than the current default (fewer iterations).</summary>
    public static bool VerifierNeedsRehash(string? verifier)
        => verifier is null || !verifier.StartsWith(VerifierPrefix + "$", StringComparison.Ordinal)
           || !int.TryParse(verifier.Split('$')[1], out var n) || n < DefaultIterations;

    public static bool IsSealed(string? stored) => stored?.StartsWith(SealedPrefix, StringComparison.Ordinal) == true;

    /// <summary>Seals H under a 32-byte key: "enc1:" + Base64(nonce || ciphertext || tag).</summary>
    public static string Seal(string protocolHash, byte[] key) {
        if (key is not { Length: 32 }) {
            throw new ArgumentException("The password key must be 32 bytes.", nameof(key));
        }

        var plain = Encoding.UTF8.GetBytes(protocolHash);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes)) {
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(SealedPrefix));
        }

        var all = new byte[NonceBytes + cipher.Length + TagBytes];
        nonce.CopyTo(all, 0);
        cipher.CopyTo(all, NonceBytes);
        tag.CopyTo(all, NonceBytes + cipher.Length);
        return SealedPrefix + Convert.ToBase64String(all);
    }

    /// <summary>
    /// H from a stored PasswordHash: a plain H as is, a sealed one opened with <paramref name="key"/>; null when it is
    /// empty, sealed without a key, or does not open.
    /// </summary>
    public static string? Open(string? stored, byte[]? key) {
        if (string.IsNullOrEmpty(stored)) {
            return null;
        }

        if (!IsSealed(stored)) {
            return LooksLikeProtocolHash(stored) ? stored : null;
        }

        if (key is not { Length: 32 }) {
            return null;
        }

        byte[] all;
        try {
            all = Convert.FromBase64String(stored[SealedPrefix.Length..]);
        }
        catch (FormatException) {
            return null;
        }

        if (all.Length < NonceBytes + TagBytes + 1) {
            return null;
        }

        var nonce = all.AsSpan(0, NonceBytes);
        var cipher = all.AsSpan(NonceBytes, all.Length - NonceBytes - TagBytes);
        var tag = all.AsSpan(all.Length - TagBytes);
        var plain = new byte[cipher.Length];
        try {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(SealedPrefix));
        }
        catch (CryptographicException) {
            return null;
        }

        var text = Encoding.UTF8.GetString(plain);
        return LooksLikeProtocolHash(text) ? text : null;
    }

    /// <summary>H is Base64 of 64 bytes (88 characters).</summary>
    public static bool LooksLikeProtocolHash(string? text) {
        if (text is not { Length: 88 }) {
            return false;
        }

        Span<byte> buffer = stackalloc byte[66];
        return Convert.TryFromBase64String(text, buffer, out var written) && written == 64;
    }

    /// <summary>Constant-time comparison of two H strings.</summary>
    public static bool SameHash(string? a, string? b)
        => a is not null && b is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>A new random password key, as written to the key file (Base64 of 32 bytes).</summary>
    public static string NewKeyText() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The key from a key file's text, or null when it is not Base64 of 32 bytes.</summary>
    public static byte[]? ParseKey(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        try {
            var key = Convert.FromBase64String(text.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException) {
            return null;
        }
    }
}
