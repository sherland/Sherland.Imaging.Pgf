// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 3 exit tests (new-features/pgf-user-data-and-small-images.md): Goal 3's "second look" at
/// header-parsing code beyond user data specifically. The level-length array and post-header size
/// (color table + user data) were re-verified as already safe (byte-bounded <c>NLevels</c>,
/// <c>PgfMemoryReader.Read</c>'s own truncate-rather-than-overrun contract, plus Stage 1's explicit
/// user-data-length bound) - no change needed there, confirmed by <see cref="PgfUserDataTests"/>'s
/// existing corrupted-length cases and <see cref="PgfHeaderTests"/>'s truncated-stream cases.
///
/// This file covers what the review actually found broken: <c>PgfDecodeSession.TryOpen</c>'s
/// unconditional <c>checked((int)header.Width)</c>/<c>Height</c> cast threw an uncaught
/// <see cref="OverflowException"/> - not a <see cref="PgfFormatException"/>/<see cref="PgfStreamException"/>,
/// so none of this codec's existing catch blocks caught it - for any header declaring a width/height
/// that doesn't fit in an <see langword="int"/>. A second, related overflow existed one level down:
/// individually-int-sized dimensions whose product (times 4, for the BGRA buffer) overflows int,
/// which <c>PgfImageDecoder.TryDecode</c>/<c>PgfProgressiveDecoder.TryDecodeLevel</c>'s own
/// <c>checked(width * height * 4)</c> would then throw on instead. Both are now bounded once, in
/// <c>TryOpen</c>, so every downstream <c>checked(...)</c> can never actually overflow.
/// </summary>
public class PgfUntrustedLengthTests
{
    private static byte[] BuildRgbaHeaderOnlyBytes(uint width, uint height)
    {
        PgfHeader header = new(
            Width: width,
            Height: height,
            NLevels: 3, // any nonzero value - these tests never reach real level decode
            Quality: 0,
            Bpp: 32,
            Channels: 4,
            Mode: PgfConstants.ImageModeRGBA,
            UsedBitsPerChannel: 8,
            VersionNumberRaw: PgfHeader.PackVersionNumber(PgfConstants.CodecMajor, PgfConstants.CodecYear, PgfConstants.CodecWeek));

        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header);
        return writer.WrittenSpan.ToArray();
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 32u)] // doesn't fit in an int at all
    [InlineData(32u, 0xFFFFFFFFu)]
    [InlineData((uint)int.MaxValue + 1, 32u)]
    public void TryDecode_WidthOrHeightDoesNotFitInInt_FailsClosed_WithoutThrowing(uint width, uint height)
    {
        byte[] pgfBytes = BuildRgbaHeaderOnlyBytes(width, height);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes, static (span, w, h) => span.Length, out int _);

        Assert.False(decoded);
    }

    [Theory]
    [InlineData(100_000u, 100_000u)] // each individually well within int range, product*4 is not
    [InlineData(60_000u, 60_000u)]
    public void TryDecode_WidthTimesHeightOverflowsBufferSize_FailsClosed_WithoutThrowing(uint width, uint height)
    {
        byte[] pgfBytes = BuildRgbaHeaderOnlyBytes(width, height);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes, static (span, w, h) => span.Length, out int _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryOpen_ProgressiveDecoder_HugeWidth_FailsClosed_WithoutThrowing()
    {
        byte[] pgfBytes = BuildRgbaHeaderOnlyBytes(0xFFFFFFFFu, 32u);

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(pgfBytes);

        Assert.Null(progressive);
    }

    [Fact]
    public void TryDecode_ZeroWidthOrHeight_FailsClosed_WithoutThrowing()
    {
        byte[] pgfBytes = BuildRgbaHeaderOnlyBytes(0u, 32u);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes, static (span, w, h) => span.Length, out int _);

        Assert.False(decoded);
    }

    /// <summary>Regression proving a normal, real-sized image is unaffected by the new bound - the
    /// point of this stage is to reject genuinely-overflowing dimensions, not to narrow the format's
    /// real supported range.</summary>
    [Fact]
    public void TryDecode_NormalSizedImage_StillDecodesCorrectly_UnaffectedByNewBound()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? encoded));

        bool decoded = PgfImageDecoder.TryDecode(encoded!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }
}
