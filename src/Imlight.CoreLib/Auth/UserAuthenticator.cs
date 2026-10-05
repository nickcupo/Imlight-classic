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
 * USER AUTHENTICATION
 * ========================================================================
 *
 * PURPOSE:
 * Provides an authentication mechnism for user login. Includes
 * checks for account existence, bans, and credential verification.
 *
 * USAGE EXAMPLE:
 * AuthenticationDetails authResult = UserAuthenticator.Authenticate(sessionActor, authMessage);
 * if (authResult._result == UserAuthenResult.Success) { ... }
 *
 * NOTE:
 * - 
 *
 * TODO:
 * - Last login time, machine ID, and IP should be saved to the database.
 *
 * Created by: Jooty
 * Version: KALI 1.0
 * Date: 3/19/2025
 */

using System;
using System.Security.Cryptography;
using System.Text;
using Imcodec.IO;
using Imlight.Common;
using Imlight.CoreLib.Shared.Cryptography;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using static Imcodec.MessageLayer.Generated.LOGIN_7_PROTOCOL;

namespace Imlight.CoreLib.Auth;

internal enum UserAuthenResult {

    Success = 0,
    AccountBanned = 0x538FBC0,
    MachineBanned = 0x44FB7BF8,
    AuthenFailed = 0x3B689180,
    AISNoLogin = 0x6311BDD6,
    Timeout = 0x512C42FF,
    FtpCapped = 0x5BFF7366,
    ErrorNoLock = 0x67DD13EA,
    FailedUpload = 0x10857D75

}

/// <summary>
/// Manages user authentication during the login process.
/// </summary>
/// <remarks>
/// Differentiates from <see cref="UserValidator"/> by handling initial login authentication.
/// Performs checks for account existence, bans, and credential verification.
/// </remarks>
internal static class UserAuthenticator {

    private static readonly bool s_enforceRevision 
        = ConfigurationManager.Settings["Global Settings.EnforceRevision"].AsBool();
    private static readonly string s_serverRevision 
        = ConfigurationManager.Settings["Global Settings.GameRevision"].AsString();
    // CLASSIC: private two-player server. When true, any password (even an empty one) logs
    // in to an existing account; only the username has to match.
    private static readonly bool s_anyPasswordLogin
        = ConfigurationManager.Settings["Classic.AnyPasswordLogin"].AsBool(false);

    internal class AuthenticationDetails {
        
        internal Account _account;
        internal ByteString _sessionKey;
        internal ByteString _rec1;
        internal UserAuthenResult _result;
        internal bool _closeSession; // CLASSIC: a failed password ends the connection

    }

