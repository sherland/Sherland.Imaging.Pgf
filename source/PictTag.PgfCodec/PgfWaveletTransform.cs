namespace PictTag.PgfCodec;

internal enum PgfCodecError
{
    None,
    InsufficientMemory,
}

/// <summary>
/// Direct port of <c>CWaveletTransform</c> (WaveletTransform.h/WaveletTransform.cpp) - the
/// per-channel pyramid of <see cref="PgfSubband"/>s and the integer lifting transform between
/// levels. Row-pointer arithmetic (<c>DataT* row0</c> etc., reassigned and offset throughout the
/// original) is translated to plain <see cref="int"/> offsets into the flat backing array, sliced
/// into a <see cref="Span{T}"/> only where a whole-row operation (<see cref="ForwardRow"/>/
/// <see cref="InverseRow"/>) needs one - array indexing and span slicing are two views of the same
/// memory, so mixing them (as the original mixes pointer arithmetic and whole-row calls) is safe
/// and behaves identically.
///
/// ROI is not ported (see <see cref="PgfMacroBlock"/>'s doc comment for why that's justified) -
/// <see cref="InverseTransform"/> and <see cref="SubbandsToInterleaved"/> port only the non-ROI
/// <c>#else</c> branches of the original (Width/Height directly, <c>InitBuffPos()</c> resetting to
/// 0, no aligned-ROI position bookkeeping).
/// </summary>
internal sealed class PgfWaveletTransform
{
    private const int C1 = 1; // best value 1, per the original's own comment
    private const int C2 = 2; // best value 2

    private readonly int levelCount; // CWaveletTransform's own m_nLevels = levels + 1 (subband-plane count)
    private readonly PgfSubband[][] subbands;

    public PgfWaveletTransform(int width, int height, int levels, int[]? data = null)
    {
        levelCount = levels + 1;
        subbands = new PgfSubband[levelCount][];

        int loWidth = width, hiWidth = width, loHeight = height, hiHeight = height;
        for (int level = 0; level < levelCount; level++)
        {
            subbands[level] =
            [
                new PgfSubband(),
                new PgfSubband(),
                new PgfSubband(),
                new PgfSubband(),
            ];

            subbands[level][(int)PgfSubbandOrientation.Ll].Initialize(loWidth, loHeight, level, PgfSubbandOrientation.Ll);
            subbands[level][(int)PgfSubbandOrientation.Hl].Initialize(hiWidth, loHeight, level, PgfSubbandOrientation.Hl);
            subbands[level][(int)PgfSubbandOrientation.Lh].Initialize(loWidth, hiHeight, level, PgfSubbandOrientation.Lh);
            subbands[level][(int)PgfSubbandOrientation.Hh].Initialize(hiWidth, hiHeight, level, PgfSubbandOrientation.Hh);

            hiWidth = loWidth >> 1;
            hiHeight = loHeight >> 1;
            loWidth = (loWidth + 1) >> 1;
            loHeight = (loHeight + 1) >> 1;
        }

        if (data is not null)
        {
            subbands[0][(int)PgfSubbandOrientation.Ll].SetBuffer(data);
        }
    }

    public PgfSubband GetSubband(int level, PgfSubbandOrientation orientation) => subbands[level][(int)orientation];

