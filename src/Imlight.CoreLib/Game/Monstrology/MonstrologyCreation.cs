using System;
namespace Imlight.CoreLib.Game.Monstrology;

// Confirmed request-writer callers: 0x1409f7424=1, 0x1409f7444=2, 0x1409f7464=3.
// 0x1409f7404=2 changes a UI tab and is NOT a request type.
internal enum MonstrologyCreationKind { HouseGuest = 1, KillCard = 2, SummonCard = 3 }
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
