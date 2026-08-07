// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf;
using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 1 exit tests: header round-trip (write in C#, real native
/// <c>Open()</c> reads it back correctly) for every mode this PRD covers - the same "early, cheap
/// cross-implementation win" the base PRD's own Stage 4 used, now extended past RGBA. Proves
/// <see cref="PgfHeaderIO"/>'s widened mode-acceptance (<see cref="PgfModeInfo"/>) actually produces
/// header bytes the real, unmodified <c>CompleteHeader()</c> validation accepts - not just bytes
/// this port's own reader accepts, which would prove nothing about the real format.
/// </summary>
public class PgfModeHeaderTests
{
    public static IEnumerable<object[]> AllCoveredModes { get; } =
    [
        [PgfConstants.ImageModeBitmap, "Bitmap"],
        [PgfConstants.ImageModeGrayScale, "GrayScale"],
        [PgfConstants.ImageModeIndexedColor, "IndexedColor"],
        [PgfConstants.ImageModeHSLColor, "HSLColor"],
        [PgfConstants.ImageModeHSBColor, "HSBColor"],
        [PgfConstants.ImageModeLabColor, "LabColor"],
        [PgfConstants.ImageModeRGBColor, "RGBColor"],
        [PgfConstants.ImageModeGray16, "Gray16"],
        [PgfConstants.ImageModeLab48, "Lab48"],
        [PgfConstants.ImageModeRGB48, "RGB48"],
        [PgfConstants.ImageModeRGBA, "RGBA"],
        [PgfConstants.ImageModeCMYKColor, "CMYKColor"],
        [PgfConstants.ImageModeCMYK64, "CMYK64"],
        [PgfConstants.ImageModeGray32, "Gray32"],
        [PgfConstants.ImageModeRGB12, "RGB12"],
        [PgfConstants.ImageModeRGB16, "RGB16"],
    ];

    [Theory]
    [MemberData(nameof(AllCoveredModes))]
    public void Write_ThenNativeOracleOpensIt_ReportsCorrectHeaderShape(byte mode, string _)
    {
        PgfHeader header = PgfHeaderIO.CreateForMode(width: 64, height: 48, quality: 0, mode);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header);

        bool ok = NativePgfOracle.TryGetHeaderInfo(
            writer.WrittenSpan, out int width, out int height, out int levels,
            out byte oracleMode, out byte oracleBpp, out byte oracleChannels, out byte oracleUsedBits);

        Assert.True(ok, $"Native Open() rejected a C#-written header for mode {mode}.");
        Assert.Equal(64, width);
        Assert.Equal(48, height);
        Assert.Equal(header.NLevels, levels);
        Assert.Equal(mode, oracleMode);
        Assert.Equal(header.Bpp, oracleBpp);
        Assert.Equal(header.Channels, oracleChannels);
        Assert.Equal(header.UsedBitsPerChannel, oracleUsedBits);
    }

    [Theory]
    [MemberData(nameof(AllCoveredModes))]
    public void Write_ThenReadBack_RoundTripsHeaderExactly(byte mode, string _)
    {
        // IndexedColor needs its color table present to read back at all (see
        // Write_WithoutColorTable_IndexedColorMode_OmitsColorTable) - its own round trip is covered
        // separately by IndexedColor_ColorTable_RoundTripsExactly_AndNativeOracleOpensIt.
        if (mode == PgfConstants.ImageModeIndexedColor)
        {
            return;
        }

        PgfHeader original = PgfHeaderIO.CreateForMode(width: 37, height: 23, quality: 4, mode);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, original);

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        (PgfPreHeader _, PgfHeader roundTripped, uint[] levelLengths, byte[]? colorTable, PgfUserData _) = PgfHeaderIO.Read(reader);

        Assert.Equal(original, roundTripped);
        Assert.Equal(original.NLevels, levelLengths.Length);
        Assert.Null(colorTable);
    }

    [Fact]
    public void TryGetBppAndChannels_UnsupportedMode_ReturnsFalse()
    {
        // The reserved Adobe modes this PRD's Non-goals explicitly exclude (Multichannel/Duotone/
        // DeepMultichannel/Duotone16), plus a genuinely unknown byte.
        Assert.False(PgfModeInfo.TryGetBppAndChannels(7, out _, out _));
        Assert.False(PgfModeInfo.TryGetBppAndChannels(8, out _, out _));
        Assert.False(PgfModeInfo.TryGetBppAndChannels(14, out _, out _));
        Assert.False(PgfModeInfo.TryGetBppAndChannels(15, out _, out _));
        Assert.False(PgfModeInfo.TryGetBppAndChannels(200, out _, out _));
    }

    [Fact]
    public void CreateForMode_UnsupportedMode_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PgfHeaderIO.CreateForMode(64, 64, 0, mode: 7));

    /// <summary>The color table's own round trip: a known, non-trivial 256-entry palette survives
    /// write -&gt; read byte-exact, and the real native oracle still opens the file successfully
    /// with it present (proving the post-header <c>hSize</c> accounting - PGFimage.cpp:936-938 -
    /// is right, not just that this port's own reader tolerates whatever it itself wrote).</summary>
    [Fact]
    public void IndexedColor_ColorTable_RoundTripsExactly_AndNativeOracleOpensIt()
    {
        byte[] colorTable = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < PgfConstants.ColorTableLen; i++)
        {
            colorTable[(i * 4) + 0] = (byte)(i * 3); // B
            colorTable[(i * 4) + 1] = (byte)(i * 5); // G
            colorTable[(i * 4) + 2] = (byte)(i * 7); // R
            colorTable[(i * 4) + 3] = 0; // reserved
        }

        PgfHeader header = PgfHeaderIO.CreateForMode(width: 32, height: 32, quality: 0, PgfConstants.ImageModeIndexedColor);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header, colorTable);

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        (PgfPreHeader _, PgfHeader _, uint[] _, byte[]? roundTripped, PgfUserData _) = PgfHeaderIO.Read(reader);

        Assert.NotNull(roundTripped);
        Assert.Equal(colorTable, roundTripped);

        bool ok = NativePgfOracle.TryGetHeaderInfo(
            writer.WrittenSpan, out int width, out int height, out int _,
            out byte _, out byte _, out byte _, out byte _);
        Assert.True(ok, "Native Open() rejected a C#-written IndexedColor header with a color table.");
        Assert.Equal(32, width);
        Assert.Equal(32, height);
    }

    [Fact]
    public void Write_WithoutColorTable_IndexedColorMode_OmitsColorTable()
    {
        PgfHeader header = PgfHeaderIO.CreateForMode(width: 16, height: 16, quality: 0, PgfConstants.ImageModeIndexedColor);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header); // no color table passed

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());

        // No color table on the wire means postHeaderSize < ColorTableSize once level-length parsing
        // gets there - Read must fail closed (a genuinely malformed IndexedColor file), not silently
        // treat the level-length array as color table bytes.
        Assert.Throws<PgfFormatException>(() => PgfHeaderIO.Read(reader));
    }
}
