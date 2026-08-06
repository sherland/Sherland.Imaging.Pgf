using Sherland.Imaging.Pgf;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 2 exit tests (new-features/managed-pgf-codec.md): <see cref="BitStream"/> against
/// hand-constructed bit patterns - no PGF file involved, pure bit-array math verified in isolation
/// before anything is built on top of it.
/// </summary>
public class BitStreamTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(31u)]
    [InlineData(32u)]
    [InlineData(33u)]
    [InlineData(63u)]
    [InlineData(64u)]
    public void SetBit_ThenGetBit_ReturnsTrue_OnlyAtThatPosition(uint pos)
    {
        Span<uint> stream = stackalloc uint[4];
        BitStream.SetBit(stream, pos);

        for (uint p = 0; p < 128; p++)
        {
            Assert.Equal(p == pos, BitStream.GetBit(stream, p));
        }
    }

    [Fact]
    public void SetBit_ThenClearBit_ReturnsFalse()
    {
        Span<uint> stream = stackalloc uint[2];
        BitStream.SetBit(stream, 10);
        Assert.True(BitStream.GetBit(stream, 10));

        BitStream.ClearBit(stream, 10);
        Assert.False(BitStream.GetBit(stream, 10));
    }

    [Fact]
    public void ClearBit_DoesNotAffectNeighboringBits()
    {
        Span<uint> stream = stackalloc uint[2];
        BitStream.SetBit(stream, 9);
        BitStream.SetBit(stream, 10);
        BitStream.SetBit(stream, 11);

        BitStream.ClearBit(stream, 10);

        Assert.True(BitStream.GetBit(stream, 9));
        Assert.False(BitStream.GetBit(stream, 10));
        Assert.True(BitStream.GetBit(stream, 11));
    }

    [Theory]
    [InlineData(0u, 5u, 0x1Au)] // fits in one word
    [InlineData(20u, 16u, 0xBEEFu)] // spans the boundary at bit 32 (20+16=36 > 32)
    [InlineData(31u, 8u, 0xABu)] // starts one bit before the word boundary
    [InlineData(0u, 32u, 0xDEADBEEFu)] // exactly one full word
    public void SetValueBlock_ThenGetValueBlock_RoundTrips(uint pos, uint k, uint val)
    {
        Span<uint> stream = stackalloc uint[4];
        BitStream.SetValueBlock(stream, pos, val, k);

        uint expectedMasked = k == 32 ? val : val & ((1u << (int)k) - 1);
        Assert.Equal(expectedMasked, BitStream.GetValueBlock(stream, pos, k));
    }

    [Fact]
    public void SetValueBlock_DoesNotDisturbBitsOutsideTheBlock()
    {
        Span<uint> stream = stackalloc uint[2];
        BitStream.SetBitBlock(stream, 0, 64); // all ones

        BitStream.SetValueBlock(stream, 10, 0, 5); // clear a 5-bit hole of zeros

        for (uint p = 0; p < 10; p++)
        {
            Assert.True(BitStream.GetBit(stream, p), $"bit {p} should still be set");
        }

        for (uint p = 15; p < 64; p++)
        {
            Assert.True(BitStream.GetBit(stream, p), $"bit {p} should still be set");
        }

        Assert.Equal(0u, BitStream.GetValueBlock(stream, 10, 5));
    }

    [Theory]
    [InlineData(0u, 5u, 0x1Au, true)]
    [InlineData(0u, 5u, 0x05u, false)]
    [InlineData(20u, 16u, 0xBEEFu, true)]
    public void CompareBitBlock_MatchesOnlyWhatWasActuallySet(uint pos, uint k, uint val, bool compareAgainstSameValue)
    {
        Span<uint> stream = stackalloc uint[4];
        BitStream.SetValueBlock(stream, pos, val, k);

        bool result = BitStream.CompareBitBlock(stream, pos, k, compareAgainstSameValue ? val : val ^ 0x3u);

        Assert.Equal(compareAgainstSameValue, result);
    }

    [Fact]
    public void ClearBitBlock_WithinOneWord_ClearsThroughEndOfWord()
    {
        Span<uint> stream = stackalloc uint[1];
        stream[0] = 0xFFFFFFFF;

        // Real semantics (see BitStream.ClearBitBlock's doc comment): no end-mask, so this clears
        // bits [4,32) - through the end of the word - not just the requested [4,12).
        BitStream.ClearBitBlock(stream, 4, 8);

        for (uint p = 0; p < 32; p++)
        {
            bool expected = p < 4;
            Assert.Equal(expected, BitStream.GetBit(stream, p));
        }
    }

    [Fact]
    public void ClearBitBlock_AcrossWordBoundary_ClearsThroughEndOfLastWordTouched()
    {
        Span<uint> stream = stackalloc uint[3];
        stream.Fill(0xFFFFFFFF);

        // pos=20, len=24 -> spans word 0 (bits 20-31) and word 1; word 1 is cleared *entirely*
        // (bits 32-63), not just up to bit 43. Word 2 (bits 64-95) is never touched at all, so it
        // keeps its original (set) value from stream.Fill above.
        BitStream.ClearBitBlock(stream, 20, 24);

        for (uint p = 0; p < 96; p++)
        {
            bool expected = p < 20 || p >= 64;
            Assert.Equal(expected, BitStream.GetBit(stream, p));
        }
    }

    [Fact]
    public void SetBitBlock_AcrossWordBoundary_SetsThroughEndOfLastWordTouched()
    {
        Span<uint> stream = stackalloc uint[3];
        stream.Clear();

        // Mirror of ClearBitBlock_AcrossWordBoundary_ClearsThroughEndOfLastWordTouched.
        BitStream.SetBitBlock(stream, 20, 24);

        for (uint p = 0; p < 96; p++)
        {
            bool expected = p is >= 20 and < 64;
            Assert.Equal(expected, BitStream.GetBit(stream, p));
        }
    }

    [Fact]
    public void SeekBitRange_FindsNextSetBit()
    {
        Span<uint> stream = stackalloc uint[2];
        BitStream.SetBit(stream, 10);

        uint distance = BitStream.SeekBitRange(stream, 0, 64);

        Assert.Equal(10u, distance);
    }

    [Fact]
    public void SeekBitRange_NoSetBitWithinLen_ReturnsLen()
    {
        Span<uint> stream = stackalloc uint[2];

        uint distance = BitStream.SeekBitRange(stream, 0, 64);

        Assert.Equal(64u, distance);
    }

    [Fact]
    public void SeekBitRange_SkipsWholeZeroWordsQuickly()
    {
        Span<uint> stream = stackalloc uint[4];
        BitStream.SetBit(stream, 100);

        uint distance = BitStream.SeekBitRange(stream, 0, 128);

        Assert.Equal(100u, distance);
    }

    [Fact]
    public void SeekBit1Range_FindsNextClearBit()
    {
        Span<uint> stream = stackalloc uint[2];
        stream.Fill(0xFFFFFFFF);
        BitStream.ClearBit(stream, 15);

        uint distance = BitStream.SeekBit1Range(stream, 0, 64);

        Assert.Equal(15u, distance);
    }

    [Fact]
    public void SeekBit1Range_NoClearBitWithinLen_ReturnsLen()
    {
        Span<uint> stream = stackalloc uint[2];
        stream.Fill(0xFFFFFFFF);

        uint distance = BitStream.SeekBit1Range(stream, 0, 64);

        Assert.Equal(64u, distance);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 32u)]
    [InlineData(31u, 32u)]
    [InlineData(32u, 32u)]
    [InlineData(33u, 64u)]
    [InlineData(63u, 64u)]
    [InlineData(64u, 64u)]
    public void AlignWordPos_RoundsUpToNextWordBoundary(uint pos, uint expected)
    {
        Assert.Equal(expected, BitStream.AlignWordPos(pos));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(32u, 1u)]
    [InlineData(33u, 2u)]
    [InlineData(64u, 2u)]
    [InlineData(65u, 3u)]
    public void NumberOfWords_ComputesWordCountForBitLength(uint pos, uint expectedWords)
    {
        Assert.Equal(expectedWords, BitStream.NumberOfWords(pos));
    }
}
