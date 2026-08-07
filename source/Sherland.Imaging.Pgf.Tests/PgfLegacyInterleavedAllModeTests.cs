// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>Cross-implementation evidence for the generic pre-Version5 entropy path. The native
/// side is exercised only through <c>GetBitmap</c>, avoiding <c>pgf_debug_decode_channel</c>'s known
/// repeated-call crash risk (pgf-legacy-interleaved-decode.md Stage 3).</summary>
public class PgfLegacyInterleavedAllModeTests
{
    public static IEnumerable<object[]> Modes()
    {
        foreach (byte mode in new[] { PgfConstants.ImageModeGrayScale, PgfConstants.ImageModeIndexedColor,
            PgfConstants.ImageModeHSLColor, PgfConstants.ImageModeHSBColor, PgfConstants.ImageModeLabColor,
            PgfConstants.ImageModeRGBColor, PgfConstants.ImageModeRGBA, PgfConstants.ImageModeCMYKColor,
            PgfConstants.ImageModeGray16, PgfConstants.ImageModeLab48, PgfConstants.ImageModeRGB48,
            PgfConstants.ImageModeCMYK64, PgfConstants.ImageModeGray32, PgfConstants.ImageModeRGB12,
            PgfConstants.ImageModeRGB16 })
        {
            Assert.True(PgfModeInfo.TryGetBppAndChannels(mode, out byte bpp, out byte channels));
            yield return [mode, bpp, channels];
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void NativeLegacyInterleaved_SafeNativeAndManagedDecodesMatchModern(
        byte mode, byte bpp, byte channels)
    {
        AssertEquivalentCase(mode, bpp, channels, width: 64, height: 48);
        AssertEquivalentCase(mode, bpp, channels, width: 37, height: 23);
    }

    [Fact]
    public void NativeLegacyInterleaved_LargeMultiLevelRgbaMatchesModern()
    {
        const int width = 2049;
        const int height = 1027;
        Assert.True((long)width * height * 4 > 1_000_000);

        AssertEquivalentCase(PgfConstants.ImageModeRGBA, bpp: 32, channels: 4, width, height,
            requireMultipleLevels: true);
    }

    private static void AssertEquivalentCase(
        byte mode, byte bpp, byte channels, int width, int height, bool requireMultipleLevels = false)
    {
        byte[] source = CreateSource(width, height, bpp);
        byte[] palette = CreatePalette(mode);

        Assert.True(NativePgfOracle.TryEncodeMode(
            source, width, height, quality: 0, mode, bpp, channels, palette, out byte[]? modernPgf));
        Assert.True(NativePgfOracle.TryEncodeLegacyInterleavedMode(
            source, width, height, quality: 0, mode, bpp, channels, palette, out byte[]? legacyPgf));

        Assert.True(NativePgfOracle.TryGetHeaderInfo(
            legacyPgf!, out int headerWidth, out int headerHeight, out int levels,
            out _, out _, out _, out _));
        Assert.Equal(width, headerWidth);
        Assert.Equal(height, headerHeight);
        if (requireMultipleLevels)
        {
            Assert.True(levels > 1, $"Expected a multi-level fixture, got {levels} level(s).");
        }

        PgfDecodeSession? modernSession = PgfDecodeSession.TryOpen(modernPgf);
        Assert.NotNull(modernSession);
        Assert.True(modernSession!.Version5);

        PgfDecodeSession? legacySession = PgfDecodeSession.TryOpen(legacyPgf);
        Assert.NotNull(legacySession);
        Assert.False(legacySession!.Version5);

        int[] channelMap = Enumerable.Range(0, channels).ToArray();
        Assert.True(NativePgfOracle.TryDecodeRaw(
            modernPgf!, bpp, channelMap, out byte[]? modernNativeRaw,
            out int modernNativeWidth, out int modernNativeHeight));
        Assert.True(NativePgfOracle.TryDecodeRaw(
            legacyPgf, bpp, channelMap, out byte[]? legacyNativeRaw,
            out int legacyNativeWidth, out int legacyNativeHeight));
        Assert.Equal((modernNativeWidth, modernNativeHeight), (legacyNativeWidth, legacyNativeHeight));
        Assert.Equal(modernNativeRaw, legacyNativeRaw);

        Assert.True(PgfImageDecoder.TryDecode(
            modernPgf, static (bgra, _, _) => bgra.ToArray(), out byte[]? modernManagedBgra,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(PgfImageDecoder.TryDecode(
            legacyPgf, static (bgra, _, _) => bgra.ToArray(), out byte[]? legacyManagedBgra,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(modernManagedBgra, legacyManagedBgra);
    }

    private static byte[] CreateSource(int width, int height, byte bpp)
    {
        int rowBytes = checked((int)(((long)width * bpp + 7) / 8));
        byte[] source = new byte[checked(rowBytes * height)];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)((i * 73L + ((i / rowBytes) * 29L) + 19) & 0xFF);
        }

        return source;
    }

    private static byte[] CreatePalette(byte mode)
    {
        if (mode != PgfConstants.ImageModeIndexedColor)
        {
            return [];
        }

        byte[] palette = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < PgfConstants.ColorTableLen; i++)
        {
            palette[(i * 4) + 0] = (byte)(i * 3);
            palette[(i * 4) + 1] = (byte)(255 - i);
            palette[(i * 4) + 2] = (byte)(i ^ 0x5A);
            palette[(i * 4) + 3] = 255;
        }

        return palette;
    }
}
