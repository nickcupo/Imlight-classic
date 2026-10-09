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
 * AMBIENT DUNGEONS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): the shared part of ambient wizards grouping for
 * dungeons (owner: "script them for dungeons too to help with dungeons").
 *   - The dungeon sigil (InteractDungeonSigilComponent) tells the zone's
 *     ambient wizards when a real player steps on and starts or joins a
 *     countdown (AmbientSigilNotice); AmbientZone.Dungeons decides who
 *     walks over, and AmbientDungeonParty runs the helpers inside.
 *   - The settings ([Classic] AmbientWizardDungeons, ...Chance,
 *     ...Helpers; DungeonSettings).
 *   - Players who said no to helpers, left alone for half an hour.
 *   - Each dungeon's hard player limit (its gamedata.bin, read once): a
 *     tower for one wizard takes no helper.
 *
 * USAGE EXAMPLE:
 * AmbientDungeons.NotifySigil(Entity.ZoneRef, notice);   // the sigil
 * if (AmbientDungeons.Settings.Enabled) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A real player stepped on a dungeon sigil, starting or joining its group's countdown.</summary>
/// <param name="Group">The sigil's group (shared with the sigil; it locks).</param>
/// <param name="Pad">The sigil's centre.</param>
/// <param name="PadHeading">The sigil's rotation (the compact location's W).</param>
/// <param name="SigilGid">The sigil's global id.</param>
/// <param name="SigilType">Its sigil template (the face slots).</param>
/// <param name="DestinationZone">The dungeon's first zone.</param>
/// <param name="DestinationLoc">Where in it the group arrives.</param>
/// <param name="PlayerCharId">The real player who stepped on.</param>
/// <param name="PlayerLevel">That player's level.</param>
internal sealed record AmbientSigilNotice(SigilGroup Group, Vector3 Pad, float PadHeading, ulong SigilGid, string SigilType,
                                          string DestinationZone, string DestinationLoc, ulong PlayerCharId, int PlayerLevel);

/// <summary>Ambient dungeon state shared across actors (see the file header).</summary>
internal static class AmbientDungeons {

    private static readonly ConcurrentDictionary<ulong, DateTime> s_noHelpers = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<int>>> s_hardLimits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings in force (Off until the director starts).</summary>
    internal static DungeonSettings Settings { get; set; } = DungeonSettings.Off;

    /// <summary>Tells <paramref name="zone"/>'s ambient wizards a real player is on a dungeon sigil. Free when it has none.</summary>
    internal static void NotifySigil(IActorRef zone, AmbientSigilNotice notice) {
        if (AmbientWizards.Count == 0) {
            return;
        }

        // CLASSIC (2026-10-09): a player with ambient companions takes them in (AmbientCompanionGroup); strangers on the
        // street do not come over to a grouped player's sigil.
        if (AmbientGroups.HasGroup(notice.PlayerCharId)) {
            AmbientGroups.NotifySigil(notice);
            return;
        }

        if (Settings.Enabled && AmbientWizards.TryGetGroup(zone, out var group)) {
            group.Tell(notice);
        }
    }

    /// <summary>The player said no to dungeon helpers: none comes for a while.</summary>
    internal static void SaidNo(ulong player, DateTime now) => s_noHelpers[player] = now + DungeonManners.QuietAfterNo;

    /// <summary>True while ambient wizards leave <paramref name="player"/>'s dungeon runs alone.</summary>
    internal static bool WantsNoHelpers(ulong player, DateTime now)
        => s_noHelpers.TryGetValue(player, out var until) && now < until;

    /// <summary>The dungeon zone's hard player limit (0: none given), read once from its gamedata.bin, off the caller's thread.</summary>
    internal static Task<int> HardLimitOf(string zone)
        => s_hardLimits.GetOrAdd(zone ?? "", z => new Lazy<Task<int>>(() => Task.Run(() => {
            try {
                return ResourceManager.LoadDeserializedFile<WizZoneData>(z, "gamedata.bin")?.m_nHardLimit ?? 0;
            }
            catch (Exception e) {
                Logger.Warning("Ambient wizards: no hard limit for {Zone} ({Error}); taken as one wizard.", Logger.Args(z, e.Message));
                return 1; // unknown: no helper rather than one too many
            }
        }))).Value;

    internal static void ClearForTests() => s_noHelpers.Clear();

}
