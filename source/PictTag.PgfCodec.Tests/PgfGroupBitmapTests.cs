using PictTag.PgfCodec.Tests.Oracle;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 6 exit test: Group G (Bitmap, 1bpp) - only the "new unpacked since
/// Version7" sub-variant is ported (both encode and decode), a deliberate scope cut documented in
/// <see cref="PgfColorConversion"/>'s Group G doc comment: <c>RgbToYuv</c>'s pre-Version7 packed-input
/// alternative is permanently disabled (commented out) in the real source, and <c>GetBitmap</c>'s
/// pre-Version7 decode branch stores channel data at a different width entirely (one <c>DataT</c> per
/// byte, not per pixel) - real, reachable code, but for a file shape no real digiKam thumbnail or
/// this port's own encoder could ever produce, and porting it would need PgfDecodeSession's
/// channel-allocation logic to special-case a file's own historical version flag. Per this PRD's own
/// "don't guess at untested bit-packing logic" guidance, this is left unported rather than guessed at.
/// </summary>
public class PgfGroupBitmapTests
{
    private static byte[] PackedBitmap(int width, int height, Func<int, int, bool> setBit)
    {
        int rowBytes = (width + 7) / 8;
        byte[] data = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (setBit(x, y))
                {
                    int byteIndex = (y * rowBytes) + (x / 8);
                    int bitIndex = 7 - (x % 8); // MSB-first
                    data[byteIndex] |= (byte)(1 << bitIndex);
                }
            }
        }

        return data;
    }

    private static bool GetBit(byte[] packed, int rowBytes, int x, int y)
    {
        int byteIndex = (y * rowBytes) + (x / 8);
        int bitIndex = 7 - (x % 8);
        return ((packed[byteIndex] >> bitIndex) & 1) != 0;
    }

    [Fact]
    public void ManagedEncodeThenManagedDecode_RoundTripsExactlyAtQuality0()
    {
        const int width = 37, height = 23;
        byte[] source = PackedBitmap(width, height, (x, y) => ((x / 3) + (y / 2)) % 2 == 0);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeBitmap, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        int rowBytes = (width + 7) / 8;
        int px = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte expected = GetBit(source, rowBytes, x, y) ? (byte)255 : (byte)0;
                Assert.Equal(expected, bgraResult![px]);
                Assert.Equal(expected, bgraResult[px + 1]);
                Assert.Equal(expected, bgraResult[px + 2]);
                Assert.Equal(255, bgraResult[px + 3]);
                px += 4;
            }
        }
    }

    [Fact]
    public void ManagedEncodeThenNativeOracleDecodesRaw_BitsAreByteExact()
    {
        const int width = 37, height = 23;
        byte[] source = PackedBitmap(width, height, (x, y) => ((x * 7) + (y * 13)) % 5 == 0);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeBitmap, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 1, [0], out byte[]? oracleRaw, out int oracleWidth, out int oracleHeight);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded Bitmap file.");
        Assert.Equal(width, oracleWidth);
        Assert.Equal(height, oracleHeight);

        // Lossless (quality=0), no offset applied on either side (PgfColorConversion.EncodeBitmapToY's
        // own doc comment) - the real oracle's own packed reconstruction must equal this port's
        // source bits exactly.
        Assert.Equal(source, oracleRaw);
    }

    [Fact]
    public void AllOnes_RoundTripsExactly()
    {
        const int width = 16, height = 16;
        byte[] source = PackedBitmap(width, height, (_, _) => true);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeBitmap, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);
        Assert.All(bgraResult!, b => Assert.Equal(255, b));
    }

    [Fact]
    public void AllZeros_RoundTripsExactly()
    {
        const int width = 16, height = 16;
        byte[] source = PackedBitmap(width, height, (_, _) => false);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeBitmap, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        for (int i = 0; i < bgraResult!.Length; i += 4)
        {
            Assert.Equal(0, bgraResult[i]);
            Assert.Equal(0, bgraResult[i + 1]);
            Assert.Equal(0, bgraResult[i + 2]);
            Assert.Equal(255, bgraResult[i + 3]);
        }
    }

    [Theory]
    [MemberData(nameof(EdgeCaseDimensionsData))]
    public void EdgeCaseDimensions_RoundTripLosslessly(int width, int height)
    {
        // Width not a multiple of 8 exercises the trailing-bits-in-last-byte-of-row path
        // (PgfColorConversion.EncodeBitmapToY's "if (cnt < width)" guard).
        byte[] source = PackedBitmap(width, height, (x, y) => ((x * 3) + (y * 5)) % 4 == 0);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeBitmap, out byte[]? pgfBytes);
        Assert.True(encoded, $"Encode failed for {width}x{height}.");

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded, $"Decode failed for {width}x{height}.");

        int rowBytes = (width + 7) / 8;
        int px = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte expected = GetBit(source, rowBytes, x, y) ? (byte)255 : (byte)0;
                Assert.Equal(expected, bgraResult![px]);
                px += 4;
            }
        }
    }

    public static IEnumerable<object[]> EdgeCaseDimensionsData() =>
        TestBitmaps.EdgeCaseDimensions().Select(d => new object[] { d.Width, d.Height });
}
