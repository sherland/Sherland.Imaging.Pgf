namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of <c>PGFRect</c> (<c>PGFtypes.h:229-270</c>) - a plain, wavelet-alignment-unaware
/// pixel rectangle (<c>pgf-roi-support.md</c>'s Proposed architecture). Values are plain
/// <see cref="int"/> (not <c>UINT32</c>) for consistency with the rest of this port's coordinate
/// arithmetic (<see cref="PgfSubband"/>'s own <c>Width</c>/<c>Height</c>) - always non-negative in
/// practice, so the signedness difference from the native <c>UINT32</c> fields never matters.
/// </summary>
internal readonly record struct PgfRoi(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>Inclusive top-left, exclusive bottom-right - matches the native's own doc comment
    /// on <c>PGFRect::IsInside</c>.</summary>
    public bool IsInside(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
