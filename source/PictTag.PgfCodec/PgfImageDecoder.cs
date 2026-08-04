using System.Buffers;

namespace PictTag.PgfCodec;

/// <summary>
/// Single-shot managed decode: PGF bytes to a BGRA buffer, direct equivalent of
/// <c>CPGFImage::Open</c> + <c>Read(0)</c> + <c>GetBitmap</c> (PGFimage.cpp:141, 402, 1788) chained
/// together. pgf-all-image-modes.md: covers every mode <see cref="IsModeSupported"/> lists (grows as
/// that PRD's remaining groups land), not just RGBA - <see cref="ConvertToBgra"/> is the one mode
/// dispatch point. <see cref="PgfProgressiveDecoder"/> (Stage 9) is the level-by-level equivalent,
/// mirroring <c>Open</c>+repeated <c>Read(level)</c> - this type always decodes straight through to
/// level 0 in one call, sharing <see cref="PgfDecodeSession"/>'s setup/per-level decode logic with it.
///
/// Stage 10: <see cref="TryDecode{TResult}"/> writes into an <see cref="ArrayPool{T}"/>-rented buffer
/// and hands it to <paramref name="onDecoded"/>-in-<see cref="TryDecode{TResult}"/> as a
/// <see cref="ReadOnlySpan{T}"/>, never an owned array - matching
/// <c>PictTag.Data.PgfDecoding.PgfDecoder.TryDecode</c>'s existing shape exactly (managed-pgf-codec.md's
/// "Decode public API mirrors the existing shape" architecture note), so Stage 12's call-site swap is
/// a mechanical facade change, not a redesign.
///
/// Fails closed (returns <see langword="false"/>, never throws) on any malformed/truncated input,
/// matching the native shim's own <c>catch (...) { return false; }</c> boundary
/// (managed-pgf-codec.md Tier 5) - every throwing path in <see cref="PgfHeaderIO"/>/
/// <see cref="PgfMemoryReader"/>/<see cref="PgfDecoderCore"/> is a real-but-invalid-input signal, not
/// a programming error, so it's caught inside <see cref="PgfDecodeSession"/> rather than left to
/// propagate.
///
/// pgf-cancellation-and-progress.md: <paramref name="progress"/> reports once per level actually
/// decoded (an area-weighted fraction, see <see cref="PgfProgressCurve"/>, since each level covers 4x
/// the previous level's linear coverage); <paramref name="cancellationToken"/> is checked once per
/// level, before that level's work starts, and surfaces as <see cref="OperationCanceledException"/> -
/// a deliberate departure from this method's usual fail-closed-return-false convention, since
/// cancellation is caller-requested, not a malformed-input failure mode.
/// </summary>
public static class PgfImageDecoder
{
    /// <summary>The modes <see cref="ConvertToBgra"/> knows how to turn into BGRA32 output - grows
    /// as pgf-all-image-modes.md's remaining groups land. Checked by <see cref="PgfDecodeSession.
    /// TryOpen"/> before allocating anything, so an unsupported (but otherwise well-formed) mode
    /// fails closed at header-parse time, matching this port's original RGBA-only guard.</summary>
    internal static bool IsModeSupported(byte mode) => mode switch
    {
        PgfConstants.ImageModeRGBA => true,
        PgfConstants.ImageModeCMYKColor => true,
        PgfConstants.ImageModeGrayScale => true,
        PgfConstants.ImageModeIndexedColor => true,
        PgfConstants.ImageModeHSLColor => true,
        PgfConstants.ImageModeHSBColor => true,
        PgfConstants.ImageModeRGBColor => true,
        PgfConstants.ImageModeLabColor => true,
        PgfConstants.ImageModeGray16 => true,
        PgfConstants.ImageModeLab48 => true,
        PgfConstants.ImageModeRGB48 => true,
        PgfConstants.ImageModeCMYK64 => true,
        PgfConstants.ImageModeGray32 => true,
        PgfConstants.ImageModeBitmap => true,
        PgfConstants.ImageModeRGB12 => true,
        PgfConstants.ImageModeRGB16 => true,
        _ => false,
    };

