// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf;
using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 4 exit tests (new-features/managed-pgf-codec.md): header read against the real fixture
/// (cross-checked against the native oracle where one exists), and header write cross-validated
/// against the real native decoder - the strongest possible proof for this stage, since a single
/// wrong byte anywhere in the pre-header/header would make the native decoder's own <c>Open()</c>
/// fail or report the wrong dimensions.
/// </summary>
public class PgfHeaderTests
{
    [Fact]
    public void Read_RealFixture_WidthAndHeightMatchNativeOracle()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        Assert.True(NativePgfOracle.TryGetDimensions(pgfBytes, out int oracleWidth, out int oracleHeight));

        PgfMemoryReader reader = new(pgfBytes);
        (PgfPreHeader _, PgfHeader header, uint[] _, byte[]? _, PgfUserData _) = PgfHeaderIO.Read(reader);

        Assert.Equal((uint)oracleWidth, header.Width);
        Assert.Equal((uint)oracleHeight, header.Height);
    }

    [Fact]
    public void Read_RealFixture_LevelCountMatchesNativeOracle()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        Assert.True(NativePgfOracle.TryGetLevelCount(pgfBytes, out int oracleLevels));

        PgfMemoryReader reader = new(pgfBytes);
        (PgfPreHeader _, PgfHeader header, uint[] levelLengths, byte[]? _, PgfUserData _) = PgfHeaderIO.Read(reader);

        Assert.Equal((byte)oracleLevels, header.NLevels);
        Assert.Equal(oracleLevels, levelLengths.Length);
    }

    [Fact]
    public void Read_RealFixture_ReportsPlausibleFields()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);

        PgfMemoryReader reader = new(pgfBytes);
        (PgfPreHeader preHeader, PgfHeader header, uint[] levelLengths, byte[]? _, PgfUserData _) = PgfHeaderIO.Read(reader);

        // The existing pgf_decode_bgra oracle (PictTag.Data.Tests.PgfDecoderTests, already passing)
        // requires Channels()==4 to succeed at all - this fixture is proven RGBA-compatible already.
        Assert.Equal(4, header.Channels);
        Assert.Equal(32, header.Bpp);
        Assert.True(header.Quality <= PgfConstants.MaxQuality);
        Assert.True(header.NLevels > 0, "A real (non-degenerate-sized) thumbnail should never hit the nLevels=0 fallback.");
        Assert.All(levelLengths, len => Assert.True(len > 0, "Every real level should have a nonzero encoded length."));
        Assert.True((preHeader.VersionFlags & PgfVersionFlags.Version6) != 0, "Any modern real PGF file sets Version6.");
    }

    [Fact]
    public void Read_GarbageBytes_ThrowsPgfFormatException_WithoutCrashing()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04];
        PgfMemoryReader reader = new(garbage);

        Assert.Throws<PgfFormatException>(() => PgfHeaderIO.Read(reader));
    }

    [Fact]
    public void Read_TruncatedRealFixture_ThrowsCleanly_WithoutCrashing()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        byte[] truncated = pgfBytes[..5]; // cuts off mid magic/version/hSize

        PgfMemoryReader reader = new(truncated);

        Assert.ThrowsAny<Exception>(() => PgfHeaderIO.Read(reader));
    }

    [Theory]
    [InlineData(10u, 10u)]
    [InlineData(64u, 64u)]
    [InlineData(37u, 23u)]
    [InlineData(200u, 150u)]
    public void Write_ThenNativeOracleOpensIt_ReportsCorrectWidthAndHeight(uint width, uint height)
    {
        PgfHeader header = PgfHeaderIO.CreateForEncode(width, height, quality: 0);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header);

        Assert.True(NativePgfOracle.TryGetDimensions(writer.WrittenSpan, out int oracleWidth, out int oracleHeight));
        Assert.Equal((int)width, oracleWidth);
        Assert.Equal((int)height, oracleHeight);
    }

    [Theory]
    [InlineData(10u, 10u)]
    [InlineData(64u, 64u)]
    [InlineData(37u, 23u)]
    [InlineData(200u, 150u)]
    public void Write_ThenNativeOracleOpensIt_ReportsCorrectLevelCount(uint width, uint height)
    {
        PgfHeader header = PgfHeaderIO.CreateForEncode(width, height, quality: 0);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header);

        Assert.True(NativePgfOracle.TryGetLevelCount(writer.WrittenSpan, out int oracleLevels));
        Assert.Equal(header.NLevels, oracleLevels);
    }

    [Fact]
    public void Write_ThenReadBack_RoundTripsAllFieldsExactly()
    {
        PgfHeader original = PgfHeaderIO.CreateForEncode(width: 123, height: 77, quality: 4);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, original);

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        (PgfPreHeader preHeader, PgfHeader roundTripped, uint[] levelLengths, byte[]? _, PgfUserData _) = PgfHeaderIO.Read(reader);

        Assert.Equal(original, roundTripped);
        Assert.Equal(original.NLevels, levelLengths.Length);
        Assert.All(levelLengths, len => Assert.Equal(0u, len)); // still-unpatched placeholder
        Assert.Equal(PgfConstants.EncoderVersionFlags, preHeader.VersionFlags);
    }

    [Theory]
    [InlineData(10u, 10u, 0)] // lossless: no level-size reduction target, but ComputeLevels is quality-independent
    [InlineData(100u, 100u, 0)]
    [InlineData(101u, 100u, 0)]
    [InlineData(1000u, 1000u, 0)]
    public void ComputeLevels_MatchesNativeOracle_AcrossDimensions(uint width, uint height, byte quality)
    {
        PgfHeader header = PgfHeaderIO.CreateForEncode(width, height, quality);
        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header);

        Assert.True(NativePgfOracle.TryGetLevelCount(writer.WrittenSpan, out int oracleLevels));
        Assert.Equal(oracleLevels, header.NLevels);
    }
}
