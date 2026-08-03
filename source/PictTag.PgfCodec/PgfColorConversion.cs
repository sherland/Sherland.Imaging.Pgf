namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CPGFImage::RgbToYuv</c>/<c>GetBitmap</c>/<c>Downsample</c>'s <c>ImageModeRGBA</c>
/// branches (PGFimage.cpp:1578, 2232, 809) - the only color mode this port targets (real digiKam
/// thumbnails are always RGBA, per managed-pgf-codec.md's Non-goals). <c>channelMap</c> is not
/// ported as a parameter: every real caller in this codebase (shim.cpp's <c>pgf_decode_bgra</c>/
/// <c>pgf_encode_bgra_alloc</c>, and this port's own top-level decode/encode) uses the identity map
/// <c>{0,1,2,3}</c> (BGRA byte order, little-endian host), so it's hardcoded rather than threaded
/// through as unused generality. Likewise <c>pitch</c> is always <c>width*4</c> (contiguous, no row
/// padding) for every real caller - confirmed in shim.cpp - so it's not a parameter either.
/// </summary>
internal static class PgfColorConversion
{
    private const int YuvOffset8 = 128;

    /// <summary>Direct port of <c>RgbToYuv</c>'s <c>ImageModeRGBA</c> case (PGFimage.cpp:1578-1613):
    /// <c>Y = ((B + 2G + R) &gt;&gt; 2) - 128</c>, <c>U = R - G</c>, <c>V = B - G</c>,
    /// <c>A = Alpha - 128</c>. Full resolution for all four channels - downsampling (if any) is a
    /// separate step (<see cref="Downsample"/>), matching <c>ImportBitmap</c> calling them
    /// separately.</summary>
    public static void EncodeBgraToYuva(
        ReadOnlySpan<byte> bgra, int width, int height, Span<int> y, Span<int> u, Span<int> v, Span<int> a)
    {
        int pos = 0;
        int cnt = 0;
        int pixelCount = width * height;
        for (; pos < pixelCount; pos++)
        {
            byte b = bgra[cnt];
            byte g = bgra[cnt + 1];
            byte r = bgra[cnt + 2];
            byte alpha = bgra[cnt + 3];

            y[pos] = unchecked((int)(((b + (g << 1) + r) >> 2) - YuvOffset8));
            u[pos] = unchecked((int)(r - g));
            v[pos] = unchecked((int)(b - g));
            a[pos] = unchecked((int)(alpha - YuvOffset8));

            cnt += 4;
        }
    }

    /// <summary>Direct port of <c>CPGFImage::Downsample</c> (PGFimage.cpp:809) - 2x2 box-average
    /// subsampling of one chroma/alpha channel by a factor of 2, in place (the write cursor
    /// <c>sampledPos</c> never overtakes the read cursors <c>loPos</c>/<c>hiPos</c>, so overwriting
    /// the same backing buffer while reading ahead of it is safe, exactly as in the original). Only
    /// the first <c>NewWidth * NewHeight</c> elements of <paramref name="channel"/> are valid on
    /// return; the caller must slice accordingly (matching the original's <c>m_width[ch]</c>/
    /// <c>m_height[ch]</c> shrinking in place instead of reallocating).</summary>
    public static (int NewWidth, int NewHeight) Downsample(Span<int> channel, int width, int height)
    {
        int w2 = width / 2;
        int h2 = height / 2;
        int oddW = width % 2;
        int oddH = height % 2;
        int loPos = 0, hiPos = width, sampledPos = 0;

        for (int i = 0; i < h2; i++)
        {
            for (int j = 0; j < w2; j++)
            {
                channel[sampledPos] = unchecked((int)((channel[loPos] + channel[loPos + 1] + channel[hiPos] + channel[hiPos + 1]) >> 2));
                loPos += 2; hiPos += 2;
                sampledPos++;
            }

            if (oddW != 0)
            {
                channel[sampledPos] = unchecked((int)((channel[loPos] + channel[hiPos]) >> 1));
                loPos++; hiPos++;
                sampledPos++;
            }

            loPos += width; hiPos += width;
        }

        if (oddH != 0)
        {
            for (int j = 0; j < w2; j++)
            {
                channel[sampledPos] = unchecked((int)((channel[loPos] + channel[loPos + 1]) >> 1));
                loPos += 2; hiPos += 2;
                sampledPos++;
            }

            if (oddW != 0)
            {
                channel[sampledPos] = channel[loPos];
            }
        }

        return ((width + 1) / 2, (height + 1) / 2);
    }

