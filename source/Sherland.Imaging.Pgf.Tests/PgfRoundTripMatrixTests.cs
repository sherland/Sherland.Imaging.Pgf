// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 8 exit test (new-features/managed-pgf-codec.md): "the central proof this PRD exists to
/// deliver." Tier 4's 4-way round-trip cross-matrix - for every fixture x every edge-case dimension x
/// every quality value from 0 through <see cref="PgfConstants.MaxQuality"/> - runs all four legs
/// (encode C#/decode C#, encode C#/decode native, encode native/decode C#, encode native/decode
/// native) and asserts the quality-dependent agreement documented in the PRD's "Achievable round-trip
/// guarantee": exact match to the original source bitmap at <c>quality=0</c> (lossless), and
/// pixel-identical agreement across all four legs at every other quality value (the "quantization is
/// deterministic, so lossy output should be implementation-invariant" hypothesis - confirmed here to
/// hold cleanly at every quality level for every fixture/dimension combination tested, no narrowing
/// needed for any specific value).
/// </summary>
public class PgfRoundTripMatrixTests
{
    public enum FixtureKind
    {
        SolidColor,
        Checkerboard,
        Gradient,
    }

    private static (byte[] Bgra, int Width, int Height) BuildFixture(FixtureKind kind, int width, int height) => kind switch
    {
        FixtureKind.SolidColor => TestBitmaps.SolidColor(width, height, b: 30, g: 200, r: 90, a: 128),
        FixtureKind.Checkerboard => TestBitmaps.Checkerboard(width, height),
        FixtureKind.Gradient => TestBitmaps.Gradient(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static TheoryData<FixtureKind, int, int, byte> AllCombinations()
    {
        TheoryData<FixtureKind, int, int, byte> data = [];
        foreach (FixtureKind kind in Enum.GetValues<FixtureKind>())
        {
            foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
            {
                for (int quality = 0; quality <= PgfConstants.MaxQuality; quality++)
                {
                    data.Add(kind, width, height, (byte)quality);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void FourWayRoundTrip_AgreesAcrossAllLegsAtEveryQualityLevel(FixtureKind kind, int width, int height, byte quality)
    {
        (byte[] original, int w, int h) = BuildFixture(kind, width, height);

        // Leg 1: encode C#, decode C# - self round trip.
        Assert.True(PgfImageEncoder.TryEncode(original, w, h, quality, out byte[]? csBytes, cancellationToken: TestContext.Current.CancellationToken), "C# encode failed.");
        Assert.True(PgfImageDecoder.TryDecode(csBytes!, static (bgra, width, height) => (Bytes: bgra.ToArray(), width, height),
            out (byte[] Bytes, int width, int height) csDecodeCs, cancellationToken: TestContext.Current.CancellationToken), "C# decode of C#-encoded bytes failed.");
        Assert.Equal(w, csDecodeCs.width);
        Assert.Equal(h, csDecodeCs.height);

        // Leg 2: encode C#, decode native - validates the new encoder against the trusted, unmodified
        // real decoder (the single most important leg per the PRD).
        Assert.True(NativePgfOracle.TryDecode(csBytes!, out byte[]? nativeDecodeCs, out int w2, out int h2), "Native decode of C#-encoded bytes failed.");
        Assert.Equal(w, w2);
        Assert.Equal(h, h2);

        // Leg 3: encode native, decode C# - validates the new decoder against real encoder output.
        Assert.True(NativePgfOracle.TryEncode(original, w, h, quality, out byte[]? nativeBytes), "Native encode failed.");
        Assert.True(PgfImageDecoder.TryDecode(nativeBytes!, static (bgra, width, height) => (Bytes: bgra.ToArray(), width, height),
            out (byte[] Bytes, int width, int height) csDecodeNative, cancellationToken: TestContext.Current.CancellationToken), "C# decode of native-encoded bytes failed.");
        Assert.Equal(w, csDecodeNative.width);
        Assert.Equal(h, csDecodeNative.height);

        // Leg 4: encode native, decode native - sanity check the shim export itself is wired correctly.
        Assert.True(NativePgfOracle.TryDecode(nativeBytes!, out byte[]? nativeDecodeNative, out int w4, out int h4), "Native decode of native-encoded bytes failed.");
        Assert.Equal(w, w4);
        Assert.Equal(h, h4);

        if (quality == 0)
        {
            Assert.Equal(original, csDecodeCs.Bytes);
            Assert.Equal(original, nativeDecodeCs);
            Assert.Equal(original, csDecodeNative.Bytes);
            Assert.Equal(original, nativeDecodeNative);
        }
        else
        {
            Assert.Equal(csDecodeCs.Bytes, nativeDecodeCs);
            Assert.Equal(csDecodeCs.Bytes, csDecodeNative.Bytes);
            Assert.Equal(csDecodeCs.Bytes, nativeDecodeNative);
        }
    }
}
