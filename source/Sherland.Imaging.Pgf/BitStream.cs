// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

using System.Runtime.CompilerServices;

namespace Sherland.Imaging.Pgf;

/// <summary>
/// Direct C# port of libpgf's <c>BitStream.h</c> (native/Sherland.Imaging.Pgf.Native/libpgf/BitStream.h) -
/// stateless bit-array primitives operating on a caller-supplied word buffer, shared by both the
/// decode and encode ports (new-features/managed-pgf-codec.md, Stage 2). Method names, parameter
/// order, and bit-layout semantics deliberately mirror the original 1:1 (including the field names
/// <c>pos</c>/<c>k</c>/<c>len</c>/<c>val</c>) to keep this file directly diffable against the
/// vendored source rather than a paraphrase - the whole point of porting bottom-up is that each
/// piece can be verified against the real algorithm in isolation.
///
/// <c>WordWidth</c> = 32 (bits per <see cref="uint"/> word), <c>WordWidthLog</c> = 5 (its base-2
/// log) - see PGFplatform.h. A bit stream is a flat array of words; <c>pos</c>
/// parameters throughout are zero-based bit offsets into that array, not word offsets.
/// </summary>
internal static class BitStream
{
    private const int WordWidth = 32;

    /// <summary>Exposed (not just <c>private</c>) so callers converting a bit position into a word
    /// index - e.g. <c>PgfMacroBlock</c> slicing <c>CodeBuffer</c> at a bit-aligned offset - can
    /// reuse the same constant instead of hard-coding <c>&gt;&gt; 5</c>.</summary>
    public const int WordWidthLog = 5;

    private const uint WordMask = 0xFFFFFFE0;
    private const uint Filled = 0xFFFFFFFF;

    /// <summary>Set one bit of a bit stream to 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBit(Span<uint> stream, uint pos)
    {
        stream[(int)(pos >> WordWidthLog)] |= 1u << (int)(pos % WordWidth);
    }

    /// <summary>Set one bit of a bit stream to 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearBit(Span<uint> stream, uint pos)
    {
        stream[(int)(pos >> WordWidthLog)] &= ~(1u << (int)(pos % WordWidth));
    }

    /// <summary>Return one bit of a bit stream.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(ReadOnlySpan<uint> stream, uint pos)
    {
        return (stream[(int)(pos >> WordWidthLog)] & (1u << (int)(pos % WordWidth))) > 0;
    }

    /// <summary>Compare k-bit binary representation of stream at position pos with val.</summary>
    /// <param name="stream">The bit stream to read.</param>
    /// <param name="pos">Zero-based bit offset into <paramref name="stream"/>.</param>
    /// <param name="k">Number of bits to compare.</param>
    /// <param name="val">Value to compare against.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareBitBlock(ReadOnlySpan<uint> stream, uint pos, uint k, uint val)
    {
        uint iLoInt = pos >> WordWidthLog;
        uint iHiInt = (pos + k - 1) >> WordWidthLog;
        uint mask = Filled >> (WordWidth - (int)k);

        if (iLoInt == iHiInt)
        {
            // fits into one integer
            val &= mask;
            val <<= (int)(pos % WordWidth);
            return (stream[(int)iLoInt] & val) == val;
        }
        else
        {
            // must be split over integer boundary
            ulong v1 = MakeU64(stream[(int)iLoInt], stream[(int)iHiInt]);
            ulong v2 = (ulong)(val & mask) << (int)(pos % WordWidth);
            return (v1 & v2) == v2;
        }
    }

    /// <summary>Store k-bit binary representation of val in stream at position pos.</summary>
    /// <param name="stream">The bit stream to write into.</param>
    /// <param name="pos">Zero-based bit offset into <paramref name="stream"/>.</param>
    /// <param name="val">Value to store.</param>
    /// <param name="k">Number of bits of integer representation of val.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueBlock(Span<uint> stream, uint pos, uint val, uint k)
    {
        int offset = (int)(pos % WordWidth);
        uint iLoInt = pos >> WordWidthLog;
        uint iHiInt = (pos + k - 1) >> WordWidthLog;
        uint loMask = Filled << offset;
        uint hiMask = Filled >> (WordWidth - 1 - (int)((pos + k - 1) % WordWidth));

        if (iLoInt == iHiInt)
        {
            // fits into one integer
            stream[(int)iLoInt] &= ~(loMask & hiMask); // clear bits
            stream[(int)iLoInt] |= val << offset; // write value
        }
        else
        {
            // must be split over integer boundary
            stream[(int)iLoInt] &= ~loMask; // clear bits
            stream[(int)iLoInt] |= val << offset; // write lower part of value
            stream[(int)iHiInt] &= ~hiMask; // clear bits
            stream[(int)iHiInt] |= val >> (WordWidth - offset); // write higher part of value
        }
    }

    /// <summary>Read k-bit number from stream at position pos.</summary>
    /// <param name="stream">The bit stream to read.</param>
    /// <param name="pos">Zero-based bit offset into <paramref name="stream"/>.</param>
    /// <param name="k">Number of bits to read: 1 &lt;= k &lt;= 32.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetValueBlock(ReadOnlySpan<uint> stream, uint pos, uint k)
    {
        uint iLoInt = pos >> WordWidthLog; // integer of first bit
        uint iHiInt = (pos + k - 1) >> WordWidthLog; // integer of last bit
        uint loMask = Filled << (int)(pos % WordWidth);
        uint hiMask = Filled >> (WordWidth - 1 - (int)((pos + k - 1) % WordWidth));

        uint count;
        if (iLoInt == iHiInt)
        {
            // inside integer boundary
            count = stream[(int)iLoInt] & (loMask & hiMask);
            count >>= (int)(pos % WordWidth);
        }
        else
        {
            // overlapping integer boundary
            count = stream[(int)iLoInt] & loMask;
            count >>= (int)(pos % WordWidth);
            uint hiCount = stream[(int)iHiInt] & hiMask;
            hiCount <<= WordWidth - (int)(pos % WordWidth);
            count |= hiCount;
        }

        return count;
    }

