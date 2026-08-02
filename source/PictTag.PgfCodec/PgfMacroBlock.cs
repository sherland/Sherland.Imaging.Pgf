namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CDecoder::CMacroBlock</c> (Decoder.h/Decoder.cpp) - the entropy-coded decoding
/// unit. A macro block holds one fixed-size (<see cref="PgfConstants.BufferSize"/>) chunk of encoded
/// bitstream (<see cref="CodeBuffer"/>) and decodes it into a flat array of wavelet coefficients
/// (<see cref="Value"/>) via the Malvar "Fast Progressive Wavelet Coder" bitplane/significance-map
/// scheme (comment at Decoder.h:90) - not arithmetic coding.
///
/// <b>ROI is deliberately not ported</b> (managed-pgf-codec.md's "ROI is always compiled in" trap):
/// this port's encoder never sets the <see cref="PgfVersionFlags.PGFROI"/> bit, so every real file
/// this codebase produces or consumes has <c>m_roi == false</c> in the original, which means the
/// native <c>ROIBlockHeader</c> is *never actually read from the stream* - it stays at its
/// constructor default (<c>ROIBlockHeader(BufferSize)</c>, i.e. <c>bufferSize = BufferSize</c>,
/// <c>tileEnd = false</c>) for every macroblock. This port hard-codes that same default instead of
/// modeling the 15-bit/1-bit <c>ROIBlockHeader</c> bitfield at all - a real simplification the real
/// code's own dead branches justify, not an assumption.
///
/// <b>Also not ported</b>: the OpenMP multi-macroblock-array path (<c>CDecoder::m_macroBlocks</c>) -
/// this build always compiles with <c>LIBPGF_DISABLE_OPENMP</c> (CMakeLists.txt), so
/// <c>CDecoder::DecodeBuffer</c>'s real, only-ever-exercised branch is the single-macroblock
/// sequential one; this port mirrors only that branch (see <see cref="PgfDecoderCore"/>).
/// </summary>
internal sealed class PgfMacroBlock
{
    /// <summary>Decoded output coefficients, index <c>[0, BufferSize)</c>. <c>DataT</c> in the
    /// original - <see cref="short"/> in this build (no <c>__PGF32SUPPORT__</c>).</summary>
    public readonly short[] Value = new short[PgfConstants.BufferSize];

    /// <summary>Encoded input bitstream, one macroblock's worth, in 32-bit words.</summary>
    public readonly uint[] CodeBuffer = new uint[PgfConstants.BufferSize];

    /// <summary>Current read position into <see cref="Value"/> for <c>DequantizeValue</c>
    /// consumption (<see cref="PgfDecoderCore"/>).</summary>
    public uint ValuePos;

    // Mirrors the real ROIBlockHeader.bufferSize field's role exactly, including its starting value:
    // the original's CMacroBlock constructor deliberately initializes m_header to
    // ROIBlockHeader(0) - not BufferSize - specifically "to make sure IsCompletelyRead() returns
    // true for an empty macro block" (Decoder.h's own constructor comment). A freshly-constructed
    // block has decoded nothing yet, so it must report "completely read" immediately, forcing the
    // first DequantizeValue call to decode a real block before reading Value[0] - getting this
    // wrong (as this port initially did, hardcoding BufferSize from the start) means the very
    // first read silently pulls from an undecoded, all-zero Value array instead of ever calling
    // BitplaneDecode at all. Sizing is fixed at BufferSize once a real decode happens (ROI is not
    // ported - see class doc comment - so every decoded block's true bufferSize is always the
    // full BufferSize, never a smaller ROI-tile size).
    private uint bufferSizeInUse;

    public bool IsCompletelyRead => ValuePos >= bufferSizeInUse;

    /// <summary>Called by <see cref="PgfDecoderCore"/>'s <c>ReadMacroBlock</c> at the same point the
    /// original sets <c>block-&gt;m_header = h</c> (Decoder.cpp:574) - marks this block as holding a
    /// real, freshly-read <see cref="CodeBuffer"/> ready for <see cref="BitplaneDecode"/>.</summary>
    public void MarkReadyToDecode() => bufferSizeInUse = PgfConstants.BufferSize;

