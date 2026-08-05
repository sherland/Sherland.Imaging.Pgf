using System.Buffers.Binary;

namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CEncoder</c>'s macroblock-filling/writing orchestration (Encoder.cpp) - the
/// single-macroblock path only, mirroring <see cref="PgfDecoderCore"/>'s same OpenMP-disabled
/// simplification (see <see cref="PgfEncodeMacroBlock"/>'s doc comment). ROI encoding
/// (<see cref="SetRoi"/>/<see cref="EncodeTileBuffer"/>) is real as of <c>pgf-roi-support.md</c>
/// Stage 4.
/// </summary>
internal sealed class PgfEncoderCore
{
    private readonly PgfByteWriter writer;
    private readonly PgfEncodeMacroBlock currentBlock;

    /// <summary>Mirrors <c>CEncoder::m_roi</c> - true once <see cref="SetRoi"/> has been called.
    /// Gates whether <see cref="WriteMacroBlock"/> actually writes the extra 2
    /// <see cref="PgfRoiBlockHeader"/> bytes to the wire - see <see cref="PgfDecoderCore"/>'s
    /// symmetric read-side flag.</summary>
    private bool roi;

    /// <summary>Mirrors <c>CEncoder::m_levelLength</c> (Encoder.h:216) - <see langword="null"/> until
    /// <see cref="BeginLevelLengthTracking"/> is called, gating <see cref="WriteMacroBlock"/>'s
    /// accounting block exactly like the native's own <c>if (m_levelLength)</c> check
    /// (Encoder.cpp:455). pgf-real-level-lengths.md Stage 1.</summary>
    private uint[]? levelLength;

    /// <summary>Mirrors <c>CEncoder::m_currLevelIndex</c> (Encoder.h:226) - which
    /// <see cref="levelLength"/> entry the byte count of the *next* flushed macroblock gets credited
    /// to.</summary>
    private int currLevelIndex;

    /// <summary>Mirrors <c>CEncoder::m_bufferStartPos</c> (Encoder.h:218) - the stream position the
    /// currently-buffered macroblock's encoded bytes started at, set by <see cref="SetBufferStartPos"/>.</summary>
    private long bufferStartPos;

    public PgfEncoderCore(PgfByteWriter writer, PgfWorkspace? workspace = null)
    {
        this.writer = writer;
        currentBlock = new PgfEncodeMacroBlock(workspace);
    }

    /// <summary>The real, accumulated per-level byte lengths once encoding has finished (index 0 =
    /// coarsest level, matching the on-wire/native <c>m_levelLength</c> array order) - <see
    /// langword="null"/> until <see cref="BeginLevelLengthTracking"/> has been called.
    /// <see cref="PgfImageEncoder"/> reads this after <see cref="Flush"/> to patch the real values
    /// into the level-length placeholder <see cref="PgfHeaderIO.Write"/> already reserved.
    /// pgf-real-level-lengths.md Goal 1.</summary>
    public uint[]? LevelLength => levelLength;

    /// <summary>Direct port of the tracking-setup half of <c>CEncoder::WriteLevelLength</c>
    /// (Encoder.cpp:177-196) - allocates a fresh, zeroed <see cref="levelLength"/> array and
    /// establishes <see cref="bufferStartPos"/> at the caller's current stream position. Unlike the
    /// native, this doesn't write the placeholder bytes itself - <see cref="PgfHeaderIO.Write"/>
    /// already reserves and zero-fills that exact byte range (pgf-real-level-lengths.md's own
    /// resolved Open Question 1: reuse, don't duplicate), so the caller must invoke this immediately
    /// after that write returns, while the stream position is still exactly past the placeholder.
    /// pgf-real-level-lengths.md Stage 1.</summary>
    public void BeginLevelLengthTracking(int levelCount)
    {
        levelLength = new uint[levelCount];
        currLevelIndex = 0;
        SetBufferStartPos();
    }

