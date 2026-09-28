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
 * TRUE FRIEND CODES
 * ========================================================================
 *
 * PURPOSE:
 * The rules of True Friend codes: making one for a wizard and checking one
 * another wizard enters.
 *
 * USAGE EXAMPLE:
 * var made = TrueFriendCodes.Create(store, creatorCharId, creatorName, DateTimeOffset.UtcNow);
 * var used = TrueFriendCodes.Use(store, userCharId, enteredCode, DateTimeOffset.UtcNow, isFriend);
 *
 * NOTE:
 * 2009 (Friends oldid 17171, 2009-05-29): select an online friend, click True
 * Friend and get a code; give it to someone you know outside the game, and
 * they enter it next time to become your True Friend, which allows text chat
 * whatever the chat level (Chat oldid 56193, 2010-01-09). The client's text
 * (GUI.lang 00000499, 00000500) says a code is good for a single use and for
 * 48 hours. The client's errors (MSG_SENDCHATCODE.Error) are the KingsIsle
 * string hashes of ERROR_TooManyCodes, ERROR_FailedToCreateCode,
 * ERROR_NoSuchCode, ERROR_UnableToGenerateCode and ERROR_CreatorDoesNotExist
 * (WizardGraphicalClient r806919, WizardGUIManager::HandleChatCodeError), and a
 * successful use names a wizard already on the user's friend list
 * (HandleUseChatCodeSuccess refuses anyone else).
 * Guesses, no 2009 source: the code's form (10 letters and digits without
 * look-alikes) and the limit of open codes per wizard (5).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using System.Linq;
using System.Security.Cryptography;
using Imcodec.Cryptography;

namespace Imlight.CoreLib.Shared.Character;

/// <summary>
/// A True Friend code a wizard made.
/// </summary>
/// <param name="Code">The code, upper case.</param>
/// <param name="CreatorCharId">The character id of the wizard who made it.</param>
/// <param name="CreatorName">That wizard's name when the code was made.</param>
/// <param name="CreatedUnixSeconds">When it was made.</param>
public sealed record TrueFriendCode(string Code, ulong CreatorCharId, string CreatorName, long CreatedUnixSeconds) {

    /// <summary>
    /// True once the code is <see cref="TrueFriendCodes.Lifetime"/> old.
    /// </summary>
    public bool IsExpired(DateTimeOffset now)
        => now.ToUnixTimeSeconds() - CreatedUnixSeconds >= (long) TrueFriendCodes.Lifetime.TotalSeconds;

}

/// <summary>
/// Where True Friend codes are kept until used or expired.
/// </summary>
public interface ITrueFriendCodeStore {

    /// <summary>
    /// The code, whether or not it has expired; null when there is none.
    /// </summary>
    TrueFriendCode? Find(string code);

    /// <summary>
    /// How many codes the wizard made that have not expired or been used.
    /// </summary>
    int CountOpen(ulong creatorCharId, DateTimeOffset now);

    /// <summary>
    /// Keeps a new code. False when the code is already taken.
    /// </summary>
    bool Add(TrueFriendCode code);

    /// <summary>
    /// Forgets a code (used or expired).
    /// </summary>
    void Remove(string code);

}

/// <summary>
/// Why a code could not be made or used, as the client names it.
/// </summary>
public enum TrueFriendCodeError {
    None,
    TooManyCodes,
    FailedToCreateCode,
    NoSuchCode,
    UnableToGenerateCode,
    CreatorDoesNotExist,
}

/// <summary>
/// The rules of True Friend codes.
/// </summary>
public static class TrueFriendCodes {