    /// <summary>Significance flag vector (Malvar's paper) - true once a coefficient position has
    /// been found significant in an earlier (higher) bitplane, so later bitplanes only need to read
    /// its refinement bit, not search for new significance. Reset once per macroblock (not once per
    /// bitplane - it accumulates across the whole <see cref="BitplaneDecode"/> call), with a
    /// sentinel at <c>[BufferSizeInUse]</c> enabling the original's search-with-sentinel pattern
    /// without per-iteration bounds checks.</summary>
    private readonly bool[] sigFlagVector = new bool[PgfConstants.BufferSize + 1];

    /// <summary>Direct port of <c>CMacroBlock::BitplaneDecode</c> (Decoder.cpp:660). Decodes
    /// <see cref="CodeBuffer"/> into <see cref="Value"/>, bitplane by bitplane from the most to the
    /// least significant, each plane RL-coded or not depending on what the header bits say - see the
    /// original's own coding-scheme comment (Decoder.cpp:651-659) for the exact bitstream grammar.</summary>
    public void BitplaneDecode()
    {
        uint bufferSize = bufferSizeInUse;

        Array.Clear(sigFlagVector, 0, (int)bufferSize);
        sigFlagVector[bufferSize] = true; // sentinel

        Array.Clear(Value);

        uint nPlanes = BitStream.GetValueBlock(CodeBuffer, 0, PgfConstants.MaxBitPlanesLog);
        uint codePos = PgfConstants.MaxBitPlanesLog;

        if (nPlanes == 0)
        {
            nPlanes = PgfConstants.MaxBitPlanes + 1;
        }

        // planeMask deliberately allowed to overflow short's positive range when nPlanes=16
        // (1 << 15 = 32768, which as a signed 16-bit value truncates to -32768/0x8000) - matching
        // the original's own implementation-defined-but-universally-two's-complement C++ behavior
        // (DataT planeMask = 1 << (nPlanes-1)). SetBitAtPos/SetSign only ever treat this as a raw
        // bit pattern (OR/subtract), never as a signed magnitude, so the wraparound is harmless and
        // must be reproduced exactly, not "fixed."
        short planeMask = unchecked((short)(1 << (int)(nPlanes - 1)));

        for (int plane = (int)nPlanes - 1; plane >= 0; plane--)
        {
            uint sigLen;

            if (BitStream.GetBit(CodeBuffer, codePos))
            {
                // <1><codeLen><codedSigAndSignBits>_<refBits> - RL coding of sigBits+signBits together
                codePos++;
                uint codeLen = BitStream.GetValueBlock(CodeBuffer, codePos, PgfConstants.RLblockSizeLen);
                uint sigPos = codePos + PgfConstants.RLblockSizeLen;
                codePos = BitStream.AlignWordPos(sigPos + codeLen);

                sigLen = ComposeBitplaneRld(bufferSize, planeMask, sigPos, CodeBuffer.AsSpan((int)(codePos >> BitStream.WordWidthLog)));
            }
            else
            {
                // <0><sigLen> - sigBits and signBits (or their RL codes) follow separately
                codePos++;
                sigLen = BitStream.GetValueBlock(CodeBuffer, codePos, PgfConstants.RLblockSizeLen);
                codePos += PgfConstants.RLblockSizeLen;

                if (BitStream.GetBit(CodeBuffer, codePos))
                {
                    // <1><codeLen><codedSignBits>_<sigBits>_<refBits> - RL coding just for signBits
                    codePos++;
                    uint codeLen = BitStream.GetValueBlock(CodeBuffer, codePos, PgfConstants.RLblockSizeLen);
                    uint signPos = codePos + PgfConstants.RLblockSizeLen;
                    uint sigPos = BitStream.AlignWordPos(signPos + codeLen);
                    codePos = BitStream.AlignWordPos(sigPos + sigLen);

                    sigLen = ComposeBitplaneRld(
                        bufferSize, planeMask,
                        CodeBuffer.AsSpan((int)(sigPos >> BitStream.WordWidthLog)),
                        CodeBuffer.AsSpan((int)(codePos >> BitStream.WordWidthLog)),
                        signPos);
                }
                else
                {
                    // <0><signLen>_<signBits>_<sigBits>_<refBits> - no RL coding used at all
                    codePos++;
                    uint signLen = BitStream.GetValueBlock(CodeBuffer, codePos, PgfConstants.RLblockSizeLen);
                    uint signPos = BitStream.AlignWordPos(codePos + PgfConstants.RLblockSizeLen);
                    uint sigPos = BitStream.AlignWordPos(signPos + signLen);
                    codePos = BitStream.AlignWordPos(sigPos + sigLen);

                    sigLen = ComposeBitplane(
                        bufferSize, planeMask,
                        CodeBuffer.AsSpan((int)(sigPos >> BitStream.WordWidthLog)),
                        CodeBuffer.AsSpan((int)(codePos >> BitStream.WordWidthLog)),
                        CodeBuffer.AsSpan((int)(signPos >> BitStream.WordWidthLog)));
                }
            }

            codePos = BitStream.AlignWordPos(codePos + bufferSize - sigLen);
            planeMask = unchecked((short)(planeMask >> 1));
        }

        ValuePos = 0;
    }

