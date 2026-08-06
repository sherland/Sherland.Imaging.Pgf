namespace Sherland.Imaging.Pgf;

/// <summary>Direct port of the version-flag bits from PGFtypes.h (the <c>PGFPreHeader.version</c>
/// byte - a bitmask, not to be confused with <c>PGFHeader.version</c>'s major/year/week build
/// identifier, see <see cref="PgfHeader.VersionNumberRaw"/>). These bits are what actually gate
/// decode behavior (e.g. <see cref="Version6"/> controls whether <c>hSize</c> is 2 or 4 bytes on
/// disk; <see cref="Version5"/> controls the tile-based vs. interleaved subband encoding scheme;
/// <see cref="PGFROI"/> controls whether region-of-interest decoding applies) - unlike the
/// major/year/week field, which this codebase's decode path never branches on.</summary>
[Flags]
internal enum PgfVersionFlags : byte
{
    None = 0,
    Version2 = 2,
    PGF32 = 4,
    PGFROI = 8,
    Version5 = 16,
    Version6 = 32,
    Version7 = 64,
}
