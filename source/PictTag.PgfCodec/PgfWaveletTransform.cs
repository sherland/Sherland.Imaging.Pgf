using System.Numerics;

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
/// ROI tile-index geometry (<see cref="SetROI"/>/<see cref="GetNofTiles"/>/<see cref="TileIsRelevant"/>)
/// is ported as of <c>pgf-roi-support.md</c> Stage 2, but <see cref="InverseTransform"/> and
/// <see cref="SubbandsToInterleaved"/> still only port the non-ROI <c>#else</c> branches of the
/// original (Width/Height directly, <c>InitBuffPos()</c> resetting to 0, no aligned-ROI position
/// bookkeeping) - real ROI-aware reconstruction is a Stage 3 concern, not this stage's (see
/// <see cref="SetROI"/>'s own doc comment for why the two are safely separable: pure tile-index
/// geometry has no interaction with the pixel data flow until something actually calls it).
/// </summary>
internal sealed class PgfWaveletTransform
{
    /// <summary>Test hook for proving the scalar fallback against the same input as the accelerated
    /// path. Production code always leaves this at its default value.</summary>
    internal static bool ForceScalarVectorsForTesting { get; set; }
    private const int C1 = 1; // best value 1, per the original's own comment
    private const int C2 = 2; // best value 2

    private readonly int levelCount; // CWaveletTransform's own m_nLevels = levels + 1 (subband-plane count)
    private readonly PgfSubband[][] subbands;

    /// <summary>Mirrors <c>CWaveletTransform::m_indices</c> - array of length <see cref="levelCount"/>
    /// of tile *index* bounds (not pixel bounds) per level, computed by <see cref="SetROI"/>. Null
    /// until <see cref="SetROI"/> is called (nothing calls it yet - Stage 3/4 wire ROI decode/encode
    /// through it).</summary>
    private PgfRoi[]? indices;

