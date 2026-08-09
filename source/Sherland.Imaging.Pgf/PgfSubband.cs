// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf;

/// <summary>
/// Direct port of the decode/encode-relevant parts of <c>CSubband</c> (Subband.h/Subband.cpp) - one
/// quadrant of one wavelet transform level's coefficients, plus the sequential read/write cursor
/// (<see cref="ReadBuffer"/>/<see cref="WriteBuffer"/>) <see cref="PgfWaveletTransform"/>'s row-based
/// lifting steps use, distinct from the indexed <see cref="GetData"/>/<see cref="SetData"/> access
/// <c>PlaceTile</c>/<c>ExtractTile</c> use via <see cref="PgfDecoderCore.Partition"/>/
/// <see cref="PgfEncoderCore.Partition"/>.
///
/// <c>CSubband::Dequantize</c> is deliberately not ported: grepping every call site found it's only
/// ever used from <c>CPGFImage::Reconstruct</c> (PGFimage.cpp:348), an encode-time "verify what I
/// just wrote" helper this codebase never calls (only <c>Open()</c>+<c>Read()</c> for decode,
/// <c>SetHeader()</c>+<c>ImportBitmap()</c>+<c>Write()</c> for encode) - genuinely dead code for
/// this port's scope, not an oversight.
///
/// ROI tile geometry (<see cref="NTiles"/>/<see cref="TilePosition"/>/<see cref="TileIndex"/>/
/// <see cref="AlignedRoi"/>) is ported as of <c>pgf-roi-support.md</c> Stage 2, but ROI-aware data
/// flow is not yet: <see cref="AllocMemory"/> still simplifies the real version's <c>oldSize &gt;=
/// newSize</c> reuse-check (which only matters once ROI can shrink/grow <c>m_size</c> after
/// <c>Initialize</c> via <see cref="BufferWidth"/>) to "allocate <see cref="Width"/>*<see cref="Height"/>
/// once if not already allocated" - <c>PlaceTile</c>/<c>ExtractTile</c> still only
/// port the non-ROI branches too. Stage 3 (decode) and Stage 4 (encode) wire the tile geometry this
/// stage adds into actual data placement/extraction.
/// </summary>
internal sealed class PgfSubband
{
    private readonly PgfWorkspace? workspace;
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Level { get; private set; }

    public PgfSubbandOrientation Orientation { get; private set; }

    private int size;
    private int[]? data;
    private int dataPos;

    public PgfSubband(PgfWorkspace? workspace = null) => this.workspace = workspace;

    /// <summary>Mirrors <c>CSubband::m_nTiles</c> - number of tiles in one dimension in this
    /// subband, set by <see cref="SetNTiles"/> before <see cref="TilePosition"/>/<see cref="TileIndex"/>
    /// are called (<c>pgf-roi-support.md</c> Stage 2 - tile geometry only, no decode/encode data flow
    /// yet).</summary>
    public int NTiles { get; private set; }

    /// <summary>Mirrors <c>CSubband::m_ROI</c> - the region of interest actually reconstructable at
    /// this subband's level, aligned to tile/wavelet-margin boundaries by
    /// <see cref="PgfWaveletTransform.SetROI"/> (never pixel-exact to the caller's requested
    /// rectangle - see this PRD's "Why this needs to be grounded" section on
    /// <c>GetAlignedROI</c>/<c>ComputeLevelROI</c>). Defaults to the full subband
    /// (<c>Initialize</c>'s own default, matching the native's <c>Initialize</c>) until a real
    /// <see cref="SetAlignedRoi"/> call narrows it.</summary>
    public PgfRoi AlignedRoi { get; private set; }

    /// <summary>Mirrors <c>CSubband::BufferWidth</c> (Subband.h:154) - the data buffer's row stride
    /// once ROI-aligned allocation is wired in (Stage 3/4); not yet consumed by
    /// <see cref="AllocMemory"/> at this stage (still full <see cref="Width"/>/<see cref="Height"/>
    /// allocation - see class doc comment).</summary>
    public int BufferWidth => AlignedRoi.Width;