    /// <summary>
    /// Authenticates the user using the provided <see cref="SessionActor"/> and <see cref="MSG_USER_AUTHEN_V3"/> message.
    /// </summary>
    /// <param name="sessionActor">The session actor.</param>
    /// <param name="authMessage">The authentication message.</param>
    /// <returns>The authentication details.</returns>
    internal static AuthenticationDetails Authenticate(SessionActor sessionActor, MSG_USER_AUTHEN_V3 authMessage) {
        var sessionId  = sessionActor.SessionID;
        var offerTime  = sessionActor.OfferTime;
        var offerMilli = sessionActor.OfferMillisecondsIntoSecond;
        var (returnedSessionid, username, clientKey1) = DecodeRec1(authMessage.Rec1, sessionActor);
        var details = new AuthenticationDetails();

        // Check if the session id matches.
        if (returnedSessionid != sessionId) {
            details._result = UserAuthenResult.AuthenFailed;

            return details;
        }

        // If the revision is enforced, check if the client revision matches the server revision.
        if (s_enforceRevision) {
            var clientRevision = authMessage.Revision;
            if (clientRevision != s_serverRevision) {
                Logger.Warning("SessionActor {0} was rejected due to revision mismatch. (Given: {1}, Expected: {2})",
                    Logger.Args(sessionActor.SessionID, clientRevision, s_serverRevision));

                details._result = UserAuthenResult.ErrorNoLock;

                return details;
            }
        }

        // CLASSIC: brute-force protection (H2): a locked-out account or address gets the plain failure.
        var throttle = SecuritySettings.Logins.Value;
        var address = sessionActor.RemoteIp;
        if (throttle.LockedFor(username, address) is { } wait) {
            Logger.Warning("Login for {0} from {1} refused: locked out for {2:F0} s after failed logins",
                Logger.Args(username, address, wait.TotalSeconds));
            details._result = UserAuthenResult.AuthenFailed;
            details._closeSession = true;

            return details;
        }

        // Check if we can find the account.
        var matchedAccount = AccountCollection.GetAccount(username);

        // CLASSIC: the password is checked before anything about the account is revealed (L2), against H (the only
        // thing the client's ClientKey1 can be checked against; see Imlight.Classic.Net.PasswordHashing), in constant
        // time. An account without H (in-client password login off) never matches.
        var protocolHash = matchedAccount is null ? null : PasswordStore.ProtocolHashOf(matchedAccount);
        var doesPasswordMatch = matchedAccount is not null
            && (s_anyPasswordLogin || ClientKey1Matches(protocolHash, sessionId, offerTime, offerMilli, clientKey1));
        if (!doesPasswordMatch) {
            var lockedOut = throttle.Failure(matchedAccount is null ? null : username, address);
            Logger.Warning("Login failed for {0} from {1}{2}",
                Logger.Args(username, address, lockedOut ? " (now locked out)" : ""));
            details._result = UserAuthenResult.AuthenFailed;
            details._closeSession = true;

            return details;
        }

        // Check to see if this machine is banned.
        if (InfractionCollection.IsMachineBanned(authMessage.MachineID)) {
            // Add an infraction to the account.
            matchedAccount.AddInfraction(InfractionType.Warn, "Logged in with banned machine ID.", null);

            details._result = UserAuthenResult.MachineBanned;

            return details;
        }

        // Check to see if the IP is banned.
        if (InfractionCollection.IsIpBanned(sessionActor.Ip)) {
            // Add an infraction to the account.
            matchedAccount.AddInfraction(InfractionType.Warn, "Logged in with banned IP.", null);
            details._result = UserAuthenResult.MachineBanned;

            return details;
        }

        // Check to see if this account is currently banned.
        if (matchedAccount.InfractionHistory.IsCurrentlyBanned || matchedAccount.IsLocked) {
            details._result = UserAuthenResult.AccountBanned;

            return details;
        }

        details._account = matchedAccount;
        throttle.Success(username);
        if (!s_anyPasswordLogin) {
            PasswordStore.AfterProtocolLogin(matchedAccount);
        }

        // Create a new session key and store it in the database.
        // CLASSIC: 32 random bytes (Base64, the same 44-character form as before); the old key had 31 random bits.
        ByteString sessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ClientKeyCollection.AddSessionKey(matchedAccount.AccountId, authMessage.MachineID, sessionKey, address);
        details._sessionKey = sessionKey;

        // todo: these are only ever cached and not ever persistently saved
        matchedAccount.LastLoginMachineId = authMessage.MachineID;
        matchedAccount.LastLoginTime = DateTime.UtcNow;
        matchedAccount.LastLoginIp = sessionActor.Ip;

        // Craft a successful reply and return.
        var rec1 = Rec1.Encode(sessionKey, sessionId, offerTime, offerMilli);
        details._rec1 = rec1;

        return details;
    }

    /// <summary>CLASSIC: ClientKey1 = Base64(SHA-512(H || salt)), compared in constant time.</summary>
    internal static bool ClientKey1Matches(string protocolHash, ushort sessionId, uint offerTime, uint offerMilli,
                                           string clientKey1) {
        if (protocolHash is null || string.IsNullOrEmpty(clientKey1)) {
            return false;
        }

        var expected = ClientKey.SaltedClientKey1(protocolHash, sessionId, offerTime, offerMilli);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(clientKey1));
    }

    private static (ushort, string, string) DecodeRec1(ByteString rec1, SessionActor sessionActor) {
        var decoded = Rec1.Decode(
            rec1, 
            sessionActor.SessionID, 
            sessionActor.OfferTime,
            sessionActor.OfferMillisecondsIntoSecond
        );
        var split = decoded.ToString().Split(' ');

        // The split should be of 3 parts: session id, username, and client key.
        if (split.Length != 3) {
            var exceptionMessage = "Failed to decode rec1. " +
                                   $"Expected 3 parts, got {split.Length}";
            throw new Exception(exceptionMessage);
        }

        // Cast the session id to a ushort.
        if (!ushort.TryParse(split[0], out var sId)) {
            var exceptionMessage = "Failed to decode rec1. " +
                                   $"Expected ushort, got {split[0]}";
            throw new Exception(exceptionMessage);
        }

        return (sId, split[1], split[2]);
    }
    
}
