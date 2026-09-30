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
 * CLASSIC QUEST ENGINE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * A zone whose client trigger list holds an entry the codec could not decode
 * (a null, as in Krokotopia/KT_Pyramid/KT_Chamber) still loads its triggers.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/29/2026
 */

using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ZoneTriggerLoadTests {

    [Fact]
    public void UndecodedTriggersAreDroppedAndTheRestKept() {
        var kept = new Trigger { m_triggerName = "Teleport location 2" };
        var triggers = new List<Trigger> { kept, null!, new Trigger { m_triggerName = "ToTower2FromPyramid3" } };

        var dropped = ZoneTriggerSupervisor.DropUndecodedTriggers(triggers);

        Assert.Equal(1, dropped);
        Assert.Equal(2, triggers.Count);
        Assert.DoesNotContain(null, triggers);
        Assert.Same(kept, triggers[0]);
    }

    [Fact]
    public void AListWithoutNullsIsUnchanged() {
        var triggers = new List<Trigger> { new Trigger { m_triggerName = "A" } };

        Assert.Equal(0, ZoneTriggerSupervisor.DropUndecodedTriggers(triggers));
        Assert.Single(triggers);
    }

}
