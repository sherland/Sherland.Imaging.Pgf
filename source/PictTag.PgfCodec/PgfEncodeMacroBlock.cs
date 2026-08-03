namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>CEncoder::CMacroBlock</c> (Encoder.h/Encoder.cpp) - the entropy-coding mirror
/// of <see cref="PgfMacroBlock"/>. Collects raw wavelet coefficients into <see cref="Value"/> via
/// <see cref="WriteValue"/>, then <see cref="BitplaneEncode"/> compresses them into
/// <see cref="CodeBuffer"/> using the same bitplane/significance-map scheme in reverse.
///
/// Same OpenMP simplification as <see cref="PgfMacroBlock"/>, for the same reason (this build
/// always compiles with <c>LIBPGF_DISABLE_OPENMP</c>): single-macroblock orchestration only (see
/// <see cref="PgfEncoderCore"/>). Unlike <see cref="PgfMacroBlock"/>'s note, <see cref="Header"/>
/// *is* modeled here (<c>pgf-roi-support.md</c> Goal 3) - see <see cref="PgfRoiBlockHeader"/>'s own
/// doc comment for when the extra 2 header bytes are actually written to the stream.
/// </summary>
internal sealed class PgfEncodeMacroBlock
{
    /// <summary>Mirrors <c>CMacroBlock::m_header</c> - set by <see cref="PgfEncoderCore"/>'s
    /// <c>EncodeBuffer</c> immediately before <see cref="BitplaneEncode"/>, exactly like the
    /// original's <c>m_currentBlock-&gt;m_header = h;</c> (Encoder.cpp:348).</summary>
    public PgfRoiBlockHeader Header { get; private set; }

    public void SetHeader(PgfRoiBlockHeader header) => Header = header;
    /// <summary>Raw (post-quantization, pre-entropy-coding) coefficients collected via
    /// <see cref="WriteValue"/>, index <c>[0, BufferSize)</c>. <c>DataT</c> in the original -
    /// <see cref="int"/> in this build (real <c>__PGF32SUPPORT__</c> build, see
    /// <see cref="PgfConstants"/>'s doc comment).</summary>
    public readonly int[] Value = new int[PgfConstants.BufferSize];

    /// <summary>Entropy-coded output bitstream, one macroblock's worth, in 32-bit words.</summary>
    public readonly uint[] CodeBuffer = new uint[PgfConstants.BufferSize];

    public uint ValuePos;

    /// <summary>Direct port of <c>CMacroBlock::m_lastLevelIndex</c> (Encoder.h:92) - the level-length
    /// directory index this block's byte count should be credited to once flushed, set by
    /// <see cref="PgfEncoderCore.AdvanceLevel"/> at a level boundary (mirrors
    /// <c>CEncoder::SetEncodedLevel</c>, Encoder.h:164) but only taking effect on this block's *next*
    /// flush, not immediately - a level boundary can fall mid-buffer. Default -1 matches the native's
    /// own <c>Init(-1)</c> (Encoder.h:67); pgf-real-level-lengths.md Stage 1.</summary>
    public int LastLevelIndex = -1;

    /// <summary>Running max of <c>abs(Value[i])</c> across every <see cref="WriteValue"/> call since
    /// the last <see cref="BitplaneEncode"/> - what <see cref="NumberOfBitplanes"/> bases its answer
    /// on (the encoder has to discover how many bitplanes are needed from the actual data; the
    /// decoder just reads a stored count).</summary>
    public uint MaxAbsValue;

    /// <summary>Bit-length of the encoded bitstream actually written to <see cref="CodeBuffer"/> so
    /// far - the encode-side counterpart of <see cref="PgfMacroBlock.ValuePos"/>, but measured in
    /// bits of *output*, not count of *input* values (mirrors <c>m_codePos</c>).</summary>
    public uint CodePos;

    private readonly bool[] sigFlagVector = new bool[PgfConstants.BufferSize + 1];

    // Scratch buffers reused across bitplane iterations within one BitplaneEncode call, and across
    // calls - BufferLen = BufferSize/WordWidth = 512 words (Decoder.h's #define, shared meaning).
    // Pre-allocated instance arrays rather than re-allocated per call: Stage 10 (managed-pgf-codec.md)
    // is where allocation strategy gets a dedicated hardening pass; this stage's job is correctness.
    private const int BufferLen = PgfConstants.BufferSize / 32;
    private readonly uint[] sigBitsScratch = new uint[BufferLen];
    private readonly uint[] refBitsScratch = new uint[BufferLen];
    private readonly uint[] signBitsScratch = new uint[BufferLen];

