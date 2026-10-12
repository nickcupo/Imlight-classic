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
 * AMBIENT GROUPS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): the shared part of grouping with ambient wizards
 * (owner: "build a group 'naturally' like the old days with randos").
 *   - Which real player leads which group of ambient companions (one
 *     AmbientCompanionGroup actor per player), and which wizard is whose
 *     companion; the group's chat channel id for the client's group chat.
 *   - Routing: a duel with the player in it (CombatDuelComponent through
 *     AmbientWizards.NotifyDuel) and a dungeon sigil the player steps on
 *     (InteractDungeonSigilComponent through AmbientDungeons.NotifySigil)
 *     reach the player's group, wherever its companions are.
 *   - The client's own group requests (AmbientGroupService): an invite to
 *     an ambient wizard goes to the zone it lives in, which answers it like
 *     a call by name; Leave Group, a removed member and group chat go to the
 *     group.
 *   - What a call is about (Target): a quest title, a goal's subject
 *     ("Defeat Jotun" -> jotun), a goal's zone or a world name, with the
 *     quest's level, from the served quest templates.
 *
 * USAGE EXAMPLE:
 * if (AmbientGroups.TryGet(playerCharId, out var group)) group.Tell(notice);
 * var target = AmbientGroups.Target("hall of kings");   // level 40, Grizzleheim/...
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A companion goes back to its home zone (AmbientZone.Groups): in place when it still stands in it, else later.</summary>
/// <param name="Wizard">The wizard.</param>
/// <param name="ZoneActor">The zone it stands in now (null: it was taken out of the world).</param>
/// <param name="After">When it comes back into its home zone, if it is not there now.</param>
internal sealed record AmbientCompanionBack(AmbientWizard Wizard, IActorRef ZoneActor, TimeSpan After);

/// <summary>A real player invited an ambient wizard with the client's group invite (to the wizard's home zone).</summary>
internal sealed record AmbientGroupInvite(AmbientWizard Wizard, ulong Leader);

/// <summary>What a call is about: its name as players say it, the quest's level and its zone.</summary>
internal sealed record AmbientGroupTarget(string Name, int Level, string Zone);

/// <summary>Ambient grouping state shared across actors (see the file header).</summary>
internal static class AmbientGroups {

    private static readonly ConcurrentDictionary<ulong, IActorRef> s_byLeader = new();
    private static readonly ConcurrentDictionary<ulong, ulong> s_leaderOf = new();
    private static readonly ConcurrentDictionary<ulong, int> s_companions = new();
    private static readonly ConcurrentDictionary<ulong, ulong> s_channels = new();
    private static Dictionary<string, (int Level, string Zone)> s_targets;

    /// <summary>The settings in force (Off until the director starts).</summary>
    internal static GroupSettings Settings { get; set; } = GroupSettings.Off;

    /// <summary>The group <paramref name="leader"/> leads, if any.</summary>
    internal static bool TryGet(ulong leader, out IActorRef group) {
        group = null;
        return leader != 0 && !s_byLeader.IsEmpty && s_byLeader.TryGetValue(leader, out group);
    }

    /// <summary>True when <paramref name="leader"/> has ambient companions (or is getting some).</summary>
    internal static bool HasGroup(ulong leader) => leader != 0 && !s_byLeader.IsEmpty && s_byLeader.ContainsKey(leader);

    /// <summary>Records <paramref name="group"/> as <paramref name="leader"/>'s; false (and nothing changes) when another is.</summary>
    internal static bool Register(ulong leader, IActorRef group, ulong channel) {
        if (!s_byLeader.TryAdd(leader, group)) {
            return s_byLeader.TryGetValue(leader, out var current) && current.Equals(group);
        }

        s_channels[channel] = leader;
        return true;
    }

    /// <summary>The group is over.</summary>
    internal static void Unregister(ulong leader, IActorRef group, ulong channel) {
        s_byLeader.TryRemove(new KeyValuePair<ulong, IActorRef>(leader, group));
        s_channels.TryRemove(new KeyValuePair<ulong, ulong>(channel, leader));
        s_companions.TryRemove(leader, out _);
        foreach (var member in s_leaderOf.Where(kv => kv.Value == leader).Select(kv => kv.Key).ToList()) {
            s_leaderOf.TryRemove(member, out _);
        }
    }

