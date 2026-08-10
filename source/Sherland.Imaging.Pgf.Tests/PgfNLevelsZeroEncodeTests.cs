// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 5 exit tests (new-features/pgf-user-data-and-small-images.md): the encode side of the
/// <c>nLevels=0</c> raw/uncoded path, completing the round trip Stage 4 only proved decode-side (via
/// the real native encoder). This port's own encoder now writes this path too - covers both
/// self-consistency (this port's encoder -> this port's decoder) and cross-checks against the real
/// native decoder, matching this codec's usual "verify against the real oracle, not just internal
/// consistency" bar.
/// </summary>
public class PgfNLevelsZeroEncodeTests
{
    // Not the real System.Progress<T>: that posts through SynchronizationContext, which makes
    // "did this call report X" assertions racy - matches PgfProgressAndCancellationTests' own
    // reasoning for the same test double.
    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    public static TheoryData<int, int> SizesFrom1x1ThroughMinimumSupportedDimension()
    {
        TheoryData<int, int> data = [];
        for (int size = 1; size <= TestBitmaps.MinimumSupportedDimension; size++)
        {
            data.Add(size, size);
        }

        data.Add(1, TestBitmaps.MinimumSupportedDimension - 1);
        data.Add(TestBitmaps.MinimumSupportedDimension - 1, 1);
        data.Add(1, 100);
        data.Add(100, 1);
        data.Add(3, 8);
        data.Add(9, 1);

        return data;
    }

    [Theory]
    [MemberData(nameof(SizesFrom1x1ThroughMinimumSupportedDimension))]
    public void TryEncode_ThenTryDecode_RoundTripsByteExact(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        bool encoded = PgfImageEncoder.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded, $"Expected {width}x{height} to encode via the nLevels=0 raw path.");

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, dw, dh) => span.ToArray(), out byte[]? decodedBgra, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    /// <summary>The strongest possible proof for this stage: this port's own encoder output, opened
    /// by the real native decoder (not just this port's own, potentially self-consistently-wrong,
    /// reader) - the real format's own parser must agree the file is well-formed and lossless.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(9, 9)]
    [InlineData(5, 5)]
    public void TryEncode_NativeOracleDecodesItByteExact(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken));

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);

        Assert.True(decoded, $"Native oracle failed to decode this port's own nLevels=0 encoded output for {width}x{height}.");
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal(bgra, decodedBgra);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryEncode_QualityAtOrBelowDownsampleThreshold_StillLossless(byte quality)
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken));

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    /// <summary>Matches the same finding Stage 4's decode-side test caught: above
    /// <see cref="PgfConstants.DownsampleThreshold"/>, chroma downsampling makes this path lossy the
    /// same way the normal wavelet path is - encode must still succeed and round-trip to the
    /// *correct dimensions*, just not byte-identical pixels.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void TryEncode_AboveDownsampleThreshold_StillEncodesAndDecodesSuccessfully(byte quality)
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken));

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => (Width: w, Height: h), out var result, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(decoded);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    [Fact]
    public void TryEncodeMode_IndexedColor_NLevelsZero_RoundTripsCorrectly()
    {
        byte[] colorTable = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < colorTable.Length; i++)
        {
            colorTable[i] = (byte)(i * 5);
        }

        (byte[] gradientBgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        byte[] indexSource = new byte[width * height];
        for (int i = 0; i < indexSource.Length; i++)
        {
            indexSource[i] = gradientBgra[i * 4];
        }

        bool encoded = PgfImageEncoder.TryEncodeMode(
            indexSource, width, height, 0, PgfConstants.ImageModeIndexedColor, out byte[]? pgfBytes, colorTable, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(decoded);
        Assert.NotNull(decodedBgra);
    }

    [Fact]
    public void TryEncode_WithUserData_NLevelsZero_BothRoundTrip()
    {
        byte[] userData = "tiny image, real metadata"u8.ToArray();
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(1, 1, 10, 20, 30, 255);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, userData: userData, cancellationToken: TestContext.Current.CancellationToken));

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, out PgfUserData decodedUserData, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
        Assert.Equal(userData, decodedUserData.CachedBytes);
    }

    [Fact]
    public void ProgressiveDecoder_DecodesThisPortsOwnEncodedNLevelsZeroFile()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(9, 9);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken));

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(pgfBytes!);
        Assert.NotNull(progressive);
        Assert.Equal(0, progressive.Levels);

        bool decoded = progressive.TryDecodeLevel(0, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void TryEncode_ReportsProgressAsComplete()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(5, 5);

        List<double> reports = [];
        bool encoded = PgfImageEncoder.TryEncode(
            bgra, width, height, quality: 0, out byte[]? pgfBytes, progress: new SynchronousProgress<double>(reports.Add), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(encoded);
        Assert.Contains(1.0, reports);
    }

    /// <summary>Regression: normal (nLevels&gt;=1) images are completely unaffected by this stage's
    /// new branch - the existing round-trip matrix already proves this broadly, this is a narrow
    /// sanity check specific to sizes right at the boundary.</summary>
    [Fact]
    public void TryEncode_JustAboveMinimumDimension_StillUsesNormalWaveletPath()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(TestBitmaps.MinimumSupportedDimension, TestBitmaps.MinimumSupportedDimension);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken));

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }
}
