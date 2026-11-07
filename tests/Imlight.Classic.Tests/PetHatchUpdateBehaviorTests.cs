// CLASSIC: the hatch sequence's MSG_PETUPDATEBEHAVIOR carries the pet item's behavior. r806919 handles it on the item
// (ClientPetItemBehavior::MSG_PetUpdateBehavior 0x142108120) and applies Data only when it is a BasePetItemBehavior
// (0x1420eac90); the owner's ClientPetOwnerBehavior, which we sent before, is not one (iOS lead, ios-parity hatch3 run on
// playbot-ios, 2026-11-06).

using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PetHatchUpdateBehaviorTests {

    private static ClientPetItemBehavior Hatched() => new() {
        m_level = 1, m_XP = 0, m_requiredXP = 125, m_happiness = 3, m_overallRating = 40,
        m_allTalents = [999019], m_expressedTalents = [],
    };

    [Fact]
    public void TheHatchedPetsItemBehaviorGoesOutAndReadsBack() {
        Assert.True(PetService.TrySerializePetItemBehavior(Hatched(), out var blob));

        var reader = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(reader.Deserialize<PropertyClass>((byte[]) blob, PetService.PetItemBehaviorMask, out var read));
        var item = Assert.IsType<ClientPetItemBehavior>(read);
        Assert.Equal(1, (int) item.m_level);
        Assert.Equal(125, (int) item.m_requiredXP);
        Assert.Equal(3, (int) item.m_happiness);
        Assert.Equal(40, (int) item.m_overallRating);
        Assert.Null(item.m_allTalents); // flags 7: server-only, never transmitted
    }

    [Fact]
    public void TransmitAndMask31SendTheSameBytes() {
        // A non-versionable blob is positional; both masks select the same ClientPetItemBehavior properties, so the
        // client reads it right whichever of the two it uses.
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Serialize(Hatched(), (PropertyFlags) 31, out var mask31));
        Assert.True(PetService.TrySerializePetItemBehavior(Hatched(), out var blob));
        Assert.Equal((byte[]) mask31, (byte[]) blob);
    }

    [Fact]
    public void NoItemBehaviorSendsNothing() {
        Assert.False(PetService.TrySerializePetItemBehavior(null, out _));
    }
}
