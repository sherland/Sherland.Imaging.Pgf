// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

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

    private static void AssertBgraMatchesPackedBits(ReadOnlySpan<byte> bgra, ReadOnlySpan<byte> packed, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        int pixel = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte packedByte = packed[(y * rowBytes) + (x / 8)];
                byte expected = (packedByte & (0x80 >> (x % 8))) != 0 ? (byte)255 : (byte)0;
                Assert.Equal(expected, bgra[pixel]);
                Assert.Equal(expected, bgra[pixel + 1]);
                Assert.Equal(expected, bgra[pixel + 2]);
                Assert.Equal(255, bgra[pixel + 3]);
                pixel += 4;
            }
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLegacyFixture_NativeDecoderRecoversKnownPackedBits(bool clearVersion5)
    {
        // The fixture writer emits pre-Version7 packed Bitmap values. clearVersion5 additionally
        // writes the matching pre-Version5 interleaved HL/LH entropy stream, not merely a falsified
        // header flag. These deliberately odd dimensions exercise both the packed trailing byte and
        // DecodeInterleaved's unequal-subband fixups.
        const int width = 37, height = 23;
        byte[] source = PackedBitmap(width, height, (x, y) => ((x * 11) + (y * 7)) % 9 < 4);

        Assert.True(NativePgfOracle.TryEncodeLegacyBitmap(source, width, height, clearVersion5, out byte[]? pgfBytes));

        Assert.True(NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 1, [0], out byte[]? decoded, out int decodedWidth, out int decodedHeight));
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        if (!clearVersion5)
        {
            // This is the normal historical case, independently reproducible from libpgf 6.14.12:
            // its legacy packed Bitmap input and native decoder round-trip the source exactly.
            Assert.Equal(source, decoded);
        }
        else
        {
            // No historical encoder can produce pre-Version5 interleaved bytes. Native acceptance
            // is nevertheless essential: it proves this test-only writer's payload matches the
            // real DecodeInterleaved layout instead of being only a header mutation. Stage 3 will
            // compare this exact native result against the managed decoder.
            Assert.NotEmpty(decoded!);
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void DecodeSession_ExposesLegacyBitmapVersionFlags(bool clearVersion5, bool expectedVersion5, bool expectedVersion7)
    {
        const int width = 37, height = 23;
        byte[] source = PackedBitmap(width, height, (x, y) => ((x * 5) + (y * 3)) % 7 < 3);
        Assert.True(NativePgfOracle.TryEncodeLegacyBitmap(source, width, height, clearVersion5, out byte[]? pgfBytes));

        PgfDecodeSession? session = PgfDecodeSession.TryOpen(pgfBytes!);
        Assert.NotNull(session);
        Assert.Equal(expectedVersion5, session!.Version5);
        Assert.Equal(expectedVersion7, session.Version7);
    }

    [Theory]
    [InlineData(37, 23, false)]
    [InlineData(37, 23, true)]
    [InlineData(101, 73, false)]
    [InlineData(101, 73, true)]
    [InlineData(257, 131, false)]
    [InlineData(257, 131, true)]
    public void NativeLegacyFixture_ManagedDecodeMatchesNativeDecoder(int width, int height, bool clearVersion5)
    {
        byte[] source = PackedBitmap(width, height, (x, y) => ((x * 17) + (y * 29) + (x * y)) % 13 < 6);
        Assert.True(NativePgfOracle.TryEncodeLegacyBitmap(source, width, height, clearVersion5, out byte[]? pgfBytes));
        Assert.True(NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 1, [0], out byte[]? nativePacked, out _, out _));

        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, _, _) => bgra.ToArray(), out byte[]? managedBgra));
        AssertBgraMatchesPackedBits(managedBgra!, nativePacked!, width, height);
    }

    [Fact]
    public void NativeLegacyFixture_LargeMultiLevelManagedDecodeMatchesNativeDecoder()
    {
        // 2049 x 1027 yields an 8.4 MiB rendered BGRA result and exercises numerous macroblocks,
        // levels, non-aligned packed rows, and the legacy interleaved fixups without committing any
        // opaque megabyte-scale fixture binary.
        const int width = 2049, height = 1027;
        byte[] source = PackedBitmap(width, height, (x, y) => ((x * 1103515245L + y * 12345) & 0x10000) != 0);
        Assert.True(NativePgfOracle.TryEncodeLegacyBitmap(source, width, height, clearVersion5: true, out byte[]? pgfBytes));
        Assert.True(NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 1, [0], out byte[]? nativePacked, out _, out _));

        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, _, _) => bgra.ToArray(), out byte[]? managedBgra));
        AssertBgraMatchesPackedBits(managedBgra!, nativePacked!, width, height);
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
