namespace PictTag.PgfCodec;

/// <summary>
/// Encode-side counterpart to <see cref="PgfMemoryReader"/> - a growable output buffer matching the
/// <c>Write</c>/<c>SetPos</c>/<c>GetPos</c> vocabulary later encoder stages (Stage 4+, porting
/// <c>CEncoder</c>) need for header-patching (<c>WriteLevelLength</c>/<c>UpdatePostHeaderSize</c>
/// seek backward to rewrite placeholder fields once the real values are known, then writing
/// continues forward from wherever it left off).
///
/// Backed by a real <see cref="MemoryStream"/> rather than a hand-rolled growable array: unlike the
/// native shim's <c>pgf_encode_bgra_alloc</c> (which deliberately avoids <c>CPGFMemoryStream</c>'s
/// allocating constructor because its <c>realloc()</c>-based growth mismatches a paired
/// <c>delete[]</c> - see that function's doc comment), C# has no such allocator mismatch to work
/// around, so there is no reason to replicate the C++ workaround here - a real growable
/// <see cref="MemoryStream"/> is both simpler and idiomatic.
/// </summary>
internal sealed class PgfByteWriter
{
    private readonly MemoryStream stream = new();

    public long Position => stream.Position;

    public long Length => stream.Length;

    public void Write(ReadOnlySpan<byte> data) => stream.Write(data);

    /// <summary>Matches <c>CPGFStream::SetPos</c>'s <c>FSFromStart</c>/<c>FSFromCurrent</c>/
    /// <c>FSFromEnd</c> modes via <see cref="SeekOrigin"/>. Unlike <see cref="PgfMemoryReader.
    /// SetPos"/>, seeking past the current length is valid here (matching
    /// <see cref="MemoryStream"/>'s own semantics) - the next <see cref="Write"/> extends the
    /// stream, which is exactly what the header-patching use case above needs: seek back, write a
    /// few bytes, and the writer's read of "current end" for later appends is unaffected.</summary>
    public void SetPos(SeekOrigin origin, long offset) => stream.Seek(offset, origin);

    /// <summary>The written bytes so far, in order - a live view backed by the stream's internal
    /// buffer, only valid until the next <see cref="Write"/> (which may reallocate).</summary>
    public ReadOnlySpan<byte> WrittenSpan =>
        stream.TryGetBuffer(out ArraySegment<byte> segment)
            ? segment.AsSpan()
            : stream.ToArray();
}
