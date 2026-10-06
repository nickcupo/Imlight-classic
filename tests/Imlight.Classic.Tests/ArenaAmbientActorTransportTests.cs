// CLASSIC: exercise the production actor factory and endpoint, which a fake matchmaker world cannot cover.
using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Ambient;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaAmbientActorTransportTests {
    private sealed record Transfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER Message, IActorRef Sender);

    private sealed class ServerProbe : ReceiveActor {
        public ServerProbe(Channel<Transfer> received) => Receive<ZONE_102_PROTOCOL.MSG_ZONETRANSFER>(message =>
            received.Writer.TryWrite(new Transfer(message, Sender)));
    }

    [Fact]
    public async Task ProductionActorStartsAndSendsThePrivateArenaTransferFromItsRealEndpoint() {
        EquipmentAttachConcurrencyTests.Configure();
        using var system = ActorSystem.Create("arena-ambient-transport", "akka.actor.provider = local");
        var transfers = Channel.CreateUnbounded<Transfer>();
        var server = system.ActorOf(Props.Create(() => new ServerProbe(transfers)), "server");
        const ulong charId = 0xA3B1E00000000199, runId = 0xE000000000000123;
        var identity = AmbientIdentity.Generate(3, "WizardCity/WC_Duel_Arena_New", new NameTableSizes(100, 100, 50, 50), (20, 20));
        var wizard = new Wizard { CharId = charId };
        var ambient = new AmbientWizard(AmbientWizardRecord.From(identity, charId), wizard) { Zone = identity.HomeZone };
        var entry = new ArenaAmbientParticipants.Entry(ambient);
        var actor = system.ActorOf(ArenaAmbientParticipant.Props(entry, server), "participant");
        entry.Actor = actor;
        try {
            // Tell immediately: Akka must finish the real actor's construction/PreStart before processing this trip.
            actor.Tell(new ArenaAmbientParticipant.Trip("WizardCity/MB_Arena", "Start", runId));
            var transfer = await transfers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("WizardCity/MB_Arena", transfer.Message.DestinationZone);
            Assert.Equal("Start", transfer.Message.DestinationLocation);
            Assert.Equal(runId, transfer.Message.OwnerCharId);
            Assert.True(transfer.Message.IsPrivate);
            Assert.False(transfer.Message.ResetInstance);
            Assert.False(transfer.Message.SendToClient);
            Assert.Equal(ambient.Endpoint, transfer.Sender);
            Assert.Equal(actor, ambient.Driver);
            Assert.True(AmbientWizards.TryGet(transfer.Sender, out var registered));
            Assert.Same(ambient, registered);
            Assert.True(ActiveWizardDirectory.TryGet(transfer.Sender, out var active, out _));
            Assert.Same(wizard, active);
            var answer = await transfer.Sender.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(wizard, answer.Wizard);
        }
        finally {
            await system.Terminate();
            AmbientWizards.Unregister(ambient);
            ActiveWizardDirectory.Remove(ambient.Endpoint);
        }
    }
}
