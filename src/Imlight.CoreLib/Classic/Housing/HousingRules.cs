using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic.Housing;

// CLASSIC: ordinary furniture in the existing Ravenwood dorm. Castles, custom-object blobs,
// pets, housing games and later scale/brightness editing require their own verified native paths.
internal static class HousingRules {
    internal const string DormZone = "WizardCity/Interiors/WC_Housing_Dorm_Interior";
    internal const string BlobType = "Housing";
    internal const string SubType = "Proxy";
    // r806919 ClientHousingBlobStrategy's lookup range is [UserData, UserData + 200).
    // This is a native package bound, not a claim about the historical room's furniture allowance.
    internal const int PackageSlots = 200;
    // UserData200/400 belong to the owner's attic. A second room package occupies600..799.
    internal const uint SecondRoomUserData = 600;
    internal const string AtticSubType = "Attic";
    // Native ClientAtticBehavior::GetCapacity (r806919 0x140f148d0) returns300. This is
    // a current-client compatibility ceiling, NOT evidence of the October2010 allowance.
    internal const int AtticCompatibilityCeiling = 300;
    // Historical allowance awaits dated evidence or an explicit owner ruling. Backend/codec
    // tests use an explicit capacity; the real handlers stay closed while this is zero.
    internal const int ApprovedAtticCapacity = 0;
    internal const int AtticPackageCount = 2;
    internal static uint AtticUserData(int packageIndex) => checked((uint)((packageIndex + 1) * PackageSlots));

    internal static bool TryAtticSlot(ulong id, uint dynamicProc, int packageIndex, int count, out int slot) {
        slot = -1;
        if (packageIndex < 0 || packageIndex >= AtticPackageCount || count < 0 || count > PackageSlots) return false;
        var first = AtticUserData(packageIndex);
        var index = (uint)id;
        slot = index >= first && index - first <= int.MaxValue ? (int)(index - first) : -1;
        return slot >= 0 && slot < count && id == PlacedGlobalId(index, dynamicProc);
    }

    internal static bool IsDorm(string zone) => string.Equals(zone, DormZone, StringComparison.OrdinalIgnoreCase);
    internal static bool CanEdit(ulong character, ulong owner, string zone)
        => character != 0 && character == owner && (IsDorm(zone) || HouseCatalog.IsApprovedRoom(zone));

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
