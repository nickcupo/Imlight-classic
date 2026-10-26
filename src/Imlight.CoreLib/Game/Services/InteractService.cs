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
 * INTERACT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player interactions with game world objects, NPCs, and services, 
 * handling complex interaction routing mechanisms.
 * 
 * USAGE EXAMPLE:
 * Internal service processing player interaction messages within 
 * the game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal class InteractService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new InteractService(parentActor));

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC))]
    private void ReceiveNpcInteract(QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC message) {
        var wizard = GetActiveWizard();

        // A player is closing their shop
        if (message.ServiceName == "") {
            CloseShop(wizard.GameObjectID);
            // CLASSIC: r806919 also sends an empty service name to ask an NPC for its CURRENT options: 500 ms after an
            // NPC dialog closes (FUN_14172ca20/ca40: it drops its cached options and sends Reinteract 2), on
            // CancelInteraction (Reinteract 1) and after the server's MSG_REINTERACT. It expects MSG_SENDNPCOPTIONS with
            // Reinteract echoed; with 2 and a quest offer or turn-in in the list it opens that dialog (a talk-back
            // goal's chain). Dropping it left the NPC with no or stale options until the wizard walked away and back.
            if (IsOptionsRequest(message.ServiceName, message.Reinteract, message.GlobalID)) {
                RequestNpcOptions(message.GlobalID, message.Reinteract);
            }
            return;
        }

        HandleInteraction(message, msg => msg.GlobalID, msg => msg.ServiceName, msg => msg.ServiceIndex);
    }

    // CLASSIC: r806919's AvailableCompleteEntry (0x1415962d0) sends this instead of MSG_INTERACTNPC: the talk-back of that
    // quest goal at that NPC (DialogChain). Routed like the option's click.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_INTERACTCOMPLETEGOAL))]
    private void ReceiveInteractCompleteGoal(WIZARD_12_PROTOCOL.MSG_INTERACTCOMPLETEGOAL message)
        => RouteChainedOption(message.MobileID, DialogChain.PersonaGoalService, message.QuestID, message.GoalID, null);

    // CLASSIC: r806919's AvailableQuestEntry (0x141596820) sends this instead of MSG_INTERACTNPC: that quest's offer at
    // that NPC (DialogChain). Routed like the option's click.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_INTERACTAVAILABLEQUEST))]
    private void ReceiveInteractAvailableQuest(WIZARD_12_PROTOCOL.MSG_INTERACTAVAILABLEQUEST message)
        => RouteChainedOption(message.MobileID, DialogChain.OfferService, 0, 0, message.QuestName.ToString());

    private void RouteChainedOption(ulong npcId, string serviceName, ulong questId, ulong goalId, string questName) {
        var wizard = GetActiveWizard();
        var npc = npcId == 0 ? null : GetZoneObject(npcId);
        if (wizard is null || npc?.GetComponentOfType<InteractServiceMementoComponent>() is not { } memento) {
            Logger.Debug("{0} chose a chained option of {1}; no such NPC with services here.", Logger.Args(wizard?.CharId, npcId));
            return;
        }

        memento.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEINTERACTION {
            GlobalID = npcId,
            ServiceName = serviceName,
            ServiceIndex = uint.MaxValue,
            PlayerActor = SessionActor.ActorRef,
            PlayerCharacter = wizard,
            PlayerObject = GetActiveGameObject(),
            ChainQuestId = questId,
            ChainGoalId = goalId,
            ChainQuestName = questName,
        });
    }

    /// <summary>CLASSIC: an MSG_INTERACTNPC that asks an NPC for its current options (empty service, Reinteract set).</summary>
    internal static bool IsOptionsRequest(string serviceName, int reinteract, ulong npcId)
        => string.IsNullOrEmpty(serviceName) && reinteract != 0 && npcId != 0;

    private void RequestNpcOptions(ulong npcId, int reinteract) {
        var wizard = GetActiveWizard();
        var npc = GetZoneObject(npcId);
        if (wizard is null || npc?.GetComponentOfType<InteractServiceMementoComponent>() is not { } memento) {
            Logger.Debug("{0} asked NPC {1} for its options; no such NPC with services here.", Logger.Args(wizard?.CharId, npcId));
            return;
        }

        memento.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEINTERACTION {
            GlobalID = npcId,
            ServiceName = "",
            ServiceIndex = 0,
            PlayerActor = SessionActor.ActorRef,
            PlayerCharacter = wizard,
            PlayerObject = GetActiveGameObject(),
            Reinteract = reinteract,
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_INTERACTOPTION))]
    private void ReceiveInteractOption(GAME_5_PROTOCOL.MSG_INTERACTOPTION message)
        => HandleInteraction(message, msg => msg.ObjectID, msg => "Interact", msg => (uint) msg.OptionIndex);

    private void HandleInteraction<T>(T message, Func<T, ulong> getObjectId, Func<T, string> getServiceName, Func<T, uint> getServiceIndex) {
        var wizard = GetActiveWizard();

        // Search for the interaction object.
        var npc = GetZoneObject(getObjectId(message));
        if (npc == null) {
            Logger.Error("{0} searched for NPC by global ID {1} but one was not found",
                Logger.Args(wizard.CharId, getObjectId(message)));

            return;
        }

        // Check if the interacted object contains a service memento component.
        var serviceMementoComponent = npc.GetComponentOfType<InteractServiceMementoComponent>();
        if (serviceMementoComponent == null) {
            Logger.Error("{0} interacted with NPC {1} but it does not contain a service memento component",
                Logger.Args(wizard.CharId, getObjectId(message)));

            return;
        }

        // CLASSIC: a shop, trainer, bank or Bazaar opens only for a wizard standing by it, and is remembered for the
        // requests that follow (some carry no NPC id).
        if (IsEconomyService(npc)) {
            if (!ServiceProximity.IsNear(wizard, npc)) {
                Logger.Warning("{0} asked for the service of {1} from out of range.",
                    Logger.Args(wizard.CharId, getObjectId(message)));

                return;
            }

            ServiceProximity.Record(wizard, getObjectId(message));
        }

        var actor = SessionActor.ActorRef;
        var gameObj = GetActiveGameObject();

        // Inform the service memento component that the player is interacting with it.
        var msg = new ZONE_102_PROTOCOL.MSG_ZONEINTERACTION {
            GlobalID = getObjectId(message),
            ServiceName = getServiceName(message),
            ServiceIndex = getServiceIndex(message),
            PlayerActor = actor,
            PlayerCharacter = wizard,
            PlayerObject = gameObj
        };
        serviceMementoComponent.ActorRef.Tell(msg);
    }

    // CLASSIC: the services that spend or move gold, items, training points or bank storage.
    private static bool IsEconomyService(Imlight.CoreLib.Game.Zone.Core.ZoneEntity npc)
        => npc.GetComponentOfType<InteractBankComponent>() is not null
        || npc.GetComponentOfType<InteractVendorComponent>() is not null
        || npc.GetComponentOfType<InteractTreasureVendorComponent>() is not null
        || npc.GetComponentOfType<InteractPotionShopComponent>() is not null
        || npc.GetComponentOfType<InteractAuctionHouseComponent>() is not null
        || npc.GetComponentOfType<InteractTrainerComponent>() is not null
        || npc.GetComponentOfType<InteractDyeShopComponent>() is not null
        || npc.GetComponentOfType<InteractSeamstressComponent>() is not null
        || npc.GetComponentOfType<InteractReagentComponent>() is not null;

    private void CloseShop(ulong gameObjectId) {
        var enableMovementStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE() {
            GameObjectID = gameObjectId,
            State = StringHash.Compute("Moving"),
            IgnoreIfCurrentStateIsOff = 1
        };
        ZoneBroadcast(enableMovementStateMsg, false);

        var clearWizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG() {
            GameObjectID = gameObjectId,
            WizBangID = (uint) WizBangs.None
        };
        ZoneBroadcast(clearWizBangMsg, false);
    }

}
