using System.Buffers.Binary;

namespace PictTag.PgfCodec;

/// <summary>
/// Single-shot managed encode: a source byte buffer to PGF bytes, direct equivalent of
/// <c>CPGFImage::SetHeader</c> + <c>WriteHeader</c> + <c>WriteImage</c> (PGFimage.cpp:893, 978, 1149)
/// chained together - the algebraic mirror of <see cref="PgfImageDecoder"/>. <see cref="TryEncode"/>
/// (BGRA/RGBA/32bpp) matches <c>pgf_encode_bgra_alloc</c>'s own scope exactly and is the only
/// production-relevant shape (no metadata, no color table, `nLevels` always auto-computed).
/// pgf-all-image-modes.md: <see cref="TryEncodeMode"/> generalizes it to every mode
/// <see cref="PgfImageDecoder.IsModeSupported"/> covers - test infrastructure (Goal 2), not a second
/// production path.
///
/// <b>Level-length bytes are always written as zero placeholders, never patched with real values</b>
/// (unlike <c>CEncoder::UpdateLevelLength</c>) - a deliberate scope cut, not an oversight. Grepping
/// every real consumer in this codebase (<c>CPGFImage::Read</c>/<c>GetBitmap</c>, this port's own
/// <see cref="PgfImageDecoder"/>, and digiKam's own documented call sequence in shim.cpp's header
/// comment) confirms level lengths are read only by <c>CPGFImage::ReadEncodedData</c> - a
/// raw-bytes-without-decoding extraction utility nothing in this codebase's scope calls - and
/// <c>Decoder.cpp`'s own comment states outright that "level length information is optional." Getting
/// the real values byte-exact would require porting <c>CEncoder</c>'s macroblock/level-boundary
/// deferred-accounting (a value spans macroblocks that don't align with level boundaries) for a field
/// nothing decodes or asserts on - this PRD's "decode-correctness, not bitstream-identity" guarantee
/// (see managed-pgf-codec.md) explicitly does not require byte-identical files anyway.
///
/// pgf-cancellation-and-progress.md: <c>progress</c>/<c>cancellationToken</c>
/// mirror <see cref="PgfImageDecoder.TryDecode{TResult}"/>'s own semantics exactly (once per level,
/// area-weighted fraction, <see cref="OperationCanceledException"/> on cancellation) - ported for
/// completeness/symmetry, not because a production caller exists yet (this type has none, see
/// managed-pgf-codec.md's Non-goals).
/// </summary>
internal static class PgfImageEncoder
{
    /// <summary>pgf-user-data-and-small-images.md Stage 2: <paramref name="userData"/> is optional
    /// caller-supplied metadata written into the encoded file's post-header (empty by default -
    /// every existing call site keeps compiling and behaving unchanged). Round-trips byte-exact
    /// through <see cref="PgfHeaderIO.Read"/> on the decode side.</summary>
    public static bool TryEncode(
        ReadOnlySpan<byte> bgra, int width, int height, byte quality, out byte[]? pgfBytes,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default, ReadOnlySpan<byte> userData = default,
        bool roi = false) =>
        TryEncodeMode(bgra, width, height, quality, PgfConstants.ImageModeRGBA, out pgfBytes, colorTable: default, progress, cancellationToken, userData, roi);

