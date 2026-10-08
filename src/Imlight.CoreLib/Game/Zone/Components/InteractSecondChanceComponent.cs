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
 * INTERACT SECOND CHANCE CHEST
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: makes a boss room's Second Chance chest (October 2009; the r806919 client's <World>_MonsterChest_<Boss>
 * templates, listed in classic-data/rules/second-chance-2009.yaml) usable. Clicking it opens the client's
 * PaidLootRollStation window with MSG_PAID_LOOT_ROLL_PROMPT {Id = the chest, Cost, Uses = uses left today}; the
 * answer, MSG_PAID_LOOT_ROLL_RESPONSE, goes to SecondChanceService.
 *
 * NOTE:
 * The chest offers PaidLootRollStationOption, the service its template's InteractableBehavior names
 * (PaidLootRollService, GUI_ChestInteract, GUI/QuestButtons/Use_Monster_Chest.dds).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.SecondChance;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractSecondChanceComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "PaidLootRollService";
    public string NpcIcon         => "GUI/QuestButtons/Use_Monster_Chest.dds";
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_ChestInteract";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => Chest(template) is not null;

    private static SecondChanceChest Chest(CoreTemplate template)
        => ClassicProgression.SecondChance is { } rules && template is GameObjectTemplate t
            ? rules.ChestByTemplate(t.m_templateID) ?? rules.ChestByName(t.m_objectName?.ToString())
            : null;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new PaidLootRollStationOption {
                m_displayKey = DisplayKey,
                m_iconKey = NpcIcon,
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard wizard, CoreObject playerObject, uint serviceOptionIndex) {
        if (ClassicProgression.SecondChance is not { } rules || wizard is null
            || Chest(CoreObjectFactory.GetCoreTemplate(Entity.ActiveGameObject.m_templateID)) is not { } chest) {
            return;
        }

        var chestGid = Entity.ActiveGameObject.m_globalID.Full;
        var instance = OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId)?.InstanceOwnerId ?? 0;
        var charId = wizard.CharId; var zone = wizard.Zone; var accountId = wizard.Account?.AccountId;
        OpenAcknowledged(wizard, chestGid, chest, zone, instance, rules, packet => playerActor.Tell(packet),
            () => playerActor.Tell("Close"), () => ActiveWizardDirectory.TryGet(playerActor, out var selected, out var world)
                && ReferenceEquals(selected, wizard) && ReferenceEquals(world, playerObject)
                && world is not null && world.m_globalID.Full == wizard.GameObjectID
                && string.Equals(Entity.Zone?.ZonePath, zone, StringComparison.OrdinalIgnoreCase)
                && wizard.CharId == charId && wizard.Account?.AccountId == accountId
                && string.Equals(wizard.Zone, zone, StringComparison.OrdinalIgnoreCase)
                && (OnlinePlayerCollection.GetOnlinePlayer(charId)?.InstanceOwnerId ?? 0) == instance, owner: playerActor);
    }

    // CLASSIC: the real interaction prepares a freshly priced prompt, never a fallible cached use count.
    internal static SecondChanceResult OpenAcknowledged(Wizard wizard, ulong chestId, SecondChanceChest chest,
        string zone, ulong instance, SecondChanceRules rules, Action<IMessage> send, Action close,
        Func<bool> isCurrent = null, SecondChanceChests state = null, IActorRef owner = null) {
        state ??= SecondChanceChests.Instance;
        try {
            var result = ClassicSecondChanceTransactions.TryOpen(wizard, chestId, chest, zone, instance, rules,
                packets => { foreach (var packet in packets) {
                    if (isCurrent?.Invoke() == false) break;
                    send(packet);
                } }, isCurrent, state, owner);
            Logger.Information("Second Chance: wizard {0}, chest {1}, open outcome {2}, refusal {3}.",
                Logger.Args(wizard?.CharId ?? 0, chestId, result.Status, result.Refusal));
            if (isCurrent?.Invoke() == false && !WizardCollection.IsInventorySnapshotUncertain(wizard)) {
                if (wizard is not null) state.CloseOwned(wizard.CharId, owner);
                return result with { Status = SecondChanceStatus.ContextLost };
            }
            if (result.Status == SecondChanceStatus.PreparationFailed) { if (wizard is not null) state.CloseOwned(wizard.CharId, owner); close(); }
            else if (result.Refusal == ChestRefusal.BossNotDefeated) send(ClassicChat.Notice(
                $"Defeat {chest.Boss} first: the chest offers a second chance at {chest.Boss}'s rewards.", false));
            return result;
        }
        catch (Exception error) {
            Logger.Warning("Second Chance: wizard {0}, chest {1}, open failed ({2}).",
                Logger.Args(wizard?.CharId ?? 0, chestId, error.GetType().Name));
            if (wizard is not null) state.CloseOwned(wizard.CharId, owner);
            close(); return new(SecondChanceStatus.PreparationFailed);
        }
    }
}
