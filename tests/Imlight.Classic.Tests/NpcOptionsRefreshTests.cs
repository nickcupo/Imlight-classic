// CLASSIC: r806919 asks an NPC for its CURRENT options with MSG_INTERACTNPC {GlobalID, ServiceName "", ServiceIndex 0,
// Reinteract}: 500 ms after an NPC dialog closes (FUN_14172ca20 / FUN_14172ca40 drop its cached options, Reinteract 2),
// on CancelInteraction (1) and after the server's MSG_REINTERACT. It expects MSG_SENDNPCOPTIONS with Reinteract echoed;
// with 2 and a quest offer or turn-in listed it opens that dialog (a talk-back goal's chain, e.g. KT-SPH2-C02-007 at
// General Khaba). The server used to treat every empty service name as "close my shop" and stop there, so the NPC
// showed no or stale options until the wizard walked away and back (live bug 24).

using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class NpcOptionsRefreshTests {

    [Theory]
    [InlineData(2)]   // after a dialog closed
    [InlineData(1)]   // CancelInteraction
    [InlineData(3)]   // a MSG_REINTERACT type, echoed
    public void AnEmptyServiceWithReinteractAsksForTheNpcsOptions(int reinteract) {
        Assert.True(InteractService.IsOptionsRequest("", reinteract, 4242));
        Assert.True(InteractServiceMementoComponent.IsOptionsRequest("", reinteract));
    }

    [Fact]
    public void ClosingAShopOrAClickIsNotAnOptionsRequest() {
        Assert.False(InteractService.IsOptionsRequest("", 0, 4242));               // closing a shop
        Assert.False(InteractService.IsOptionsRequest("", 2, 0));                  // no NPC named
        Assert.False(InteractService.IsOptionsRequest("QuestPersonaGoalService", 2, 4242)); // a click on an option
        Assert.False(InteractServiceMementoComponent.IsOptionsRequest("", 0));
        Assert.False(InteractServiceMementoComponent.IsOptionsRequest("WizShoppingService", 2));
    }

    // qa-s146: Olde News handed in to Sergeant Muldoon, who then has three mainline offers. r806919 re-asks (Reinteract
    // 2) and opens the services menu; it sends no MSG_INTERACTAVAILABLEQUEST with more than one chain entry. The server's
    // own hand-in timer then routed QuestOfferService index 0 with Reinteract 2 and opened "A Giant Problem" on top.
    [Theory]
    [InlineData("QuestOfferService", 2)]       // the hand-in timer's interaction
    [InlineData("QuestPersonaGoalService", 2)]
    [InlineData("QuestOfferService", 1)]
    [InlineData("", 2)]                        // the client's own re-ask
    public void AClassicReaskOnlyGetsTheOptionsBack(string service, int reinteract)
        => Assert.True(InteractServiceMementoComponent.OnlyAnswersOptions(service, reinteract, classicEngine: true));

    [Fact]
    public void AClickStillActsAndTheStockEngineKeepsItsReinteract() {
        Assert.False(InteractServiceMementoComponent.OnlyAnswersOptions("QuestOfferService", 0, classicEngine: true));  // a click
        Assert.False(InteractServiceMementoComponent.OnlyAnswersOptions("QuestOfferService", 2, classicEngine: false)); // stock
        Assert.True(InteractServiceMementoComponent.OnlyAnswersOptions("", 2, classicEngine: false));
    }

    [Fact]
    public void OnlyTheStockEngineOpensTheNextOfferAfterAHandIn() {
        Assert.False(InteractPersonaGoalComponent.ServerChainsAfterHandIn(classicEngine: true)); // r806919 chains itself
        Assert.True(InteractPersonaGoalComponent.ServerChainsAfterHandIn(classicEngine: false));
    }
}
