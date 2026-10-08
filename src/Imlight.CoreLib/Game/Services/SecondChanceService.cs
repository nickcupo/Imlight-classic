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
 * SECOND CHANCE SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the answer to a Second Chance chest's window (r806919 PaidLootRollStation). MSG_PAID_LOOT_ROLL_RESPONSE
 * {Id, Response 1 = "Spend $COST$ crowns", 0 = close}: the wizard pays the Crowns and the boss's rewards roll again
 * (the boss's 2009 drops, as a won duel rolls them; no XP). The client shows the rewards from
 * MSG_PAID_LOOT_ROLL_RESULT.Loot (a LootInfoList serialized as for a minigame's rewards) with the next cost, the
 * Crowns left and the uses left today.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.SecondChance;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed class SecondChanceService(SessionActor sessionActor) : MessageService(sessionActor) {
    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new SecondChanceService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE))]
    private void ReceivePaidLootRollResponse(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE message) {
        var wizard = GetActiveWizard();
        if (wizard is null) return;
        var instance = OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId)?.InstanceOwnerId ?? 0;
        var charId = wizard.CharId; var zone = wizard.Zone; var accountId = wizard.Account?.AccountId;
        // The MessageService cache is not proof of current selection. Use the existing completed
        // attachment snapshot, which also checks directory/world/owner/generation/travel/disposal.
        SessionActor.TryCapturePetGameAttach(wizard, out var attach);
        UseAcknowledged(wizard, message, zone, instance, ClassicProgression.SecondChance, ClassicProgression.MobRewards,
            SendToSocket, CloseSession, () => attach is not null && SessionActor.MatchesPetGameAttach(attach)
                && wizard.CharId == charId && wizard.Account?.AccountId == accountId
                && (OnlinePlayerCollection.GetOnlinePlayer(charId)?.InstanceOwnerId ?? 0) == instance, owner: SessionActor.ActorRef);

    }

    // CLASSIC: the actual response handler and caller fixtures share this complete failure/publication path.
    internal static SecondChanceResult UseAcknowledged(Wizard wizard, WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE message,
        string zone, ulong instance, SecondChanceRules rules, MobRewardRules rewards, Action<IMessage> send,
        Action close, Func<bool> isCurrent = null, SecondChanceChests state = null, IActorRef owner = null) {
        state ??= SecondChanceChests.Instance;
        if (wizard is null || message is null) return new(SecondChanceStatus.Refused);
        if (message.Response == 0 || rules is null || rewards is null) {
            state.CloseOwned(wizard.CharId, owner); return new(SecondChanceStatus.Refused);
        }
        try {
            var result = ClassicSecondChanceTransactions.TryUse(wizard, message.Id, zone, instance, rules, rewards,
                packets => { foreach (var packet in packets) {
                    if (isCurrent?.Invoke() == false) break;
                    send(packet);
                } }, isCurrent, state, owner);
            Logger.Information("Second Chance: wizard {0}, chest {1}, outcome {2}, refusal {3}.",
                Logger.Args(wizard.CharId, message.Id.Full, result.Status, result.Refusal));
            if (isCurrent?.Invoke() == false && !WizardCollection.IsInventorySnapshotUncertain(wizard)) {
                state.CloseOwned(wizard.CharId, owner);
                return result with { Status = SecondChanceStatus.ContextLost };
            }
            if (result.Status == SecondChanceStatus.PreparationFailed) {
                state.CloseOwned(wizard.CharId, owner); close();
            }
            else if (result.Status == SecondChanceStatus.Refused) {
                if (result.Refusal == ChestRefusal.NotEnoughCrowns && result.Quote is { } quote)
                    send(ClassicChat.Line($"You need {quote.Cost} Crowns for another chance."));
                send(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_ERROR());
            }
            return result;
        }
        catch (Exception error) {
            // No compensation/reroll: the save may have committed. The transaction already quarantined
            // every unknown save/publication outcome under its original lane; pre-save errors also close.
            Logger.Warning("Second Chance: wizard {0}, chest {1}, failed ({2}), uncertain {3}.",
                Logger.Args(wizard.CharId, message.Id.Full, error.GetType().Name, WizardCollection.IsInventorySnapshotUncertain(wizard)));
            state.CloseOwned(wizard.CharId, owner); close();
            return new(SecondChanceStatus.PreparationFailed);
        }
    }

    protected override void OnPreDispose() {
        SecondChanceChests.Instance.ForgetOwner(SessionActor.ActorRef);
        base.OnPreDispose();
    }
}