    /// <summary>The group of <paramref name="leader"/> now has <paramref name="members"/> companions.</summary>
    internal static void SetMembers(ulong leader, IReadOnlyCollection<ulong> members) {
        s_companions[leader] = members.Count;
        foreach (var gone in s_leaderOf.Where(kv => kv.Value == leader && !members.Contains(kv.Key)).Select(kv => kv.Key).ToList()) {
            s_leaderOf.TryRemove(gone, out _);
        }

        foreach (var member in members) {
            s_leaderOf[member] = leader;
        }
    }

    /// <summary>How many companions <paramref name="leader"/> has.</summary>
    internal static int CompanionCount(ulong leader) => s_companions.TryGetValue(leader, out var n) ? n : 0;

    /// <summary>The player whose companion <paramref name="ambientCharId"/> is, or 0.</summary>
    internal static ulong LeaderOf(ulong ambientCharId) => s_leaderOf.TryGetValue(ambientCharId, out var leader) ? leader : 0;

    /// <summary>The leader whose group chat channel is <paramref name="channel"/>, or 0.</summary>
    internal static ulong LeaderOfChannel(ulong channel) => channel != 0 && s_channels.TryGetValue(channel, out var leader) ? leader : 0;

    /// <summary>A duel changed: each real player in it who leads a group hears of it (the companions come and fight).</summary>
    internal static void NotifyDuel(AmbientDuelNotice notice) {
        if (s_byLeader.IsEmpty || notice is null) {
            return;
        }

        foreach (var player in notice.PlayerCharIds ?? []) {
            if (s_byLeader.TryGetValue(player, out var group)) {
                group.Tell(notice);
            }
        }
    }

    /// <summary>A leader stepped on a dungeon sigil: the companions step on with them.</summary>
    internal static void NotifySigil(AmbientSigilNotice notice) {
        if (!s_byLeader.IsEmpty && notice is not null && s_byLeader.TryGetValue(notice.PlayerCharId, out var group)) {
            group.Tell(notice);
        }
    }

    /// <summary>
    /// The client's group invite from <paramref name="leader"/> to <paramref name="ambientCharId"/>: to the wizard's zone,
    /// which answers it. False when the wizard cannot be asked now (the caller tells the client): already with someone,
    /// in a dungeon, away, or grouping is off.
    /// </summary>
    internal static bool Invite(ulong leader, ulong ambientCharId, out bool alreadyGrouped) {
        alreadyGrouped = false;
        if (!Settings.Enabled || !AmbientWizards.TryGet(ambientCharId, out var wizard) || wizard.Group is null) {
            return false;
        }

        var with = LeaderOf(ambientCharId);
        if (with == leader) {
            return true; // already in this group: nothing to do
        }

        if (with != 0 || wizard.Driver is not null) {
            alreadyGrouped = with != 0;
            return false;
        }

        wizard.Group.Tell(new AmbientGroupInvite(wizard, leader));
        return true;
    }

