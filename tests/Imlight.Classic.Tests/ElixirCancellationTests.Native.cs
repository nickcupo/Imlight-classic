using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed partial class ElixirCancellationTests {
    [Fact]
    public async Task NativeTrashHandlerRejectsSpoofAndOrdinaryGearThenConsumesTheConfirmedActiveOriginalOnce() {
        using var fixture = new Fixture();
        fixture.ApplyEffects();
        fixture.Wizard.EquipmentBehavior.ForceEquipItem(new WizClientObjectItem {
            m_globalID = 9200, m_characterId = 42, m_templateID = 191103,
        });
        using var system = ActorSystem.Create("elixir-trash-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var packets = Channel.CreateUnbounded<IMessage>();
        var socket = system.ActorOf(Props.Create(() => new SocketProbe(packets)), "socket");
        var session = system.ActorOf(Props.CreateBy(new SessionProducer(socket)), "session");
        var instance = await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
        var actor = system.ActorOf(Props.Create(() => new InventoryProbe(instance, fixture.Store, fixture.Wizard)), "inventory");
        try {
            async Task<IMessage[]> Trash(ulong id, ulong template = 0) {
                Assert.True(await actor.Ask<bool>(new TrashStep(new GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM {
                    GlobalID = id, TemplateID = template,
                }), Timeout, TestContext.Current.CancellationToken));
                await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                await socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
                var got = new List<IMessage>();
                while (packets.Reader.TryRead(out var packet)) got.Add(packet);
                return [.. got];
            }
            var saves = fixture.Store.Saves;
            Assert.Empty(await Trash(9100, ulong.MaxValue));
            Assert.Empty(await Trash(9200));
            Assert.Equal(saves, fixture.Store.Saves);
            var cleanup = await Trash(9100);
            Assert.Equal(4, cleanup.Length);
            Assert.IsType<WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER>(cleanup[0]);
            Assert.IsType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(cleanup[1]);
            Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(cleanup[2]);
            Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM>(cleanup[3]);
            Assert.Empty(await Trash(9100));
            Assert.Equal(saves + 1, fixture.Store.Saves);
            Assert.NotNull(fixture.Wizard.EquipmentBehavior.GetItem(9200));
            Assert.NotNull(fixture.Wizard.EquipmentBehavior.GetItem(9101));
            Assert.Equal(9575, fixture.Wizard.Account.Crowns);
            Assert.DoesNotContain(9100ul, fixture.Wizard.InventoryBehavior.InventoryItemIds);
            Assert.Equal(0f, fixture.Wizard.GameStats.m_dmgBonusPercentAll, 5);
            Assert.Equal(.15f, fixture.Wizard.GameStats.m_accBonusPercentAll, 5);
        }
        finally { await system.Terminate(); }
    }

    private sealed record TrashStep(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM Packet);
    private sealed class InventoryProbe : InventoryService {
        private readonly ElixirTests.Store _store;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public InventoryProbe(SessionActor session, ElixirTests.Store store, Wizard wizard) : base(session) {
            _store = store;
            typeof(MessageService).GetField("_cachedWizard", Flags)!.SetValue(this, wizard);
            typeof(MessageService).GetField("_cachedWizardGameObject", Flags)!.SetValue(this, wizard.GameObject ?? new WizClientObject());
        }
        protected override void ConfigureReceivers() {
            Receive<TrashStep>(step => {
                try {
                    using var scope = _store.Scope();
                    typeof(InventoryService).GetMethod("ReceiveTrashInventoryItem", Flags)!.Invoke(this, [step.Packet]);
                    Sender.Tell(true);
                }
                catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            base.ConfigureReceivers();
        }
    }
}
