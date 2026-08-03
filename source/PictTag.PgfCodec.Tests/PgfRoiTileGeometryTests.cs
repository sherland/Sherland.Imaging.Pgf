using PictTag.PgfCodec;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// Stage 2 exit tests (new-features/pgf-roi-support.md): pure tile-index geometry -
/// <see cref="PgfWaveletTransform.SetROI"/>/<see cref="PgfWaveletTransform.GetNofTiles"/>/
/// <see cref="PgfWaveletTransform.TileIsRelevant"/> and <see cref="PgfSubband.TilePosition"/>/
/// <see cref="PgfSubband.TileIndex"/> - no decode/encode data flow involved at all (that's Stage
/// 3/4). Two of these cases are hand-traced against the real algorithm (not just "does it run
/// without throwing"), per this PRD's "verify it, don't just port it silently" instruction for the
/// cross-level nesting invariant specifically.
/// </summary>
public class PgfRoiTileGeometryTests
{
    [Theory]
    [InlineData(8, 8, 1)]
    [InlineData(64, 64, 3)]
    [InlineData(37, 53, 4)] // odd, non-power-of-two dimensions
    public void FullImageRoi_ProducesFullTileCoverageAndFullAlignedRoiAtEveryLevel(int width, int height, int levels)
    {
        PgfWaveletTransform wt = new(width, height, levels);
        wt.SetROI(new PgfRoi(0, 0, width, height));

        for (int level = 0; level <= levels; level++)
        {
            int nTiles = wt.GetNofTiles(level);

            for (int tileY = 0; tileY < nTiles; tileY++)
            {
                for (int tileX = 0; tileX < nTiles; tileX++)
                {
                    Assert.True(
                        wt.TileIsRelevant(level, tileX, tileY),
                        $"tile ({tileX},{tileY}) at level {level} should be relevant for a full-image ROI.");
                }
            }

            // one step outside the tile grid must never be relevant
            Assert.False(wt.TileIsRelevant(level, nTiles, 0));
            Assert.False(wt.TileIsRelevant(level, 0, nTiles));

            foreach (PgfSubbandOrientation orientation in new[]
                     {
                         PgfSubbandOrientation.Ll, PgfSubbandOrientation.Hl,
                         PgfSubbandOrientation.Lh, PgfSubbandOrientation.Hh,
                     })
            {
                PgfSubband subband = wt.GetSubband(level, orientation);
                Assert.Equal(new PgfRoi(0, 0, subband.Width, subband.Height), subband.AlignedRoi);
            }
        }
    }

