using PictTag.PgfCodec.Tests.Oracle;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// pgf-all-image-modes.md Stage 2 exit tests: true Group A (GrayScale/IndexedColor/HSLColor/
/// HSBColor - NOT LabColor, see PgfModeInfo.SupportsDownsample's doc comment for why Lab's decode
/// structure doesn't match this group despite sharing its encode transform). Covers both directions
/// this port now supports for these modes: encode (test infrastructure, Goal 2) and decode (the real
/// public-API surface, Goal 1), cross-checked against the real native oracle via
/// <c>pgf_debug_decode_raw</c> (native/PictTag.PgfDecoder/shim.cpp) rather than
/// <c>pgf_debug_decode_channel</c>'s crash-prone <c>GetChannel()</c> path.
/// </summary>
public class PgfGroupATests
{
    private static byte[] SingleChannelGradient(int width, int height)
    {
        byte[] data = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                data[(y * width) + x] = (byte)(((x * 7) + (y * 13)) % 256);
            }
        }

        return data;
    }

    private static byte[] TripleChannelGradient(int width, int height)
    {
        byte[] data = new byte[width * height * 3];
        int cnt = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                data[cnt] = (byte)(((x * 7) + (y * 13)) % 256);
                data[cnt + 1] = (byte)(((x * 11) + (y * 5)) % 256);
                data[cnt + 2] = (byte)(((x * 3) + (y * 17)) % 256);
                cnt += 3;
            }
        }

        return data;
    }

    private static byte[] SyntheticColorTable()
    {
        byte[] table = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < PgfConstants.ColorTableLen; i++)
        {
            table[(i * 4) + 0] = (byte)(i * 3); // B
            table[(i * 4) + 1] = (byte)(255 - i); // G
            table[(i * 4) + 2] = (byte)(i ^ 0x5A); // R
            table[(i * 4) + 3] = 0;
        }

        return table;
    }

    public static IEnumerable<object[]> SingleChannelModes { get; } =
    [
        [PgfConstants.ImageModeGrayScale],
        [PgfConstants.ImageModeIndexedColor],
    ];

    public static IEnumerable<object[]> TripleChannelModes { get; } =
    [
        [PgfConstants.ImageModeHSLColor],
        [PgfConstants.ImageModeHSBColor],
    ];

    // ---- 1-channel modes: GrayScale/IndexedColor ----

    [Theory]
    [MemberData(nameof(SingleChannelModes))]
    public void SingleChannel_ManagedEncodeThenManagedDecode_RoundTripsExactlyAtQuality0(byte mode)
    {
        byte[] source = SingleChannelGradient(37, 23);
        byte[]? colorTable = mode == PgfConstants.ImageModeIndexedColor ? SyntheticColorTable() : null;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, mode, out byte[]? pgfBytes, colorTable);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);
        Assert.Equal(37 * 23 * 4, bgraResult!.Length);

        for (int i = 0; i < source.Length; i++)
        {
            int px = i * 4;
            if (mode == PgfConstants.ImageModeGrayScale)
            {
                Assert.Equal(source[i], bgraResult[px]);
                Assert.Equal(source[i], bgraResult[px + 1]);
                Assert.Equal(source[i], bgraResult[px + 2]);
            }
            else
            {
                int paletteOffset = source[i] * 4;
                Assert.Equal(colorTable![paletteOffset], bgraResult[px]);
                Assert.Equal(colorTable[paletteOffset + 1], bgraResult[px + 1]);
                Assert.Equal(colorTable[paletteOffset + 2], bgraResult[px + 2]);
            }

            Assert.Equal(255, bgraResult[px + 3]);
        }
    }

    [Theory]
    [MemberData(nameof(SingleChannelModes))]
    public void SingleChannel_ManagedEncodeThenNativeOracleDecodesRaw_ChannelIsByteExact(byte mode)
    {
        byte[] source = SingleChannelGradient(37, 23);
        byte[]? colorTable = mode == PgfConstants.ImageModeIndexedColor ? SyntheticColorTable() : null;

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, mode, out byte[]? pgfBytes, colorTable);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 8, [0], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);

        // Lossless (quality=0): the real oracle's own reconstructed channel byte must equal this
        // port's source input exactly, not just "whatever this port's own decoder also produces" -
        // the genuine cross-implementation proof Goal 4 asks for.
        Assert.Equal(source, oracleRaw);
    }

    [Fact]
    public void IndexedColor_DecodeAppliesCorrectPaletteEntry_ForEveryDistinctIndex()
    {
        // One pixel per palette index (16x16 = 256), so every distinct index value 0..255 appears
        // exactly once - directly exercises the Test Rig's own "confirming the right RGB triple is
        // substituted for each palette index" requirement, not just a couple of spot values.
        byte[] source = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            source[i] = (byte)i;
        }

        byte[] colorTable = SyntheticColorTable();

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 16, 16, quality: 0, PgfConstants.ImageModeIndexedColor, out byte[]? pgfBytes, colorTable);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        for (int i = 0; i < 256; i++)
        {
            int px = i * 4;
            int paletteOffset = i * 4; // source[i] == i by construction
            Assert.Equal(colorTable[paletteOffset], bgraResult![px]);
            Assert.Equal(colorTable[paletteOffset + 1], bgraResult[px + 1]);
            Assert.Equal(colorTable[paletteOffset + 2], bgraResult[px + 2]);
        }
    }

    // ---- 3-channel modes: HSLColor/HSBColor ----

    [Theory]
    [MemberData(nameof(TripleChannelModes))]
    public void TripleChannel_ManagedEncodeThenManagedDecode_RoundTripsExactlyAtQuality0(byte mode)
    {
        byte[] source = TripleChannelGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, mode, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => bgra.ToArray(), out byte[]? bgraResult);
        Assert.True(decoded);

        int srcCnt = 0, dstCnt = 0;
        for (int i = 0; i < 37 * 23; i++)
        {
            Assert.Equal(source[srcCnt], bgraResult![dstCnt]);
            Assert.Equal(source[srcCnt + 1], bgraResult[dstCnt + 1]);
            Assert.Equal(source[srcCnt + 2], bgraResult[dstCnt + 2]);
            Assert.Equal(255, bgraResult[dstCnt + 3]);
            srcCnt += 3;
            dstCnt += 4;
        }
    }

    [Theory]
    [MemberData(nameof(TripleChannelModes))]
    public void TripleChannel_ManagedEncodeThenNativeOracleDecodesRaw_ChannelsAreByteExact(byte mode)
    {
        byte[] source = TripleChannelGradient(37, 23);

        bool encoded = PgfImageEncoder.TryEncodeMode(source, 37, 23, quality: 0, mode, out byte[]? pgfBytes);
        Assert.True(encoded);

        bool oracleOk = NativePgfOracle.TryDecodeRaw(pgfBytes!, bpp: 24, [0, 1, 2], out byte[]? oracleRaw, out int width, out int height);
        Assert.True(oracleOk, "Native oracle rejected a C#-encoded file.");
        Assert.Equal(37, width);
        Assert.Equal(23, height);
        Assert.Equal(source, oracleRaw);
    }

    // ---- Quality sweep + edge-case dimensions, both single- and triple-channel ----

    [Theory]
    [InlineData(PgfConstants.ImageModeGrayScale)]
    [InlineData(PgfConstants.ImageModeHSLColor)]
    public void EdgeCaseDimensions_RoundTripAtRepresentativeQualities(byte mode)
    {
        int channelCount = mode == PgfConstants.ImageModeGrayScale ? 1 : 3;
        byte[] qualities = [0, 3, 8, PgfConstants.MaxQuality];

        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            byte[] source = channelCount == 1 ? SingleChannelGradient(width, height) : TripleChannelGradient(width, height);

            foreach (byte quality in qualities)
            {
                bool encoded = PgfImageEncoder.TryEncodeMode(source, width, height, quality, mode, out byte[]? pgfBytes);
                Assert.True(encoded, $"Encode failed for {width}x{height} @ quality {quality}.");

                bool decoded = PgfImageDecoder.TryDecode(pgfBytes!, (bgra, w, h) => (bgra.ToArray(), w, h), out (byte[] Bgra, int W, int H) result);
                Assert.True(decoded, $"Decode failed for {width}x{height} @ quality {quality}.");
                Assert.Equal(width, result.W);
                Assert.Equal(height, result.H);

                if (quality == 0)
                {
                    // Lossless only - higher qualities are genuinely lossy (Group A applies no
                    // downsampling, but the wavelet/entropy path still quantizes above quality 0).
                    int srcCnt = 0, dstCnt = 0;
                    for (int i = 0; i < width * height; i++)
                    {
                        for (int c = 0; c < channelCount; c++)
                        {
                            Assert.Equal(source[srcCnt + c], result.Bgra[dstCnt + c]);
                        }

                        srcCnt += channelCount;
                        dstCnt += 4;
                    }
                }
            }
        }
    }
}