    /// <summary>Mode dispatch for the final coefficients-to-BGRA32 step (PRD Goal 1: output is always
    /// BGRA32 regardless of source mode) - the one place a new <see cref="IsModeSupported"/> mode
    /// needs a case added. Shared with <see cref="PgfProgressiveDecoder"/> (same assembly).
    ///
    /// Deliberately reads width/height/chromaWidth from <paramref name="channelData"/> itself
    /// (channel 0's own <c>Width</c>/<c>Height</c>, channel 1's own <c>Width</c> when a mode needs a
    /// chroma width) rather than <paramref name="session"/>'s <c>FullWidth</c>/<c>FullHeight</c>/
    /// <c>ChromaWidth</c> - those are level-0-only, but <see cref="PgfProgressiveDecoder"/> calls this
    /// once per (possibly coarser) level, where the real per-level dimensions live only in
    /// <paramref name="channelData"/>.</summary>
    internal static void ConvertToBgra(PgfDecodeSession session, (int[] Data, int Width, int Height)[] channelData, Span<byte> bgra)
    {
        int width = channelData[0].Width;
        int height = channelData[0].Height;

        switch (session.Mode)
        {
            // CMYKColor shares RGBA's exact GetBitmap case block (PGFimage.cpp:2232-2233) - not a
            // real CMYK colorimetric transform, the native codec's own established (if loose)
            // treatment of a 4th channel as alpha-like regardless of what it represents, confirmed
            // directly against the source, not assumed - see PgfColorConversion's class doc comment.
            case PgfConstants.ImageModeRGBA:
            case PgfConstants.ImageModeCMYKColor:
                PgfColorConversion.DecodeYuvaToBgra(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data, channelData[3].Data,
                    width, height, channelData[1].Width, session.Downsample, bgra);
                break;
            case PgfConstants.ImageModeGrayScale:
                PgfColorConversion.DecodeYuvOffsetToGray(channelData[0].Data, width, height, bgra);
                break;
            case PgfConstants.ImageModeIndexedColor:
                PgfColorConversion.DecodeYuvOffsetToIndexed(channelData[0].Data, width, height, session.ColorTable!, bgra);
                break;
            case PgfConstants.ImageModeHSLColor:
            case PgfConstants.ImageModeHSBColor:
                PgfColorConversion.DecodeYuvOffsetToTripleChannel(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data, width, height, bgra);
                break;
            case PgfConstants.ImageModeRGBColor:
                PgfColorConversion.DecodeYuvToBgra(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data,
                    width, height, channelData[1].Width, session.Downsample, bgra);
                break;
            case PgfConstants.ImageModeLabColor:
                PgfColorConversion.DecodeYuvOffsetToTripleChannelWithUpsample(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data,
                    width, height, channelData[1].Width, session.Downsample, bgra);
                break;
            case PgfConstants.ImageModeGray16:
                PgfColorConversion.DecodeYuvOffset16ToGray(channelData[0].Data, width, height, bgra);
                break;
            case PgfConstants.ImageModeLab48:
                PgfColorConversion.DecodeYuvOffset16ToTripleChannelWithUpsample(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data,
                    width, height, channelData[1].Width, session.Downsample, bgra);
                break;
            case PgfConstants.ImageModeRGB48:
                PgfColorConversion.DecodeYuv48ToBgra(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data,
                    width, height, channelData[1].Width, session.Downsample, bgra);
                break;
            case PgfConstants.ImageModeCMYK64:
                PgfColorConversion.DecodeYuv64ToBgra(
                    channelData[0].Data, channelData[1].Data, channelData[2].Data, channelData[3].Data,
                    width, height, channelData[1].Width, session.Downsample, bgra);
                break;
            case PgfConstants.ImageModeGray32:
                PgfColorConversion.DecodeYuvOffset31ToGray(channelData[0].Data, width, height, bgra);
                break;
            case PgfConstants.ImageModeBitmap:
                if (session.Version7)
                {
                    PgfColorConversion.DecodeYToBitmapBgra(channelData[0].Data, width, height, bgra);
                }
                else
                {
                    PgfColorConversion.DecodeLegacyPackedBitmapToBgra(
                        channelData[0].Data, width, height, session.Version5, bgra);
                }
                break;
            case PgfConstants.ImageModeRGB12:
                PgfColorConversion.DecodeYuvToRgb12Bgra(channelData[0].Data, channelData[1].Data, channelData[2].Data, width, height, bgra);
                break;
            case PgfConstants.ImageModeRGB16:
                PgfColorConversion.DecodeYuvToRgb16Bgra(channelData[0].Data, channelData[1].Data, channelData[2].Data, width, height, bgra);
                break;
            default:
                throw new InvalidOperationException($"Unreachable: IsModeSupported should have rejected mode {session.Mode} before this point.");
        }
    }

