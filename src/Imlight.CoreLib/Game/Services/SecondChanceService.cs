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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.SecondChance;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Services;

internal sealed class SecondChanceService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new SecondChanceService(parentActor));

    private static readonly ObjectSerializer s_lootSerializer = new(Behaviors: SerializerFlags.None);

    private ulong _charId;

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE))]
    private void ReceivePaidLootRollResponse(WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESPONSE message) {
        var wizard = GetActiveWizard();
        var chests = SecondChanceChests.Instance;
        if (wizard is null) {
            return;
        }

        _charId = wizard.CharId;
        if (message.Response == 0 || ClassicProgression.SecondChance is not { } rules || ClassicProgression.MobRewards is not { } mobRewards) {
            chests.Close(wizard.CharId);

            return;
        }

        var instance = OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId)?.InstanceOwnerId ?? 0;
        var refusal = chests.TryUse(wizard.CharId, message.Id, wizard.Zone, instance, rules,
            cost => ClassicCrowns.TrySpend(wizard.Account, cost), out var chest, out var cost);
        if (refusal != ChestRefusal.None) {
            Logger.Information("Second Chance: wizard {0} roll refused: {1}.", Logger.Args(wizard.CharId, refusal));
            if (refusal == ChestRefusal.NotEnoughCrowns) {
                InformGameClient($"You need {chests.NextCost(wizard.CharId, chest, rules)} Crowns for another chance.");
            }

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_ERROR());

            return;
        }

        // The boss's rewards, rolled as a won duel rolls them (no XP), granted without the duel's loot popup.
        var boss = chests.BossFor(wizard.CharId, chest);
        var result = new DropTableResult { DropTableId = "second_chance:" + chest.Chest };
        CombatService.AddClassicMobLoot(mobRewards, boss, result, Random.Shared);
        LootGranter.Grant(SessionActor.ActorRef, wizard, result, showPopup: false);

        var loot = DropTableConverter.ToLootInfoList(result);
        if (!s_lootSerializer.Serialize(loot, 5, out var lootData)) {
            Logger.Error("Second Chance: the loot of {0} did not serialize.", Logger.Args(chest.Chest));
            lootData = new Imcodec.IO.ByteString(System.Array.Empty<byte>());
        }

        var balance = wizard.Account?.Crowns ?? 0;
        Logger.Information("Second Chance: wizard {0} paid {1} Crowns at {2} (boss {3}): {4} gold, {5} items, {6} cards, {7} reagents; {8} Crowns left.",
            Logger.Args(wizard.CharId, cost, chest.Chest, boss, result.GoldAmount, result.Items.Count, result.TreasureCards.Count, result.Reagents.Count, balance));
        SendToSocket(ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT {
            Id = message.Id,
            Cost = chests.NextCost(wizard.CharId, chest, rules),
            Balance = balance,
            Uses = chests.UsesLeft(wizard.CharId, chest, rules),
            Loot = lootData,
        });
    }

    protected override void OnPreDispose() {
        if (_charId != 0) {
            SecondChanceChests.Instance.Forget(_charId);
        }

        base.OnPreDispose();
    }

}