    /// <summary>Clears bits <c>[pos, pos+len)</c> - and then some: there is no end-mask on the
    /// final word touched (confirmed against the real behavior, not just the "at least len" doc
    /// comment above it in the original), so every bit from <paramref name="pos"/> through the end
    /// of whatever word contains bit <c>pos+len-1</c> gets cleared, even in the single-word case.
    /// E.g. <c>ClearBitBlock(stream, 4, 8)</c> clears bits 4-31, not just 4-11. Callers must not
    /// assume an exact <paramref name="len"/>-bit range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearBitBlock(Span<uint> stream, uint pos, uint len)
    {
        uint iFirstInt = pos >> WordWidthLog;
        uint iLastInt = (pos + len - 1) >> WordWidthLog;
        uint startMask = Filled << (int)(pos % WordWidth);

        if (iFirstInt == iLastInt)
        {
            stream[(int)iFirstInt] &= ~startMask;
        }
        else
        {
            stream[(int)iFirstInt] &= ~startMask;
            for (uint i = iFirstInt + 1; i <= iLastInt; i++)
            {
                stream[(int)i] = 0;
            }
        }
    }

    /// <summary>Sets bits <c>[pos, pos+len)</c> - and then some, through the end of whatever word
    /// contains bit <c>pos+len-1</c>. See <see cref="ClearBitBlock"/>'s doc comment - the same
    /// no-end-mask behavior applies here.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBitBlock(Span<uint> stream, uint pos, uint len)
    {
        uint iFirstInt = pos >> WordWidthLog;
        uint iLastInt = (pos + len - 1) >> WordWidthLog;
        uint startMask = Filled << (int)(pos % WordWidth);

        if (iFirstInt == iLastInt)
        {
            stream[(int)iFirstInt] |= startMask;
        }
        else
        {
            stream[(int)iFirstInt] |= startMask;
            for (uint i = iFirstInt + 1; i <= iLastInt; i++)
            {
                stream[(int)i] = Filled;
            }
        }
    }

    /// <summary>Returns the distance to the next 1 in stream at position pos. If no 1 is found
    /// within len bits, then len is returned.
    ///
    /// The original C++ (<c>while ((*word &amp; testMask) == 0) &amp;&amp; (count &lt; len))</c>)
    /// deliberately over-reads one word past the caller's logical range whenever the scanned range
    /// is entirely zero and ends exactly on a word boundary - harmless there only because real
    /// callers' buffers (e.g. <c>CMacroBlock::m_codeBuffer</c>) always have slack past the "in use"
    /// length. A bounds-checked <see cref="Span{T}"/> has no such slack to rely on implicitly, so
    /// this port checks <c>count &lt; len</c> first (reordering the <c>&amp;&amp;</c>, which
    /// short-circuits identically either way for every in-bounds access) to never read past
    /// <paramref name="len"/> bits - same return value for every valid input, provably safe instead
    /// of safe-by-caller-convention.</summary>
    public static uint SeekBitRange(ReadOnlySpan<uint> stream, uint pos, uint len)
    {
        uint count = 0;
        uint testMask = 1u << (int)(pos % WordWidth);
        int wordIndex = (int)(pos >> WordWidthLog);

        while (count < len && (stream[wordIndex] & testMask) == 0)
        {
            count++;
            testMask <<= 1;
            if (testMask == 0)
            {
                wordIndex++;
                testMask = 1;

                // fast steps if all bits in a word are zero
                while (count + WordWidth <= len && stream[wordIndex] == 0)
                {
                    wordIndex++;
                    count += WordWidth;
                }
            }
        }

        return count;
    }

    /// <summary>Returns the distance to the next 0 in stream at position pos. If no 0 is found
    /// within len bits, then len is returned. See <see cref="SeekBitRange"/>'s doc comment for why
    /// the <c>&amp;&amp;</c> operand order here deliberately differs from the original C++.</summary>
    public static uint SeekBit1Range(ReadOnlySpan<uint> stream, uint pos, uint len)
    {
        uint count = 0;
        uint testMask = 1u << (int)(pos % WordWidth);
        int wordIndex = (int)(pos >> WordWidthLog);

        while (count < len && (stream[wordIndex] & testMask) != 0)
        {
            count++;
            testMask <<= 1;
            if (testMask == 0)
            {
                wordIndex++;
                testMask = 1;

                // fast steps if all bits in a word are one
                while (count + WordWidth <= len && stream[wordIndex] == Filled)
                {
                    wordIndex++;
                    count += WordWidth;
                }
            }
        }

        return count;
    }

    /// <summary>Compute bit position of the next 32-bit word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AlignWordPos(uint pos) => (pos + WordWidth - 1) & WordMask;

    /// <summary>Compute number of the 32-bit words needed to hold pos bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint NumberOfWords(uint pos) => (pos + WordWidth - 1) >> WordWidthLog;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MakeU64(uint lo, uint hi) => lo | ((ulong)hi << 32);
}
