using System.Buffers.Binary;

namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CDecoder</c>'s macroblock/dequantization orchestration (Decoder.cpp) - the
/// single-macroblock-array path only (see <see cref="PgfMacroBlock"/>'s doc comment for why that
/// simplification is faithful to this build's real, only-ever-exercised OpenMP behavior, not an
/// assumption). ROI decoding (<see cref="SetRoi"/>/<see cref="SkipTileBuffer"/>) is real as of
/// <c>pgf-roi-support.md</c> Stage 3.
/// </summary>
internal sealed class PgfDecoderCore
{
    private readonly PgfMemoryReader reader;
    private readonly PgfMacroBlock currentBlock;

    /// <summary>Mirrors <c>CDecoder::m_macroBlocksAvailable</c> for the single-macroblock case: 0 or
    /// 1 - the block has either not been decoded yet (0) or has been decoded and may still have
    /// unread values (1, tracked separately by <see cref="PgfMacroBlock.ValuePos"/>).</summary>
    private int macroBlocksAvailable;

    /// <summary>Mirrors <c>CDecoder::m_roi</c> - true once <see cref="SetRoi"/> has been called
    /// (never automatically, and never un-set - matches the native's own one-directional
    /// <c>SetROI()</c> setter, no corresponding clear). Gates whether <see cref="ReadMacroBlock"/>
    /// actually reads the extra 2 <see cref="PgfRoiBlockHeader"/> bytes off the wire (<see cref="PgfRoiBlockHeader"/>'s
    /// own doc comment) - false for every real file this app has ever produced/consumed before this
    /// PRD.</summary>
    private bool roi;

    public PgfDecoderCore(PgfMemoryReader reader, PgfWorkspace? workspace = null)
    {
        this.reader = reader;
        currentBlock = new PgfMacroBlock(workspace);
    }

    /// <summary>Rewinds the single-macroblock decoder to the first encoded level for a new pass on
    /// the same already-validated PGF stream.</summary>
    public void Reset(long dataPosition)
    {
        reader.SetPos(SeekOrigin.Begin, dataPosition);
        currentBlock.Reset();
        macroBlocksAvailable = 0;
        roi = false;
    }

    /// <summary>Direct port of <c>CDecoder::SetROI</c> (Decoder.h:192) - enables ROI-aware macroblock
    /// framing (the extra header bytes, plus makes <see cref="SkipTileBuffer"/> a valid call). Must
    /// be called before the first <see cref="ReadMacroBlock"/>/<see cref="GetNextMacroBlock"/> of an
    /// ROI-flagged stream (mirrors <c>CPGFImage::SetROI</c> calling <c>m_decoder-&gt;SetROI()</c>
    /// before any tile is read).</summary>
    public void SetRoi() => roi = true;

    /// <summary>Direct port of <c>CDecoder::DequantizeValue</c> (Decoder.cpp:472) - the single
    /// coefficient consumer every subband-filling loop (<see cref="Partition"/>) calls. Fetches the
    /// next macroblock if the current one is exhausted, then writes <c>value &lt;&lt; quantParam</c>
    /// into <paramref name="band"/> at <paramref name="bandPos"/>.</summary>
    public void DequantizeValue(Span<int> band, int bandPos, int quantParam)
    {
        if (currentBlock.IsCompletelyRead)
        {
            GetNextMacroBlock();
        }

        band[bandPos] = unchecked(currentBlock.Value[currentBlock.ValuePos] << quantParam);
        currentBlock.ValuePos++;
    }

    /// <summary>Direct port of <c>CDecoder::GetNextMacroBlock</c> (Decoder.cpp:487), collapsed to the
    /// single-macroblock case: there is only ever one block, so "get the next one" always means
    /// "decode a fresh one in place." Public (not just called lazily from <see cref="DequantizeValue"/>
    /// like every non-ROI call site) because ROI decode's own per-tile loop
    /// (<c>CPGFImage::Read(rect,...)</c>, PGFimage.cpp:530/537) calls it explicitly before each
    /// tile's first placement - ported faithfully even though, in this port's always-single-macroblock
    /// configuration, it is provably equivalent to the lazy fetch <see cref="DequantizeValue"/>'s
    /// first call within that tile would trigger anyway (every tile's encoded data exactly exhausts
    /// whatever macroblock(s) it occupies - <c>PgfEncodeMacroBlock</c>'s per-tile <c>EncodeTileBuffer</c>
    /// flush guarantees this): matching the real call sequence exactly is safer than relying on that
    /// reasoning never having an edge case this port hasn't considered.</summary>
    public void GetNextMacroBlock()
    {
        macroBlocksAvailable--;
        if (macroBlocksAvailable <= 0)
        {
            DecodeBuffer();
        }
    }

