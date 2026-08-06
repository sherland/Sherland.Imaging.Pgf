using System.Buffers.Binary;
using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 5 exit tests: Groups B/D/F - the 16-/32-bit-per-channel scaled
/// versions of Groups A/C/E (Gray16/Lab48, RGB48, CMYK64) plus Gray32. This is the territory Stage 0's
/// DataT short-&gt;int widening exists for: <c>YuvOffset16</c> (32768) alone exceeds
/// <see cref="short"/>'s range, and <c>YuvOffset31</c> (2^30) is nowhere close. Decode always
/// downscales to this port's mandatory 8-bit BGRA32 output using the same real <c>bpp==8</c>
/// downscale branch <c>GetBitmap</c> itself offers callers - for the two pure-offset modes (Gray16/
/// Gray32, no cross-channel transform), the expected output byte is independently, exactly
/// computable (<c>sourceValue &gt;&gt; shift</c>, since the encode/decode offsets cancel exactly),
/// so those tests assert against a hand-derived value, not just "whatever the oracle said" - matching
/// this repo's own Tier 1 test-rig philosophy.
/// </summary>
public class PgfGroupBDFTests
{
    private static byte[] SingleChannel16Gradient(int width, int height)
    {
        byte[] data = new byte[width * height * 2];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort value = (ushort)(((x * 701) + (y * 1301)) % 65536);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt), value);
                cnt += 2;
            }
        }

        return data;
    }

    private static byte[] TripleChannel16Gradient(int width, int height)
    {
        byte[] data = new byte[width * height * 6];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt), (ushort)(((x * 701) + (y * 1301)) % 65536));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt + 2), (ushort)(((x * 503) + (y * 907)) % 65536));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt + 4), (ushort)(((x * 311) + (y * 1103)) % 65536));
                cnt += 6;
            }
        }

        return data;
    }

    private static byte[] QuadChannel16Gradient(int width, int height)
    {
        byte[] data = new byte[width * height * 8];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt), (ushort)(((x * 701) + (y * 1301)) % 65536));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt + 2), (ushort)(((x * 503) + (y * 907)) % 65536));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt + 4), (ushort)(((x * 311) + (y * 1103)) % 65536));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(cnt + 6), (ushort)(((x * 199) + (y * 1601)) % 65536));
                cnt += 8;
            }
        }

        return data;
    }

    private static byte[] SingleChannel32Gradient(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint value = unchecked((uint)(((long)x * 700_001) + ((long)y * 1_300_003)));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cnt), value);
                cnt += 4;
            }
        }

        return data;
    }

    // ---- Gray16: hand-derivable expected output (encode/decode offsets cancel exactly) ----

    [Fact]
    public void Gray16_ManagedEncodeThenNativeOracleDecodesRaw_MatchesHandDerivedExpectedByte()
    {
        byte[] source = SingleChannel16Gradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeGray16, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 8, [0], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded Gray16 file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);

        for (int i = 0; i < 37 * 23; i++)
        {
            ushort value16 = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(i * 2));
            byte expected = (byte)(value16 >> 8); // Clamp8(value16 - 32768 + 32768) >> 8) == value16 >> 8, exactly
            Assert.Equal(expected, oracleRaw![i]);
        }
    }

    [Fact]
    public void Gray16_ManagedEncodeThenManagedDecode_MatchesHandDerivedExpectedByte()
    {
        byte[] source = SingleChannel16Gradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeGray16, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        for (int i = 0; i < 37 * 23; i++)
        {
            ushort value16 = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(i * 2));
            byte expected = (byte)(value16 >> 8);
            int px = i * 4;
            Assert.Equal(expected, bgraResult![px]);
            Assert.Equal(expected, bgraResult[px + 1]);
            Assert.Equal(expected, bgraResult[px + 2]);
            Assert.Equal(255, bgraResult[px + 3]);
        }
    }

    // ---- Gray32: same closed-form derivation, scaled ----

    [Fact]
    public void Gray32_ManagedEncodeThenNativeOracleDecodesRaw_MatchesHandDerivedExpectedByte()
    {
        byte[] source = SingleChannel32Gradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeGray32, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 8, [0], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded Gray32 file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);

        for (int i = 0; i < 37 * 23; i++)
        {
            uint value32 = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(i * 4));
            byte expected = (byte)Math.Clamp(value32 >> 23, 0u, 255u);
            Assert.Equal(expected, oracleRaw![i]);
        }
    }

    // ---- Lab48: shares Group A's per-channel offset transform, scaled to 16 bits, with Group C's
    // chroma-upsample decode structure ----

    [Fact]
    public void Lab48_ManagedEncodeThenManagedDecode_RoundTripsAtQuality0_NoDownsample()
    {
        byte[] source = TripleChannel16Gradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeLab48, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        for (int i = 0; i < 37 * 23; i++)
        {
            ushort l = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan((i * 6) + 0));
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan((i * 6) + 2));
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan((i * 6) + 4));
            int px = i * 4;
            Assert.Equal((byte)(l >> 8), bgraResult![px]);
            Assert.Equal((byte)(a >> 8), bgraResult[px + 1]);
            Assert.Equal((byte)(b >> 8), bgraResult[px + 2]);
            Assert.Equal(255, bgraResult[px + 3]);
        }
    }

    [Fact]
    public void Lab48_ManagedEncodeThenNativeOracleDecodesRaw_ByteExact_WithDownsample()
    {
        byte[] source = TripleChannel16Gradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeLab48, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded Lab48 file.");
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

    // ---- RGB48: real 3-channel YUV transform at 16-bit precision ----

    [Fact]
    public void Rgb48_ManagedEncodeThenNativeOracleDecodesRaw_ByteExact()
    {
        byte[] source = TripleChannel16Gradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeRGB48, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded RGB48 file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < 37 * 23; i++)
        {
            Assert.Equal(oracleRaw![srcCnt], bgraResult![dstCnt]);
            Assert.Equal(oracleRaw[srcCnt + 1], bgraResult[dstCnt + 1]);
            Assert.Equal(oracleRaw[srcCnt + 2], bgraResult[dstCnt + 2]);
            Assert.Equal(255, bgraResult[dstCnt + 3]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    [Fact]
    public void Rgb48_ManagedEncodeThenNativeOracleDecodesRaw_ByteExact_WithDownsample()
    {
        byte[] source = TripleChannel16Gradient(64, 64);
        byte quality = PgfConstants.DownsampleThreshold + 2;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 64, 64, quality, PgfConstants.ImageModeRGB48, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded RGB48 file.");

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

    // ---- CMYK64: real 4-channel YUV+alpha transform at 16-bit precision ----

    /// <summary>CMYK64's <c>GetBitmap</c> case branches on <c>bpp%16==0</c> to pick 16-bit vs 8-bit
    /// output (PGFimage.cpp:2288,2321) - <c>bpp=32</c> (the natural "4 channels x 8 bits" value)
    /// unfortunately also satisfies <c>%16==0</c> (32/16=2 &lt; the real 4-channel stride needed),
    /// landing in the *wrong* branch with an undersized stride. <c>bpp=40</c> is the smallest value
    /// that's a real 8-bit-per-channel request (5 one-byte "channels" &gt;= the real 4,
    /// <c>%16==8</c>) - the oracle's own documented "GetBitmap doesn't touch bytes beyond the real
    /// channel count" contract (<c>pgf_debug_decode_raw</c>'s doc comment) leaves the 5th
    /// (padding) byte per pixel zeroed, stripped out below before comparing.</summary>
    [Fact]
    public void Cmyk64_ManagedEncodeThenNativeOracleDecodesRaw_ByteExact()
    {
        byte[] source = QuadChannel16Gradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, PgfConstants.ImageModeCMYK64, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 40, [0, 1, 2, 3], out byte[]? oracleRawPadded, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded CMYK64 file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < 37 * 23; i++)
        {
            Assert.Equal(oracleRawPadded![srcCnt], bgraResult![dstCnt]);
            Assert.Equal(oracleRawPadded[srcCnt + 1], bgraResult[dstCnt + 1]);
            Assert.Equal(oracleRawPadded[srcCnt + 2], bgraResult[dstCnt + 2]);
            Assert.Equal(oracleRawPadded[srcCnt + 3], bgraResult[dstCnt + 3]);
            srcCnt += 5;
            dstCnt += 4;
        }
    }

    [Theory]
    [InlineData(PgfConstants.ImageModeGray16)]
    [InlineData(PgfConstants.ImageModeRGB48)]
    [InlineData(PgfConstants.ImageModeCMYK64)]
    [InlineData(PgfConstants.ImageModeGray32)]
    public void EdgeCaseDimensions_EncodeAndDecodeSucceed(byte mode)
    {
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            byte[] source = mode switch
            {
                PgfConstants.ImageModeGray16 => SingleChannel16Gradient(width, height),
                PgfConstants.ImageModeRGB48 => TripleChannel16Gradient(width, height),
                PgfConstants.ImageModeCMYK64 => QuadChannel16Gradient(width, height),
                PgfConstants.ImageModeGray32 => SingleChannel32Gradient(width, height),
                _ => throw new InvalidOperationException(),
            };

            bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality: 0, mode, out byte[]? pgfBytes);
            Assert.True(encoded, $"Encode failed for mode {mode} at {width}x{height}.");

            bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (w, h), out (int W, int H) result);
            Assert.True(decoded, $"Decode failed for mode {mode} at {width}x{height}.");
            Assert.Equal(width, result.W);
            Assert.Equal(height, result.H);
        }
    }
}
