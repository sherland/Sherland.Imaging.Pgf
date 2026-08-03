namespace PictTag.PgfCodec;

/// <summary>Shared by <see cref="PgfImageDecoder"/> and <see cref="PgfProgressiveDecoder"/> - matches
/// <c>PictTag.Data.PgfDecoding.PgfDecoder.DecodedCallback{TResult}</c>'s exact shape (managed-pgf-
/// codec.md's "Decode public API mirrors the existing shape"): the decoded BGRA buffer is only ever
/// exposed as a <see cref="ReadOnlySpan{T}"/> over a pooled (<see cref="System.Buffers.ArrayPool{T}"/>)
/// array, valid only for the duration of this callback, never handed back as an owned array.</summary>
public delegate TResult PgfDecodedCallback<out TResult>(ReadOnlySpan<byte> bgra, int width, int height);

/// <summary>
/// Shared decode-setup logic factored out of <see cref="PgfImageDecoder"/> (single-shot) and
/// <see cref="PgfProgressiveDecoder"/> (level-by-level, Stage 9) once both needed the identical
/// header-parse-plus-channel-allocation preamble: direct equivalent of
/// <c>CPGFImage::Open</c> (PGFimage.cpp:141) up to (not including) any actual level decoding.
///
/// pgf-all-image-modes.md: generalized from a hardcoded 4-channel/RGBA-only shape to
/// <see cref="PgfHeader.Channels"/>-many channels for any mode <see cref="PgfImageDecoder"/>'s own
/// mode dispatch covers - channel 0 is always full resolution; channels 1..N-1 are downsampled
/// (chroma-subsampled) only when <see cref="PgfModeInfo.SupportsDownsample"/> says the mode's own
/// real behavior downsamples them (PGFimage.cpp:921-927), not unconditionally the way the old
/// RGBA-only version assumed.
/// </summary>
internal sealed class PgfDecodeSession
{
    private PgfDecodeSession(
        PgfWaveletTransform[] channels, PgfDecoderCore decoder, int quant, bool downsample,
        int fullWidth, int fullHeight, int chromaWidth, byte levels, byte mode, byte[]? colorTable,
        PgfUserData userData)
    {
        Channels = channels;
        Decoder = decoder;
        Quant = quant;
        Downsample = downsample;
        FullWidth = fullWidth;
        FullHeight = fullHeight;
        ChromaWidth = chromaWidth;
        Levels = levels;
        Mode = mode;
        ColorTable = colorTable;
        UserData = userData;
    }

    /// <summary>The mode's channels' wavelet pyramids, in the header's own channel order (e.g. Y, U,
    /// V, A for RGBA; a single channel for GrayScale/IndexedColor). <see cref="Channels"/>[1..] are
    /// pre-sized to <see cref="ChromaWidth"/> x its matching height when <see cref="Downsample"/> is
    /// true, matching <c>CPGFImage::Open</c>'s own per-channel width/height setup
    /// (PGFimage.cpp:176-187).</summary>
    public PgfWaveletTransform[] Channels { get; }

    /// <summary>The single shared bitstream-reading state every channel's subbands are decoded
    /// through, in the channel-interleaved-per-level order the real bitstream uses (see
    /// <see cref="PgfImageDecoder"/>'s doc comment).</summary>
    public PgfDecoderCore Decoder { get; }

    public int Quant { get; }

    public bool Downsample { get; }

    public int FullWidth { get; }

    public int FullHeight { get; }

    public int ChromaWidth { get; }

    public byte Levels { get; }

    public byte Mode { get; }

    /// <summary>Raw 1024-byte RGBQUAD color table (<see cref="PgfHeaderIO.Read"/>'s own shape) when
    /// <see cref="Mode"/> is <see cref="PgfConstants.ImageModeIndexedColor"/>, otherwise
    /// <see langword="null"/>.</summary>
    public byte[]? ColorTable { get; }

    /// <summary>The file's post-header user data (pgf-user-data-and-small-images.md Goal 1), read
    /// according to whatever <see cref="PgfUserDataPolicy"/> <see cref="TryOpen"/> was called
    /// with.</summary>
    public PgfUserData UserData { get; }

