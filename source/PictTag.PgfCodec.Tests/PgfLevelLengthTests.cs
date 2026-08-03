namespace PictTag.PgfCodec.Tests;

/// <summary>
/// pgf-real-level-lengths.md Stages 1-3 exit test: self-consistency for the newly-real per-level
/// byte-length table. Encodes, then confirms (a) the level lengths <see cref="PgfHeaderIO.Read"/>
/// parses off the wire sum to exactly the bitstream bytes the encoder actually wrote after the
/// level-length placeholder, (b) every level's length is nonzero (a real image always has real
/// per-level data), and (c) <see cref="PgfProgressiveDecoder.TryGetLevelLength"/> - the new Goal 2
/// public accessor - reports the same values, in the level-0-is-full-resolution order it documents.
/// Cross-implementation verification against the native oracle's own <c>GetEncodedLevelLength</c> is
/// a separate, later concern (Stage 5) - this is this port's own internal correctness proof.
/// </summary>
public class PgfLevelLengthTests
{
    [Theory]
    [MemberData(nameof(DimensionsAndQualities))]
    public void Encode_LevelLengths_SumToActualBitstreamSize_AndMatchDecodeAccessor(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));
        AssertLevelLengthsSelfConsistent(pgfBytes!);
    }

    [Theory]
    [MemberData(nameof(DimensionsAndQualities))]
    public void Encode_Roi_LevelLengths_SumToActualBitstreamSize_AndMatchDecodeAccessor(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes, roi: true));
        AssertLevelLengthsSelfConsistent(pgfBytes!);
    }

    private static void AssertLevelLengthsSelfConsistent(byte[] pgfBytes)
    {
        PgfMemoryReader reader = new(pgfBytes);
        (_, PgfHeader header, uint[] levelLengths, _, _) = PgfHeaderIO.Read(reader);

        // reader.Position is now exactly past the level-length placeholder table - everything from
        // here to end-of-file is the real entropy-coded bitstream the accounting in PgfEncoderCore
        // was crediting to levelLengths as it went.
        long actualBitstreamLength = pgfBytes.Length - reader.Position;

        Assert.Equal(header.NLevels, levelLengths.Length);

        ulong summedLength = 0;
        foreach (uint length in levelLengths)
        {
            summedLength += length;
        }

        Assert.Equal((ulong)actualBitstreamLength, summedLength);
        Assert.All(levelLengths, len => Assert.True(len > 0, "Every real level should have a nonzero encoded length."));

        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);

        for (int level = 0; level < header.NLevels; level++)
        {
            Assert.True(decoder.TryGetLevelLength(level, out uint length));
            Assert.Equal(levelLengths[header.NLevels - level - 1], length);
        }

        Assert.False(decoder.TryGetLevelLength(-1, out _));
        Assert.False(decoder.TryGetLevelLength(header.NLevels, out _));
    }

    public static TheoryData<int, int, byte> DimensionsAndQualities()
    {
        TheoryData<int, int, byte> data = [];
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            foreach (byte quality in (ReadOnlySpan<byte>)[0, 8, PgfConstants.MaxQuality])
            {
                data.Add(width, height, quality);
            }
        }

        return data;
    }
}
