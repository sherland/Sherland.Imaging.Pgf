// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 8 tests (new-features/managed-pgf-codec.md) for <see cref="PgfImageEncoder"/> on its own:
/// Tier 5's encode-side negative/malformed-input cases, plus the output-size monotonicity invariant
/// ("higher quality values should be no larger than at quality=0"). The actual correctness proof - the
/// 4-way round-trip matrix - lives in <see cref="PgfRoundTripMatrixTests"/>.
/// </summary>
public class PgfImageEncoderTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public void InvalidDimensions_FailsClosed_WithoutThrowing(int width, int height)
    {
        bool encoded = PgfImageEncoder.TryEncode([], width, height, quality: 0, out byte[]? pgfBytes);

        Assert.False(encoded);
        Assert.Null(pgfBytes);
    }

    /// <summary>Matches <see cref="TestBitmaps.MinimumSupportedDimension"/> - the same real,
    /// vendored-library <c>nLevels=0</c> boundary (managed-pgf-codec.md's scope notes), ported here
    /// via <c>PgfHeaderIO.ComputeLevels</c> returning 0. Used to hard-fail here; now encodes via the
    /// raw/uncoded path (pgf-user-data-and-small-images.md Stage 5) - see
    /// <see cref="PgfNLevelsZeroEncodeTests"/> for the real round-trip proof, this just confirms the
    /// old hard-failure is gone.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(9, 9)]
    public void BelowMinimumDimension_NoLongerFailsClosed_EncodesViaRawPath(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        bool encoded = PgfImageEncoder.TryEncode(bgra, w, h, quality: 0, out byte[]? pgfBytes);

        Assert.True(encoded, $"Expected {width}x{height} (below MinimumSupportedDimension) to encode via the nLevels=0 raw path.");
        Assert.NotNull(pgfBytes);
    }

    [Theory]
    [InlineData(32)] // PgfConstants.MaxQuality + 1 (31 + 1, pgf-all-image-modes.md's DataT correction)
    [InlineData(255)]
    public void QualityAboveMax_FailsClosed_WithoutThrowing(int quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(32, 32);

        bool encoded = PgfImageEncoder.TryEncode(bgra, w, h, (byte)quality, out byte[]? pgfBytes);

        Assert.False(encoded);
        Assert.Null(pgfBytes);
    }

    [Fact]
    public void BgraBufferShorterThanDimensionsImply_FailsClosed_WithoutThrowing()
    {
        byte[] tooShort = new byte[10 * 10 * 4 - 1];

        bool encoded = PgfImageEncoder.TryEncode(tooShort, width: 10, height: 10, quality: 0, out byte[]? pgfBytes);

        Assert.False(encoded);
        Assert.Null(pgfBytes);
    }

    [Fact]
    public void BgraBufferLongerThanDimensionsImply_FailsClosed_WithoutThrowing()
    {
        byte[] tooLong = new byte[10 * 10 * 4 + 4];

        bool encoded = PgfImageEncoder.TryEncode(tooLong, width: 10, height: 10, quality: 0, out byte[]? pgfBytes);

        Assert.False(encoded);
        Assert.Null(pgfBytes);
    }

    [Fact]
    public void CallerOwnedDestination_ProducesTheExactConvenienceStream_WithoutAnOwnedResult()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 8, out byte[]? expected));

        byte[] destination = new byte[expected!.Length];
        bool encoded = PgfImageEncoder.TryEncodeMode(
            bgra, width, height, quality: 8, PgfConstants.ImageModeRGBA, destination, out int bytesWritten);

        Assert.True(encoded);
        Assert.Equal(expected.Length, bytesWritten);
        Assert.Equal(expected, destination);
    }

    [Fact]
    public void CallerOwnedDestination_ReportsRequiredLength_AndDoesNotWritePartially()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 8, out byte[]? expected));

        byte[] destination = Enumerable.Repeat((byte)0xCC, expected!.Length - 1).ToArray();
        bool encoded = PgfImageEncoder.TryEncodeMode(
            bgra, width, height, quality: 8, PgfConstants.ImageModeRGBA, destination, out int bytesWritten);

        Assert.False(encoded);
        Assert.Equal(expected.Length, bytesWritten);
        Assert.All(destination, value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void WorkspaceBackedEncode_MatchesTheConvenienceStream()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 8, out byte[]? expected));

        using var workspace = new PgfWorkspace();
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 8, out byte[]? actual, workspace: workspace));

        Assert.Equal(expected, actual);
    }

    /// <summary>Tier 4's "cheap invariant worth asserting alongside pixel agreement" - swept across
    /// every quality value, not just one representative pair, since this is a compression codec and a
    /// regression here would indicate a quantization-logic bug even if pixel-agreement tests still
    /// pass. Deliberately compares every quality value against the <c>quality=0</c> baseline only
    /// (matching the PRD's own wording), not strict step-by-step monotonicity between adjacent
    /// quality values - a real, confirmed-not-a-port-bug non-monotonic step exists in the *native*
    /// encoder too (10x10 gradient: 82 bytes at quality=8, 86 bytes at quality=9, reproduced
    /// byte-for-byte identically by this port - entropy-coding overhead can occasionally exceed the
    /// coefficient-magnitude savings from one quantization step to the next, especially at tiny
    /// single-macroblock image sizes), so asserting strict adjacent-step monotonicity would be a
    /// stricter bar than the real codec itself actually meets.</summary>
    [Theory]
    [MemberData(nameof(EdgeCaseDimensions))]
    public void OutputSize_NeverExceedsLosslessBaseline_AcrossQualityLevels(int width, int height)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality: 0, out byte[]? losslessBytes));

        for (byte quality = 1; quality <= PgfConstants.MaxQuality; quality++)
        {
            Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? bytes));
            Assert.True(bytes!.Length <= losslessBytes!.Length,
                $"{w}x{h}: quality={quality} ({bytes.Length} bytes) exceeds quality=0 baseline ({losslessBytes.Length} bytes).");
        }
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
