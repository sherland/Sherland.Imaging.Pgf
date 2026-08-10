// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using System.Buffers.Binary;
using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 9/10: the one round-trip leg none of the per-group test files cover -
/// a file produced by the *real native encoder* (not this port's own <c>PgfImageEncoder</c>), read
/// back by this port's own managed decoder. Every per-group test file (<c>PgfGroupATests</c>,
/// <c>PgfGroupCTests</c>, etc.) already proves "C# encode -&gt; native decode" (byte-exact) and
/// "C# encode -&gt; C# decode" (self-consistency); this closes the loop the other direction, for
/// every mode <see cref="PgfImageDecoder.IsModeSupported"/> covers - genuine confirmation that this
/// port's decoder isn't just internally consistent with its own encoder's own possibly-shared
/// assumptions, but reads a truly independent implementation's real output correctly.
/// </summary>
public class PgfNativeEncodeRoundTripTests
{
    private const int Width = 37;
    private const int Height = 23;

    [Fact]
    public void RGBA_NativeEncodeThenManagedDecode_RoundTripsExactly()
    {
        byte[] source = new byte[Width * Height * 4];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            source[cnt] = (byte)(cnt % 251);
            source[cnt + 1] = (byte)((cnt * 3) % 251);
            source[cnt + 2] = (byte)((cnt * 7) % 251);
            source[cnt + 3] = (byte)((cnt * 11) % 251);
            cnt += 4;
        }

        Assert.True(NativePgfOracle.TryEncode(source, Width, Height, quality: 0, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(source, result);
    }

    [Fact]
    public void CMYKColor_NativeEncodeThenManagedDecode_RoundTripsExactly()
    {
        byte[] source = new byte[Width * Height * 4];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            source[cnt] = (byte)(cnt % 251);
            source[cnt + 1] = (byte)((cnt * 3) % 251);
            source[cnt + 2] = (byte)((cnt * 7) % 251);
            source[cnt + 3] = (byte)((cnt * 11) % 251);
            cnt += 4;
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeCMYKColor, bpp: 32, channels: 4, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(PgfConstants.ImageModeGrayScale)]
    [InlineData(PgfConstants.ImageModeHSLColor)]
    [InlineData(PgfConstants.ImageModeHSBColor)]
    public void GroupA_NativeEncodeThenManagedDecode_RoundTripsExactly(byte mode)
    {
        int channels = mode == PgfConstants.ImageModeGrayScale ? 1 : 3;
        byte bpp = (byte)(channels * 8);
        byte[] source = new byte[Width * Height * channels];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)((i * 7) % 256);
        }

