using System.Buffers.Binary;

namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CPGFImage::RgbToYuv</c>/<c>GetBitmap</c>/<c>Downsample</c>'s per-mode branches
/// (PGFimage.cpp:1388, 1788, 809) - one method-pair per group identified in pgf-all-image-modes.md
/// (RGBA is the only color mode real digiKam thumbnails use, per managed-pgf-codec.md's Non-goals,
/// but this port now also targets every other mode the format defines). <c>channelMap</c> is not
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

    // ---- Group C (pgf-all-image-modes.md): RGBColor - the genuine 3-channel version of RGBA's YUV
    // transform, just without an alpha channel.

    /// <summary>Direct port of <c>RgbToYuv</c>'s <c>ImageModeRGBColor</c> case (PGFimage.cpp:1505-1538):
    /// identical <c>Y</c>/<c>U</c>/<c>V</c> formulas to <see cref="EncodeBgraToYuva"/>, minus alpha.
    /// <paramref name="interleaved"/> is tightly packed BGR (3 bytes/pixel) - this port's own encode
    /// input contract (see class doc comment).</summary>
    public static void EncodeRgbToYuv(ReadOnlySpan<byte> interleaved, int width, int height, Span<int> y, Span<int> u, Span<int> v)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            byte b = interleaved[cnt];
            byte g = interleaved[cnt + 1];
            byte r = interleaved[cnt + 2];

            y[pos] = unchecked(((b + (g << 1) + r) >> 2) - YuvOffset8);
            u[pos] = unchecked(r - g);
            v[pos] = unchecked(b - g);

            cnt += 3;
        }
    }

    /// <summary>Direct port of <c>GetBitmap</c>'s <c>ImageModeRGBColor</c> case (PGFimage.cpp:1975-2046):
    /// identical reconstruction to <see cref="DecodeYuvaToBgra"/> (same <c>uPos</c>/<c>uOffset</c>
    /// chroma-upsampling bookkeeping), minus alpha - A is always 255 (this mode has no real alpha
    /// channel, matching Goal 1's "output stays BGRA32" contract).</summary>
    public static void DecodeYuvToBgra(
        ReadOnlySpan<int> y, ReadOnlySpan<int> u, ReadOnlySpan<int> v,
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

                byte g = Clamp8(y[yPos] + YuvOffset8 - ((uAvg + vAvg) >> 2));
                bgra[rowStart + cnt + 1] = g;
                bgra[rowStart + cnt + 2] = Clamp8(uAvg + g);
                bgra[rowStart + cnt] = Clamp8(vAvg + g);
                bgra[rowStart + cnt + 3] = 255;

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

    // ---- LabColor (pgf-all-image-modes.md): its own group, not really Group A or Group C -
    // shares Group A's simple per-channel encode transform (no cross-channel math), but needs Group
    // C's chroma-upsample bookkeeping on decode (channel 0/L always full-resolution, channels 1-2/
    // a,b upsampled) since Lab is downsample-eligible and Group A's other members aren't - see
    // PgfModeInfo.SupportsDownsample's doc comment for the real, confirmed reason.

    /// <summary>Direct port of <c>GetBitmap</c>'s <c>ImageModeLabColor</c> case (PGFimage.cpp:2124-2159):
    /// each of 3 channels independently offset by <see cref="YuvOffset8"/> like Group A's
    /// <see cref="DecodeYuvOffsetToTripleChannel"/> (no cross-channel transform), but channels 1,2
    /// (a,b) use the same <c>uPos</c>/<c>uOffset</c> chroma-upsampling bookkeeping as
    /// <see cref="DecodeYuvToBgra"/>/<see cref="DecodeYuvaToBgra"/> - channel 0 (L) always stays full
    /// resolution. A=255 (no real alpha channel).</summary>
    public static void DecodeYuvOffsetToTripleChannelWithUpsample(
        ReadOnlySpan<int> c0, ReadOnlySpan<int> c1, ReadOnlySpan<int> c2,
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
                bgra[rowStart + cnt] = Clamp8(c0[yPos] + YuvOffset8);
                bgra[rowStart + cnt + 1] = Clamp8(c1[uPos] + YuvOffset8);
                bgra[rowStart + cnt + 2] = Clamp8(c2[uPos] + YuvOffset8);
                bgra[rowStart + cnt + 3] = 255;

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

    // ---- Groups B/D/F (pgf-all-image-modes.md): Gray16/Lab48 (Group B), RGB48 (Group D),
    // CMYK64 (Group F), plus Gray32 - the 16-/32-bit-per-channel scaled versions of Groups A/C/E.
    // This is exactly the territory Stage 0's DataT short->int widening exists for: encode's
    // yuvOffset16 (32768) alone exceeds Int16.MaxValue, and RgbToYuv/GetBitmap's own real
    // UsedBitsPerChannel()-driven shift is always 0 on encode (native bit depth == this port's own
    // canonical PgfModeInfo.UsedBitsPerChannel for every mode it ever writes, so the "0 means no-op"
    // case is the only one these methods need to model - not a simplification, just what's real).
    //
    // Decode always downscales to this port's mandatory 8-bit BGRA32 output (Goal 1) using exactly
    // the same <c>bpp%8==0</c>/<c>bpp==8</c> branch GetBitmap itself offers real callers
    // (PGFimage.cpp:1949-1972, 2091-2121, 2201-2229, 2321-2353, 2419-2436) - not an invented
    // downscale, the native codec's own documented "caller picks the output bpp" contract
    // (PGFimage.cpp:1772-1787's doc comment), just always exercised at 8bpp since that's this port's
    // only public decode shape. The cross-channel transform (RGB48/CMYK64) always happens at full
    // 16-bit precision *before* the final <c>Clamp8(value &gt;&gt; shift)</c> downscale - confirmed
    // directly in the source order, not assumed.

    private const int YuvOffset16 = 1 << 15; // 1 << (UsedBitsPerChannel()-1), UsedBitsPerChannel()=16
    private const int Yuv16To8Shift = 8; // max(0, UsedBitsPerChannel()-8) = max(0, 16-8)
    private const int YuvOffset31 = 1 << 30; // 1 << (UsedBitsPerChannel()-1), UsedBitsPerChannel()=31 (Gray32, capped)
    private const int Yuv32To8Shift = 23; // max(0, UsedBitsPerChannel()-8) = max(0, 31-8)

    /// <summary>Encode side of Gray16: direct port of <c>RgbToYuv</c>'s <c>ImageModeGray16</c>/
    /// <c>ImageModeLab48</c> case (PGFimage.cpp:1474-1504), 1-channel form. <paramref
    /// name="channelBytes"/> is tightly packed 16-bit-per-pixel, little-endian (2 bytes/pixel) - this
    /// port's own encode input contract for every 16-bit-per-channel mode (matching real 16-bit
    /// image data's natural byte layout, e.g. a 16-bit-per-channel TIFF/PNG source).</summary>
    public static void EncodeSingleChannel16ToYuvOffset(ReadOnlySpan<byte> channelBytes, int width, int height, Span<int> channel)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(channelBytes[cnt..]);
            channel[pos] = unchecked(value - YuvOffset16);
            cnt += 2;
        }
    }

    /// <summary>Encode side of Lab48: direct port of the same <c>RgbToYuv</c> case as
    /// <see cref="EncodeSingleChannel16ToYuvOffset"/> (PGFimage.cpp:1474-1504), 3-channel form -
    /// each of 3 interleaved 16-bit source values per pixel independently offset, no cross-channel
    /// transform (same relationship to <see cref="EncodeSingleChannel16ToYuvOffset"/> as Group A's
    /// <see cref="EncodeTripleChannelToYuvOffset"/> has to <see cref="EncodeSingleChannelToYuvOffset"/>).</summary>
    public static void EncodeTripleChannel16ToYuvOffset(
        ReadOnlySpan<byte> interleaved, int width, int height, Span<int> c0, Span<int> c1, Span<int> c2)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            c0[pos] = unchecked(BinaryPrimitives.ReadUInt16LittleEndian(interleaved[cnt..]) - YuvOffset16);
            c1[pos] = unchecked(BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 2)..]) - YuvOffset16);
            c2[pos] = unchecked(BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 4)..]) - YuvOffset16);
            cnt += 6;
        }
    }

    /// <summary>Encode side of RGB48: direct port of <c>RgbToYuv</c>'s <c>ImageModeRGB48</c> case
    /// (PGFimage.cpp:1539-1577) - the 16-bit-per-channel version of <see cref="EncodeRgbToYuv"/>'s
    /// real YUV transform. <paramref name="interleaved"/> is tightly packed BGR, 16 bits/channel,
    /// little-endian (6 bytes/pixel).</summary>
    public static void EncodeRgb48ToYuv(ReadOnlySpan<byte> interleaved, int width, int height, Span<int> y, Span<int> u, Span<int> v)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            int b = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[cnt..]);
            int g = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 2)..]);
            int r = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 4)..]);

            y[pos] = unchecked(((b + (g << 1) + r) >> 2) - YuvOffset16);
            u[pos] = unchecked(r - g);
            v[pos] = unchecked(b - g);

            cnt += 6;
        }
    }

    /// <summary>Encode side of CMYK64: direct port of <c>RgbToYuv</c>'s <c>ImageModeCMYK64</c> case
    /// (PGFimage.cpp:1614-1653) - the 16-bit-per-channel version of <see cref="EncodeBgraToYuva"/>'s
    /// real transform (same established "4th channel treated as alpha-like" precedent as
    /// <see cref="PgfConstants.ImageModeCMYKColor"/> - PgfImageEncoder's own dispatch comment).
    /// <paramref name="interleaved"/> is tightly packed, 16 bits/channel, little-endian (8 bytes/pixel).</summary>
    public static void EncodeCmyk64ToYuva(
        ReadOnlySpan<byte> interleaved, int width, int height, Span<int> y, Span<int> u, Span<int> v, Span<int> a)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            int b = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[cnt..]);
            int g = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 2)..]);
            int r = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 4)..]);
            int alpha = BinaryPrimitives.ReadUInt16LittleEndian(interleaved[(cnt + 6)..]);

            y[pos] = unchecked(((b + (g << 1) + r) >> 2) - YuvOffset16);
            u[pos] = unchecked(r - g);
            v[pos] = unchecked(b - g);
            a[pos] = unchecked(alpha - YuvOffset16);

            cnt += 8;
        }
    }

    /// <summary>Encode side of Gray32: direct port of <c>RgbToYuv</c>'s <c>ImageModeGray32</c> case
    /// (PGFimage.cpp:1655-1681, <c>__PGF32SUPPORT__</c> - active in this build, see
    /// <see cref="PgfConstants"/>'s doc comment). <paramref name="channelBytes"/> is tightly packed
    /// 32-bit-per-pixel, little-endian (4 bytes/pixel).</summary>
    public static void EncodeSingleChannel32ToYuvOffset(ReadOnlySpan<byte> channelBytes, int width, int height, Span<int> channel)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(channelBytes[cnt..]);
            channel[pos] = unchecked((int)(value - YuvOffset31));
            cnt += 4;
        }
    }

    /// <summary>Decode side of Gray16: direct port of <c>GetBitmap</c>'s <c>ImageModeGray16</c>
    /// case's <c>bpp%8==0</c> (8-bit output) branch (PGFimage.cpp:1949-1972) - reconstructs then
    /// downscales (<c>Clamp8((channel + 32768) &gt;&gt; 8)</c>), broadcasts into B/G/R like Group A's
    /// <see cref="DecodeYuvOffsetToGray"/>, A=255.</summary>
    public static void DecodeYuvOffset16ToGray(ReadOnlySpan<int> channel, int width, int height, Span<byte> bgra)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            byte gray = Clamp8((channel[pos] + YuvOffset16) >> Yuv16To8Shift);
            bgra[cnt] = gray;
            bgra[cnt + 1] = gray;
            bgra[cnt + 2] = gray;
            bgra[cnt + 3] = 255;
            cnt += 4;
        }
    }

    /// <summary>Decode side of Lab48: direct port of <c>GetBitmap</c>'s <c>ImageModeLab48</c> case's
    /// <c>bpp%8==0</c> branch (PGFimage.cpp:2201-2229) - same <c>uPos</c>/<c>uOffset</c>
    /// chroma-upsampling structure as <see cref="DecodeYuvOffsetToTripleChannelWithUpsample"/>
    /// (LabColor's 8-bit decode), each channel independently reconstructed then downscaled.</summary>
    public static void DecodeYuvOffset16ToTripleChannelWithUpsample(
        ReadOnlySpan<int> c0, ReadOnlySpan<int> c1, ReadOnlySpan<int> c2,
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
                bgra[rowStart + cnt] = Clamp8((c0[yPos] + YuvOffset16) >> Yuv16To8Shift);
                bgra[rowStart + cnt + 1] = Clamp8((c1[uPos] + YuvOffset16) >> Yuv16To8Shift);
                bgra[rowStart + cnt + 2] = Clamp8((c2[uPos] + YuvOffset16) >> Yuv16To8Shift);
                bgra[rowStart + cnt + 3] = 255;

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

    /// <summary>Decode side of RGB48: direct port of <c>GetBitmap</c>'s <c>ImageModeRGB48</c> case's
    /// <c>bpp%8==0</c> branch (PGFimage.cpp:2091-2121) - the cross-channel YUV transform happens at
    /// full 16-bit precision (matching <see cref="DecodeYuvToBgra"/> exactly), only the final output
    /// byte is downscaled (<c>Clamp8(value &gt;&gt; 8)</c>). A=255.</summary>
    public static void DecodeYuv48ToBgra(
        ReadOnlySpan<int> y, ReadOnlySpan<int> u, ReadOnlySpan<int> v,
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

                int g = y[yPos] + YuvOffset16 - ((uAvg + vAvg) >> 2);
                bgra[rowStart + cnt + 1] = Clamp8(g >> Yuv16To8Shift);
                bgra[rowStart + cnt + 2] = Clamp8((uAvg + g) >> Yuv16To8Shift);
                bgra[rowStart + cnt] = Clamp8((vAvg + g) >> Yuv16To8Shift);
                bgra[rowStart + cnt + 3] = 255;

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

    /// <summary>Decode side of CMYK64: direct port of <c>GetBitmap</c>'s <c>ImageModeCMYK64</c>
    /// case's <c>bpp%8==0</c> branch (PGFimage.cpp:2321-2353) - like <see cref="DecodeYuv48ToBgra"/>
    /// plus a real (downscaled) alpha channel, matching <see cref="DecodeYuvaToBgra"/>'s established
    /// "4th channel is real alpha" precedent for the RGBA/CMYK family.</summary>
    public static void DecodeYuv64ToBgra(
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
                int aAvg = a[uPos] + YuvOffset16;

                int g = y[yPos] + YuvOffset16 - ((uAvg + vAvg) >> 2);
                bgra[rowStart + cnt + 1] = Clamp8(g >> Yuv16To8Shift);
                bgra[rowStart + cnt + 2] = Clamp8((uAvg + g) >> Yuv16To8Shift);
                bgra[rowStart + cnt] = Clamp8((vAvg + g) >> Yuv16To8Shift);
                bgra[rowStart + cnt + 3] = Clamp8(aAvg >> Yuv16To8Shift);

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

    /// <summary>Decode side of Gray32: direct port of <c>GetBitmap</c>'s <c>ImageModeGray32</c>
    /// case's <c>bpp==8</c> branch (PGFimage.cpp:2419-2436, <c>__PGF32SUPPORT__</c>). Broadcasts into
    /// B/G/R like <see cref="DecodeYuvOffset16ToGray"/>, A=255. Genuinely needs Stage 0's DataT
    /// widening: <see cref="YuvOffset31"/> alone (2^30) is far outside <c>short</c>'s range.</summary>
    public static void DecodeYuvOffset31ToGray(ReadOnlySpan<int> channel, int width, int height, Span<byte> bgra)
    {
        int pixelCount = width * height;
        int cnt = 0;
        for (int pos = 0; pos < pixelCount; pos++)
        {
            byte gray = Clamp8((channel[pos] + YuvOffset31) >> Yuv32To8Shift);
            bgra[cnt] = gray;
            bgra[cnt + 1] = gray;
            bgra[cnt + 2] = gray;
            bgra[cnt + 3] = 255;
            cnt += 4;
        }
    }
}
