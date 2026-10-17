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
using Imcodec.ObjectProperty.TypeCache;
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

    // CLASSIC: "my speed is inconsistent" after battles (owner, mounted, 2026-10-17). r806919 removes an effect by
    // internal id alone (RemoveExistingEffect 0x141736a00), and the mount's SpeedBuff stacks (STACKING_ALLOWED). Gear
    // 1..11 and a mount at 12: the duel stow removes 12, the restore takes 13, and PostCombatEffect used Count + 1 = 13,
    // so the fade's removal took the mount speed off and the next stow's removal of 13 found nothing.
    [Fact]
    public void TheFadeAfterADuelNeverReusesTheRestoredMountSpeedId() {
        var wizard = new Wizard { CharId = 0xA3B1E000000001F2 };
        var next = () => Imlight.CoreLib.Classic.Elixirs.ElixirRuntime.NextEffectId(wizard);
        wizard.GameEffects.Add(new WizStatisticEffect { m_internalID = next() }); // 0: the sequence starts at max + 1
        for (var i = 1; i <= 11; i++) wizard.GameEffects.Add(new WizStatisticEffect { m_internalID = next() });
        wizard.GameEffects.Remove(wizard.GameEffects.Snapshot()[0]);
        var mount = new SpeedEffect { m_internalID = next(), m_speedMultiplier = 40 };
        Assert.Equal(12, mount.m_internalID);
        wizard.GameEffects.Add(mount);

        wizard.GameEffects.Remove(mount); // stowed for the duel
        var restored = new SpeedEffect { m_internalID = next(),
            m_speedMultiplier = 40 };
        wizard.GameEffects.Add(restored); // worn again after it
        Assert.Equal(13, restored.m_internalID);

        var add = PostCombatEffects.Put(wizard, wizard.GameObjectID, "PostCombatEffect", DateTime.UtcNow.AddSeconds(30));
        var swap = PostCombatEffects.Put(wizard, wizard.GameObjectID, "PostCombatEffect2", DateTime.UtcNow.AddSeconds(6));
        var end = PostCombatEffects.Take(wizard, wizard.GameObjectID);

        var removedIds = swap.Concat(end).OfType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>().Select(m => m.InternalID).ToList();
        Assert.Equal(2, removedIds.Count);
        Assert.DoesNotContain(restored.m_internalID, removedIds);
        var ids = wizard.GameEffects.Snapshot().Select(e => e.m_internalID).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(restored, wizard.GameEffects.Snapshot());
        Assert.Single(add);
    }

}
