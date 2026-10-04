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

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
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

        var chests = SecondChanceChests.Instance;
        var chestGid = Entity.ActiveGameObject.m_globalID.Full;
        var instance = OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId)?.InstanceOwnerId ?? 0;
        var refusal = chests.Open(wizard.CharId, chestGid, chest, wizard.Zone, instance, rules);
        if (refusal == ChestRefusal.BossNotDefeated) {
            playerActor.Tell(ClassicChat.Notice($"Defeat {chest.Boss} first: the chest offers a second chance at {chest.Boss}'s rewards.", false));

            return;
        }

        Logger.Information("Second Chance: wizard {0} opens {1} ({2} uses left, next {3} Crowns).",
            Logger.Args(wizard.CharId, chest.Chest, chests.UsesLeft(wizard.CharId, chest, rules), chests.NextCost(wizard.CharId, chest, rules)));
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_CROWNS_BALANCE { Balance = wizard.Account?.Crowns ?? 0 });
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_PROMPT {
            Id = chestGid,
            Cost = chests.NextCost(wizard.CharId, chest, rules),
            Uses = chests.UsesLeft(wizard.CharId, chest, rules),
        });
    }

}
