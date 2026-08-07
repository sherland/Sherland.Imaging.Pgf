// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf;

/// <summary>
/// Direct port of <c>PGFRect</c> (<c>PGFtypes.h:229-270</c>) - a plain, wavelet-alignment-unaware
/// pixel rectangle (<c>pgf-roi-support.md</c>'s Proposed architecture). Values are plain
/// <see cref="int"/> (not <c>UINT32</c>) for consistency with the rest of this port's coordinate
/// arithmetic (<see cref="PgfSubband"/>'s own <c>Width</c>/<c>Height</c>) - always non-negative in
/// practice, so the signedness difference from the native <c>UINT32</c> fields never matters.
///
/// Public (unlike most of this codec's internal types): it's part of
/// <see cref="PgfProgressiveDecoder"/>'s own public ROI API surface
/// (<see cref="PgfProgressiveDecoder.TrySetRoi"/>/<see cref="PgfProgressiveDecoder.TryGetAlignedRoi"/>),
/// not just an internal implementation detail.
/// </summary>
public readonly record struct PgfRoi(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>Inclusive top-left, exclusive bottom-right - matches the native's own doc comment
    /// on <c>PGFRect::IsInside</c>.</summary>
    public bool IsInside(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