    public bool IsFull => ValuePos >= PgfConstants.BufferSize;

    /// <summary>Direct port of <c>CEncoder::WriteValue</c> (Encoder.cpp:326), minus the
    /// buffer-full-triggers-an-encode-and-flush behavior (that belongs to
    /// <see cref="PgfEncoderCore"/>'s orchestration, which calls <see cref="BitplaneEncode"/>
    /// itself once a block fills up, mirroring <c>CEncoder::WriteValue</c> calling
    /// <c>EncodeBuffer</c> inline). Stores <paramref name="value"/> and updates
    /// <see cref="MaxAbsValue"/>.</summary>
    public void WriteValue(int value)
    {
        Value[ValuePos++] = value;

        // Promote to long before Abs - matches the original's abs(DataT) needing headroom for
        // int.MinValue (-2147483648): Math.Abs((int)) would throw OverflowException there, but
        // Math.Abs((long)) correctly yields 2147483648 with no overflow, exactly like the C++'s
        // wider-than-DataT abs() does.
        uint magnitude = (uint)Math.Abs((long)value);
        if (magnitude > MaxAbsValue)
        {
            MaxAbsValue = magnitude;
        }
    }

    /// <summary>Direct port of <c>NumberOfBitplanes</c> (Encoder.cpp:750) - how many bitplanes are
    /// needed to represent <see cref="MaxAbsValue"/>, with the same "0 means 16" wraparound encoding
    /// <see cref="PgfMacroBlock.BitplaneDecode"/> reverses.
    ///
    /// Deliberate deviation, verified harmless: the original computes this by repeatedly
    /// right-shifting <c>m_maxAbsValue</c> itself (<c>while (m_maxAbsValue > 0) { m_maxAbsValue
    /// &gt;&gt;= 1; cnt++; }</c>), leaving it at 0 as a mutation side-effect. This port shifts a
    /// local copy instead, leaving <see cref="MaxAbsValue"/> unchanged here - safe because the
    /// *real* reset happens explicitly and unconditionally in <c>WriteMacroBlock</c>
    /// (<c>PgfEncoderCore.WriteMacroBlock</c>, mirroring Encoder.cpp:469's own
    /// <c>block-&gt;m_maxAbsValue = 0;</c>) immediately after every <see cref="BitplaneEncode"/>
    /// call, which already makes the original's in-method mutation redundant - not something this
    /// port depends on differently, just not something worth replicating as a side effect of a
    /// "compute" method.</summary>
    private byte NumberOfBitplanes()
    {
        if (MaxAbsValue == 0)
        {
            return 1;
        }

        byte count = 0;
        uint remaining = MaxAbsValue;
        while (remaining > 0)
        {
            remaining >>= 1;
            count++;
        }

        return count == PgfConstants.MaxBitPlanes + 1 ? (byte)0 : count;
    }

