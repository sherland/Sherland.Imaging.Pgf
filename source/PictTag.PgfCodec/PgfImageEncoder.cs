namespace PictTag.PgfCodec;

/// <summary>
/// Single-shot managed encode: a BGRA buffer to PGF bytes, direct equivalent of
/// <c>CPGFImage::SetHeader</c> + <c>WriteHeader</c> + <c>WriteImage</c> (PGFimage.cpp:893, 978, 1149)
/// chained together, RGBA/32bpp only - the algebraic mirror of <see cref="PgfImageDecoder"/>, and
/// matching <c>pgf_encode_bgra_alloc</c>'s own scope exactly (BGRA input only, no metadata, no color
/// table, `nLevels` always auto-computed).
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
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        pgfBytes = null;

        if (width <= 0 || height <= 0 || quality > PgfConstants.MaxQuality)
        {
            return false;
        }

        if (bgra.Length != checked(width * height * 4))
        {
            return false;
        }

        PgfHeader header = PgfHeaderIO.CreateForEncode((uint)width, (uint)height, quality);
        if (header.NLevels == 0)
        {
            // The wavelet-transform-free "raw" path - out of scope, matching the native shim's own
            // MinimumSupportedDimension guard (managed-pgf-codec.md's scope notes).
            return false;
        }

        bool downsample = quality > PgfConstants.DownsampleThreshold;
        int quant = downsample ? quality - 1 : quality;

        short[] y = new short[width * height];
        short[] u = new short[width * height];
        short[] v = new short[width * height];
        short[] a = new short[width * height];
        PgfColorConversion.EncodeBgraToYuva(bgra, width, height, y, u, v, a);

        int chromaWidth = width;
        int chromaHeight = height;
        if (downsample)
        {
            (chromaWidth, chromaHeight) = PgfColorConversion.Downsample(u, width, height);
            PgfColorConversion.Downsample(v, width, height);
            PgfColorConversion.Downsample(a, width, height);
        }

        int chromaSize = chromaWidth * chromaHeight;
        PgfWaveletTransform[] channels =
        [
            new PgfWaveletTransform(width, height, header.NLevels, y),
            new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels, u[..chromaSize]),
            new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels, v[..chromaSize]),
            new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels, a[..chromaSize]),
        ];

        // Direct port of WriteHeader's per-channel forward-transform loop (PGFimage.cpp:1016-1019):
        // every level of every channel is transformed before any entropy encoding starts.
        for (int c = 0; c < 4; c++)
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
        PgfHeaderIO.Write(writer, header);

        PgfEncoderCore encoder = new(writer);

        int totalLevels = header.NLevels;
        int levelsCompleted = 0;

        // Direct port of CPGFImage::WriteImage's level loop (PGFimage.cpp:1182) calling WriteLevel
        // (PGFimage.cpp:1104-1118): all 4 channels' subbands at the current level, extracted
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

            for (int c = 0; c < 4; c++)
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
