// CLASSIC: mechanics added to the separate October 2010 profile. Older profiles keep their own rules.

using Imlight.Classic;

namespace Imlight.CoreLib.Classic;

internal static class ClassicOctoberRules {
    internal static bool Active => ClassicRuntime.IsActive && ClassicRuntime.Rules.Profile.Id == "october-2010-arc1";

    // July 2010 archived update notes: a stun in PvP leaves four automatic stun shields instead of one.
    // https://web.archive.org/web/20140122055803/https://www.wizard101.com/game/community/updatenotes/july2010
    internal static int AutomaticStunBlocks(bool pvp) => Active && pvp ? 4 : 1;
}
