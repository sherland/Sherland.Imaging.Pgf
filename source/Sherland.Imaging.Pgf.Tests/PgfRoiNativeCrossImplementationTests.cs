// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 5 exit tests (new-features/pgf-roi-support.md): the cross-implementation encode leg of the
/// ROI round-trip matrix - <c>pgf_encode_bgra_alloc_roi</c> (shim.cpp) is a genuinely independent
/// (real C++, not this port) encoder producing real <c>PGFROI</c>-flagged bytes, decode-tested here
/// against the managed <see cref="PgfProgressiveDecoder"/>. This is the stronger cross-check
/// (proves the managed *decoder* against ground truth an entirely separate implementation produced)
/// compared to the reverse leg (native decoding this port's own encoder output), which this PRD
/// leaves unexercised - the native shim has no ROI-aware *decode* export at all (only
/// <c>pgf_open</c>/<c>pgf_decode_level_bgra</c>, both always non-ROI), and adding one was judged not
/// worth the additional native surface for a capability with no production call site (this PRD's own
/// Context section) once the higher-value leg (native-encode/managed-decode) already proves the
/// managed decoder correct against an independent reference.
/// </summary>
public class PgfRoiNativeCrossImplementationTests
{
    [Theory]
    [InlineData(64, 64, 0)]
    [InlineData(64, 64, 8)]
    [InlineData(37, 53, 0)]
    [InlineData(37, 53, 5)]
    public void NativeRoiEncode_FullImage_ManagedDecodeMatchesNativePlainDecode(int width, int height, byte quality)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);

        Assert.True(NativePgfOracle.TryEncodeRoi(bgra, width, height, quality, out byte[]? nativeRoiPgf));
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality, out byte[]? nativePlainPgf));

        // Sanity: the native encoder really did produce a different, ROI-flagged bitstream, not a
        // no-op.
        Assert.NotEqual(nativePlainPgf, nativeRoiPgf);
        Assert.Equal(8, nativeRoiPgf![3] & 8); // PGFROI

        // Reference: the real native (non-ROI) decoder on the plain file - independent of both this
        // port's encoder AND decoder.
        Assert.True(NativePgfOracle.TryDecode(nativePlainPgf, out byte[]? referenceBgra, out int refW, out int refH));

        // The managed decoder, decoding the *native-encoded* ROI file.
        PgfProgressiveDecoder? managedDecoder = PgfProgressiveDecoder.TryOpen(nativeRoiPgf);
        Assert.NotNull(managedDecoder);
        Assert.True(managedDecoder.TrySetRoi(new PgfRoi(0, 0, width, height)));
        bool decoded = managedDecoder.TryDecodeLevel(0, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var managedResult);
        Assert.True(decoded);

        Assert.Equal(refW, managedResult.w);
        Assert.Equal(refH, managedResult.h);
        Assert.Equal(referenceBgra, managedResult.Bytes);
    }

    public static IEnumerable<object[]> PartialRoiCases()
    {
        yield return [64, 64, 20, 20, 44, 44];
        yield return [64, 64, 0, 0, 8, 8];
        yield return [96, 80, 10, 10, 30, 25];
    }

    [Theory]
    [MemberData(nameof(PartialRoiCases))]
    public void NativeRoiEncode_PartialRoi_ManagedDecodeMatchesCorrespondingRegionOfNativePlainDecode(
        int width, int height, int reqLeft, int reqTop, int reqRight, int reqBottom)
    {
        (byte[] bgra, _, _) = TestBitmaps.Gradient(width, height);
        const byte quality = 0;

        Assert.True(NativePgfOracle.TryEncodeRoi(bgra, width, height, quality, out byte[]? nativeRoiPgf));
        Assert.True(NativePgfOracle.TryEncode(bgra, width, height, quality, out byte[]? nativePlainPgf));
        Assert.True(NativePgfOracle.TryDecode(nativePlainPgf, out byte[]? referenceBgra, out int refW, out _));
        Assert.Equal(width, refW);

        PgfProgressiveDecoder? managedDecoder = PgfProgressiveDecoder.TryOpen(nativeRoiPgf);
        Assert.NotNull(managedDecoder);
        Assert.True(managedDecoder.TrySetRoi(new PgfRoi(reqLeft, reqTop, reqRight, reqBottom)));
        bool decoded = managedDecoder.TryDecodeLevel(0, static (b, w, h) => (Bytes: b.ToArray(), w, h), out var managedResult);
        Assert.True(decoded);

        Assert.True(managedDecoder.TryGetAlignedRoi(0, out PgfRoi aligned));
        Assert.True(managedDecoder.TryGetAccurateRoi(0, out PgfRoi accurate));
        Assert.Equal(new PgfRoi(reqLeft, reqTop, reqRight, reqBottom), accurate);

        for (int y = 0; y < accurate.Height; y++)
        {
            for (int x = 0; x < accurate.Width; x++)
            {
                int absoluteX = accurate.Left + x;
                int absoluteY = accurate.Top + y;
                int managedIndex = (((absoluteY - aligned.Top) * aligned.Width) + (absoluteX - aligned.Left)) * 4;
                int referenceIndex = ((absoluteY * width) + absoluteX) * 4;

                ReadOnlySpan<byte> managedPixel = managedResult.Bytes.AsSpan(managedIndex, 4);
                ReadOnlySpan<byte> referencePixel = referenceBgra.AsSpan(referenceIndex, 4);

                Assert.True(
                    managedPixel.SequenceEqual(referencePixel),
                    $"Pixel ({absoluteX},{absoluteY}) mismatch: managed decode of native-encoded ROI file gave " +
                    $"[{managedPixel[0]},{managedPixel[1]},{managedPixel[2]},{managedPixel[3]}], native plain decode gave " +
                    $"[{referencePixel[0]},{referencePixel[1]},{referencePixel[2]},{referencePixel[3]}].");
            }
        }
    }
}
