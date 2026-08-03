using System.Buffers;

namespace PictTag.PgfCodec;

/// <summary>
/// Stage 9: progressive/level-by-level managed decode, mirroring
/// <c>PictTag.Data.PgfDecoding.PgfDecoder.ProgressivePgfDecoder</c>'s existing public shape
/// (<see cref="Width"/>/<see cref="Height"/>/<see cref="Levels"/>, <see cref="TryGetLevelSize"/>,
/// <see cref="TryDecodeLevel{TResult}"/> with the same <see cref="PgfDecodedCallback{TResult}"/>
/// delegate pattern) - direct equivalent of <c>CPGFImage::Open</c> + repeated <c>Read(level)</c> +
/// <c>GetBitmap</c> (PGFimage.h: "the current level immediately after Open() is Levels()"), called
/// once per level in strictly decreasing order to render progressively coarse-to-fine without
/// re-decoding earlier levels - the same native semantics shim.cpp's own doc comment on
/// <c>pgf_open</c>/<c>pgf_decode_level_bgra</c> documents.
///
/// Unlike the native wrapper, this type is not <see cref="IDisposable"/>: there is no unmanaged
/// handle to free - every field is a managed array, so ordinary GC is the entire cleanup story. This
/// is a genuine, deliberate simplification a managed port gets for free, not an oversight or a gap
/// versus the native shape being mirrored.
///
/// Stage 10: each <see cref="TryDecodeLevel{TResult}"/> call rents its output buffer from
/// <see cref="ArrayPool{T}"/> for the duration of <paramref name="onDecoded"/>-in-
/// <see cref="TryDecodeLevel{TResult}"/> rather than allocating a fresh array per call - the same
/// pooling <see cref="PgfImageDecoder"/> uses, appropriate here too since a caller rendering a
/// progressive sequence calls this once per level, not once total.
/// </summary>
public sealed class PgfProgressiveDecoder
{
    private readonly PgfDecodeSession session;
    private readonly (int[] Data, int Width, int Height)[] lastDecoded = new (int[], int, int)[4];
    private int currentLevel;

    private PgfProgressiveDecoder(PgfDecodeSession session)
    {
        this.session = session;
        currentLevel = session.Levels;
    }

    /// <summary>Width/Height at level 0 (full resolution) - matches
    /// <c>PgfDecoder.ProgressivePgfDecoder.Width</c>/<c>Height</c>'s own doc comment: the level
    /// dimensions actually available to decode may differ; use <see cref="TryGetLevelSize"/> per
    /// level.</summary>
    public int Width => session.FullWidth;

    public int Height => session.FullHeight;

    public int Levels => session.Levels;

    public static PgfProgressiveDecoder? TryOpen(ReadOnlyMemory<byte> pgfData)
    {
        PgfDecodeSession? session = PgfDecodeSession.TryOpen(pgfData);
        return session is null ? null : new PgfProgressiveDecoder(session);
    }

    /// <summary>Direct port of <c>CPGFImage::Width(level)</c>/<c>Height(level)</c>'s
    /// <c>LevelSizeL</c> formula (PGFimage.h:413,420,499): <c>ceil(size / 2^level)</c>, independent of
    /// how much has actually been decoded so far - matches <c>pgf_level_size</c>'s own only-checks-
    /// <c>level &lt; 0</c> validation (no upper-bound check either) rather than adding a stricter one
    /// this port's own oracle doesn't have.</summary>
    public bool TryGetLevelSize(int level, out int width, out int height)
    {
        if (level < 0)
        {
            width = height = 0;
            return false;
        }

        width = LevelSize(Width, level);
        height = LevelSize(Height, level);
        return true;
    }

    private static int LevelSize(int size, int level) => (size + (1 << level) - 1) >> level;

    /// <summary>Direct port of <c>CPGFImage::Read(level)</c>'s non-ROI loop (PGFimage.cpp:428-475)
    /// followed by <c>GetBitmap</c>: decodes any not-yet-reached levels down to
    /// <paramref name="level"/> (a no-op if already there or past it from an earlier call - matching
    /// the original's own <c>while (m_currentLevel > level)</c> guard), then always re-runs color
    /// conversion on whatever is currently decoded, exactly mirroring
    /// <c>pgf_decode_level_bgra</c> calling <c>Read</c>+<c>GetBitmap</c> unconditionally every call.
    ///
    /// Levels must be requested in decreasing order across calls on one instance - the same contract
    /// <c>PgfDecoder.ProgressivePgfDecoder</c> and the native <c>CPGFImage::Read</c> both document,
    /// since each level's subbands are freed once consumed (<see cref="PgfWaveletTransform.
    /// InverseTransform"/>'s doc comment). Requesting a level already passed on this instance returns
    /// <see langword="false"/> rather than silently decoding/returning the wrong level's data - a
    /// deliberate, stricter fail-closed behavior than the native shim has for the same misuse
    /// (managed-pgf-codec.md Tier 5's general philosophy), not a difference in the valid/documented
    /// usage pattern.</summary>
    public bool TryDecodeLevel<TResult>(
        int level, PgfDecodedCallback<TResult> onDecoded, out TResult? result,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        result = default;

        if (level < 0 || level >= Levels || level > currentLevel)
        {
            return false;
        }

        // pgf-cancellation-and-progress.md: progress is a per-call sweep over just the levels this
        // call decodes (levelsInThisCall may be 0 for a same-level re-request - see
        // PgfProgressCurve's doc comment for why per-call, not whole-image PM_Absolute, was chosen).
        int levelsInThisCall = currentLevel - level;
        int levelsCompleted = 0;

        while (currentLevel > level)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (int[] Data, int Width, int Height)[]? decoded = session.DecodeOneLevel(currentLevel);
            if (decoded is null)
            {
                return false;
            }

            Array.Copy(decoded, lastDecoded, 4);
            currentLevel--;
            levelsCompleted++;
            progress?.Report(PgfProgressCurve.FractionAfter(levelsCompleted, levelsInThisCall));
        }

        int outWidth = lastDecoded[0].Width;
        int outHeight = lastDecoded[0].Height;
        int chromaWidth = lastDecoded[1].Width;

        int bufferSize = checked(outWidth * outHeight * 4);
        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            PgfColorConversion.DecodeYuvaToBgra(
                lastDecoded[0].Data, lastDecoded[1].Data, lastDecoded[2].Data, lastDecoded[3].Data,
                outWidth, outHeight, chromaWidth, session.Downsample, rented.AsSpan(0, bufferSize));

            result = onDecoded(rented.AsSpan(0, bufferSize), outWidth, outHeight);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
