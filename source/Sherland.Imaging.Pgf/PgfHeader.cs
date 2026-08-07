// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

using System.Buffers.Binary;

namespace Sherland.Imaging.Pgf;

/// <summary>Direct port of <c>PGFHeader</c> (PGFtypes.h) - the 16-byte, <c>#pragma pack(1)</c> fixed
/// header. <see cref="VersionNumberRaw"/> is the packed <c>PGFVersionNumber</c> bitfield
/// (<c>major:4, year:6, week:6</c>, LSB-first per MSVC's bitfield packing for consecutive
/// same-underlying-type fields) - kept opaque/unpacked here because nothing in this codebase's
/// decode/encode path actually branches on it (only <c>CPGFImage::Version()</c>, a reporting
/// accessor never called from the shim, reads it) - see
/// <see cref="PgfVersionFlags"/> for the field that actually matters.</summary>
internal readonly record struct PgfHeader(
    uint Width,
    uint Height,
    byte NLevels,
    byte Quality,
    byte Bpp,
    byte Channels,
    byte Mode,
    byte UsedBitsPerChannel,
    ushort VersionNumberRaw)
{
    /// <summary>Packs (major, year, week) into <see cref="VersionNumberRaw"/>'s bit layout -
    /// verified against the real MSVC-compiled behavior by the Stage 4 cross-implementation exit
    /// test (a C#-written header, opened by the real native decoder), not assumed from reading the
    /// bitfield declaration alone.</summary>
    public static ushort PackVersionNumber(byte major, byte year, byte week) =>
        (ushort)(major | (year << 4) | (week << 10));
}

internal readonly record struct PgfPreHeader(PgfVersionFlags VersionFlags, uint HSize);

