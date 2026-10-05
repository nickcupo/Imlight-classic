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
 * CLASSIC BOSS CHEATS (DUEL)
 * ========================================================================
 *
 * PURPOSE:
 * The duel's side of the scripted boss cheats (BossCheatDirector): one
 * director per duel, started again with each new fight on the sigil, and
 * the boss's summons, which join the boss's own side (a creature slot,
 * owned by the boss) rather than a wizard's.
 *
 * USAGE EXAMPLE:
 * BeginBossCheatRound(Duel.m_roundNum);   // ReceiveNewRound
 *
 * NOTE:
 * A summoned creature is added to the duel the next round, as every
 * minion is (AddWaitingCombatParticipants).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    private BossCheatDirector _bossCheats;

    /// <summary>CLASSIC: this fight's boss cheats.</summary>
    internal BossCheatDirector BossCheats => _bossCheats ??= new BossCheatDirector(this);

    /// <summary>
    /// CLASSIC: a new round of the fight; the first round starts a new director, since the sigil's component outlives
    /// each fight.
    /// </summary>
    private void BeginBossCheatRound(int round) {
        if (!ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive || BossCheatCount == 0) {
            return;
        }

        if (round <= 1) {
            _bossCheats = null;
        }

        BossCheats.NewRound(round);
    }

    private static int BossCheatCount => Imlight.CoreLib.Classic.ClassicProgression.BossCheats.Count;

    /// <summary>
    /// CLASSIC: <paramref name="summon"/>'s creatures join <paramref name="boss"/>'s side, in free creature slots.
    /// </summary>
    internal void SummonBossMinions(CombatDuelSubCircle boss, BossSummon summon) {
        for (var i = 0; i < summon.Count; i++) {
            var slot = GetAvailableSubCircleTeamCreature();
            if (slot is null) {
                Logger.Information("Duel {0} | BOSSCHEAT summon of {1} ({2}) stops at {3} of {4}: no free creature slot.",
                    Logger.Args(Duel.m_duelID.Full, summon.Name, summon.Creature, i, summon.Count));

                return;
            }

            var template = CoreObjectFactory.GetCoreTemplate(summon.Creature);
            if (template is null) {
                Logger.Warning("Duel {0} | BOSSCHEAT summon: no template for creature tid {1}.",
                    Logger.Args(Duel.m_duelID.Full, summon.Creature));

                return;
            }

            var centre = Entity.ActiveGameObject?.m_location ?? (boss?.ParticipantObject?.m_location ?? default);
            var info = new CoreObjectInfo {
                m_templateID = summon.Creature,
                m_location = centre,
                m_fScale = 1.0f,
            };
            var minionObj = CoreObjectFactory.FinalizeCoreObject(info, template);
            minionObj = CoreObjectFactory.InitializeCoreObjectBehaviors(minionObj, template);

            var minionActor = Entity.SpawnCombatMinionActor(minionObj, template);
            if (minionActor is null) {
                return;
            }

            try {
                AssignParticipantToSubCircle(slot, minionActor, minionObj, isSummonedMinion: true, minionOwnerSubCircle: boss.SlotIndex);
            }
            catch (Exception ex) {
                Logger.Error("Duel {0} | BOSSCHEAT summon: failed to assign tid {1} to slot {2}: {3}",
                    Logger.Args(Duel.m_duelID.Full, summon.Creature, slot.SlotIndex, ex));
                slot.RemoveParticipant();
                minionActor.Tell(PoisonPill.Instance);

                return;
            }

            Logger.Information("Duel {0} | BOSSCHEAT {1} ({2}) joins {3}'s side in slot {4} (in the duel next round).",
                Logger.Args(Duel.m_duelID.Full, summon.Name, summon.Creature, boss.SlotIndex, slot.SlotIndex));
        }
    }

}
