using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic.Housing;

internal static class HousingCodec {
    // r806919 ClientBlobCache::MSG_SendBlob uses plain, non-versionable SerializerBinary,
    // flags0 and property mask1. CoreObjectSerializer's extra header is not valid here.
    internal static ByteString Encode(PropertyClass value) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(value, (PropertyFlags)1, out var data))
            throw new InvalidDataException("Housing property serialization failed.");
        return data;
    }

    internal static BlobRequest Manifest(HousingLedger ledger, ulong zoneId) => new() {
        m_type = HousingRules.BlobType, m_associatedGID = zoneId,
        m_blobRequestObjectList = Enumerable.Range(0, ledger.PackageCount).Select(index => new BlobRequestObject {
            m_subType = HousingRules.SubType, m_versionNumber = index == 0 ? ledger.Version : ledger.SecondVersion,
            m_packageNumber = index == 0 ? ledger.PackageNumber : ledger.SecondPackageNumber,
            m_userData = index == 0 ? 0 : HousingRules.SecondRoomUserData,
            m_objectCount = (uint)ledger.PackageEntries(index).Count(),
        }).ToList(),
    };

    internal static Blob Blob(HousingLedger ledger, ulong zoneId, int packageIndex = 0) => new() {
        m_type = HousingRules.BlobType, m_subType = HousingRules.SubType,
        m_versionNumber = packageIndex == 0 ? ledger.Version : ledger.SecondVersion,
        m_packageNumber = packageIndex == 0 ? ledger.PackageNumber : ledger.SecondPackageNumber,
        m_associatedGID = zoneId, m_epochDays = 0,
        m_data = new HousingBlob { m_housingBlobObjectList = ledger.PackageEntries(packageIndex).Select(Pack).ToList() },
    };

    internal static HousingBlobObject Pack(HousingEntry entry) {
        if (entry.Removed) return new HousingBlobObject(); // stable holes preserve all other object ids
        if (!HousingRules.ValidPosition(entry.X, entry.Y, entry.Z, entry.Yaw))
            throw new InvalidDataException("Invalid saved housing coordinates.");
        var result = new HousingBlobObject {
            m_gameObjectTemplateID = entry.TemplateId, m_positionXY = ((uint)PackAxis(entry.X) << 16) | PackAxis(entry.Y),
            m_positionZ = entry.Z,
        };
        var yaw = entry.Yaw % MathF.Tau;
        if (yaw < 0) yaw += MathF.Tau;
        // Native HousingBlobObject::SetPosition at 0x141d9d8a0/0x141d9d9bf stores integer
        // degrees for extended yaw; it does not divide a full turn into 512 angular steps.
        // Preserve native mulss(180) THEN divss(pi), not multiplication by a precomputed
        // reciprocal: 315*pi/180 is 314.99997 with the reciprocal but exactly315 natively.
        var degrees = Math.Min(359, (int)(yaw * 180.0f / MathF.PI));
        if (degrees % 45 == 0) {
            result.m_yaw = degrees switch { 0 => 0, 90 => 1, 180 => 2, 270 => 3, 45 => 4, 135 => 5, 225 => 6, _ => 7 };
        }
        else {
            result.m_gameObjectTemplateID |= 0x80000000u;
            if (degrees >= 256) result.m_gameObjectTemplateID |= 0x40000000u;
            result.m_extraData1 = ((uint)degrees & 0xff) << 16;
        }
        return result;
    }

    internal static ushort PackAxis(float value) {
        var rounded = (int)(value >= 0 ? value + 0.5f : value - 0.5f);
        return (ushort)(rounded < 0 ? 0x8000 - rounded : rounded);
    }

    // Decode only the documented Housing/Proxy request shape, with length/count/hash checks
    // before allocation. The generic PropertyClass decoder accepts arbitrary class hashes and
    // unbounded vector counts, so it must not receive an untrusted network blob.
    internal static bool AcceptRequest(ByteString data, ulong zoneId, int package) {
        var ledger = new HousingLedger { PackageNumber = package };
        return TryRequests(data, zoneId, ledger, out var packages) && packages.Count == 1 && packages[0] == 0;
    }

    internal static bool TryRequests(ByteString data, ulong zoneId, HousingLedger ledger, out List<int> packages) {
        packages = [];
        byte[] bytes = data;
        if (bytes is null || bytes.Length > 256) return false;
        try {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != new BlobRequest().GetHash() || ReadName(reader) != HousingRules.BlobType
                || reader.ReadUInt64() != zoneId) return false;
            var count = reader.ReadUInt32();
            if (count == 0 || count > ledger.PackageCount) return false;
            for (var i = 0; i < count; i++) {
                if (reader.ReadUInt32() != new BlobRequestObject().GetHash() || ReadName(reader) != HousingRules.SubType) return false;
                reader.ReadUInt32(); // cached version is not authority
                var package = reader.ReadInt32(); var userData = reader.ReadUInt32();
                var index = package == ledger.PackageNumber && userData == 0 ? 0
                    : ledger.PackageCount == 2 && package == ledger.SecondPackageNumber && userData == HousingRules.SecondRoomUserData ? 1 : -1;
                if (index < 0 || packages.Contains(index) || reader.ReadUInt32() > HousingRules.PackageSlots) return false;
                packages.Add(index);
            }
            return stream.Position == stream.Length;
        }
        catch (EndOfStreamException) { return false; }
    }

    private static string ReadName(BinaryReader reader) {
        var size = reader.ReadUInt16();
        if (size > 16) throw new EndOfStreamException();
        var data = reader.ReadBytes(size);
        if (data.Length != size) throw new EndOfStreamException();
        return Encoding.ASCII.GetString(data);
    }
}