    /// <summary>Direct port of <c>CWaveletTransform::ForwardTransform</c> (WaveletTransform.cpp:89) -
    /// forward lifting transform of the LL subband at <paramref name="level"/>, splitting the result
    /// into all 4 subbands at <c>level + 1</c>, then quantizing (encode only - decode's dequantization
    /// happens per-value in <see cref="PgfDecoderCore.DequantizeValue"/>, not as a separate subband
    /// pass) and freeing the now-consumed source band.</summary>
    public PgfCodecError ForwardTransform(int level, int quant)
    {
        int destLevel = level + 1;
        PgfSubband srcBand = subbands[level][(int)PgfSubbandOrientation.Ll];
        int width = srcBand.Width;
        int height = srcBand.Height;
        int[] src = srcBand.GetBuffer();

        for (int i = 0; i < PgfConstants.NSubbands; i++)
        {
            if (!subbands[destLevel][i].AllocMemory())
            {
                return PgfCodecError.InsufficientMemory;
            }
        }

        if (height >= PgfConstants.FilterSize)
        {
            int row0 = 0, row1 = width, row2 = 2 * width, row3;
            ForwardRow(src.AsSpan(row0, width));
            ForwardRow(src.AsSpan(row1, width));
            ForwardRow(src.AsSpan(row2, width));
            for (int k = 0; k < width; k++)
            {
                src[row1 + k] = unchecked((int)(src[row1 + k] - ((src[row0 + k] + src[row2 + k] + C1) >> 1))); // high pass
                src[row0 + k] = unchecked((int)(src[row0 + k] + ((src[row1 + k] + C1) >> 1))); // low pass
            }

            InterleavedToSubbands(destLevel, src.AsSpan(row0, width), src.AsSpan(row1, width));
            row0 = row1; row1 = row2; row2 += width; row3 = row2 + width;

            for (int i = 3; i < height - 1; i += 2)
            {
                ForwardRow(src.AsSpan(row2, width));
                ForwardRow(src.AsSpan(row3, width));
                for (int k = 0; k < width; k++)
                {
                    src[row2 + k] = unchecked((int)(src[row2 + k] - ((src[row1 + k] + src[row3 + k] + C1) >> 1))); // high pass
                    src[row1 + k] = unchecked((int)(src[row1 + k] + ((src[row0 + k] + src[row2 + k] + C2) >> 2))); // low pass
                }

                InterleavedToSubbands(destLevel, src.AsSpan(row1, width), src.AsSpan(row2, width));
                row0 = row2; row1 = row3; row2 = row3 + width; row3 = row2 + width;
            }

            if ((height & 1) != 0)
            {
                for (int k = 0; k < width; k++)
                {
                    src[row1 + k] = unchecked((int)(src[row1 + k] + ((src[row0 + k] + C1) >> 1))); // low pass
                }

                InterleavedToSubbands(destLevel, src.AsSpan(row1, width), default);
            }
            else
            {
                ForwardRow(src.AsSpan(row2, width));
                for (int k = 0; k < width; k++)
                {
                    src[row2 + k] = unchecked((int)(src[row2 + k] - src[row1 + k])); // high pass
                    src[row1 + k] = unchecked((int)(src[row1 + k] + ((src[row0 + k] + src[row2 + k] + C2) >> 2))); // low pass
                }

                InterleavedToSubbands(destLevel, src.AsSpan(row1, width), src.AsSpan(row2, width));
            }
        }
        else
        {
            int row0 = 0, row1 = width;
            for (int k = 0; k < height; k += 2)
            {
                ForwardRow(src.AsSpan(row0, width));
                ForwardRow(src.AsSpan(row1, width));
                InterleavedToSubbands(destLevel, src.AsSpan(row0, width), src.AsSpan(row1, width));
                row0 += width << 1;
                row1 += width << 1;
            }

            if ((height & 1) != 0)
            {
                InterleavedToSubbands(destLevel, src.AsSpan(row0, width), default);
            }
        }

        if (quant > 0)
        {
            // subband quantization (without LL)
            for (int i = 1; i < PgfConstants.NSubbands; i++)
            {
                subbands[destLevel][i].Quantize(quant);
            }

            // LL subband quantization, only at the coarsest level
            if (destLevel == levelCount - 1)
            {
                subbands[destLevel][(int)PgfSubbandOrientation.Ll].Quantize(quant);
            }
        }

        srcBand.FreeMemory();
        return PgfCodecError.None;
    }

    /// <summary>Direct port of <c>ForwardRow</c> (WaveletTransform.cpp:181) - forward lifting of one
    /// row. Low-pass filter at even positions: <c>1/8[-1, 2, (6), 2, -1]</c>; high-pass filter at
    /// odd positions: <c>1/4[-2, (4), -2]</c> (per the original's own filter-coefficient comment).</summary>
    private static void ForwardRow(Span<int> src)
    {
        int width = src.Length;
        if (width < PgfConstants.FilterSize)
        {
            return;
        }

        int i = 3;

        src[1] = unchecked((int)(src[1] - ((src[0] + src[2] + C1) >> 1))); // high pass
        src[0] = unchecked((int)(src[0] + ((src[1] + C1) >> 1))); // low pass

        for (; i < width - 1; i += 2)
        {
            src[i] = unchecked((int)(src[i] - ((src[i - 1] + src[i + 1] + C1) >> 1))); // high pass
            src[i - 1] = unchecked((int)(src[i - 1] + ((src[i - 2] + src[i] + C2) >> 2))); // low pass
        }

        if ((width & 1) != 0)
        {
            src[i - 1] = unchecked((int)(src[i - 1] + ((src[i - 2] + C1) >> 1))); // low pass
        }
        else
        {
            src[i] = unchecked((int)(src[i] - src[i - 1])); // high pass
            src[i - 1] = unchecked((int)(src[i - 1] + ((src[i - 2] + src[i] + C2) >> 2))); // low pass
        }
    }