    /// <summary>Reconstructs one bitplane from separately-stored significant/refinement/sign
    /// bitsets (no RLE at all) - direct port of the non-RLE <c>ComposeBitplane</c>
    /// (Decoder.cpp:773). Returns the bit-length of <paramref name="sigBits"/> actually consumed.</summary>
    private uint ComposeBitplane(uint bufferSize, short planeMask, ReadOnlySpan<uint> sigBits, ReadOnlySpan<uint> refBits, ReadOnlySpan<uint> signBits)
    {
        uint valPos = 0, signPos = 0, refPos = 0, sigPos = 0;

        while (valPos < bufferSize)
        {
            uint sigEnd = valPos;
            while (!sigFlagVector[sigEnd])
            {
                sigEnd++;
            }

            sigEnd -= valPos;
            sigEnd += sigPos;

            while (sigPos < sigEnd)
            {
                uint zerocnt = BitStream.SeekBitRange(sigBits, sigPos, sigEnd - sigPos);
                sigPos += zerocnt;
                valPos += zerocnt;
                if (sigPos < sigEnd)
                {
                    SetBitAtPos(valPos, planeMask);
                    SetSign(valPos, BitStream.GetBit(signBits, signPos++));
                    sigFlagVector[valPos++] = true;
                    sigPos++;
                }
            }

            if (valPos < bufferSize)
            {
                if (BitStream.GetBit(refBits, refPos))
                {
                    SetBitAtPos(valPos, planeMask);
                }

                refPos++;
                valPos++;
            }
        }

        return sigPos;
    }

    /// <summary>Reconstructs one bitplane where significant bits AND sign bits are jointly
    /// run-length-encoded together (the "Sig1" grammar branch) - direct port of the
    /// <c>codePos</c>-taking <c>ComposeBitplaneRLD</c> overload (Decoder.cpp:834). RLE scheme: a run
    /// of <c>2^k</c> zeros is coded as a single 0 bit; a run of <c>count</c> zeros followed by a 1 is
    /// coded <c>1&lt;count&gt;x</c> where x is the sign bit of the terminating significant
    /// coefficient. <paramref name="codePos"/> is a *bit* position into <see cref="CodeBuffer"/>
    /// (unlike the other overload, which takes pre-sliced word spans) because sig/sign bits and the
    /// adaptive counter share the same bit cursor here.</summary>
    private uint ComposeBitplaneRld(uint bufferSize, short planeMask, uint codePos, ReadOnlySpan<uint> refBits)
    {
        uint valPos = 0, refPos = 0;
        uint sigPos = 0;
        uint k = 3;
        uint runlen = 1u << (int)k;
        uint count = 0, rest = 0;
        bool set1 = false;

        while (valPos < bufferSize)
        {
            uint sigEnd = valPos;
            while (!sigFlagVector[sigEnd])
            {
                sigEnd++;
            }

            sigEnd -= valPos;
            sigEnd += sigPos;

            while (sigPos < sigEnd)
            {
                if (rest != 0 || set1)
                {
                    sigPos += rest;
                    valPos += rest;
                    rest = 0;
                }
                else
                {
                    if (BitStream.GetBit(CodeBuffer, codePos++))
                    {
                        if (k > 0)
                        {
                            count = BitStream.GetValueBlock(CodeBuffer, codePos, k);
                            codePos += k;
                            if (count > 0)
                            {
                                sigPos += count;
                                valPos += count;
                            }

                            k--;
                            runlen >>= 1;
                        }

                        set1 = true;
                    }
                    else
                    {
                        sigPos += runlen;
                        valPos += runlen;

                        if (k < 32)
                        {
                            k++;
                            runlen <<= 1;
                        }
                    }
                }

                if (sigPos < sigEnd)
                {
                    if (set1)
                    {
                        set1 = false;
                        SetBitAtPos(valPos, planeMask);
                        SetSign(valPos, BitStream.GetBit(CodeBuffer, codePos++));
                        sigFlagVector[valPos++] = true;
                        sigPos++;
                    }
                }
                else
                {
                    rest = sigPos - sigEnd;
                    sigPos = sigEnd;
                    valPos -= rest;
                }
            }

            if (valPos < bufferSize)
            {
                if (BitStream.GetBit(refBits, refPos))
                {
                    SetBitAtPos(valPos, planeMask);
                }

                refPos++;
                valPos++;
            }
        }

        return sigPos;
    }

