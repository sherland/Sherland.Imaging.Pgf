namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of the <c>ROIBlockHeader</c> union (<c>PGFtypes.h:184-206</c>) - a 16-bit value
/// carrying two bitfields on the non-big-endian branch (<c>PGF_USE_BIG_ENDIAN</c> is never defined
/// on this host, see <see cref="PgfConstants"/>'s doc comment for the equivalent
/// endianness-branch-selection reasoning): the low <see cref="PgfConstants.RLblockSizeLen"/> (15)
/// bits are <see cref="BufferSize"/> (how many logical values this macroblock holds - always the
/// full <see cref="PgfConstants.BufferSize"/> outside ROI mode, but a real tile-boundary-truncated
/// count once ROI tiling is active - <c>pgf-roi-support.md</c> Goal 3), and the top bit is
/// <see cref="TileEnd"/> (true only on the last macroblock of a tile's data, consumed by
/// <c>CDecoder::SkipTileBuffer</c>).
///
/// Written/read on the wire only when ROI is actually enabled on the encoder/decoder
/// (<c>Encoder.cpp</c>/<c>Decoder.cpp</c>'s own <c>if (m_roi)</c> guards around the extra 2 bytes) -
/// this type itself is just the value; wire (de)serialization is <c>PgfDecoderCore</c>/
/// <c>PgfEncoderCore</c>'s job.
/// </summary>
internal readonly record struct PgfRoiBlockHeader
{
    private const int TileEndBit = PgfConstants.RLblockSizeLen; // bit 15
    private const uint BufferSizeMask = (1u << TileEndBit) - 1; // 0x7FFF

    /// <summary>Raw 16-bit union value - mirrors <c>ROIBlockHeader::val</c>.</summary>
    public ushort Value { get; }

    /// <summary>Mirrors <c>ROIBlockHeader(UINT16 v)</c> - construct directly from a raw value read
    /// off the wire.</summary>
    public PgfRoiBlockHeader(ushort value) => Value = value;

    /// <summary>Mirrors <c>ROIBlockHeader(UINT32 size, bool end)</c> - construct from the two
    /// logical fields (every real call site in this port uses this form, not the raw one, since
    /// nothing yet reads a ROI-flagged file off the wire).</summary>
    public PgfRoiBlockHeader(uint bufferSize, bool tileEnd)
    {
        Value = (ushort)(bufferSize | (tileEnd ? 1u << TileEndBit : 0));
    }

    /// <summary>Mirrors <c>ROIBlockHeader::rbh.bufferSize</c> - number of logical values (not
    /// encoded bytes - that's the separate <c>wordLen</c> prefix) this macroblock holds.</summary>
    public uint BufferSize => Value & BufferSizeMask;

    /// <summary>Mirrors <c>ROIBlockHeader::rbh.tileEnd</c> - true for the last macroblock of a
    /// tile's data (or, outside ROI mode, the single final macroblock of the whole image).</summary>
    public bool TileEnd => (Value & (1u << TileEndBit)) != 0;
}
