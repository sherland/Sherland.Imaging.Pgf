using System.Buffers.Binary;
using PictTag.PgfCodec.Tests.Oracle;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 7 exit test: Group H (RGB12, RGB16) - genuinely bespoke packed
/// sub-byte/sub-word formats. Per this PRD's own Test Rig guidance ("dedicated, hand-computed unit
/// tests isolating just the packing/unpacking math... given how error-prone hand-rolled bit-packing
/// code is to get right on the first try"), this includes isolated hand-computed encode-side tests
/// (no wavelet transform or entropy coding involved) alongside full-pipeline byte-exact oracle
/// comparisons.
/// </summary>
public class PgfGroupHTests
{
    // ---- RGB12: hand-computed packing/YUV math, isolated from the wavelet/entropy pipeline ----

    [Fact]
    public void EncodeRgb12ToYuv_KnownPixelPair_ComputesExactExpectedValues()
    {
        // Pixel 0: B=3, G=10, R=5. Pixel 1: B=7, G=2, R=12.
        // byte0 = B0 | (G0<<4) = 3 | 160 = 163. byte1 = R0 | (B1<<4) = 5 | 112 = 117.
        // byte2 = G1 | (R1<<4) = 2 | 192 = 194.
        byte[] packed = [163, 117, 194];
        int[] y = new int[2];
        int[] u = new int[2];
        int[] v = new int[2];

        PgfColorConversion.EncodeRgb12ToYuv(packed, width: 2, height: 1, y, u, v);

        // Y0 = ((3 + 2*10 + 5) >> 2) - 8 = (28 >> 2) - 8 = 7 - 8 = -1. U0 = 5-10 = -5. V0 = 3-10 = -7.
        Assert.Equal(-1, y[0]);
        Assert.Equal(-5, u[0]);
        Assert.Equal(-7, v[0]);

        // Y1 = ((7 + 2*2 + 12) >> 2) - 8 = (23 >> 2) - 8 = 5 - 8 = -3. U1 = 12-2 = 10. V1 = 7-2 = 5.
        Assert.Equal(-3, y[1]);
        Assert.Equal(10, u[1]);
        Assert.Equal(5, v[1]);
    }

    [Fact]
    public void EncodeRgb12ToYuv_OddWidth_ReadsDanglingFinalPixelCorrectly()
    {
        // 3 pixels: pair (0,1) uses bytes[0..2], pixel 2 (dangling, even-position read) uses
        // bytes[3..4] - exercises PGFimage.cpp:1701-1719's odd-width final-pixel branch.
        // Pixel 2: B=4, G=9, R=1 -> byte3 = 4 | (9<<4) = 148, byte4 = 1 (low nibble only).
        byte[] packed = [163, 117, 194, 148, 1];
        int[] y = new int[3];
        int[] u = new int[3];
        int[] v = new int[3];

        PgfColorConversion.EncodeRgb12ToYuv(packed, width: 3, height: 1, y, u, v);

        // Y2 = ((4 + 2*9 + 1) >> 2) - 8 = (23 >> 2) - 8 = 5 - 8 = -3. U2 = 1-9 = -8. V2 = 4-9 = -5.
        Assert.Equal(-3, y[2]);
        Assert.Equal(-8, u[2]);
        Assert.Equal(-5, v[2]);
    }

    [Fact]
    public void DecodeYuvToRgb12Bgra_KnownCoefficients_ComputesExactExpectedBgra()
    {
        // Inverse of the encode test above: y=-1, u=-5, v=-7 should reconstruct G=10, R=5, B=3.
        int[] y = [-1];
        int[] u = [-5];
        int[] v = [-7];
        byte[] bgra = new byte[4];

        PgfColorConversion.DecodeYuvToRgb12Bgra(y, u, v, width: 1, height: 1, bgra);

        // 4-bit -> 8-bit bit replication: v*17 (equivalently (v<<4)|v).
        Assert.Equal((byte)(3 * 17), bgra[0]); // B
        Assert.Equal((byte)(10 * 17), bgra[1]); // G
        Assert.Equal((byte)(5 * 17), bgra[2]); // R
        Assert.Equal(255, bgra[3]);
    }

    // ---- RGB16: hand-computed RGB565 math ----

    [Fact]
    public void EncodeRgb16ToYuv_KnownPixel_ComputesExactExpectedValues()
    {
        // R5=17, G6=42, B5=9 -> rgb565 = (17<<11)|(42<<5)|9 = 36169.
        ushort rgb565 = (17 << 11) | (42 << 5) | 9;
        byte[] packed = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(packed, rgb565);
        int[] y = new int[1];
        int[] u = new int[1];
        int[] v = new int[1];

        PgfColorConversion.EncodeRgb16ToYuv(packed, width: 1, height: 1, y, u, v);

        // r = 17*2 = 34, g = 42, b = 9*2 = 18.
        // Y = ((18 + 2*42 + 34) >> 2) - 32 = (136 >> 2) - 32 = 34 - 32 = 2. U = 34-42 = -8. V = 18-42 = -24.
        Assert.Equal(2, y[0]);
        Assert.Equal(-8, u[0]);
        Assert.Equal(-24, v[0]);
    }

    [Fact]
    public void DecodeYuvToRgb16Bgra_KnownCoefficients_ComputesExactExpectedBgra()
    {
        int[] y = [2];
        int[] u = [-8];
        int[] v = [-24];
        byte[] bgra = new byte[4];

        PgfColorConversion.DecodeYuvToRgb16Bgra(y, u, v, width: 1, height: 1, bgra);

        // Reconstructs R5=17, G6=42, B5=9, expanded via standard RGB565->888 bit replication.
        byte expectedB = (byte)((9 << 3) | (9 >> 2));
        byte expectedG = (byte)((42 << 2) | (42 >> 4));
        byte expectedR = (byte)((17 << 3) | (17 >> 2));
        Assert.Equal(expectedB, bgra[0]);
        Assert.Equal(expectedG, bgra[1]);
        Assert.Equal(expectedR, bgra[2]);
        Assert.Equal(255, bgra[3]);
    }