    /// <summary>Direct port of <c>GetBitmap</c>'s <c>ImageModeRGBA</c> case (PGFimage.cpp:2232-2273):
    /// <c>G = Clamp8(Y + 128 - ((U + V) &gt;&gt; 2))</c>, <c>R = Clamp8(U + G)</c>,
    /// <c>B = Clamp8(V + G)</c>, <c>A = Clamp8(Alpha + 128)</c>. <paramref name="chromaWidth"/> and
    /// <paramref name="downsample"/> drive the same nearest-neighbor chroma/alpha upsampling as the
    /// original's <c>uPos</c>/<c>uOffset</c> bookkeeping: each chroma sample is reused across a 2x2
    /// block of output pixels when <paramref name="downsample"/> is true (each U/V/A array is
    /// expected at <paramref name="chromaWidth"/> x its matching height in that case; equal to
    /// <paramref name="width"/> x <paramref name="height"/>, same layout as <paramref name="y"/>,
    /// when false).</summary>
    public static void DecodeYuvaToBgra(
        ReadOnlySpan<int> y, ReadOnlySpan<int> u, ReadOnlySpan<int> v, ReadOnlySpan<int> a,
        int width, int height, int chromaWidth, bool downsample, Span<byte> bgra)
    {
        int yOffset = 0;
        int uOffset = 0;
        int rowStart = 0;

        for (int i = 0; i < height; i++)
        {
            int uPos = uOffset;
            int yPos = yOffset;
            int cnt = 0;

            for (int j = 0; j < width; j++)
            {
                int uAvg = u[uPos];
                int vAvg = v[uPos];
                byte aAvg = Clamp8(a[uPos] + YuvOffset8);

                byte g = Clamp8(y[yPos] + YuvOffset8 - ((uAvg + vAvg) >> 2));
                bgra[rowStart + cnt + 1] = g;
                bgra[rowStart + cnt + 2] = Clamp8(uAvg + g);
                bgra[rowStart + cnt] = Clamp8(vAvg + g);
                bgra[rowStart + cnt + 3] = aAvg;

                cnt += 4;
                if (!downsample || (j & 1) != 0)
                {
                    uPos++;
                }

                yPos++;
            }

            if (!downsample || (i & 1) != 0)
            {
                uOffset += chromaWidth;
            }

            yOffset += width;
            rowStart += width * 4;
        }
    }

    private static byte Clamp8(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    // ---- Group A (pgf-all-image-modes.md): GrayScale/IndexedColor/HSLColor/HSBColor. NOT LabColor
    // - direct inspection of GetBitmap (PGFimage.cpp:1886-1916 vs. 2124-2159) found Lab has its own
    // decode case with real chroma-upsample bookkeeping (matching RGBColor's structure), even though
    // it shares Group A's encode-side transform - see PgfModeInfo.SupportsDownsample's doc comment.
    // True Group A is never downsample-eligible, so decode here never needs upsample positioning:
    // one source pixel's channel value maps to exactly one output pixel, always.

    /// <summary>Direct port of RgbToYuv's Group-A case (PGFimage.cpp:1445-1473), 1-channel form
    /// (GrayScale/IndexedColor): the single source byte offset by <see cref="YuvOffset8"/> (128).
    /// <paramref name="channelBytes"/> is tightly packed, one byte per pixel (this port's own encode
    /// input contract - see class doc comment's pitch/channelMap reasoning).</summary>
    public static void EncodeSingleChannelToYuvOffset(ReadOnlySpan<byte> channelBytes, int width, int height, Span<int> channel)
    {
        int pixelCount = width * height;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            channel[pos] = unchecked(channelBytes[pos] - YuvOffset8);
        }
    }

