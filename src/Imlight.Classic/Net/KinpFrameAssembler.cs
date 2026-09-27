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
 * KINP FRAMING
 * ========================================================================
 *
 * PURPOSE:
 * Cuts a TCP byte stream into whole KINP frames. One socket read can hold
 * several frames, or only part of one; the rest waits for the next read.
 *
 * USAGE EXAMPLE:
 * frames.Append(buffer.AsSpan(0, bytesRead));
 * for (var read = frames.Next(); read.Status != KinpReadStatus.Incomplete; read = frames.Next()) { ... }
 *
 * NOTE:
 * A frame is u16 0xF00D, u16 length (bytes after the length field), then
 * the body. A length of 0x8000 or more marks a large frame: a u32 follows
 * and counts every byte after itself. Bytes that do not start with the
 * magic are skipped up to the next magic. After an oversized frame the
 * assembler holds and returns nothing more.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Buffers.Binary;

namespace Imlight.Classic.Net;

/// <summary>
/// What <see cref="KinpFrameAssembler.Next"/> found at the front of the stream.
/// </summary>
public enum KinpReadStatus {

    /// <summary>
    /// No whole frame is buffered yet.
    /// </summary>
    Incomplete,

    /// <summary>
    /// A whole frame with a 16-bit length.
    /// </summary>
    Frame,

    /// <summary>
    /// A whole frame with the 0x8000 marker and a 32-bit length.
    /// </summary>
    LargeFrame,

    /// <summary>
    /// Bytes before the next frame magic were dropped.
    /// </summary>
    Skipped,

    /// <summary>
    /// A frame announced more than the maximum size. Nothing after it can be trusted, so every later
    /// call returns <see cref="Incomplete"/>.
    /// </summary>
    Oversized,

}

/// <summary>
/// One result of <see cref="KinpFrameAssembler.Next"/>.
/// </summary>
/// <param name="Status">What was found.</param>
/// <param name="Frame">The whole frame, magic included, for <see cref="KinpReadStatus.Frame"/> and <see cref="KinpReadStatus.LargeFrame"/>; empty otherwise.</param>
/// <param name="ByteCount">The frame's size, the bytes skipped, or the size an oversized frame announced.</param>
public readonly record struct KinpRead(KinpReadStatus Status, byte[] Frame, long ByteCount);

/// <summary>
/// Reassembles KINP frames from socket reads.
/// </summary>
public sealed class KinpFrameAssembler {

    /// <summary>
    /// 64 KiB: every frame with a 16-bit length fits, with room for a modest large frame.
    /// </summary>
    public const int DefaultMaxFrameSize = 0x10000;

    private const byte MagicLow = 0x0D;
    private const byte MagicHigh = 0xF0;
    private const int SmallHeaderLength = 4;
    private const int LargeHeaderLength = 8;
    private const ushort LargeFrameMarker = 0x8000;

    private readonly int _maxFrameSize;
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _count;
    private bool _refused;

    /// <summary>
    /// Creates an empty assembler.
    /// </summary>
    /// <param name="maxFrameSize">The largest frame accepted, header included.</param>
    public KinpFrameAssembler(int maxFrameSize = DefaultMaxFrameSize) {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameSize, LargeHeaderLength);
        _maxFrameSize = maxFrameSize;
    }

    /// <summary>
    /// Bytes received but not yet returned as a frame.
    /// </summary>
    public int BufferedBytes => _count;

    /// <summary>
    /// Adds the bytes of one socket read.
    /// </summary>
    /// <param name="data">The bytes read.</param>
    public void Append(ReadOnlySpan<byte> data) {
        if (data.IsEmpty || _refused) {
            return;
        }

        if (_start + _count + data.Length > _buffer.Length) {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
            _start = 0;
            if (_count + data.Length > _buffer.Length) {
                Array.Resize(ref _buffer, Math.Max(_count + data.Length, _buffer.Length * 2));
            }
        }

        data.CopyTo(_buffer.AsSpan(_start + _count));
        _count += data.Length;
    }

    /// <summary>
    /// Takes the next whole frame, or reports why there is none. Call until it returns
    /// <see cref="KinpReadStatus.Incomplete"/>, which it always does after <see cref="KinpReadStatus.Oversized"/>.
    /// </summary>
    /// <returns>The frame, skipped bytes, an oversized announcement, or <see cref="KinpReadStatus.Incomplete"/>.</returns>
    public KinpRead Next() {
        if (_refused || _count < 2) {
            return Incomplete();
        }

        var pending = _buffer.AsSpan(_start, _count);
        if (pending[0] != MagicLow || pending[1] != MagicHigh) {
            return Skip(BytesBeforeNextMagic(pending));
        }

        if (_count < SmallHeaderLength) {
            return Incomplete();
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(pending[2..]);
        var isLarge = length >= LargeFrameMarker;
        long frameSize;
        if (isLarge) {
            if (_count < LargeHeaderLength) {
                return Incomplete();
            }

            frameSize = LargeHeaderLength + (long) BinaryPrimitives.ReadUInt32LittleEndian(pending[4..]);
        }
        else {
            frameSize = SmallHeaderLength + length;
        }

        if (frameSize > _maxFrameSize) {
            _refused = true;
            Consume(_count);

            return new KinpRead(KinpReadStatus.Oversized, [], frameSize);
        }
        if (_count < frameSize) {
            return Incomplete();
        }

        var frame = pending[..(int) frameSize].ToArray();
        Consume(frame.Length);

        return new KinpRead(isLarge ? KinpReadStatus.LargeFrame : KinpReadStatus.Frame, frame, frame.Length);
    }

    private static int BytesBeforeNextMagic(ReadOnlySpan<byte> pending) {
        for (var i = 1; i < pending.Length - 1; i++) {
            if (pending[i] == MagicLow && pending[i + 1] == MagicHigh) {
                return i;
            }
        }

        // A trailing 0x0D may be the first half of a magic that the next read completes.
        return pending[^1] == MagicLow ? pending.Length - 1 : pending.Length;
    }

    private static KinpRead Incomplete() => new(KinpReadStatus.Incomplete, [], 0);

    private KinpRead Skip(int byteCount) {
        Consume(byteCount);

        return new KinpRead(KinpReadStatus.Skipped, [], byteCount);
    }

    private void Consume(int byteCount) {
        _start += byteCount;
        _count -= byteCount;
        if (_count == 0) {
            _start = 0;
        }
    }

}
