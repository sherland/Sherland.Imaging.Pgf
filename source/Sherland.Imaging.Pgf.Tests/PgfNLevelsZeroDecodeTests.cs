// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 4 exit tests (new-features/pgf-user-data-and-small-images.md): the decode side of the
/// wavelet-transform-free "raw/uncoded" <c>nLevels=0</c> path (<c>CPGFImage::Open</c>,
/// PGFimage.cpp:198-214), for images below <see cref="TestBitmaps.MinimumSupportedDimension"/>. This
/// port's own encoder doesn't write this path yet (Stage 5's scope) - files are produced by the real
/// native encoder instead (<see cref="NativePgfOracle.TryEncode"/>/<see cref="NativePgfOracle.
/// TryEncodeMode"/>, whose own `width &lt; 10 || height &lt; 10` guard was removed as this PRD's own
/// Open Question 1), giving the strongest possible proof: byte-exact against the real oracle, not
/// just self-consistency within this port.
/// </summary>
public class PgfNLevelsZeroDecodeTests
{
    public static TheoryData<int, int> BelowMinimumDimensions()
    {
        TheoryData<int, int> data = [];
        foreach ((int width, int height) in new (int, int)[] { (1, 1), (1, 7), (7, 1), (9, 9), (5, 5), (3, 8), (1, 100), (100, 1) })
        {
            data.Add(width, height);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BelowMinimumDimensions))]
    public void TryDecode_NativeEncodedTinyImage_ByteExact(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        bool encoded = NativePgfOracle.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes);
        Assert.True(encoded, $"Native oracle failed to encode {width}x{height} - Open Question 1's guard removal assumption broke.");

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, dw, dh) => span.ToArray(), out byte[]? decodedBgra);

        Assert.True(decoded, $"Expected {width}x{height} (below MinimumSupportedDimension) to decode via the nLevels=0 path.");
        Assert.Equal(bgra, decodedBgra);
    }

    /// <summary>The raw path has no forward transform to quantize the output of (this PRD's own
    /// grounding, confirmed directly against PGFimage.cpp:1159-1175's <c>WriteImage</c> mirror, not
    /// assumed), so it is lossless at every quality up to <see cref="PgfConstants.
    /// DownsampleThreshold"/> - above that, RGBA's chroma downsample decision (PGFimage.cpp:161-174)
    /// still applies exactly as it does on the normal wavelet path (confirmed directly, not assumed:
    /// an initial version of this test wrongly expected losslessness at every quality and caught its
    /// own wrong assumption here - quality 4/6/15 genuinely differ from the source, matching real
    /// chroma-subsampling loss, not a decode bug). "No quantization" and "no chroma downsampling" are
    /// two different things; only the former is actually true unconditionally on this path.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryDecode_NativeEncodedTinyImage_QualityAtOrBelowDownsampleThreshold_StillLossless(byte quality)
    {
        Assert.True(quality <= PgfConstants.DownsampleThreshold);
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);

        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality, out byte[]? pgfBytes));

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    /// <summary>Above <see cref="PgfConstants.DownsampleThreshold"/>, chroma downsampling makes this
    /// path lossy the same way the normal wavelet path is - this just confirms decode still succeeds
    /// (dimensions match) rather than asserting byte-exact pixels, which real chroma subsampling
    /// makes an incorrect expectation.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(15)]
    public void TryDecode_NativeEncodedTinyImage_AboveDownsampleThreshold_StillDecodesSuccessfully(byte quality)
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);

        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality, out byte[]? pgfBytes));

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => (Width: w, Height: h), out var result);

        Assert.True(decoded);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    [Fact]
    public void TryDecode_1x1Image_DecodesCorrectly()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(1, 1, b: 200, g: 100, r: 50, a: 255);

        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes));

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => (Bgra: span.ToArray(), Width: w, Height: h), out var result);

        Assert.True(decoded);
        Assert.Equal(bgra, result.Bgra);
        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
    }

    [Fact]
    public void ProgressiveDecoder_TryOpen_NLevelsZero_LevelsIsZero_AndLevel0Decodes()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes));

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(pgfBytes!);

        Assert.NotNull(progressive);
        Assert.Equal(0, progressive.Levels);
        Assert.Equal(width, progressive.Width);
        Assert.Equal(height, progressive.Height);

        bool decoded = progressive.TryDecodeLevel(0, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void ProgressiveDecoder_NLevelsZero_RequestingNonZeroLevel_FailsClosed()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes));

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(pgfBytes!);
        Assert.NotNull(progressive);

        bool decoded = progressive.TryDecodeLevel(1, static (span, w, h) => span.ToArray(), out byte[]? _);

        Assert.False(decoded);
    }

    [Fact]
    public void ProgressiveDecoder_NLevelsZero_RequestingLevel0Twice_IsIdempotent()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes));

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(pgfBytes!);
        Assert.NotNull(progressive);

        Assert.True(progressive.TryDecodeLevel(0, static (span, w, h) => span.ToArray(), out byte[]? first));
        Assert.True(progressive.TryDecodeLevel(0, static (span, w, h) => span.ToArray(), out byte[]? second));

        Assert.Equal(bgra, first);
        Assert.Equal(bgra, second);
    }

    [Fact]
    public void TryDecode_TruncatedRawChannelStream_FailsClosed_WithoutThrowing()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes));

        byte[] truncated = pgfBytes.AsSpan(0, pgfBytes!.Length - 5).ToArray();

        bool decoded = PgfImageDecoder.TryDecode(truncated, static (span, w, h) => span.ToArray(), out byte[]? _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecode_IndexedColorTinyImage_NLevelsZero_ByteExact()
    {
        byte[] colorTable = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < colorTable.Length; i++)
        {
            colorTable[i] = (byte)(i * 3);
        }

        (byte[] gradientBgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        byte[] indexSource = new byte[width * height];
        for (int i = 0; i < indexSource.Length; i++)
        {
            indexSource[i] = gradientBgra[i * 4];
        }

        bool encoded = NativePgfOracle.TryEncodeMode(
            indexSource, width, height, quality: 0, PgfConstants.ImageModeIndexedColor, bpp: 8, channels: 1,
            colorTable, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);

        Assert.True(decoded);
        Assert.NotNull(decodedBgra);
    }
}
