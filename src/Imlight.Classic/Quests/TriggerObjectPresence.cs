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
 * ZONE TRIGGERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: whether each object a zone's triggers manage is there, per state
 * scope (a zone instance, or one player in a public zone).
 *
 * USAGE EXAMPLE:
 * var presence = new TriggerObjectPresence<IActorRef>(plan.Objects);
 * presence.Remove("Gate of Paulson", scope);
 * presence.IsPresent("Gate of Paulson", scope, owner => armed(owner, scope));
 *
 * NOTE:
 * Each owning trigger has its own copy of its trigger object: the copy
 * exists while the trigger is armed. ResRemoveTriggerObject takes every
 * copy with that name away and ResAddTriggerObject brings them back; a
 * trigger that is armed again creates its copy again, and one that is
 * disarmed loses it. Sunken City's "Gate of Paulson" belongs to two
 * triggers, a StartZone one and the gate trigger that removes it and then
 * disables itself: the gate stays gone. An object the zone places is there
 * until a trigger removes it.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

/// <summary>
/// The presence of a zone's trigger-managed objects, per state scope.
/// </summary>
/// <typeparam name="TScope">The state scope key: the zone instance, or a player in a public zone.</typeparam>
public sealed class TriggerObjectPresence<TScope> where TScope : notnull {

    private const int PlacedCopy = -1;

    private readonly IReadOnlyDictionary<string, TriggerObjectPlan> _plans;
    private readonly Dictionary<int, List<string>> _tagsByOwner = [];
    private readonly Dictionary<TScope, Dictionary<(string Tag, int Owner), bool>> _overrides;

    /// <summary>
    /// Creates the presence of the objects of a plan; nothing has happened yet in any scope.
    /// </summary>
    /// <param name="plans">The managed objects (ZoneTriggerLiveness.Objects); objects that are not spawned are left out.</param>
    /// <param name="scopes">Compares scope keys; null uses the default comparer.</param>
    public TriggerObjectPresence(IReadOnlyDictionary<string, TriggerObjectPlan> plans, IEqualityComparer<TScope>? scopes = null) {
        _plans = plans.Where(p => p.Value.Spawn).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _overrides = new Dictionary<TScope, Dictionary<(string, int), bool>>(scopes);
        foreach (var plan in _plans.Values) {
            foreach (var owner in plan.Owners) {
                if (!_tagsByOwner.TryGetValue(owner, out var tags)) {
                    _tagsByOwner[owner] = tags = [];
                }

                tags.Add(plan.Tag);
            }
        }
    }

    /// <summary>The managed tags.</summary>
    public IEnumerable<string> Tags => _plans.Keys;

    /// <summary>True when <paramref name="tag"/> is managed.</summary>
    public bool Manages(string? tag) => tag is not null && _plans.ContainsKey(tag);

    /// <summary>The tags whose trigger object <paramref name="owner"/> owns.</summary>
    public IReadOnlyList<string> TagsOwnedBy(int owner)
        => _tagsByOwner.TryGetValue(owner, out var tags) ? tags : [];

    /// <summary>
    /// True when the object is there for the scope.
    /// </summary>
    /// <param name="tag">The object's zone tag.</param>
    /// <param name="scope">The state scope.</param>
    /// <param name="armed">True when the owning trigger (data index) is armed in the scope.</param>
    public bool IsPresent(string tag, TScope scope, Func<int, bool> armed) {
        if (!_plans.TryGetValue(tag, out var plan)) {
            return true;
        }

        _overrides.TryGetValue(scope, out var overrides);
        if (plan.Placed) {
            return overrides is null || !overrides.TryGetValue((tag, PlacedCopy), out var placed) || placed;
        }

        return plan.Owners.Any(owner => overrides is not null && overrides.TryGetValue((tag, owner), out var set) ? set : armed(owner));
    }

    /// <summary>
    /// The owning trigger was armed or disarmed in the scope: its copies follow its state again.
    /// </summary>
    /// <param name="owner">The trigger (data index).</param>
    /// <param name="scope">The state scope.</param>
    /// <returns>The tags of its copies.</returns>
    public IReadOnlyList<string> OwnerChanged(int owner, TScope scope) {
        var tags = TagsOwnedBy(owner);
        if (tags.Count > 0 && _overrides.TryGetValue(scope, out var overrides)) {
            foreach (var tag in tags) {
                overrides.Remove((tag, owner));
            }
        }

        return tags;
    }

    /// <summary>ResRemoveTriggerObject: every copy of the object goes away in the scope.</summary>
    /// <returns>False when the tag is not managed.</returns>
    public bool Remove(string tag, TScope scope) => Set(tag, scope, false);

    /// <summary>ResAddTriggerObject: every copy of the object comes back in the scope.</summary>
    /// <returns>False when the tag is not managed.</returns>
    public bool Add(string tag, TScope scope) => Set(tag, scope, true);

    private bool Set(string tag, TScope scope, bool present) {
        if (!_plans.TryGetValue(tag, out var plan)) {
            return false;
        }

        if (!_overrides.TryGetValue(scope, out var overrides)) {
            _overrides[scope] = overrides = [];
        }

        if (plan.Placed) {
            overrides[(tag, PlacedCopy)] = present;
        } else {
            foreach (var owner in plan.Owners) {
                overrides[(tag, owner)] = present;
            }
        }

        return true;
    }

}