    public static bool TryDecode<TResult>(
        ReadOnlyMemory<byte> pgfData, PgfDecodedCallback<TResult> onDecoded, out TResult? result,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        TryDecode(pgfData, onDecoded, out result, out _, progress: progress, cancellationToken: cancellationToken);

    /// <summary>pgf-user-data-and-small-images.md Goal 1/2: same decode as the simpler overload above,
    /// plus the file's post-header user data. A separate overload rather than widening that
    /// signature in place - C# forbids a required parameter (an <see langword="out"/> parameter can't
    /// have a default value) after optional ones, and every existing call site (this codebase's own
    /// facade plus every test) must keep compiling and behaving unchanged.
    /// <paramref name="userDataPolicy"/>/<paramref name="userDataPrefixSize"/> default to caching
    /// everything, matching <c>ConfigureDecoder</c>'s own default - a caller that doesn't care about
    /// user data pays nothing extra to ignore <paramref name="userData"/>.</summary>
    public static bool TryDecode<TResult>(
        ReadOnlyMemory<byte> pgfData, PgfDecodedCallback<TResult> onDecoded, out TResult? result, out PgfUserData userData,
        PgfUserDataPolicy userDataPolicy = PgfUserDataPolicy.CacheAll, uint userDataPrefixSize = 0,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        result = default;
        userData = PgfUserData.None;

        PgfDecodeSession? session = PgfDecodeSession.TryOpen(pgfData, userDataPolicy, userDataPrefixSize);
        if (session is null)
        {
            return false;
        }

        userData = session.UserData;

        // pgf-user-data-and-small-images.md Goal 4: nLevels=0 files have their raw channel data
        // already fully available from PgfDecodeSession.TryOpen (mirroring the native codec's own
        // Open()-time read, CPGFImage::Read's nLevels==0 branch being a no-op at level 0 - PGFimage.
        // cpp:415-422) - the level loop below naturally runs zero times when session.Levels == 0, so
        // channelData just needs to start out already populated with it instead of an empty array.
        (int[] Data, int Width, int Height)[] channelData =
            session.RawChannelData ?? new (int[], int, int)[session.Channels.Length];

        int totalLevels = session.Levels;
        int levelsCompleted = 0;

        // Direct port of CPGFImage::Read's non-ROI, non-interleaved (Version5+) loop
        // (PGFimage.cpp:428-475): entropy-decode all 4 channels' subbands at the current level
        // before inverse-transforming any of them - the bitstream interleaves channels
        // level-by-level, not channel-by-channel, so this ordering is load-bearing, not cosmetic.
        //
        // Cancellation is checked once per level, before that level's work starts (pgf-
        // cancellation-and-progress.md Goal 3/architecture note) - an already-decoded level is never
        // thrown away by a cancellation that arrives just as it finishes.
        for (int currentLevel = session.Levels; currentLevel > 0; currentLevel--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (int[] Data, int Width, int Height)[]? decoded = session.DecodeOneLevel(currentLevel);
            if (decoded is null)
            {
                return false;
            }

            channelData = decoded;
            levelsCompleted++;
            progress?.Report(PgfProgressCurve.FractionAfter(levelsCompleted, totalLevels));
        }

        if (session.Levels == 0)
        {
            // Matches CPGFImage::Read's own nLevels==0 branch (PGFimage.cpp:415-422): the callback
            // fires once, at 1.0, since the data was already read during Open() - there's no
            // incremental level work left to report progress across.
            progress?.Report(1.0);
        }

        int bufferSize = checked(session.FullWidth * session.FullHeight * 4);
        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            ConvertToBgra(session, channelData, rented.AsSpan(0, bufferSize));

            result = onDecoded(rented.AsSpan(0, bufferSize), session.FullWidth, session.FullHeight);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
