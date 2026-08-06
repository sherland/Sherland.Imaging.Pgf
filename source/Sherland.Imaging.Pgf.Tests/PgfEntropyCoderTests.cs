using Sherland.Imaging.Pgf;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 5 exit tests (new-features/managed-pgf-codec.md): self-consistency round-trips of the
/// entropy coder alone (<see cref="PgfEncoderCore"/> -&gt; <see cref="PgfDecoderCore"/>, bypassing
/// <see cref="PgfDecoderCore.Partition"/>/<see cref="PgfEncoderCore.Partition"/>'s 2D tiling and
/// using <c>quantParam=0</c> so <c>DequantizeValue</c>'s <c>value &lt;&lt; quantParam</c> is the
/// identity - isolating pure entropy-coding correctness from quantization or subband-tiling order).
///
/// Deliberately does NOT use the native oracle here: this repo's own investigation
/// (managed-pgf-codec.md's Stage 1 Progress log) found a real, unresolved crash risk in
/// <c>pgf_debug_decode_channel</c> - the only native hook that could expose intermediate
/// coefficient data for cross-validation - so this stage's correctness proof is self-consistency
/// (does the C# encoder's output decode back to the original input?) plus hand-reasoned edge cases,
/// with full cross-implementation validation deferred to Stage 7's end-to-end BGRA comparison
/// against the always-safe <c>pgf_decode_bgra</c>, once the wavelet transform and color conversion
/// exist to produce a real, decodable file.
/// </summary>
public class PgfEntropyCoderTests
{
    private static int[] RoundTrip(int[] values)
    {
        PgfByteWriter writer = new();
        PgfEncoderCore encoder = new(writer);

        foreach (int value in values)
        {
            encoder.WriteValue(value);
        }

        encoder.Flush();

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        PgfDecoderCore decoder = new(reader);

        int[] result = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            decoder.DequantizeValue(result, i, quantParam: 0);
        }

        return result;
    }

    [Fact]
    public void AllZeros_RoundTripsExactly()
    {
        int[] values = new int[1000];

        Assert.Equal(values, RoundTrip(values));
    }

    [Theory]
    [InlineData((int)100)]
    [InlineData((int)-100)]
    [InlineData((int)1)]
    [InlineData((int)-1)]
    [InlineData((int)32767)]
    [InlineData((int)-32767)]
    public void ConstantValue_RoundTripsExactly(int constant)
    {
        int[] values = new int[2000];
        Array.Fill(values, constant);

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void AlternatingSigns_RoundTripsExactly()
    {
        int[] values = new int[3000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (int)(i % 2 == 0 ? 7 : -7);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SmallRandomRange_RoundTripsExactly()
    {
        Random random = new(Seed: 42);
        int[] values = new int[5000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (int)random.Next(-500, 501);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void FullShortRange_RoundTripsExactly()
    {
        Random random = new(Seed: 123);
        int[] values = new int[3000];
        for (int i = 0; i < values.Length; i++)
        {
            // This range (the old Int16 boundary) is what every real RGBA-only value this codec's
            // production path has ever produced looks like - kept as its own case for that reason,
            // not because it's close to any type boundary anymore (see
            // ValuesBeyondInt16Range_RoundTripsExactly for the real current boundary, now that
            // DataT is int/MaxBitPlanes=31 - pgf-all-image-modes.md's DataT correction).
            values[i] = random.Next(-32767, 32768);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    /// <summary>pgf-all-image-modes.md's DataT correction: proves Stage 0's short-&gt;int widening
    /// actually buys new range, not just a type rename. -32768 (exactly the old Int16.MinValue,
    /// unrepresentable in the pre-widening <c>short</c>-based coefficient storage) and values well
    /// beyond it must round-trip exactly - this is precisely the magnitude range pgf-all-image-
    /// modes.md's 16-bit-per-channel groups (Gray16/Lab48/RGB48/CMYK64) will produce.</summary>
    [Fact]
    public void ValuesBeyondInt16Range_RoundTripsExactly()
    {
        Random random = new(Seed: 456);
        int[] values = new int[3000];
        values[0] = -32768;
        values[1] = 32768;
        values[2] = int.MinValue + 1; // NumberOfBitplanes' abs() promotion path (see PgfEncodeMacroBlock.WriteValue)
        for (int i = 3; i < values.Length; i++)
        {
            values[i] = random.Next(-100_000, 100_001);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SparseWithSpikes_RoundTripsExactly()
    {
        // Realistic wavelet-coefficient-like distribution: mostly zero, occasional large values -
        // exercises the adaptive run-length k-parameter adjusting both up and down repeatedly.
        Random random = new(Seed: 7);
        int[] values = new int[8000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = random.Next(0, 20) == 0 ? (int)random.Next(-30000, 30001) : (int)0;
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void ExactlyOneMacroblock_RoundTripsExactly()
    {
        Random random = new(Seed: 99);
        int[] values = new int[PgfConstants.BufferSize];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (int)random.Next(-1000, 1001);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SpansMultipleMacroblocks_RoundTripsExactly()
    {
        Random random = new(Seed: 2024);
        int[] values = new int[(PgfConstants.BufferSize * 2) + 500];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (int)random.Next(-2000, 2001);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SingleValue_RoundTripsExactly()
    {
        int[] values = [12345];

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void LongRunOfZerosThenOneSpike_RoundTripsExactly()
    {
        int[] values = new int[10000];
        values[^1] = 999;

        Assert.Equal(values, RoundTrip(values));
    }

    /// <summary>Unlike every other test in this file (which drives <see cref="PgfEncoderCore.
    /// WriteValue"/>/<see cref="PgfDecoderCore.DequantizeValue"/> directly, in flat linear order),
    /// this one exercises <see cref="PgfEncoderCore.Partition"/>/<see cref="PgfDecoderCore.
    /// Partition"/>'s LinBlockSize-tiled traversal order - the actual driver every real subband
    /// fill uses (Stage 6+). Dimensions deliberately not multiples of
    /// <see cref="PgfConstants.LinBlockSize"/> (8), so all four of Partition's code paths (main
    /// block, width remainder, height remainder, corner) are exercised, not just the common case.</summary>
    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(20, 13)]
    [InlineData(13, 20)]
    [InlineData(37, 23)]
    [InlineData(1, 1)]
    [InlineData(9, 3)]
    public void Partition_RoundTripsExactly(int width, int height)
    {
        Random random = new(Seed: width * 1000 + height);
        int[] source = new int[width * height];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (int)random.Next(-5000, 5001);
        }

        PgfByteWriter writer = new();
        PgfEncoderCore encoder = new(writer);
        encoder.Partition(source, width, height, startPos: 0, pitch: width);
        encoder.Flush();

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        PgfDecoderCore decoder = new(reader);
        int[] destination = new int[width * height];
        decoder.Partition(destination, quantParam: 0, width, height, startPos: 0, pitch: width);

        Assert.Equal(source, destination);
    }
}
