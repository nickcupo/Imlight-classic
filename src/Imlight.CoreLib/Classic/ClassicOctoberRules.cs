// CLASSIC: mechanics added to the separate October 2010 profile. Older profiles keep their own rules.

using System;
using Imlight.Classic;

namespace Imlight.CoreLib.Classic;

internal static class ClassicOctoberRules {
    internal static bool Active => ClassicRuntime.IsActive && ClassicProfiles.IsOctoberFamily(ClassicRuntime.Rules.Profile); // CLASSIC: and every profile extending it

    // July 2010 archived update notes: a stun in PvP leaves four automatic stun shields instead of one.
    // https://web.archive.org/web/20140122055803/https://www.wizard101.com/game/community/updatenotes/july2010
    internal static int AutomaticStunBlocks(bool pvp) => Active && pvp ? 4 : 1;

    // May 2011 notes (patch 1.140, wintertusk-2011): a stun gives one stun shield, and in PvP as many shields as the
    // stunned wizard has opposing players (1v1 one ... 4v4 four) instead of July 2010's flat four.
    // https://web.archive.org/web/20140122200906/https://www.wizard101.com/game/community/updatenotes/may2011
    internal static int AutomaticStunBlocks(bool pvp, int opposingPlayers)
        => pvp && ClassicRuntime.IsActive && ClassicProfiles.IsWintertuskFamily(ClassicRuntime.Rules.Profile)
            ? Math.Max(1, opposingPlayers)
            : AutomaticStunBlocks(pvp);
}