    /// <summary>Direct port of the <see cref="levelLength"/>-bookkeeping half of
    /// <c>CEncoder::SetEncodedLevel</c> (Encoder.h:164) - <c>m_forceWriting</c> is not modeled, for
    /// the same reason <c>pgf-roi-support.md</c>'s own Stage 4 found it dead in this port's
    /// always-single-macroblock build (see <see cref="PgfImageEncoder"/>'s ROI-branch call site).
    /// Sets the currently-buffered macroblock's <see cref="PgfEncodeMacroBlock.LastLevelIndex"/>;
    /// takes effect the next time that block is actually flushed (<see cref="WriteMacroBlock"/>), not
    /// immediately - a level boundary can fall mid-buffer, exactly matching the native's own deferred
    /// timing. <paramref name="currentLevel"/> is <see cref="PgfImageEncoder"/>'s own per-level loop
    /// variable (not yet decremented) at the point a level's data finishes being buffered; the native
    /// computes the equivalent index from its own post-decrement <c>m_currentLevel</c>
    /// (<c>m_nLevels - currentLevel - 1</c> there), which is algebraically identical to
    /// <c>levelLength.Length - currentLevel</c> here once the differing decrement timing is accounted
    /// for. A no-op before <see cref="BeginLevelLengthTracking"/> has been called.
    /// pgf-real-level-lengths.md Stage 1.</summary>
    public void AdvanceLevel(int currentLevel)
    {
        if (levelLength != null)
        {
            currentBlock.LastLevelIndex = levelLength.Length - currentLevel;
        }
    }

    /// <summary>Direct port of <c>CEncoder::SetBufferStartPos</c> (Encoder.h:194).</summary>
    private void SetBufferStartPos() => bufferStartPos = writer.Position;

    /// <summary>Direct port of <c>CEncoder::ComputeBufferLength</c> (Encoder.h:181).</summary>
    private long ComputeBufferLength() => writer.Position - bufferStartPos;

    /// <summary>Direct port of <c>CEncoder::SetROI</c> (Encoder.h:204) - enables writing the extra
    /// ROI block header bytes. Must be called before the first <see cref="WriteValue"/>/<see cref="EncodeTileBuffer"/>
    /// of an ROI-flagged encode.</summary>
    public void SetRoi() => roi = true;

    /// <summary>Direct port of <c>CEncoder::WriteValue</c> (Encoder.cpp:326) - stores one coefficient,
    /// triggering an encode-and-flush of the current macroblock first if it's already full.</summary>
    public void WriteValue(int value)
    {
        if (currentBlock.IsFull)
        {
            // Mirrors ROIBlockHeader(BufferSize, false) (Encoder.cpp:328) - a buffer-full flush is
            // never a tile boundary outside ROI mode (nothing calls SetRoi yet).
            EncodeBuffer(new PgfRoiBlockHeader((uint)PgfConstants.BufferSize, tileEnd: false));
        }

        currentBlock.WriteValue(value);
    }

    /// <summary>Direct port of <c>CEncoder::Flush</c> (Encoder.cpp:310) - pads any partially-filled
    /// final macroblock with zeros and encodes it. Must be called exactly once, after the last
    /// <see cref="WriteValue"/> for the whole image (mirrors the original's own call site in
    /// <c>CPGFImage::WriteImage</c>, after all levels have been written).</summary>
    public void Flush()
    {
        if (currentBlock.ValuePos > 0)
        {
            Array.Clear(currentBlock.Value, (int)currentBlock.ValuePos, PgfConstants.BufferSize - (int)currentBlock.ValuePos);
            currentBlock.ValuePos = PgfConstants.BufferSize;

            // Mirrors ROIBlockHeader(m_currentBlock->m_valuePos, true) (Encoder.cpp:316) - always
            // tileEnd:true (the last macroblock of the whole image, in the absence of any earlier
            // tile boundary), and valuePos is BufferSize here since it was just padded above.
            EncodeBuffer(new PgfRoiBlockHeader(currentBlock.ValuePos, tileEnd: true));
        }
    }

    /// <summary>Direct port of <c>CEncoder::EncodeTileBuffer</c> (Encoder.h:200) - flushes whatever
    /// is currently buffered (0 to <see cref="PgfConstants.BufferSize"/> values, not padded with
    /// zeros - unlike <see cref="Flush"/>) as the last macroblock of a tile
    /// (<see cref="PgfRoiBlockHeader.TileEnd"/> always true). Called once per tile, after that tile's
    /// worth of <see cref="WriteValue"/> calls across all three HL/LH/HH subbands - only valid once
    /// <see cref="SetRoi"/> has been called.</summary>
    public void EncodeTileBuffer()
    {
        System.Diagnostics.Debug.Assert(roi, "EncodeTileBuffer requires SetRoi() to have been called.");
        EncodeBuffer(new PgfRoiBlockHeader(currentBlock.ValuePos, tileEnd: true));
    }