    public static PgfDecodeSession? TryOpen(
        ReadOnlyMemory<byte> pgfData, PgfUserDataPolicy userDataPolicy = PgfUserDataPolicy.CacheAll, uint userDataPrefixSize = 0)
    {
        try
        {
            PgfMemoryReader reader = new(pgfData);
            (_, PgfHeader header, _, byte[]? colorTable, PgfUserData userData) =
                PgfHeaderIO.Read(reader, userDataPolicy, userDataPrefixSize);

            if (!PgfImageDecoder.IsModeSupported(header.Mode) ||
                !PgfModeInfo.TryGetBppAndChannels(header.Mode, out byte expectedBpp, out byte expectedChannels) ||
                header.Channels != expectedChannels || header.Bpp != expectedBpp)
            {
                // Fail closed rather than silently mis-decoding a mode this port doesn't (yet) model
                // - matches managed-pgf-codec.md's original RGBA-only guard, now mode-generic.
                return null;
            }

            if (header.NLevels == 0)
            {
                // The wavelet-transform-free "raw" path (CPGFImage::Open's nLevels==0 branch) -
                // deliberately out of scope, unreachable for any real image (min(width,height) >= 10
                // per this port's own encoder guard) - see managed-pgf-codec.md's scope notes.
                return null;
            }

            // pgf-user-data-and-small-images.md Goal 3's "second look" at the rest of the
            // header-parsing code, beyond user data specifically: Width/Height are just as
            // untrusted-declared as any post-header length for a general (non-digiKam) caller, and
            // the original unconditional `checked((int)header.Width)` cast threw an uncaught
            // OverflowException - confirmed empirically, not assumed - for any header declaring a
            // width/height that doesn't fit in an int (e.g. 0xFFFFFFFF), propagating straight past
            // every catch block in this call chain instead of failing closed. Explicit range checks
            // first avoid the throw entirely.
            if (header.Width == 0 || header.Height == 0 || header.Width > int.MaxValue || header.Height > int.MaxValue)
            {
                return null;
            }

            int fullWidth = (int)header.Width;
            int fullHeight = (int)header.Height;

            // A second, related overflow: FullWidth/FullHeight individually fit in an int here, but
            // PgfImageDecoder.TryDecode/PgfProgressiveDecoder.TryDecodeLevel each later compute
            // `checked(width * height * 4)` for the decoded BGRA buffer size - confirmed reachable
            // (e.g. 100000x100000, unremarkable individually) to overflow *that* multiplication and
            // throw uncaught there instead. Bounding it once here means neither downstream
            // `checked(...)` can ever actually overflow - failing closed at the single point that
            // already parses the header, rather than duplicating this check in every consumer.
            if ((long)fullWidth * fullHeight > int.MaxValue / 4)
            {
                return null;
            }

            // Matches CPGFImage::Open's interpretation of m_header.quality (PGFimage.cpp:161-174):
            // downsampling only kicks in above DownsampleThreshold, and (pgf-all-image-modes.md) only
            // for modes SetHeader's own mode list actually downsamples (PGFimage.cpp:921-927).
            bool downsample = header.Quality > PgfConstants.DownsampleThreshold && PgfModeInfo.SupportsDownsample(header.Mode);
            int quant = downsample ? header.Quality - 1 : header.Quality;

            int chromaWidth = downsample ? (fullWidth + 1) / 2 : fullWidth;
            int chromaHeight = downsample ? (fullHeight + 1) / 2 : fullHeight;

            PgfWaveletTransform[] channels = new PgfWaveletTransform[header.Channels];
            channels[0] = new PgfWaveletTransform(fullWidth, fullHeight, header.NLevels);
            for (int c = 1; c < header.Channels; c++)
            {
                channels[c] = new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels);
            }

            PgfDecoderCore decoder = new(reader);

            return new PgfDecodeSession(
                channels, decoder, quant, downsample, fullWidth, fullHeight, chromaWidth, header.NLevels, header.Mode, colorTable,
                userData);
        }
        catch (PgfFormatException)
        {
            return null;
        }
        catch (PgfStreamException)
        {
            return null;
        }
    }

    /// <summary>Decodes every subband at <paramref name="level"/> for every channel, then
    /// inverse-transforms all of them - direct port of one iteration of <c>CPGFImage::Read</c>'s loop
    /// body (PGFimage.cpp:428-460). Returns the per-channel <c>(data, width, height)</c> the caller
    /// needs for color conversion, or <see langword="null"/> on an inverse-transform allocation
    /// failure or malformed/truncated bitstream data (<see cref="PgfDecoderCore"/>'s macroblock reads
    /// throw <see cref="PgfFormatException"/>/<see cref="PgfStreamException"/> for the latter - caught
    /// here, alongside <see cref="TryOpen"/>'s header-parse try/catch, so every throwing path in this
    /// session's lifetime fails closed the same way, matching managed-pgf-codec.md's Tier 5).
    /// Shared by <see cref="PgfImageDecoder"/> (calls this once per level down to 0) and
    /// <see cref="PgfProgressiveDecoder"/> (calls this once per level down to whatever level the
    /// caller most recently requested).</summary>
    public (int[] Data, int Width, int Height)[]? DecodeOneLevel(int level)
    {
        try
        {
            for (int c = 0; c < Channels.Length; c++)
            {
                PgfWaveletTransform wt = Channels[c];
                if (level == Levels)
                {
                    wt.GetSubband(level, PgfSubbandOrientation.Ll).PlaceTile(Decoder, Quant);
                }

                wt.GetSubband(level, PgfSubbandOrientation.Hl).PlaceTile(Decoder, Quant);
                wt.GetSubband(level, PgfSubbandOrientation.Lh).PlaceTile(Decoder, Quant);
                wt.GetSubband(level, PgfSubbandOrientation.Hh).PlaceTile(Decoder, Quant);
            }

            (int[] Data, int Width, int Height)[] result = new (int[], int, int)[Channels.Length];
            for (int c = 0; c < Channels.Length; c++)
            {
                PgfCodecError err = Channels[c].InverseTransform(level, out int w, out int h, out int[] data);
                if (err != PgfCodecError.None)
                {
                    return null;
                }

                result[c] = (data, w, h);
            }

            return result;
        }
        catch (PgfFormatException)
        {
            return null;
        }
        catch (PgfStreamException)
        {
            return null;
        }
    }
}
