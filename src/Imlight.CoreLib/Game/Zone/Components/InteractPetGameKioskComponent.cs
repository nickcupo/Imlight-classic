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
 * CLASSIC PET GAME KIOSK
 * ========================================================================
 *
 * PURPOSE:
 * The Pet Pavilion's game kiosks (PetGameDanceKiosk, PetGameDropKiosk,
 * PetGameCannonKiosk, PetGameMazeKiosk): interacting opens the client's
 * pet game window for that game.
 *
 * USAGE EXAMPLE:
 * Attached by ShouldAttachToEntity; the player picks the kiosk option and
 * gets MSG_PETGAMEKIOSK(GlobalID, GameInfo = the game's PetGameInfo, GamesWon).
 *
 * NOTE:
 * The official client deserializes GameInfo as a PetGameInfo (flags 5) and
 * fires OpenPetGameKiosk; its window then sends MSG_PETGAMEJOIN(Game, Track
 * as "%d"), which PetGameService handles. Only with the pet systems on.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractPetGameKioskComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "PetGameKioskService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_Kiosk";

    private static readonly ObjectSerializer s_serializer = new(Behaviors: SerializerFlags.None);

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && PetGameConfigs.KioskGames.ContainsKey(gameObjectTemplate.m_objectName.ToString());

    private string Game => CoreObjectFactory.GetCoreTemplate(Entity.ActiveGameObject.m_templateID) is GameObjectTemplate t
        ? PetGameConfigs.KioskGames.GetValueOrDefault(t.m_objectName.ToString()) : null;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.PetsLeveling)
            ? [new PetGameKioskOption { m_displayKey = DisplayKey, m_iconKey = "Kiosk", m_serviceName = ServiceName, m_forceInteract = true }]
            : [];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!PetGameConfigs.TryGet(Game, out var info) || !s_serializer.Serialize(info, (PropertyFlags) 5, out var gameInfo)) {
            Logger.Warning("Pet game kiosk {0}: no 2010 game for it.", Logger.Args(Entity.ActiveGameObject.m_templateID.Full));

            return;
        }

        byte[] bytes = gameInfo;
        Logger.Debug("Pet game kiosk {0}: {1} ({2} tracks, {3} bytes).",
            Logger.Args(Entity.ActiveGameObject.m_templateID.Full, info.m_name.ToString(), info.m_trackChoices?.Count ?? 0, bytes?.Length ?? 0));
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_PETGAMEKIOSK {
            GlobalID = Entity.ActiveGameObject.m_globalID,
            GameInfo = gameInfo,
            GamesWon = 0,
        });
    }

}
