using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Housing;

// CLASSIC: ordinary furniture in the existing Ravenwood dorm. Castles, custom-object blobs, attic,
// pets, housing games and later scale/brightness editing require their own verified native paths.
internal static class HousingRules {
    internal const string DormZone = "WizardCity/Interiors/WC_Housing_Dorm_Interior";
    internal const string BlobType = "Housing";
    internal const string SubType = "Proxy";
    // r806919 ClientHousingBlobStrategy's lookup range is [UserData, UserData + 200).
    // This is a native package bound, not a claim about the historical room's furniture allowance.
    internal const int PackageSlots = 200;

    internal static bool IsDorm(string zone) => string.Equals(zone, DormZone, StringComparison.OrdinalIgnoreCase);
    internal static bool CanEdit(ulong character, ulong owner, string zone)
        => character != 0 && character == owner && IsDorm(zone);

    internal static bool OrdinaryFurniture(IEnumerable<string> adjectives, IEnumerable<string> behaviors,
        int primaryColors = 0, int secondaryColors = 0) {
        var a = (adjectives ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = (behaviors ?? []).ToArray();
        return a.Contains("Housing") && a.Contains("Furniture") && primaryColors == 0 && secondaryColors == 0
            && b.Contains("FurnitureInfoBehaviorTemplate", StringComparer.Ordinal)
            && b.All(x => x is "FurnitureInfoBehaviorTemplate" or "RenderBehaviorTemplate" or "CollisionBehaviorTemplate");
    }

    internal static bool ValidPosition(float x, float y, float z, float yaw)
        => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(yaw)
            // Native cache XY is sign-magnitude 16 bit; do not permit wraparound when rounded.
            && MathF.Abs(x) <= 32766 && MathF.Abs(y) <= 32766 && MathF.Abs(yaw) <= MathF.Tau;

    // Native BaseClientHousingBlobStrategy::RemoveBlob/CreateObject: slot low32, byte5 type9,
    // DynamicServerProcID low16 in the high16. Byte4 is zero, unlike inventory object ids.
    internal static ulong PlacedGlobalId(uint slot, uint dynamicServerProcId)
        => slot | (9UL << 40) | ((ulong)(ushort)dynamicServerProcId << 48);

    internal static bool TrySlot(ulong globalId, uint dynamicServerProcId, int count, out int slot) {
        slot = (int)(uint)globalId;
        return slot >= 0 && slot < count && count <= PackageSlots
            && globalId == PlacedGlobalId((uint)slot, dynamicServerProcId);
    }
}
