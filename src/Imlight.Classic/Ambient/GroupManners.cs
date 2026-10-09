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
 * GROUP MANNERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): grouping with ambient wizards the way 2010
 * players grouped with strangers (owner: "we should be able to ask
 * ambient wizards to join us in a dungeon or whatever and build a group
 * 'naturally' like the old days with randos"). The pure rules:
 *   - Settings ([Classic] AmbientWizardGroups, ...GroupSize,
 *     ...GroupChance, ...GroupWindow, ...GroupMinutes).
 *   - Hearing a call: "anyone want to do jotun?", "need help with
 *     kraken", "LF group for hall of kings", "wanna group?", a wizard's
 *     name with any of those, or the client's menu phrase "Would you like
 *     to join my group?" (ParseCall), and what it is about (the target).
 *   - Who answers how (Answer): a fitting level for the player and for
 *     the target (the quest's level, when the server knows it), the
 *     wizard's temper, whether its school is already in the group and
 *     whether the group lacks a healer. Some say yes, some "brb" first,
 *     some decline, most of an open call's listeners let it pass. A
 *     direct ask (by name, a whisper, the client's group invite) is
 *     answered more often and more often with a yes.
 *   - Ending it: "bye" (and the like) always; "thanks" only out of a
 *     fight and after a few minutes together (a "ty" for a heal is not a
 *     goodbye) (ParseDismiss, ThanksEnds).
 *   - How long a companion stays (StayFor), and the few fixed waits.
 *   - Size: up to four in a group, real players included (OpenSlots).
 * Nothing here lowers any creature's strength: companions are ordinary
 * wizards of their level with their usual decks.
 *
 * USAGE EXAMPLE:
 * var call = GroupManners.ParseCall("anyone want to do jotun?");   // Open, "jotun"
 * var answer = GroupManners.Answer(facts, settings.Willing, rng.NextDouble());
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Imlight.Classic.Ambient;

/// <summary>The ambient grouping switches (see the file header).</summary>
/// <param name="Enabled">[Classic] AmbientWizardGroups (default true).</param>
/// <param name="MaxSize">[Classic] AmbientWizardGroupSize, 2..4 (default 4): the whole group, real players included.</param>
/// <param name="Willing">[Classic] AmbientWizardGroupChance, 0..1 (default 0.5): a friendly wizard's chance to say yes to
/// an open call; tempers, direct asks and levels move it.</param>
/// <param name="Window">[Classic] AmbientWizardGroupWindow (default true): show companions in the client's own group
/// window (MSG_PARTY*), with its group chat and Leave Group button.</param>
/// <param name="StayMinutes">[Classic] AmbientWizardGroupMinutes, 10..240 (default 45): how long a companion stays, on
/// average (each one 0.6 to 1.6 times this).</param>
public sealed record GroupSettings(bool Enabled, int MaxSize, double Willing, bool Window, double StayMinutes) {

    public const int DefaultSize = 4;
    public const double DefaultWilling = 0.5;
    public const double DefaultStayMinutes = 45;

    public static GroupSettings Off { get; } = new(false, 0, 0, false, 0);

    /// <summary>Parses the settings; blank or bad values take their defaults.</summary>
    public static GroupSettings Parse(string? enabled, string? size, string? willing, string? window, string? minutes) {
        var on = Flag(enabled);
        var n = int.TryParse(size?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 2, 4) : DefaultSize;
        var p = double.TryParse(willing?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w)
            ? Math.Clamp(w, 0, 1) : DefaultWilling;
        var stay = double.TryParse(minutes?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && double.IsFinite(m)
            ? Math.Clamp(m, 10, 240) : DefaultStayMinutes;
        return on && p > 0 ? new GroupSettings(true, n, p, Flag(window), stay) : Off;
    }

    private static bool Flag(string? value)
        => string.IsNullOrWhiteSpace(value) || !bool.TryParse(value.Trim(), out var on) || on;

}

/// <summary>How a line asks for company.</summary>
public enum RecruitKind { None, Open, Named }

/// <summary>A call for company and what it is about (a dungeon, boss, quest or place as the player typed it), if said.</summary>
public sealed record RecruitCall(RecruitKind Kind, string? Target) {

    public static RecruitCall None { get; } = new(RecruitKind.None, null);

}

/// <summary>A line that may end a group.</summary>
public enum DismissKind { None, Bye, Thanks }

/// <summary>What a wizard answers to a call.</summary>
public enum GroupAnswer { Ignore, Yes, Brb, Decline, Done, TooLow, Full }

/// <summary>What a wizard weighs when asked to group.</summary>
/// <param name="HelperLevel">Its level.</param>
/// <param name="PlayerLevel">The asking player's level.</param>
/// <param name="TargetLevel">The level of what the player wants to do (a quest's level), or 0 when not known.</param>
/// <param name="Direct">Asked by name or by whisper.</param>
/// <param name="Invite">Asked with the client's group invite.</param>
/// <param name="Temper">Its temper.</param>
/// <param name="SchoolTaken">Someone in the group already has its school.</param>
/// <param name="Healer">It is a Life wizard.</param>
/// <param name="GroupHasHealer">The group already has a Life wizard.</param>
/// <param name="OpenSlots">Places left in the group.</param>
public readonly record struct RecruitFacts(int HelperLevel, int PlayerLevel, int TargetLevel, bool Direct, bool Invite,
                                           AmbientTemper Temper, bool SchoolTaken, bool Healer, bool GroupHasHealer, int OpenSlots);

/// <summary>The grouping rules (see the file header).</summary>
public static class GroupManners {

    /// <summary>Most companions a player can have: a group is four, the player included.</summary>
    public const int MaxGroup = 4;

    /// <summary>A player who has been gone (logged off) this long loses the group.</summary>
    public static readonly TimeSpan LeaderGone = TimeSpan.FromSeconds(30);

    /// <summary>"thanks" ends a group only after this long together (before, it is a thanks for a heal).</summary>
    public static readonly TimeSpan ThanksAfter = TimeSpan.FromMinutes(3);

    /// <summary>A player who has not moved or fought this long is asked "u there?"...</summary>
    public static readonly TimeSpan AfkAsk = TimeSpan.FromMinutes(10);

    /// <summary>...and this long, the group leaves.</summary>
    public static readonly TimeSpan AfkLeave = TimeSpan.FromMinutes(15);

    /// <summary>A companion whose time is up inside a dungeon stays until it is out, or at most this much longer.</summary>
    public static readonly TimeSpan DungeonGrace = TimeSpan.FromMinutes(20);

    /// <summary>A companion that cannot follow (a house, a full dungeon) waits outside this long, then goes.</summary>
    public static readonly TimeSpan WaitOutside = TimeSpan.FromMinutes(10);

    /// <summary>A companion that cannot walk to the player teleports to them (as to a friend) at most this often.</summary>
    public static readonly TimeSpan PortEvery = TimeSpan.FromMinutes(2);

    /// <summary>A "brb" lasts this long at least...</summary>
    public static readonly TimeSpan BrbShortest = TimeSpan.FromSeconds(25);

    /// <summary>...and at most this long.</summary>
    public static readonly TimeSpan BrbLongest = TimeSpan.FromSeconds(80);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex[] s_calls = [
        new(@"\b(lfg|lfm|lf\s*(group|grp|team|help|ppl|people|more|\d))\b|\blooking\s+for\s+(a\s+)?(group|grp|team|help|ppl|people|players|helpers?)\b", Options),
        new(@"\b(anyone|anybody|any1|someone|somebody|who|ppl|people|guys|u\s+guys|you\s+guys)\b.{0,30}?\b(wanna|want|wants|want2|help|helps|come|join|group|team|quest|do|run|kill|beat|defeat|fight|go)\b", Options),
        new(@"\b(need|needs|needing|want|wanna)\s+(some\s+|a\s+|more\s+)?(help|hand|backup|group|grp|team|people|ppl|helpers?|partners?)\b", Options),
        new(@"\b(wanna|want\s+to|want2|wan\s+to|u\s+want\s+to|do\s+(you|u)\s+want\s+to|would\s+(you|u)\s+like\s+to)\s+(group|grp|team|team\s+up|quest|join|come|duo|help|do)\b", Options),
        new(@"\b(join\s+(my|our|me|us)|group\s+up|team\s+up|party\s+up|group\s+with\s+me|quest\s+with\s+me|come\s+with\s+me)\b", Options),
        new(@"^\s*(group|grp|team|party|teaming)\s*\?+\s*$", Options),
        new(@"\b(help\s+me|help\s+(pls|plz|please)|(pls|plz|please)\s+help|halp)\b", Options),
        new(@"\b(can|could|will|would)\s+(you|u|someone|anyone|any1|somebody|anybody)\s+(help|come|join)\b", Options),
        new(@"\blet'?s\s+go\s+(do\s+some\s+quests|defeat\s+a\s+boss)\b", Options),
    ];

    // Questions for information, other activities, and "no" are not calls for company.
    private static readonly Regex s_notACall = new(
        @"\b(find|where|how\s+do|how\s+to|how\s+can|what\s+is|duel|pvp|arena|trade|hatch|solo|no\s+help|don'?t\s+need|dont\s+need|not\s+looking|no\s+thanks|nvm|never\s*mind)\b",
        Options);

    private static readonly Regex s_target = new(
        @"\b(?:do|with|for|at|in|kill|beat|defeat|fight|run|to|on|vs|against)\s+(?:the\s+)?(?<t>[a-z0-9' ]{3,40}?)\s*(?:[?!.,]|$|\b(?:pls|plz|please|with\s+me|w\s+me|anyone|any1|someone|together|now|rn|asap)\b)",
        Options);

    private static readonly Regex s_leadingFiller = new(
        @"^(me|us|u|you|to|do|go|come|run|kill|beat|defeat|fight|help|with|w|the|a|an|my|our|some|at|for|in|on|vs|against|get|finish)\s+",
        Options);

    private static readonly string[] s_noTarget = [
        "me", "it", "this", "that", "something", "stuff", "quest", "quests", "my quest", "a quest", "some quests", "group", "a group",
        "help", "u", "you", "us", "my group", "our group", "a boss", "boss", "some", "a dungeon", "dungeon", "my dungeon", "this quest",
        "this one", "that one", "my", "go", "them", "him", "her", "a hand", "team", "a team",
    ];

    private static readonly Regex s_bye = new(
        @"\b(bye+|bai|byebye|cya+|see\s+ya|see\s+you|later|l8r|gtg|g2g|got\s+to\s+go|gotta\s+go|have\s+to\s+go|leave\s+(the\s+)?group|leaving|you\s+can\s+go|u\s+can\s+go|i'?ll\s+solo|ill\s+solo|i'?m\s+done|im\s+done|that'?s\s+all|thats\s+all|good\s*night|gn|disband|kick)\b",
        Options);

    private static readonly Regex s_thanks = new(@"\b(thanks|thank\s+you|thank\s+u|thx|thnx|thanx|ty|tyvm|tysm|tyty)\b", Options);

    /// <summary>
    /// Whether <paramref name="text"/> asks for company. A line that also uses <paramref name="myFirstName"/>, or a whisper,
    /// is <see cref="RecruitKind.Named"/>; others are <see cref="RecruitKind.Open"/>.
    /// </summary>
    public static RecruitCall ParseCall(string? text, string? myFirstName = null, bool whisper = false) {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120) {
            return RecruitCall.None;
        }

        var line = text.Trim();
        if (s_notACall.IsMatch(line) || !s_calls.Any(r => r.IsMatch(line))) {
            return RecruitCall.None;
        }

        var named = whisper || (!string.IsNullOrEmpty(myFirstName) && AmbientChatBrain.Mentions(line, myFirstName));
        return new RecruitCall(named ? RecruitKind.Named : RecruitKind.Open, Target(line, myFirstName));
    }

    /// <summary>What the call is about, as typed ("jotun", "hall of kings"), or null.</summary>
    public static string? Target(string text, string? myFirstName = null) {
        string? found = null;
        foreach (Match match in s_target.Matches(text.ToLowerInvariant())) {
            var t = Regex.Replace(match.Groups["t"].Value, @"\s+", " ").Trim().Trim('\'');
            string before;
            do {
                before = t;
                t = s_leadingFiller.Replace(t, "");
                t = Regex.Replace(t, @"\s+(quest|dungeon|boss|pls|plz|please|again)$", "");
            } while (t != before);

            if (t.Length < 3 || s_noTarget.Contains(t) || (myFirstName is { Length: > 0 } && t.Equals(myFirstName, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            if (Regex.IsMatch(t, @"^(group|grp|team|help|quest|join)\b")) {
                continue;
            }

            found = t; // the last one: "anyone want to come with me to do jotun" -> "jotun"
        }

        return found;
    }

    /// <summary>Whether <paramref name="text"/> says goodbye (any time) or thanks (see <see cref="ThanksEnds"/>).</summary>
    public static DismissKind ParseDismiss(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return DismissKind.None;
        }

        if (s_bye.IsMatch(text)) {
            return DismissKind.Bye;
        }

        return s_thanks.IsMatch(text) ? DismissKind.Thanks : DismissKind.None;
    }

    /// <summary>A "thanks" ends the group: out of a fight and after <see cref="ThanksAfter"/> together.</summary>
    public static bool ThanksEnds(bool inDuel, TimeSpan together) => !inDuel && together >= ThanksAfter;

    /// <summary>Places left for companions: a group of <paramref name="maxSize"/> holds the real players first.</summary>
    public static int OpenSlots(int realPlayers, int companions, int maxSize)
        => Math.Max(0, Math.Min(MaxGroup, Math.Max(2, maxSize)) - Math.Max(1, realPlayers) - Math.Max(0, companions));

    /// <summary>The chance (0..0.95) that a wizard says yes (see the file header).</summary>
    public static double YesChance(RecruitFacts facts, double willing) {
        if (facts.OpenSlots <= 0) {
            return 0;
        }

        var p = willing * facts.Temper switch { AmbientTemper.Quiet => 0.6, AmbientTemper.Chatty => 1.2, _ => 1.0 };
        if (facts.Invite) {
            p *= 1.6;
        }
        else if (facts.Direct) {
            p *= 1.35;
        }

        if (facts.SchoolTaken) {
            p *= 0.8;
        }

        if (facts.Healer && !facts.GroupHasHealer) {
            p *= 1.25;
        }

        if (facts.TargetLevel > 0 && facts.HelperLevel > facts.TargetLevel + 8) {
            p *= 0.6; // it did that one long ago; helping out is a favour
        }

        if (facts.HelperLevel > facts.PlayerLevel + 12) {
            p *= 0.4; // a high level walking a low one through: it happened, but not often
        }

        return Math.Clamp(p, 0, 0.95);
    }

    /// <summary>
    /// The wizard's answer, from a <paramref name="roll"/> in 0..1. Too low for it (five levels under the player, or two
    /// under the target's level): a direct ask hears so, an open call is let pass.
    /// </summary>
    public static GroupAnswer Answer(RecruitFacts facts, double willing, double roll) {
        var asked = facts.Direct || facts.Invite;
        if (facts.OpenSlots <= 0) {
            return asked ? GroupAnswer.Full : GroupAnswer.Ignore;
        }

        if ((facts.TargetLevel > 0 && facts.HelperLevel < facts.TargetLevel - 2) || facts.HelperLevel < Math.Max(1, facts.PlayerLevel - 5)) {
            return asked ? GroupAnswer.TooLow : GroupAnswer.Ignore;
        }

        var p = YesChance(facts, willing);
        if (roll < p) {
            return roll < p * 0.15 ? GroupAnswer.Brb : GroupAnswer.Yes;
        }

        var rest = (roll - p) / Math.Max(1e-9, 1 - p);
        var done = facts.TargetLevel > 0 && facts.HelperLevel > facts.TargetLevel + 8;
        var no = done ? GroupAnswer.Done : GroupAnswer.Decline;
        if (asked) {
            return rest < 0.75 ? no : GroupAnswer.Ignore;
        }

        return rest < 0.2 ? no : GroupAnswer.Ignore;
    }

    /// <summary>How long a companion stays: 0.6 to 1.6 times <paramref name="stayMinutes"/>, fixed by <paramref name="seed"/>.</summary>
    public static TimeSpan StayFor(int seed, double stayMinutes)
        => TimeSpan.FromMinutes(Math.Max(1, stayMinutes) * (0.6 + (uint) seed % 1001 / 1000.0));

    /// <summary>
    /// Zones a companion does not follow into: houses (2010: only by the owner's invitation) and the tutorial. Arena
    /// matches and minigames are refused by the caller (they are instances with their own rules).
    /// </summary>
    public static bool Followable(string? zone)
        => !string.IsNullOrEmpty(zone)
           && !zone.StartsWith("Housing", StringComparison.OrdinalIgnoreCase)
           && !zone.Contains("Tutorial", StringComparison.OrdinalIgnoreCase);

    /// <summary>The client's school index for its group and friends windows: 1 Balance .. 7 Storm (0: no icon).</summary>
    public static uint SocialSchool(AmbientSchool school) => school switch {
        AmbientSchool.Balance => 1, AmbientSchool.Death => 2, AmbientSchool.Fire => 3, AmbientSchool.Ice => 4,
        AmbientSchool.Life => 5, AmbientSchool.Myth => 6, AmbientSchool.Storm => 7, _ => 0,
    };

}