    public void Initialize(int width, int height, int level, PgfSubbandOrientation orientation)
    {
        Width = width;
        Height = height;
        size = width * height;
        Level = level;
        Orientation = orientation;
        data = null;
        dataPos = 0;
        AlignedRoi = new PgfRoi(0, 0, width, height);
        NTiles = 0;
    }

    /// <summary>Mirrors <c>CSubband::SetNTiles</c> (Subband.h:158) - must be called before
    /// <see cref="TileIndex"/>/<see cref="TilePosition"/>, exactly like the native's own doc
    /// comment.</summary>
    public void SetNTiles(int nTiles) => NTiles = nTiles;

    /// <summary>Direct port of <c>CSubband::SetAlignedROI</c> (Subband.cpp:240) - stores
    /// <paramref name="roi"/>, clamped so it never exceeds this subband's real <see cref="Width"/>/
    /// <see cref="Height"/> (a tile-aligned ROI can legitimately overshoot at the bottom-right edge,
    /// same as the native's own clamp).</summary>
    public void SetAlignedRoi(PgfRoi roi)
    {
        int right = Math.Min(roi.Right, Width);
        int bottom = Math.Min(roi.Bottom, Height);
        AlignedRoi = new PgfRoi(roi.Left, roi.Top, right, bottom);
    }

    /// <summary>Direct port of <c>CSubband::TilePosition</c> (Subband.cpp:257) - computes the pixel
    /// position and size of tile (<paramref name="tileX"/>, <paramref name="tileY"/>) within this
    /// subband's full (un-ROI'd) <see cref="Width"/>/<see cref="Height"/>, via the same recursive
    /// binary halving the native uses (repeatedly bisecting <see cref="NTiles"/> down to 1, halving
    /// the covered pixel span at each step) - not a plain <c>width/nTiles</c> division, since that
    /// wouldn't reproduce the native's exact odd/even split (<c>(w+1)&gt;&gt;1</c> for the
    /// lower-index half, matching its own worked example comment: tile widths <c>8 7 8 7</c> for
    /// <c>width=30, nTiles=4</c>).</summary>
    public void TilePosition(int tileX, int tileY, out int xPos, out int yPos, out int w, out int h)
    {
        int nTiles = NTiles;
        int left = 0, right = nTiles;
        int top = 0, bottom = nTiles;

        xPos = 0;
        yPos = 0;
        w = Width;
        h = Height;

        while (nTiles > 1)
        {
            int m = left + ((right - left) >> 1);
            if (tileX >= m)
            {
                xPos += (w + 1) >> 1;
                w >>= 1;
                left = m;
            }
            else
            {
                w = (w + 1) >> 1;
                right = m;
            }

            m = top + ((bottom - top) >> 1);
            if (tileY >= m)
            {
                yPos += (h + 1) >> 1;
                h >>= 1;
                top = m;
            }
            else
            {
                h = (h + 1) >> 1;
                bottom = m;
            }

            nTiles >>= 1;
        }
    }

