using PictTag.PgfCodec;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// Stage 3 exit tests (new-features/managed-pgf-codec.md): <see cref="PgfMemoryReader"/> against
/// hand-constructed byte buffers, focused on the edge cases that make this port's contract exact
/// rather than "whatever felt natural" - truncate-at-EOS on read, and the real upper-bound-only
/// SetPos check (plus this port's deliberate added lower-bound check).
/// </summary>
public class PgfMemoryReaderTests
{
    private static byte[] SampleBytes => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    [Fact]
    public void Read_WithinBounds_ReturnsRequestedBytes_AndAdvancesPosition()
    {
        PgfMemoryReader reader = new(SampleBytes);

        Span<byte> destination = stackalloc byte[4];
        int read = reader.Read(destination);

        Assert.Equal(4, read);
        Assert.Equal((byte[])[0, 1, 2, 3], destination.ToArray());
        Assert.Equal(4, reader.Position);
    }

    [Fact]
    public void Read_SequentialCalls_ContinueFromWherePreviousCallLeftOff()
    {
        PgfMemoryReader reader = new(SampleBytes);

        Span<byte> first = stackalloc byte[3];
        Span<byte> second = stackalloc byte[3];
        reader.Read(first);
        reader.Read(second);

        Assert.Equal((byte[])[0, 1, 2], first.ToArray());
        Assert.Equal((byte[])[3, 4, 5], second.ToArray());
    }

    [Fact]
    public void Read_PastEndOfStream_TruncatesInsteadOfThrowingOrOverreading()
    {
        PgfMemoryReader reader = new(SampleBytes);
        reader.SetPos(SeekOrigin.Begin, 8);

        Span<byte> destination = stackalloc byte[10];
        int read = reader.Read(destination);

        Assert.Equal(2, read);
        Assert.Equal((byte[])[8, 9], destination[..2].ToArray());
        Assert.Equal(10, reader.Position);
    }

    [Fact]
    public void Read_AtExactEndOfStream_ReturnsZero()
    {
        PgfMemoryReader reader = new(SampleBytes);
        reader.SetPos(SeekOrigin.Begin, 10);

        Span<byte> destination = stackalloc byte[5];
        int read = reader.Read(destination);

        Assert.Equal(0, read);
    }

    [Fact]
    public void SetPos_FromStart_SetsAbsolutePosition()
    {
        PgfMemoryReader reader = new(SampleBytes);

        reader.SetPos(SeekOrigin.Begin, 5);

        Assert.Equal(5, reader.Position);
    }

    [Fact]
    public void SetPos_FromCurrent_OffsetsRelativeToCurrentPosition()
    {
        PgfMemoryReader reader = new(SampleBytes);
        reader.SetPos(SeekOrigin.Begin, 4);

        reader.SetPos(SeekOrigin.Current, 3);

        Assert.Equal(7, reader.Position);
    }

    [Fact]
    public void SetPos_FromCurrent_CanMoveBackward()
    {
        PgfMemoryReader reader = new(SampleBytes);
        reader.SetPos(SeekOrigin.Begin, 6);

        reader.SetPos(SeekOrigin.Current, -4);

        Assert.Equal(2, reader.Position);
    }

    [Fact]
    public void SetPos_FromEnd_OffsetsRelativeToLength()
    {
        PgfMemoryReader reader = new(SampleBytes);

        reader.SetPos(SeekOrigin.End, -3);

        Assert.Equal(7, reader.Position);
    }

    [Fact]
    public void SetPos_ExactlyAtEnd_Succeeds()
    {
        PgfMemoryReader reader = new(SampleBytes);

        reader.SetPos(SeekOrigin.Begin, 10);

        Assert.Equal(10, reader.Position);
    }

    [Fact]
    public void SetPos_PastEnd_ThrowsPgfStreamException()
    {
        PgfMemoryReader reader = new(SampleBytes);

        Assert.Throws<PgfStreamException>(() => reader.SetPos(SeekOrigin.Begin, 11));
    }

    [Fact]
    public void SetPos_Negative_ThrowsPgfStreamException()
    {
        PgfMemoryReader reader = new(SampleBytes);

        Assert.Throws<PgfStreamException>(() => reader.SetPos(SeekOrigin.Begin, -1));
    }

    [Fact]
    public void SetPos_FailedSeek_DoesNotChangePosition()
    {
        PgfMemoryReader reader = new(SampleBytes);
        reader.SetPos(SeekOrigin.Begin, 3);

        Assert.Throws<PgfStreamException>(() => reader.SetPos(SeekOrigin.Begin, 999));

        Assert.Equal(3, reader.Position);
    }

    [Fact]
    public void Length_IsTheFullBufferSize()
    {
        PgfMemoryReader reader = new(SampleBytes);

        Assert.Equal(10, reader.Length);
    }
}
