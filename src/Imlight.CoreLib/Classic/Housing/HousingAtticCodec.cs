using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic.Housing;

internal static class HousingAtticCodec {
    // Native AtticLoaded0x140f320b0 consumes ordinary HousingBlobObject with zero position,
    // and indexes it by UserData+slot. The Attic map retains this synthetic id until a delete.
    internal static BlobRequest Manifest(AtticLedger attic) => new() {
        m_type = HousingRules.BlobType, m_associatedGID = attic.ContainerId,
        m_blobRequestObjectList = attic.Packages.ConvertAll(p => new BlobRequestObject {
            m_subType = HousingRules.AtticSubType, m_versionNumber = p.Version,
            m_packageNumber = p.PackageNumber, m_userData = p.UserData, m_objectCount = (uint)p.Entries.Count,
        }),
    };

    internal static Blob Blob(AtticLedger attic, AtticPackage p) => new() {
        m_type = HousingRules.BlobType, m_subType = HousingRules.AtticSubType,
        m_versionNumber = p.Version, m_packageNumber = p.PackageNumber,
        m_associatedGID = attic.ContainerId, m_epochDays = 0,
        m_data = new HousingBlob { m_housingBlobObjectList = p.Entries.ConvertAll(e => new HousingBlobObject {
            m_gameObjectTemplateID = e.Removed ? 0 : e.TemplateId,
        }) },
    };

    // Never feed untrusted PropertyClass data into the generic decoder. Accept only the
    // exact owner container/packages we issued, bounded counts, known hashes and no suffix.
    internal static bool TryRequests(ByteString data, AtticLedger attic, out List<int> packages) {
        packages = [];
        byte[] bytes = data;
        if (!attic.Valid() || bytes is null || bytes.Length > 256) return false;
        try {
            using var stream = new MemoryStream(bytes, false);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            if (r.ReadUInt32() != new BlobRequest().GetHash() || Name(r) != HousingRules.BlobType
                || r.ReadUInt64() != attic.ContainerId) return false;
            var count = r.ReadUInt32();
            if (count == 0 || count > HousingRules.AtticPackageCount) return false;
            for (var i = 0; i < count; i++) {
                if (r.ReadUInt32() != new BlobRequestObject().GetHash() || Name(r) != HousingRules.AtticSubType) return false;
                r.ReadUInt32(); // client cached version; reply uses the committed version
                var number = r.ReadInt32();
                var index = attic.Packages.FindIndex(p => p.PackageNumber == number);
                if (index < 0 || packages.Contains(index) || r.ReadUInt32() != attic.Packages[index].UserData
                    || r.ReadUInt32() > HousingRules.PackageSlots) return false;
                packages.Add(index);
            }
            return stream.Position == stream.Length;
        }
        catch (EndOfStreamException) { packages = []; return false; }
    }

    // Native PickUpAll sender0x140f55830 serializes HousingItemList, non-versionable,
    // mask4. Its sole flags7 field is count:uint32 then exactly that many placed GIDs.
    internal static bool TryExceptions(ByteString data, HousingLedger room, uint dynamicProc, out HashSet<int> excluded) {
        excluded = [];
        byte[] bytes = data;
        if (room is null || bytes is null || bytes.Length < 8 || bytes.Length > 8 + HousingRules.PackageSlots * 8) return false;
        try {
            using var stream = new MemoryStream(bytes, false);
            using var r = new BinaryReader(stream);
            if (r.ReadUInt32() != new HousingItemList().GetHash()) return false;
            var count = r.ReadUInt32();
            if (count > HousingRules.PackageSlots || bytes.Length != 8 + count * 8) return false;
            for (var i = 0; i < count; i++) {
                if (!HousingRules.TrySlot(r.ReadUInt64(), dynamicProc, room.Entries.Count, out var slot)
                    || !room.Active(slot) || !excluded.Add(slot)) return false;
            }
            return stream.Position == stream.Length;
        }
        catch (EndOfStreamException) { excluded = []; return false; }
    }

    private static string Name(BinaryReader r) {
        var size = r.ReadUInt16();
        if (size > 16) throw new EndOfStreamException();
        var bytes = r.ReadBytes(size);
        if (bytes.Length != size) throw new EndOfStreamException();
        return Encoding.ASCII.GetString(bytes);
    }
}
