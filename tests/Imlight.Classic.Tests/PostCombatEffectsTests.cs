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
 * CLASSIC: the after-duel effects on a wizard (PostCombatEffects): one at a time, the removal sent before the new
 * effect, and the grace flag the creatures read follows them.
 */

using System;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.Cryptography;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PostCombatEffectsTests {

    [Fact]
    public void TheEffectIdsAreTheClientTemplateNames() {
        Assert.Equal(1618528611u, PostCombatEffects.StillId);
        Assert.Equal(StringHash.Compute("PostCombatEffect2"), PostCombatEffects.MovingId);
    }

    [Fact]
    public void SwappingEffectsRemovesTheStillOneBeforeAddingTheMovingOne() {
        var wizard = new Wizard { CharId = 0xA3B1E000000001F1 };
        var id = wizard.GameObjectID;
        var first = PostCombatEffects.Put(wizard, id, "PostCombatEffect", DateTime.UtcNow.AddSeconds(30));
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(Assert.Single(first));
        Assert.True(wizard.IsInCombatGrace);

        var swap = PostCombatEffects.Put(wizard, id, "PostCombatEffect2", DateTime.UtcNow.AddSeconds(6));
        Assert.Equal(2, swap.Count);
        var removed = Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(swap[0]);
        Assert.Equal(PostCombatEffects.StillId, removed.EffectNameID);
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(swap[1]);
        Assert.Equal(1, wizard.GameEffects.Count);
        Assert.True(wizard.IsInCombatGrace);

        var end = PostCombatEffects.Take(wizard, id);
        Assert.Equal(PostCombatEffects.MovingId, Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(Assert.Single(end)).EffectNameID);
        Assert.Equal(0, wizard.GameEffects.Count);
        Assert.False(wizard.IsInCombatGrace);
        Assert.Empty(PostCombatEffects.Take(wizard, id));
    }

}
