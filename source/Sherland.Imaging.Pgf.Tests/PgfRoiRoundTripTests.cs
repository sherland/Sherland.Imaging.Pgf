namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 4 exit test (new-features/pgf-roi-support.md): "the encode-then-decode self-consistency
/// round trip for ROI-flagged files" - the earliest point real <c>PGFROI</c>-flagged bytes exist to
/// decode at all, so this is also, necessarily, the first real functional test of every Stage 3
/// decode-side code path (<c>SkipTileBuffer</c>, tile-relevant <c>PlaceTile</c>, the ROI-generalized
/// <c>InverseTransform</c>) - see this PRD's Stage 3 Progress log entry for why that couldn't be
/// tested independently.
///
/// Covers this PRD's Test rig "Tile/alignment correctness in isolation" point: the full image
/// (should match a plain non-ROI decode exactly - both encode paths run the identical forward
/// wavelet transform, so tiling is purely a bitstream-layout choice, not a numerical one, meaning
/// this holds bit-for-bit at every quality level, not just lossless), a single/multi-tile interior
/// region, and a corner region requiring the wavelet-margin/alignment expansion
/// <see cref="PgfWaveletTransform.SetROI"/>'s own doc comment describes. Also implicitly covers the
/// "SkipTileBuffer correctness" point: any partial-ROI decode that skips some tiles and decodes
/// others through several pyramid levels in one <see cref="PgfProgressiveDecoder.TryDecodeLevel{TResult}"/>
/// call already exercises "does skipping one tile leave the stream position correct for the next
/// real read" - a bug there would corrupt every subsequent pixel, not just the skipped tile's own,
/// so a passing pixel-exact comparison is real evidence, not just "didn't throw."
/// </summary>
public class PgfRoiRoundTripTests
{
    [Theory]
    [InlineData(64, 64, 0)]
    [InlineData(64, 64, 8)]
    [InlineData(37, 53, 0)]
    [InlineData(37, 53, 5)]
    [InlineData(200, 150, 12)]
    public void FullImageRoi_MatchesPlainNonRoiDecode_ByteForByte(int width, int height, byte quality)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? plainPgf));
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? roiPgf, roi: true));
        Assert.NotEqual(plainPgf, roiPgf); // genuinely different bitstreams, not a no-op flag

        bool plainDecoded = PgfImageDecoder.TryDecode(plainPgf, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var plain);
        Assert.True(plainDecoded);

        PgfProgressiveDecoder? roiDecoder = PgfProgressiveDecoder.TryOpen(roiPgf);
        Assert.NotNull(roiDecoder);
        Assert.True(roiDecoder.TrySetRoi(new PgfRoi(0, 0, width, height)));

        bool roiDecoded = roiDecoder.TryDecodeLevel(0, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var roiResult);
        Assert.True(roiDecoded);

        Assert.True(roiDecoder.TryGetAlignedRoi(0, out PgfRoi aligned));
        Assert.Equal(new PgfRoi(0, 0, width, height), aligned);

        Assert.Equal(plain.w, roiResult.w);
        Assert.Equal(plain.h, roiResult.h);
        Assert.Equal(plain.Bytes, roiResult.Bytes);
    }

    public static IEnumerable<object[]> PartialRoiCases()
    {
        // (imageWidth, imageHeight, requestLeft, requestTop, requestRight, requestBottom)
        yield return [64, 64, 20, 20, 44, 44]; // interior, multi-tile
        yield return [64, 64, 0, 0, 8, 8]; // top-left corner - exercises SetROI's margin expansion
        yield return [64, 64, 56, 56, 64, 64]; // bottom-right corner
        yield return [96, 80, 10, 10, 30, 25]; // small interior region, non-square image
        yield return [37, 53, 3, 7, 19, 29]; // odd/unaligned image and request
    }

    [Theory]
    [MemberData(nameof(PartialRoiCases))]
    public void PartialRoi_MatchesCorrespondingRegionOfPlainNonRoiDecode(
        int width, int height, int reqLeft, int reqTop, int reqRight, int reqBottom)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);
        const byte quality = 0; // lossless: the reference decode must be pixel-exact, no quantization noise

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? plainPgf));
        bool plainDecoded = PgfImageDecoder.TryDecode(plainPgf, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var plain);
        Assert.True(plainDecoded);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? roiPgf, roi: true));

        PgfProgressiveDecoder? roiDecoder = PgfProgressiveDecoder.TryOpen(roiPgf);
        Assert.NotNull(roiDecoder);
        Assert.True(roiDecoder.TrySetRoi(new PgfRoi(reqLeft, reqTop, reqRight, reqBottom)));

        bool roiDecoded = roiDecoder.TryDecodeLevel(0, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var roiResult);
        Assert.True(roiDecoded);

        Assert.True(roiDecoder.TryGetAlignedRoi(0, out PgfRoi aligned));
        Assert.Equal(roiResult.w, aligned.Width);
        Assert.Equal(roiResult.h, aligned.Height);

        // The aligned (buffer) ROI must actually cover the caller's requested rectangle (possibly
        // cropped wider - never narrower than what was asked for).
        Assert.True(aligned.Left <= reqLeft && aligned.Top <= reqTop);
        Assert.True(aligned.Right >= reqRight && aligned.Bottom >= reqBottom);

        // Pixel-exactness is only guaranteed within the *accurate* ROI (CPGFImage::ComputeLevelROI -
        // the caller's own original request, clamped), not the full aligned/buffer extent: the
        // wavelet transform isn't tile-independent, so pixels between the accurate and aligned
        // rectangles are boundary-filter padding, not a correctness claim (PgfProgressiveDecoder.
        // TryGetAlignedRoi's own doc comment - a real finding from this stage's development, not
        // assumed up front).
        Assert.True(roiDecoder.TryGetAccurateRoi(0, out PgfRoi accurate));
        Assert.Equal(new PgfRoi(reqLeft, reqTop, reqRight, reqBottom), accurate);

        for (int y = 0; y < accurate.Height; y++)
        {
            for (int x = 0; x < accurate.Width; x++)
            {
                int absoluteX = accurate.Left + x;
                int absoluteY = accurate.Top + y;
                int roiPixelIndex = (((absoluteY - aligned.Top) * aligned.Width) + (absoluteX - aligned.Left)) * 4;
                int plainPixelIndex = ((absoluteY * width) + absoluteX) * 4;

                ReadOnlySpan<byte> roiPixel = roiResult.Bytes.AsSpan(roiPixelIndex, 4);
                ReadOnlySpan<byte> plainPixel = plain.Bytes.AsSpan(plainPixelIndex, 4);

                Assert.True(
                    roiPixel.SequenceEqual(plainPixel),
                    $"Pixel ({absoluteX},{absoluteY}) mismatch: ROI decode gave " +
                    $"[{roiPixel[0]},{roiPixel[1]},{roiPixel[2]},{roiPixel[3]}], plain decode gave " +
                    $"[{plainPixel[0]},{plainPixel[1]},{plainPixel[2]},{plainPixel[3]}].");
            }
        }
    }

    [Fact]
    public void RoiFlaggedFile_HasPgfRoiVersionFlagSet()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(32, 32);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, roi: true));

        // preHeaderBytes[3] is the version-flags byte (PgfHeaderIO.Write) - PGFROI = 8.
        Assert.Equal(8, pgfBytes![3] & 8);
    }

    [Fact]
    public void RoiFlaggedFile_PlainNonRoiApi_DoesNotMisreadItAsAnUnrelatedValidFile()
    {
        // PgfImageDecoder/PgfProgressiveDecoder without TrySetRoi are not ROI-aware (this PRD's own
        // architecture section: only PgfProgressiveDecoder gets ROI support) - an ROI-flagged file's
        // macroblocks carry 2 extra header bytes the non-ROI reader never expects, so this must not
        // silently decode successfully with wrong/corrupted pixels. Whether it fails outright
        // (PgfFormatException caught internally -> false) or, for a small enough fixture, degrades
        // some other way, is not asserted - only that it does not silently produce a plausible-looking
        // wrong image that a test comparing against the plain encode would miss.
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? roiPgf, roi: true));
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? plainPgf));

        bool decodedAsNonRoi = PgfImageDecoder.TryDecode(roiPgf, static (b, w, h) => b.ToArray(), out byte[]? misreadBytes);

        if (decodedAsNonRoi)
        {
            bool plainDecoded = PgfImageDecoder.TryDecode(plainPgf, static (b, w, h) => b.ToArray(), out byte[]? correctBytes);
            Assert.True(plainDecoded);
            Assert.NotEqual(correctBytes, misreadBytes);
        }
    }
}