    public PgfWaveletTransform(int width, int height, int levels, int[]? data = null, PgfWorkspace? workspace = null)
    {
        levelCount = levels + 1;
        subbands = new PgfSubband[levelCount][];

        int loWidth = width, hiWidth = width, loHeight = height, hiHeight = height;
        for (int level = 0; level < levelCount; level++)
        {
            subbands[level] =
            [
                new PgfSubband(workspace),
                new PgfSubband(workspace),
                new PgfSubband(workspace),
                new PgfSubband(workspace),
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

    /// <summary>Resets all per-pass subband state so a reusable decode session can rewind its
    /// bitstream and reacquire workspace backing arrays without retaining prior coefficients.</summary>
    public void ResetForDecode()
    {
        indices = null;
        foreach (PgfSubband[] level in subbands)
        {
            foreach (PgfSubband subband in level)
            {
                subband.ResetForDecode();
            }
        }
    }

    /// <summary>Direct port of <c>CWaveletTransform::GetNofTiles</c> (WaveletTransform.h:125) -
    /// number of tiles in one dimension at <paramref name="level"/>, independent of any requested
    /// ROI (doubling every level going finer, per this PRD's "Why this needs to be grounded"
    /// section).</summary>
    public int GetNofTiles(int level) => 1 << (levelCount - level - 1);

    /// <summary>Direct port of <c>CWaveletTransform::TileIsRelevant</c> (WaveletTransform.h:119) -
    /// whether tile (<paramref name="tileX"/>, <paramref name="tileY"/>) at <paramref name="level"/>
    /// falls inside the tile-index bounds <see cref="SetROI"/> computed. Requires <see cref="SetROI"/>
    /// to have been called first (mirrors the native's own <c>ASSERT(m_indices)</c>).</summary>
    public bool TileIsRelevant(int level, int tileX, int tileY)
    {
        System.Diagnostics.Debug.Assert(indices is not null, "SetROI must be called before TileIsRelevant.");
        return indices![level].IsInside(tileX, tileY);
    }

    /// <summary>Direct port of <c>CWaveletTransform::GetAlignedROI</c> (WaveletTransform.h:130) -
    /// the aligned ROI (in pixels) of the LL subband at <paramref name="level"/>, as computed by
    /// <see cref="SetROI"/>.</summary>
    public PgfRoi GetAlignedROI(int level) => subbands[level][(int)PgfSubbandOrientation.Ll].AlignedRoi;

    /// <summary>Direct port of <c>CWaveletTransform::SetROI</c> (WaveletTransform.cpp:519) - computes
    /// and stores tile-index bounds (<see cref="indices"/>) and each subband's aligned pixel ROI, for
    /// every level from 0 (finest) up to <see cref="levelCount"/>-1 (coarsest).
    ///
    /// Pure geometry: reads only each <see cref="PgfSubband"/>'s <see cref="PgfSubband.Width"/>/
    /// <see cref="PgfSubband.Height"/> (fixed at construction) and writes only tile-index/aligned-ROI
    /// bookkeeping - never touches pixel data, which is exactly why this is safe to port ahead of the
    /// decode/encode data-flow changes that will actually *use* it (<c>pgf-roi-support.md</c> Stage 2
    /// vs. Stage 3/4).
    ///
    /// The native's own margin-enlargement step (<c>delta = (FilterSize &gt;&gt; 1) &lt;&lt;
    /// m_nLevels</c>, added to the requested rect before tiling) accounts for the wavelet filter's
    /// support width needing extra source pixels beyond the exact requested rectangle at every level
    /// - ported byte-for-byte, not re-derived, since getting the margin wrong would silently decode a
    /// too-small or misaligned region rather than visibly fail.
    ///
    /// The native's own cross-level nesting invariant (<c>WaveletTransform.cpp:544-545</c>) is a
    /// no-op <c>ASSERT</c> in the original (compiled out in Release) - ported here as a real,
    /// always-checked exception instead, per this PRD's "verify it, don't just port it silently"
    /// instruction, so an off-by-one in this geometry fails loudly here rather than silently
    /// decoding/encoding the wrong tiles three stages later.</summary>
    public void SetROI(PgfRoi roi)
    {
        int delta = (PgfConstants.FilterSize >> 1) << levelCount;

        indices = new PgfRoi[levelCount];

        int left = roi.Left > delta ? roi.Left - delta : 0;
        int top = roi.Top > delta ? roi.Top - delta : 0;
        int right = roi.Right + delta;
        int bottom = roi.Bottom + delta;

        for (int l = 0; l < levelCount; l++)
        {
            int nTiles = GetNofTiles(l);
            PgfSubband ll = subbands[l][(int)PgfSubbandOrientation.Ll];

            ll.SetNTiles(nTiles);
            ll.TileIndex(true, left, top, out int indicesLeft, out int indicesTop, out int alignedLeft, out int alignedTop);
            ll.TileIndex(false, right, bottom, out int indicesRight, out int indicesBottom, out int alignedRight, out int alignedBottom);
            var tileIndices = new PgfRoi(indicesLeft, indicesTop, indicesRight, indicesBottom);
            var alignedRoi = new PgfRoi(alignedLeft, alignedTop, alignedRight, alignedBottom);
            ll.SetAlignedRoi(alignedRoi);

            if (l > 0)
            {
                PgfRoi prev = indices[l - 1];
                if (prev.Left < 2 * tileIndices.Left || prev.Top < 2 * tileIndices.Top ||
                    prev.Right > 2 * tileIndices.Right || prev.Bottom > 2 * tileIndices.Bottom)
                {
                    throw new InvalidOperationException(
                        $"ROI tile-index nesting invariant violated between level {l - 1} and {l}.");
                }
            }

            indices[l] = tileIndices;

            for (int b = 1; b < PgfConstants.NSubbands; b++)
            {
                PgfSubband sb = subbands[l][b];
                sb.SetNTiles(nTiles);
                sb.TilePosition(tileIndices.Left, tileIndices.Top, out int aroiLeft, out int aroiTop, out _, out _);
                sb.TilePosition(tileIndices.Right - 1, tileIndices.Bottom - 1, out int aroiRight, out int aroiBottom, out int w, out int h);
                sb.SetAlignedRoi(new PgfRoi(aroiLeft, aroiTop, aroiRight + w, aroiBottom + h));
            }

            left = alignedRoi.Left >> 1;
            top = alignedRoi.Top >> 1;
            right = (alignedRoi.Right + 1) >> 1;
            bottom = (alignedRoi.Bottom + 1) >> 1;
        }
    }

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
                ForwardVerticalMiddle(src, row0, row1, row2, row3, width);

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

    /// <summary>Applies one vertical forward-lifting pair. Columns are independent at this point,
    /// so the hardware-vector path is byte-for-byte equivalent to the scalar loop; unsupported
    /// runtimes (including Browser/WASM without SIMD) retain the scalar fallback and scalar tail.</summary>
    private static void ForwardVerticalMiddle(int[] src, int row0, int row1, int row2, int row3, int width)
    {
        int k = 0;
        if (Vector.IsHardwareAccelerated && !ForceScalarVectorsForTesting)
        {
            int lanes = Vector<int>.Count;
            Vector<int> c1 = new(C1);
            Vector<int> c2 = new(C2);
            for (; k <= width - lanes; k += lanes)
            {
                Vector<int> previousLow = new(src, row0 + k);
                Vector<int> low = new(src, row1 + k);
                Vector<int> high = new(src, row2 + k);
                Vector<int> nextHigh = new(src, row3 + k);

                high -= (low + nextHigh + c1) >> 1;
                low += (previousLow + high + c2) >> 2;

                high.CopyTo(src, row2 + k);
                low.CopyTo(src, row1 + k);
            }
        }

        for (; k < width; k++)
        {
            src[row2 + k] = unchecked((int)(src[row2 + k] - ((src[row1 + k] + src[row3 + k] + C1) >> 1))); // high pass
            src[row1 + k] = unchecked((int)(src[row1 + k] + ((src[row0 + k] + src[row2 + k] + C2) >> 2))); // low pass
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

    /// <summary>Direct port of <c>CWaveletTransform::InverseTransform</c> (WaveletTransform.cpp:246),
    /// including its ROI branch (lines 257-332) - inverse lifting transform of all 4 subbands at
    /// <paramref name="srcLevel"/>, combined into the LL subband at <c>srcLevel - 1</c>. The
    /// just-consumed <paramref name="srcLevel"/> subbands are freed at the end - this is the actual
    /// mechanism behind "continuing from wherever it left off" between separate progressive-decode
    /// calls (Stage 9), not a special resume protocol: re-decoding an already-consumed level is
    /// structurally impossible once its memory is gone.
    ///
    /// <b>Why the ROI branch is needed even for shape fidelity, not just correctness</b>: the four
    /// <paramref name="srcLevel"/> subbands' <see cref="PgfSubband.AlignedRoi"/> are computed
    /// independently (<see cref="PgfSubband.TileIndex"/> for LL vs. <see cref="PgfSubband.TilePosition"/>
    /// for HL/LH/HH - <see cref="SetROI"/>), so they can disagree on their own top-left aligned
    /// pixel by up to one tile's worth at a boundary even though they nominally represent "the same"
    /// ROI at this level - the <c>srcOffsetX</c>/<c>srcOffsetY</c>/<c>destROI</c> reconciliation
    /// below (ported byte-for-byte, not re-derived - this PRD's Progress log Stage 2 entry already
    /// flagged this as more involved than the PRD's own architecture section implied) corrects for
    /// exactly that disagreement before combining them. When ROI was never set (every subband's
    /// <see cref="PgfSubband.AlignedRoi"/> still its <c>Initialize</c> default, the full subband),
    /// every offset/adjustment computed here works out to zero - verified in this method's own doc
    /// comment reasoning during Stage 3 development, and by the fact that the full pre-existing
    /// non-ROI test suite (managed-pgf-codec.md/pgf-all-image-modes.md/pgf-user-data-and-small-images.md,
    /// hundreds of tests) stays green after this generalization - not a separate code path.</summary>
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

        PgfRoi destRoi = destBand.AlignedRoi;
        int destWidth = destRoi.Width;
        int destHeight = destRoi.Height;
        width = destWidth;
        height = destHeight;

        int origin = 0;
        int workingWidth = destWidth;
        int workingHeight = destHeight;
        int destRoiLeft = destRoi.Left;
        int destRoiTop = destRoi.Top;

        // update destination ROI (WaveletTransform.cpp:264-274)
        if ((destRoiTop & 1) != 0)
        {
            destRoiTop++;
            origin += destWidth;
            workingHeight--;
        }

        if ((destRoiLeft & 1) != 0)
        {
            destRoiLeft++;
            origin++;
            workingWidth--;
        }

        PgfSubband srcLl = subbands[srcLevel][(int)PgfSubbandOrientation.Ll];
        PgfSubband srcHl = subbands[srcLevel][(int)PgfSubbandOrientation.Hl];
        PgfSubband srcLh = subbands[srcLevel][(int)PgfSubbandOrientation.Lh];
        PgfSubband srcHh = subbands[srcLevel][(int)PgfSubbandOrientation.Hh];

        // init source buffer position (WaveletTransform.cpp:276-331): reconcile each of the 4
        // srcLevel subbands' independently-computed AlignedRoi against destRoi's own (adjusted)
        // top-left, in units of level-0 pixels halved once per level (source subbands are one
        // pyramid level coarser than destBand).
        int leftD = destRoiLeft >> 1;
        int left0 = srcLl.AlignedRoi.Left;
        int left1 = srcHl.AlignedRoi.Left;
        int topD = destRoiTop >> 1;
        int top0 = srcLl.AlignedRoi.Top;
        int top1 = srcLh.AlignedRoi.Top;

        int srcOffsetX0 = 0, srcOffsetX1 = 0;
        int srcOffsetY0 = 0, srcOffsetY1 = 0;

        if (leftD >= Math.Max(left0, left1))
        {
            srcOffsetX0 = leftD - left0;
            srcOffsetX1 = leftD - left1;
        }
        else if (left0 <= left1)
        {
            int dx = (left1 - leftD) << 1;
            destRoiLeft += dx;
            origin += dx;
            workingWidth -= dx;
            srcOffsetX0 = left1 - left0;
        }
        else
        {
            int dx = (left0 - leftD) << 1;
            destRoiLeft += dx;
            origin += dx;
            workingWidth -= dx;
            srcOffsetX1 = left0 - left1;
        }

        if (topD >= Math.Max(top0, top1))
        {
            srcOffsetY0 = topD - top0;
            srcOffsetY1 = topD - top1;
        }
        else if (top0 <= top1)
        {
            int dy = (top1 - topD) << 1;
            destRoiTop += dy;
            origin += dy * destWidth;
            workingHeight -= dy;
            srcOffsetY0 = top1 - top0;
        }
        else
        {
            int dy = (top0 - topD) << 1;
            destRoiTop += dy;
            origin += dy * destWidth;
            workingHeight -= dy;
            srcOffsetY1 = top0 - top1;
        }

        srcLl.InitBuffPos(srcOffsetX0, srcOffsetY0);
        srcHl.InitBuffPos(srcOffsetX1, srcOffsetY0);
        srcLh.InitBuffPos(srcOffsetX0, srcOffsetY1);
        srcHh.InitBuffPos(srcOffsetX1, srcOffsetY1);

        // From here on, workingWidth/workingHeight (not destWidth/destHeight) are the loop's actual
        // pixel span - exactly matching the native's own width/height (adjusted) vs. destWidth/
        // destHeight (fixed buffer stride/branch-condition) distinction. When ROI was never set,
        // workingWidth==destWidth and workingHeight==destHeight throughout (every adjustment above
        // is a no-op), so this is the same code path the non-ROI case always ran, not a branch.
        int row0, row1, row2, row3;

        if (destHeight >= PgfConstants.FilterSize)
        {
            row0 = origin; row1 = row0 + destWidth;
            SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row0, workingWidth), destBuffer.AsSpan(row1, workingWidth));
            for (int k = 0; k < workingWidth; k++)
            {
                destBuffer[row0 + k] = unchecked((int)(destBuffer[row0 + k] - ((destBuffer[row1 + k] + C1) >> 1))); // even
            }

            row2 = row1 + destWidth; row3 = row2 + destWidth;
            for (int i = 2; i < workingHeight - 1; i += 2)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row2, workingWidth), destBuffer.AsSpan(row3, workingWidth));
                InverseVerticalMiddle(destBuffer, row0, row1, row2, row3, workingWidth);

                InverseRow(destBuffer.AsSpan(row0, workingWidth));
                InverseRow(destBuffer.AsSpan(row1, workingWidth));
                row0 = row2; row1 = row3; row2 = row1 + destWidth; row3 = row2 + destWidth;
            }

            if ((workingHeight & 1) != 0)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row2, workingWidth), default);
                for (int k = 0; k < workingWidth; k++)
                {
                    destBuffer[row2 + k] = unchecked((int)(destBuffer[row2 + k] - ((destBuffer[row1 + k] + C1) >> 1))); // even
                    destBuffer[row1 + k] = unchecked((int)(destBuffer[row1 + k] + ((destBuffer[row0 + k] + destBuffer[row2 + k] + C1) >> 1))); // odd
                }

                InverseRow(destBuffer.AsSpan(row0, workingWidth));
                InverseRow(destBuffer.AsSpan(row1, workingWidth));
                InverseRow(destBuffer.AsSpan(row2, workingWidth));
            }
            else
            {
                for (int k = 0; k < workingWidth; k++)
                {
                    destBuffer[row1 + k] = unchecked((int)(destBuffer[row1 + k] + destBuffer[row0 + k]));
                }

                InverseRow(destBuffer.AsSpan(row0, workingWidth));
                InverseRow(destBuffer.AsSpan(row1, workingWidth));
            }
        }
        else
        {
            row0 = origin; row1 = row0 + destWidth;
            for (int k = 0; k < workingHeight; k += 2)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row0, workingWidth), destBuffer.AsSpan(row1, workingWidth));
                InverseRow(destBuffer.AsSpan(row0, workingWidth));
                InverseRow(destBuffer.AsSpan(row1, workingWidth));
                row0 += destWidth << 1;
                row1 += destWidth << 1;
            }

            if ((workingHeight & 1) != 0)
            {
                SubbandsToInterleaved(srcLevel, destBuffer.AsSpan(row0, workingWidth), default);
                InverseRow(destBuffer.AsSpan(row0, workingWidth));
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

    /// <summary>Inverse counterpart to <see cref="ForwardVerticalMiddle"/>. Vectorization is
    /// limited to independent column lanes and always falls through to scalar code for the tail or
    /// for runtimes without hardware vectors.</summary>
    private static void InverseVerticalMiddle(int[] dest, int row0, int row1, int row2, int row3, int width)
    {
        int k = 0;
        if (Vector.IsHardwareAccelerated && !ForceScalarVectorsForTesting)
        {
            int lanes = Vector<int>.Count;
            Vector<int> c1 = new(C1);
            Vector<int> c2 = new(C2);
            for (; k <= width - lanes; k += lanes)
            {
                Vector<int> previousEven = new(dest, row0 + k);
                Vector<int> odd = new(dest, row1 + k);
                Vector<int> even = new(dest, row2 + k);
                Vector<int> nextOdd = new(dest, row3 + k);

                even -= (odd + nextOdd + c2) >> 2;
                odd += (previousEven + even + c1) >> 1;

                even.CopyTo(dest, row2 + k);
                odd.CopyTo(dest, row1 + k);
            }
        }

        for (; k < width; k++)
        {
            dest[row2 + k] = unchecked((int)(dest[row2 + k] - ((dest[row1 + k] + dest[row3 + k] + C2) >> 2))); // even
            dest[row1 + k] = unchecked((int)(dest[row1 + k] + ((dest[row0 + k] + dest[row2 + k] + C1) >> 1))); // odd
        }
    }

    /// <summary>Direct port of <c>SubbandsToInterleaved</c> (WaveletTransform.cpp:445), including its
    /// ROI <c>storePos</c>/<c>IncBuffRow</c> branch - the read-side mirror of
    /// <see cref="InterleavedToSubbands"/>: recombines the four child subbands at
    /// <paramref name="srcLevel"/> back into one (or two) interleaved row(s).
    ///
    /// <c>storePos</c> matters whenever a subband's own <see cref="PgfSubband.BufferWidth"/> (its
    /// ROI-sized buffer's row stride) is wider than the row currently being reconstructed
    /// (<paramref name="loRow"/>/<paramref name="hiRow"/>'s half-length, <c>wquot</c>) - i.e. this
    /// particular destination row only consumes part of what the subband buffer actually holds per
    /// row, so <see cref="PgfSubband.ReadBuffer"/>'s plain sequential cursor would otherwise drift
    /// into the next row's data instead of skipping the unread remainder;
    /// <see cref="PgfSubband.IncBuffRow"/> corrects that by jumping to the start of the next buffer
    /// row instead of wherever <see cref="PgfSubband.ReadBuffer"/> happened to leave the cursor. When
    /// ROI was never set, <see cref="PgfSubband.BufferWidth"/> always equals the destination
    /// subband's own real (halved-per-level) width, making <c>storePos</c> false unconditionally -
    /// verified by construction, not by branching around it.</summary>
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
            bool storePos = wquot < ll.BufferWidth;
            int llPos = 0, hlPos = 0, lhPos = 0, hhPos = 0;
            if (storePos)
            {
                llPos = ll.GetBuffPos(); hlPos = hl.GetBuffPos(); lhPos = lh.GetBuffPos(); hhPos = hh.GetBuffPos();
            }

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

            if (storePos)
            {
                ll.IncBuffRow(llPos); hl.IncBuffRow(hlPos); lh.IncBuffRow(lhPos); hh.IncBuffRow(hhPos);
            }
        }
        else
        {
            bool storePos = wquot < ll.BufferWidth;
            int llPos = 0, hlPos = 0;
            if (storePos)
            {
                llPos = ll.GetBuffPos(); hlPos = hl.GetBuffPos();
            }

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

            if (storePos)
            {
                ll.IncBuffRow(llPos); hl.IncBuffRow(hlPos);
            }
        }
    }
}
