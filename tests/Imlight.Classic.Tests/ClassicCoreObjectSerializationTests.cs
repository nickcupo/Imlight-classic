// CLASSIC: independently inspect the native fallback header; symmetric codec round trips hid the defect.
using System;
using System.Buffers.Binary;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicCoreObjectSerializationTests {
    private const ulong Character = (1UL << 40) + 1337;

    [Fact]
    public void UnmappedLadderUsesItsRealTypeHashWithoutChangingItsTemplateOrBody() {
        var ladder = new Ladder { m_characterID = Character, m_score = 701 };
        var legacy = new BitWriter(); new CoreObjectSerializer().PreWriteObject(legacy, ladder);
        Assert.Equal(new byte[6], legacy.GetData()); // A non-null body followed this false null pointer.
        var writer = new BitWriter(); Assert.True(new ClassicCoreObjectSerializer().PreWriteObject(writer, ladder));
        var header = writer.GetData(); Assert.Equal(6, header.Length); Assert.Equal(0, header[0]); Assert.Equal(0, header[1]);
        Assert.Equal(ladder.GetHash(), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2)));
        Assert.Equal(0UL, ladder.m_templateID.Full); Assert.Equal(Character, ladder.m_characterID.Full);
        var reader = new BitReader(header);
        Assert.Equal(PreloadResult.Success, new ClassicCoreObjectSerializer().PreloadObject(reader, out var decoded));
        Assert.IsType<Ladder>(decoded);
    }

    [Fact]
    public void FalseNullLadderBodyPlacesCharacterLowBitsAtTheReportedResistanceVectorCount() {
        var stats = new WizGameStats { m_pArenaLadder = new Ladder { m_characterID = Character } };
        Assert.True(new CoreObjectSerializer(false, SerializerFlags.None).Serialize(stats, 24, out var original));
        var raw = (byte[])original;
        // Native compact WizGameStats: six-byte root header,16 int32 scalars,empty charge vector,two floats.
        const int ladderHeader = 6 + 16 * 4 + 4 + 2 * 4;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(ladderHeader + 2, 4)));
        // Following two null pointers,10 scalars and four empty vectors consume the hidden68-byte CoreObject base.
        const int consumedFollowingStats = 2 * 6 + 10 * 4 + 4 * 4;
        Assert.Equal(unchecked((uint)Character), BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(ladderHeader + 6 + consumedFollowingStats, 4)));
        Assert.True(new ClassicCoreObjectSerializer(false, SerializerFlags.None).Serialize(stats, 24, out var fixedData));
        var repaired = (byte[])fixedData;
        Assert.Equal(stats.m_pArenaLadder.GetHash(), BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(ladderHeader + 2, 4)));
        Assert.Equal(raw.Length, repaired.Length); // Header correction alone, with no new or removed properties.
        Assert.Equal(raw[..(ladderHeader + 2)], repaired[..(ladderHeader + 2)]);
        Assert.Equal(raw[(ladderHeader + 6)..], repaired[(ladderHeader + 6)..]);
    }

    [Theory] [InlineData(false, 24u)] [InlineData(true, 24u)] [InlineData(false, 28u)] [InlineData(true, 28u)]
    public void RankedCharacterStatsAndFollowingVectorsRoundTripInAttachAndRenderFormats(bool compressed, uint mask) {
        var codec = new ClassicCoreObjectSerializer(false, compressed ? SerializerFlags.Compress : SerializerFlags.None);
        var player = new WizClientObject { m_templateID = 1, m_characterId = Character, m_globalID = Character + 2,
            m_permID = Character + 2, m_gameStats = new WizGameStats {
                m_currentHitpoints = 115, m_baseHitpoints = 115, m_currentMana = 30,
                m_currentArenaPoints = 44, m_currentPvPCurrency = 33,
                m_pArenaLadder = new Ladder { m_characterID = Character, m_gameNameID = 782091, m_score = 701,
                    m_gamesWon = 12, m_gamesLost = 4, m_gamesPlayed = 16 },
                m_dmgReducePercent = [.125f, .25f], m_accBonusPercent = [.75f], m_dmgReduceFlat = [3f],
            } };
        Assert.True(codec.Serialize(player, mask, out var bytes));
        Assert.True(codec.Deserialize<WizClientObject>((byte[])bytes, mask, out var loaded));
        Assert.Equal(Character, loaded!.m_characterId.Full); Assert.Equal(Character + 2, loaded.m_globalID.Full);
        var loadedStats = loaded.m_gameStats;
        if (mask == 28) {
            // Player.m_gameStats is private (flags24), so render mask28 intentionally omits it.
            Assert.Null(loadedStats);
            Assert.True(codec.Serialize(player.m_gameStats, mask, out var statsBytes));
            Assert.True(codec.Deserialize<WizGameStats>((byte[])statsBytes, mask, out loadedStats));
        }
        Assert.Equal(115, loadedStats!.m_currentHitpoints); Assert.Equal(44, loadedStats.m_currentArenaPoints);
        Assert.Equal(Character, loadedStats.m_pArenaLadder.m_characterID.Full);
        Assert.Equal(701, loadedStats.m_pArenaLadder.m_score); Assert.Equal(16, loadedStats.m_pArenaLadder.m_gamesPlayed);
        Assert.Null(loadedStats.m_pDerbyLadder); Assert.Null(loadedStats.m_bracketLader);
        Assert.Equal(player.m_gameStats.m_dmgReducePercent, loadedStats.m_dmgReducePercent);
        Assert.Equal(player.m_gameStats.m_accBonusPercent, loadedStats.m_accBonusPercent);
        Assert.Equal(player.m_gameStats.m_dmgReduceFlat, loadedStats.m_dmgReduceFlat);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void ExistingMappedNativeCoreHeadersRemainByteExact(int kind) {
        CoreObject value = kind switch { 0 => new ClientObject(), 1 => new WizClientObject(), 2 => new WizClientObjectItem(),
            3 => new WizClientPet(), 4 => new WizClientMount(), 5 => new ClientReagentItem(), _ => new ClientRecipe() };
        value.m_templateID = 782091;
        var original = new BitWriter(); new CoreObjectSerializer().PreWriteObject(original, value);
        var repaired = new BitWriter(); new ClassicCoreObjectSerializer().PreWriteObject(repaired, value);
        Assert.Equal(original.GetData(), repaired.GetData()); Assert.NotEqual(0, repaired.GetData()[0]);
    }

    [Fact]
    public void NormalObjectAndNullHeadersKeepTheirNativeSixByteEncoding() {
        var value = new WizGameStats(); var original = new BitWriter(); new CoreObjectSerializer().PreWriteObject(original, value);
        var repaired = new BitWriter(); new ClassicCoreObjectSerializer().PreWriteObject(repaired, value);
        Assert.Equal(original.GetData(), repaired.GetData());
        var empty = new BitWriter(); Assert.False(new ClassicCoreObjectSerializer().PreWriteObject(empty, null!));
        Assert.Equal(new byte[6], empty.GetData());
        Assert.Equal(PreloadResult.NullHash, new ClassicCoreObjectSerializer().PreloadObject(new BitReader(empty.GetData()), out var decoded));
        Assert.Null(decoded);
    }
}