    /// <summary>Direct port of <c>InterleavedToSubbands</c> (WaveletTransform.cpp:207) - splits one
    /// (or two, low+high) transformed, interleaved row(s) into the four child subbands at
    /// <paramref name="destLevel"/>. <paramref name="hiRow"/> empty means "null" (the original's
    /// last-odd-row case, where there is no high-pass partner row).</summary>
    private void InterleavedToSubbands(int destLevel, ReadOnlySpan<int> loRow, ReadOnlySpan<int> hiRow)
    {
        int width = loRow.Length;
        int wquot = width >> 1;
        bool wrem = (width & 1) != 0;
        PgfSubband ll = subbands[destLevel][(int)PgfSubbandOrientation.Ll];
        PgfSubband hl = subbands[destLevel][(int)PgfSubbandOrientation.Hl];
        PgfSubband lh = subbands[destLevel][(int)PgfSubbandOrientation.Lh];
        PgfSubband hh = subbands[destLevel][(int)PgfSubbandOrientation.Hh];

        if (!hiRow.IsEmpty)
        {
            int lo = 0, hi = 0;
            for (int i = 0; i < wquot; i++)
            {
                ll.WriteBuffer(loRow[lo++]);
                hl.WriteBuffer(loRow[lo++]);
                lh.WriteBuffer(hiRow[hi++]);
                hh.WriteBuffer(hiRow[hi++]);
            }

            if (wrem)
            {
                ll.WriteBuffer(loRow[lo]);
                lh.WriteBuffer(hiRow[hi]);
            }
        }
        else
        {
            int lo = 0;
            for (int i = 0; i < wquot; i++)
            {
                ll.WriteBuffer(loRow[lo++]);
                hl.WriteBuffer(loRow[lo++]);
            }

            if (wrem)
            {
                ll.WriteBuffer(loRow[lo]);
            }
        }
    }

    /// <summary>Direct port of <c>CWaveletTransform::InverseTransform</c>'s non-ROI branch
    /// (WaveletTransform.cpp:246) - inverse lifting transform of all 4 subbands at
    /// <paramref name="srcLevel"/>, combined into the LL subband at <c>srcLevel - 1</c>. The
    /// just-consumed <paramref name="srcLevel"/> subbands are freed at the end - this is the actual
    /// mechanism behind "continuing from wherever it left off" between separate progressive-decode
    /// calls (Stage 9), not a special resume protocol: re-decoding an already-consumed level is
    /// structurally impossible once its memory is gone.</summary>
    public PgfCodecError InverseTransform(int srcLevel, out int width, out int height, out int[] data)
    {
        int destLevel = srcLevel - 1;
        PgfSubband destBand = subbands[destLevel][(int)PgfSubbandOrientation.Ll];

        if (!destBand.AllocMemory())
        {
            width = height = 0;
            data = [];
            return PgfCodecError.InsufficientMemory;
        }

        int[] destBuffer = destBand.GetBuffer();
        int origin = 0;

        width = destBand.Width;
        height = destBand.Height;
        int destWidth = width;
        int destHeight = height;

        for (int i = 0; i < PgfConstants.NSubbands; i++)
        {
            subbands[srcLevel][i].InitBuffPos();
        }

        int row0, row1, row2, row3;

        if (destHeight >= PgfConstants.FilterSize)
        {
            row0 = origin; row1 = row0 + destWidth;
            SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row0, width), destBuffer.AsSpan(row1, width));
            for (int k = 0; k < width; k++)
            {
                destBuffer[row0 + k] = unchecked((int)(destBuffer[row0 + k] - ((destBuffer[row1 + k] + C1) >> 1))); // even
            }

