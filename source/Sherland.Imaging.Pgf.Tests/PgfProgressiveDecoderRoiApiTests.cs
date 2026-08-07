// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 3 exit tests (new-features/pgf-roi-support.md) - <b>only</b> the part of
/// <see cref="PgfProgressiveDecoder"/>'s new ROI API surface (<see cref="PgfProgressiveDecoder.TrySetRoi"/>/
/// <see cref="PgfProgressiveDecoder.TryGetAlignedRoi"/>) that's actually testable without a real
/// ROI-flagged bitstream to decode: no real digiKam thumbnail and no file this port's own encoder
/// produces today has the <c>PGFROI</c> version flag set (Stage 4 is what makes that possible), so
/// there is currently no way to exercise <c>SkipTileBuffer</c>/tile-relevant <c>PlaceTile</c>/the
/// generalized <c>InverseTransform</c> against real bytes - see this PRD's Progress log for the
/// explicit finding and reordering: substantive decode-correctness testing (self-consistency and
/// cross-implementation) is deferred to Stage 4's round-trip test, the earliest point real
/// ROI-flagged bytes exist. This file covers what Stage 3 genuinely owns on its own: the public
/// API's own contract (bounds validation, the file-level <c>RoiSupported</c> gate, and this port's
/// one-ROI-per-session divergence), using the existing non-ROI-flagged sample fixture.
/// </summary>
public class PgfProgressiveDecoderRoiApiTests
{
    private static byte[] SampleBytes => File.ReadAllBytes(TestFixtures.SampleThumbnailPath);

    [Fact]
    public void TrySetRoi_OnNonRoiFlaggedFile_ReturnsFalse()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(SampleBytes);
        Assert.NotNull(decoder);

        // The sample fixture (like every real file this app has ever produced/consumed) has no
        // PGFROI version flag - mirrors the native's own ROIisSupported()==false case, but this
        // port fails closed (returns false) rather than the native's silent full-decode fallback.
        bool result = decoder.TrySetRoi(new PgfRoi(0, 0, decoder.Width, decoder.Height));

        Assert.False(result);
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)] // negative left
    [InlineData(0, -1, 10, 10)] // negative top
    public void TrySetRoi_WithOutOfBoundsTopLeft_ReturnsFalse(int left, int top, int right, int bottom)
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(SampleBytes);
        Assert.NotNull(decoder);

        bool result = decoder.TrySetRoi(new PgfRoi(left, top, right, bottom));

        Assert.False(result);
    }

    [Fact]
    public void TrySetRoi_WithLeftOrTopAtOrBeyondImageBounds_ReturnsFalse()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(SampleBytes);
        Assert.NotNull(decoder);

        // matches the native's own ASSERT(rect.left < width && rect.top < height)
        bool result = decoder.TrySetRoi(new PgfRoi(decoder.Width, 0, decoder.Width + 1, 1));

        Assert.False(result);
    }

    [Fact]
    public void TryGetAlignedRoi_BeforeTrySetRoi_ReturnsFalse()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(SampleBytes);
        Assert.NotNull(decoder);

        bool result = decoder.TryGetAlignedRoi(0, out PgfRoi aligned);

        Assert.False(result);
        Assert.Equal(default, aligned);
    }

    [Fact]
    public void TrySetRoi_CalledTwice_ThrowsInvalidOperationException()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(SampleBytes);
        Assert.NotNull(decoder);

        // Even though the first call returns false (file isn't ROI-flagged), the one-per-session
        // guard is about call sequencing, not success - this port's session-per-open divergence
        // (this PRD's "Open questions" third item) still applies regardless of outcome.
        decoder.TrySetRoi(new PgfRoi(0, 0, decoder.Width, decoder.Height));

        Assert.Throws<InvalidOperationException>(
            () => decoder.TrySetRoi(new PgfRoi(0, 0, decoder.Width, decoder.Height)));
    }

    [Fact]
    public void TrySetRoi_AfterDecodingHasStarted_ThrowsInvalidOperationException()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(SampleBytes);
        Assert.NotNull(decoder);
        Assert.True(decoder.Levels > 0);

        bool decoded = decoder.TryDecodeLevel(decoder.Levels - 1, static (_, w, h) => (w, h), out _);
        Assert.True(decoded);

        Assert.Throws<InvalidOperationException>(
            () => decoder.TrySetRoi(new PgfRoi(0, 0, decoder.Width, decoder.Height)));
    }
}
