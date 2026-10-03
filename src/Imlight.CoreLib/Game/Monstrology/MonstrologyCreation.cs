using System;
namespace Imlight.CoreLib.Game.Monstrology;

// CLASSIC: MSG_MONSTERMAGICREQUESTCREATE.RequestType, read from the r806919 tome (GUI/MonsterTome.gui) and its
// handlers. The tome's button table (built at 0x1409f3569..0x1409f362c) binds CreateSummonButton to 0x1409f7420,
// CreateHouseGuestButton to 0x1409f7440 and CreateKillButton to 0x1409f7460; those pass 1, 2 and 3 to the request
// writer 0x1409fc710, which sends the value unchanged as RequestType. (0x1409f7404 = 2 changes a UI tab and is not a
// request.) The first mapping here (1 = house guest) turned every Treasure Card click into a house guest.
internal enum MonstrologyCreationKind { SummonCard = 1, HouseGuest = 2, KillCard = 3 }
internal sealed record MonstrologyCreationCost(int Animus, int Gold, uint KnownOutputTemplate);
internal static class MonstrologyCreation {
    internal static bool TryKind(int value, out MonstrologyCreationKind kind) {
        kind = (MonstrologyCreationKind)value;
        return value is 1 or 2 or 3;
    }
    internal static MonstrologyCreationCost Cost(MonstrologyMob mob, MonstrologyCreationKind kind)
        => kind switch {
            MonstrologyCreationKind.HouseGuest => new(mob.GuestAnimus, mob.GuestGold, mob.GuestTemplate),
            MonstrologyCreationKind.KillCard => new(mob.KillAnimus, mob.KillGold, 0),
            MonstrologyCreationKind.SummonCard => new(mob.SummonAnimus, mob.SummonGold, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }; // Dynamic card template/effect construction still needs stock evidence.
}
