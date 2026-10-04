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
 * SECOND CHANCE CHESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the state of the October 2009 Second Chance chests (classic-data/rules/second-chance-2009.yaml): which
 * bosses each wizard beat in which zone visit, the chest a wizard opened, and the uses of each chest today.
 *
 * NOTE:
 * "Simply use the chest after your initial duel, pay a few Crowns and you are given a second chance at the exact
 * same rewards the boss drops normally. These chests can only be used a certain number of times per day per
 * character." (October 2009 Update Notes). A win counts for the zone and instance it happened in, and is forgotten
 * when the wizard wins elsewhere or after WinLifetime. Uses are counted per UTC day in memory: a server restart
 * gives a wizard the day's uses again (documented in the session report).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Rules;

namespace Imlight.CoreLib.Game.SecondChance;

/// <summary>Why a wizard may not use a chest now.</summary>
internal enum ChestRefusal { None, NoChest, BossNotDefeated, NoUsesLeft, NotEnoughCrowns, NoPrompt }

/// <summary>CLASSIC: Second Chance chest state for every wizard.</summary>
internal sealed class SecondChanceChests {

    /// <summary>How long a boss win opens its chest when the wizard stays in the zone.</summary>
    public static readonly TimeSpan WinLifetime = TimeSpan.FromHours(2);

    private sealed record Win(string Zone, ulong Instance, HashSet<ulong> Templates, DateTime At);

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Win> _wins = [];
    private readonly Dictionary<(ulong CharId, ulong Chest, DateOnly Day), int> _uses = [];
    private readonly Dictionary<ulong, (ulong ChestGid, SecondChanceChest Chest)> _prompts = [];
    private readonly Func<DateTime> _now;

    public SecondChanceChests(Func<DateTime> now = null) => _now = now ?? (() => DateTime.UtcNow);

    public static SecondChanceChests Instance { get; } = new();

    private DateOnly Today => DateOnly.FromDateTime(_now());

    /// <summary>A won duel: <paramref name="mobTemplates"/> were defeated in this zone and instance.</summary>
    public void RecordWin(ulong charId, string zone, ulong instance, IEnumerable<ulong> mobTemplates) {
        if (zone is null || mobTemplates is null) {
            return;
        }

        lock (_gate) {
            var now = _now();
            if (_wins.TryGetValue(charId, out var win) && SameVisit(win, zone, instance) && now - win.At < WinLifetime) {
                win.Templates.UnionWith(mobTemplates);
                _wins[charId] = win with { At = now };
            }
            else {
                _wins[charId] = new Win(zone, instance, [.. mobTemplates], now);
            }
        }
    }

    private static bool SameVisit(Win win, string zone, ulong instance)
        => string.Equals(win.Zone, zone, StringComparison.OrdinalIgnoreCase) && win.Instance == instance;

    /// <summary>True if the wizard beat one of <paramref name="chest"/>'s bosses in this zone and instance.</summary>
    public bool HasDefeated(ulong charId, SecondChanceChest chest, string zone, ulong instance) {
        lock (_gate) {
            return _wins.TryGetValue(charId, out var win) && SameVisit(win, zone, instance) && _now() - win.At < WinLifetime
                   && string.Equals(chest.Zone, zone, StringComparison.OrdinalIgnoreCase)
                   && chest.BossTemplates.Any(win.Templates.Contains);
        }
    }

    /// <summary>Uses of <paramref name="chest"/> left today.</summary>
    public int UsesLeft(ulong charId, SecondChanceChest chest, SecondChanceRules rules) {
        lock (_gate) {
            return Math.Max(0, rules.DailyUses - _uses.GetValueOrDefault((charId, chest.Template, Today)));
        }
    }

    /// <summary>The Crowns the next use costs.</summary>
    public int NextCost(ulong charId, SecondChanceChest chest, SecondChanceRules rules) {
        lock (_gate) {
            return rules.CostOfUse(_uses.GetValueOrDefault((charId, chest.Template, Today)));
        }
    }

    /// <summary>The wizard clicked a chest: remember it for the answer to the prompt.</summary>
    public ChestRefusal Open(ulong charId, ulong chestGid, SecondChanceChest chest, string zone, ulong instance, SecondChanceRules rules) {
        if (chest is null) {
            return ChestRefusal.NoChest;
        }

        if (!HasDefeated(charId, chest, zone, instance)) {
            return ChestRefusal.BossNotDefeated;
        }

        lock (_gate) {
            _prompts[charId] = (chestGid, chest);
        }

        return UsesLeft(charId, chest, rules) > 0 ? ChestRefusal.None : ChestRefusal.NoUsesLeft;
    }

    /// <summary>The wizard closed the chest window.</summary>
    public void Close(ulong charId) {
        lock (_gate) {
            _prompts.Remove(charId);
        }
    }

    /// <summary>
    /// The wizard paid for a roll: checks the prompt, the win and the uses, then <paramref name="pay"/> (the Crown
    /// debit, given the price; false when the wizard cannot pay) and counts the use, all at once.
    /// </summary>
    /// <returns>The chest and price, or the refusal.</returns>
    public ChestRefusal TryUse(ulong charId, ulong chestGid, string zone, ulong instance, SecondChanceRules rules,
                               Func<int, bool> pay, out SecondChanceChest chest, out int cost) {
        chest = null;
        cost = 0;
        lock (_gate) {
            if (!_prompts.TryGetValue(charId, out var prompt) || prompt.ChestGid != chestGid) {
                return ChestRefusal.NoPrompt;
            }

            chest = prompt.Chest;
            var key = (charId, chest.Template, Today);
            var used = _uses.GetValueOrDefault(key);
            var now = _now();
            if (!_wins.TryGetValue(charId, out var win) || !SameVisit(win, zone, instance) || now - win.At >= WinLifetime
                || !chest.BossTemplates.Any(win.Templates.Contains)) {
                return ChestRefusal.BossNotDefeated;
            }

            if (used >= rules.DailyUses) {
                return ChestRefusal.NoUsesLeft;
            }

            cost = rules.CostOfUse(used);
            if (!pay(cost)) {
                return ChestRefusal.NotEnoughCrowns;
            }

            _uses[key] = used + 1;

            return ChestRefusal.None;
        }
    }

    /// <summary>The boss whose rewards a chest rolls: the first of its templates the wizard beat.</summary>
    public ulong BossFor(ulong charId, SecondChanceChest chest) {
        lock (_gate) {
            return _wins.TryGetValue(charId, out var win)
                ? chest.BossTemplates.FirstOrDefault(win.Templates.Contains, chest.BossTemplates[0])
                : chest.BossTemplates[0];
        }
    }

    /// <summary>The wizard logged out: an open chest window is gone (a win stays tied to its zone and instance).</summary>
    public void Forget(ulong charId) => Close(charId);

}
