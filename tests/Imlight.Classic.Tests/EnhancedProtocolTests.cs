using System;
using System.Buffers.Binary;
using System.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class EnhancedProtocolTests {
    [Fact] public void HelloRoundTrips() {
        var messages = EnhancedMessageDecoder.Decode(MessageEncoder.Encode(new EnhancedClassicProtocol.Hello { StrictClassic = true }));
        var hello = Assert.IsType<EnhancedClassicProtocol.Hello>(Assert.Single(messages));
        Assert.True(hello.StrictClassic); Assert.Equal((ushort)1, hello.ProtocolVersion);
    }
    [Fact] public void OrderRetainsEveryIdentityAndRawTarget() {
        var source = new EnhancedClassicProtocol.MinionRequest { DuelID = ulong.MaxValue, MinionID = 345, Round = 12, RequestID = 54, MoveType = 2, SpellSelection = 6, SpellTarget = 7 };
        var actual = Assert.IsType<EnhancedClassicProtocol.MinionRequest>(Assert.Single(EnhancedMessageDecoder.Decode(MessageEncoder.Encode(source))));
        Assert.Equal(source.DuelID, actual.DuelID); Assert.Equal(source.MinionID, actual.MinionID);
        Assert.Equal(source.Round, actual.Round); Assert.Equal(source.RequestID, actual.RequestID);
        Assert.Equal(source.SpellTarget, actual.SpellTarget); Assert.Equal(source.SpellSelection, actual.SpellSelection);
    }
    [Theory] [InlineData((ushort)0)] [InlineData((ushort)255)] [InlineData((ushort)256)] [InlineData((ushort)273)] [InlineData((ushort)65535)]
    public void ClientKeepAliveDecodesAtAnySessionAge(ushort minutes) {
        // The official client's KeepAlive frame has no trailing zero: its last byte is the high byte of the minutes.
        var frame = MessageEncoder.Encode(new ControlMessageProtocol.KeepAlive { SessionId = 17390, Milliseconds = 500, ElapsedSessionTime = minutes });
        var keepAlive = Assert.IsType<ControlMessageProtocol.KeepAlive>(Assert.Single(EnhancedMessageDecoder.Decode(frame)));
        Assert.Equal(minutes, keepAlive.ElapsedSessionTime);

        var client = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(client, 0xF00D);
        BinaryPrimitives.WriteUInt16LittleEndian(client.AsSpan(2), 10);
        client[4] = 1; client[5] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(client.AsSpan(8), 17390);
        BinaryPrimitives.WriteUInt16LittleEndian(client.AsSpan(10), 500);
        BinaryPrimitives.WriteUInt16LittleEndian(client.AsSpan(12), minutes);
        var fromClient = Assert.IsType<ControlMessageProtocol.KeepAlive>(Assert.Single(EnhancedMessageDecoder.Decode(client)));
        Assert.Equal(minutes, fromClient.ElapsedSessionTime);
    }
    [Fact] public void StockMessageStillDecodes() {
        var actual = EnhancedMessageDecoder.Decode(MessageEncoder.Encode(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE { MoveType = 1, SpellSelection = 3, SpellTarget = 32, TimeLeft = 10 }));
        var move = Assert.IsType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE>(Assert.Single(actual));
        Assert.Equal((byte)3, move.SpellSelection); Assert.Equal((uint)32, move.SpellTarget);
    }
    [Fact] public void MixedRecordsStayInOrder() {
        var a = MessageEncoder.Encode(new EnhancedClassicProtocol.Hello());
        var b = MessageEncoder.Encode(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE());
        var frame = new byte[a.Length + b.Length - 9];
        a.AsSpan(0, a.Length - 1).CopyTo(frame);
        b.AsSpan(8).CopyTo(frame.AsSpan(a.Length - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)(frame.Length - 4));
        var messages = EnhancedMessageDecoder.Decode(frame);
        Assert.Collection(messages, m => Assert.IsType<EnhancedClassicProtocol.Hello>(m), m => Assert.IsType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE>(m));
    }
    [Theory] [InlineData(0)] [InlineData(3)] [InlineData(65000)]
    public void InvalidRecordLengthRejected(ushort size) {
        var frame = MessageEncoder.Encode(new EnhancedClassicProtocol.Hello());
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), size);
        Assert.Throws<InvalidDataException>(() => EnhancedMessageDecoder.Decode(frame));
    }
    [Fact] public void InvalidProfileFlagRejected() {
        var frame = MessageEncoder.Encode(new EnhancedClassicProtocol.Hello()); frame[14] = 2;
        Assert.Throws<InvalidDataException>(() => EnhancedMessageDecoder.Decode(frame));
    }
    [Fact] public void TrailingBodyDataRejected() {
        var original = MessageEncoder.Encode(new EnhancedClassicProtocol.Hello());
        var frame = new byte[original.Length + 1]; original.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)(frame.Length - 4));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), 8);
        Assert.Throws<InvalidDataException>(() => EnhancedMessageDecoder.Decode(frame));
    }
    [Fact] public void ServerResponseCannotBeInjectedByClient() {
        Assert.Throws<InvalidDataException>(() => EnhancedMessageDecoder.Decode(MessageEncoder.Encode(new EnhancedClassicProtocol.Capabilities { Flags = 1 })));
        Assert.Throws<InvalidDataException>(() => EnhancedMessageDecoder.Decode(MessageEncoder.Encode(new EnhancedClassicProtocol.MinionState())));
    }
    [Fact] public void OversizedStateCannotEncode() {
        Assert.Throws<InvalidDataException>(() => MessageEncoder.Encode(new EnhancedClassicProtocol.MinionState { Payload = new string('x', 24001) }));
    }
}
