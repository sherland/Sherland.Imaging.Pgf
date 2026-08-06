namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 6 exit test (new-features/pgf-roi-support.md): "every fixture x every swept ROI rectangle x
/// every quality level, all four legs" - <see cref="PgfRoiRoundTripTests"/> (Stage 4) and
/// <see cref="PgfRoiNativeCrossImplementationTests"/> (Stage 5) already cover the two most important
/// legs (self-consistency, native-encode/managed-decode) at a handful of representative cases; this
/// file is the systematic sweep across sizes x quality x rectangle shapes the PRD's own Test rig
/// calls for, plus the one correctness property the earlier stages' tests only exercised implicitly:
/// that a <see cref="PgfDecoderCore.SkipTileBuffer"/>-driven partial decode leaves the shared
/// bitstream position correct for a genuinely <i>separate, later</i>
/// <see cref="PgfProgressiveDecoder.TryDecodeLevel{TResult}"/> call (not just within one call's own
/// internal per-level loop).
///
/// Every quality value 0..<see cref="PgfConstants.MaxQuality"/> is not swept exhaustively here (32
/// values x several sizes x several rectangles would be a slow, low-marginal-value suite - the
/// underlying per-value risk is uniform, not concentrated at specific quality values the way it is at
/// tile/alignment boundaries) - a representative spread (0, 1, mid-range, near-max, max) stands in for
/// it, matching this port's own established test-sizing convention elsewhere in this test rig.
/// </summary>
public class PgfRoiFullMatrixTests
{
    public static IEnumerable<object[]> SizeQualityRoiMatrix()
    {
        (int Width, int Height)[] sizes = [(32, 32), (64, 64), (37, 53), (96, 80)];
        byte[] qualities = [0, 1, 12, 24, PgfConstants.MaxQuality];

        foreach ((int width, int height) in sizes)
        {
            foreach (byte quality in qualities)
            {
                // full image, an interior region, and the four corners - the same shapes
                // PgfRoiRoundTripTests hand-picks per case, swept here across every size/quality.
                yield return [width, height, 0, 0, width, height, quality];
                yield return [width, height, width / 4, height / 4, (3 * width) / 4, (3 * height) / 4, quality];
                yield return [width, height, 0, 0, width / 3 + 1, height / 3 + 1, quality];
                yield return [width, height, (2 * width) / 3, (2 * height) / 3, width, height, quality];
            }
        }
    }

