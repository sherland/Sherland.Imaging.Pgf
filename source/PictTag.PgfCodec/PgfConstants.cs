namespace PictTag.PgfCodec;

/// <summary>
/// Numeric constants mirrored directly from <c>PGFtypes.h</c>/<c>PGFplatform.h</c>/
/// <c>WaveletTransform.h</c>, for this build's configuration specifically.
///
/// <b>Correction (pgf-all-image-modes.md, pre-Stage-1 verification):</b> earlier docs/comments in
/// this codebase (including <c>managed-pgf-codec.md</c>'s own "Traps confirmed in the real code")
/// claimed <c>__PGF32SUPPORT__</c> was not defined here, reasoning only from
/// <c>CMakeLists.txt</c> never defining it directly. That's wrong: <c>PGFplatform.h:66-67</c>
/// defines <c>__PGF32SUPPORT__</c> <i>by default</i> (<c>#ifndef NPGF32</c>), and
/// <c>CMakeLists.txt</c> never defines <c>NPGF32</c> either, so the real compiled oracle DLL has
/// it active: <c>DataT = INT32</c> (<c>PGFtypes.h:273</c>), <c>MaxBitPlanes = 31</c>. This was
/// never caught by the RGBA-only test suite because 8-bit-per-channel data never approaches
/// <c>Int16</c>'s range: it stopped being silent once pgf-all-image-modes.md's 16-bit-per-channel
/// groups (Gray16/Lab48/RGB48/CMYK64) needed it. <c>DataT</c> is <see cref="int"/> in this port,
/// matching the real oracle build.
/// </summary>
internal static class PgfConstants
{
    /// <summary>"PGF" - the 3-byte magic every PGF file starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "PGF"u8;

    public const int MagicVersionSize = 4; // magic[3] + version byte
    public const int PreHeaderSize = 8; // MagicVersionSize + hSize (UINT32, since Version6)
    public const int HeaderSize = 16; // width,height,nLevels,quality,bpp,channels,mode,usedBitsPerChannel,version
    public const int ColorTableLen = 256;
    public const int ColorTableSize = ColorTableLen * 4; // RGBQUAD = 4 bytes each

    public const int MaxLevel = 30;
    public const int MaxBitPlanes = 31; // 32 minus sign bit, __PGF32SUPPORT__ build (see class doc comment)
    public const int MaxBitPlanesLog = 5; // bits needed to encode MaxBitPlanes
    public const int MaxQuality = MaxBitPlanes;
    public const int DownsampleThreshold = 3;

    /// <summary>Larger of FilterSizeL(5)/FilterSizeH(3) - WaveletTransform.h.</summary>
    public const int FilterSize = 5;

    /// <summary>Must be a multiple of WordWidth(32), &lt;= UINT16_MAX - PGFtypes.h. One macroblock's
    /// entire input (encoded bitstream, in 32-bit words) and output (decoded coefficients) capacity.</summary>
    public const int BufferSize = 16384;

    /// <summary>Side length of a coefficient block in an LL or HH subband - Partition's tiling unit.</summary>
    public const int LinBlockSize = 8;

    /// <summary>Side length of a coefficient block in an HL or LH subband - DecodeInterleaved's
    /// tiling unit (DecodeInterleaved itself is not ported - see PgfMacroBlock's doc comment).</summary>
    public const int InterBlockSize = 4;

    /// <summary>Bit-length of the run-length block-size field within the bitplane coding scheme
    /// (&lt; 16, ld(BufferSize) &lt; RLblockSizeLen &lt;= 2*ld(BufferSize)) - PGFtypes.h.</summary>
    public const int RLblockSizeLen = 15;

    /// <summary>Max length of a run-length-encoded block: <c>(1 &lt;&lt; RLblockSizeLen) - 1</c>.</summary>
    public const int MaxCodeLen = (1 << RLblockSizeLen) - 1;

    /// <summary>Number of subbands per wavelet transform level (LL, HL, LH, HH).</summary>
    public const int NSubbands = 4;

    public const byte ImageModeIndexedColor = 2;
    public const byte ImageModeRGBA = 17;
    public const byte ImageModeUnknown = 255;

    /// <summary>This codec's fixed major/year/week build identifiers (PGFtypes.h:
    /// <c>PGFMajorNumber=7, PGFYear=19, PGFWeek=3</c>) - what <c>CompleteHeader()</c> always writes
    /// into a fresh <c>PGFHeader.version</c>, regardless of what's actually being encoded.</summary>
    public const byte CodecMajor = 7;
    public const byte CodecYear = 19;
    public const byte CodecWeek = 3;

    /// <summary>The exact <see cref="PgfVersionFlags"/> this port's encoder always writes:
    /// <c>PGF32</c> included (pgf-all-image-modes.md's DataT correction - the real build's
    /// <c>PGFVersion</c> constant, PGFtypes.h:76, includes it, and <c>CPGFImage::SetHeader</c>,
    /// PGFimage.cpp:905, writes <c>PGFVersion | flags</c> into every real file's preheader), no
    /// <c>PGFROI</c> (this port never emits ROI-flagged files - see managed-pgf-codec.md's "ROI is
    /// always compiled in" decode-side trap; encode simply never sets the bit). The bit itself only
    /// ever feeds <c>ChannelDepth()</c>/<c>MaxChannelDepth()</c> (PGFimage.h:406,518), a reporting
    /// accessor never called from the shim or this port's decode path, so this is metadata
    /// faithfulness, not a functional decode/encode dependency.</summary>
    public const PgfVersionFlags EncoderVersionFlags =
        PgfVersionFlags.Version2 | PgfVersionFlags.PGF32 | PgfVersionFlags.Version5 |
        PgfVersionFlags.Version6 | PgfVersionFlags.Version7;
}
