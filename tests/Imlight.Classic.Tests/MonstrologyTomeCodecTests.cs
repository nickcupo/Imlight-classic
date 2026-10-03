using System;
using System.Collections.Generic;
using System.IO;
using Imlight.CoreLib.Game.Monstrology;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Xunit;
namespace Imlight.Classic.Tests;
public sealed class MonstrologyTomeCodecTests {
    [Fact] public void BytesMatchObservedParserReadsAndStockDmlPreservesBinary() {
        var data = MonstrologyTomeCodec.Encode(new Dictionary<uint,int> { [0x01020304] = 0x0506 });
        Assert.Equal(new byte[] { 0xed,0x0d,0xec,0x1d,1,0,0,0,1,0,4,3,2,1,6,5 }, data);
        var source = new WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME { GlobalID = 42, MonsterData = new ByteString(data) };
        var decoded = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_REQUESTMONSTERTOME>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(source))!));
        Assert.Equal(data, (byte[])decoded.MonsterData);
        Assert.Equal(0x0506, MonstrologyTomeCodec.Decode((byte[])decoded.MonsterData)[0x01020304]);
    }
    [Fact] public void EmptyAndBoundaryCountsRoundTripWithoutTextConversion() {
        Assert.Empty(MonstrologyTomeCodec.Decode(MonstrologyTomeCodec.Encode(new Dictionary<uint,int>())));
        var values = new Dictionary<uint,int> { [uint.MaxValue] = ushort.MaxValue, [1] = 0 };
        Assert.Equal(values.Count, MonstrologyTomeCodec.Decode(MonstrologyTomeCodec.Encode(values)).Count);
        Assert.Throws<InvalidDataException>(() => MonstrologyTomeCodec.Encode(new Dictionary<uint,int> { [1] = 65536 }));
    }
    [Fact] public void WrongMagicVersionTruncatedTrailingAndDuplicateEntriesRefused() {
        var valid = MonstrologyTomeCodec.Encode(new Dictionary<uint,int> { [1] = 2, [2] = 3 });
        foreach (var index in new[] { 0, 4, 8 }) {
            var bad = (byte[])valid.Clone(); bad[index] ^= 0x80;
            Assert.Throws<InvalidDataException>(() => MonstrologyTomeCodec.Decode(bad));
        }
        Assert.Throws<InvalidDataException>(() => MonstrologyTomeCodec.Decode(valid.AsSpan(0, valid.Length-1)));
        var trailing = new byte[valid.Length+1]; valid.CopyTo(trailing,0);
        Assert.Throws<InvalidDataException>(() => MonstrologyTomeCodec.Decode(trailing));
        valid[16] = 1;
        Assert.Throws<InvalidDataException>(() => MonstrologyTomeCodec.Decode(valid));
    }
    [Fact] public void EssenceUsesGeneratedTypeMaskFiveAndNormalRawStockStr() {
        var bytes = MonstrologyContracts.EncodeEssence(42, new ExtractionReceipt(123,3,15));
        var serializer = new ObjectSerializer(Behaviors: SerializerFlags.None);
        Assert.True(serializer.Deserialize<CollectedEssenceTrackingList>(bytes, 5, out var tracking));
        Assert.Equal(3, tracking.m_collectedEssenceCount);
        var entry = Assert.Single(tracking.m_essenceTrackingList);
        Assert.Equal(42UL,entry.m_ownerGID.Full); Assert.Equal(123U,entry.m_templateID);
        var source = new WIZARD2_53_PROTOCOL.MSG_UPDATECOLLECTEDESSENCES { EssenceData = new ByteString(bytes) };
        var decoded = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATECOLLECTEDESSENCES>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(source))!));
        Assert.Equal(bytes,(byte[])decoded.EssenceData);
    }
}