    /// <summary>Direct port of <c>CSubband::TileIndex</c> (Subband.cpp:309) - the inverse of
    /// <see cref="TilePosition"/>: given a pixel position, finds the tile index that
    /// bounds/contains it via binary search over <see cref="NTiles"/>, plus the extremal aligned
    /// pixel coordinate at that tile boundary. <paramref name="topLeft"/> selects which of the two
    /// symmetric search variants the native has (exclusive-upper-bound search for a rectangle's
    /// top-left corner vs. inclusive-upper-bound search for its bottom-right corner) - the two
    /// aren't the same search run twice with different inputs, they use different comparison
    /// operators (<c>xPos &lt; m</c> vs. <c>xPos &lt;= m</c>) and different starting tile indices
    /// (<c>0</c> vs. <c>1</c>), exactly mirrored here rather than unified into one path.</summary>
    public void TileIndex(bool topLeft, int xPos, int yPos, out int tileX, out int tileY, out int x, out int y)
    {
        int left = 0, right = Width;
        int top = 0, bottom = Height;
        int nTiles = NTiles;

        if (xPos > Width)
        {
            xPos = Width;
        }

        if (yPos > Height)
        {
            yPos = Height;
        }

        if (topLeft)
        {
            tileX = 0;
            while (nTiles > 1)
            {
                nTiles >>= 1;
                int m = left + ((right - left + 1) >> 1);
                if (xPos < m)
                {
                    right = m;
                }
                else
                {
                    tileX += nTiles;
                    left = m;
                }
            }

            x = left;

            nTiles = NTiles;
            tileY = 0;
            while (nTiles > 1)
            {
                nTiles >>= 1;
                int m = top + ((bottom - top + 1) >> 1);
                if (yPos < m)
                {
                    bottom = m;
                }
                else
                {
                    tileY += nTiles;
                    top = m;
                }
            }

            y = top;
        }
        else
        {
            tileX = 1;
            while (nTiles > 1)
            {
                nTiles >>= 1;
                int m = left + ((right - left + 1) >> 1);
                if (xPos <= m)
                {
                    right = m;
                }
                else
                {
                    tileX += nTiles;
                    left = m;
                }
            }

            x = right;

            nTiles = NTiles;
            tileY = 1;
            while (nTiles > 1)
            {
                nTiles >>= 1;
                int m = top + ((bottom - top + 1) >> 1);
                if (yPos <= m)
                {
                    bottom = m;
                }
                else
                {
                    tileY += nTiles;
                    top = m;
                }
            }

            y = bottom;
        }
    }

    /// <summary>Direct port of <c>CSubband::AllocMemory</c> (Subband.cpp:77), sized from
    /// <see cref="BufferWidth"/>*<see cref="AlignedRoi"/>.Height - identical to
    /// <see cref="Width"/>*<see cref="Height"/> whenever ROI was never set (the default
    /// <see cref="AlignedRoi"/> is the full subband), so this is a value-preserving generalization,
    /// not a behavior change for any non-ROI decode.
    ///
    /// Simplified from the original's resize-aware version (<c>oldSize &gt;= newSize</c> reuse
    /// check): this port maintains a one-ROI-per-session invariant (a fresh
    /// <see cref="PgfWaveletTransform"/>/session per distinct ROI request, resolving this PRD's own
    /// "Open questions" - re-reading with a different ROI means opening a new session, not reusing
    /// this one - matching the native's own documented <c>ResetStreamPos</c> constraint rather than
    /// inventing a more flexible model), so <see cref="PgfWaveletTransform.SetROI"/> - if called at
    /// all - always happens before the first <see cref="AllocMemory"/> call for any given subband;
    /// this method never needs to shrink/grow an already-allocated buffer.</summary>
    public bool AllocMemory()
    {
        if (data is not null)
        {
            return true;
        }

        int logicalLength = BufferWidth * AlignedRoi.Height;
        data = workspace is null ? new int[logicalLength] : workspace.RentInt32Backing(logicalLength);
        return true;
    }

    public void FreeMemory() => data = null;

    /// <summary>Releases this decode pass's view of its backing storage before the owning
    /// <see cref="PgfWorkspace"/> recycles it for a subsequent pass.</summary>
    public void ResetForDecode()
    {
        data = null;
        dataPos = 0;
        NTiles = 0;
        AlignedRoi = new PgfRoi(0, 0, Width, Height);
    }

    /// <summary>Direct port of <c>CSubband::SetBuffer</c> (Subband.h:148) - used only for level-0's
    /// LL subband, which shares the channel's own raw pixel array rather than owning its own
    /// allocation (<c>CWaveletTransform::InitSubbands</c>'s <c>data</c> parameter).</summary>
    public void SetBuffer(int[] buffer) => data = buffer;

    public int[] GetBuffer()
    {
        System.Diagnostics.Debug.Assert(data is not null, "Subband buffer accessed before AllocMemory/SetBuffer.");
        return data!;
    }

