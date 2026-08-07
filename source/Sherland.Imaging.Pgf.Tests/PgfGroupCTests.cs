// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 4 exit test: Group C (RGBColor) - the genuine 3-channel YUV
/// transform (real cross-channel decorrelation, unlike Group A/E's per-channel offsets), and the
/// first non-RGBA mode this port exercises the chroma-downsample/upsample path for on its own
/// dedicated case block (RGBColor is in SetHeader's downsample-eligible list, PGFimage.cpp:921).
/// </summary>
public class PgfGroupCTests
{
    private static byte[] RgbGradient(int width, int height)
    {
        byte[] data = new byte[width * height * 3];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                data[cnt] = (byte)(((x * 7) + (y * 13)) % 256); // B
                data[cnt + 1] = (byte)(((x * 11) + (y * 5)) % 256); // G
                data[cnt + 2] = (byte)(((x * 3) + (y * 17)) % 256); // R
                cnt += 3;
            }
        }

        return data;
    }

    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsExactlyAtQuality0_NoDownsample()
    {
        byte[] source = RgbGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeRGBColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
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

    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsAtHighQuality_WithDownsample()
    {
        byte[] source = RgbGradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeRGBColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (bgra.ToArray(), w, h), out (byte[] Bgra, int W, int H) result);
        Assert.True(decoded);
        Assert.Equal(64, result.W);
        Assert.Equal(64, result.H);
        Assert.Equal(64 * 64 * 4, result.Bgra.Length);
    }

    [Fact]
    public void ManagedEncodeThenNativeOracleDecodesRaw_ChannelsAreByteExact()
    {
        byte[] source = RgbGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeRGBColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded RGBColor file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);
        Assert.Equal(source, oracleRaw);
    }

    /// <summary>Same fixture, downsample-eligible quality - proves the downsampled chroma channels
    /// this port's encoder writes decode byte-exact against the real oracle too, not just the
    /// simpler always-full-resolution case above.</summary>
    [Fact]
    public void ManagedEncodeThenNativeOracleDecodesRaw_ChannelsAreByteExact_WithDownsample()
    {
        byte[] source = RgbGradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeRGBColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded RGBColor file.");
        Assert.Equal(64, width);
        Assert.Equal(64, height);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
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
        byte[] source = RgbGradient(width, height);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeRGBColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
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
