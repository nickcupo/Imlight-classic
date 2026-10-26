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
 * PROTOCOL FIX TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Client-protocol audit 2026-10: menu chat said aloud (extended form), the quest dialog acknowledgements, and a
 * whisper by typed name each have a handler, and the pieces that decide the answer behave.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Security;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Misc;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ProtocolFixTests {

    private static bool Handles(System.Type service, System.Type message)
        => service.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(m => m.GetCustomAttributes<MessageHandlerAttribute>().Any(a => a.MessageType == message));

    [Fact]
    public void MenuChatExtendedFormHasAHandler()
        => Assert.True(Handles(typeof(ChatService), typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHATEXT)));

    [Fact]
    public void WhisperByNameHasAHandler()
        => Assert.True(Handles(typeof(ChatService), typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHATBYNAME)));

    [Fact]
    public void QuestDialogAcknowledgementsHaveHandlers() {
        Assert.True(Handles(typeof(QuestService), typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_DECLINEQUEST)));
        Assert.True(Handles(typeof(QuestService), typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEPERSONA)));
        Assert.True(Handles(typeof(QuestService), typeof(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG)));
        Assert.True(Handles(typeof(QuestService), typeof(WIZARD_12_PROTOCOL.MSG_INTERACTUNDERWAYQUEST)));
        // CLASSIC: r806919's chained options (AvailableCompleteEntry / AvailableQuestEntry) send these; InteractService
        // routes them to the NPC like the option's click (DialogChain).
        Assert.True(Handles(typeof(InteractService), typeof(WIZARD_12_PROTOCOL.MSG_INTERACTCOMPLETEGOAL)));
        Assert.False(Handles(typeof(QuestService), typeof(WIZARD_12_PROTOCOL.MSG_INTERACTCOMPLETEGOAL)));
        Assert.True(Handles(typeof(InteractService), typeof(WIZARD_12_PROTOCOL.MSG_INTERACTAVAILABLEQUEST)));
    }

    [Fact]
    public void ExtendedMenuChatIsLengthChecked() {
        Assert.True(ChatGuard.AcceptsQuickChatExt([1, 2, 3]));
        Assert.False(ChatGuard.AcceptsQuickChatExt([]));
        Assert.False(ChatGuard.AcceptsQuickChatExt(new byte[ChatGuard.MaxLength * 2 + 1]));
    }

    [Fact]
    public void DecliningAnOfferClearsItSoItIsNotAcceptableAndCanBeOfferedAgain() {
        var offers = new BoundedOffers<QuestTemplate>();
        var quest = new QuestTemplate { m_questName = "WC-Quest" };
        offers.Add("WC-Quest", quest);

        Assert.True(QuestService.ClearDeclinedOffer(offers, "WC-Quest"));
        Assert.Null(offers.Find("WC-Quest"));
        Assert.False(QuestService.ClearDeclinedOffer(offers, "WC-Quest")); // twice: harmless

        offers.Add("WC-Quest", quest); // the next talk offers it again
        Assert.Same(quest, offers.Find("WC-Quest"));
    }

    [Fact]
    public void DecliningLeavesOtherOffersAndUnknownNamesAlone() {
        var offers = new BoundedOffers<QuestTemplate>();
        offers.Add("A", new QuestTemplate { m_questName = "A" });

        Assert.False(QuestService.ClearDeclinedOffer(offers, "B"));
        Assert.False(QuestService.ClearDeclinedOffer(offers, null));
        Assert.False(QuestService.ClearDeclinedOffer(offers, ""));
        Assert.False(QuestService.ClearDeclinedOffer(offers, new string('x', 5000)));
        Assert.NotNull(offers.Find("A"));
    }

    private static OnlinePlayer P(ulong id) => new() { CharacterId = id, ActorPath = $"/user/{id}" };

    private static readonly Dictionary<ulong, string> Names = new() {
        [1] = "Ethan Stormblade", [2] = "Ava Frostwind", [3] = "Ava Frostwind Jr",
    };

    private static string NameOf(ulong id) => Names.GetValueOrDefault(id);

    [Fact]
    public void TypedNameFindsTheOnlinePlayerIgnoringCaseAndSpacing() {
        var online = new[] { P(1), P(2), P(3) };

        Assert.Equal(2UL, ChatService.FindOnlinePlayerByName("ava frostwind", online, 1, NameOf)?.CharacterId);
        Assert.Equal(2UL, ChatService.FindOnlinePlayerByName("  AVA   Frostwind ", online, 1, NameOf)?.CharacterId);
        Assert.Equal(3UL, ChatService.FindOnlinePlayerByName("Ava Frostwind Jr", online, 1, NameOf)?.CharacterId);
    }

    [Fact]
    public void TypedNameOfNobodyOnlineOrOfTheSenderOrBlankFindsNoOne() {
        var online = new[] { P(1), P(2) };

        Assert.Null(ChatService.FindOnlinePlayerByName("Nobody Here", online, 1, NameOf));
        Assert.Null(ChatService.FindOnlinePlayerByName("Ethan Stormblade", online, 1, NameOf)); // yourself
        Assert.Null(ChatService.FindOnlinePlayerByName("Ava Frostwind", [P(1)], 1, NameOf)); // offline: not in the list
        Assert.Null(ChatService.FindOnlinePlayerByName("   ", online, 1, NameOf));
        Assert.Null(ChatService.FindOnlinePlayerByName(null, online, 1, NameOf));
        Assert.Null(ChatService.FindOnlinePlayerByName("Ava Frostwind", online, 1, _ => null));
    }

}
