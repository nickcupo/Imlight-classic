// CLASSIC: the native client returns each selected option's serialized m_serviceIndex, not its visual row.
using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class NpcShopOptionRoutingTests {
    private sealed class Silent : ReceiveActor;

    private sealed class Service(string name, params ServiceOptionBase[] options) : IServiceComponent {
        public string ServiceName => name;
        public string NpcIcon => "";
        public string NpcNameKey => "";
        public string NpcTextKey => "";
        public WizBangs WizBang => WizBangs.None;
        public string StateName => "";
        public string InteractWizBang => "";
        public string DisplayKey => "";
        public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _) => options;
        public void OnServiceInteraction(IActorRef actor, Wizard wizard, CoreObject obj, uint index) { }
    }

    [Theory]
    [InlineData(false)] // Zeke 44124: quest dialog plus Equipment.
    [InlineData(true)]  // Eloise 164322: Stitch Items plus Equipment.
    public void EquipmentAfterAnotherServiceReturnsItsOwnWireIndex(bool seamstress) {
        Service first = seamstress
            ? new("SeamstressService", new SeamstressOption { m_serviceName = "SeamstressService" })
            : new("QuestOfferService", new AvailableQuestEntry { m_serviceName = "QuestOfferService" });
        var equipment = new Service("WizShoppingService", new EquipmentShopOption { m_serviceName = "WizShoppingService" });
        var owners = new Dictionary<int, IServiceComponent>();
        var sent = InteractServiceMementoComponent.BuildServiceOptions([first, equipment], null!, owners);

        // Exercise the same property mask as MSG_SENDNPCOPTIONS, then build the click from the received option.
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Serialize(new ServiceMementoBase { m_serviceOptions = sent }, 4, out var bytes));
        Assert.True(serializer.Deserialize<ServiceMementoBase>(bytes, 4, out var received));
        var selected = Assert.IsType<EquipmentShopOption>(received.m_serviceOptions[1]);
        var click = new QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC {
            ServiceName = selected.m_serviceName,
            ServiceIndex = selected.m_serviceIndex,
        };
        Assert.Equal(1u, click.ServiceIndex);
        Assert.Equal(new uint[] { 0, 1 }, received.m_serviceOptions.Select(option => option.m_serviceIndex));

        using var system = ActorSystem.Create("npc-shop-wire-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var player = system.ActorOf(Props.Create<Silent>());
        var index = new PlayerOptionIndex();
        index.Set(player, owners);
        Assert.True(index.TryResolve(player, (int) click.ServiceIndex, click.ServiceName, out var resolved));
        Assert.Same(equipment, resolved);
        var other = received.m_serviceOptions[0];
        Assert.True(index.TryResolve(player, (int) other.m_serviceIndex, other.m_serviceName, out resolved));
        Assert.Same(first, resolved);
    }

    [Fact]
    public void AStaleIndexCannotDispatchToADifferentNamedService() {
        using var system = ActorSystem.Create("npc-shop-stale-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        var player = system.ActorOf(Props.Create<Silent>());
        var quest = new Service("QuestOfferService");
        var equipment = new Service("WizShoppingService");
        var index = new PlayerOptionIndex();
        index.Set(player, new Dictionary<int, IServiceComponent> { [0] = quest, [1] = equipment });

        Assert.False(index.TryResolve(player, 0, "WizShoppingService", out _));
        Assert.False(index.TryResolve(player, 1, "SeamstressService", out _));
        Assert.True(index.TryResolve(player, 1, "WizShoppingService", out var resolved));
        Assert.Same(equipment, resolved);
        // MSG_INTERACTOPTION carries only an index and uses this internal placeholder name.
        Assert.True(index.TryResolve(player, 1, "Interact", out resolved));
        Assert.Same(equipment, resolved);
        index.Remove(player);
        Assert.False(index.TryResolve(player, 1, "WizShoppingService", out _));
    }

    [Fact]
    public void FlatWireIndexesKeepMultipleQuestOrdinalsAndRebuildWithoutOldOwners() {
        var quests = new Service("QuestOfferService",
            new AvailableQuestEntry { m_serviceName = "QuestOfferService" },
            new AvailableQuestEntry { m_serviceName = "QuestOfferService" });
        var equipment = new Service("WizShoppingService", new EquipmentShopOption { m_serviceName = "WizShoppingService" });
        var owners = new Dictionary<int, IServiceComponent>();
        var options = InteractServiceMementoComponent.BuildServiceOptions([equipment, quests], null!, owners);
        Assert.Equal(new uint[] { 0, 1, 2 }, options.Select(option => option.m_serviceIndex));
        Assert.Equal(0, PlayerOptionIndex.Ordinal(owners, (int) options[1].m_serviceIndex));
        Assert.Equal(1, PlayerOptionIndex.Ordinal(owners, (int) options[2].m_serviceIndex));

        options = InteractServiceMementoComponent.BuildServiceOptions([equipment], null!, owners);
        Assert.Equal(0u, Assert.Single(options).m_serviceIndex);
        Assert.Same(equipment, Assert.Single(owners).Value);
    }
}