    public void SetData(int pos, int value) => GetBuffer()[pos] = value;

    public int GetData(int pos) => GetBuffer()[pos];

    /// <summary>Direct port of <c>CSubband::InitBuffPos</c> (Subband.h:160) - generalized to take an
    /// optional ROI-relative <paramref name="left"/>/<paramref name="top"/> offset (default 0,0,
    /// matching every non-ROI call site exactly: <c>top*BufferWidth+left</c> with both 0 is always
    /// 0).</summary>
    public void InitBuffPos(int left = 0, int top = 0) => dataPos = (top * BufferWidth) + left;

    /// <summary>Mirrors <c>CSubband::GetBuffPos</c> (Subband.h:151) - current read/write cursor,
    /// saved/restored by <see cref="PgfWaveletTransform.SubbandsToInterleaved"/>'s ROI-aware
    /// row-position bookkeeping.</summary>
    public int GetBuffPos() => dataPos;

    /// <summary>Direct port of <c>CSubband::IncBuffRow</c> (Subband.h:141) - advances the cursor from
    /// a previously-saved <paramref name="pos"/> to the start of the next buffer row (<paramref name="pos"/>
    /// + <see cref="BufferWidth"/>), used when a subband's own buffer is narrower than the row of
    /// pixels currently being reconstructed (<see cref="PgfWaveletTransform.SubbandsToInterleaved"/>'s
    /// <c>storePos</c> case).</summary>
    public void IncBuffRow(int pos) => dataPos = pos + BufferWidth;

    public void WriteBuffer(int value) => GetBuffer()[dataPos++] = value;

    public int ReadBuffer() => GetBuffer()[dataPos++];

    /// <summary>Direct port of <c>CSubband::Quantize</c> (Subband.cpp:112) - scalar
    /// quantization-with-deadzone, called per-subband from <c>CWaveletTransform::ForwardTransform</c>
    /// (encode only; decode's equivalent adjustment lives in <c>PlaceTile</c>, mirroring
    /// <c>CSubband::PlaceTile</c>'s own inline adjustment rather than a separate <c>Dequantize</c>
    /// call - see this class's doc comment for why <c>Dequantize</c> itself isn't ported).</summary>
    public void Quantize(int quantParam)
    {
        int[] buffer = GetBuffer();

        if (Orientation == PgfSubbandOrientation.Ll)
        {
            quantParam -= Level + 1;
            if (quantParam > 0)
            {
                quantParam--;
                for (int i = 0; i < size; i++)
                {
                    buffer[i] = buffer[i] < 0
                        ? unchecked((int)-(((-buffer[i] >> quantParam) + 1) >> 1))
                        : unchecked((int)(((buffer[i] >> quantParam) + 1) >> 1));
                }
            }
        }
        else
        {
            quantParam -= Orientation == PgfSubbandOrientation.Hh ? Level - 1 : Level;
            if (quantParam > 0)
            {
                int threshold = ((1 << quantParam) * 7) / 5; // good value, per the original's own comment
                quantParam--;
                for (int i = 0; i < size; i++)
                {
                    if (buffer[i] < -threshold)
                    {
                        buffer[i] = unchecked((int)-(((-buffer[i] >> quantParam) + 1) >> 1));
                    }
                    else if (buffer[i] > threshold)
                    {
                        buffer[i] = unchecked((int)(((buffer[i] >> quantParam) + 1) >> 1));
                    }
                    else
                    {
                        buffer[i] = 0;
                    }
                }
            }
        }
    }

