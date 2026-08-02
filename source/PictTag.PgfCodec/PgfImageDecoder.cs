namespace PictTag.PgfCodec;

/// <summary>
/// Single-shot managed decode: PGF bytes to a BGRA buffer, direct equivalent of
/// <c>CPGFImage::Open</c> + <c>Read(0)</c> + <c>GetBitmap</c> (PGFimage.cpp:141, 402, 1788) chained
/// together, RGBA/32bpp only (managed-pgf-codec.md's Non-goals: indexed color already fails during
/// header parse, and every real digiKam thumbnail is RGBA anyway). Progressive/level-by-level decode
/// (mirroring <c>Open</c>+repeated <c>Read(level)</c>) is Stage 9 - this type always decodes straight
/// through to level 0.
///
/// Fails closed (returns <see langword="false"/>, never throws) on any malformed/truncated input,
/// matching the native shim's own <c>catch (...) { return false; }</c> boundary
/// (managed-pgf-codec.md Tier 5) - every throwing path in <see cref="PgfHeaderIO"/>/
/// <see cref="PgfMemoryReader"/>/<see cref="PgfDecoderCore"/> is a real-but-invalid-input signal, not
/// a programming error, so it's caught here rather than left to propagate.
/// </summary>
internal static class PgfImageDecoder
{
    public static bool TryDecode(ReadOnlyMemory<byte> pgfData, out byte[]? bgra, out int width, out int height)
    {
        bgra = null;
        width = height = 0;

        try
        {
            PgfMemoryReader reader = new(pgfData);
            (_, PgfHeader header, _) = PgfHeaderIO.Read(reader);

            if (header.Mode != PgfConstants.ImageModeRGBA || header.Channels != 4 || header.Bpp != 32)
            {
                // Out of scope (managed-pgf-codec.md Non-goals) - real digiKam thumbnails are always
                // RGBA; fail closed rather than silently mis-decoding a mode this port never models.
                return false;
            }

            if (header.NLevels == 0)
            {
                // The wavelet-transform-free "raw" path (CPGFImage::Open's nLevels==0 branch) -
                // deliberately out of scope, unreachable for any real image (min(width,height) >= 10
                // per this port's own encoder guard) - see managed-pgf-codec.md's scope notes.
                return false;
            }

            int fullWidth = checked((int)header.Width);
            int fullHeight = checked((int)header.Height);
            if (fullWidth <= 0 || fullHeight <= 0)
            {
                return false;
            }

            // Matches CPGFImage::Open's interpretation of m_header.quality (PGFimage.cpp:161-174):
            // downsampling only kicks in above DownsampleThreshold, and only then does m_quant drop
            // by one relative to the stored quality value.
            bool downsample = header.Quality > PgfConstants.DownsampleThreshold;
            int quant = downsample ? header.Quality - 1 : header.Quality;

            int chromaWidth = downsample ? (fullWidth + 1) / 2 : fullWidth;
            int chromaHeight = downsample ? (fullHeight + 1) / 2 : fullHeight;

            PgfWaveletTransform[] channels =
            [
                new PgfWaveletTransform(fullWidth, fullHeight, header.NLevels),
                new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels),
                new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels),
                new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels),
            ];

            PgfDecoderCore decoder = new(reader);
            short[][] channelData = new short[4][];

            // Direct port of CPGFImage::Read's non-ROI, non-interleaved (Version5+) loop
            // (PGFimage.cpp:428-475): entropy-decode all 4 channels' subbands at the current level
            // before inverse-transforming any of them - the bitstream interleaves channels
            // level-by-level, not channel-by-channel, so this ordering is load-bearing, not
            // cosmetic.
            for (int currentLevel = header.NLevels; currentLevel > 0; currentLevel--)
            {
                for (int c = 0; c < 4; c++)
                {
                    PgfWaveletTransform wt = channels[c];
                    if (currentLevel == header.NLevels)
                    {
                        wt.GetSubband(currentLevel, PgfSubbandOrientation.Ll).PlaceTile(decoder, quant);
                    }

                    wt.GetSubband(currentLevel, PgfSubbandOrientation.Hl).PlaceTile(decoder, quant);
                    wt.GetSubband(currentLevel, PgfSubbandOrientation.Lh).PlaceTile(decoder, quant);
                    wt.GetSubband(currentLevel, PgfSubbandOrientation.Hh).PlaceTile(decoder, quant);
                }

                for (int c = 0; c < 4; c++)
                {
                    PgfCodecError err = channels[c].InverseTransform(currentLevel, out _, out _, out short[] data);
                    if (err != PgfCodecError.None)
                    {
                        return false;
                    }

                    channelData[c] = data;
                }
            }

            byte[] output = new byte[checked(fullWidth * fullHeight * 4)];
            PgfColorConversion.DecodeYuvaToBgra(
                channelData[0], channelData[1], channelData[2], channelData[3],
                fullWidth, fullHeight, chromaWidth, downsample, output);

            bgra = output;
            width = fullWidth;
            height = fullHeight;
            return true;
        }
        catch (PgfFormatException)
        {
            return false;
        }
        catch (PgfStreamException)
        {
            return false;
        }
    }
}