    /// <summary>Reconstructs one bitplane where significant bits are stored plainly but sign bits
    /// are run-length-encoded separately (the "Sign1" grammar branch) - direct port of the
    /// <c>signPos</c>-taking <c>ComposeBitplaneRLD</c> overload (Decoder.cpp:937). RLE scheme (the
    /// *complement* of the sibling overload's): a run of <c>2^k</c> positive signs is coded as a
    /// single 1 bit; a run of <c>count</c> positive signs followed by a negative is coded
    /// <c>0&lt;count&gt;</c>.</summary>
    private uint ComposeBitplaneRld(uint bufferSize, short planeMask, ReadOnlySpan<uint> sigBits, ReadOnlySpan<uint> refBits, uint signPos)
    {
        uint valPos = 0, refPos = 0;
        uint sigPos = 0;
        uint count = 0;
        uint k = 0;
        uint runlen = 1u << (int)k;
        bool signBit = false;
        bool zeroAfterRun = false;

        while (valPos < bufferSize)
        {
            uint sigEnd = valPos;
            while (!sigFlagVector[sigEnd])
            {
                sigEnd++;
            }

            sigEnd -= valPos;
            sigEnd += sigPos;

            while (sigPos < sigEnd)
            {
                uint zerocnt = BitStream.SeekBitRange(sigBits, sigPos, sigEnd - sigPos);
                sigPos += zerocnt;
                valPos += zerocnt;
                if (sigPos < sigEnd)
                {
                    SetBitAtPos(valPos, planeMask);

                    if (count == 0)
                    {
                        if (zeroAfterRun)
                        {
                            signBit = false;
                            zeroAfterRun = false;
                        }
                        else if (BitStream.GetBit(CodeBuffer, signPos++))
                        {
                            count = runlen - 1;
                            signBit = true;

                            if (k < 32)
                            {
                                k++;
                                runlen <<= 1;
                            }
                        }
                        else
                        {
                            if (k > 0)
                            {
                                count = BitStream.GetValueBlock(CodeBuffer, signPos, k);
                                signPos += k;
                                k--;
                                runlen >>= 1;
                            }

                            if (count > 0)
                            {
                                count--;
                                signBit = true;
                                zeroAfterRun = true;
                            }
                            else
                            {
                                signBit = false;
                            }
                        }
                    }
                    else
                    {
                        count--;
                    }

                    SetSign(valPos, signBit);
                    sigFlagVector[valPos++] = true;
                    sigPos++;
                }
            }

            if (valPos < bufferSize)
            {
                if (BitStream.GetBit(refBits, refPos))
                {
                    SetBitAtPos(valPos, planeMask);
                }

                refPos++;
                valPos++;
            }
        }

        return sigPos;
    }

    /// <summary>Direct port of <c>SetBitAtPos</c> (Decoder.h:87) - accumulates a magnitude bit into
    /// <see cref="Value"/>[pos] while preserving whatever sign was set on the first significant bit
    /// for that position (OR for non-negative accumulation, subtract for negative - since a negative
    /// short's bit pattern isn't a plain sign-magnitude representation, "subtract to add magnitude"
    /// is the correct operation, not OR).</summary>
    private void SetBitAtPos(uint pos, short planeMask)
    {
        short current = Value[pos];
        Value[pos] = current >= 0
            ? unchecked((short)(current | planeMask))
            : unchecked((short)(current - planeMask));
    }

    /// <summary>Direct port of <c>SetSign</c> (Decoder.h:88) - negates <see cref="Value"/>[pos] if
    /// <paramref name="sign"/> is true (the encoded sign bit convention: 1 = negative), called
    /// exactly once per position, on its first significant bitplane.</summary>
    private void SetSign(uint pos, bool sign)
    {
        if (sign)
        {
            Value[pos] = unchecked((short)-Value[pos]);
        }
    }
}