    // ---- Full pipeline: managed encode -> managed decode, and byte-exact against the native oracle ----

    private static byte[] Rgb12Gradient(int width, int height)
    {
        int rowBytes = ((width * 12) + 7) / 8;
        byte[] data = new byte[rowBytes * height];

        for (int row = 0; row < height; row++)
        {
            for (int j = 0; j < width; j++)
            {
                int b = ((j * 3) + (row * 5)) % 16;
                int g = ((j * 7) + (row * 2)) % 16;
                int r = ((j * 11) + (row * 13)) % 16;
                int byteBase = (row * rowBytes) + ((j / 2) * 3);
                if ((j & 1) == 0)
                {
                    data[byteBase] = (byte)(b | (g << 4));
                    data[byteBase + 1] = (byte)r;
                }
                else
                {
                    data[byteBase + 1] |= (byte)(b << 4);
                    data[byteBase + 2] = (byte)(g | (r << 4));
                }
            }
        }

        return data;
    }

    private static byte[] Rgb16Gradient(int width, int height)
    {
        byte[] data = new byte[width * height * 2];
        int cnt = 0;
        for (int row = 0; row < height; row++)
        {
            for (int j = 0; j < width; j++)
            {
                int r5 = ((j * 3) + (row * 7)) % 32;
                int g6 = ((j * 5) + (row * 11)) % 64;
                int b5 = ((j * 13) + (row * 2)) % 32;
                ushort packed = (ushort)((r5 << 11) | (g6 << 5) | b5);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt), packed);
                cnt += 2;
            }
        }

        return data;
    }

    [Fact]
    public void Rgb12_ManagedEncodeThenNativeOracleDecodesRaw_ReconstructsSameValues()
    {
        const int width = 13, height = 11; // odd width exercises the dangling-pixel encode path
        byte[] source = Rgb12Gradient(width, height);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeRGB12, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 12, [0, 1, 2], out byte[]? oracleRaw, out int oracleWidth, out int oracleHeight);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded RGB12 file.");
        Assert.Equal(width, oracleWidth);
        Assert.Equal(height, oracleHeight);

        // Lossless at quality=0: the real oracle's own packed reconstruction must equal this port's
        // source bytes exactly (both directions port the identical bit layout).
        Assert.Equal(source, oracleRaw);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        int rowBytes = ((width * 12) + 7) / 8;
        int px = 0;
        for (int row = 0; row < height; row++)
        {
            for (int j = 0; j < width; j++)
            {
                int byteBase = (row * rowBytes) + ((j / 2) * 3);
                int b, g, r;
                if ((j & 1) == 0)
                {
                    b = source[byteBase] & 0x0F;
                    g = (source[byteBase] & 0xF0) >> 4;
                    r = source[byteBase + 1] & 0x0F;
                }
                else
                {
                    b = (source[byteBase + 1] & 0xF0) >> 4;
                    g = source[byteBase + 2] & 0x0F;
                    r = (source[byteBase + 2] & 0xF0) >> 4;
                }

                Assert.Equal((byte)((b << 4) | b), bgraResult![px]);
                Assert.Equal((byte)((g << 4) | g), bgraResult[px + 1]);
                Assert.Equal((byte)((r << 4) | r), bgraResult[px + 2]);
                px += 4;
            }
        }
    }

    [Fact]
    public void Rgb16_ManagedEncodeThenNativeOracleDecodesRaw_ReconstructsSameValues()
    {
        const int width = 37, height = 23;
        byte[] source = Rgb16Gradient(width, height);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, PgfConstants.ImageModeRGB16, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 16, [0, 1, 2], out byte[]? oracleRaw, out int oracleWidth, out int oracleHeight);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded RGB16 file.");
        Assert.Equal(width, oracleWidth);
        Assert.Equal(height, oracleHeight);
        Assert.Equal(source, oracleRaw);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        int px = 0, srcCnt = 0;
        for (int i = 0; i < width * height; i++)
        {
            ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(srcCnt));
            int r5 = (packed & 0xF800) >> 11;
            int g6 = (packed & 0x07E0) >> 5;
            int b5 = packed & 0x001F;

            Assert.Equal((byte)((b5 << 3) | (b5 >> 2)), bgraResult![px]);
            Assert.Equal((byte)((g6 << 2) | (g6 >> 4)), bgraResult[px + 1]);
            Assert.Equal((byte)((r5 << 3) | (r5 >> 2)), bgraResult[px + 2]);

            px += 4;
            srcCnt += 2;
        }
    }

    [Theory]
    [InlineData(PgfConstants.ImageModeRGB12)]
    [InlineData(PgfConstants.ImageModeRGB16)]
    public void EdgeCaseDimensions_EncodeAndDecodeSucceed(byte mode)
    {
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            byte[] source = mode == PgfConstants.ImageModeRGB12 ? Rgb12Gradient(width, height) : Rgb16Gradient(width, height);

            bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, mode, out byte[]? pgfBytes);
            Assert.True(encoded, $"Encode failed for mode {mode} at {width}x{height}.");

            bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (w, h), out (int W, int H) result);
            Assert.True(decoded, $"Decode failed for mode {mode} at {width}x{height}.");
            Assert.Equal(width, result.W);
            Assert.Equal(height, result.H);
        }
    }
}
