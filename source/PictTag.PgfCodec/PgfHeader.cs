using System.Buffers.Binary;

namespace PictTag.PgfCodec;

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
    /// <summary>Reads pre-header, header, and level-length array from the current stream position -
    /// the same sequence <c>CDecoder</c>'s constructor reads (minus macroblock/thread setup, which
    /// belongs to Stage 5's entropy decoder, not header parsing).</summary>
    public static (PgfPreHeader PreHeader, PgfHeader Header, uint[] LevelLengths) Read(PgfMemoryReader reader)
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

        // CDecoder: "be ready to read all versions including version 0" - version 0 (a malformed/
        // placeholder pre-header byte, not a real historical PGF version) skips post-header and
        // level-length entirely; every real file has version > 0.
        if ((byte)versionFlags > 0)
        {
            if (header.Mode == PgfConstants.ImageModeIndexedColor)
            {
                // Out of scope (managed-pgf-codec.md Non-goals): real digiKam thumbnails are always
                // RGBA. Fail closed rather than silently mis-parsing a color table this port never
                // models.
                throw new PgfFormatException("Indexed-color PGF images are not supported.");
            }

            uint postHeaderSize = hSize > PgfConstants.HeaderSize ? hSize - PgfConstants.HeaderSize : 0;
            if (postHeaderSize > 0)
            {
                // No color table possible here (rejected above) - the entire remainder is user
                // data/metadata, which this port has no use for. Skip it, matching CDecoder's own
                // UP_Skip policy path, to land at the correct level-length offset.
                reader.SetPos(SeekOrigin.Current, postHeaderSize);
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

        return (preHeader, header, levelLengths);
    }

    /// <summary>Writes pre-header, header, and a zeroed level-length placeholder array - matching
    /// the header-writing portion of <c>CEncoder</c>'s constructor plus <c>WriteLevelLength</c>
    /// (Encoder.cpp:70,177). Unlike the original, the placeholder is never patched with real values
    /// afterward (no <c>UpdateLevelLength</c> equivalent) - see <see cref="PgfImageEncoder"/>'s doc
    /// comment for why that's a deliberate, permanent scope cut rather than unfinished work: nothing
    /// in this codebase's real decode path ever reads level lengths.
    ///
    /// Scope limitation, deliberate: always writes <c>hSize = HeaderSize</c> (no color table, no
    /// user data) - this port never emits either, matching <c>pgf_encode_bgra_alloc</c>'s own
    /// scope (BGRA input only, no metadata parameter).</summary>
    public static void Write(PgfByteWriter writer, PgfHeader header)
    {
        Span<byte> preHeaderBytes = stackalloc byte[PgfConstants.PreHeaderSize];
        PgfConstants.Magic.CopyTo(preHeaderBytes);
        preHeaderBytes[3] = (byte)PgfConstants.EncoderVersionFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(preHeaderBytes[4..8], PgfConstants.HeaderSize);
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

        Span<byte> zero = stackalloc byte[4];
        for (int i = 0; i < header.NLevels; i++)
        {
            writer.Write(zero);
        }
    }

    /// <summary>Builds a complete <see cref="PgfHeader"/> for a fresh BGRA encode, replicating
    /// <c>CPGFImage::SetHeader</c>+<c>CompleteHeader</c>+<c>ComputeLevels</c> for this port's exact
    /// scope (32bpp RGBA, no color table, no user data). <paramref name="quality"/> must already be
    /// validated by the caller (0..<see cref="PgfConstants.MaxQuality"/>) - this mirrors
    /// <c>SetHeader</c>'s own precondition, not a defensive re-check.</summary>
    public static PgfHeader CreateForEncode(uint width, uint height, byte quality)
    {
        byte nLevels = ComputeLevels(width, height);
        ushort versionNumber = PgfHeader.PackVersionNumber(PgfConstants.CodecMajor, PgfConstants.CodecYear, PgfConstants.CodecWeek);

        return new PgfHeader(
            Width: width,
            Height: height,
            NLevels: nLevels,
            Quality: quality,
            Bpp: 32,
            Channels: 4,
            Mode: PgfConstants.ImageModeRGBA,
            UsedBitsPerChannel: 8,
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
