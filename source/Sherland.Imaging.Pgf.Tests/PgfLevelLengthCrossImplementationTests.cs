using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// pgf-real-level-lengths.md Stage 5 exit tests: the two cross-implementation legs the Test rig
/// section describes, now that Stage 4's shim export exists.
///
/// <b>Decode-side accessor leg</b> (<see cref="NativeEncode_ManagedDecodeAccessor_MatchesNativeReport"/>):
/// opens a real native-encoded file through <see cref="PgfProgressiveDecoder"/> and confirms
/// <see cref="PgfProgressiveDecoder.TryGetLevelLength"/> reports exactly what
/// <c>CPGFImage::GetEncodedLevelLength</c> itself reports for that same file - a genuine,
/// independent oracle (unlike <c>pgf-legacy-interleaved-decode.md</c>'s self-consistency-only
/// situation), since level lengths are parsed here, not computed.
///
/// <b>Cross-implementation encode leg</b> (<see cref="ManagedEncode_LevelLengths_MatchNativeEncode_ForSameSourceImage"/>):
/// resolves this PRD's second Open Question empirically. The base codec's own established proof
/// achieved byte-exact bitstream identity between this port's encoder and the native one, so the
/// per-level byte lengths - a pure function of where macroblock boundaries land in that identical
/// bitstream - turn out to match byte-for-byte too, confirmed directly below rather than assumed.
/// If a future codec change ever breaks that exact match, the fallback bar this PRD's own Test rig
/// section already sets ("each implementation's own number is provably correct for its own output")
/// is already proven separately by <see cref="PgfLevelLengthTests"/>'s self-consistency checks.
/// </summary>
public class PgfLevelLengthCrossImplementationTests
{
    [Theory]
    [MemberData(nameof(DimensionsAndQualities))]
    public void NativeEncode_ManagedDecodeAccessor_MatchesNativeReport(int width, int height, byte quality)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);

        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality, out byte[]? nativePgf));
        Assert.True(NativePgfOracle.TryGetLevelLengths(nativePgf, out uint[]? nativeLevelLengths));

        PgfProgressiveDecoder? managedDecoder = PgfProgressiveDecoder.TryOpen(nativePgf);
        Assert.NotNull(managedDecoder);
        Assert.Equal(nativeLevelLengths!.Length, managedDecoder.Levels);

        for (int level = 0; level < managedDecoder.Levels; level++)
        {
            Assert.True(managedDecoder.TryGetLevelLength(level, out uint managedLength));
            Assert.Equal(nativeLevelLengths[level], managedLength);
        }
    }

    [Theory]
    [MemberData(nameof(DimensionsAndQualities))]
    public void ManagedEncode_LevelLengths_MatchNativeEncode_ForSameSourceImage(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? managedPgf));
        Assert.True(NativePgfOracle.TryEncode(bgra, w, h, quality, out byte[]? nativePgf));

        PgfProgressiveDecoder? managedDecoder = PgfProgressiveDecoder.TryOpen(managedPgf);
        Assert.NotNull(managedDecoder);
        Assert.True(NativePgfOracle.TryGetLevelLengths(nativePgf, out uint[]? nativeLevelLengths));

        Assert.Equal(nativeLevelLengths!.Length, managedDecoder.Levels);

        for (int level = 0; level < managedDecoder.Levels; level++)
        {
            Assert.True(managedDecoder.TryGetLevelLength(level, out uint managedLength));
            Assert.Equal(nativeLevelLengths[level], managedLength);
        }
    }

    [Theory]
    [MemberData(nameof(DimensionsAndQualities))]
    public void ManagedRoiEncode_LevelLengths_MatchNativeRoiEncode_ForSameSourceImage(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? managedPgf, roi: true));
        Assert.True(NativePgfOracle.TryEncodeRoi(bgra, w, h, quality, out byte[]? nativePgf));

        PgfProgressiveDecoder? managedDecoder = PgfProgressiveDecoder.TryOpen(managedPgf);
        Assert.NotNull(managedDecoder);
        Assert.True(NativePgfOracle.TryGetLevelLengths(nativePgf, out uint[]? nativeLevelLengths));

        Assert.Equal(nativeLevelLengths!.Length, managedDecoder.Levels);

        for (int level = 0; level < managedDecoder.Levels; level++)
        {
            Assert.True(managedDecoder.TryGetLevelLength(level, out uint managedLength));
            Assert.Equal(nativeLevelLengths[level], managedLength);
        }
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
