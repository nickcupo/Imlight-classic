using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Imlight.CoreLib.Game.Monstrology;

internal static class MonstrologyTomeCodec {
    internal const uint Magic = 0x1DEC0DED;
    internal const uint Version = 1;
    // Stock DML STR length is a ushort. Inner count is also ushort, but bytes impose the tighter bound.
    internal const int MaximumEntries = (ushort.MaxValue - 10) / 6;
    internal static byte[] Encode(IReadOnlyDictionary<uint, int> entries) {
        if (entries.Count > MaximumEntries) throw new InvalidDataException("Tome exceeds stock STR capacity");
        var data = new byte[10 + entries.Count * 6];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), checked((ushort)entries.Count));
        var offset = 10;
        foreach (var entry in entries.OrderBy(x => x.Key)) {
            if (entry.Key == 0 || entry.Value < 0 || entry.Value > ushort.MaxValue)
                throw new InvalidDataException("Invalid tome entry");
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), entry.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 4), (ushort)entry.Value);
            offset += 6;
        }
        return data;
    }
    internal static Dictionary<uint, int> Decode(ReadOnlySpan<byte> data) {
        if (data.Length < 10 || data.Length > ushort.MaxValue
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) != Version)
            throw new InvalidDataException("Invalid tome identifier/version");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8));
        if (data.Length != 10 + count * 6) throw new InvalidDataException("Invalid tome length");
        var entries = new Dictionary<uint, int>();
        for (var i = 0; i < count; i++) {
            var offset = 10 + i * 6;
            var template = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
            var animus = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4));
            if (template == 0 || !entries.TryAdd(template, animus)) throw new InvalidDataException("Duplicate/invalid tome template");
        }
        return entries;
    }
}
