using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Xunit;

namespace Imlight.Classic.Tests;

// REA audit 2026-10-17: hidden quests are kept in the string the client writes (Classic/ClientJournal.cs) and go
// back into the player object at login.
public sealed class ClientJournalTests {

    private static bool Handles(System.Type message) => Handles(typeof(QuestService), message);

    private static bool Handles(System.Type service, System.Type message)
        => service.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(m => m.GetCustomAttributes<MessageHandlerAttribute>().Any(a => a.MessageType == message));

    [Fact]
    public void TheJournalMessagesHaveHandlers() {
        Assert.True(Handles(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS)));
        // The tutorial-tip log is TutorialTipService's alone (it decodes the client's log format).
        Assert.True(Handles(typeof(TutorialTipService), typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP)));
        Assert.False(Handles(typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP)));
        Assert.False(Handles(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG)));
        Assert.False(Handles(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG)));
    }

    // A hidden-quests blob exactly as the r806919 client writes it (HiddenQuestsBehavior, read back in OnPostLoad
    // 0x141e83340): 0xFACEACE7, version 1, uint16 count, uint64 quest ids. Binary: zero bytes and 0xFA/0xCE/0xE7.
    private static readonly ulong[] s_ids = [0x4A7B_0000_0000_01F1UL, 0x0000_0000_0000_00FFUL, 0xE7CE_FA00_1234_5678UL];
    private static readonly byte[] s_blob = HiddenQuestsBlob.Write(s_ids);

    [Fact]
    public void TheBlobLayoutIsTheClients() {
        Assert.Equal(new byte[] { 0xE7, 0xAC, 0xCE, 0xFA, 1, 0, 0, 0, 3, 0 }, s_blob[..10]);
        Assert.Equal(10 + 8 * 3, s_blob.Length);
        Assert.Equal(s_ids, HiddenQuestsBlob.Read(s_blob));
    }

    [Fact]
    public void OnlyAWellFormedBlobIsKeptAndItIsKeptAsBytes() {
        // The message field is an Imcodec ByteString; the handler takes its bytes, never a string.
        Imcodec.IO.ByteString sent = s_blob;
        Assert.Equal(s_blob, ClientJournalStore.Accept((byte[]) sent));
        Assert.Equal(HiddenQuestsBlob.Write([]), ClientJournalStore.Accept(HiddenQuestsBlob.Write([])));

        Assert.Null(ClientJournalStore.Accept(null));
        Assert.Null(ClientJournalStore.Accept(s_blob[..^1]));                      // short
        Assert.Null(ClientJournalStore.Accept([.. s_blob, 0]));                     // long
        var badMagic = (byte[]) s_blob.Clone(); badMagic[0] ^= 1;
        Assert.Null(ClientJournalStore.Accept(badMagic));
        var badVersion = (byte[]) s_blob.Clone(); badVersion[4] = 2;
        Assert.Null(ClientJournalStore.Accept(badVersion));
        Assert.Null(ClientJournalStore.Accept(System.Text.Encoding.UTF8.GetBytes("text")));
        Assert.Equal("ClientJournals/42", ClientJournalStore.IdFor(42));
    }

    [Fact]
    public void TheSavedBlobGoesBackToTheClientByteForByte() {
        var hidden = new HiddenQuestsBehavior { m_behaviorTemplateNameID = 0x1A22DBF4 };
        var player = new WizClientObject { m_inactiveBehaviors = new List<BehaviorInstance> { new TutorialLogBehavior(), hidden } };
        var saved = ClientJournalStore.Accept(s_blob)!;

        WizardObjectLoader.SetJournalBehaviors(player, new ClientJournal { CharId = 7, HiddenQuestsData = saved });

        var sent = Assert.IsType<ClassicHiddenQuestsBehavior>(player.m_inactiveBehaviors[1]);
        Assert.Equal(hidden.GetHash(), sent.GetHash());
        Assert.Equal(0x1A22DBF4u, sent.m_behaviorTemplateNameID);

        // Serialized as the player object's behaviors are (not versionable): template name id, uint16 length, bytes.
        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);
        Assert.True(serializer.Serialize(sent, Imcodec.ObjectProperty.PropertyFlags.Prop_Transmit | Imcodec.ObjectProperty.PropertyFlags.Prop_AuthorityTransmit, out var bytes));
        byte[] data = bytes;
        var tail = data[^(2 + s_blob.Length)..];
        Assert.Equal(s_blob.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail));
        Assert.Equal(s_blob, tail[2..]);
        Assert.Equal(s_ids, HiddenQuestsBlob.Read(tail[2..])); // what OnPostLoad reads

        // The generated string property would have sent UTF-8 instead: the reason for the raw writer.
        Assert.NotEqual(s_blob, System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(s_blob)));
    }

    [Fact]
    public void AWizardWithoutAJournalKeepsTheTemplateDefaults() {
        var hidden = new HiddenQuestsBehavior { m_hiddenQuestsData = "template" };
        var player = new WizClientObject { m_inactiveBehaviors = new List<BehaviorInstance> { hidden } };

        WizardObjectLoader.SetJournalBehaviors(player, null);

        Assert.Equal("template", hidden.m_hiddenQuestsData);
    }

}
