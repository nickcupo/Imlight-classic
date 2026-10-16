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
 * CLASSIC PROFILE FAMILIES AND DATA MEMBERSHIP
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: one place for the two questions a later profile (celestia-2010)
 * must answer like its parent (october-2010-arc1):
 *   - capability: "is this profile at least October 2010?" (its extends chain
 *     contains october-2010-arc1), replacing literal id comparisons;
 *   - data membership: "does a file or record listing october-2010-arc1 apply
 *     to me?" (the profile declares inherits_membership), so the parent's
 *     spell records and rules files need no edit.
 * A profile without inherits_membership keeps exact-id membership, so the
 * existing profiles behave exactly as before.
 *
 * USAGE EXAMPLE:
 * if (ClassicProfiles.IsOctoberFamily(profileId)) { ... }
 * if (ClassicProfiles.Lists(table.Profiles, profileId)) { ... }
 *
 * NOTE:
 * Ids resolve through the profiles ClassicProfileLoader has loaded in this
 * process (the active profile always is). An id never loaded is its own only
 * member and its own only lineage.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Imlight.Classic;

/// <summary>
/// CLASSIC: profile families (capability checks) and data-file membership with inheritance.
/// </summary>
public static class ClassicProfiles {

    /// <summary>The October 2010 rules profile; every profile that extends it has its mechanics.</summary>
    public const string October2010 = "october-2010-arc1";

    /// <summary>The Celestia / level 60 profile (October 2010 with Celestia); extends <see cref="October2010"/>.</summary>
    public const string Celestia2010 = "celestia-2010";

    /// <summary>The May 2011 (Wintertusk) profile; extends <see cref="Celestia2010"/> and inherits its membership.</summary>
    public const string Wintertusk2011 = "wintertusk-2011";

    /// <summary>
    /// The level every 2009 rule was written for (the cap of late-2009 and october-2010-arc1). Code that once said 50
    /// uses <see cref="MaxLevelOf(string)"/>, which answers this for a profile without a cap or one never loaded.
    /// </summary>
    public const int Arc1LevelCap = 50;

    private static readonly ConcurrentDictionary<string, (ImmutableArray<string> Members, ImmutableArray<string> Lineage, int? LevelCap)> s_known
        = new(StringComparer.Ordinal);

    /// <summary>Records a loaded profile so its id resolves to its membership, lineage and level cap.</summary>
    internal static void Register(ClassicProfile profile)
        => s_known[profile.Id] = (profile.MembershipIds.IsDefaultOrEmpty ? [profile.Id] : profile.MembershipIds, profile.Lineage,
            profile.LevelCap);

    /// <summary>
    /// The highest level the profile's wizards reach: its level cap, or <see cref="Arc1LevelCap"/> when it has none
    /// (dev-unrestricted) or was never loaded. Used where the code had a literal 50.
    /// </summary>
    public static int MaxLevelOf(string? profileId)
        => profileId is not null && s_known.TryGetValue(profileId, out var known) && known.LevelCap is int cap and > 0
            ? cap : Arc1LevelCap;

    /// <summary>The highest <see cref="MaxLevelOf(string)"/> among <paramref name="profileIds"/> (50 for none).</summary>
    public static int MaxLevelOf(IEnumerable<string> profileIds)
        => profileIds.Select(id => MaxLevelOf(id)).DefaultIfEmpty(Arc1LevelCap).Max();

    /// <summary>The ids whose listing includes <paramref name="profileId"/>, child first.</summary>
    public static ImmutableArray<string> MembershipIds(string profileId)
        => s_known.TryGetValue(profileId, out var known) ? known.Members : [profileId];

    /// <summary>The extends chain of <paramref name="profileId"/>, child first.</summary>
    public static ImmutableArray<string> Lineage(string profileId)
        => s_known.TryGetValue(profileId, out var known) ? known.Lineage : [profileId];

    /// <summary>
    /// True when <paramref name="listed"/> (a data file's or record's <c>profiles</c>) includes <paramref name="profileId"/>,
    /// directly or through a parent it inherits membership from.
    /// </summary>
    public static bool Lists(IEnumerable<string>? listed, string? profileId)
        => listed is not null && profileId is not null && AnyListed(listed, MembershipIds(profileId));

    /// <summary>True when <paramref name="profileId"/> is <paramref name="ancestorId"/> or extends it.</summary>
    public static bool IsOrExtends(string? profileId, string ancestorId)
        => profileId is not null && Lineage(profileId).Contains(ancestorId, StringComparer.Ordinal);

    /// <summary>True for october-2010-arc1 and every profile that extends it (celestia-2010, ...).</summary>
    public static bool IsOctoberFamily(string? profileId) => IsOrExtends(profileId, October2010);

    /// <summary>True for october-2010-arc1 and every profile that extends it (celestia-2010, ...).</summary>
    public static bool IsOctoberFamily(ClassicProfile? profile) => profile?.IsOrExtends(October2010) == true;

    /// <summary>True for celestia-2010 and every profile that extends it (wintertusk-2011, ...).</summary>
    public static bool IsCelestiaOrLater(string? profileId) => IsOrExtends(profileId, Celestia2010);

    /// <summary>True for celestia-2010 and every profile that extends it (wintertusk-2011, ...).</summary>
    public static bool IsCelestiaOrLater(ClassicProfile? profile) => profile?.IsOrExtends(Celestia2010) == true;

    /// <summary>True for wintertusk-2011 and every profile that extends it: the May 2011 rules apply.</summary>
    public static bool IsWintertuskFamily(string? profileId) => IsOrExtends(profileId, Wintertusk2011);

    /// <summary>True for wintertusk-2011 and every profile that extends it: the May 2011 rules apply.</summary>
    public static bool IsWintertuskFamily(ClassicProfile? profile) => profile?.IsOrExtends(Wintertusk2011) == true;

    internal static bool AnyListed(IEnumerable<string> listed, ImmutableArray<string> members) {
        var set = listed as ICollection<string> ?? listed.ToList();
        foreach (var id in members) {
            if (set.Contains(id, StringComparer.Ordinal)) {
                return true;
            }
        }

        return false;
    }

}