    /// <summary>Direct port of <c>CMacroBlock::BitplaneEncode</c> (Encoder.cpp:482) - encodes
    /// <see cref="Value"/>[0, bufferSize) into <see cref="CodeBuffer"/>, bitplane by bitplane, most
    /// to least significant. Each plane picks whichever of three encodings (RLE'd sig+sign together,
    /// RLE'd signs only, or no RLE at all) is smallest - see the original's own grammar comment
    /// (Encoder.cpp:473-481, identical to <see cref="PgfMacroBlock.BitplaneDecode"/>'s).
    ///
    /// <c>favorSpeed</c> mirrors <c>CEncoder::m_favorSpeed</c> (<c>FavorSpeedOverSize</c>) - skips
    /// the sign-RLE cost comparison entirely when true. This port never sets it (no speed/size
    /// tradeoff knob exposed at this port's scope), but the parameter exists so the logic stays a
    /// faithful, complete port rather than silently dropping a real original code path.</summary>
    public void BitplaneEncode(uint bufferSize, bool favorSpeed = false)
    {
        Array.Clear(sigFlagVector, 0, (int)bufferSize);
        sigFlagVector[bufferSize] = true; // sentinel

        Array.Clear(CodeBuffer, 0, (int)bufferSize);
        CodePos = 0;

        byte nPlanes = NumberOfBitplanes();

        BitStream.SetValueBlock(CodeBuffer, 0, nPlanes, PgfConstants.MaxBitPlanesLog);
        CodePos += PgfConstants.MaxBitPlanesLog;

        uint planeCount = nPlanes == 0 ? (uint)PgfConstants.MaxBitPlanes + 1 : nPlanes;
        uint planeMask = 1u << (int)(planeCount - 1);

        for (int plane = (int)planeCount - 1; plane >= 0; plane--)
        {
            Array.Clear(sigBitsScratch);

            uint sigLen = DecomposeBitplane(
                bufferSize, planeMask, CodePos + PgfConstants.RLblockSizeLen + 1,
                sigBitsScratch, refBitsScratch, signBitsScratch, out uint signLen, out uint codeLen);

            if (sigLen > 0 && codeLen <= PgfConstants.MaxCodeLen &&
                codeLen < BitStream.AlignWordPos(sigLen) + BitStream.AlignWordPos(signLen) + 2 * PgfConstants.RLblockSizeLen)
            {
                // <1><codeLen> - RL coding of sigBits+signBits together was efficient enough
                BitStream.SetBit(CodeBuffer, CodePos++);
                BitStream.SetValueBlock(CodeBuffer, CodePos, codeLen, PgfConstants.RLblockSizeLen);
                CodePos += PgfConstants.RLblockSizeLen + codeLen;
            }
            else
            {
                // <0><sigLen> - not using RL coding for sigBits
                BitStream.ClearBit(CodeBuffer, CodePos++);
                BitStream.SetValueBlock(CodeBuffer, CodePos, sigLen, PgfConstants.RLblockSizeLen);
                CodePos += PgfConstants.RLblockSizeLen;

                bool useRl;
                uint wordPos;
                if (favorSpeed || signLen == 0)
                {
                    useRl = false;
                    codeLen = 0;
                }
                else
                {
                    useRl = true;
                    codeLen = RlESigns(CodePos + PgfConstants.RLblockSizeLen + 1, signBitsScratch, signLen);
                }

                if (useRl && codeLen <= PgfConstants.MaxCodeLen && codeLen < signLen)
                {
                    // <1><codeLen><codedSignBits>_ - RL coding of signBits was efficient
                    BitStream.SetBit(CodeBuffer, CodePos++);
                    BitStream.SetValueBlock(CodeBuffer, CodePos, codeLen, PgfConstants.RLblockSizeLen);
                    wordPos = BitStream.NumberOfWords(CodePos + PgfConstants.RLblockSizeLen + codeLen);
                }
                else
                {
                    // <0><signLen>_<signBits>_ - RL coding of signBits wasn't efficient
                    BitStream.ClearBit(CodeBuffer, CodePos++);
                    BitStream.SetValueBlock(CodeBuffer, CodePos, signLen, PgfConstants.RLblockSizeLen);

                    wordPos = BitStream.NumberOfWords(CodePos + PgfConstants.RLblockSizeLen);
                    uint signWords = BitStream.NumberOfWords(signLen);
                    for (uint k = 0; k < signWords; k++)
                    {
                        CodeBuffer[wordPos++] = signBitsScratch[k];
                    }
                }

                // <sigBits>_
                uint sigWords = BitStream.NumberOfWords(sigLen);
                for (uint k = 0; k < sigWords; k++)
                {
                    CodeBuffer[wordPos++] = sigBitsScratch[k];
                }

                CodePos = wordPos << BitStream.WordWidthLog;
            }

            // _<refBits> (word-aligned)
            uint refWordPos = BitStream.NumberOfWords(CodePos);
            uint refWords = BitStream.NumberOfWords(bufferSize - sigLen);
            for (uint k = 0; k < refWords; k++)
            {
                CodeBuffer[refWordPos++] = refBitsScratch[k];
            }

            CodePos = refWordPos << BitStream.WordWidthLog;
            planeMask >>= 1;
        }
    }

