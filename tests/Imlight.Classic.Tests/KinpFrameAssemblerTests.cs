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
 * KINP FRAMING TESTS
 * ========================================================================
 *
 * PURPOSE:
 * KinpFrameAssembler returns every whole frame of a read, keeps a partial
 * frame for the next read, and skips or refuses what is not a frame.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Net;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class KinpFrameAssemblerTests {

    [Fact]
    public void TwentyBackToBackMovesInOneReadAllComeOut() {
        var moves = Enumerable.Range(0, 20).Select(i => ClientMove((ushort) i)).ToList();
        var assembler = new KinpFrameAssembler();

        assembler.Append(moves.SelectMany(frame => frame).ToArray());
        var frames = Drain(assembler);

        Assert.Equal(20, frames.Count);
        Assert.All(frames, read => Assert.Equal(KinpReadStatus.Frame, read.Status));
        Assert.Equal(moves, frames.Select(read => read.Frame));
        Assert.Equal(0, assembler.BufferedBytes);
    }

    [Fact]
    public void PartialFrameWaitsForTheNextRead() {
        var first = ClientMove(1);
        var second = ClientMove(2);
        var assembler = new KinpFrameAssembler();

        assembler.Append([.. first, .. second[..5]]);
        var frames = Drain(assembler);

        Assert.Equal([first], frames.Select(read => read.Frame));
        Assert.Equal(5, assembler.BufferedBytes);

        assembler.Append(second[5..]);

        Assert.Equal([second], Drain(assembler).Select(read => read.Frame));
    }

    [Fact]
    public void FrameSplitAtEveryOffsetIsReassembled() {
        var frame = ClientMove(7);
        for (var split = 1; split < frame.Length; split++) {
            var assembler = new KinpFrameAssembler();

            assembler.Append(frame[..split]);
            Assert.Equal(KinpReadStatus.Incomplete, assembler.Next().Status);
            assembler.Append(frame[split..]);
            var read = assembler.Next();

            Assert.Equal(KinpReadStatus.Frame, read.Status);
            Assert.Equal(frame, read.Frame);
            Assert.Equal(KinpReadStatus.Incomplete, assembler.Next().Status);
        }
    }

    [Fact]
    public void RandomlyChunkedStreamKeepsEveryFrameInOrder() {
        var random = new Random(1264);
        var sent = Enumerable.Range(0, 300).Select(_ => SmallFrame(random.Next(0, 3000), random)).ToList();
        var stream = sent.SelectMany(frame => frame).ToArray();
        var assembler = new KinpFrameAssembler();
        var received = new List<byte[]>();

        for (var offset = 0; offset < stream.Length;) {
            var chunk = Math.Min(random.Next(1, 4096), stream.Length - offset);
            assembler.Append(stream.AsSpan(offset, chunk));
            offset += chunk;
            received.AddRange(Drain(assembler).Select(read => read.Frame));
        }

        Assert.Equal(sent, received);
        Assert.Equal(0, assembler.BufferedBytes);
    }

    [Fact]
    public void FrameLargerThanTheInitialBufferIsReassembled() {
        var frame = SmallFrame(0x7000, new Random(3));
        var assembler = new KinpFrameAssembler();

        for (var offset = 0; offset < frame.Length; offset += 1000) {
            assembler.Append(frame.AsSpan(offset, Math.Min(1000, frame.Length - offset)));
        }

        Assert.Equal([frame], Drain(assembler).Select(read => read.Frame));
    }

    [Fact]
    public void BytesBeforeTheMagicAreSkipped() {
        var frame = ClientMove(3);
        var assembler = new KinpFrameAssembler();

        assembler.Append([0x01, 0x02, 0x03, .. frame]);
        var reads = Drain(assembler);

        Assert.Equal(2, reads.Count);
        Assert.Equal(KinpReadStatus.Skipped, reads[0].Status);
        Assert.Equal(3, reads[0].ByteCount);
        Assert.Equal(frame, reads[1].Frame);
    }

    [Fact]
    public void TrailingHalfMagicIsKeptForTheNextRead() {
        var frame = ClientMove(4);
        var assembler = new KinpFrameAssembler();

        assembler.Append([0x55, frame[0]]);
        var skipped = assembler.Next();
        assembler.Append(frame[1..]);

        Assert.Equal(KinpReadStatus.Skipped, skipped.Status);
        Assert.Equal(1, skipped.ByteCount);
        Assert.Equal([frame], Drain(assembler).Select(read => read.Frame));
    }

    [Fact]
    public void LargeFrameIsCutByItsThirtyTwoBitLength() {
        var body = new byte[0x9000];
        var large = new byte[8 + body.Length];
        large[0] = 0x0D;
        large[1] = 0xF0;
        large[2] = 0x00;
        large[3] = 0x80;
        BitConverter.TryWriteBytes(large.AsSpan(4), (uint) body.Length);
        var after = ClientMove(5);
        var assembler = new KinpFrameAssembler();

        assembler.Append([.. large, .. after]);
        var reads = Drain(assembler);

        Assert.Equal(KinpReadStatus.LargeFrame, reads[0].Status);
        Assert.Equal(large.Length, reads[0].ByteCount);
        Assert.Equal(after, reads[1].Frame);
    }

    [Fact]
    public void FrameOverTheMaximumIsRefusedFromItsHeader() {
        var assembler = new KinpFrameAssembler(maxFrameSize: 64);

        assembler.Append([0x0D, 0xF0, 0x00, 0x01]);
        var read = assembler.Next();

        Assert.Equal(KinpReadStatus.Oversized, read.Status);
        Assert.Equal(4 + 0x100, read.ByteCount);
    }

    [Fact]
    public void HugeLargeFrameIsRefusedBeforeItsBodyArrives() {
        var assembler = new KinpFrameAssembler();

        assembler.Append([0x0D, 0xF0, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0x7F]);

        Assert.Equal(KinpReadStatus.Oversized, assembler.Next().Status);
    }

    [Fact]
    public void NothingComesOutAfterAnOversizedFrame() {
        var assembler = new KinpFrameAssembler(maxFrameSize: 64);

        assembler.Append([.. ClientMove(1), 0x0D, 0xF0, 0x00, 0x01, .. ClientMove(2)]);
        var reads = Drain(assembler);
        assembler.Append(ClientMove(3));

        Assert.Equal([KinpReadStatus.Frame, KinpReadStatus.Oversized], reads.Select(read => read.Status));
        Assert.Equal(KinpReadStatus.Incomplete, assembler.Next().Status);
        Assert.Equal(0, assembler.BufferedBytes);
    }

    private static List<KinpRead> Drain(KinpFrameAssembler assembler) {
        var reads = new List<KinpRead>();
        for (var read = assembler.Next(); read.Status != KinpReadStatus.Incomplete; read = assembler.Next()) {
            reads.Add(read);
        }

        return reads;
    }

    private static byte[] ClientMove(ushort x) {
        // GAME_5 order 1 carries three u16 coordinates and a direction byte.
        byte[] body = [(byte) x, (byte) (x >> 8), 0x10, 0x00, 0x20, 0x00, 0x7F];
        var dmlLength = body.Length + 4;
        var length = 4 + dmlLength + 1;
        byte[] header = [0x0D, 0xF0, (byte) length, (byte) (length >> 8), 0x00, 0x00, 0x00, 0x00];
        byte[] dml = [0x05, 0x01, (byte) dmlLength, (byte) (dmlLength >> 8)];

        return [.. header, .. dml, .. body, 0x00];
    }

    private static byte[] SmallFrame(int bodyLength, Random random) {
        var frame = new byte[4 + bodyLength];
        frame[0] = 0x0D;
        frame[1] = 0xF0;
        frame[2] = (byte) bodyLength;
        frame[3] = (byte) (bodyLength >> 8);
        random.NextBytes(frame.AsSpan(4));

        return frame;
    }

}
