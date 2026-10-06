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
 * ARENA GUARDS (PRACTICE AND RANKED)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the two figures inside the Wizard City Arena, "Ranked and
 * Practice that give you access to the respective types of PVP matches"
 * (wiki Arena oldid 6408, 2009-01-29). Interacting opens the client's PvP
 * window for that tournament (the session's ArenaService sends
 * MSG_PREPVPKIOSK; Classic/Arena/ArenaMessages).
 *
 * NOTE:
 * The templates are the client's WC-ST01-PVPPractice (100587) and
 * WC-ST01-PVPRanked (100586), PvPKioskBehavior with tournaments
 * "PvPPractice" and "PvPSanctioned"; which template is which guard comes
 * from classic-data/pvp/arena-*.yaml. The option is the client's
 * PvPLobbyOption (m_tournamentName), as the r806919 kiosks use.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractPvpKioskComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "PvPKioskService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_ObjectInteract";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate && ClassicArena.KioskKind(gameObjectTemplate.m_templateID) is not null;

    private ArenaKind? Kind => ClassicArena.KioskKind((uint) Entity.ActiveGameObject.m_templateID.Full);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => ClassicArena.Enabled && Kind is { } kind && ClassicArena.Config is { } config
            ? [new PvPLobbyOption {
                m_displayKey = DisplayKey,
                m_iconKey = "Kiosk",
                m_serviceName = ServiceName,
                // CLASSIC: the arena guards open their lobby only on X/interact (owner ruling, 2026-10-05).
                // The normal service range and interaction checks still apply; proximity only offers this option.
                m_forceInteract = false,
                m_tournamentName = kind == ArenaKind.Ranked ? config.RankedTournament : config.PracticeTournament,
            }]
            : [];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!ClassicArena.Enabled || Kind is not { } kind) {
            return;
        }

        playerActor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_ARENAKIOSK {
            Ranked = kind == ArenaKind.Ranked,
            KioskGid = Entity.ActiveGameObject.m_globalID,
        });
    }

}
