using Sherland.Imaging.Pgf;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 3 exit tests (new-features/managed-pgf-codec.md): <see cref="PgfByteWriter"/> growth and
/// seek-back-and-patch correctness - the latter is the real usage pattern later encoder stages need
/// (<c>CEncoder::UpdatePostHeaderSize</c>/<c>WriteLevelLength</c> seek back to rewrite a placeholder
/// field, then writing continues forward from wherever it left off).
/// </summary>
public class PgfByteWriterTests
{
    [Fact]
    public void Write_AppendsBytes_AndWrittenSpanReflectsThem()
    {
        PgfByteWriter writer = new();

        writer.Write([1, 2, 3]);
        writer.Write([4, 5]);

        Assert.Equal((byte[])[1, 2, 3, 4, 5], writer.WrittenSpan.ToArray());
        Assert.Equal(5, writer.Length);
        Assert.Equal(5, writer.Position);
    }

    [Fact]
    public void Write_PastInitialCapacity_GrowsAndPreservesAllContent()
    {
        PgfByteWriter writer = new();
        byte[] large = new byte[100_000];
        for (int i = 0; i < large.Length; i++)
        {
            large[i] = (byte)(i % 256);
        }

        writer.Write(large);

        Assert.Equal(large.Length, writer.Length);
        Assert.Equal(large, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void SetPos_ThenWrite_OverwritesInPlace_WithoutTruncatingWhatFollows()
    {
        PgfByteWriter writer = new();
        writer.Write([0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA]);

        // The real header-patching pattern: seek back to a placeholder, overwrite a few bytes,
        // and everything after the patched region must survive untouched.
        writer.SetPos(SeekOrigin.Begin, 1);
        writer.Write([0xBB, 0xBB]);

        Assert.Equal((byte[])[0xAA, 0xBB, 0xBB, 0xAA, 0xAA, 0xAA], writer.WrittenSpan.ToArray());
        Assert.Equal(6, writer.Length);
    }

    [Fact]
    public void SetPos_ThenWrite_PastPreviousEnd_ExtendsTheStream()
    {
        PgfByteWriter writer = new();
        writer.Write([1, 2, 3]);

        writer.SetPos(SeekOrigin.End, 0);
        writer.Write([4, 5]);

        Assert.Equal((byte[])[1, 2, 3, 4, 5], writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void SetPos_FromCurrent_MovesRelativeToCurrentPosition()
    {
        PgfByteWriter writer = new();
        writer.Write([1, 2, 3, 4, 5]);

        writer.SetPos(SeekOrigin.Begin, 0);
        writer.SetPos(SeekOrigin.Current, 2);

        Assert.Equal(2, writer.Position);
    }

    [Fact]
    public void Position_TracksWritesAndSeeksIndependentlyOfLength()
    {
        PgfByteWriter writer = new();
        writer.Write([1, 2, 3, 4]);

        writer.SetPos(SeekOrigin.Begin, 1);

        Assert.Equal(1, writer.Position);
        Assert.Equal(4, writer.Length);
    }

    [Fact]
    public void EmptyWriter_HasZeroLengthAndPosition()
    {
        PgfByteWriter writer = new();

        Assert.Equal(0, writer.Length);
        Assert.Equal(0, writer.Position);
        Assert.Empty(writer.WrittenSpan.ToArray());
    }
}