/// <summary>Header read/write, matching <c>CDecoder</c>'s constructor (Decoder.cpp:83) and the
/// header-writing portion of <c>CEncoder</c>'s constructor (Encoder.cpp:70) exactly - see
/// managed-pgf-codec.md's Stage 4 notes for the byte-level trace this was built from.</summary>
internal static class PgfHeaderIO
{
    /// <summary>Reads pre-header, header, level-length array, and (pgf-all-image-modes.md Stage 1)
    /// <see cref="PgfConstants.ImageModeIndexedColor"/>'s post-header color table, from the current
    /// stream position - the same sequence <c>CDecoder</c>'s constructor reads (minus macroblock/
    /// thread setup, which belongs to Stage 5's entropy decoder, not header parsing).
    ///
    /// pgf-user-data-and-small-images.md Stage 1: also reads the post-header's user data (the other
    /// half of <c>PGFPostHeader</c>, PGFtypes.h:173-178), respecting <paramref name="policy"/>/
    /// <paramref name="prefixSize"/> exactly like <c>CDecoder</c>'s constructor does
    /// (Decoder.cpp:143-186) - previously this just skipped over those bytes unconditionally
    /// (matching <c>UP_Skip</c> only). Goal 3's untrusted-length defense lives here: the post-header
    /// size is derived from <paramref name="reader"/>'s own <c>hSize</c> field, which is attacker-
    /// controlled for a general (non-digiKam) caller - before caching anything, the declared user
    /// data length is checked against the stream's real remaining length so a corrupted/malicious
    /// header claiming far more data than actually exists fails closed (<see cref="PgfFormatException"/>)
    /// instead of attempting an oversized allocation that would then silently short-read.</summary>
    public static (PgfPreHeader PreHeader, PgfHeader Header, uint[] LevelLengths, byte[]? ColorTable, PgfUserData UserData) Read(
        PgfMemoryReader reader, PgfUserDataPolicy policy = PgfUserDataPolicy.CacheAll, uint prefixSize = 0)
    {
        Span<byte> magicVersion = stackalloc byte[PgfConstants.MagicVersionSize];
        if (reader.Read(magicVersion) != magicVersion.Length)
        {
            throw new PgfFormatException("Truncated stream: missing magic/version.");
        }

        if (!magicVersion[..3].SequenceEqual(PgfConstants.Magic))
        {
            throw new PgfFormatException("Not a PGF file (bad magic).");
        }

        PgfVersionFlags versionFlags = (PgfVersionFlags)magicVersion[3];

        // hSize is 4 bytes since Version6, 2 bytes for older (pre-2019) files - CDecoder reads
        // whichever the version flag calls for; every file this port's own encoder produces (or any
        // modern real PGF, including every real digiKam thumbnail) always sets Version6.
        uint hSize;
        if ((versionFlags & PgfVersionFlags.Version6) != 0)
        {
            Span<byte> hSizeBytes = stackalloc byte[4];
            if (reader.Read(hSizeBytes) != 4)
            {
                throw new PgfFormatException("Truncated stream: missing 4-byte header size.");
            }

            hSize = BinaryPrimitives.ReadUInt32LittleEndian(hSizeBytes);
        }
        else
        {
            Span<byte> hSizeBytes = stackalloc byte[2];
            if (reader.Read(hSizeBytes) != 2)
            {
                throw new PgfFormatException("Truncated stream: missing 2-byte header size.");
            }

            hSize = BinaryPrimitives.ReadUInt16LittleEndian(hSizeBytes);
        }

        PgfPreHeader preHeader = new(versionFlags, hSize);

        // CDecoder reads min(hSize, HeaderSize) bytes into the header struct, leaving any remaining
        // (never-actually-possible-for-real-files, hSize < HeaderSize) fields at their
        // default-constructed zero - PGFHeader's default ctor zeroes everything.
        Span<byte> headerBytes = stackalloc byte[PgfConstants.HeaderSize];
        headerBytes.Clear();
        int headerBytesToRead = (int)Math.Min(hSize, PgfConstants.HeaderSize);
        if (reader.Read(headerBytes[..headerBytesToRead]) != headerBytesToRead)
        {
            throw new PgfFormatException("Truncated stream: missing header fields.");
        }

        PgfHeader header = new(
            Width: BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[0..4]),
            Height: BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[4..8]),
            NLevels: headerBytes[8],
            Quality: headerBytes[9],
            Bpp: headerBytes[10],
            Channels: headerBytes[11],
            Mode: headerBytes[12],
            UsedBitsPerChannel: headerBytes[13],
            VersionNumberRaw: BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[14..16]));

        uint[] levelLengths = [];
        byte[]? colorTable = null;
        PgfUserData userData = PgfUserData.None;

        // CDecoder: "be ready to read all versions including version 0" - version 0 (a malformed/
        // placeholder pre-header byte, not a real historical PGF version) skips post-header and
        // level-length entirely; every real file has version > 0.
        if ((byte)versionFlags > 0)
        {
            uint postHeaderSize = hSize > PgfConstants.HeaderSize ? hSize - PgfConstants.HeaderSize : 0;

            // PGFPostHeader ::= [ColorTable] [UserData] (Encoder.cpp:38) - color table always comes
            // first when present. pgf-all-image-modes.md Stage 1: this port now reads it instead of
            // rejecting the mode outright.
            if (header.Mode == PgfConstants.ImageModeIndexedColor)
            {
                if (postHeaderSize < PgfConstants.ColorTableSize)
                {
                    throw new PgfFormatException("Indexed-color PGF header is missing its color table.");
                }

                colorTable = new byte[PgfConstants.ColorTableSize];
                if (reader.Read(colorTable) != PgfConstants.ColorTableSize)
                {
                    throw new PgfFormatException("Truncated stream: missing color table.");
                }

                postHeaderSize -= PgfConstants.ColorTableSize;
            }

            if (postHeaderSize > 0)
            {
                // Whatever remains is user data (Decoder.cpp:143-186's read/skip logic).
                //
                // Goal 3's untrusted-length defense: postHeaderSize is derived from hSize, a
                // header-declared field a general (non-digiKam) caller cannot trust. Bound it
                // against the stream's real remaining length before caching anything - PgfMemoryReader.
                // Read already truncates safely rather than overrunning the buffer, but without this
                // check a corrupted/malicious declared length would either (a) attempt an
                // unnecessarily huge allocation for CacheAll, or (b) silently "succeed" having cached
                // fewer bytes than userDataLen claims, indistinguishable from a well-formed short
                // read. Fail closed instead, matching this codec's existing Tier 5 philosophy.
                long remaining = reader.Length - reader.Position;
                if (postHeaderSize > remaining)
                {
                    throw new PgfFormatException(
                        $"User data length {postHeaderSize} exceeds remaining stream length {remaining}.");
                }

                uint userDataLen = postHeaderSize;

                if (policy == PgfUserDataPolicy.Skip)
                {
                    reader.SetPos(SeekOrigin.Current, userDataLen);
                    userData = new PgfUserData([], userDataLen);
                }
                else
                {
                    uint cachedLen = policy == PgfUserDataPolicy.CachePrefix
                        ? Math.Min(userDataLen, prefixSize)
                        : userDataLen;

                    byte[] cached = new byte[cachedLen];
                    if (reader.Read(cached) != cachedLen)
                    {
                        throw new PgfFormatException("Truncated stream: missing user data.");
                    }

                    if (cachedLen < userDataLen)
                    {
                        // CachePrefix cached fewer bytes than the file actually has - skip the rest
                        // to land at the correct level-length offset (Decoder.cpp's own Skip(size -
                        // cachedUserDataLen)). Already bounded above, so this SetPos cannot fail.
                        reader.SetPos(SeekOrigin.Current, userDataLen - cachedLen);
                    }

                    userData = new PgfUserData(cached, userDataLen);
                }
            }

            levelLengths = new uint[header.NLevels];
            Span<byte> levelLengthBytes = stackalloc byte[4];
            for (int i = 0; i < header.NLevels; i++)
            {
                if (reader.Read(levelLengthBytes) != 4)
                {
                    throw new PgfFormatException("Truncated stream: missing level-length entry.");
                }

                levelLengths[i] = BinaryPrimitives.ReadUInt32LittleEndian(levelLengthBytes);
            }
        }

        return (preHeader, header, levelLengths, colorTable, userData);
    }

    /// <summary>Writes pre-header, header, an optional <paramref name="colorTable"/>, optional
    /// <paramref name="userData"/> (pgf-user-data-and-small-images.md Stage 2 - previously this port
    /// never wrote any, matching <c>pgf_encode_bgra_alloc</c>'s own scope, which also never passed
    /// <c>SetHeader</c> any), and a zeroed level-length placeholder array - matching the
    /// header-writing portion of <c>CEncoder</c>'s constructor plus <c>WriteLevelLength</c>
    /// (Encoder.cpp:70,112-128,177) and <c>SetHeader</c>'s own userData copy (PGFimage.cpp:940-947).
    /// <c>PGFPostHeader ::= [ColorTable] [UserData]</c> (Encoder.cpp:38) - color table always
    /// precedes user data when both are present, matching <see cref="Read"/>'s own read order.
    ///
    /// pgf-real-level-lengths.md Stage 1/2: the level-length placeholder written here is the same
    /// byte range <c>WriteLevelLength</c> reserves in the native (resolved Open Question 1 - this
    /// method already zero-fills exactly that range, so no second placeholder write exists anywhere
    /// in this port). <see cref="PgfImageEncoder"/> patches real accumulated values into it after
    /// encoding finishes (<c>UpdateLevelLength</c>'s own seek-write-restore, Encoder.cpp:202-234) -
    /// this method's own <see langword="long"/> return value is that patch's seek target: the stream
    /// position where the placeholder begins.</summary>
    public static long Write(
        PgfByteWriter writer, PgfHeader header, ReadOnlySpan<byte> colorTable = default, ReadOnlySpan<byte> userData = default,
        bool roi = false)
    {
        bool hasColorTable = header.Mode == PgfConstants.ImageModeIndexedColor && !colorTable.IsEmpty;
        if (hasColorTable && colorTable.Length != PgfConstants.ColorTableSize)
        {
            throw new ArgumentException($"Color table must be exactly {PgfConstants.ColorTableSize} bytes.", nameof(colorTable));
        }

        uint hSize = PgfConstants.HeaderSize + (hasColorTable ? (uint)PgfConstants.ColorTableSize : 0) + (uint)userData.Length;

        // pgf-roi-support.md Goal 3: PGFROI is the one version-flag bit this port's encoder can now
        // set, opting a file into the tile-structured ROI encoding scheme (CPGFImage::SetHeader,
        // PGFimage.cpp:905, writes PGFVersion | flags the same way).
        PgfVersionFlags versionFlags = PgfConstants.EncoderVersionFlags | (roi ? PgfVersionFlags.PGFROI : PgfVersionFlags.None);

        Span<byte> preHeaderBytes = stackalloc byte[PgfConstants.PreHeaderSize];
        PgfConstants.Magic.CopyTo(preHeaderBytes);
        preHeaderBytes[3] = (byte)versionFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(preHeaderBytes[4..8], hSize);
        writer.Write(preHeaderBytes);

        Span<byte> headerBytes = stackalloc byte[PgfConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[0..4], header.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[4..8], header.Height);
        headerBytes[8] = header.NLevels;
        headerBytes[9] = header.Quality;
        headerBytes[10] = header.Bpp;
        headerBytes[11] = header.Channels;
        headerBytes[12] = header.Mode;
        headerBytes[13] = header.UsedBitsPerChannel;
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes[14..16], header.VersionNumberRaw);
        writer.Write(headerBytes);

        if (hasColorTable)
        {
            writer.Write(colorTable);
        }

        if (!userData.IsEmpty)
        {
            writer.Write(userData);
        }

        long levelLengthPos = writer.Position;

        Span<byte> zero = stackalloc byte[4];
        for (int i = 0; i < header.NLevels; i++)
        {
            writer.Write(zero);
        }

        return levelLengthPos;
    }

    /// <summary>Builds a complete <see cref="PgfHeader"/> for a fresh BGRA encode, replicating
    /// <c>CPGFImage::SetHeader</c>+<c>CompleteHeader</c>+<c>ComputeLevels</c> for this port's
    /// original scope (32bpp RGBA). <paramref name="quality"/> must already be validated by the
    /// caller (0..<see cref="PgfConstants.MaxQuality"/>) - this mirrors <c>SetHeader</c>'s own
    /// precondition, not a defensive re-check.</summary>
    public static PgfHeader CreateForEncode(uint width, uint height, byte quality) =>
        CreateForMode(width, height, quality, PgfConstants.ImageModeRGBA);

    /// <summary>pgf-all-image-modes.md Stage 1: the general form of <see cref="CreateForEncode"/>,
    /// for any mode <see cref="PgfModeInfo.TryGetBppAndChannels"/> covers - same
    /// <c>SetHeader</c>+<c>CompleteHeader</c>+<c>ComputeLevels</c> replication, with bpp/channels/
    /// usedBitsPerChannel sourced from <see cref="PgfModeInfo"/> instead of hardcoded to RGBA's.
    /// Throws for a mode this port doesn't cover - a genuine caller error, not malformed input (see
    /// <see cref="Read"/>'s fail-closed convention for the untrusted-input case).</summary>
    public static PgfHeader CreateForMode(uint width, uint height, byte quality, byte mode)
    {
        if (!PgfModeInfo.TryGetBppAndChannels(mode, out byte bpp, out byte channels))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported image mode.");
        }

        byte nLevels = ComputeLevels(width, height);
        ushort versionNumber = PgfHeader.PackVersionNumber(PgfConstants.CodecMajor, PgfConstants.CodecYear, PgfConstants.CodecWeek);

        return new PgfHeader(
            Width: width,
            Height: height,
            NLevels: nLevels,
            Quality: quality,
            Bpp: bpp,
            Channels: channels,
            Mode: mode,
            UsedBitsPerChannel: PgfModeInfo.UsedBitsPerChannel(bpp, channels),
            VersionNumberRaw: versionNumber);
    }

    /// <summary>Direct port of <c>CPGFImage::ComputeLevels</c> (PGFimage.cpp) for the "no
    /// caller-requested level count" case (this port always lets the codec pick, matching
    /// <c>pgf_encode_bgra_alloc</c>'s <c>header.nLevels = 0</c> input) - the auto-selection branch
    /// only, not the "shrink an already-valid requested count" branch (unreachable when the input
    /// is always invalid/zero).</summary>
    public static byte ComputeLevels(uint width, uint height)
    {
        const int maxThumbnailWidth = 20 * PgfConstants.FilterSize;
        uint m = Math.Min(width, height);

        int nLevels = 1;
        uint s = m;
        while (s > maxThumbnailWidth)
        {
            nLevels++;
            s >>= 1;
        }

        int levels = nLevels;
        s = PgfConstants.FilterSize * (1u << levels);
        while (m < s)
        {
            levels--;
            s >>= 1;
        }

        if (levels > PgfConstants.MaxLevel)
        {
            return PgfConstants.MaxLevel;
        }

        if (levels < 0)
        {
            return 0;
        }

        return (byte)levels;
    }
}
