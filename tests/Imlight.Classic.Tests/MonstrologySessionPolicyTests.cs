using System;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
namespace Imlight.Classic.Tests;
public sealed class MonstrologySessionPolicyTests {
    [Fact] public void CapabilityBitOneUsesExistingStockExtensionFlagsField() {
        var flags = 1u | new MonstrologySessionPolicy().Advertise(true);
        var frame = MessageEncoder.Encode(new EnhancedClassicProtocol.Capabilities { Flags=flags });
        var decoded = new EnhancedClassicProtocol.Capabilities();
        decoded.Decode(new BitReader(frame.AsSpan(12,6).ToArray()));
        Assert.Equal(EnhancedClassicProtocol.Version,decoded.ProtocolVersion);
        Assert.Equal(3u,decoded.Flags);
    }
    [Fact] public void OrdinarySessionDefaultsEnhancedButGlobalOffNeverAdvertisesOrRuns() {
        var policy = new MonstrologySessionPolicy();
        Assert.Equal(2u,policy.Advertise(true));
        Assert.Equal(0u,policy.Advertise(false));
        Assert.Equal(MonstrologyResult.Rejected,policy.WithPermission(false,()=>throw new Exception("Must not run")));
    }
    [Theory] [InlineData((ushort)1,true)] [InlineData((ushort)0,false)] [InlineData((ushort)2,false)]
    public void StrictOrUnknownHelloRejectsMutationAndExtractionBeforeRepositoryAccess(ushort version,bool strict) {
        var policy = new MonstrologySessionPolicy();
        policy.Negotiate(version,strict);
        Assert.False(policy.Allows(true)); Assert.Equal(0u,policy.Advertise(true));
        Assert.Equal(MonstrologyResult.Rejected,policy.WithPermission(true,()=>throw new Exception("Must not mutate")));
        var award = new ExtractionAward("strict",42,35085,1,10,true,true,true);
        Assert.Equal(MonstrologyResult.Rejected,MonstrologyContracts.CommitExtraction(null!,true,policy,42,award,new[]{0,10}));
    }
    [Fact] public void ReconnectGetsIndependentPolicyAndBindingTracksItsHello() {
        var wizard = new Wizard { CharId=42 };
        Assert.False(MonstrologySessionPolicy.AllowsWizard(wizard,true));
        var old = new MonstrologySessionPolicy();
        MonstrologySessionPolicy.Bind(wizard,old);
        Assert.True(MonstrologySessionPolicy.AllowsWizard(wizard,true));
        old.Negotiate(EnhancedClassicProtocol.Version,true);
        Assert.False(MonstrologySessionPolicy.AllowsWizard(wizard,true));
        var reconnect = new MonstrologySessionPolicy();
        MonstrologySessionPolicy.Bind(wizard,reconnect);
        Assert.True(MonstrologySessionPolicy.AllowsWizard(wizard,true));
        Assert.False(old.Allows(true));
        reconnect.Negotiate(EnhancedClassicProtocol.Version,true);
        Assert.False(MonstrologySessionPolicy.AllowsWizard(wizard,true));
    }
    [Theory] [InlineData(0u)] [InlineData(1u)]
    public void CapabilityBitOneDoesNotChangeExistingMinionBit(uint minion) {
        var policy = new MonstrologySessionPolicy();
        var combined = minion | policy.Advertise(true);
        Assert.Equal(minion,combined & 1u); Assert.Equal(2u,combined & 2u);
        policy.Negotiate(EnhancedClassicProtocol.Version,true);
        Assert.Equal(minion,minion | policy.Advertise(true));
    }
}
