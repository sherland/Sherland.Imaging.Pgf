using System.Buffers.Binary;

namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CDecoder</c>'s macroblock/dequantization orchestration (Decoder.cpp) - the
/// single-macroblock-array, non-ROI path only (see <see cref="PgfMacroBlock"/>'s doc comment for
/// why those two simplifications are faithful to this build's real, only-ever-exercised behavior,
/// not assumptions).
/// </summary>
internal sealed class PgfDecoderCore
{
    private readonly PgfMemoryReader reader;
    private readonly PgfMacroBlock currentBlock = new();

    /// <summary>Mirrors <c>CDecoder::m_macroBlocksAvailable</c> for the single-macroblock case: 0 or
    /// 1 - the block has either not been decoded yet (0) or has been decoded and may still have
    /// unread values (1, tracked separately by <see cref="PgfMacroBlock.ValuePos"/>).</summary>
    private int macroBlocksAvailable;

    public PgfDecoderCore(PgfMemoryReader reader)
    {
        this.reader = reader;
    }

    /// <summary>Direct port of <c>CDecoder::DequantizeValue</c> (Decoder.cpp:472) - the single
    /// coefficient consumer every subband-filling loop (<see cref="Partition"/>) calls. Fetches the
    /// next macroblock if the current one is exhausted, then writes <c>value &lt;&lt; quantParam</c>
    /// into <paramref name="band"/> at <paramref name="bandPos"/>.</summary>
    public void DequantizeValue(Span<short> band, int bandPos, int quantParam)
    {
        if (currentBlock.IsCompletelyRead)
        {
            GetNextMacroBlock();
        }

        band[bandPos] = unchecked((short)(currentBlock.Value[currentBlock.ValuePos] << quantParam));
        currentBlock.ValuePos++;
    }

    /// <summary>Direct port of <c>CDecoder::GetNextMacroBlock</c> (Decoder.cpp:487), collapsed to the
    /// single-macroblock case: there is only ever one block, so "get the next one" always means
    /// "decode a fresh one in place."</summary>
    private void GetNextMacroBlock()
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

    /// <summary>Direct port of <c>CDecoder::ReadMacroBlock</c> (Decoder.cpp:545), the non-ROI branch
    /// only (<c>m_roi</c> is always false for this port - see <see cref="PgfMacroBlock"/>'s doc
    /// comment): <c>&lt;wordLen&gt;(16 bits) data</c>, no ROI block header ever actually read from
    /// the stream.</summary>
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
        block.MarkReadyToDecode();
    }

    /// <summary>Direct port of <c>CDecoder::Partition</c> (Decoder.cpp:276) - the LL/HH subband
    /// unpartitioning driver, tiling in <see cref="PgfConstants.LinBlockSize"/> squares. Takes a flat
    /// coefficient buffer rather than a <c>CSubband*</c>: <c>CSubband::SetData</c> is just
    /// <c>m_data[pos] = v</c> (Subband.h:102), so nothing subband-specific is needed at this layer -
    /// see managed-pgf-codec.md's Stage 5 notes.
    ///
    /// <c>DecodeInterleaved</c> (the pre-Version5 HL/LH decode path) is deliberately not ported:
    /// this port's encoder always sets <see cref="PgfVersionFlags.Version5"/>, and so does every
    /// modern real PGF file, so <c>CPGFImage::Read</c>'s <c>else</c> branch calling it is dead code
    /// for this port's real-world scope.</summary>
    public void Partition(Span<short> band, int quantParam, int width, int height, int startPos, int pitch)
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
}
