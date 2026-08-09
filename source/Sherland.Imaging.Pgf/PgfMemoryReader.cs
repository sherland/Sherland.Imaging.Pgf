// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf;

/// <summary>
/// Replicates the real, exact contract of <c>CPGFMemoryStream</c> (PGFstream.cpp) as used by every
/// decode call in this codebase - always the non-owning constructor
/// (<c>CPGFMemoryStream(UINT8*, size_t)</c>), never the allocating/growable one (that one exists
/// only for encode's write-then-grow path, ported separately as <see cref="PgfByteWriter"/> since
/// C# has no realloc()/delete[] mismatch to work around and can just use a real growable buffer -
/// see shim.cpp's <c>pgf_encode_bgra_alloc</c> doc comment for why the native side deliberately
/// avoids <c>CPGFMemoryStream</c>'s allocating constructor).
///
/// A class, not a <c>ref struct</c>: later decode stages (Stage 5+) hold a stream reference as a
/// long-lived field across many calls, mirroring <c>CDecoder</c>'s own <c>CPGFStream*</c> member -
/// a <c>ref struct</c> couldn't be stored that way.
/// </summary>
internal sealed class PgfMemoryReader
{
    private readonly ReadOnlyMemory<byte> buffer;
    private long position;

    public PgfMemoryReader(ReadOnlyMemory<byte> buffer)
    {
        this.buffer = buffer;
    }

    /// <summary>Total stream length - always the full buffer size for this non-owning reader,
    /// matching <c>CPGFMemoryStream(UINT8*, size_t)</c>'s constructor setting
    /// <c>m_eos = pBuffer + size</c> unconditionally (there is no separate "amount written"
    /// concept on the read side - the whole caller-supplied buffer is the stream).</summary>
    public long Length => buffer.Length;

    public long Position => position;

    /// <summary>Reads up to <paramref name="destination"/>'s length, truncating at end-of-stream
    /// rather than throwing or short-reading unexpectedly - the real
    /// <c>CPGFMemoryStream::Read</c> behavior (<c>*count = max(0, m_eos - m_pos)</c>), not
    /// "whatever <see cref="Stream"/>/<see cref="BinaryReader"/> does by default." Returns the
    /// actual number of bytes copied and advances <see cref="Position"/> by that amount.</summary>
    public int Read(Span<byte> destination)
    {
        long remaining = buffer.Length - position;
        int toCopy = (int)Math.Clamp(destination.Length, 0, Math.Max(0, remaining));

        buffer.Span.Slice((int)position, toCopy).CopyTo(destination);
        position += toCopy;
        return toCopy;
    }

    /// <summary>Sets the stream position, matching <c>CPGFMemoryStream::SetPos</c>'s three modes
    /// exactly (<c>FSFromStart</c>/<c>FSFromCurrent</c>/<c>FSFromEnd</c> -&gt;
    /// <see cref="SeekOrigin.Begin"/>/<see cref="SeekOrigin.Current"/>/<see cref="SeekOrigin.End"/>).
    /// Throws <see cref="PgfStreamException"/> if the new position would exceed
    /// <see cref="Length"/> - the real, verified upper-bound check
    /// (<c>if (m_pos &gt; m_eos) ReturnWithError(InvalidStreamPos)</c>).
    ///
    /// Deliberately adds a lower-bound check the original does not have: C++'s <c>SetPos</c> never
    /// validates a negative result (only the upper bound), leaving <c>m_pos</c> before
    /// <c>m_buffer</c> - not dereferenced immediately, so not a crash there, but genuinely invalid
    /// and only caught (if ever) by whatever call happens to dereference it later. No real decode
    /// path in this codebase seeks negative, so this changes no valid-input behavior; it just fails
    /// fast and clearly instead of leaving an invalid position to be discovered downstream.</summary>
    public void SetPos(SeekOrigin origin, long offset)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => buffer.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (newPosition < 0 || newPosition > buffer.Length)
        {
            throw new PgfStreamException($"Invalid stream position {newPosition} (length {buffer.Length}).");
        }

        position = newPosition;
    }
}