    /// <summary>
    /// How long a code can be used (the client: "valid for 48 hours").
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);

    /// <summary>
    /// Open codes a wizard may have at once. A guess: the client has a "too many codes" message but no number.
    /// </summary>
    public const int MaxOpenCodesPerWizard = 5;

    /// <summary>
    /// Characters in a code. A guess.
    /// </summary>
    public const int CodeLength = 10;

    // Letters and digits without 0/O, 1/I/L and 5/S, which read alike when a code is passed on by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZ2346789";

    private const int CreateAttempts = 8;

    /// <summary>
    /// The value MSG_SENDCHATCODE.Error carries for <paramref name="error"/>: the string hash of its ERROR_ name, 0 for none.
    /// </summary>
    public static uint ErrorCode(TrueFriendCodeError error)
        => error == TrueFriendCodeError.None ? 0 : StringHash.Compute("ERROR_" + error);

    /// <summary>
    /// A new random code.
    /// </summary>
    public static string NewCode() {
        Span<char> code = stackalloc char[CodeLength];
        for (var i = 0; i < code.Length; i++) {
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(code);
    }

    /// <summary>
    /// A code as typed, in the stored form: upper case, without spaces or dashes.
    /// </summary>
    public static string Normalize(string? typed)
        => new((typed ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '\0').Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// Makes a code for a wizard.
    /// </summary>
    /// <returns>The code, or the error to report.</returns>
    public static (TrueFriendCode? Code, TrueFriendCodeError Error) Create(ITrueFriendCodeStore store, ulong creatorCharId,
                                                                          string creatorName, DateTimeOffset now,
                                                                          Func<string>? newCode = null) {
        int open;
        try {
            open = store.CountOpen(creatorCharId, now);
        }
        catch (Exception) {
            return (null, TrueFriendCodeError.FailedToCreateCode);
        }

        if (open >= MaxOpenCodesPerWizard) {
            return (null, TrueFriendCodeError.TooManyCodes);
        }

        for (var attempt = 0; attempt < CreateAttempts; attempt++) {
            var code = new TrueFriendCode((newCode ?? NewCode)(), creatorCharId, creatorName, now.ToUnixTimeSeconds());
            try {
                if (store.Add(code)) {
                    return (code, TrueFriendCodeError.None);
                }
            }
            catch (Exception) {
                return (null, TrueFriendCodeError.FailedToCreateCode);
            }
        }

        return (null, TrueFriendCodeError.UnableToGenerateCode);
    }

    /// <summary>
    /// Uses a code a wizard entered. A used or expired code is forgotten.
    /// </summary>
    /// <param name="store">The codes.</param>
    /// <param name="userCharId">The character id of the wizard entering the code.</param>
    /// <param name="typed">The code as entered.</param>
    /// <param name="now">The time.</param>
    /// <param name="creatorExists">True when a character id still belongs to a character.</param>
    /// <param name="areFriends">True when the user has the character on its friend list.</param>
    /// <returns>The code, or the error to report.</returns>
    public static (TrueFriendCode? Code, TrueFriendCodeError Error) Use(ITrueFriendCodeStore store, ulong userCharId, string? typed,
                                                                       DateTimeOffset now, Func<ulong, bool> creatorExists,
                                                                       Func<ulong, bool> areFriends) {
        var normalized = Normalize(typed);
        if (normalized.Length == 0 || store.Find(normalized) is not { } code) {
            return (null, TrueFriendCodeError.NoSuchCode);
        }

        if (code.IsExpired(now)) {
            store.Remove(normalized);

            return (null, TrueFriendCodeError.NoSuchCode);
        }

        // A wizard's own code is left for the friend it was made for.
        if (code.CreatorCharId == userCharId) {
            return (null, TrueFriendCodeError.NoSuchCode);
        }

        if (!creatorExists(code.CreatorCharId)) {
            store.Remove(normalized);

            return (null, TrueFriendCodeError.CreatorDoesNotExist);
        }

        // The client accepts a True Friend only from its friend list; a code from someone else is kept for them.
        if (!areFriends(code.CreatorCharId)) {
            return (null, TrueFriendCodeError.NoSuchCode);
        }

        store.Remove(normalized);

        return (code, TrueFriendCodeError.None);
    }

}
