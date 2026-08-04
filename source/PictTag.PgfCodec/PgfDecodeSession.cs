using System.Buffers.Binary;

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
        PgfUserData userData, (int[] Data, int Width, int Height)[]? rawChannelData, bool roiSupported,
        bool version5, bool version7, uint[] levelLengths)
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
        RawChannelData = rawChannelData;
        RoiSupported = roiSupported;
        Version5 = version5;
        Version7 = version7;
        LevelLengths = levelLengths;
    }

    /// <summary>Mirrors <c>CPGFImage::ROIisSupported</c> (PGFimage.h:466) - whether this file's own
    /// preheader version flags declare <see cref="PgfVersionFlags.PGFROI"/>. <see cref="PgfProgressiveDecoder.TrySetRoi"/>
    /// checks this and fails closed rather than silently falling back to a plain (non-cropped) decode
    /// the way the native's own <c>Read(rect,...)</c> does when <c>!ROIisSupported()</c> - a caller
    /// that explicitly asked for ROI decoding on a file that can't do it should get a clear "no", not
    /// a surprising full decode.</summary>
    public bool RoiSupported { get; }

    /// <summary>Whether the file's own preheader declares Version5. This is deliberately exposed
    /// alongside <see cref="Version7"/> rather than inferred from the major-version field: native
    /// <c>CPGFImage::Read</c> branches on this bit for the HL/LH entropy layout, and legacy Bitmap
    /// conversion also needs it to distinguish the pre-Version5 <c>yw=w2</c> row stride from the
    /// Version5/6 <c>yw=w</c> form (pgf-bitmap-legacy-packed.md Stage 2).</summary>
    public bool Version5 { get; }

    /// <summary>Whether the file's own preheader declares Version7, the native Bitmap representation
    /// boundary. Its absence selects the historical packed-byte color-conversion path; it says
    /// nothing by itself about whether Version5's tiled entropy layout applies, hence the separate
    /// <see cref="Version5"/> property (pgf-bitmap-legacy-packed.md Stage 2).</summary>
    public bool Version7 { get; }

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

    /// <summary>pgf-user-data-and-small-images.md Goal 4: non-<see langword="null"/> only when
    /// <see cref="Levels"/> is 0 - the wavelet-transform-free "raw/uncoded" path
    /// (<c>CPGFImage::Open</c>'s <c>nLevels==0</c> branch, PGFimage.cpp:198-214) for images too small
    /// to build even one wavelet level. Unlike the normal path, the native codec reads this data
    /// synchronously during <c>Open()</c> itself, not lazily during <c>Read(level)</c> - this port
    /// mirrors that timing exactly (populated here, in <see cref="TryOpen"/>, not in
    /// <see cref="DecodeOneLevel"/>), so both <see cref="PgfImageDecoder"/> and
    /// <see cref="PgfProgressiveDecoder"/> can treat "level 0" as already-available data rather than
    /// something to decode.</summary>
    public (int[] Data, int Width, int Height)[]? RawChannelData { get; }

    /// <summary>The file's real per-level byte-length table, in on-wire/native order (index 0 =
    /// coarsest level - <see cref="PgfEncoderCore.LevelLength"/>'s own doc comment) - already parsed
    /// correctly by <see cref="PgfHeaderIO.Read"/> regardless of whether anything ever consumes it
    /// (it has to skip past this section to reach whatever follows either way), previously discarded
    /// here. <see cref="PgfProgressiveDecoder.TryGetLevelLength"/> is the public accessor built on
    /// top of this. pgf-real-level-lengths.md Stage 3/Goal 2.</summary>
    public uint[] LevelLengths { get; }

    public static PgfDecodeSession? TryOpen(
        ReadOnlyMemory<byte> pgfData, PgfUserDataPolicy userDataPolicy = PgfUserDataPolicy.CacheAll, uint userDataPrefixSize = 0)
    {
        try
        {
            PgfMemoryReader reader = new(pgfData);
            (PgfPreHeader preHeader, PgfHeader header, uint[] levelLengths, byte[]? colorTable, PgfUserData userData) =
                PgfHeaderIO.Read(reader, userDataPolicy, userDataPrefixSize);

            bool roiSupported = (preHeader.VersionFlags & PgfVersionFlags.PGFROI) == PgfVersionFlags.PGFROI;
            bool version5 = (preHeader.VersionFlags & PgfVersionFlags.Version5) == PgfVersionFlags.Version5;
            bool version7 = (preHeader.VersionFlags & PgfVersionFlags.Version7) == PgfVersionFlags.Version7;

            if (!PgfImageDecoder.IsModeSupported(header.Mode) ||
                !PgfModeInfo.TryGetBppAndChannels(header.Mode, out byte expectedBpp, out byte expectedChannels) ||
                header.Channels != expectedChannels || header.Bpp != expectedBpp)
            {
                // Fail closed rather than silently mis-decoding a mode this port doesn't (yet) model
                // - matches managed-pgf-codec.md's original RGBA-only guard, now mode-generic.
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

            if (header.NLevels == 0)
            {
                // The wavelet-transform-free "raw" path (CPGFImage::Open's nLevels==0 branch,
                // PGFimage.cpp:198-214) - too small for even one wavelet level
                // (TestBitmaps.MinimumSupportedDimension). Read directly here (matching the native
                // codec's own Open()-time timing, not Read(level)'s) rather than building a
                // PgfWaveletTransform/PgfDecoderCore neither the raw format nor this path uses.
                (int[] Data, int Width, int Height)[]? rawChannelData =
                    TryReadRawChannels(reader, header.Channels, fullWidth, fullHeight, chromaWidth, chromaHeight);
                if (rawChannelData is null)
                {
                    return null;
                }

                return new PgfDecodeSession(
                    [], new PgfDecoderCore(reader), quant, downsample, fullWidth, fullHeight, chromaWidth, header.NLevels,
                    header.Mode, colorTable, userData, rawChannelData, roiSupported, version5, version7, levelLengths);
            }

            PgfWaveletTransform[] channels = new PgfWaveletTransform[header.Channels];
            channels[0] = new PgfWaveletTransform(fullWidth, fullHeight, header.NLevels);
            for (int c = 1; c < header.Channels; c++)
            {
                channels[c] = new PgfWaveletTransform(chromaWidth, chromaHeight, header.NLevels);
            }

            PgfDecoderCore decoder = new(reader);

            return new PgfDecodeSession(
                channels, decoder, quant, downsample, fullWidth, fullHeight, chromaWidth, header.NLevels, header.Mode, colorTable,
                userData, rawChannelData: null, roiSupported, version5, version7, levelLengths);
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

    /// <summary>Direct port of <c>CPGFImage::Open</c>'s <c>nLevels==0</c> read loop
    /// (PGFimage.cpp:198-214): each channel's raw <c>DataT</c> (<see cref="int"/> in this port -
    /// <c>PgfConstants</c>' own doc comment) coefficients, already color-converted (YUV-space) but
    /// never wavelet-transformed or quantized (there is no forward transform to quantize the output
    /// of, on this path), read sequentially - one whole channel's array, then the next, not
    /// interleaved the way the leveled bitstream is. Channel 0 is always <paramref name="fullWidth"/>
    /// x <paramref name="fullHeight"/>; channels 1..N-1 use <paramref name="chromaWidth"/>/
    /// <paramref name="chromaHeight"/> instead when the caller's downsample decision applies -
    /// confirmed directly against the native source that this decision is made identically regardless
    /// of <c>nLevels</c> (PGFimage.cpp:175-187, evaluated before the <c>nLevels</c> branch), so a
    /// downsample-eligible mode still spatially subsamples chroma even on this otherwise-lossless
    /// path. Returns <see langword="null"/> on a truncated stream (this port's own fail-closed
    /// convention), rather than the native's own <c>ReturnWithError(MissingData)</c> throw.</summary>
    private static (int[] Data, int Width, int Height)[]? TryReadRawChannels(
        PgfMemoryReader reader, byte channelCount, int fullWidth, int fullHeight, int chromaWidth, int chromaHeight)
    {
        (int[] Data, int Width, int Height)[] result = new (int[], int, int)[channelCount];
        Span<byte> valueBytes = stackalloc byte[4];

        for (int c = 0; c < channelCount; c++)
        {
            int width = c == 0 ? fullWidth : chromaWidth;
            int height = c == 0 ? fullHeight : chromaHeight;
            int size = checked(width * height);

            int[] data = new int[size];
            for (int i = 0; i < size; i++)
            {
                if (reader.Read(valueBytes) != 4)
                {
                    return null;
                }

                data[i] = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
            }

            result[c] = (data, width, height);
        }

        return result;
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

            return InverseTransformAllChannels(level);
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

    /// <summary>Direct port of <c>CPGFImage::SetROI</c> (PGFimage.cpp:614-631) - enables ROI decoding
    /// on <see cref="Decoder"/> and computes tile-index geometry for every channel's
    /// <see cref="PgfWaveletTransform"/>, halving <paramref name="roi"/> for chroma channels exactly
    /// when <see cref="Downsample"/> applies (channel 0 always gets the unhalved rect, matching the
    /// native's own unconditional <c>m_wtChannel[0]-&gt;SetROI(rect)</c> before the downsample
    /// check). Must be called before the first <see cref="DecodeOneLevelRoi"/> call - see
    /// <see cref="PgfProgressiveDecoder.TrySetRoi"/>'s doc comment for this port's one-ROI-per-session
    /// divergence from the native's more general re-readable-with-a-new-ROI model.</summary>
    public void SetRoi(PgfRoi roi)
    {
        Decoder.SetRoi();
        Channels[0].SetROI(roi);

        PgfRoi chromaRoi = roi;
        if (Downsample && Channels.Length > 1)
        {
            chromaRoi = new PgfRoi(roi.Left >> 1, roi.Top >> 1, (roi.Right + 1) >> 1, (roi.Bottom + 1) >> 1);
        }

        for (int c = 1; c < Channels.Length; c++)
        {
            Channels[c].SetROI(chromaRoi);
        }
    }

    /// <summary>Direct port of one iteration of <c>CPGFImage::Read(rect,...)</c>'s ROI loop body
    /// (PGFimage.cpp:519-547) - the tile-aware sibling of <see cref="DecodeOneLevel"/>: per channel,
    /// places the coarsest level's LL band directly (tile=false, exactly like the non-ROI path -
    /// there's only ever one tile there), then for every tile at this level either decodes it
    /// (<see cref="PgfWaveletTransform.TileIsRelevant"/>) or skips its encoded bytes
    /// (<see cref="PgfDecoderCore.SkipTileBuffer"/>) without decoding. Requires <see cref="SetRoi"/>
    /// to have been called first.</summary>
    public (int[] Data, int Width, int Height)[]? DecodeOneLevelRoi(int level)
    {
        try
        {
            for (int c = 0; c < Channels.Length; c++)
            {
                PgfWaveletTransform wt = Channels[c];
                int nTiles = wt.GetNofTiles(level);

                if (level == Levels)
                {
                    Decoder.GetNextMacroBlock();
                    wt.GetSubband(level, PgfSubbandOrientation.Ll).PlaceTile(Decoder, Quant);
                }

                for (int tileY = 0; tileY < nTiles; tileY++)
                {
                    for (int tileX = 0; tileX < nTiles; tileX++)
                    {
                        if (wt.TileIsRelevant(level, tileX, tileY))
                        {
                            Decoder.GetNextMacroBlock();
                            wt.GetSubband(level, PgfSubbandOrientation.Hl).PlaceTile(Decoder, Quant, tile: true, tileX, tileY);
                            wt.GetSubband(level, PgfSubbandOrientation.Lh).PlaceTile(Decoder, Quant, tile: true, tileX, tileY);
                            wt.GetSubband(level, PgfSubbandOrientation.Hh).PlaceTile(Decoder, Quant, tile: true, tileX, tileY);
                        }
                        else
                        {
                            Decoder.SkipTileBuffer();
                        }
                    }
                }
            }

            return InverseTransformAllChannels(level);
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

    private (int[] Data, int Width, int Height)[]? InverseTransformAllChannels(int level)
    {
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
}
