/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * LAUNCHER FILE LIST
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the LatestFileList.bin KingsIsle's own launcher (the 2009-2014
 * WizardLauncher.exe, run against our server) downloads after it signs in.
 * It lists the launcher's packages with no files in them, so the launcher
 * patches nothing: it never "updates" the pinned r806919 install or itself.
 * Our own updater (tools/mac/classic-update.py) brings the classic patches.
 *
 * The format is KingsIsle's binary record list, read from the 2014 file:
 *   per table: u32 record count, a schema record, then the records;
 *   a record: 0x02, kind (1 schema, 2 data), u16 total length, values;
 *   a schema: per field u16 name length, name, u8 type (9 STR, 3 UINT),
 *             u8 0x28; then u16 length and the table's name;
 *   values: STR as u16 length + bytes, UINT as u32 (little-endian).
 * Tables: _TableList (the names), About (Version), and each package's
 * table (SrcFileName ... HeaderCRC), here empty.
 *
 * NOTE:
 * The CRC the patch server sends with it is the server's own CRC-32
 * (init 0, no final xor), the same one PatchServer uses for the real list.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Imlight.CoreLib.Shared.Cryptography;

namespace Imlight.CoreLib.Classic.Launcher;

internal static class LauncherFileList {

    internal const string FileName = "LatestFileList.bin";

    /// <summary>The packages the launcher patches: its own files, the game's base, and nothing else.</summary>
    internal static readonly string[] Packages = ["Base", "PatchClient"];

    private const byte Str = 9, UInt = 3, FieldFlags = 0x28;

    private static readonly (string Name, byte Type)[] FileFields = [
        ("SrcFileName", Str), ("TarFileName", Str), ("FileType", UInt), ("Size", UInt), ("HeaderSize", UInt),
        ("CompressedHeaderSize", UInt), ("CRC", UInt), ("HeaderCRC", UInt), ("_TargetTable", Str),
    ];

    private static readonly Lazy<byte[]> s_bytes = new(() => Build(Packages));

    /// <summary>The file, built once.</summary>
    internal static byte[] Bytes => s_bytes.Value;

    /// <summary>Its CRC as the patch server reports it.</summary>
    internal static uint Crc => Crc32.Calculate(0, Bytes);

    /// <summary>A list with these packages, each empty.</summary>
    internal static byte[] Build(IReadOnlyList<string> packages) {
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);

        // _TableList: one Name per table.
        w.Write((uint) (packages.Count + 1));
        WriteSchema(w, [("Name", Str), ("_TargetTable", Str)], "_TableList");
        WriteRecord(w, Text("About"));
        foreach (var package in packages) WriteRecord(w, Text(package));

        // About: Version 1.
        w.Write(1u);
        WriteSchema(w, [("Version", UInt), ("_TargetTable", Str)], "About");
        WriteRecord(w, BitConverter.GetBytes(1u));

        // One empty file table per package.
        foreach (var package in packages) {
            w.Write(0u);
            WriteSchema(w, FileFields, package);
        }

        w.Flush();
        return output.ToArray();
    }

    private static byte[] Text(string value) {
        var bytes = Encoding.ASCII.GetBytes(value);
        var result = new byte[bytes.Length + 2];
        BitConverter.GetBytes((ushort) bytes.Length).CopyTo(result, 0);
        bytes.CopyTo(result, 2);
        return result;
    }

    private static void WriteSchema(BinaryWriter w, (string Name, byte Type)[] fields, string table) {
        using var body = new MemoryStream();
        foreach (var (name, type) in fields) {
            body.Write(Text(name));
            body.WriteByte(type);
            body.WriteByte(FieldFlags);
        }
        body.Write(Text(table));
        WriteFramed(w, 1, body.ToArray());
    }

    private static void WriteRecord(BinaryWriter w, byte[] values) => WriteFramed(w, 2, values);

    private static void WriteFramed(BinaryWriter w, byte kind, byte[] body) {
        w.Write((byte) 2);
        w.Write(kind);
        w.Write((ushort) (body.Length + 4));
        w.Write(body);
    }

    /// <summary>The table names in a list file (the first table), for tests and checks.</summary>
    internal static List<string> TableNames(byte[] data) {
        var names = new List<string>();
        var count = BitConverter.ToUInt32(data, 0);
        var at = 4;
        for (var i = 0; i <= count; i++) {
            var length = BitConverter.ToUInt16(data, at + 2);
            if (data[at] != 2) throw new InvalidDataException("not a record at " + at);
            if (data[at + 1] == 2) {
                var textLength = BitConverter.ToUInt16(data, at + 4);
                names.Add(Encoding.ASCII.GetString(data, at + 6, textLength));
            }
            at += length;
        }
        return names;
    }
}
