// CLASSIC: exercise real shop stock publication; native layout and currency are separate fields.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaNativeShopLayoutTests {
    private sealed class Recorder : ReceiveActor {
        public Recorder(Channel<WIZARD_12_PROTOCOL.MSG_SHOPLIST> packets)
            => Receive<WIZARD_12_PROTOCOL.MSG_SHOPLIST>(packet => packets.Writer.TryWrite(packet));
    }

    [Theory]
    [InlineData(164327u, 6, 1)]
    [InlineData(38226u, 0, 1)]
    [InlineData(730100u, 0, 0)]
    public async Task ActualStockPacketUsesNativeLayoutWithoutChangingCurrencyOrPurchaseGate(uint npc, int layout, int currency) {
        EquipmentAttachConcurrencyTests.Configure();
        var previousArena = ClassicArena.Config;
        var cache = (ConcurrentDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var previous = new Dictionary<ulong, CoreTemplate?>();
        using var system = ActorSystem.Create("native-shop-layout-" + npc, "akka.actor.provider = local");
        try {
            ClassicRuntime.ResetForTests();
            ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
            ClassicArenaGearTemplates.Initialize(ClassicDataFixture.Root, "october-2010-arc1");
            ClassicArena.UseForTests(ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-october-2010.yaml")));
            var inventory = ClassicArena.TicketInventory(npc)?.Inventory ?? [new GID(730101u)];
            foreach (var id in inventory) {
                previous[id.Full] = cache.TryGetValue(id.Full, out var old) ? old : null;
                cache[id.Full] = new WizItemTemplate { m_templateID = id.MParts.TemplateId, m_equipEffects = [] };
            }
            var entity = (ZoneEntity)RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
            CombatRegressionTests.SetProperty(entity, nameof(ZoneEntity.ActiveGameObject),
                new CoreObject { m_templateID = npc, m_globalID = 901777 });
            var component = new InteractVendorComponent(entity);
            if (currency == 1) component.OnStart();
            else typeof(InteractVendorComponent).GetField("_inventory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, inventory);
            var packets = Channel.CreateUnbounded<WIZARD_12_PROTOCOL.MSG_SHOPLIST>();
            var recorder = system.ActorOf(Props.Create(() => new Recorder(packets)));
            CombatRegressionTests.Invoke(component, "SendShopOfferings", recorder);
            var packet = await packets.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(901777UL, packet.GlobalID);
            // Real client dbcd40 deserializes bare SerializerBinary with mask5; server publishes mask4.
            var codec = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
            Assert.True(codec.Deserialize<WizShopOffering>((byte[])packet.Data, (PropertyFlags)5, out var offering));
            Assert.Equal(layout, offering.m_furnitureShop);
            Assert.Equal(currency, offering.m_shopType);
            var expected = inventory.Where(id => !ClassicArenaGearTemplates.IsWithheld(id.MParts.TemplateId)).Select(id => id.Full).ToArray();
            Assert.Equal(expected, offering.m_shopList.Select(id => id.Full));
            if (npc == 164327) Assert.Equal(10, offering.m_shopList.Count);
            foreach (var id in inventory) Assert.Equal(expected.Contains(id.Full), component.HasItem(id));
            Assert.False(component.HasItem(new GID(730999u)));
            if (npc == 38226) foreach (var id in new uint[] { 100510, 100540, 164172 }) {
                Assert.DoesNotContain(offering.m_shopList, offered => offered.MParts.TemplateId == id);
                Assert.False(component.HasItem(new GID(id)));
            }
        }
        finally {
            await system.Terminate();
            foreach (var (id, old) in previous) {
                if (old is null) cache.TryRemove(id, out _); else cache[id] = old;
            }
            ClassicArena.UseForTests(previousArena);
            ClassicArenaGearTemplates.Initialize(null, "late-2009");
            ClassicRuntime.ResetForTests();
        }
    }
}