    /// <summary>Direct port of <c>CSubband::PlaceTile</c>'s non-ROI branch (Subband.cpp:203) -
    /// allocates this subband, computes the orientation/level-adjusted dequantization parameter
    /// (the decode-side mirror of <see cref="Quantize"/>'s own adjustment), and drives
    /// <see cref="PgfDecoderCore.Partition"/> to fill it from the entropy decoder.</summary>
    public void PlaceTile(PgfDecoderCore decoder, int quantParam)
    {
        if (!AllocMemory())
        {
            throw new PgfFormatException("Failed to allocate subband memory.");
        }

        quantParam -= Orientation switch
        {
            PgfSubbandOrientation.Ll => Level + 1,
            PgfSubbandOrientation.Hh => Level - 1,
            _ => Level,
        };
        if (quantParam < 0)
        {
            quantParam = 0;
        }

        decoder.Partition(GetBuffer(), quantParam, Width, Height, startPos: 0, pitch: Width);
    }

    /// <summary>Direct port of <c>CSubband::PlaceTile</c>'s ROI (<c>tile=true</c>) branch
    /// (Subband.cpp:217-225) - places tile (<paramref name="tileX"/>, <paramref name="tileY"/>) only,
    /// at its position within this subband's own ROI-sized buffer (<paramref name="tileX"/>/
    /// <paramref name="tileY"/>'s pixel position from <see cref="TilePosition"/>, offset by
    /// <see cref="AlignedRoi"/>'s own top-left so the buffer is addressed relative to itself, not the
    /// full un-cropped subband - <see cref="AllocMemory"/>'s doc comment for why this buffer can be
    /// smaller than <see cref="Width"/>*<see cref="Height"/>).</summary>
    public void PlaceTile(PgfDecoderCore decoder, int quantParam, bool tile, int tileX, int tileY)
    {
        if (!tile)
        {
            PlaceTile(decoder, quantParam);
            return;
        }

        if (!AllocMemory())
        {
            throw new PgfFormatException("Failed to allocate subband memory.");
        }

        quantParam -= Orientation switch
        {
            PgfSubbandOrientation.Ll => Level + 1,
            PgfSubbandOrientation.Hh => Level - 1,
            _ => Level,
        };
        if (quantParam < 0)
        {
            quantParam = 0;
        }

        TilePosition(tileX, tileY, out int xPos, out int yPos, out int w, out int h);

        System.Diagnostics.Debug.Assert(xPos >= AlignedRoi.Left && yPos >= AlignedRoi.Top, "Tile position outside aligned ROI.");

        int startPos = (xPos - AlignedRoi.Left) + ((yPos - AlignedRoi.Top) * BufferWidth);
        decoder.Partition(GetBuffer(), quantParam, w, h, startPos, pitch: BufferWidth);
    }

    /// <summary>Direct port of <c>CSubband::ExtractTile</c>'s non-ROI branch (Subband.cpp:177) -
    /// drives <see cref="PgfEncoderCore.Partition"/> to feed this subband's (already-quantized, via
    /// <see cref="Quantize"/>) coefficients into the entropy encoder.</summary>
    public void ExtractTile(PgfEncoderCore encoder)
    {
        encoder.Partition(GetBuffer(), Width, Height, startPos: 0, pitch: Width);
    }

    /// <summary>Direct port of <c>CSubband::ExtractTile</c>'s ROI (<c>tile=true</c>) branch
    /// (Subband.cpp:179-185) - extracts tile (<paramref name="tileX"/>, <paramref name="tileY"/>)
    /// only, from its position within this subband's full (un-cropped) buffer - unlike
    /// <see cref="PlaceTile(PgfDecoderCore,int,bool,int,int)"/>'s decode-side counterpart, the
    /// encoder always has the whole subband in memory (no ROI-sized allocation on the encode side -
    /// <c>pgf-roi-support.md</c> Goal 2's own framing: "the whole image is always encoded, just
    /// tile-structured"), so this addresses <see cref="Width"/> directly, not
    /// <see cref="BufferWidth"/>.</summary>
    public void ExtractTile(PgfEncoderCore encoder, bool tile, int tileX, int tileY)
    {
        if (!tile)
        {
            ExtractTile(encoder);
            return;
        }

        TilePosition(tileX, tileY, out int xPos, out int yPos, out int w, out int h);
        int startPos = xPos + (yPos * Width);
        encoder.Partition(GetBuffer(), w, h, startPos, pitch: Width);
    }
}
