// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 7 exit tests (new-features/managed-pgf-codec.md): "full Tier 1 + Tier 2 byte-exact decode
/// pass - the milestone proving the whole scalar decode pipeline is correct end to end." The C#
/// encoder doesn't exist yet (Stage 8), so every PGF file here is produced by the native shim's
/// <c>pgf_encode_bgra_alloc</c> (Stage 1) - <see cref="PgfImageDecoder"/> only needs to prove it can
/// decode a real bitstream correctly, regardless of which implementation wrote it. Decode is
/// deterministic given the same bytes, so byte-exact managed-vs-native agreement is asserted at
/// every quality level, not just quality=0 - unlike the 4-way round-trip matrix (Stage 8), there is
/// no cross-implementation-encoder ambiguity here to narrow the claim for.
/// </summary>
public class PgfImageDecoderTests
{
    private static readonly byte[] QualityLevels = [0, 1, 4, 6, 15];

    private static void AssertManagedMatchesNativeOracle(byte[] pgfBytes, int expectedWidth, int expectedHeight)
    {
        bool nativeOk = NativePgfOracle.TryDecode(pgfBytes, out byte[]? nativeBgra, out int nativeWidth, out int nativeHeight);
        Assert.True(nativeOk, "Native oracle failed to decode a file it just produced.");

        bool managedOk = PgfImageDecoder.TryDecode(pgfBytes, static (bgra, width, height) => (Bytes: bgra.ToArray(), width, height),
            out (byte[] Bytes, int width, int height) managed);
        Assert.True(managedOk, "Managed decoder failed on a file the native oracle decodes successfully.");

        Assert.Equal(expectedWidth, managed.width);
        Assert.Equal(expectedHeight, managed.height);
        Assert.Equal(nativeWidth, managed.width);
        Assert.Equal(nativeHeight, managed.height);
        Assert.Equal(nativeBgra, managed.Bytes);
    }

    public static TheoryData<int, int, byte> FixtureDimensionsAndQualities()
    {
        TheoryData<int, int, byte> data = [];
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            foreach (byte quality in QualityLevels)
            {
                data.Add(width, height, quality);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureDimensionsAndQualities))]
    public void Gradient_ManagedDecodeMatchesNativeOracle_AtEveryQualityLevel(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);
        Assert.True(NativePgfOracle.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));

        AssertManagedMatchesNativeOracle(pgfBytes!, w, h);
    }

    [Theory]
    [MemberData(nameof(FixtureDimensionsAndQualities))]
    public void Checkerboard_ManagedDecodeMatchesNativeOracle_AtEveryQualityLevel(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Checkerboard(width, height);
        Assert.True(NativePgfOracle.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));

        AssertManagedMatchesNativeOracle(pgfBytes!, w, h);
    }

    [Theory]
    [MemberData(nameof(FixtureDimensionsAndQualities))]
    public void SolidColor_ManagedDecodeMatchesNativeOracle_AtEveryQualityLevel(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.SolidColor(width, height, b: 30, g: 200, r: 90, a: 128);
        Assert.True(NativePgfOracle.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));

        AssertManagedMatchesNativeOracle(pgfBytes!, w, h);
    }

    /// <summary>Tier 1's own bar, on top of Tier 2's cross-implementation agreement: at
    /// <c>quality=0</c> (lossless), the managed decoder must reproduce the exact original source
    /// bitmap, not just match the native oracle's output.</summary>
    [Theory]
    [MemberData(nameof(EdgeCaseDimensions))]
    public void Quality0_ManagedDecodeMatchesOriginalSourceBitmapExactly(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);
        Assert.True(NativePgfOracle.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes));

        bool managedOk = PgfImageDecoder.TryDecode(pgfBytes!, static (bgra, width, height) => (Bytes: bgra.ToArray(), width, height),
            out (byte[] Bytes, int width, int height) managed, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(managedOk);
        Assert.Equal(w, managed.width);
        Assert.Equal(h, managed.height);
        Assert.Equal(bgra, managed.Bytes);
    }

    public static TheoryData<int, int> EdgeCaseDimensions()
    {
        TheoryData<int, int> data = [];
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            data.Add(width, height);
        }

        return data;
    }

    /// <summary>Tier 2's real-world leg: the existing committed digiKam-derived fixture, not just
    /// synthetic bitmaps - decoded with both implementations and compared byte-exact.</summary>
    [Fact]
    public void SampleThumbnail_ManagedDecodeMatchesNativeOracle()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        Assert.True(NativePgfOracle.TryGetDimensions(pgfBytes, out int width, out int height));

        AssertManagedMatchesNativeOracle(pgfBytes, width, height);
    }

    [Fact]
    public void TruncatedStream_FailsClosed_WithoutThrowing()
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(64, 64);
        Assert.True(NativePgfOracle.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes));

        byte[] truncated = pgfBytes![..(pgfBytes.Length / 2)];

        bool ok = PgfImageDecoder.TryDecode(truncated, static (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Fact]
    public void GarbageBytes_FailsClosed_WithoutThrowing()
    {
        byte[] garbage = new byte[256];
        new Random(42).NextBytes(garbage);

        bool ok = PgfImageDecoder.TryDecode(garbage, static (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Fact]
    public void EmptyInput_FailsClosed_WithoutThrowing()
    {
        bool ok = PgfImageDecoder.TryDecode(ReadOnlyMemory<byte>.Empty, static (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Null(result);
    }
}
