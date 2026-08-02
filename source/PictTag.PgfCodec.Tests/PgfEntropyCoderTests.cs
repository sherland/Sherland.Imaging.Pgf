using PictTag.PgfCodec;

namespace PictTag.PgfCodec.Tests;

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
    private static short[] RoundTrip(short[] values)
    {
        PgfByteWriter writer = new();
        PgfEncoderCore encoder = new(writer);

        foreach (short value in values)
        {
            encoder.WriteValue(value);
        }

        encoder.Flush();

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        PgfDecoderCore decoder = new(reader);

        short[] result = new short[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            decoder.DequantizeValue(result, i, quantParam: 0);
        }

        return result;
    }

    [Fact]
    public void AllZeros_RoundTripsExactly()
    {
        short[] values = new short[1000];

        Assert.Equal(values, RoundTrip(values));
    }

    [Theory]
    [InlineData((short)100)]
    [InlineData((short)-100)]
    [InlineData((short)1)]
    [InlineData((short)-1)]
    [InlineData((short)32767)]
    [InlineData((short)-32767)]
    public void ConstantValue_RoundTripsExactly(short constant)
    {
        short[] values = new short[2000];
        Array.Fill(values, constant);

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void AlternatingSigns_RoundTripsExactly()
    {
        short[] values = new short[3000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (short)(i % 2 == 0 ? 7 : -7);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SmallRandomRange_RoundTripsExactly()
    {
        Random random = new(Seed: 42);
        short[] values = new short[5000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (short)random.Next(-500, 501);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void FullShortRange_RoundTripsExactly()
    {
        Random random = new(Seed: 123);
        short[] values = new short[3000];
        for (int i = 0; i < values.Length; i++)
        {
            // Avoid short.MinValue (-32768): the real codec's own MaxBitPlanes=15 ceiling means a
            // magnitude of exactly 32768 is already outside what NumberOfBitplanes can represent
            // (its cnt==MaxBitPlanes+1 wraparound path is for exactly this edge - see
            // PgfEncodeMacroBlock.NumberOfBitplanes's doc comment) - not a real input this codec
            // is designed to round-trip, matching the original's own implicit assumption.
            values[i] = (short)random.Next(-32767, 32768);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SparseWithSpikes_RoundTripsExactly()
    {
        // Realistic wavelet-coefficient-like distribution: mostly zero, occasional large values -
        // exercises the adaptive run-length k-parameter adjusting both up and down repeatedly.
        Random random = new(Seed: 7);
        short[] values = new short[8000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = random.Next(0, 20) == 0 ? (short)random.Next(-30000, 30001) : (short)0;
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void ExactlyOneMacroblock_RoundTripsExactly()
    {
        Random random = new(Seed: 99);
        short[] values = new short[PgfConstants.BufferSize];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (short)random.Next(-1000, 1001);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SpansMultipleMacroblocks_RoundTripsExactly()
    {
        Random random = new(Seed: 2024);
        short[] values = new short[(PgfConstants.BufferSize * 2) + 500];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (short)random.Next(-2000, 2001);
        }

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void SingleValue_RoundTripsExactly()
    {
        short[] values = [12345];

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void LongRunOfZerosThenOneSpike_RoundTripsExactly()
    {
        short[] values = new short[10000];
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
        short[] source = new short[width * height];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (short)random.Next(-5000, 5001);
        }

        PgfByteWriter writer = new();
        PgfEncoderCore encoder = new(writer);
        encoder.Partition(source, width, height, startPos: 0, pitch: width);
        encoder.Flush();

        PgfMemoryReader reader = new(writer.WrittenSpan.ToArray());
        PgfDecoderCore decoder = new(reader);
        short[] destination = new short[width * height];
        decoder.Partition(destination, quantParam: 0, width, height, startPos: 0, pitch: width);

        Assert.Equal(source, destination);
    }
}
