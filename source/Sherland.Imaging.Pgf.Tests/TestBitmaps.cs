// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Known-pixel synthetic BGRA bitmaps for Tier 1 of the functional test rig
/// (<c>new-features/managed-pgf-codec.md</c>) - authored directly as raw pixel arrays so expected
/// output is independently computable, not "whatever the oracle said". Covers edge-case dimensions
/// (1x1, odd/prime sizes, sizes spanning several wavelet levels) alongside simple, predictable
/// pixel content (solid colors, checkerboard, gradient).
/// </summary>
internal static class TestBitmaps
{
    public static (byte[] Bgra, int Width, int Height) SolidColor(int width, int height, byte b, byte g, byte r, byte a)
    {
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = a;
        }

        return (bgra, width, height);
    }

    public static (byte[] Bgra, int Width, int Height) Checkerboard(int width, int height, int squareSize = 4)
    {
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool light = ((x / squareSize) + (y / squareSize)) % 2 == 0;
                int i = (y * width + x) * 4;
                byte v = light ? (byte)255 : (byte)0;
                bgra[i] = v;
                bgra[i + 1] = v;
                bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        }

        return (bgra, width, height);
    }

    /// <summary>Smooth horizontal/vertical gradient - exercises non-constant, non-repeating
    /// coefficients across every subband, unlike <see cref="SolidColor"/> (all-zero AC coefficients)
    /// or <see cref="Checkerboard"/> (highly regular/periodic).</summary>
    public static (byte[] Bgra, int Width, int Height) Gradient(int width, int height)
    {
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bgra[i] = (byte)(width <= 1 ? 0 : x * 255 / (width - 1));
                bgra[i + 1] = (byte)(height <= 1 ? 0 : y * 255 / (height - 1));
                bgra[i + 2] = (byte)(((x + y) * 255) / (width + height - 1 == 0 ? 1 : width + height - 1));
                bgra[i + 3] = 255;
            }
        }

        return (bgra, width, height);
    }

    /// <summary>Dimensions chosen to exercise edge cases: odd/prime sizes (no power-of-two
    /// alignment to hide rounding bugs in level-size computation) and sizes large enough to span
    /// several wavelet transform levels. Deliberately excludes anything with
    /// <c>min(width, height) &lt; 10</c> (e.g. 1x1) - see <see cref="MinimumSupportedDimension"/>.</summary>
    public static IEnumerable<(int Width, int Height)> EdgeCaseDimensions()
    {
        yield return (10, 10);
        yield return (11, 10);
        yield return (13, 13);
        yield return (37, 23);
        yield return (64, 64);
        yield return (200, 150);
    }

    /// <summary>Below this size (in either dimension), <c>CPGFImage::ComputeLevels()</c> falls back
    /// to <c>nLevels=0</c> - a completely different, wavelet-transform-free "store raw/uncoded
    /// channel data" path. Originally out of scope for both this port and the native shim's own
    /// test-only <c>pgf_encode_bgra_alloc</c>/<c>pgf_debug_decode_channel</c> exports (a real
    /// heap-corruption risk was found exercising it during this test rig's early development,
    /// new-features/managed-pgf-codec.md) - <c>pgf_debug_decode_channel</c>'s own, unrelated,
    /// still-unresolved repeated-call crash risk remains, but the size-range guard itself didn't
    /// reproduce once isolated and re-tested (pgf-user-data-and-small-images.md's Open Question 1),
    /// so both the native shim's `pgf_encode_bgra_alloc`/`pgf_encode_raw_alloc` and this port's own
    /// decoder/encoder (<see cref="PgfDecodeSession"/>/<see cref="PgfImageEncoder"/>, Stages 4-5 of
    /// that same PRD) now support this range like any other. Still marks a real, meaningful boundary
    /// worth naming - the point where the format switches from wavelet-coded to raw storage, not an
    /// unsupported region anymore.</summary>
    public const int MinimumSupportedDimension = 10;
}
