using System.Buffers.Binary;

namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CEncoder</c>'s macroblock-filling/writing orchestration (Encoder.cpp) - the
/// single-macroblock, non-ROI path only, mirroring <see cref="PgfDecoderCore"/>'s same two
/// simplifications for the same reasons (see <see cref="PgfEncodeMacroBlock"/>'s doc comment).
/// </summary>
internal sealed class PgfEncoderCore
{
    private readonly PgfByteWriter writer;
    private readonly PgfEncodeMacroBlock currentBlock = new();

    public PgfEncoderCore(PgfByteWriter writer)
    {
        this.writer = writer;
    }

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

    /// <summary>Direct port of <c>CEncoder::WriteMacroBlock</c> (Encoder.cpp:406), the non-ROI,
    /// non-big-endian branch: <c>&lt;wordLen&gt;(16 bits) data</c>. The block's real
    /// <see cref="PgfEncodeMacroBlock.Header"/> is never written to the stream here (mirrors the
    /// original's own <c>if (m_roi)</c> guard around those 2 bytes - <see cref="PgfDecoderCore"/>'s
    /// equally guarded read side), since nothing enables ROI encoding yet.</summary>
    private void WriteMacroBlock(PgfEncodeMacroBlock block)
    {
        ushort wordLen = (ushort)BitStream.NumberOfWords(block.CodePos);

        Span<byte> wordLenBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(wordLenBytes, wordLen);
        writer.Write(wordLenBytes);

        ReadOnlySpan<byte> codeBufferBytes =
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.CodeBuffer.AsSpan(0, wordLen));
        writer.Write(codeBufferBytes);

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
