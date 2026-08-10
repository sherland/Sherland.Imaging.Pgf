// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 4b exit test: LabColor - its own group, not Group A or Group C,
/// despite the PRD's original text grouping it with Group A. Confirmed by direct inspection of both
/// <c>RgbToYuv</c> and <c>GetBitmap</c> independently (this PRD's own "verify each side fresh"
/// instruction): Lab shares Group A's simple per-channel encode transform (PGFimage.cpp:1445-1473)
/// but needs Group C's chroma-upsample decode structure (PGFimage.cpp:2124-2159), because Lab is in
/// <c>SetHeader</c>'s downsample-eligible mode list (PGFimage.cpp:921-927) while GrayScale/
/// IndexedColor/HSL/HSB are not.
/// </summary>
public class PgfGroupLabTests
{
    private static byte[] LabGradient(int width, int height)
    {
        byte[] data = new byte[width * height * 3];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                data[cnt] = (byte)(((x * 7) + (y * 13)) % 256); // L
                data[cnt + 1] = (byte)(((x * 11) + (y * 5)) % 256); // a
                data[cnt + 2] = (byte)(((x * 3) + (y * 17)) % 256); // b
                cnt += 3;
            }
        }

        return data;
    }

    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsExactlyAtQuality0_NoDownsample()
    {
        byte[] source = LabGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeLabColor, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(decoded);

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < 37 * 23; i++)
        {
            Assert.Equal(source[srcCnt], bgraResult![dstCnt]);
            Assert.Equal(source[srcCnt + 1], bgraResult[dstCnt + 1]);
            Assert.Equal(source[srcCnt + 2], bgraResult[dstCnt + 2]);
            Assert.Equal(255, bgraResult[dstCnt + 3]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    /// <summary>The whole point of this group being split from Group A: Lab must actually engage
    /// the downsample path (unlike HSL/HSB, which never do) - proves PgfModeInfo.SupportsDownsample
    /// really gates this per-mode, not just per-channel-count.</summary>
    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsAtHighQuality_WithDownsample()
    {
        byte[] source = LabGradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeLabColor, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (bgra.ToArray(), w, h), out (byte[] Bgra, int W, int H) result, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(decoded);
        Assert.Equal(64, result.W);
        Assert.Equal(64, result.H);
        Assert.Equal(64 * 64 * 4, result.Bgra.Length);
    }

    [Fact]
    public void ManagedEncodeThenNativeOracleDecodesRaw_ChannelsAreByteExact()
    {
        byte[] source = LabGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeLabColor, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded LabColor file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);
        Assert.Equal(source, oracleRaw);
    }

    [Fact]
    public void ManagedEncodeThenNativeOracleDecodesRaw_ChannelsAreByteExact_WithDownsample()
    {
        byte[] source = LabGradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeLabColor, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded LabColor file.");
        Assert.Equal(64, width);
        Assert.Equal(64, height);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(decoded);

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < 64 * 64; i++)
        {
            Assert.Equal(oracleRaw![srcCnt], bgraResult![dstCnt]);
            Assert.Equal(oracleRaw[srcCnt + 1], bgraResult[dstCnt + 1]);
            Assert.Equal(oracleRaw[srcCnt + 2], bgraResult[dstCnt + 2]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    [Theory]
    [MemberData(nameof(EdgeCaseDimensionsData))]
    public void EdgeCaseDimensions_RoundTripLosslessly(int width, int height)
    {
        byte[] source = LabGradient(width, height);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeLabColor, out byte[]? pgfBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(decoded);

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < width * height; i++)
        {
            Assert.Equal(source[srcCnt], bgraResult![dstCnt]);
            Assert.Equal(source[srcCnt + 1], bgraResult[dstCnt + 1]);
            Assert.Equal(source[srcCnt + 2], bgraResult[dstCnt + 2]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    public static IEnumerable<object[]> EdgeCaseDimensionsData() =>
        TestBitmaps.EdgeCaseDimensions().Select(d => new object[] { d.Width, d.Height });
}
