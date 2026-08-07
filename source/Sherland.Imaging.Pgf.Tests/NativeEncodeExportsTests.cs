// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 1 exit tests (<c>new-features/managed-pgf-codec.md</c>): the native shim's new test-only
/// encode export (<c>pgf_encode_bgra_alloc</c>) works correctly on its own, before any C# port code
/// depends on it. Proves the new native infrastructure itself is sound - encode with the real C++
/// encoder, decode with the real C++ decoder, confirm the original bitmap comes back exactly at
/// quality=0 (lossless).
///
/// Deliberately does NOT exercise <c>pgf_debug_decode_channel</c> (also added in shim.cpp): repeated
/// calls to that specific function - even a modest number, even mixed among many more calls to
/// the encode/decode exports that never showed any problem on their own - produced a real,
/// repeatable native crash during this test rig's development that was not root-caused (see
/// shim.cpp's doc comment on that function for the full investigation). It remains available for
/// manual, one-off use during later stages (comparing the C# port's intermediate decode state
/// against this oracle) but must not be added to automated test loops until that's resolved.
/// </summary>
public class NativeEncodeExportsTests
{
    [Fact]
    public void SolidColor_EncodeThenDecode_Lossless_MatchesOriginalExactly()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(64, 64, b: 10, g: 20, r: 200, a: 255);

        bool encoded = NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes);
        Assert.True(encoded);
        Assert.NotNull(pgfBytes);

        bool decoded = NativePgfOracle.TryDecode(pgfBytes, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);
        Assert.True(decoded);
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void Checkerboard_EncodeThenDecode_Lossless_MatchesOriginalExactly()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Checkerboard(32, 32);

        bool encoded = NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);
        Assert.True(decoded);
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void Gradient_EncodeThenDecode_Lossless_MatchesOriginalExactly()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(96, 48);

        bool encoded = NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);
        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    [Theory]
    [MemberData(nameof(EdgeCaseDimensions))]
    public void EdgeCaseDimensions_EncodeThenDecode_Lossless_MatchesOriginalExactly(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        bool encoded = NativePgfOracle.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes);
        Assert.True(encoded, $"Encode failed for {width}x{height}.");

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);
        Assert.True(decoded, $"Decode failed for {width}x{height}.");
        Assert.Equal(w, decodedWidth);
        Assert.Equal(h, decodedHeight);
        Assert.Equal(bgra, decodedBgra);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(15)]
    public void EveryQualityLevel_EncodeThenDecode_ProducesCorrectDimensionsAndDoesNotThrow(byte quality)
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);

        bool encoded = NativePgfOracle.TryEncode(bgra, width, height, quality, out byte[]? pgfBytes);
        Assert.True(encoded, $"Encode failed at quality={quality}.");

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);
        Assert.True(decoded, $"Decode failed at quality={quality}.");
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.NotNull(decodedBgra);

        if (quality == 0)
        {
            Assert.Equal(bgra, decodedBgra);
        }
    }

    [Fact]
    public void HigherQuality_ProducesNoLargerOutput_ThanLossless()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(128, 128);

        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? lossless));
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 6, out byte[]? lossy));

        Assert.True(lossy!.Length <= lossless!.Length,
            $"Expected quality=6 ({lossy.Length} bytes) to be no larger than quality=0 ({lossless.Length} bytes).");
    }

    [Fact]
    public void ZeroDimensions_EncodeFailsClosed_WithoutThrowing()
    {
        bool encoded = NativePgfOracle.TryEncode([], width: 0, height: 0, quality: 0, out byte[]? pgfBytes);

        Assert.False(encoded);
        Assert.Null(pgfBytes);
    }

    /// <summary>Below <see cref="TestBitmaps.MinimumSupportedDimension"/>, <c>CPGFImage::
    /// ComputeLevels()</c> falls back to the wavelet-transform-free <c>nLevels=0</c> "raw/uncoded"
    /// path. The native shim used to reject this range unconditionally rather than exercise it (a
    /// real heap-corruption risk found during this test rig's original development) - re-tested
    /// under `pgf-user-data-and-small-images.md`'s Open Question 1 once isolated from the (by then
    /// separately fixed) `realloc()`/`delete[]` bug found in the same investigation: a 5000-call
    /// encode-then-decode stress test across ten sizes down to 1x1 reproduced no crash and no pixel
    /// mismatch, so the guard was removed (`shim.cpp`'s own doc comment has the full account). This
    /// oracle now round-trips these sizes correctly, same as every other size.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(9, 9)]
    public void BelowMinimumDimension_EncodeThenDecode_Lossless_MatchesOriginalExactly(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        bool encoded = NativePgfOracle.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes);
        Assert.True(encoded, $"Expected {width}x{height} (below MinimumSupportedDimension) to encode now that the native guard is gone.");

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);
        Assert.True(decoded, $"Decode failed for {width}x{height}.");
        Assert.Equal(w, decodedWidth);
        Assert.Equal(h, decodedHeight);
        Assert.Equal(bgra, decodedBgra);
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
}
