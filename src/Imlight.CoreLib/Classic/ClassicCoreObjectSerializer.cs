// CLASSIC: unmapped CoreObject subclasses use the native normal-object hash fallback.
using System;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;

namespace Imlight.CoreLib.Classic;

internal sealed class ClassicCoreObjectSerializer(bool versionable = false,
    SerializerFlags behaviors = SerializerFlags.Compress) : ObjectSerializer(versionable, behaviors) {
    private readonly CoreObjectSerializer _headers = new(versionable, behaviors);

    internal static ObjectSerializer Create(bool versionable = false, SerializerFlags behaviors = SerializerFlags.Compress)
        => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
            ? new ClassicCoreObjectSerializer(versionable, behaviors)
            : new CoreObjectSerializer(versionable, behaviors);

    public override bool PreWriteObject(BitWriter writer, PropertyClass value) {
        var temporary = new BitWriter();
        var result = _headers.PreWriteObject(temporary, value);
        var header = temporary.GetData();
        // Keep mapped native block/type/template headers byte-for-byte. Ladder derives CoreObject,
        // but has no mapped block/type: writing its default template0 otherwise announces a null
        // pointer followed by a real body, corrupting every following WizGameStats property.
        var fallback = value is CoreObject && header[0] == 0 && header[1] == 0;
        writer.WriteUInt8(header[0]); writer.WriteUInt8(header[1]);
        writer.WriteUInt32(fallback ? value.GetHash()
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4)));
        return result;
    }

    public override PreloadResult PreloadObject(BitReader reader, out PropertyClass value) {
        var block = reader.ReadUInt8(); var type = reader.ReadUInt8(); var hash = reader.ReadUInt32();
        if (block == 0 && type == 0 && hash == 0) { value = null; return PreloadResult.NullHash; }
        var header = new BitWriter(); header.WriteUInt8(block); header.WriteUInt8(type); header.WriteUInt32(hash);
        _headers.TypeRegistry = TypeRegistry; _headers.UseServerTypeRegistry = UseServerTypeRegistry;
        return _headers.PreloadObject(new BitReader(header.GetData()), out value);
    }
}
