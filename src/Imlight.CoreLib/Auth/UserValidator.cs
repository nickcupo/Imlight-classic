using Imlight.Common;
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
 * USER VALIDATION
 * ========================================================================
 *
 * PURPOSE:
 * Provides an validation mechnism forclients already logged in through
 * authentication and attempting to start the game.
 *
 * USAGE EXAMPLE:
 * ValidationDetails validationResult = UserValidator.Validate(sessionActor, validateMessage);
 * if (validationResult._result == UserValidateResult.Success) { ... }
 *
 * NOTE:
 * - On DEBUG builds, the session key is always valid. This is to allow
 *   developers to test the game without having to worry about session keys.
 *
 * TODO:
 * - 
 *
 * Created by: Jooty
 * Version: KALI 1.0
 * Date: 3/19/2025
 */

using System;
using Imlight.CoreLib.Shared.Cryptography;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using static Imcodec.MessageLayer.Generated.LOGIN_7_PROTOCOL;

namespace Imlight.CoreLib.Auth;

internal enum UserValidateResult {

    Success = 0,
    AccountBanned = 87620544,
    MachineBanned = 1157331960,
    ValidateFailed = 246825817,
    Timeout = 1361855231,

}

/// <summary>
/// Manages user session validation for clients already logged in and starting the game.
/// </summary>
/// <remarks>
/// Differentiates from <see cref="UserAuthenticator"/> by handling post-login session validation.
/// </remarks>
internal static class UserValidator {

    internal class ValidationDetails {

        internal Account _account;
        internal string _sessionKey;
        internal UserValidateResult _result;
        internal bool _closeSession; // CLASSIC: a failed validate ends the connection

    }

    /// <summary>
    /// Validates the user. This happens after the game client has already authenticated,
    /// and is re-validating their session.
    /// </summary>
    /// <param name="sessionActor">The session actor.</param>
    /// <param name="validateMessage">The validation message.</param>
    /// <returns>The validation details.</returns>
    internal static ValidationDetails Validate(SessionActor sessionActor, MSG_USER_VALIDATE validateMessage) {
        var details = new ValidationDetails();

        // CLASSIC: failed validates count against the address (the key is 256 bits, so this only stops floods).
        var throttle = SecuritySettings.Logins.Value;
        var address = sessionActor.RemoteIp;
        if (throttle.LockedFor(null, address) is not null) {
            details._result = UserValidateResult.ValidateFailed;
            details._closeSession = true;

            return details;
        }

        // Try getting the account from the message's UserID.
        var matchedAccount = AccountCollection.GetAccount(validateMessage.UserID);
        if (matchedAccount is null) {
            throttle.Failure(null, address);
            details._result = UserValidateResult.ValidateFailed;
            details._closeSession = true;

            return details;
        }

        // Check to see if this account is banned.
        if (matchedAccount.InfractionHistory.IsCurrentlyBanned || matchedAccount.IsLocked) {
            details._result = UserValidateResult.AccountBanned;

            return details;
        }

        // Check to see if this machine is banned.
        if (InfractionCollection.IsMachineBanned(validateMessage.MachineID)) {
            // Add an infraction to the account.
            matchedAccount.AddInfraction(InfractionType.Warn, "Logged in with banned machine ID.", null);

            details._result = UserValidateResult.MachineBanned;

            return details;
        }

        // Check to see if this IP is banned.
        if (InfractionCollection.IsIpBanned(sessionActor.Ip)) {
            // Add an infraction to the account.
            matchedAccount.AddInfraction(InfractionType.Warn, "Logged in with banned IP.", null);

            details._result = UserValidateResult.MachineBanned;

            return details;
        }

        // Validation happens after authentication, so we need to check if the session key matches.
        var sessionKey = ClientKeyCollection.GetSessionKey(matchedAccount.AccountId, validateMessage.MachineID, address);
        if (string.IsNullOrEmpty(sessionKey)) {
            throttle.Failure(null, address);
            details._closeSession = true;
            // CLASSIC: say why, so a client started with a ticket from another program (-U ..USERID KEY) can be diagnosed.
            Logger.Debug("Validate: no session key for account {0} on machine {1}",
                Logger.Args(matchedAccount.AccountId.ToString(), validateMessage.MachineID.ToString()));
            details._result = UserValidateResult.ValidateFailed;

            return details;
        }

        // Finally, see if the session key matches.
        var passKey = validateMessage.PassKey3;
        var sessionId = sessionActor.SessionID;
        var offerTime = sessionActor.OfferTime;
        var offerMilli = sessionActor.OfferMillisecondsIntoSecond;
        var doesSessionMatch = PassKey3.VerifyPK3(sessionKey, sessionId, offerTime, offerMilli, passKey);

        // Developers get a free pass.
#if DEBUG
        //doesSessionMatch = true;
#endif

        if (!doesSessionMatch) {
            throttle.Failure(null, address);
            Logger.Warning("Validate failed for account {0} from {1}: wrong PassKey3",
                Logger.Args(matchedAccount.AccountId, address));
            details._result = UserValidateResult.ValidateFailed;
            details._closeSession = true;

            return details;
        }

        // If we've made it this far, the user is valid.
        ClientKeyCollection.Touch(matchedAccount.AccountId); // CLASSIC: the key stays valid another idle window
        matchedAccount.LastLoginMachineId = validateMessage.MachineID;
        matchedAccount.LastLoginTime = DateTime.UtcNow;
        matchedAccount.LastLoginIp = sessionActor.Ip;
        // CLASSIC (go-live): a key from a launcher that uses the public host marks this address for the public
        // game address (a LAN machine coming back through the router's hairpin NAT).
        Imlight.CoreLib.Classic.PublicGameAddress.LoginValidated(matchedAccount.AccountId, sessionKey, address);

        details._account = matchedAccount;
        details._sessionKey = sessionKey;
        details._result = UserValidateResult.Success;

        return details;
    }
    
}
