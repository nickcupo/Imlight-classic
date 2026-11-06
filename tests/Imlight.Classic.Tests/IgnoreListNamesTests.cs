// CLASSIC: r806919 reads IgnoreEntryData.m_ignoreName as the packed name bytes (BuddyListManager's MSG_IgnoreList
// handler 0x141745e70 copies it into a BuddyEntry; GetName 0x141738520 decodes it with the NameCodec). A string
// property's UTF-8 turned the gender byte into EF BF BD (ios-parity, playbot-ios finbot01, 2026-11-06).

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class IgnoreListNamesTests {

    private static readonly byte[] Tiffany = [0x80, 128, 66, 12];
    private static readonly byte[] Adam = [0x82, 7, 200, 31];

    private static IgnoreEntryDataList List(params ulong[] charIds) => new() {
        m_ignoreDataList = [.. charIds.Select(id => new IgnoreEntryData {
            m_ignoreName = "x", m_characterID = id, m_gameObjectID = id + 1 })],
    };

    private static int IndexOf(byte[] buffer, byte[] part) {
        for (var i = 0; i + part.Length <= buffer.Length; i++) {
            if (buffer.AsSpan(i, part.Length).SequenceEqual(part)) return i;
        }

        return -1;
    }

    [Fact]
    public void EachNameGoesOutAsItsPackedBytesInListOrder() {
        var list = List(5419509891798596965, 4957263571689881072);
        Assert.True(IgnoreListNames.TrySerialize(list, [Tiffany, Adam], 1, out var bytes));

        var tiffany = IndexOf(bytes, Tiffany);
        var adam = IndexOf(bytes, Adam);
        Assert.True(tiffany >= 0 && adam > tiffany);
        Assert.Equal(-1, IndexOf(bytes, [0xEF, 0xBF, 0xBD]));

        var reader = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(reader.Deserialize<IgnoreEntryDataList>(bytes, 1u, out var read));
        Assert.Equal([5419509891798596965ul, 4957263571689881072ul],
            read!.m_ignoreDataList.Select(e => e.m_characterID.Full));
        Assert.Equal(["x", "x"], list.m_ignoreDataList.Select(e => e.m_ignoreName)); // the list itself is left as it was
    }

    [Fact]
    public void AnEmptyListStillSerializes() {
        Assert.True(IgnoreListNames.TrySerialize(List(), [], 1, out var bytes));
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void ItRefusesRatherThanSendAWrongList() {
        // A character id made of stand-in bytes ('~' is 0x7E) would take a name's place; the read-back check refuses it.
        Assert.False(IgnoreListNames.TrySerialize(List(0x7E7E7E7E7E7E7E7E, 1), [Tiffany, Adam], 1, out _));
        Assert.False(IgnoreListNames.TrySerialize(List(1, 2), [Tiffany], 1, out _));
        Assert.False(IgnoreListNames.TrySerialize(List(1), [Array.Empty<byte>()], 1, out _));
    }
}