    /// <summary>Direct port of <c>CDecoder::DecodeBuffer</c>'s single-macroblock branch
    /// (Decoder.cpp:504-512) - the only branch this build's <c>LIBPGF_DISABLE_OPENMP</c> ever
    /// reaches.</summary>
    private void DecodeBuffer()
    {
        ReadMacroBlock(currentBlock);
        currentBlock.BitplaneDecode();
        macroBlocksAvailable = 1;
    }

    /// <summary>Direct port of <c>CDecoder::ReadMacroBlock</c> (Decoder.cpp:545): <c>&lt;wordLen&gt;
    /// (16 bits) [ROIBlockHeader](16 bits, only when <see cref="roi"/>) data</c>. Outside ROI mode
    /// (every real file this app has produced/consumed before this PRD), the header value is never
    /// actually read off the wire - it stays at the original's own default <c>ROIBlockHeader
    /// h(BufferSize)</c>, matching Stage 1's bookkeeping exactly.</summary>
    private void ReadMacroBlock(PgfMacroBlock block)
    {
        Span<byte> wordLenBytes = stackalloc byte[2];
        if (reader.Read(wordLenBytes) != 2)
        {
            throw new PgfFormatException("Truncated stream: missing macroblock word length.");
        }

        ushort wordLen = BinaryPrimitives.ReadUInt16LittleEndian(wordLenBytes);
        if (wordLen > PgfConstants.BufferSize)
        {
            throw new PgfFormatException($"Macroblock word length {wordLen} exceeds BufferSize.");
        }

        // Mirrors ROIBlockHeader h(BufferSize) (Decoder.cpp:548) - the default used whenever this
        // block isn't part of an ROI-flagged stream.
        var header = new PgfRoiBlockHeader((uint)PgfConstants.BufferSize, tileEnd: false);

        if (roi)
        {
            Span<byte> headerBytes = stackalloc byte[2];
            if (reader.Read(headerBytes) != 2)
            {
                throw new PgfFormatException("Truncated stream: missing ROI block header.");
            }

            header = new PgfRoiBlockHeader(BinaryPrimitives.ReadUInt16LittleEndian(headerBytes));
        }

        // A fresh decoder gets a zero-initialized CodeBuffer, and the bitplane grammar can inspect
        // padding words beyond this block's declared wire length. Clear before each partial read so
        // a reusable session has the same zero-padding semantics rather than seeing the preceding
        // macroblock's tail.
        Array.Clear(block.CodeBuffer);
        Span<byte> codeBufferBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.CodeBuffer.AsSpan());
        int byteCount = wordLen * 4;
        if (reader.Read(codeBufferBytes[..byteCount]) != byteCount)
        {
            throw new PgfFormatException("Truncated stream: missing macroblock data.");
        }

        // Real digiKam/PGF files are little-endian on disk (managed-pgf-codec.md's "Endianness"
        // trap) and this host is little-endian, so the raw bytes already have the correct in-memory
        // uint layout - no per-word byte-swap needed (matches the original's own __VAL no-op on a
        // little-endian host, PGF_USE_BIG_ENDIAN not defined here).