    /// <summary>Direct port of RgbToYuv's Group-A case (PGFimage.cpp:1445-1473), 3-channel form
    /// (HSLColor/HSBColor - and LabColor's identical encode step, see class remarks above):
    /// each of 3 interleaved source bytes per pixel independently offset by <see cref="YuvOffset8"/>,
    /// no cross-channel transform.</summary>
    public static void EncodeTripleChannelToYuvOffset(
        ReadOnlySpan<byte> interleaved, int width, int height, Span<int> c0, Span<int> c1, Span<int> c2)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            c0[pos] = unchecked(interleaved[cnt] - YuvOffset8);
            c1[pos] = unchecked(interleaved[cnt + 1] - YuvOffset8);
            c2[pos] = unchecked(interleaved[cnt + 2] - YuvOffset8);
            cnt += 3;
        }
    }

    /// <summary>GrayScale's decode: reconstructs GetBitmap's Group-A single-channel byte
    /// (<c>Clamp8(channel + 128)</c>), then broadcasts it into B/G/R with A=255 - the "output stays
    /// BGRA32 regardless of source mode" expansion pgf-all-image-modes.md Goal 1 asks this port to
    /// add on top of what GetBitmap itself does (GetBitmap only ever writes exactly the source mode's
    /// own channel count of bytes per pixel - see native/PictTag.PgfDecoder/shim.cpp's
    /// <c>pgf_debug_decode_raw</c> doc comment for how this port's own oracle comparison isolates the
    /// real, verifiable reconstructed byte from this port's own broadcast).</summary>
    public static void DecodeYuvOffsetToGray(ReadOnlySpan<int> channel, int width, int height, Span<byte> bgra)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            byte gray = Clamp8(channel[pos] + YuvOffset8);
            bgra[cnt] = gray;
            bgra[cnt + 1] = gray;
            bgra[cnt + 2] = gray;
            bgra[cnt + 3] = 255;
            cnt += 4;
        }
    }

    /// <summary>IndexedColor's decode: reconstructs GetBitmap's Group-A single-channel byte the same
    /// way as <see cref="DecodeYuvOffsetToGray"/>, but the byte is a palette INDEX - substituted via
    /// <paramref name="colorTable"/> (raw RGBQUAD bytes, B/G/R/reserved per entry - the exact shape
    /// <see cref="PgfHeaderIO.Read"/> returns) rather than used directly as a gray level. A=255 (no
    /// real alpha channel in this mode).</summary>
    public static void DecodeYuvOffsetToIndexed(ReadOnlySpan<int> channel, int width, int height, ReadOnlySpan<byte> colorTable, Span<byte> bgra)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            byte index = Clamp8(channel[pos] + YuvOffset8);
            int paletteOffset = index * 4;
            bgra[cnt] = colorTable[paletteOffset]; // B
            bgra[cnt + 1] = colorTable[paletteOffset + 1]; // G
            bgra[cnt + 2] = colorTable[paletteOffset + 2]; // R
            bgra[cnt + 3] = 255;
            cnt += 4;
        }
    }

    /// <summary>HSLColor/HSBColor's decode: reconstructs GetBitmap's Group-A 3-channel bytes and
    /// packs them directly into B/G/R with no colorimetric HSL/HSB-to-RGB transform - this port's own
    /// Non-goals explicitly exclude "improving" the original codec's color science, and the native
    /// codec itself is "not semantically aware of HSL/HSB being different color spaces" at this layer
    /// (see PgfModeInfo's own doc comment) - so raw channel-to-byte-position packing is the faithful
    /// port, not a placeholder. A=255.</summary>
    public static void DecodeYuvOffsetToTripleChannel(
        ReadOnlySpan<int> c0, ReadOnlySpan<int> c1, ReadOnlySpan<int> c2, int width, int height, Span<byte> bgra)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            bgra[cnt] = Clamp8(c0[pos] + YuvOffset8);
            bgra[cnt + 1] = Clamp8(c1[pos] + YuvOffset8);
            bgra[cnt + 2] = Clamp8(c2[pos] + YuvOffset8);
            bgra[cnt + 3] = 255;
            cnt += 4;
        }
    }
}
