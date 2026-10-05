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
 * PLAYER OPTION INDEX TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Two wizards at one NPC keep their own option indexes: the final gate's group run found a friend's click on a hand-in doing nothing
 * because the NPC had rebuilt its shared index map for the other wizard (hand-in done, next quest offered).
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~PlayerOptionIndexTests
 *
 * NOTE:
 * Local actors only; no database or network server is started.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PlayerOptionIndexTests {

    private sealed class Silent : ReceiveActor;

    private sealed class FakeComponent(string name) : IServiceComponent {

        public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) => [];
        public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) { }
        public string ServiceName { get; } = name;
        public string NpcIcon => "";
        public string NpcNameKey => "";
        public string NpcTextKey => "";
        public WizBangs WizBang => WizBangs.None;
        public string StateName => "";
        public string InteractWizBang => "";
        public string DisplayKey => "";

    }

    [Fact]
    public void ClickGoesToTheComponentOfTheListThatWizardWasSent() {
        using var system = ActorSystem.Create("option-index", "akka.actor.provider = local");
        var a = system.ActorOf(Props.Create<Silent>(), "a");
        var b = system.ActorOf(Props.Create<Silent>(), "b");
        var persona = new FakeComponent("QuestPersonaGoalService");
        var offer = new FakeComponent("QuestOfferService");
        var index = new PlayerOptionIndex();
        // B was sent [persona]; then the NPC rebuilt its list for A (A handed in, so [offer]) and the shared map now says 0 -> offer.
        index.Set(b, new Dictionary<int, IServiceComponent> { [0] = persona });
        index.Set(a, new Dictionary<int, IServiceComponent> { [0] = offer });

        Assert.True(index.TryResolve(b, 0, out var forB));
        Assert.Same(persona, forB);
        Assert.True(index.TryResolve(a, 0, out var forA));
        Assert.Same(offer, forA);
    }

    [Fact]
    public void AWizardSentNothingOrGoneResolvesNothing() {
        // CLASSIC (security audit 2026-10-04): no fallback to the NPC's last-built list; a wizard who was never sent
        // options (or left the range) cannot click through another wizard's list.
        using var system = ActorSystem.Create("option-index-2", "akka.actor.provider = local");
        var a = system.ActorOf(Props.Create<Silent>(), "a");
        var persona = new FakeComponent("QuestPersonaGoalService");
        var index = new PlayerOptionIndex();

        Assert.False(index.TryResolve(a, 0, out _));
        index.Set(a, new Dictionary<int, IServiceComponent> { [0] = persona });
        Assert.True(index.TryResolve(a, 0, out var c));
        Assert.Same(persona, c);
        index.Set(a, new Dictionary<int, IServiceComponent>());
        Assert.False(index.TryResolve(a, 0, out _));
        index.Remove(a);
        Assert.False(index.TryResolve(a, 0, out _));
    }

}