        Assert.True(NativePgfOracle.TryEncodeMode(source, Width, Height, quality: 0, mode, bpp, (byte)channels, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                Assert.Equal(source[srcCnt + c], result![dstCnt + c]);
            }

            Assert.Equal(255, result![dstCnt + 3]);
            srcCnt += channels;
            dstCnt += 4;
        }
    }

    [Fact]
    public void IndexedColor_NativeEncodeThenManagedDecode_RoundTripsExactly()
    {
        byte[] source = new byte[Width * Height];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i % 256);
        }

        byte[] colorTable = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < PgfConstants.ColorTableLen; i++)
        {
            colorTable[(i * 4) + 0] = (byte)(i * 3);
            colorTable[(i * 4) + 1] = (byte)(255 - i);
            colorTable[(i * 4) + 2] = (byte)(i ^ 0x5A);
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeIndexedColor, bpp: 8, channels: 1, colorTable, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        for (int i = 0; i < source.Length; i++)
        {
            int px = i * 4;
            int paletteOffset = source[i] * 4;
            Assert.Equal(colorTable[paletteOffset], result![px]);
            Assert.Equal(colorTable[paletteOffset + 1], result[px + 1]);
            Assert.Equal(colorTable[paletteOffset + 2], result[px + 2]);
        }
    }

    [Fact]
    public void RGBColor_NativeEncodeThenManagedDecode_RoundTripsExactly()
    {
        byte[] source = new byte[Width * Height * 3];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)((i * 5) % 256);
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeRGBColor, bpp: 24, channels: 3, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            Assert.Equal(source[srcCnt], result![dstCnt]);
            Assert.Equal(source[srcCnt + 1], result[dstCnt + 1]);
            Assert.Equal(source[srcCnt + 2], result[dstCnt + 2]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    [Fact]
    public void LabColor_NativeEncodeThenManagedDecode_RoundTripsExactly()
    {
        byte[] source = new byte[Width * Height * 3];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)((i * 5) % 256);
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeLabColor, bpp: 24, channels: 3, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            Assert.Equal(source[srcCnt], result![dstCnt]);
            Assert.Equal(source[srcCnt + 1], result[dstCnt + 1]);
            Assert.Equal(source[srcCnt + 2], result[dstCnt + 2]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    [Fact]
    public void Gray16_NativeEncodeThenManagedDecode_MatchesHandDerivedExpectedByte()
    {
        byte[] source = new byte[Width * Height * 2];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt), (ushort)((i * 701) % 65536));
            cnt += 2;
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeGray16, bpp: 16, channels: 1, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        for (int i = 0; i < Width * Height; i++)
        {
            ushort value16 = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(i * 2));
            byte expected = (byte)(value16 >> 8);
            int px = i * 4;
            Assert.Equal(expected, result![px]);
        }
    }

    [Fact]
    public void Gray32_NativeEncodeThenManagedDecode_MatchesHandDerivedExpectedByte()
    {
        byte[] source = new byte[Width * Height * 4];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            uint value = unchecked((uint)(i * 700_001));
            BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(cnt), value);
            cnt += 4;
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeGray32, bpp: 32, channels: 1, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        for (int i = 0; i < Width * Height; i++)
        {
            uint value32 = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(i * 4));
            byte expected = (byte)Math.Clamp(value32 >> 23, 0u, 255u);
            int px = i * 4;
            Assert.Equal(expected, result![px]);
        }
    }

    [Fact]
    public void Lab48_NativeEncodeThenManagedDecode_MatchesHandDerivedExpectedByte()
    {
        byte[] source = new byte[Width * Height * 6];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt), (ushort)((i * 701) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 2), (ushort)((i * 503) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 4), (ushort)((i * 311) % 65536));
            cnt += 6;
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeLab48, bpp: 48, channels: 3, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        for (int i = 0; i < Width * Height; i++)
        {
            ushort l = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan((i * 6) + 0));
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan((i * 6) + 2));
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan((i * 6) + 4));
            int px = i * 4;
            Assert.Equal((byte)(l >> 8), result![px]);
            Assert.Equal((byte)(a >> 8), result[px + 1]);
            Assert.Equal((byte)(b >> 8), result[px + 2]);
        }
    }

    [Fact]
    public void Rgb48_NativeEncodeThenManagedDecode_Succeeds()
    {
        byte[] source = new byte[Width * Height * 6];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt), (ushort)((i * 701) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 2), (ushort)((i * 503) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 4), (ushort)((i * 311) % 65536));
            cnt += 6;
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeRGB48, bpp: 48, channels: 3, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (w, h), out (int W, int H) result, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Width, result.W);
        Assert.Equal(Height, result.H);
    }

    [Fact]
    public void Cmyk64_NativeEncodeThenManagedDecode_Succeeds()
    {
        byte[] source = new byte[Width * Height * 8];
        int cnt = 0;
        for (int i = 0; i < Width * Height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt), (ushort)((i * 701) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 2), (ushort)((i * 503) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 4), (ushort)((i * 311) % 65536));
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt + 6), (ushort)((i * 199) % 65536));
            cnt += 8;
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeCMYK64, bpp: 64, channels: 4, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (w, h), out (int W, int H) result, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Width, result.W);
        Assert.Equal(Height, result.H);
    }

    [Fact]
    public void Bitmap_NativeEncodeThenManagedDecode_RoundTripsExactly()
    {
        int rowBytes = (Width + 7) / 8;
        byte[] source = new byte[rowBytes * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (((x * 7) + (y * 13)) % 5 == 0)
                {
                    source[(y * rowBytes) + (x / 8)] |= (byte)(1 << (7 - (x % 8)));
                }
            }
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeBitmap, bpp: 1, channels: 1, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? result, cancellationToken: TestContext.Current.CancellationToken));

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bool bit = ((source[(y * rowBytes) + (x / 8)] >> (7 - (x % 8))) & 1) != 0;
                byte expected = bit ? (byte)255 : (byte)0;
                int px = ((y * Width) + x) * 4;
                Assert.Equal(expected, result![px]);
            }
        }
    }

    [Fact]
    public void Rgb12_NativeEncodeThenManagedDecode_Succeeds()
    {
        int rowBytes = ((Width * 12) + 7) / 8;
        byte[] source = new byte[rowBytes * Height];
        for (int row = 0; row < Height; row++)
        {
            for (int j = 0; j < Width; j++)
            {
                int b = ((j * 3) + (row * 5)) % 16;
                int g = ((j * 7) + (row * 2)) % 16;
                int r = ((j * 11) + (row * 13)) % 16;
                int byteBase = (row * rowBytes) + ((j / 2) * 3);
                if ((j & 1) == 0)
                {
                    source[byteBase] = (byte)(b | (g << 4));
                    source[byteBase + 1] = (byte)r;
                }
                else
                {
                    source[byteBase + 1] |= (byte)(b << 4);
                    source[byteBase + 2] = (byte)(g | (r << 4));
                }
            }
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeRGB12, bpp: 12, channels: 3, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (w, h), out (int W, int H) result, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Width, result.W);
        Assert.Equal(Height, result.H);
    }

    [Fact]
    public void Rgb16_NativeEncodeThenManagedDecode_Succeeds()
    {
        byte[] source = new byte[Width * Height * 2];
        int cnt = 0;
        for (int row = 0; row < Height; row++)
        {
            for (int j = 0; j < Width; j++)
            {
                int r5 = ((j * 3) + (row * 7)) % 32;
                int g6 = ((j * 5) + (row * 11)) % 64;
                int b5 = ((j * 13) + (row * 2)) % 32;
                ushort packed = (ushort)((r5 << 11) | (g6 << 5) | b5);
                BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(cnt), packed);
                cnt += 2;
            }
        }

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, Width, Height, quality: 0, PgfConstants.ImageModeRGB16, bpp: 16, channels: 3, colorTable: default, out byte[]? pgfBytes));
        Assert.True(PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (w, h), out (int W, int H) result, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Width, result.W);
        Assert.Equal(Height, result.H);
    }
}
