namespace PictTag.PgfCodec.Tests;

/// <summary>
/// Stage 7 unit tests (new-features/managed-pgf-codec.md) for <see cref="PgfColorConversion"/> in
/// isolation - no entropy coding or wavelet transform involved, so these directly test whether the
/// BGRA&lt;-&gt;YUVA color transform is exactly invertible on its own. That's a necessary condition
/// for the full decode pipeline's lossless (quality=0) round trip: Stage 6 already proved the wavelet
/// transform is exactly invertible at quant=0, and Stage 5 proved the entropy coder is exactly
/// lossless, so if the full pipeline is going to round-trip exactly, the color transform must too -
/// this isolates that specific claim instead of only ever checking it downstream of the other two
/// stages (see <see cref="PgfImageDecoderTests"/> for the full end-to-end check).
/// </summary>
public class PgfColorConversionTests
{
    private static byte[] RoundTripNoDownsample(byte[] bgra, int width, int height)
    {
        int pixelCount = width * height;
        int[] y = new int[pixelCount];
        int[] u = new int[pixelCount];
        int[] v = new int[pixelCount];
        int[] a = new int[pixelCount];

        PgfColorConversion.EncodeBgraToYuva(bgra, width, height, y, u, v, a);

        byte[] result = new byte[bgra.Length];
        PgfColorConversion.DecodeYuvaToBgra(y, u, v, a, width, height, chromaWidth: width, downsample: false, result);
        return result;
    }

    [Fact]
    public void RoundTrip_SolidColor_NoDownsample_IsExact()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(16, 16, b: 30, g: 200, r: 90, a: 128);

        byte[] result = RoundTripNoDownsample(bgra, width, height);

        Assert.Equal(bgra, result);
    }

    [Fact]
    public void RoundTrip_Checkerboard_NoDownsample_IsExact()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Checkerboard(16, 16);

        byte[] result = RoundTripNoDownsample(bgra, width, height);

        Assert.Equal(bgra, result);
    }

    [Fact]
    public void RoundTrip_Gradient_NoDownsample_IsExact()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(37, 23);

        byte[] result = RoundTripNoDownsample(bgra, width, height);

        Assert.Equal(bgra, result);
    }

    /// <summary>Every combination of B/G/R/A byte extremes, not just the synthetic fixtures' smooth
    /// content - the color transform's exact invertibility must hold at the corners of the value
    /// range too (e.g. pure magenta/green from this class's own worked-by-hand derivation).</summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(255, 255, 255, 255)]
    [InlineData(255, 0, 255, 255)] // magenta
    [InlineData(0, 255, 0, 255)] // green
    [InlineData(255, 0, 0, 0)]
    [InlineData(0, 0, 255, 255)]
    [InlineData(128, 64, 200, 17)]
    public void RoundTrip_SinglePixel_NoDownsample_IsExact(byte b, byte g, byte r, byte a)
    {
        byte[] bgra = [b, g, r, a];

        byte[] result = RoundTripNoDownsample(bgra, width: 1, height: 1);

        Assert.Equal(bgra, result);
    }

    /// <summary>Downsampling a constant channel must reproduce the same constant: box-averaging four
    /// equal values returns that value, and nearest-neighbor upsampling replicates it back - so a
    /// solid-color image round-trips exactly even through the lossy chroma/alpha subsampling path,
    /// unlike non-constant content (not asserted here - subsampling is inherently lossy for those).</summary>
    [Fact]
    public void RoundTrip_SolidColor_WithDownsample_IsExact()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(16, 16, b: 30, g: 200, r: 90, a: 128);
        int pixelCount = width * height;
        int[] y = new int[pixelCount];
        int[] u = new int[pixelCount];
        int[] v = new int[pixelCount];
        int[] a = new int[pixelCount];
        PgfColorConversion.EncodeBgraToYuva(bgra, width, height, y, u, v, a);

        (int uw, int uh) = PgfColorConversion.Downsample(u, width, height);
        (int vw, _) = PgfColorConversion.Downsample(v, width, height);
        (int aw, _) = PgfColorConversion.Downsample(a, width, height);
        Assert.Equal(uw, vw);
        Assert.Equal(uw, aw);

        byte[] result = new byte[bgra.Length];
        PgfColorConversion.DecodeYuvaToBgra(
            y, u[..(uw * uh)], v[..(uw * uh)], a[..(uw * uh)], width, height, chromaWidth: uw, downsample: true, result);

        Assert.Equal(bgra, result);
    }

    [Theory]
    [InlineData(16, 16, 8, 8)]
    [InlineData(17, 16, 9, 8)] // odd width
    [InlineData(16, 17, 8, 9)] // odd height
    [InlineData(17, 17, 9, 9)] // odd both
    public void Downsample_ReturnsExpectedDimensions(int width, int height, int expectedWidth, int expectedHeight)
    {
        int[] channel = new int[width * height];

        (int newWidth, int newHeight) = PgfColorConversion.Downsample(channel, width, height);

        Assert.Equal(expectedWidth, newWidth);
        Assert.Equal(expectedHeight, newHeight);
    }

    /// <summary>Hand-computed 4x4 input, verifying the box-average arithmetic directly rather than
    /// only through a round trip (which could mask a compensating pair of bugs).</summary>
    [Fact]
    public void Downsample_KnownValues_ComputesExactBoxAverage()
    {
        int[] channel =
        [
            0, 4, 8, 12,
            4, 8, 12, 16,
            8, 12, 16, 20,
            12, 16, 20, 24,
        ];

        (int newWidth, int newHeight) = PgfColorConversion.Downsample(channel, width: 4, height: 4);

        Assert.Equal(2, newWidth);
        Assert.Equal(2, newHeight);

        // top-left 2x2 block: (0+4+4+8)/4 = 4
        Assert.Equal(4, channel[0]);
        // top-right 2x2 block: (8+12+12+16)/4 = 12
        Assert.Equal(12, channel[1]);
        // bottom-left 2x2 block: (8+12+12+16)/4 = 12
        Assert.Equal(12, channel[2]);
        // bottom-right 2x2 block: (16+20+20+24)/4 = 20
        Assert.Equal(20, channel[3]);
    }

    /// <summary>Odd width/height exercise the "rest of row"/"rest of column" branches
    /// (<c>oddW</c>/<c>oddH</c>) - a 3x3 input averages 2x2 blocks for the main body, then falls back
    /// to a 2-value or single-value average at the trailing edge.</summary>
    [Fact]
    public void Downsample_OddDimensions_ComputesExactBoxAverage()
    {
        int[] channel =
        [
            0, 10, 100,
            20, 30, 200,
            300, 400, 500,
        ];

        (int newWidth, int newHeight) = PgfColorConversion.Downsample(channel, width: 3, height: 3);

        Assert.Equal(2, newWidth);
        Assert.Equal(2, newHeight);

        // top-left 2x2 block: (0+10+20+30)/4 = 15
        Assert.Equal(15, channel[0]);
        // top-right: odd column, average of (100,200) = 150
        Assert.Equal(150, channel[1]);
        // bottom-left: odd row, average of (300,400) = 350
        Assert.Equal(350, channel[2]);
        // bottom-right: odd row and column, single value (500), no averaging
        Assert.Equal(500, channel[3]);
    }
}
