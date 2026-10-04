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
 * INTERACT BANK COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the dorm's bank chest (WC_RVW-Bank, "the chest at the end of your bed", wiki Dormitory oldid 4155,
 * January 2009) offers the Bank service. Using it hands the wizard's session a MSG_BANKOPEN; the session's
 * BankService sends the shared bank and opens the client's BankingWindow (MSG_OPENBANK).
 *
 * NOTE:
 * Auto-registered by reflection like the other interact components. The dorm is the only 2009 place with a bank.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractBankComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    /// <summary>The dorm chest's object name (template 44162, ObjectData/WC/WC_RVW-Bank.xml).</summary>
    internal const string BANK_OBJECT_NAME = "WC_RVW-Bank";

    public string ServiceName     => "BankService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Banking;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_Banking";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_objectName is not null
        && gameObjectTemplate.m_objectName.ToString() == BANK_OBJECT_NAME;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new WizBankingOption {
                m_displayKey = DisplayKey,
                m_iconKey = NpcIcon,
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        // The session opens the window once the shared bank is loaded; the wizard stands still while it is open
        // (the client sends MSG_DONESHOPPING when it closes, which ShopService answers).
        playerActor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_BANKOPEN {
            BankObjectId = Entity.ActiveGameObject.m_globalID,
            Zone = playerCharacter?.Zone,
        });
        Broadcast(new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = (uint) WizBang,
            GameObjectID = playerObject.m_globalID,
        });
        Broadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            State = StringHash.Compute(StateName),
            GameObjectID = playerObject.m_globalID,
        });
    }

    private void Broadcast(Imcodec.MessageLayer.IMessage message)
        => Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message = message, Selfless = false });

}
