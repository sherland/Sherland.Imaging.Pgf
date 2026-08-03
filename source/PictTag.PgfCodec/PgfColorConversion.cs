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
}
