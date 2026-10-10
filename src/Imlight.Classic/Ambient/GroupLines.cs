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
 * GROUP LINES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): what an ambient wizard says when asked to group,
 * while grouped and when it leaves, in the short lower-case chat of the
 * street lines (AmbientLines, DungeonLines), plain dictionary words only
 * (the zone filters every line through the client's chat dictionary as
 * well, ChatStyle.Apply). Nothing the player typed is put into a line.
 * A wizard with menu chat only (ChatChannel.Menu) uses the client's own
 * menu phrases (Root.wad QuickChat, r806919): "Yes!", "Let me help you
 * with that quest", "Sorry, I'm in another group", "Sorry but I cannot
 * help you right now", "I'll be right back", "I have to go AFK"; where the
 * menu has nothing fitting, it says nothing (Menu returns null).
 *
 * USAGE EXAMPLE:
 * var line = GroupLines.For(GroupLines.Yes, persona, wizard.Identity.Seed + wizard.Turn++);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System.Collections.Immutable;

namespace Imlight.Classic.Ambient;

/// <summary>Group chat lines (see the file header).</summary>
public static class GroupLines {

    /// <summary>Yes to a call or invite.</summary>
    public static readonly ImmutableArray<string> Yes = [
        "sure!", "sure, ill come", "ok ill help", "yea i need that too", "sure lets go", "ok!", "i can help", "yes!", "ill come",
        "sure, i need to do that too", "ok, lead the way", "sure, im bored lol",
    ];

    /// <summary>Yes, after a moment away.</summary>
    public static readonly ImmutableArray<string> Brb = ["brb 1 sec", "sure, brb", "one sec", "hold on, brb", "ok brb real quick"];

    /// <summary>Back from the "brb" and coming.</summary>
    public static readonly ImmutableArray<string> Back = ["back", "ok back, lets go", "back! still need help?", "im back"];

    /// <summary>No thanks.</summary>
    public static readonly ImmutableArray<string> Decline = [
        "no thx", "sorry im busy", "maybe later", "sorry, cant right now", "nah sorry", "im questing sorry", "not right now",
    ];

    /// <summary>No: it did that one long ago.</summary>
    public static readonly ImmutableArray<string> Done = ["i already did that one", "already beat it sorry", "did that already, sorry"];

    /// <summary>No: its level is too low.</summary>
    public static readonly ImmutableArray<string> TooLow = ["im too low for that lol", "my level is too low sorry", "too hard for me"];

    /// <summary>The group filled up before it got there.</summary>
    public static readonly ImmutableArray<string> Full = ["aw ur full", "oh u already have a group", "oh nvm ur full"];

    /// <summary>Its answer to the player's goodbye.</summary>
    public static readonly ImmutableArray<string> ByeBack = ["bye!", "cya!", "ty for the group!", "np, bye", "thx, gl!", "bye bye", "gg, cya"];

    /// <summary>Its answer to a thanks that does not end the group.</summary>
    public static readonly ImmutableArray<string> ThanksBack = ["np", "np!", "anytime", "ur welcome"];

    /// <summary>Leaving on its own: its time is up.</summary>
    public static readonly ImmutableArray<string> OwnLeave = [
        "gotta go, dinner", "sorry gtg, my mom needs the computer", "i have to go, ty for the group!", "gtg, bedtime",
        "sorry i have to go now", "brb... actually gtg", "gtg, ty!",
    ];

    /// <summary>Defeated: off to heal, back in a while.</summary>
    public static readonly ImmutableArray<string> Defeated = ["oops lol brb", "aw i died, brb", "sorry guys, brb"];

    /// <summary>Back after being defeated.</summary>
    public static readonly ImmutableArray<string> BackAfterDefeat = ["back!", "ok im back", "back, sorry"];

    /// <summary>The player stands still a long while.</summary>
    public static readonly ImmutableArray<string> AfkAsk = ["u there?", "hello?", "r u afk?"];

    /// <summary>The player stayed away: the group goes.</summary>
    public static readonly ImmutableArray<string> AfkLeave = ["ok i think ur afk, bye", "gtg, bye", "ok im going, bye"];

    /// <summary>A new zone with the player.</summary>
    public static readonly ImmutableArray<string> NewZone = ["where to now?", "lead the way", "ok where next?", "lets go"];

    /// <summary>It cannot go where the player went (a house, a full dungeon).</summary>
    public static readonly ImmutableArray<string> WaitOutside = ["ill wait out here", "ill wait here", "cant come in there, ill wait"];

    /// <summary>Waited long enough outside.</summary>
    public static readonly ImmutableArray<string> WaitedTooLong = ["ok i have to go, bye", "gtg, gl!"];

    /// <summary>A quick word after a won battle with the player.</summary>
    public static readonly ImmutableArray<string> AfterWin = ["gg", "nice", "ez", "gg!", "that was close lol", "nice one"];

    /// <summary>Menu-chat yes (r806919 QuickChat).</summary>
    public static readonly ImmutableArray<string> MenuYes = ["Yes!", "Let me help you with that quest"];

    /// <summary>Menu-chat no (r806919 QuickChat).</summary>
    public static readonly ImmutableArray<string> MenuNo = ["Sorry but I cannot help you right now", "Sorry, I don't have enough time for that"];

    /// <summary>Menu-chat "already grouped" (r806919 QuickChat).</summary>
    public static readonly ImmutableArray<string> MenuInGroup = ["Sorry, I'm in another group"];

    /// <summary>Menu-chat brb (r806919 QuickChat).</summary>
    public static readonly ImmutableArray<string> MenuBrb = ["I'll be right back"];

    /// <summary>Menu-chat leaving (r806919 QuickChat).</summary>
    public static readonly ImmutableArray<string> MenuLeave = ["I have to go AFK"];

    /// <summary>One of <paramref name="lines"/> by <paramref name="seed"/>.</summary>
    public static string Pick(ImmutableArray<string> lines, int seed) => lines[(int) ((uint) seed % (uint) lines.Length)];

    /// <summary>
    /// The line a wizard with <paramref name="persona"/> says from <paramref name="lines"/>: a typed line, or for a
    /// menu-chat wizard the menu phrase that fits (null: it says nothing).
    /// </summary>
    public static string? For(ImmutableArray<string> lines, ChatPersona persona, int seed) {
        if (persona.Channel != ChatChannel.Menu) {
            return Pick(lines, seed);
        }

        var menu = Menu(lines);
        return menu.IsDefaultOrEmpty ? null : Pick(menu, seed);
    }

    /// <summary>The menu phrases that stand in for <paramref name="lines"/> (empty: none fits).</summary>
    public static ImmutableArray<string> Menu(ImmutableArray<string> lines) {
        if (lines == Yes || lines == Back) {
            return MenuYes;
        }

        if (lines == Decline || lines == Done || lines == TooLow) {
            return MenuNo;
        }

        if (lines == Full) {
            return MenuInGroup;
        }

        if (lines == Brb || lines == Defeated) {
            return MenuBrb;
        }

        if (lines == OwnLeave || lines == AfkLeave || lines == WaitedTooLong) {
            return MenuLeave;
        }

        return [];
    }

}