    /// <summary>Direct port of <c>DecomposeBitplane</c> (Encoder.cpp:634) - the adaptive
    /// zero-run-length encoder for one bitplane's significant/sign/refinement bits. See the
    /// original's own RLE-scheme comment (Encoder.cpp:625-633): a run of <c>2^k</c> zeros is coded
    /// as a single 0; a run of <c>count</c> zeros followed by a 1 is coded <c>1&lt;count&gt;x</c>
    /// where x is the terminating coefficient's sign bit.</summary>
    private uint DecomposeBitplane(
        uint bufferSize, uint planeMask, uint codePos,
        Span<uint> sigBits, Span<uint> refBits, Span<uint> signBits, out uint signLen, out uint codeLen)
    {
        uint sigPos = 0;
        uint valuePos = 0;
        uint refPos = 0;
        signLen = 0;

        uint outStartPos = codePos;
        uint k = 3;
        uint runlen = 1u << (int)k;
        uint count = 0;

        while (valuePos < bufferSize)
        {
            uint valueEnd = valuePos;
            while (!sigFlagVector[valueEnd])
            {
                valueEnd++;
            }

            while (valuePos < valueEnd)
            {
                if (GetBitAtPos(valuePos, planeMask))
                {
                    // encode run of `count` zeros followed by a 1: 1<count>(sign)
                    BitStream.SetBit(CodeBuffer, codePos++);
                    if (k > 0)
                    {
                        BitStream.SetValueBlock(CodeBuffer, codePos, count, k);
                        codePos += k;
                        k--;
                        runlen >>= 1;
                    }

                    if (Value[valuePos] < 0)
                    {
                        BitStream.SetBit(signBits, signLen++);
                        BitStream.SetBit(CodeBuffer, codePos++);
                    }
                    else
                    {
                        BitStream.ClearBit(signBits, signLen++);
                        BitStream.ClearBit(CodeBuffer, codePos++);
                    }

                    BitStream.SetBit(sigBits, sigPos++);
                    sigFlagVector[valuePos] = true;
                    count = 0;
                }
                else
                {
                    count++;
                    if (count == runlen)
                    {
                        // encode run of 2^k zeros by a single 0
                        BitStream.ClearBit(CodeBuffer, codePos++);
                        if (k < 32)
                        {
                            k++;
                            runlen <<= 1;
                        }

                        count = 0;
                    }

                    sigPos++;
                }

                valuePos++;
            }

            if (valuePos < bufferSize)
            {
                if (GetBitAtPos(valuePos++, planeMask))
                {
                    BitStream.SetBit(refBits, refPos);
                }
                else
                {
                    BitStream.ClearBit(refBits, refPos);
                }

                refPos++;
            }
        }

        // flush the run in progress: encode remaining `count` zeros followed by a 1 (dummy sign) -
        // matches the original's unconditional trailer write, which is what lets the decoder's
        // matching SeekBitRange-based search always terminate cleanly at the sentinel.
        BitStream.SetBit(CodeBuffer, codePos++);
        if (k > 0)
        {
            BitStream.SetValueBlock(CodeBuffer, codePos, count, k);
            codePos += k;
        }

        BitStream.SetBit(CodeBuffer, codePos++);

        codeLen = codePos - outStartPos;
        return sigPos;
    }

    /// <summary>Direct port of <c>RLESigns</c> (Encoder.cpp:774) - the adaptive run-length encoder
    /// for long runs of the *same* sign, used as a second-pass compression of the sign bits
    /// <see cref="DecomposeBitplane"/> already collected. Encodes a run of <c>2^k</c> ones as a
    /// single 1; a run of <c>count</c> ones followed by a 0 as <c>0&lt;count&gt;</c> (the RLE roles
    /// of 0/1 are the complement of <see cref="DecomposeBitplane"/>'s scheme - see the original's
    /// own comment, Encoder.cpp:768-773).</summary>
    private uint RlESigns(uint codePos, ReadOnlySpan<uint> signBits, uint signLen)
    {
        uint outStartPos = codePos;
        uint k = 0;
        uint runlen = 1u << (int)k;
        uint signPos = 0;

        while (signPos < signLen)
        {
            uint count = BitStream.SeekBit1Range(signBits, signPos, Math.Min(runlen, signLen - signPos));

            if (count == runlen)
            {
                signPos += count;
                BitStream.SetBit(CodeBuffer, codePos++);
                if (k < 32)
                {
                    k++;
                    runlen <<= 1;
                }
            }
            else
            {
                signPos += count + 1;
                BitStream.ClearBit(CodeBuffer, codePos++);
                if (k > 0)
                {
                    BitStream.SetValueBlock(CodeBuffer, codePos, count, k);
                    codePos += k;
                    k--;
                    runlen >>= 1;
                }
            }
        }

        return codePos - outStartPos;
    }

    /// <summary>Direct port of <c>GetBitAtPos</c> (Encoder.h:98) - tests one bit of
    /// <c>abs(Value[pos])</c>. Promotes to <see cref="long"/> before <see cref="Math.Abs(long)"/> for
    /// the same int.MinValue reason as <see cref="WriteValue"/>.</summary>
    private bool GetBitAtPos(uint pos, uint planeMask) => ((uint)Math.Abs((long)Value[pos]) & planeMask) > 0;
}
