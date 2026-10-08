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
 * CLASSIC: after a won duel the client is told the wizard's health (CombatService.HealthAfterDuel).
 */

using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DuelEndHealthTests {

    [Fact]
    public void AWonDuelReportsTheHealthLeftWithTheTableMaximum() {
        var old = WizardProgressionTransactions.TestScope.Value;
        WizardProgressionTransactions.TestScope.Value = new ProgressionDependencies {
            LevelInfo = (school, level) => school == MagicSchool.Fire && level == 12 ? new MagicLevelInfo { m_hitpoints = 640 } : null,
        };
        try {
            var wizard = Wizard(MagicSchool.Fire, 12, current: 213);
            var health = CombatService.HealthAfterDuel(wizard);
            Assert.NotNull(health);
            Assert.Equal(wizard.GameObjectID, (ulong) health.CharacterID);
            Assert.Equal(213, health.NewHealth);
            Assert.Equal(640, health.NewHealthMax); // the client adds its gear effects itself
            Assert.Equal(0, health.DisplayDiff);

            Assert.Equal(0, CombatService.HealthAfterDuel(Wizard(MagicSchool.Fire, 12, current: -5)).NewHealth);
            Assert.Null(CombatService.HealthAfterDuel(Wizard(MagicSchool.Ice, 12, current: 100))); // no table: nothing
            Assert.Null(CombatService.HealthAfterDuel(null));
        }
        finally {
            WizardProgressionTransactions.TestScope.Value = old;
        }
    }

    private static Wizard Wizard(MagicSchool school, int level, int current) {
        // Without its constructor (it reads the server configuration): only the health fields matter here.
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_currentHitpoints = current;
        stats.m_baseHitpoints = 700;
        return new Wizard {
            CharId = 4242,
            MagicSchoolBehavior = new ServerMagicSchoolBehavior { MagicSchool = school, Level = level },
            GameStats = stats,
        };
    }

}
