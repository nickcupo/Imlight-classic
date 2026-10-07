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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// Restores the wizard's health to full (the result type carries no amount).
/// </summary>
internal sealed class ResAddHealthHandler : BaseResultHandler<ResAddHealth> {

    private const float QUERY_WIZARD_TIMEOUT_SECONDS = 5.0f;

    public override bool Execute(IResultContext context) {
        // Context does not ship with a wizard reference, so we need to query for it.
        var queryWizardMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
        var queryTimeout = TimeSpan.FromSeconds(QUERY_WIZARD_TIMEOUT_SECONDS);
        var queryResponse = PlayerQuery.Character(context.GetPlayerRef(), queryTimeout) /* CLASSIC: pushed wizard, else Ask */;
        if (queryResponse?.Wizard is not Wizard wizard) {
            Logger.Error("Handler failed to retrieve character data within {0} seconds.",
                Logger.Args(QUERY_WIZARD_TIMEOUT_SECONDS));

            return false;
        }

        if (WizardResourceTransactions.IsActive) {
            if (!ResourceRewardBinding.TryAccount(wizard, context, out var accountId)) return false;
            return RefillAcknowledged(wizard, context.GetPlayerRef(), accountId);
        }

        var full = wizard.GameStats.m_baseHitpoints;
        var clientMax = wizard.GameStats.GetClientTypeAlternative().m_baseHitpoints;
        wizard.UpdateHealth(full);
        context.GetPlayerRef().Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = wizard.GameObjectID,
            NewHealth = full,
            NewHealthMax = clientMax,
        });

        return true;
    }

    internal static bool RefillAcknowledged(Wizard wizard, IActorRef player, ulong? expectedAccountId = null) {
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { player.Tell("Close"); return false; }
        var status = WizardResourceTransactions.TryRefillHealth(wizard, out _,
            afterCommit: receipt => player.Tell(receipt.Message), expectedAccountId: expectedAccountId);
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { player.Tell("Close"); return false; }
        return status != ResourceMutationStatus.Refused;
    }

}
