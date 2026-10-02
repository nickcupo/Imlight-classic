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
 * COMBAT REJOIN TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Held seats for wizards who drop mid-fight: the seat, the wait when every
 * wizard has dropped, the rejoin, and the login routing to the instance.
 *
 * NOTE:
 * The live path (drop, wait, log back in, win) was run with the headless
 * client on the rig; see playbot-reports/features.md.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class CombatRejoinTests {

    [Fact]
    public void DroppedSeatHasNoActorAndRemembersTheWizard() {
        var duel = CombatRegressionTests.MakeDuel();
        var seat = Seat(duel, 4, player: true, charId: 77);
        seat.HoldSeat(DateTime.UtcNow);

        Assert.True(seat.Disconnected);
        Assert.Equal(77UL, seat.HeldCharacterId);
        Assert.Same(ActorRefs.Nobody, seat.ParticipantActor);
        Assert.True(seat.Occupied);
    }

    [Fact]
    public void DuelWaitsOnlyWhenEveryLivingWizardHasDropped() {
        var duel = CombatRegressionTests.MakeDuel();
        Seat(duel, 0, player: false, charId: 0);
        var first = Seat(duel, 4, player: true, charId: 1);
        var second = Seat(duel, 5, player: true, charId: 2);

        first.HoldSeat(DateTime.UtcNow);
        Assert.False(ShouldWait(duel));

        second.HoldSeat(DateTime.UtcNow);
        Assert.True(ShouldWait(duel));

        // A dead wizard who is still connected does not keep the fight going on its own.
        second.RejoinSeat(ActorRefs.Nobody, new CoreObject { m_templateID = 1 }, WizardWith(2, health: 0));
        Assert.True(ShouldWait(duel));
    }

    [Fact]
    public void RejoinRestoresTheSeatAndMovesOwnedMinions() {
        var duel = CombatRegressionTests.MakeDuel();
        var seat = Seat(duel, 4, player: true, charId: 9);
        var oldObject = seat.ParticipantObject;
        var minion = Seat(duel, 6, player: false, charId: 0);
        typeof(CombatDuelSubCircle).GetField("_minionOwnerObject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(minion, oldObject);

        seat.HoldSeat(DateTime.UtcNow);
        var newObject = new CoreObject { m_templateID = 1 };
        var wizard = WizardWith(9, health: 321);
        seat.RejoinSeat(ActorRefs.NoSender ?? ActorRefs.Nobody, newObject, wizard);

        Assert.False(seat.Disconnected);
        Assert.Equal(0UL, seat.HeldCharacterId);
        Assert.Same(newObject, seat.ParticipantObject);
        Assert.Same(wizard.GameStats, seat.ParticipantGameStats);
        Assert.Equal(321, seat.CombatParticipant.m_playerHealth);
        Assert.Same(newObject, typeof(CombatDuelSubCircle).GetField("_minionOwnerObject",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(minion));
    }

    [Fact]
    public void LoginGoesToTheInstanceHoldingTheSeatUntilItExpires() {
        var now = DateTime.UtcNow;
        ActiveDuels.Hold(new HeldSeat(501, "Krokotopia/KT_Krokosphinx/Interiors/KT_Arena_T5", 900, now.AddSeconds(60)));
        try {
            Assert.Equal(900UL, ActiveDuels.InstanceOwnerForLogin(501, "Krokotopia/KT_Krokosphinx/Interiors/KT_Arena_T5", now));
            Assert.Equal(501UL, ActiveDuels.InstanceOwnerForLogin(501, "WizardCity/WC_Hub", now));
            Assert.Equal(501UL, ActiveDuels.InstanceOwnerForLogin(501, "Krokotopia/KT_Krokosphinx/Interiors/KT_Arena_T5",
                now.AddSeconds(61)));
        }
        finally {
            ActiveDuels.Release(501);
        }
    }

    private static bool ShouldWait(CombatDuelComponent duel)
        => (bool) CombatRegressionTests.Invoke(duel, "ShouldWaitForRejoin")!;

    private static CombatDuelSubCircle Seat(CombatDuelComponent duel, int slot, bool player, ulong charId) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var wizard = WizardWith(charId, health: 500);
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", wizard.GameStats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        if (player) {
            circle._wizard = wizard;
        }

        circle.AddedToDuel = true;

        return circle;
    }

    private static Wizard WizardWith(ulong charId, int health) {
        var wizard = (Wizard) RuntimeHelpers.GetUninitializedObject(typeof(Wizard));
        typeof(Wizard).GetField("<CharId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wizard, charId);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = 1000;
        stats.m_currentHitpoints = health;
        wizard.GameStats = stats;

        return wizard;
    }

}
