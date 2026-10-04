using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Inventory;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>CLASSIC: the wizard's bank (ServerWizStorageBehavior): what is saved, and what the client gets.</summary>
public sealed class BankStorageTests {

    private static WizClientObjectItem Item(ulong id, uint template = 1000)
        => new() { m_globalID = id, m_templateID = template };

    [Fact]
    public void Adding_and_removing_keeps_the_saved_ids_in_step() {
        var bank = new ServerWizStorageBehavior();
        Assert.True(bank.AddItem(Item(5)));
        Assert.True(bank.AddItem(Item(6)));
        Assert.False(bank.AddItem(Item(5)));
        Assert.Equal([5UL, 6UL], bank.BankItemIds);
        Assert.True(bank.HasItem(6));

        Assert.True(bank.RemoveItem(5, out var removed));
        Assert.Equal(5UL, removed.m_globalID.Full);
        Assert.False(bank.RemoveItem(5, out _));
        Assert.Equal([6UL], bank.BankItemIds);
        Assert.Equal([6UL], bank.Items.Select(i => i.m_globalID.Full));
    }

    [Fact]
    public void The_character_document_keeps_the_bank_ids_not_the_items() {
        var bank = new ServerWizStorageBehavior();
        bank.AddItem(Item(77));
        var json = JsonConvert.SerializeObject(bank);
        Assert.Contains("BankItemIds", json);
        Assert.DoesNotContain("m_templateID", json);

        var loaded = JsonConvert.DeserializeObject<ServerWizStorageBehavior>(json)!;
        Assert.Equal([77UL], loaded.BankItemIds);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public void A_character_saved_before_the_bank_loads_with_an_empty_one() {
        var wizard = JsonConvert.DeserializeObject<Wizard>("{\"CharId\": 12}")!;
        Assert.NotNull(wizard.StorageBehavior);
        Assert.Empty(wizard.StorageBehavior.BankItemIds);
    }

    [Fact]
    public void The_client_gets_the_bank_items_and_both_sizes() {
        var bank = new ServerWizStorageBehavior();
        bank.AddItem(Item(1));
        bank.AddItem(Item(2));
        var client = bank.GetClientBehaviorInstance();
        Assert.Equal(Banking.ClassicBankSize, client.m_bankLimit);
        Assert.Equal(Banking.ClassicSharedBankSize, client.m_sharedBankLimit);
        Assert.Equal(2, client.m_itemList.Count);
    }
}