            row2 = row1 + destWidth; row3 = row2 + destWidth;
            for (int i = 2; i < destHeight - 1; i += 2)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row2, width), destBuffer.AsSpan(row3, width));
                for (int k = 0; k < width; k++)
                {
                    destBuffer[row2 + k] = unchecked((int)(destBuffer[row2 + k] - ((destBuffer[row1 + k] + destBuffer[row3 + k] + C2) >> 2))); // even
                    destBuffer[row1 + k] = unchecked((int)(destBuffer[row1 + k] + ((destBuffer[row0 + k] + destBuffer[row2 + k] + C1) >> 1))); // odd
                }

                InverseRow(destBuffer.AsSpan(row0, width));
                InverseRow(destBuffer.AsSpan(row1, width));
                row0 = row2; row1 = row3; row2 = row1 + destWidth; row3 = row2 + destWidth;
            }

            if ((destHeight & 1) != 0)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row2, width), default);
                for (int k = 0; k < width; k++)
                {
                    destBuffer[row2 + k] = unchecked((int)(destBuffer[row2 + k] - ((destBuffer[row1 + k] + C1) >> 1))); // even
                    destBuffer[row1 + k] = unchecked((int)(destBuffer[row1 + k] + ((destBuffer[row0 + k] + destBuffer[row2 + k] + C1) >> 1))); // odd
                }

                InverseRow(destBuffer.AsSpan(row0, width));
                InverseRow(destBuffer.AsSpan(row1, width));
                InverseRow(destBuffer.AsSpan(row2, width));
            }
            else
            {
                for (int k = 0; k < width; k++)
                {
                    destBuffer[row1 + k] = unchecked((int)(destBuffer[row1 + k] + destBuffer[row0 + k]));
                }

                InverseRow(destBuffer.AsSpan(row0, width));
                InverseRow(destBuffer.AsSpan(row1, width));
            }
        }
        else
        {
            row0 = origin; row1 = row0 + destWidth;
            for (int k = 0; k < destHeight; k += 2)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row0, width), destBuffer.AsSpan(row1, width));
                InverseRow(destBuffer.AsSpan(row0, width));
                InverseRow(destBuffer.AsSpan(row1, width));
                row0 += destWidth << 1;
                row1 += destWidth << 1;
            }

            if ((destHeight & 1) != 0)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row0, width), default);
                InverseRow(destBuffer.AsSpan(row0, width));
            }
        }

        for (int i = 0; i < PgfConstants.NSubbands; i++)
        {
            subbands[srcLevel][i].FreeMemory();
        }

        data = destBuffer;
        return PgfCodecError.None;
    }

    /// <summary>Direct port of <c>InverseRow</c> (WaveletTransform.cpp:420) - inverse lifting of one
    /// row. Low-pass coefficients at even positions, high-pass at odd; inverse filter for even
    /// positions: <c>1/4[-1, (4), -1]</c>; for odd positions: <c>1/8[-1, 4, (6), 4, -1]</c>.</summary>
    private static void InverseRow(Span<int> dest)
    {
        int width = dest.Length;
        if (width < PgfConstants.FilterSize)
        {
            return;
        }

        int i = 2;

        dest[0] = unchecked((int)(dest[0] - ((dest[1] + C1) >> 1))); // even

        for (; i < width - 1; i += 2)
        {
            dest[i] = unchecked((int)(dest[i] - ((dest[i - 1] + dest[i + 1] + C2) >> 2))); // even
            dest[i - 1] = unchecked((int)(dest[i - 1] + ((dest[i - 2] + dest[i] + C1) >> 1))); // odd
        }

        if ((width & 1) != 0)
        {
            dest[i] = unchecked((int)(dest[i] - ((dest[i - 1] + C1) >> 1))); // even
            dest[i - 1] = unchecked((int)(dest[i - 1] + ((dest[i - 2] + dest[i] + C1) >> 1))); // odd
        }
        else
        {
            dest[i - 1] = unchecked((int)(dest[i - 1] + dest[i - 2])); // odd
        }
    }

    /// <summary>Direct port of <c>SubbandsToInterleaved</c>'s non-ROI branch (WaveletTransform.cpp:445)
    /// - the read-side mirror of <see cref="InterleavedToSubbands"/>: recombines the four child
    /// subbands at <paramref name="srcLevel"/> back into one (or two) interleaved row(s).</summary>
    private void SubbandsToInterleaved(int srcLevel, Span<int> loRow, Span<int> hiRow)
    {
        int width = loRow.Length;
        int wquot = width >> 1;
        bool wrem = (width & 1) != 0;
        PgfSubband ll = subbands[srcLevel][(int)PgfSubbandOrientation.Ll];
        PgfSubband hl = subbands[srcLevel][(int)PgfSubbandOrientation.Hl];
        PgfSubband lh = subbands[srcLevel][(int)PgfSubbandOrientation.Lh];
        PgfSubband hh = subbands[srcLevel][(int)PgfSubbandOrientation.Hh];

        if (!hiRow.IsEmpty)
        {
            int lo = 0, hi = 0;
            for (int i = 0; i < wquot; i++)
            {
                loRow[lo++] = ll.ReadBuffer();
                loRow[lo++] = hl.ReadBuffer();
                hiRow[hi++] = lh.ReadBuffer();
                hiRow[hi++] = hh.ReadBuffer();
            }

            if (wrem)
            {
                loRow[lo] = ll.ReadBuffer();
                hiRow[hi] = lh.ReadBuffer();
            }
        }
        else
        {
            int lo = 0;
            for (int i = 0; i < wquot; i++)
            {
                loRow[lo++] = ll.ReadBuffer();
                loRow[lo++] = hl.ReadBuffer();
            }

            if (wrem)
            {
                loRow[lo] = ll.ReadBuffer();
            }
        }
    }
}
