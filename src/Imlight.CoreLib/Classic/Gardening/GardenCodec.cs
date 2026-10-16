using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Housing;

namespace Imlight.CoreLib.Classic.Gardening;

// CLASSIC: the native garden blob. GardeningCompression (r806919 0x141d9c180 writer / 0x141d9c740 reader), short
// form (param 1, as ClientBlobPatcher and the housing blob loader use it):
//   u32 magic 0xDECADE02, u16 version 4, u16 count, then per plant:
//   u32 packed XY (sign-magnitude 16 bit each, x high), f32 z, u32 template, u32 flags,
//   and unless flags & 1 (a soil pile): u32 pest, u32 pest protection, u32 next stage time, u32 stage start, u32 harvest time.
internal static class GardenCodec {
    internal static byte[] Compress(GardenLedger ledger) {
        var plants = ledger.Plants.Take(GardenRules.MaxPlants).ToList();
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(GardenRules.ShortMagic);
        w.Write(GardenRules.Version);
        w.Write((ushort)plants.Count);
        foreach (var p in plants) {
            w.Write(((uint)HousingCodec.PackAxis(p.X) << 16) | HousingCodec.PackAxis(p.Y));
            w.Write(p.Z);
            w.Write(p.TemplateId);
            w.Write(p.Flags);
            if (p.IsSoil) continue;
            w.Write(p.PestTemplateId);
            w.Write(p.PestProtectionTemplateId);
            w.Write(p.NextStageAt);
            w.Write(p.StageStart);
            w.Write(p.HarvestAt);
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>The native reader's view (for tests and audits): version 4 short form only.</summary>
    internal static List<(float X, float Y, float Z, uint Template, uint Flags, uint NextStage, uint StageStart, uint Harvest)> Decompress(byte[] data) {
        using var r = new BinaryReader(new MemoryStream(data, writable: false));
        if (r.ReadUInt32() != GardenRules.ShortMagic || r.ReadUInt16() != GardenRules.Version) throw new InvalidDataException("Not a v4 short garden.");
        var count = r.ReadUInt16();
        var list = new List<(float, float, float, uint, uint, uint, uint, uint)>();
        static float Axis(uint v) => v < 0x8000 ? v : -(float)(v - 0x8000);
        for (var i = 0; i < count; i++) {
            var xy = r.ReadUInt32(); var z = r.ReadSingle(); var t = r.ReadUInt32(); var f = r.ReadUInt32();
            uint next = 0, start = 0, harvest = 0;
            if ((f & GardenRules.FlagSoil) == 0) { r.ReadUInt32(); r.ReadUInt32(); next = r.ReadUInt32(); start = r.ReadUInt32(); harvest = r.ReadUInt32(); }
            list.Add((Axis(xy >> 16), Axis(xy & 0xffff), z, t, f, next, start, harvest));
        }
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing garden bytes.");
        return list;
    }

    internal static BlobRequest Manifest(GardenLedger ledger, ulong zoneId) => new() {
        m_type = GardenRules.BlobType, m_associatedGID = zoneId,
        m_blobRequestObjectList = [new BlobRequestObject {
            m_subType = GardenRules.SubType, m_versionNumber = ledger.Version, m_packageNumber = ledger.PackageNumber,
            m_userData = GardenRules.PackageUserData, m_objectCount = 1,
        }],
    };

    /// <summary>
    /// The serialized Garden blob. HousingBlobCustomObject.m_customData is binary on the client (std::string), but the
    /// generated C# property is a UTF-8 string, so a same-length ASCII marker is serialized and replaced byte for byte.
    /// </summary>
    internal static ByteString Blob(GardenLedger ledger, ulong zoneId) {
        var data = Compress(ledger);
        var marker = new string('G', data.Length);
        var blob = new Blob {
            m_type = GardenRules.BlobType, m_subType = GardenRules.SubType, m_versionNumber = ledger.Version,
            m_packageNumber = ledger.PackageNumber, m_associatedGID = zoneId, m_epochDays = 0,
            m_data = new HousingBlob { m_housingBlobObjectList = [new HousingBlobCustomObject {
                m_gameObjectTemplateID = GardenRules.ProxyTemplate, m_positionXY = 0, m_positionZ = 0, m_yaw = 0,
                m_extraData1 = 0, m_customData = marker,
            }] },
        };
        byte[] encoded = HousingCodec.Encode(blob);
        var markerBytes = Encoding.ASCII.GetBytes(marker);
        var at = IndexOf(encoded, markerBytes);
        if (at < 0 || IndexOf(encoded, markerBytes, at + 1) >= 0 && markerBytes.Length > 0)
            throw new InvalidDataException("Garden blob marker not found exactly once.");
        Buffer.BlockCopy(data, 0, encoded, at, data.Length);
        return encoded;
    }

    /// <summary>Accepts only a client request for exactly this ledger's Garden package.</summary>
    internal static bool AcceptRequest(ByteString data, ulong zoneId, GardenLedger ledger) {
        byte[] bytes = data;
        if (bytes is null || bytes.Length > 256 || ledger is null) return false;
        try {
            using var r = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.ASCII);
            if (r.ReadUInt32() != new BlobRequest().GetHash() || Name(r) != GardenRules.BlobType || r.ReadUInt64() != zoneId) return false;
            if (r.ReadUInt32() != 1) return false;
            if (r.ReadUInt32() != new BlobRequestObject().GetHash() || Name(r) != GardenRules.SubType) return false;
            r.ReadUInt32(); // cached version is not authority
            if (r.ReadInt32() != ledger.PackageNumber || r.ReadUInt32() != GardenRules.PackageUserData || r.ReadUInt32() > 1) return false;
            return r.BaseStream.Position == r.BaseStream.Length;
        }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>The proxy's object id: native slot | type 9 &lt;&lt; 40 | dynamic proc &lt;&lt; 48 (as furniture).</summary>
    internal static ulong ProxyId(uint dynamicProc) => HousingRules.PlacedGlobalId(GardenRules.PackageUserData, dynamicProc);

    private static string Name(BinaryReader r) {
        var size = r.ReadUInt16();
        if (size > 16) throw new EndOfStreamException();
        var b = r.ReadBytes(size);
        if (b.Length != size) throw new EndOfStreamException();
        return Encoding.ASCII.GetString(b);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0) {
        if (needle.Length == 0) return -1;
        for (var i = start; i <= haystack.Length - needle.Length; i++) {
            var ok = true;
            for (var j = 0; j < needle.Length && ok; j++) ok = haystack[i + j] == needle[j];
            if (ok) return i;
        }
        return -1;
    }
}
