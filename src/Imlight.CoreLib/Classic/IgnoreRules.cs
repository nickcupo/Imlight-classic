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
 * IGNORE RULES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: who ignores whom (multiplayer audit item C). An ignore belongs to the wizard who made it, as in 2009: when A
 *   ignores B, B's Say, whispers and friend requests no longer reach A, and B is on A's ignore list. B notices nothing
 *   and still hears A. The two wizards share one BuddyRelationships row, so the row records the ignoring wizards by id
 *   (Relationship.BlockedBy); its Blocked flag means "either ignores the other" (a suspended friendship) and is kept in
 *   step for database queries.
 *
 *   Before player data schema 3 the row had only Blocked: chat read it as "FirstPlayerId ignores SecondPlayerId", the
 *   ignore list as "either way", and the first player of a friendship row is whoever first asked. So A ignoring a
 *   friend B could drop A's whispers to B and put A on B's list. MigrateLegacy reads an old row as well as it can:
 *     - a row Ignore() created for two wizards who had none (a stranger) has no epoch (it was built with the empty
 *       constructor) and the ignoring wizard first: it becomes the first player's ignore;
 *     - an ignored friendship row does not say who ignored (its order is who asked first): it becomes an ignore both
 *       ways. That keeps the ignorer's ignore whichever it was; the other wizard may find the ignorer on their own
 *       ignore list (as before, after a relog) and can drop it there. Each migrated row is logged.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class IgnoreRules {

    /// <summary>True when the row is between these two wizards (either order).</summary>
    internal static bool IsBetween(Relationship? relationship, ulong one, ulong other)
        => relationship is not null
           && ((relationship.FirstPlayerId == one && relationship.SecondPlayerId == other)
               || (relationship.FirstPlayerId == other && relationship.SecondPlayerId == one));

    /// <summary>The other wizard on the row, seen from <paramref name="self"/>.</summary>
    internal static ulong Other(Relationship relationship, ulong self)
        => relationship.FirstPlayerId == self ? relationship.SecondPlayerId : relationship.FirstPlayerId;

    /// <summary>True when <paramref name="owner"/> ignores the other wizard on the row.</summary>
    internal static bool Ignores(Relationship? relationship, ulong owner)
        => relationship?.BlockedBy is { Count: > 0 } blockedBy && blockedBy.Contains(owner);

    /// <summary>True when <paramref name="owner"/> ignores <paramref name="target"/> according to this row.</summary>
    internal static bool Ignores(Relationship? relationship, ulong owner, ulong target)
        => IsBetween(relationship, owner, target) && Ignores(relationship, owner);

    /// <summary>
    /// Records or lifts <paramref name="owner"/>'s ignore of the other wizard on the row, leaving the other wizard's own
    /// ignore as it is. True when the row changed.
    /// </summary>
    internal static bool SetIgnore(Relationship relationship, ulong owner, bool ignore) {
        relationship.BlockedBy ??= [];
        bool changed;
        if (ignore) {
            changed = !relationship.BlockedBy.Contains(owner);
            if (changed) {
                relationship.BlockedBy.Add(owner);
            }
        }
        else {
            changed = relationship.BlockedBy.RemoveAll(id => id == owner) > 0;
        }

        relationship.Blocked = relationship.BlockedBy.Count > 0;

        return changed;
    }

    /// <summary>
    /// True for a row the old Ignore() created between two wizards with no row yet: the ignoring wizard is first, and
    /// it has no epoch (every friendship row is stamped when it is made).
    /// </summary>
    internal static bool IsLegacyStrangerIgnore(Relationship relationship)
        => relationship.RelationshipEpochInSeconds == 0 && relationship.IsBrokenUp;

    /// <summary>
    /// Brings a row written before schema 3 up to date: a Blocked row without BlockedBy becomes the first player's
    /// ignore of the second when the old Ignore() made it for a stranger, and an ignore both ways otherwise (see the
    /// file header). Also drops ids that are not on the row and repairs Blocked. True when the row changed.
    /// </summary>
    internal static bool MigrateLegacy(Relationship relationship) {
        var changed = false;
        if (relationship.BlockedBy is null) {
            relationship.BlockedBy = [];
            changed = true;
        }

        if (relationship.Blocked && relationship.BlockedBy.Count == 0) {
            relationship.BlockedBy.Add(relationship.FirstPlayerId);
            if (!IsLegacyStrangerIgnore(relationship)) {
                relationship.BlockedBy.Add(relationship.SecondPlayerId); // who ignored is unknown: keep it both ways
            }

            changed = true;
        }

        var strays = relationship.BlockedBy.RemoveAll(id => id != relationship.FirstPlayerId && id != relationship.SecondPlayerId);
        var duplicates = relationship.BlockedBy.Count - relationship.BlockedBy.Distinct().Count();
        if (duplicates > 0) {
            var distinct = relationship.BlockedBy.Distinct().ToList();
            relationship.BlockedBy.Clear();
            relationship.BlockedBy.AddRange(distinct);
        }

        var blocked = relationship.BlockedBy.Count > 0;
        if (relationship.Blocked != blocked) {
            relationship.Blocked = blocked;
            changed = true;
        }

        return changed || strays > 0 || duplicates > 0;
    }

    /// <summary>The wizards that <paramref name="owner"/> ignores, from the owner's rows (its ignore list).</summary>
    internal static IEnumerable<ulong> IgnoredBy(IEnumerable<Relationship>? relationships, ulong owner)
        => (relationships ?? [])
            .Where(r => r is not null && (r.FirstPlayerId == owner || r.SecondPlayerId == owner) && Ignores(r, owner))
            .Select(r => Other(r, owner))
            .Distinct();

    /// <summary>The wizards who ignore <paramref name="target"/>, from the target's rows.</summary>
    internal static IEnumerable<ulong> WhoIgnore(IEnumerable<Relationship>? relationships, ulong target)
        => (relationships ?? [])
            .Where(r => r is not null && (r.FirstPlayerId == target || r.SecondPlayerId == target)
                        && Other(r, target) != target && Ignores(r, Other(r, target)))
            .Select(r => Other(r, target))
            .Distinct();

}