    /// <summary>
    /// What <paramref name="phrase"/> (a call's target as typed) is: a quest's title, a goal's subject, a goal's zone or a
    /// world, with the quest's level; null when the server knows no such thing.
    /// </summary>
    internal static AmbientGroupTarget Target(string phrase) {
        var key = Key(phrase);
        if (key.Length < 2) {
            return null;
        }

        var index = s_targets ??= BuildTargets();
        if (index.TryGetValue(key, out var exact)) {
            return new AmbientGroupTarget(phrase, exact.Level, exact.Zone);
        }

        if (key.Length < 4) {
            return null;
        }

        var hit = index.Where(kv => kv.Key.Length >= 4 && (kv.Key.Contains(key, StringComparison.Ordinal) || key.Contains(kv.Key, StringComparison.Ordinal)))
            .OrderBy(kv => Math.Abs(kv.Key.Length - key.Length)).ThenBy(kv => kv.Value.Level).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (KeyValuePair<string, (int Level, string Zone)>?) kv).FirstOrDefault();
        return hit is { } found ? new AmbientGroupTarget(phrase, found.Value.Level, found.Value.Zone) : null;
    }

    /// <summary>A name as a lookup key: letters and digits only, lower case, no leading "the".</summary>
    internal static string Key(string text)
        => Regex.Replace(Regex.Replace((text ?? "").ToLowerInvariant().Trim(), @"^the\s+", ""), "[^a-z0-9]", "");

    private static readonly Regex s_subject = new(@"^(?:talk to|speak (?:to|with)|defeat|find|visit|meet|go to|see|destroy|stop|collect|kill|enter|explore)\s+(?:the\s+)?(?<what>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Dictionary<string, (int Level, string Zone)> BuildTargets() {
        var index = new Dictionary<string, (int Level, string Zone)>(StringComparer.Ordinal);

        void Add(string name, int level, string zone) {
            var key = Key(Regex.Replace(name ?? "", @"<[^>]*>", ""));
            if (key.Length < 3 || level <= 0) { // (the world short names below are added on their own)
                return;
            }

            if (!index.TryGetValue(key, out var known) || level < known.Level) {
                index[key] = (level, zone ?? known.Zone ?? "");
            }
        }

        try {
            foreach (var quest in QuestTemplateCollection.GetAllQuests() ?? []) {
                if (quest is null) {
                    continue;
                }

                var level = quest.m_questLevel;
                var zone = quest.m_goals?.FirstOrDefault(g => !string.IsNullOrEmpty(g?.m_destinationZone))?.m_destinationZone ?? "";
                Add(Locale.GetEnglishName(quest.m_questTitle ?? ""), level, zone);
                foreach (var goal in quest.m_goals ?? []) {
                    if (goal is null) {
                        continue;
                    }

                    var title = Regex.Replace(Locale.GetEnglishName(goal.m_goalTitle ?? "") ?? "", @"<[^>]*>", "");
                    if (s_subject.Match(title) is { Success: true } match) {
                        Add(match.Groups["what"].Value, level, goal.m_destinationZone ?? zone);
                    }

                    if (!string.IsNullOrEmpty(goal.m_destinationZone)) {
                        Add(AmbientKnowledge.ZoneName(goal.m_destinationZone), level, goal.m_destinationZone);
                    }

                    // The creature a goal counts ("Jotun": a bounty's tally names it) and the place the quest helper
                    // shows ("Hall of Kings").
                    if (goal.m_tallyCounter?.m_descriptor2 is { Length: > 0 } counted) {
                        Add(Locale.GetEnglishName(counted.ToString()), level, goal.m_destinationZone ?? zone);
                    }

                    if (goal.m_locationName is { Length: > 0 } place) {
                        Add(Locale.GetEnglishName(place.ToString()), level, goal.m_destinationZone ?? zone);
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Ambient groups: the quest target index is incomplete: {Error}", Logger.Args(ex.Message));
        }

        // The worlds, at the level their streets start (AmbientIdentity.LevelsFor), and the short names players used.
        foreach (var (names, world) in new (string[], string)[] {
                     (["wizard city", "wc"], "WizardCity/WC_Hub"), (["krokotopia", "kt", "kroko"], "Krokotopia/KT_Hub"),
                     (["marleybone", "mb"], "Marleybone/MB_Hub"), (["mooshu", "moo shu", "ms"], "MooShu/MS_Hub"),
                     (["dragonspyre", "dragonspire", "ds"], "DragonSpire/DS_Hub"), (["grizzleheim", "gh", "grizz"], "Grizzleheim/GH_MainHub"),
                 }) {
            var level = Math.Max(1, (int) AmbientIdentity.LevelsFor(world, 60).Min);
            foreach (var name in names) {
                var key = Key(name);
                if (key.Length >= 2) {
                    index.TryAdd(key, (level, world));
                }
            }
        }

        Logger.Information("Ambient groups know {Count} quests, bosses and places a call can be about.", Logger.Args(index.Count));
        return index;
    }

    internal static void ClearForTests() {
        s_byLeader.Clear();
        s_leaderOf.Clear();
        s_companions.Clear();
        s_channels.Clear();
    }

    /// <summary>For tests: a target index from known entries instead of the quest templates.</summary>
    internal static void SetTargetsForTests(Dictionary<string, (int Level, string Zone)> index) => s_targets = index;

}
