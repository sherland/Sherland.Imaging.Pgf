using PictTag.PgfCodec.Tests.Oracle;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 3 exit test: <see cref="PgfConstants.ImageModeCMYKColor"/> confirmed
/// reusing RGBA's exact <c>RgbToYuv</c>/<c>GetBitmap</c> case blocks (PGFimage.cpp:1578-1579,
/// 2232-2233) - not a real CMYK colorimetric transform, the native codec's own established
/// treatment of a 4th channel as alpha-like regardless of what it represents. This test proves that
/// by construction (the mode dispatch literally shares RGBA's <see cref="PgfColorConversion"/>
/// calls), then cross-checks against the real native oracle the same way <see cref="PgfGroupATests"/>
/// does for Group A, per this PRD's own "confirm by testing, don't assume the shared case block
/// means zero new work" instruction.
/// </summary>
public class PgfGroupETests
{
    private static byte[] CmykGradient(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                data[cnt] = (byte)(((x * 7) + (y * 13)) % 256); // C
                data[cnt + 1] = (byte)(((x * 11) + (y * 5)) % 256); // M
                data[cnt + 2] = (byte)(((x * 3) + (y * 17)) % 256); // Y
                data[cnt + 3] = (byte)(((x * 19) + (y * 2)) % 256); // K
                cnt += 4;
            }
        }

        return data;
    }

    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsExactlyAtQuality0_NoDownsample()
    {
        byte[] source = CmykGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeCMYKColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        Assert.Equal(source, bgraResult);
    }

    /// <summary>Quality above <see cref="PgfConstants.DownsampleThreshold"/> engages chroma
    /// subsampling for CMYKColor (it's in SetHeader's downsample-eligible list, PGFimage.cpp:924) -
    /// exercises PgfDecodeSession/PgfImageEncoder's downsample path for a 4-channel mode that isn't
    /// RGBA, not just the always-tested RGBA case.</summary>
    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsAtHighQuality_WithDownsample()
    {
        byte[] source = CmykGradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeCMYKColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (bgra.ToArray(), w, h), out (byte[] Bgra, int W, int H) result);
        Assert.True(decoded);
        Assert.Equal(64, result.W);
        Assert.Equal(64, result.H);
        Assert.Equal(source.Length, result.Bgra.Length);
    }

    [Fact]
    public void ManagedEncodeThenNativeOracleDecodesRaw_ChannelsAreByteExact()
    {
        byte[] source = CmykGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeCMYKColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 32, [0, 1, 2, 3], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded CMYKColor file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);
        Assert.Equal(source, oracleRaw);
    }

    [Theory]
    [MemberData(nameof(EdgeCaseDimensionsData))]
    public void EdgeCaseDimensions_RoundTripLosslessly(int width, int height)
    {
        byte[] source = CmykGradient(width, height);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeCMYKColor, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);
        Assert.Equal(source, bgraResult);
    }

    public static IEnumerable<object[]> EdgeCaseDimensionsData() =>
        TestBitmaps.EdgeCaseDimensions().Select(d => new object[] { d.Width, d.Height });
}