        // Matches the original setting block->m_header = h right here (Decoder.cpp:574) - see
        // PgfMacroBlock.MarkReadyToDecode's doc comment for why this specific timing matters.
        block.MarkReadyToDecode(header);
    }

    /// <summary>Direct port of <c>CDecoder::SkipTileBuffer</c> (Decoder.cpp:604), collapsed to the
    /// single-macroblock case (the native's own <c>m_macroBlocks[]</c> pre-decoded-lookahead branch
    /// is dead code here for the same OpenMP-disabled reason as everywhere else in this port - see
    /// class doc comment): reads and discards macroblocks directly off the stream (word length +
    /// ROI header + raw data, never decoded into <see cref="PgfMacroBlock"/> at all) until one with
    /// <see cref="PgfRoiBlockHeader.TileEnd"/> set is found, leaving the stream positioned at the
    /// start of the next tile's data. Only valid once <see cref="SetRoi"/> has been called (mirrors
    /// the native's own <c>ASSERT(m_roi)</c>).</summary>
    public void SkipTileBuffer()
    {
        System.Diagnostics.Debug.Assert(roi, "SkipTileBuffer requires SetRoi() to have been called.");

        macroBlocksAvailable = 0;

        PgfRoiBlockHeader header;
        do
        {
            Span<byte> wordLenBytes = stackalloc byte[2];
            if (reader.Read(wordLenBytes) != 2)
            {
                throw new PgfFormatException("Truncated stream: missing skipped-tile word length.");
            }

            ushort wordLen = BinaryPrimitives.ReadUInt16LittleEndian(wordLenBytes);
            if (wordLen > PgfConstants.BufferSize)
            {
                throw new PgfFormatException($"Skipped-tile word length {wordLen} exceeds BufferSize.");
            }

            Span<byte> headerBytes = stackalloc byte[2];
            if (reader.Read(headerBytes) != 2)
            {
                throw new PgfFormatException("Truncated stream: missing skipped-tile ROI block header.");
            }

            header = new PgfRoiBlockHeader(BinaryPrimitives.ReadUInt16LittleEndian(headerBytes));

            // skip data (mirrors m_stream->SetPos(FSFromCurrent, wordLen*WordBytes) - never read
            // into a buffer at all, unlike ReadMacroBlock's real decode path)
            reader.SetPos(SeekOrigin.Current, wordLen * 4);
        }
        while (!header.TileEnd);
    }

    /// <summary>Direct port of <c>CDecoder::Partition</c> (Decoder.cpp:276) - the LL/HH subband
    /// unpartitioning driver, tiling in <see cref="PgfConstants.LinBlockSize"/> squares. Takes a flat
    /// coefficient buffer rather than a <c>CSubband*</c>: <c>CSubband::SetData</c> is just
    /// <c>m_data[pos] = v</c> (Subband.h:102), so nothing subband-specific is needed at this layer -
    /// see managed-pgf-codec.md's Stage 5 notes.
    ///
    /// The pre-Version5 HL/LH path uses <see cref="DecodeInterleaved"/> instead; unlike this
    /// method's independent <see cref="PgfConstants.LinBlockSize"/> bands, it consumes a paired
    /// <see cref="PgfConstants.InterBlockSize"/> traversal from one shared macroblock stream.</summary>
    public void Partition(Span<int> band, int quantParam, int width, int height, int startPos, int pitch)
    {
        int wq = Math.DivRem(width, PgfConstants.LinBlockSize, out int wr);
        int hq = Math.DivRem(height, PgfConstants.LinBlockSize, out int hr);
        int ws = pitch - PgfConstants.LinBlockSize;
        int wRest = pitch - wr;
        int @base = startPos;

        // main height
        for (int i = 0; i < hq; i++)
        {
            // main width
            int base2 = @base;
            for (int j = 0; j < wq; j++)
            {
                int pos = base2;
                for (int y = 0; y < PgfConstants.LinBlockSize; y++)
                {
                    for (int x = 0; x < PgfConstants.LinBlockSize; x++)
                    {
                        DequantizeValue(band, pos, quantParam);
                        pos++;
                    }

                    pos += ws;
                }

                base2 += PgfConstants.LinBlockSize;
            }

            // rest of width
            int restPos = base2;
            for (int y = 0; y < PgfConstants.LinBlockSize; y++)
            {
                for (int x = 0; x < wr; x++)
                {
                    DequantizeValue(band, restPos, quantParam);
                    restPos++;
                }

                restPos += wRest;
                @base += pitch;
            }
        }

        // main width
        int base2Outer = @base;
        for (int j = 0; j < wq; j++)
        {
            // rest of height
            int pos = base2Outer;
            for (int y = 0; y < hr; y++)
            {
                for (int x = 0; x < PgfConstants.LinBlockSize; x++)
                {
                    DequantizeValue(band, pos, quantParam);
                    pos++;
                }

                pos += ws;
            }

            base2Outer += PgfConstants.LinBlockSize;
        }

        // rest of height
        int finalPos = base2Outer;
        for (int y = 0; y < hr; y++)
        {
            // rest of width
            for (int x = 0; x < wr; x++)
            {
                DequantizeValue(band, finalPos, quantParam);
                finalPos++;
            }

            finalPos += wRest;
        }
    }

    /// <summary>Direct port of <c>CDecoder::DecodeInterleaved</c> (Decoder.cpp:343-454), the
    /// pre-Version5 HL/LH entropy layout. Native pairs each 4x4 HL block with the corresponding LH
    /// block in one coefficient stream, then handles the genuinely possible one-row/one-column
    /// subband size mismatch explicitly. This is intentionally not reduced to calls to
    /// <see cref="Partition"/>: doing so would consume all HL values before LH and silently desync
    /// the historical bitstream (pgf-bitmap-legacy-packed.md Stage 3).</summary>
    public void DecodeInterleaved(PgfSubband hlBand, PgfSubband lhBand, int level, int quantParam)
    {
        if (!hlBand.AllocMemory() || !lhBand.AllocMemory())
        {
            throw new PgfFormatException("Failed to allocate legacy interleaved subband memory.");
        }

        Span<int> hl = hlBand.GetBuffer();
        Span<int> lh = lhBand.GetBuffer();
        int lhHq = Math.DivRem(lhBand.Height, PgfConstants.InterBlockSize, out int lhHr);
        int hlWq = Math.DivRem(hlBand.Width, PgfConstants.InterBlockSize, out int hlWr);
        int hlws = hlBand.Width - PgfConstants.InterBlockSize;
        int hlwr = hlBand.Width - hlWr;
        int lhws = lhBand.Width - PgfConstants.InterBlockSize;
        int lhwr = lhBand.Width - hlWr;
        int hlBase = 0, lhBase = 0;

        quantParam -= level;
        if (quantParam < 0) quantParam = 0;

        for (int i = 0; i < lhHq; i++)
        {
            int hlBase2 = hlBase, lhBase2 = lhBase;
            for (int j = 0; j < hlWq; j++)
            {
                int hlPos = hlBase2, lhPos = lhBase2;
                for (int y = 0; y < PgfConstants.InterBlockSize; y++)
                {
                    for (int x = 0; x < PgfConstants.InterBlockSize; x++)
                    {
                        DequantizeValue(hl, hlPos++, quantParam);
                        DequantizeValue(lh, lhPos++, quantParam);
                    }
                    hlPos += hlws;
                    lhPos += lhws;
                }
                hlBase2 += PgfConstants.InterBlockSize;
                lhBase2 += PgfConstants.InterBlockSize;
            }
            int restHlPos = hlBase2, restLhPos = lhBase2;
            for (int y = 0; y < PgfConstants.InterBlockSize; y++)
            {
                for (int x = 0; x < hlWr; x++)
                {
                    DequantizeValue(hl, restHlPos++, quantParam);
                    DequantizeValue(lh, restLhPos++, quantParam);
                }
                if (lhBand.Width > hlBand.Width) DequantizeValue(lh, restLhPos, quantParam);
                restHlPos += hlwr;
                restLhPos += lhwr;
                hlBase += hlBand.Width;
                lhBase += lhBand.Width;
            }
        }

        int remainderHlBase = hlBase, remainderLhBase = lhBase;
        for (int j = 0; j < hlWq; j++)
        {
            int hlPos = remainderHlBase, lhPos = remainderLhBase;
            for (int y = 0; y < lhHr; y++)
            {
                for (int x = 0; x < PgfConstants.InterBlockSize; x++)
                {
                    DequantizeValue(hl, hlPos++, quantParam);
                    DequantizeValue(lh, lhPos++, quantParam);
                }
                hlPos += hlws;
                lhPos += lhws;
            }
            remainderHlBase += PgfConstants.InterBlockSize;
            remainderLhBase += PgfConstants.InterBlockSize;
        }
        int finalHlPos = remainderHlBase, finalLhPos = remainderLhBase;
        for (int y = 0; y < lhHr; y++)
        {
            for (int x = 0; x < hlWr; x++)
            {
                DequantizeValue(hl, finalHlPos++, quantParam);
                DequantizeValue(lh, finalLhPos++, quantParam);
            }
            if (lhBand.Width > hlBand.Width) DequantizeValue(lh, finalLhPos, quantParam);
            finalHlPos += hlwr;
            finalLhPos += lhwr;
            hlBase += hlBand.Width;
        }

        if (hlBand.Height > lhBand.Height)
        {
            for (int j = 0; j < hlBand.Width; j++) DequantizeValue(hl, hlBase + j, quantParam);
        }
    }

}
