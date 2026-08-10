// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-codec-allocation-and-simd.md Stage 1: records the pre-workspace fact that today's public
/// convenience paths allocate after warm-up. These deliberately assert only the qualitative
/// baseline; BenchmarkDotNet remains the authoritative quantitative record because GC accounting
/// is runtime- and machine-specific. Stage 3 replaces the decode expectation with the new explicit
/// workspace contract rather than treating these allocations as permanent behavior.
/// </summary>
public class PgfAllocationBaselineTests
{
    [Fact]
    public void ConvenienceDecode_AllocatesWorkingMemoryAfterWarmup_AndStillProducesExactPixels()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        Assert.True(PgfImageDecoder.TryDecode(pgf!, static (b, _, _) => b.ToArray(), out byte[]? warmup, cancellationToken: TestContext.Current.CancellationToken));
        long allocated = Measure(() =>
        {
            Assert.True(PgfImageDecoder.TryDecode(pgf!, static (b, _, _) => b.ToArray(), out byte[]? decoded));
            Assert.Equal(source, decoded);
        });

        Assert.True(allocated > 0, "The pre-workspace convenience decode baseline should allocate working/result memory.");
    }

    [Fact]
    public void ConvenienceEncode_AllocatesWorkingMemoryAfterWarmup_AndProducesDecodableBytes()
    {
        (byte[] source, int width, int height) = TestBitmaps.Checkerboard(64, 64);

        Assert.True(PgfImageEncoder.TryEncode(source, width, height, quality: 8, out _, cancellationToken: TestContext.Current.CancellationToken));
        long allocated = Measure(() =>
        {
            Assert.True(PgfImageEncoder.TryEncode(source, width, height, quality: 8, out byte[]? pgf));
            Assert.True(NativePgfOracle.TryDecode(pgf!, out byte[]? decoded, out int decodedWidth, out int decodedHeight));
            Assert.Equal(width, decodedWidth);
            Assert.Equal(height, decodedHeight);
        });

        Assert.True(allocated > 0, "The pre-workspace convenience encode baseline should allocate work and owned output memory.");
    }

    private static long Measure(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