    /// <summary>Direct port of <c>CEncoder::EncodeBuffer</c>'s single-macroblock branch
    /// (Encoder.cpp:341-353): stamps <paramref name="header"/> onto the block (mirrors
    /// <c>m_currentBlock-&gt;m_header = h;</c>, Encoder.cpp:348), encodes what's currently buffered
    /// using its real <see cref="PgfRoiBlockHeader.BufferSize"/>, writes it, resets for the next
    /// block.</summary>
    private void EncodeBuffer(PgfRoiBlockHeader header)
    {
        currentBlock.SetHeader(header);
        currentBlock.BitplaneEncode(header.BufferSize);
        WriteMacroBlock(currentBlock);
    }

    /// <summary>Direct port of <c>CEncoder::WriteMacroBlock</c> (Encoder.cpp:406), the non-big-endian
    /// branch: <c>&lt;wordLen&gt;(16 bits) [ROIBlockHeader](16 bits, only when <see cref="roi"/>)
    /// data</c>.</summary>
    private void WriteMacroBlock(PgfEncodeMacroBlock block)
    {
        ushort wordLen = (ushort)BitStream.NumberOfWords(block.CodePos);

        Span<byte> wordLenBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(wordLenBytes, wordLen);
        writer.Write(wordLenBytes);

        if (roi)
        {
            Span<byte> headerBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(headerBytes, block.Header.Value);
            writer.Write(headerBytes);
        }

        ReadOnlySpan<byte> codeBufferBytes =
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.CodeBuffer.AsSpan(0, wordLen));
        writer.Write(codeBufferBytes);

        // Direct port of Encoder.cpp:454-465's own "store levelLength" / "prepare for next buffer"
        // pair - credits this flush's on-disk byte count to whichever level index was in effect when
        // this block started, then advances to the level AdvanceLevel most recently set (deferred
        // effect - see PgfEncodeMacroBlock.LastLevelIndex's doc comment). SetBufferStartPos runs
        // unconditionally afterward either way, exactly matching the native's own unconditional call
        // (levelLength being null just means nothing downstream ever reads bufferStartPos again).
        // pgf-real-level-lengths.md Stage 1.
        if (levelLength != null)
        {
            levelLength[currLevelIndex] += (uint)ComputeBufferLength();
            currLevelIndex = block.LastLevelIndex + 1;
        }

        SetBufferStartPos();

        block.ValuePos = 0;
        block.MaxAbsValue = 0;
    }

    /// <summary>Direct port of <c>CEncoder::Partition</c> (Encoder.cpp:246) - the LL/HH subband
    /// tiling driver, the mirror image of <see cref="PgfDecoderCore.Partition"/> (identical tiling
    /// arithmetic, calling <see cref="WriteValue"/> instead of <c>DequantizeValue</c>). Takes a flat
    /// coefficient buffer for the same reason as the decode side - <c>CSubband::GetData</c> is just
    /// <c>return m_data[pos]</c> (Subband.h:113).</summary>
    public void Partition(ReadOnlySpan<int> band, int width, int height, int startPos, int pitch)
    {
        int wq = Math.DivRem(width, PgfConstants.LinBlockSize, out int wr);
        int hq = Math.DivRem(height, PgfConstants.LinBlockSize, out int hr);
        int ws = pitch - PgfConstants.LinBlockSize;
        int wRest = pitch - wr;
        int @base = startPos;

        for (int i = 0; i < hq; i++)
        {
            int base2 = @base;
            for (int j = 0; j < wq; j++)
            {
                int pos = base2;
                for (int y = 0; y < PgfConstants.LinBlockSize; y++)
                {
                    for (int x = 0; x < PgfConstants.LinBlockSize; x++)
                    {
                        WriteValue(band[pos]);
                        pos++;
                    }

                    pos += ws;
                }

                base2 += PgfConstants.LinBlockSize;
            }

            int restPos = base2;
            for (int y = 0; y < PgfConstants.LinBlockSize; y++)
            {
                for (int x = 0; x < wr; x++)
                {
                    WriteValue(band[restPos]);
                    restPos++;
                }

                restPos += wRest;
                @base += pitch;
            }
        }

        int base2Outer = @base;
        for (int j = 0; j < wq; j++)
        {
            int pos = base2Outer;
            for (int y = 0; y < hr; y++)
            {
                for (int x = 0; x < PgfConstants.LinBlockSize; x++)
                {
                    WriteValue(band[pos]);
                    pos++;
                }

                pos += ws;
            }

            base2Outer += PgfConstants.LinBlockSize;
        }

        int finalPos = base2Outer;
        for (int y = 0; y < hr; y++)
        {
            for (int x = 0; x < wr; x++)
            {
                WriteValue(band[finalPos]);
                finalPos++;
            }

            finalPos += wRest;
        }
    }
}
