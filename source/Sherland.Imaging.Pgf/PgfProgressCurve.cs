namespace Sherland.Imaging.Pgf;

/// <summary>
/// new-features/pgf-cancellation-and-progress.md Stage 1: per-call progress fraction for a
/// multi-level decode/encode operation. Mirrors the native codec's own area-weighted percent
/// formula (<c>CPGFImage::Read</c>/<c>WriteImage</c>, PGFimage.cpp: <c>percent *= 4</c> each level,
/// since each level covers 4x the previous level's linear coverage in the 2D wavelet pyramid)
/// rather than a plain per-level-count linear sweep - a count-based fraction would misreport
/// "almost done" after finishing only the cheap, coarse levels, when the final (finest, full-
/// resolution) level actually dominates the real per-level work. Levels within one call are
/// weighted 1:4:16:... coarsest-to-finest so the reported fraction tracks real work, not level
/// count.
///
/// Deliberately a per-call sweep (0-&gt;1 over just the levels this specific call decodes/encodes),
/// not the native's <c>PM_Absolute</c> whole-image share - the PRD's Non-goals explicitly reject
/// exposing <c>ProgressMode</c> as public API, and per-call is simpler and already exactly matches
/// the native's own default <c>PM_Relative</c> mode. For <see cref="PgfImageDecoder.TryDecode{TResult}"/>/
/// <see cref="PgfImageEncoder.TryEncode"/> (always the full level range), per-call and whole-image
/// coincide anyway; it only matters for <see cref="PgfProgressiveDecoder.TryDecodeLevel{TResult}"/>'s
/// partial-range calls.
/// </summary>
internal static class PgfProgressCurve
{
    public static double FractionAfter(int levelsCompleted, int levelsInThisCall)
    {
        if (levelsInThisCall <= 0)
        {
            return 1.0;
        }

        double completedWeight = Math.Pow(4, levelsCompleted) - 1;
        double totalWeight = Math.Pow(4, levelsInThisCall) - 1;
        return completedWeight / totalWeight;
    }
}
