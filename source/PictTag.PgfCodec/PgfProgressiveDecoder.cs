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
    private readonly (int[] Data, int Width, int Height)[] lastDecoded;
    private int currentLevel;
    private bool roiEnabled;
    private bool roiSetRoiCalled;

    private PgfProgressiveDecoder(PgfDecodeSession session)
    {
        this.session = session;

        // pgf-user-data-and-small-images.md Goal 4: an nLevels=0 (raw/uncoded) session's channel
        // data is already fully available from TryOpen (mirroring the native codec's own
        // Open()-time read) - seed lastDecoded with it directly so TryDecodeLevel's
        // "while (currentLevel > level)" loop, which naturally runs zero times here, doesn't need
        // its own special case to populate it.
        lastDecoded = session.RawChannelData ?? new (int[], int, int)[session.Channels.Length];
        currentLevel = session.Levels;
    }

    /// <summary>Width/Height at level 0 (full resolution) - matches
    /// <c>PgfDecoder.ProgressivePgfDecoder.Width</c>/<c>Height</c>'s own doc comment: the level
    /// dimensions actually available to decode may differ; use <see cref="TryGetLevelSize"/> per
    /// level.</summary>
    public int Width => session.FullWidth;

    public int Height => session.FullHeight;

    public int Levels => session.Levels;

    /// <summary>pgf-user-data-and-small-images.md Goal 1: the file's post-header user data, read
    /// according to whatever <see cref="PgfUserDataPolicy"/> <see cref="TryOpen"/> was called with -
    /// a property (rather than an <see langword="out"/> parameter, as <see cref="PgfImageDecoder"/>'s
    /// callback-based <c>TryDecode</c> uses) since this type already exposes header-derived facts
    /// this way (<see cref="Width"/>/<see cref="Height"/>/<see cref="Levels"/>), not through a
    /// callback.</summary>
    public PgfUserData UserData => session.UserData;

    public static PgfProgressiveDecoder? TryOpen(
        ReadOnlyMemory<byte> pgfData, PgfUserDataPolicy userDataPolicy = PgfUserDataPolicy.CacheAll, uint userDataPrefixSize = 0)
    {
        PgfDecodeSession? session = PgfDecodeSession.TryOpen(pgfData, userDataPolicy, userDataPrefixSize);
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

    /// <summary>Enables ROI decoding for the rest of this instance's lifetime, mirroring
    /// <c>CPGFImage::Read(rect,...)</c>'s <c>SetROI(rect)</c> call (PGFimage.cpp:517) - clamps
    /// <paramref name="roi"/> to <see cref="Width"/>/<see cref="Height"/> exactly like the native's
    /// own <c>Read(rect,...)</c> does (<c>rect.right==0||rect.right&gt;width</c> etc.), rejecting an
    /// out-of-bounds top-left the same way the native's own <c>ASSERT</c> would (fail closed - a
    /// debug-only native ASSERT is not this port's error-handling convention, per this codebase's
    /// established "verify, don't recall" fail-closed philosophy elsewhere in this file). Also
    /// returns <see langword="false"/> when the file itself isn't ROI-flagged
    /// (<see cref="PgfDecodeSession.RoiSupported"/>'s own doc comment: a real, deliberate
    /// stricter-than-native divergence, since the native's own equivalent silently falls back to a
    /// plain full decode instead of reporting failure).
    ///
    /// <b>One-ROI-per-session divergence</b> (resolves this PRD's "Open questions" third item): the
    /// native <c>CPGFImage</c> supports re-reading the same open image with a different ROI by
    /// resetting the decoder's stream position first (<c>ResetStreamPos</c>'s own doc comment,
    /// <c>Read(rect,...)</c>'s <c>levelDiff &lt;= 0</c> branch). This port does not carry that
    /// forward: <see cref="TrySetRoi"/> must be called before the first <see cref="TryDecodeLevel{TResult}"/>
    /// call on this instance, and calling it again (or after decoding has started) throws. No real
    /// calling pattern needing multiple ROIs per open image exists anywhere in this codebase (this
    /// PRD's own Non-goals section already rules out a production call site) - open a fresh
    /// <see cref="TryOpen"/> session per distinct ROI request instead, matching this port's existing
    /// session-per-open model everywhere else rather than inventing a more flexible one this app has
    /// no use for.</summary>
    public bool TrySetRoi(PgfRoi roi)
    {
        if (roiSetRoiCalled)
        {
            throw new InvalidOperationException("TrySetRoi was already called on this instance.");
        }

        if (currentLevel != Levels)
        {
            throw new InvalidOperationException("TrySetRoi must be called before the first TryDecodeLevel call.");
        }

        roiSetRoiCalled = true;

        if (!session.RoiSupported || Levels == 0 || roi.Left < 0 || roi.Top < 0 || roi.Left >= Width || roi.Top >= Height)
        {
            return false;
        }

        int right = roi.Right <= 0 || roi.Right > Width ? Width : roi.Right;
        int bottom = roi.Bottom <= 0 || roi.Bottom > Height ? Height : roi.Bottom;

        session.SetRoi(new PgfRoi(roi.Left, roi.Top, right, bottom));
        roiEnabled = true;
        return true;
    }

    /// <summary>Direct port of <c>CWaveletTransform::GetAlignedROI</c> as exposed through channel 0
    /// (luma/full-resolution) - the actual, tile/wavelet-aligned pixel rectangle
    /// <paramref name="level"/>'s most recent <see cref="TryDecodeLevel{TResult}"/> call reconstructed
    /// (per this PRD's "Why this needs to be grounded" section: the caller's requested rectangle
    /// "might be cropped" - never assume the whole decoded buffer is valid content without checking
    /// this). Only meaningful after <see cref="TrySetRoi"/> returned <see langword="true"/> and
    /// <paramref name="level"/> has actually been decoded on this instance.</summary>
    public bool TryGetAlignedRoi(int level, out PgfRoi alignedRoi)
    {
        if (!roiEnabled || level < 0 || level > Levels)
        {
            alignedRoi = default;
            return false;
        }

        alignedRoi = session.Channels[0].GetAlignedROI(level);
        return true;
    }

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
    /// usage pattern.
    ///
    /// pgf-user-data-and-small-images.md Goal 4: for an <c>nLevels=0</c> session (<see cref="Levels"/>
    /// is 0), the only valid request is <paramref name="level"/> 0 - matching the native <c>ASSERT(
    /// (level &gt;= 0 &amp;&amp; level &lt; m_header.nLevels) || m_header.nLevels == 0)</c>
    /// (PGFimage.cpp:403), which explicitly carves out <c>nLevels == 0</c> as always valid at level 0
    /// rather than the usual <c>level &lt; Levels</c> range (which would otherwise reject level 0
    /// itself, since 0 &lt; 0 is false).</summary>
    public bool TryDecodeLevel<TResult>(
        int level, PgfDecodedCallback<TResult> onDecoded, out TResult? result,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        result = default;

        if (level < 0 || level > currentLevel || (Levels > 0 && level >= Levels))
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

            (int[] Data, int Width, int Height)[]? decoded = roiEnabled
                ? session.DecodeOneLevelRoi(currentLevel)
                : session.DecodeOneLevel(currentLevel);
            if (decoded is null)
            {
                return false;
            }

            Array.Copy(decoded, lastDecoded, lastDecoded.Length);
            currentLevel--;
            levelsCompleted++;
            progress?.Report(PgfProgressCurve.FractionAfter(levelsCompleted, levelsInThisCall));
        }

        if (Levels == 0)
        {
            // Matches CPGFImage::Read's own nLevels==0 branch (PGFimage.cpp:415-422): the callback
            // fires once, at 1.0, since the data was already read during Open() - the loop above
            // never runs (currentLevel and level are both already 0), so no report happens there.
            progress?.Report(1.0);
        }

        int outWidth = lastDecoded[0].Width;
        int outHeight = lastDecoded[0].Height;

        int bufferSize = checked(outWidth * outHeight * 4);
        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            PgfImageDecoder.ConvertToBgra(session, lastDecoded, rented.AsSpan(0, bufferSize));

            result = onDecoded(rented.AsSpan(0, bufferSize), outWidth, outHeight);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
