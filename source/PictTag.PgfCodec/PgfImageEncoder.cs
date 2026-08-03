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
    public static bool TryEncode(
        ReadOnlySpan<byte> bgra, int width, int height, byte quality, out byte[]? pgfBytes,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        TryEncodeMode(bgra, width, height, quality, PgfConstants.ImageModeRGBA, out pgfBytes, colorTable: default, progress, cancellationToken);

    /// <summary>General form of <see cref="TryEncode"/>, for any mode <see cref="PgfImageDecoder.
    /// IsModeSupported"/> covers - test infrastructure only (Goal 2), used to produce real fixtures
    /// to decode-test against (no real digiKam thumbnail uses anything but RGBA). <paramref
    /// name="source"/> must be tightly packed at that mode's own real bytes-per-pixel (no padding,
    /// identity channel order - same "every real caller uses the identity map, pitch = width*bpp/8"
    /// contract <see cref="PgfColorConversion"/>'s own doc comment establishes for RGBA, extended to
    /// every mode). <paramref name="colorTable"/> is required (and only meaningful) for
    /// <see cref="PgfConstants.ImageModeIndexedColor"/>.</summary>
    public static bool TryEncodeMode(
        ReadOnlySpan<byte> source, int width, int height, byte quality, byte mode, out byte[]? pgfBytes,
        ReadOnlySpan<byte> colorTable = default, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
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

        if (source.Length != checked(width * height * (bpp / 8)))
        {
            return false;
        }

        PgfHeader header = PgfHeaderIO.CreateForMode((uint)width, (uint)height, quality, mode);
        if (header.NLevels == 0)
        {
            // The wavelet-transform-free "raw" path - out of scope, matching the native shim's own
            // MinimumSupportedDimension guard (managed-pgf-codec.md's scope notes).
            return false;
        }

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

        int chromaSize = chromaWidth * chromaHeight;
        PgfWaveletTransform[] channels = new PgfWaveletTransform[channelCount];
        channels[0] = new PgfWaveletTransform(width, height, header.NLevels, channelBuffers[0]);
        for (int c = 1; c < channelCount; c++)
        {
            int[] buffer = downsample ? channelBuffers[c][..chromaSize] : channelBuffers[c];
            channels[c] = new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels, buffer);
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
        PgfHeaderIO.Write(writer, header, colorTable);

        PgfEncoderCore encoder = new(writer);

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
}