    /// <summary>General form of <see cref="TryEncode"/>, for any mode <see cref="PgfImageDecoder.
    /// IsModeSupported"/> covers - test infrastructure only (Goal 2), used to produce real fixtures
    /// to decode-test against (no real digiKam thumbnail uses anything but RGBA). <paramref
    /// name="source"/> must be tightly packed at that mode's own real bytes-per-pixel (no padding,
    /// identity channel order - same "every real caller uses the identity map, pitch = width*bpp/8"
    /// contract <see cref="PgfColorConversion"/>'s own doc comment establishes for RGBA, extended to
    /// every mode). <paramref name="colorTable"/> is required (and only meaningful) for
    /// <see cref="PgfConstants.ImageModeIndexedColor"/>. <paramref name="userData"/>: see
    /// <see cref="TryEncode"/>'s own doc comment.</summary>
    /// <summary><paramref name="roi"/> (pgf-roi-support.md Goal 2): opts into producing an
    /// ROI-flagged, tile-structured file (setting <see cref="PgfVersionFlags.PGFROI"/>) - every tile
    /// still gets encoded either way (see <c>WriteLevel</c>'s own ROI branch, PGFimage.cpp:1067-1119:
    /// unconditional, no relevance check on the encode side), so this is purely a bitstream-layout
    /// and version-flag choice, not "encode only part of the image." Ignored (no effect) when
    /// <paramref name="mode"/>'s header ends up with <see cref="PgfHeader.NLevels"/> 0 - the
    /// wavelet-transform-free raw path has no tiles to structure at all.</summary>
    public static bool TryEncodeMode(
        ReadOnlySpan<byte> source, int width, int height, byte quality, byte mode, out byte[]? pgfBytes,
        ReadOnlySpan<byte> colorTable = default, IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        ReadOnlySpan<byte> userData = default, bool roi = false)
    {
        pgfBytes = null;

        if (!PgfModeInfo.TryGetBppAndChannels(mode, out byte bpp, out byte channelCount))
        {
            return false;
        }

        if (width <= 0 || height <= 0 || quality > PgfConstants.MaxQuality)
        {
            return false;
        }

        if (source.Length != PgfModeInfo.ExpectedSourceByteLength(mode, bpp, width, height))
        {
            return false;
        }

        PgfHeader header = PgfHeaderIO.CreateForMode((uint)width, (uint)height, quality, mode);

        bool downsample = quality > PgfConstants.DownsampleThreshold && PgfModeInfo.SupportsDownsample(mode);
        int quant = downsample ? quality - 1 : quality;

        int[][] channelBuffers = new int[channelCount][];
        for (int c = 0; c < channelCount; c++)
        {
            channelBuffers[c] = new int[width * height];
        }

        // Mode dispatch for the source-bytes-to-YUV-offset step (PgfColorConversion's own per-group
        // methods) - the one place a new PgfImageDecoder.IsModeSupported mode needs a case added.
        switch (mode)
        {
            // CMYKColor shares RGBA's exact RgbToYuv case block (PGFimage.cpp:1578-1579) - see
            // PgfImageDecoder.ConvertToBgra's matching comment on the decode side.
            case PgfConstants.ImageModeRGBA:
            case PgfConstants.ImageModeCMYKColor:
                PgfColorConversion.EncodeBgraToYuva(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2], channelBuffers[3]);
                break;
            case PgfConstants.ImageModeGrayScale:
            case PgfConstants.ImageModeIndexedColor:
                PgfColorConversion.EncodeSingleChannelToYuvOffset(source, width, height, channelBuffers[0]);
                break;
            case PgfConstants.ImageModeHSLColor:
            case PgfConstants.ImageModeHSBColor:
            case PgfConstants.ImageModeLabColor:
                // LabColor shares Group A's exact RgbToYuv case block (PGFimage.cpp:1445-1473) - it
                // only needs its own dedicated code on the decode side (chroma-upsample bookkeeping,
                // see PgfColorConversion.DecodeYuvOffsetToTripleChannelWithUpsample's doc comment).
                PgfColorConversion.EncodeTripleChannelToYuvOffset(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2]);
                break;
            case PgfConstants.ImageModeRGBColor:
                PgfColorConversion.EncodeRgbToYuv(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2]);
                break;
            case PgfConstants.ImageModeGray16:
                PgfColorConversion.EncodeSingleChannel16ToYuvOffset(source, width, height, channelBuffers[0]);
                break;
            case PgfConstants.ImageModeLab48:
                PgfColorConversion.EncodeTripleChannel16ToYuvOffset(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2]);
                break;
            case PgfConstants.ImageModeRGB48:
                PgfColorConversion.EncodeRgb48ToYuv(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2]);
                break;
            case PgfConstants.ImageModeCMYK64:
                PgfColorConversion.EncodeCmyk64ToYuva(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2], channelBuffers[3]);
                break;
            case PgfConstants.ImageModeGray32:
                PgfColorConversion.EncodeSingleChannel32ToYuvOffset(source, width, height, channelBuffers[0]);
                break;
            case PgfConstants.ImageModeBitmap:
                PgfColorConversion.EncodeBitmapToY(source, width, height, channelBuffers[0]);
                break;
            case PgfConstants.ImageModeRGB12:
                PgfColorConversion.EncodeRgb12ToYuv(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2]);
                break;
            case PgfConstants.ImageModeRGB16:
                PgfColorConversion.EncodeRgb16ToYuv(source, width, height, channelBuffers[0], channelBuffers[1], channelBuffers[2]);
                break;
            default:
                return false;
        }

        int chromaWidth = width;
        int chromaHeight = height;
        if (downsample)
        {
            // Channel 0 (Y/L/gray/index) is never downsampled - only channels 1..N-1, same structure
            // for every downsample-eligible mode (PgfModeInfo.SupportsDownsample's doc comment).
            for (int c = 1; c < channelCount; c++)
            {
                (chromaWidth, chromaHeight) = PgfColorConversion.Downsample(channelBuffers[c], width, height);
            }
        }

        if (header.NLevels == 0)
        {
            // pgf-user-data-and-small-images.md Goal 4/Stage 5: the wavelet-transform-free
            // "raw/uncoded" path (CPGFImage::WriteImage's nLevels==0 branch, PGFimage.cpp:1159-1175)
            // - the color-converted (and, if applicable, chroma-downsampled) coefficients above are
            // written directly, uncoded, instead of being forward-transformed/entropy-encoded at
            // all. Cancellation is checked once, matching the normal path's "once per unit of work"
            // grain even though there's only one unit of work here (no per-level loop to check
            // between iterations of).
            cancellationToken.ThrowIfCancellationRequested();

            PgfByteWriter rawWriter = new();
            PgfHeaderIO.Write(rawWriter, header, colorTable, userData);
            WriteRawChannels(rawWriter, channelBuffers, channelCount, chromaWidth, chromaHeight, downsample);

            // Matches CPGFImage::WriteImage's own nLevels==0 branch (PGFimage.cpp:1170-1173): the
            // callback fires once, at 1.0, since there's no incremental per-level work to report
            // progress across.
            progress?.Report(1.0);

            pgfBytes = rawWriter.WrittenSpan.ToArray();
            return true;
        }

        int chromaSize = chromaWidth * chromaHeight;
        PgfWaveletTransform[] channels = new PgfWaveletTransform[channelCount];
        channels[0] = new PgfWaveletTransform(width, height, header.NLevels, channelBuffers[0]);
        for (int c = 1; c < channelCount; c++)
        {
            int[] buffer = downsample ? channelBuffers[c][..chromaSize] : channelBuffers[c];
            channels[c] = new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels, buffer);
        }

        if (roi)
        {
            // Direct port of WriteHeader's own unconditional per-channel SetROI(fullRect) call
            // (PGFimage.cpp:1011-1013) - always the *whole* channel, regardless of what a real ROI
            // decode request would later ask for (this PRD's own Goal 2 framing: "the whole image is
            // always encoded, just tile-structured"). This is what populates each subband's NTiles
            // (via SetROI's own per-level SetNTiles calls) that the tile-based ExtractTile calls
            // below need - ExtractTile itself never consults TileIsRelevant/AlignedRoi the way
            // decode-side PlaceTile does (this class's own doc comment on the per-tile loop below).
            channels[0].SetROI(new PgfRoi(0, 0, width, height));
            for (int c = 1; c < channelCount; c++)
            {
                channels[c].SetROI(new PgfRoi(0, 0, chromaWidth, chromaHeight));
            }
        }

        // Direct port of WriteHeader's per-channel forward-transform loop (PGFimage.cpp:1016-1019):
        // every level of every channel is transformed before any entropy encoding starts.
        for (int c = 0; c < channelCount; c++)
        {
            for (int level = 0; level < header.NLevels; level++)
            {
                PgfCodecError err = channels[c].ForwardTransform(level, quant);
                if (err != PgfCodecError.None)
                {
                    return false;
                }
            }
        }

        PgfByteWriter writer = new();
        PgfHeaderIO.Write(writer, header, colorTable, userData, roi);

        PgfEncoderCore encoder = new(writer);
        if (roi)
        {
            encoder.SetRoi();
        }

        int totalLevels = header.NLevels;
        int levelsCompleted = 0;

        // Direct port of CPGFImage::WriteImage's level loop (PGFimage.cpp:1182) calling WriteLevel
        // (PGFimage.cpp:1104-1118): every channel's subbands at the current level, extracted
        // (encoded) before moving to the next level - same channel-interleaved-per-level ordering as
        // decode, for the same reason (the bitstream interleaves that way).
        //
        // Cancellation is checked once per level, before that level's work starts (pgf-
        // cancellation-and-progress.md Goal 3/architecture note), mirroring TryDecode's own
        // per-level (not per-channel) grain - the forward-transform pass above isn't where the
        // native callback fires, so it's left uninstrumented.
        for (int currentLevel = header.NLevels; currentLevel > 0; currentLevel--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (roi)
            {
                // Direct port of WriteLevel's ROI branch (PGFimage.cpp:1073-1097). Unlike the plain
                // branch below, LL gets its own explicit EncodeTileBuffer flush (there's only ever
                // one LL "tile," but it still needs to end its own macroblock so the decoder's
                // GetNextMacroBlock/SkipTileBuffer sequencing lines up with the first HL tile that
                // follows), and every HL/LH/HH tile flushes its own macroblock immediately after
                // extraction, tile-end always true - no relevance check on the encode side (every
                // tile is always encoded; see this method's own doc comment on `roi`).
                //
                // CEncoder::SetEncodedLevel (called once per level in the native, on the very last
                // tile of the very last channel) is deliberately not ported: it only feeds
                // m_lastLevelIndex/m_forceWriting, both level-length bookkeeping this port already
                // never tracks (PgfImageEncoder's own class doc comment) and m_forceWriting is only
                // ever read in the multi-macroblock/OpenMP branch this port's build never reaches -
                // a real no-op for this port's scope, not an omission.
                for (int c = 0; c < channelCount; c++)
                {
                    PgfWaveletTransform wt = channels[c];
                    int nTiles = wt.GetNofTiles(currentLevel);

                    if (currentLevel == header.NLevels)
                    {
                        wt.GetSubband(currentLevel, PgfSubbandOrientation.Ll).ExtractTile(encoder);
                        encoder.EncodeTileBuffer();
                    }

                    for (int tileY = 0; tileY < nTiles; tileY++)
                    {
                        for (int tileX = 0; tileX < nTiles; tileX++)
                        {
                            wt.GetSubband(currentLevel, PgfSubbandOrientation.Hl).ExtractTile(encoder, tile: true, tileX, tileY);
                            wt.GetSubband(currentLevel, PgfSubbandOrientation.Lh).ExtractTile(encoder, tile: true, tileX, tileY);
                            wt.GetSubband(currentLevel, PgfSubbandOrientation.Hh).ExtractTile(encoder, tile: true, tileX, tileY);
                            encoder.EncodeTileBuffer();
                        }
                    }
                }

                levelsCompleted++;
                progress?.Report(PgfProgressCurve.FractionAfter(levelsCompleted, totalLevels));
                continue;
            }

            for (int c = 0; c < channelCount; c++)
            {
                PgfWaveletTransform wt = channels[c];
                if (currentLevel == header.NLevels)
                {
                    wt.GetSubband(currentLevel, PgfSubbandOrientation.Ll).ExtractTile(encoder);
                }

                wt.GetSubband(currentLevel, PgfSubbandOrientation.Hl).ExtractTile(encoder);
                wt.GetSubband(currentLevel, PgfSubbandOrientation.Lh).ExtractTile(encoder);
                wt.GetSubband(currentLevel, PgfSubbandOrientation.Hh).ExtractTile(encoder);
            }

            levelsCompleted++;
            progress?.Report(PgfProgressCurve.FractionAfter(levelsCompleted, totalLevels));
        }

        encoder.Flush();

        pgfBytes = writer.WrittenSpan.ToArray();
        return true;
    }

    /// <summary>Direct port of <c>CPGFImage::WriteImage</c>'s <c>nLevels==0</c> write loop
    /// (PGFimage.cpp:1159-1175): each channel's already color-converted (and, if
    /// <paramref name="downsample"/>, chroma-subsampled) coefficients, written sequentially - whole
    /// channel, then the next, matching <see cref="PgfDecodeSession"/>'s decode-side mirror exactly
    /// (same order, same <see cref="int"/>/<c>DataT</c> little-endian representation). Channel 0's
    /// buffer is always tightly sized to the full image (never downsampled), so its own
    /// <c>Length</c> is used directly; channels 1..N-1 write only their first
    /// <paramref name="chromaWidth"/> x <paramref name="chromaHeight"/> values when
    /// <paramref name="downsample"/> (<see cref="PgfColorConversion.Downsample"/> already compacted
    /// them there in place, leaving stale values past that point) - matching the same slicing the
    /// normal <see cref="PgfWaveletTransform"/> construction path already does just above this
    /// method's own call site.</summary>
    private static void WriteRawChannels(
        PgfByteWriter writer, int[][] channelBuffers, int channelCount, int chromaWidth, int chromaHeight, bool downsample)
    {
        Span<byte> valueBytes = stackalloc byte[4];

        for (int c = 0; c < channelCount; c++)
        {
            int[] buffer = channelBuffers[c];
            int size = c == 0 || !downsample ? buffer.Length : chromaWidth * chromaHeight;

            for (int i = 0; i < size; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(valueBytes, buffer[i]);
                writer.Write(valueBytes);
            }
        }
    }
}