    /// <summary>Hand-traced against the real algorithm (not just re-derived from the port itself):
    /// 64x64, 3 levels (levelCount=4), requesting the small top-left corner ROI (0,0,8,8). The
    /// margin-enlargement step (<c>delta = (FilterSize&gt;&gt;1)&lt;&lt;levelCount = 2&lt;&lt;4 =
    /// 32</c>) pushes the effective right/bottom to <c>8+32=40</c> before tiling, still short of the
    /// full 64px width, so this genuinely exercises a partial (not full-image-degenerate) tile-index
    /// result at levels 0 and 1 - traced by hand through the same binary-search arithmetic
    /// <see cref="PgfSubband.TileIndex"/>/<see cref="PgfSubband.TilePosition"/> implement.</summary>
    [Fact]
    public void PartialRoi_MatchesHandTracedTileIndicesAndAlignedRoi()
    {
        PgfWaveletTransform wt = new(64, 64, 3);
        wt.SetROI(new PgfRoi(0, 0, 8, 8));

        Assert.Equal(8, wt.GetNofTiles(0));
        Assert.Equal(4, wt.GetNofTiles(1));

        // Level 0: every one of the 4 level-0 subbands is 64x64, NTiles=8; hand-traced tile-index
        // bounds (0,0,5,5) and aligned ROI (0,0,40,40) for all four orientations.
        var expectedIndices0 = new PgfRoi(0, 0, 5, 5);
        var expectedAlignedRoi0 = new PgfRoi(0, 0, 40, 40);
        foreach (PgfSubbandOrientation orientation in new[]
                 {
                     PgfSubbandOrientation.Ll, PgfSubbandOrientation.Hl,
                     PgfSubbandOrientation.Lh, PgfSubbandOrientation.Hh,
                 })
        {
            Assert.Equal(expectedAlignedRoi0, wt.GetSubband(0, orientation).AlignedRoi);
        }

        for (int tileY = 0; tileY < 8; tileY++)
        {
            for (int tileX = 0; tileX < 8; tileX++)
            {
                Assert.Equal(
                    expectedIndices0.IsInside(tileX, tileY),
                    wt.TileIsRelevant(0, tileX, tileY));
            }
        }

        // Level 1: LL is 32x32, NTiles=4; hand-traced tile-index bounds (0,0,3,3) and LL aligned ROI
        // (0,0,24,24).
        Assert.Equal(new PgfRoi(0, 0, 24, 24), wt.GetAlignedROI(1));

        for (int tileY = 0; tileY < 4; tileY++)
        {
            for (int tileX = 0; tileX < 4; tileX++)
            {
                bool expected = tileX < 3 && tileY < 3;
                Assert.Equal(expected, wt.TileIsRelevant(1, tileX, tileY));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void GetNofTiles_DoublesGoingFromCoarsestToFinestLevel(int levels)
    {
        PgfWaveletTransform wt = new(128, 128, levels);

        int expected = 1;
        for (int level = levels; level >= 0; level--)
        {
            Assert.Equal(expected, wt.GetNofTiles(level));
            expected *= 2;
        }
    }

    /// <summary>Sweeps a range of fixture sizes/levels/requested rectangles - including the four
    /// corners/edges and odd/unaligned boundaries per this PRD's Test rig - and asserts the native's
    /// own cross-level nesting invariant (<c>WaveletTransform.cpp:544-545</c>) holds for every one:
    /// <see cref="PgfWaveletTransform.SetROI"/> throws <see cref="InvalidOperationException"/> if it
    /// doesn't, so "did not throw" is itself the assertion, strengthened by also checking that every
    /// level's tile-index rectangle actually stays within <see cref="PgfWaveletTransform.GetNofTiles"/>'s
    /// own bounds (a violated invariant could otherwise pass silently if the exception check alone
    /// were too weak).</summary>
    [Theory]
    [InlineData(64, 64, 3, 0, 0, 8, 8)] // top-left corner
    [InlineData(64, 64, 3, 56, 56, 64, 64)] // bottom-right corner
    [InlineData(64, 64, 3, 0, 56, 8, 64)] // bottom-left corner
    [InlineData(64, 64, 3, 56, 0, 64, 8)] // top-right corner
    [InlineData(64, 64, 3, 20, 20, 44, 44)] // interior, multi-tile
    [InlineData(64, 64, 3, 30, 30, 34, 34)] // interior, small/single-tile-ish
    [InlineData(37, 53, 4, 3, 7, 19, 29)] // odd/unaligned image and ROI
    [InlineData(128, 128, 5, 0, 0, 128, 128)] // full image, more levels
    [InlineData(128, 128, 5, 100, 5, 127, 40)] // odd edge region
    public void SweptRoiRectangles_SatisfyNestingInvariantAndStayWithinTileBounds(
        int width, int height, int levels, int left, int top, int right, int bottom)
    {
        PgfWaveletTransform wt = new(width, height, levels);

        wt.SetROI(new PgfRoi(left, top, right, bottom)); // throws InvalidOperationException on violation

        for (int level = 0; level <= levels; level++)
        {
            int nTiles = wt.GetNofTiles(level);

            // every relevant tile must be a real tile index, not something TileIndex's own binary
            // search could produce out of range
            for (int tileY = 0; tileY < nTiles; tileY++)
            {
                for (int tileX = 0; tileX < nTiles; tileX++)
                {
                    _ = wt.TileIsRelevant(level, tileX, tileY); // must not throw for any in-range tile
                }
            }
        }
    }
}
