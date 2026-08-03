namespace PictTag.PgfCodec;

/// <summary>
/// Canonical (bpp, channels) per image mode this port supports beyond RGBA (pgf-all-image-modes.md).
///
/// Deliberately NOT a port of <c>CPGFImage::CompleteHeader</c>'s own bpp/channels-defaulting
/// switches (PGFimage.cpp:224-233, 237-271, 294-317) - direct inspection found those two switches
/// have a real, confirmed gap for <see cref="PgfConstants.ImageModeHSLColor"/>/
/// <see cref="PgfConstants.ImageModeHSBColor"/>: neither switch has a case for them (the bpp-switch
/// falls through to <c>default: ASSERT(false); bpp = 24</c>; the channels-switch falls through to
/// <c>default: return false</c>, i.e. <c>CompleteHeader</c> flatly rejects a header for either mode
/// whenever a caller leaves <c>channels</c> at its auto-detect value of 0). This means a real caller
/// of the native encoder can only ever use HSL/HSB by supplying <c>bpp</c>/<c>channels</c> explicitly
/// - the "auto" path genuinely doesn't support them. Rather than reproduce that gap, this table is
/// sourced from what the actual per-mode transform code requires (the ASSERTs in
/// <c>RgbToYuv</c>/<c>GetBitmap</c>, PGFimage.cpp:1388,1788 - the real authority on "what shape does
/// this mode's data need to be", not the defaulting switch): HSL/HSB are conventional 3-component
/// color spaces, and <c>RgbToYuv</c>'s Group-A case block (PGFimage.cpp:1445-1473) is generic over
/// channel count, so 3 channels x 8 bits works for them exactly as it does for
/// <see cref="PgfConstants.ImageModeLabColor"/> - this port always supplies bpp/channels explicitly
/// (never relies on native's auto-detect), so the real gap never actually blocks anything here.
/// </summary>
internal static class PgfModeInfo
{
    /// <summary>Every mode's (bpp, channels), keyed by mode byte. Returns <see langword="false"/> for
    /// a mode this port doesn't cover (matching this PRD's Non-goals: the reserved Adobe modes
    /// Multichannel(7)/Duotone(8)/DeepMultichannel(14)/Duotone16(15), or any unrecognized byte).</summary>
    public static bool TryGetBppAndChannels(byte mode, out byte bpp, out byte channels)
    {
        switch (mode)
        {
            case PgfConstants.ImageModeBitmap:
                bpp = 1; channels = 1; return true;
            case PgfConstants.ImageModeGrayScale:
            case PgfConstants.ImageModeIndexedColor:
                bpp = 8; channels = 1; return true;
            case PgfConstants.ImageModeHSLColor:
            case PgfConstants.ImageModeHSBColor:
            case PgfConstants.ImageModeLabColor:
            case PgfConstants.ImageModeRGBColor:
                bpp = 24; channels = 3; return true;
            case PgfConstants.ImageModeGray16:
                bpp = 16; channels = 1; return true;
            case PgfConstants.ImageModeLab48:
            case PgfConstants.ImageModeRGB48:
                bpp = 48; channels = 3; return true;
            case PgfConstants.ImageModeRGBA:
            case PgfConstants.ImageModeCMYKColor:
                bpp = 32; channels = 4; return true;
            case PgfConstants.ImageModeCMYK64:
                bpp = 64; channels = 4; return true;
            case PgfConstants.ImageModeGray32:
                bpp = 32; channels = 1; return true;
            case PgfConstants.ImageModeRGB12:
                bpp = 12; channels = 3; return true;
            case PgfConstants.ImageModeRGB16:
                bpp = 16; channels = 3; return true;
            default:
                bpp = 0; channels = 0; return false;
        }
    }

    /// <summary>Direct port of <c>CompleteHeader</c>'s <c>usedBitsPerChannel</c> derivation
    /// (PGFimage.cpp:320-325): <c>bpp/channels</c>, capped at 31 (<see cref="PgfConstants.MaxBitPlanes"/>).
    /// Meaningless for the packed sub-byte/sub-word formats (RGB12/RGB16/Bitmap) that don't divide
    /// evenly and don't actually reference this field in their own transform code - harmless either
    /// way since native only ever uses it to cap, never to reject.</summary>
    public static byte UsedBitsPerChannel(byte bpp, byte channels) => (byte)Math.Min(bpp / channels, 31);

    /// <summary>Direct port of <c>CPGFImage::SetHeader</c>'s downsample-eligibility check
    /// (PGFimage.cpp:921-927): exactly these 7 modes ever get chroma-subsampled (channel 0 stays
    /// full resolution; channels 1..N-1 are 2x2 box-averaged, same structure regardless of mode -
    /// confirmed directly, not assumed, by comparing <c>GetBitmap</c>'s <see cref="PgfConstants.
    /// ImageModeLabColor"/> case, PGFimage.cpp:2124-2159, against its <see cref="PgfConstants.
    /// ImageModeRGBColor"/> case, PGFimage.cpp:1975-2046 - same <c>uPos</c>/<c>uOffset</c>/
    /// downsample-position bookkeeping, differing only in whether there's a cross-channel YUV
    /// transform on top). Every other mode this port covers (Bitmap/GrayScale/IndexedColor/HSL/HSB/
    /// Gray16/Gray32/RGB12/RGB16) is never downsampled, including the two 3-channel Group-A members
    /// (HSL/HSB) that might otherwise look downsample-eligible by analogy with Lab - they aren't;
    /// this is the real, confirmed, non-obvious boundary pgf-all-image-modes.md's own "Why grounded"
    /// section calls out (Lab shares Group A's encode transform but not its decode structure).</summary>
    public static bool SupportsDownsample(byte mode) => mode switch
    {
        PgfConstants.ImageModeRGBColor or
        PgfConstants.ImageModeRGBA or
        PgfConstants.ImageModeRGB48 or
        PgfConstants.ImageModeCMYKColor or
        PgfConstants.ImageModeCMYK64 or
        PgfConstants.ImageModeLabColor or
        PgfConstants.ImageModeLab48 => true,
        _ => false,
    };

    /// <summary>Expected tightly-packed source byte length for <see cref="PgfImageEncoder.
    /// TryEncodeMode"/>'s input contract - ceiling bits-to-bytes-per-row (<c>(width*bpp+7)/8</c>),
    /// times <paramref name="height"/>. Equivalent to the simpler <c>width * height * (bpp/8)</c> for
    /// every byte-aligned bpp (no remainder possible when <c>bpp%8==0</c>), but also correctly
    /// handles the two packed sub-byte-or-non-byte-aligned-per-pixel modes this formula would
    /// otherwise silently truncate to 0 or an undersized count via integer division: Bitmap's 1bpp
    /// (<c>(width+7)/8</c> bytes/row, matching <c>RgbToYuv</c>'s own <c>w2</c> - PGFimage.cpp:1405)
    /// and RGB12's 12bpp (2 pixels packed into 3 bytes - PGFimage.cpp:1702-1719's own per-row byte
    /// count for an odd <paramref name="width"/>'s dangling final pixel). Row-by-row, not a single
    /// whole-image division, because each row's own packing restarts independently (no packing
    /// carries across a row boundary) - confirmed against both cited case blocks, not assumed.</summary>
    public static int ExpectedSourceByteLength(byte mode, byte bpp, int width, int height) =>
        checked(((width * bpp) + 7) / 8 * height);
}
