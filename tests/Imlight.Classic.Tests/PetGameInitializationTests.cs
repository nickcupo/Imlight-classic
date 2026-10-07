using System;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Pet;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PetGameInitializationTests {
    [Fact]
    public void CompactPayloadHasNativeTypeEnvelopeAndOrderedMetadata() {
        var info = AuthoredInfo();
        Assert.True(PetGameInitializationCodec.TryPrepare(info, out var data));
        Assert.True(data.Length > 4);

        // Read the compact native field sequence independently of ObjectSerializer.Decode.
        using var reader = new BitReader((byte[])data);
        Assert.Equal(1926567985U, reader.ReadUInt32()); // PetGameInfo type, with no Bind/flags/size header.
        Assert.True(reader.ReadBit());
        Assert.Equal("AuthoredGame", reader.ReadString().ToString());
        Assert.Equal(2U, reader.ReadUInt32());
        Assert.Equal(1640127799U, reader.ReadUInt32()); // PetGameEnergyCost.
        Assert.Equal((byte)2, reader.ReadUInt8());
        Assert.Equal((byte)7, reader.ReadUInt8());
        Assert.Equal(1640127799U, reader.ReadUInt32());
        Assert.Equal((byte)4, reader.ReadUInt8());
        Assert.Equal((byte)9, reader.ReadUInt8());

        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(466661542U, reader.ReadUInt32()); // PetStatModificationSet.
        Assert.Equal("AuthoredTrack", reader.ReadString().ToString());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(2077268920U, reader.ReadUInt32()); // PetStatModification.
        Assert.Equal("AuthoredStat", reader.ReadString().ToString());
        Assert.Equal(-3, reader.ReadInt32());
        Assert.Equal(0x80ff0102U, reader.ReadUInt32());
        Assert.Equal("AuthoredScene", reader.ReadString().ToString());
        Assert.Equal(2U, reader.ReadUInt32());
        Assert.Equal(17, reader.ReadInt32());
        Assert.Equal(-11, reader.ReadInt32());

        Assert.Equal("AuthoredGameIcon", reader.ReadString().ToString());
        Assert.Equal(2U, reader.ReadUInt32());
        Assert.Equal("AuthoredTrackIconA", reader.ReadString().ToString());
        Assert.Equal("AuthoredTrackIconB", reader.ReadString().ToString());
        Assert.Equal(2U, reader.ReadUInt32());
        Assert.Equal("AuthoredTipA", reader.ReadString().ToString());
        Assert.Equal("AuthoredTipB", reader.ReadString().ToString());
        Assert.Equal(data.Length * 8, reader.BitPos());

        // Serialization supplies existing values; it does not recalculate costs or rewards.
        Assert.Equal((byte)7, info.m_energyCosts[0].m_cost);
        Assert.Equal(-3, info.m_trackChoices[0].m_modifications[0].m_change);
        Assert.Equal(0x80ff0102U, info.m_trackChoices[0].m_modifications[0].m_actualChange);
    }

    [Theory]
    [InlineData("PetGameDance")]
    [InlineData("PetGameDrop")]
    [InlineData("PetGameCannon")]
    [InlineData("PetGameMaze")]
    public void StockInitPacketPreservesRawBinaryAndExistingFields(string game) {
        Assert.True(PetGameInitializationCodec.TryPrepare(AuthoredInfo(), out var data));
        var source = new PET_9_PROTOCOL.MSG_PETGAMEINIT {
            Game = game, Data = data, MinLevel = 0, Track = 3,
        };
        var decoded = Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(
            Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(source))!));
        Assert.Equal(game, decoded.Game.ToString());
        Assert.Equal((byte)0, decoded.MinLevel);
        Assert.Equal((byte)3, decoded.Track);
        Assert.Equal((byte[])data, (byte[])decoded.Data);
        // The authored type hash/stat value contain bytes that would be damaged by UTF-8 conversion.
        Assert.NotEqual((byte[])data, (byte[])new ByteString(data.ToString()!));
    }

    [Fact]
    public void UsesExactlyTheNativeModeAndPropertyMask() {
        var info = new ModeCheckingInfo();
        Assert.True(PetGameInitializationCodec.TryPrepare(info, out var data));
        Assert.Equal(false, info.Versionable);
        Assert.Equal(SerializerFlags.None, info.Flags);
        Assert.Equal(PropertyFlags.Prop_Save | PropertyFlags.Prop_Public, info.Mask);
        Assert.True(data.Length > 4);
    }

    [Fact]
    public void NullInfoRefusesPreparationWithoutPayload() {
        Assert.False(PetGameInitializationCodec.TryPrepare(null!, out var data));
        Assert.Equal(0, data.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrThrowingEncodeDiscardsPartialPayload(bool throws) {
        Assert.False(PetGameInitializationCodec.TryPrepare(new FailingInfo(throws), out var data));
        Assert.Equal(0, data.Length);
    }

    private static PetGameInfo AuthoredInfo() => new() {
        m_isWebGame = true,
        m_name = "AuthoredGame",
        m_energyCosts = [
            new PetGameEnergyCost { m_rank = 2, m_cost = 7 },
            new PetGameEnergyCost { m_rank = 4, m_cost = 9 },
        ],
        m_trackChoices = [new PetStatModificationSet {
            m_name = "AuthoredTrack",
            m_modifications = [new PetStatModification {
                m_name = "AuthoredStat", m_change = -3, m_actualChange = 0x80ff0102,
            }],
            m_scene = "AuthoredScene", m_gameScoreFactor = [17, -11],
        }],
        m_gameIcon = "AuthoredGameIcon",
        m_trackIcons = ["AuthoredTrackIconA", "AuthoredTrackIconB"],
        m_trackToolTips = ["AuthoredTipA", "AuthoredTipB"],
    };

    private sealed record ModeCheckingInfo : PetGameInfo {
        internal bool? Versionable { get; private set; }
        internal SerializerFlags Flags { get; private set; }
        internal PropertyFlags Mask { get; private set; }
        public override bool Encode(BitWriter writer, ObjectSerializer serializer) {
            Versionable = serializer.Versionable;
            Flags = serializer.SerializerFlags;
            Mask = serializer.PropertyMask;
            return base.Encode(writer, serializer);
        }
    }

    private sealed record FailingInfo(bool Throws) : PetGameInfo {
        public override bool Encode(BitWriter writer, ObjectSerializer serializer) {
            writer.WriteUInt32(0x80ff0102);
            if (Throws) throw new InvalidOperationException("Authored serialization failure");
            return false;
        }
    }
}