    [Theory]
    [MemberData(nameof(SizeQualityRoiMatrix))]
    public void SelfConsistency_ManagedRoiDecodeMatchesManagedPlainDecode(
        int width, int height, int reqLeft, int reqTop, int reqRight, int reqBottom, byte quality)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);

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
        Assert.True(roiDecoder.TryGetAccurateRoi(0, out PgfRoi accurate));

        for (int y = 0; y < accurate.Height; y++)
        {
            for (int x = 0; x < accurate.Width; x++)
            {
                int absoluteX = accurate.Left + x;
                int absoluteY = accurate.Top + y;
                int roiIndex = (((absoluteY - aligned.Top) * aligned.Width) + (absoluteX - aligned.Left)) * 4;
                int plainIndex = ((absoluteY * width) + absoluteX) * 4;

                ReadOnlySpan<byte> roiPixel = roiResult.Bytes.AsSpan(roiIndex, 4);
                ReadOnlySpan<byte> plainPixel = plain.Bytes.AsSpan(plainIndex, 4);

                Assert.True(
                    roiPixel.SequenceEqual(plainPixel),
                    $"{width}x{height} q{quality} roi=({reqLeft},{reqTop},{reqRight},{reqBottom}): pixel " +
                    $"({absoluteX},{absoluteY}) mismatch.");
            }
        }
    }

    /// <summary>The PRD's own "SkipTileBuffer correctness" Test rig point, made explicit: decodes a
    /// small ROI (forcing several tiles to be skipped via <see cref="PgfDecoderCore.SkipTileBuffer"/>)
    /// down to a coarse level in one <see cref="PgfProgressiveDecoder.TryDecodeLevel{TResult}"/> call,
    /// then makes a <i>second, separate</i> call on the same instance requesting a finer level - a
    /// real off-by-one in <c>SkipTileBuffer</c>'s stream-position bookkeeping would desync the shared
    /// bitstream cursor for this later call specifically (not just corrupt the first call's own
    /// output), so this is a genuinely different code path than <see cref="PgfRoiRoundTripTests"/>'s
    /// single-call, decode-straight-to-level-0 tests exercise.</summary>
    [Theory]
    [InlineData(150, 150, 20, 20, 70, 70)]
    [InlineData(128, 160, 30, 30, 90, 100)]
    public void SeparateTryDecodeLevelCalls_AfterPartialRoiDecode_StayCorrect(
        int width, int height, int reqLeft, int reqTop, int reqRight, int reqBottom)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);
        const byte quality = 0;

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? plainPgf));
        bool plainDecoded = PgfImageDecoder.TryDecode(plainPgf, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var plain);
        Assert.True(plainDecoded);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? roiPgf, roi: true));

        PgfProgressiveDecoder? roiDecoder = PgfProgressiveDecoder.TryOpen(roiPgf);
        Assert.NotNull(roiDecoder);
        Assert.True(roiDecoder.Levels > 1, "Fixture should have more than one level to exercise separate calls.");
        Assert.True(roiDecoder.TrySetRoi(new PgfRoi(reqLeft, reqTop, reqRight, reqBottom)));

        // First call: stop at a coarse level (not 0) - skips some tiles at every level down to there.
        int coarseLevel = roiDecoder.Levels - 1;
        bool coarseDecoded = roiDecoder.TryDecodeLevel(coarseLevel, static (b, w, h) => (w, h), out _);
        Assert.True(coarseDecoded);

        // Second, separate call: continue down to level 0 - if SkipTileBuffer left the stream
        // position wrong during the first call, this call reads garbage from the wrong offset.
        bool fineDecoded = roiDecoder.TryDecodeLevel(0, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var fineResult);
        Assert.True(fineDecoded);

        Assert.True(roiDecoder.TryGetAlignedRoi(0, out PgfRoi aligned));
        Assert.True(roiDecoder.TryGetAccurateRoi(0, out PgfRoi accurate));

        for (int y = 0; y < accurate.Height; y++)
        {
            for (int x = 0; x < accurate.Width; x++)
            {
                int absoluteX = accurate.Left + x;
                int absoluteY = accurate.Top + y;
                int roiIndex = (((absoluteY - aligned.Top) * aligned.Width) + (absoluteX - aligned.Left)) * 4;
                int plainIndex = ((absoluteY * width) + absoluteX) * 4;

                Assert.True(
                    fineResult.Bytes.AsSpan(roiIndex, 4).SequenceEqual(plain.Bytes.AsSpan(plainIndex, 4)),
                    $"Pixel ({absoluteX},{absoluteY}) mismatch after a separate follow-up TryDecodeLevel call.");
            }
        }
    }

    /// <summary>This PRD's own Open Question: "whether enabling ROI tiling measurably hurts
    /// compression ratio... confirm empirically... rather than assume tile boundaries are cheap."
    /// Resolved empirically (see this stage's own Progress log entry for the full real numbers this
    /// test's assertion is deliberately looser than): the overhead is real and, at aggressive
    /// quality settings on small/simple images, large in <i>relative</i> terms (every tile boundary
    /// forces its own macroblock flush - <see cref="PgfEncoderCore.EncodeTileBuffer"/> - instead of
    /// packing coefficients into one efficiently-sized block, and each tile that has ANY data at all
    /// pays its own fixed per-macroblock overhead). This asserts only a generous sanity ceiling (not
    /// a tight regression gate - a "compression ratio" isn't a correctness property, and the real
    /// number varies a lot by content/size/quality, per the Progress log's own measured data) - it
    /// exists to catch a catastrophic future regression (e.g. tiles somehow encoded many times over),
    /// not to enforce a specific ratio.</summary>
    [Theory]
    [InlineData(64, 64, 0)]
    [InlineData(64, 64, 16)]
    [InlineData(256, 256, 0)]
    [InlineData(256, 256, 16)]
    public void RoiEncodeSize_StaysWithinGenerousBoundOfPlainEncode(int width, int height, byte quality)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? plainPgf));
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? roiPgf, roi: true));

        // Loose on purpose (see doc comment) - at high quality on simple content the *relative*
        // overhead is large even though the *absolute* byte count stays small; the fixed +2KB
        // headroom absorbs that without needing a percentage-only bound that would be too tight for
        // near-empty plain outputs.
        Assert.True(
            roiPgf!.Length <= (plainPgf!.Length * 10) + 2048,
            $"ROI-encoded size {roiPgf.Length} unexpectedly far beyond plain size {plainPgf.Length} " +
            $"for {width}x{height} q{quality}.");
    }
}
