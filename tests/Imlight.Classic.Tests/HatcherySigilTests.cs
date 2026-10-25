// CLASSIC: the Pet Hatchery's two-player sigils start hatching (WizardCity/Interiors/WC_Hatchery, template 112077,
// PetMorphSigil01..04). r806919 never sends MSG_PETGAMEJOIN for the morph game (its only JOIN sender is the pet game
// kiosk window's Next button, 0x140aeb3a0); its WizardGraphicalClient::HandlePetGameInit (0x141370240) builds the
// PetGameMorph window from the server's MSG_PETGAMEINIT. So two wizards on one sigil go to their own copy of
// ThePhantomZoneWorld/PetGameMorph and the server starts the game there, side = the order they stepped on.

using System;
using Akka.Actor;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.Math;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class HatcherySigilTests {

    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IActorRef A = ActorRefs.Nobody, B = ActorRefs.NoSender;

    [Fact]
    public void TwoWizardsOnOneSigilMakeAPairInTheOrderTheyStepped() {
        var pad = new HatcheryPad();
        Assert.Null(pad.Update(A, 1, on: true));
        Assert.Null(pad.Update(A, 1, on: true));                 // the same wizard again is still one
        var pair = pad.Update(B, 2, on: true);
        Assert.NotNull(pair);
        Assert.Equal([1ul, 2ul], [pair![0].CharId, pair[1].CharId]);
        Assert.Equal(0, pad.Count);                               // the next pair starts afresh
    }

    [Fact]
    public void AWizardWhoStepsOffIsNoLongerWaiting() {
        var pad = new HatcheryPad();
        Assert.Null(pad.Update(A, 1, on: true));
        Assert.Null(pad.Update(A, 1, on: false));
        Assert.Null(pad.Update(B, 2, on: true));                  // alone again: nobody goes yet
        Assert.Equal(1, pad.Count);
        pad.Remove(B);                                            // leaving the zone
        Assert.Equal(0, pad.Count);
    }

    [Fact]
    public void TheRunRemembersEachWizardsSideAndForgetsStrangers() {
        HatcheryRuns.ClearForTests();
        HatcheryRuns.Register(77, [10, 20], T0);
        Assert.Equal(0, HatcheryRuns.SideOf(77, 10, T0.AddSeconds(5)));
        Assert.Equal(1, HatcheryRuns.SideOf(77, 20, T0.AddSeconds(5)));
        Assert.Null(HatcheryRuns.SideOf(77, 30, T0));             // not one of the pair
        Assert.Null(HatcheryRuns.SideOf(78, 10, T0));             // another run
        Assert.Null(HatcheryRuns.SideOf(0, 10, T0));              // no instance owner (a public copy)
        Assert.Null(HatcheryRuns.SideOf(77, 10, T0 + HatcheryRuns.Lifetime)); // forgotten
        Assert.Equal("ThePhantomZoneWorld/PetGameMorph#77", HatcheryRuns.LobbyKey(77));
        HatcheryRuns.ClearForTests();
    }

    [Fact]
    public void OnlyTheTwoPlayerSigilIsWired() {
        Assert.True(HatcherySigilComponent.ShouldAttachToEntity(new GameObjectTemplate { m_templateID = 112077, m_behaviors = [] }));
        // The one-player sigils (REQ HasMorphSlot) carry a Celestia-era template id: later than October 2010.
        Assert.False(HatcherySigilComponent.ShouldAttachToEntity(new GameObjectTemplate { m_templateID = 266075, m_behaviors = [] }));
        Assert.False(HatcherySigilComponent.ShouldAttachToEntity(new GameObjectTemplate { m_templateID = 107081, m_behaviors = [] }));
    }

    [Fact]
    public void StandingOnTheCircleCountsHeightDoesNot() {
        var pad = new Vector3(-883.5f, -498.7f, 5f);              // PetMorphSigil01 (lifted 5 like every sigil)
        Assert.True(HatcherySigilComponent.Within(new Vector3(-883.5f, -498.7f, 0f), pad));
        Assert.True(HatcherySigilComponent.Within(new Vector3(-883.5f + 140, -498.7f, 0f), pad));
        Assert.False(HatcherySigilComponent.Within(new Vector3(-883.5f + 160, -498.7f, 0f), pad));
    }

    // r806919's PetGameMorph::HandlePetGameStart (0x140abd9a0) loads START's Data as a PetGameMorphPartner with property
    // mask 0x18; m_characterId != 0 is the two-player window, m_pets the partner's list (PETMORPHSET picks from it).
    [Fact]
    public void TheStartCarriesThePartnersWizardAndItsHatchablePets() {
        static WizClientObjectItem Pet(ulong id, int level) => new() {
            m_globalID = id, m_templateID = 1, m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = (byte) level }] };
        Assert.True(PetGameService.TryPrepareMorphPartner(4242, [Pet(1, 1), Pet(2, 3), Pet(3, 5)], out var data));
        var reader = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(reader.Deserialize<PetGameMorphPartner>((byte[]) data,
            PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var partner));
        Assert.Equal(4242ul, partner!.m_characterId.Full);
        Assert.Equal([2ul, 3ul], partner.m_pets.Select(p => p.m_globalID.Full).Order());    // Adult (3) or older only
    }
}
