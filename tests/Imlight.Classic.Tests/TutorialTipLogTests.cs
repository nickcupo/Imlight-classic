using System;
using System.Linq;
using Imlight.Classic.Net;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>The client's tutorial tip log layout (TutorialLogBehaviorBase::OnPostLoad 0x141f258b0).</summary>
public class TutorialTipLogTests {

    [Fact]
    public void The_log_is_magic_version_count_and_ids_little_endian() {
        var data = TutorialTipLog.Encode([7, 0x01020304]);
        Assert.Equal(new byte[] {
            0xED, 0x0D, 0xEC, 0x2D, // 0x2DEC0DED
            1, 0, 0, 0,             // version 1
            2, 0,                   // count (uint16)
            7, 0, 0, 0, 4, 3, 2, 1, // ids (uint32)
        }, data);
    }

    [Fact]
    public void An_empty_log_still_has_its_header() {
        Assert.Equal(10, TutorialTipLog.Encode([]).Length);
        Assert.True(TutorialTipLog.TryDecode(TutorialTipLog.Encode([]), out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void A_log_round_trips_without_duplicates() {
        Assert.True(TutorialTipLog.TryDecode(TutorialTipLog.Encode([5, 9, 5, 12]), out var tips));
        Assert.Equal([5u, 9u, 12u], tips);
    }

    [Fact]
    public void Anything_else_is_refused() {
        var good = TutorialTipLog.Encode([1, 2, 3]);
        Assert.False(TutorialTipLog.TryDecode(good.AsSpan(0, 9), out _));        // short header
        Assert.False(TutorialTipLog.TryDecode(good.AsSpan(0, good.Length - 1), out _)); // short ids
        var wrong = good.ToArray(); wrong[0] ^= 1;
        Assert.False(TutorialTipLog.TryDecode(wrong, out _));                      // magic
        var version = good.ToArray(); version[4] = 2;
        Assert.False(TutorialTipLog.TryDecode(version, out _));                    // version
    }

}
