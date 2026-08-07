// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf;

/// <summary>Direct port of the <c>Orientation</c> enum (PGFtypes.h) - the four quadrants of one
/// wavelet transform level. LL = low-pass both axes (the "preview" band, recursively transformed
/// again at the next level); HL/LH/HH = one or both axes high-pass (detail bands).</summary>
internal enum PgfSubbandOrientation
{
    Ll = 0,
    Hl = 1,
    Lh = 2,
    Hh = 3,
}
