using PictTag.PgfCodec.Tests.Oracle;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// Stage 9 exit tests (new-features/managed-pgf-codec.md): "port PgfDecoderTests.cs's
/// ProgressiveDecoder_* assertions (level-0-matches-single-shot, monotonic-resolution-growth,
/// malformed-input-fails-closed) against the new implementation" - the three tests named after that
/// file's own (<c>PictTag.Data.Tests.PgfDecoderTests</c>) - plus the Tier 2 cross-check the "Test rig:
/// functional correctness" section calls out by name: "every progressive level via
/// OpenProgressive/TryDecodeLevel", not just the single-shot path.
/// </summary>
public class PgfProgressiveDecoderTests
{
    [Fact]
    public void Level0_MatchesSingleShotDecode_ByteForByte()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);

        bool singleShotOk = PgfImageDecoder.TryDecode(pgfBytes, out byte[]? singleShotBgra, out int ssWidth, out int ssHeight);
        Assert.True(singleShotOk);

        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);

        bool decoded = decoder.TryDecodeLevel(0, static (bgra, width, height) => (Bytes: bgra.ToArray(), width, height),
            out (byte[] Bytes, int width, int height) progressive);

        Assert.True(decoded);
        Assert.Equal(ssWidth, progressive.width);
        Assert.Equal(ssHeight, progressive.height);
        Assert.Equal(singleShotBgra, progressive.Bytes);
    }

    [Fact]
    public void DecodingLevelsInDecreasingOrder_ResolutionIncreasesMonotonically()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);

        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);
        Assert.True(decoder.Levels > 1, "Fixture should have more than one level to prove monotonic growth.");

        long previousPixelCount = 0;
        for (int level = decoder.Levels - 1; level >= 0; level--)
        {
            bool decoded = decoder.TryDecodeLevel(level, static (bgra, width, height) => (bgra.Length, width, height),
                out (int Length, int width, int height) result);

            Assert.True(decoded, $"Level {level} should decode successfully.");
            Assert.Equal(result.width * result.height * 4, result.Length);

            long pixelCount = (long)result.width * result.height;
            Assert.True(pixelCount >= previousPixelCount,
                $"Level {level} ({result.width}x{result.height}) should be at least as large as the previous, coarser level.");
            previousPixelCount = pixelCount;
        }

        Assert.True(decoder.TryGetLevelSize(0, out int finalWidth, out int finalHeight));
        Assert.Equal(decoder.Width, finalWidth);
        Assert.Equal(decoder.Height, finalHeight);
    }

    [Fact]
    public void GarbageBytes_ReturnsNullWithoutThrowing()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04];

        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(garbage);

        Assert.Null(decoder);
    }

    [Fact]
    public void EmptyInput_ReturnsNullWithoutThrowing()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(ReadOnlyMemory<byte>.Empty);

        Assert.Null(decoder);
    }

    [Fact]
    public void RequestingLevelOutOfRange_ReturnsFalse()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);

        Assert.False(decoder.TryDecodeLevel(-1, static (b, w, h) => true, out _));
        Assert.False(decoder.TryDecodeLevel(decoder.Levels, static (b, w, h) => true, out _));
    }

    /// <summary>Levels must be requested in decreasing order - requesting a level already passed
    /// (coarser than the finest level reached so far) is a real caller-contract violation the native
    /// shim doesn't guard against (it would silently return the wrong-sized data), but this port
    /// deliberately fails closed instead (see <c>PgfProgressiveDecoder.TryDecodeLevel</c>'s doc
    /// comment).</summary>
    [Fact]
    public void RequestingAlreadyPassedCoarserLevel_ReturnsFalse()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);
        Assert.True(decoder.Levels > 1, "Fixture should have more than one level for this test to be meaningful.");

        Assert.True(decoder.TryDecodeLevel(0, static (b, w, h) => true, out _));

        Assert.False(decoder.TryDecodeLevel(decoder.Levels - 1, static (b, w, h) => true, out _));
    }

    /// <summary>Re-requesting the exact same level again is valid and idempotent (unlike requesting a
    /// coarser, already-passed level) - matches the native shim calling <c>Read</c>+<c>GetBitmap</c>
    /// unconditionally on every call.</summary>
    [Fact]
    public void RequestingSameLevelTwice_ReturnsSameResultBothTimes()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);

        Assert.True(decoder.TryDecodeLevel(1, static (bgra, w, h) => bgra.ToArray(), out byte[]? first));
        Assert.True(decoder.TryDecodeLevel(1, static (bgra, w, h) => bgra.ToArray(), out byte[]? second));

        Assert.Equal(first, second);
    }

    /// <summary>Tier 2's progressive-decode cross-check, explicitly called out in the PRD's test rig
    /// section: every level of the real fixture, decoded via this port's progressive API, byte-exact
    /// against the native oracle's own progressive decode of the same levels in the same order.</summary>
    [Fact]
    public void EveryLevel_ManagedProgressiveDecodeMatchesNativeOracle_ByteExact()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);

        PgfProgressiveDecoder? managed = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(managed);

        nint nativeHandle = NativePgfOracle.OpenHandle(pgfBytes, out int nativeLevels);
        Assert.NotEqual(0, nativeHandle);
        Assert.Equal(managed.Levels, nativeLevels);

        try
        {
            for (int level = managed.Levels - 1; level >= 0; level--)
            {
                bool managedOk = managed.TryDecodeLevel(level, static (bgra, w, h) => (Bytes: bgra.ToArray(), w, h),
                    out (byte[] Bytes, int w, int h) managedResult);
                Assert.True(managedOk, $"Managed progressive decode failed at level {level}.");

                bool nativeOk = NativePgfOracle.TryDecodeLevel(nativeHandle, level, out byte[]? nativeBgra, out int nw, out int nh);
                Assert.True(nativeOk, $"Native progressive decode failed at level {level}.");

                Assert.Equal(nw, managedResult.w);
                Assert.Equal(nh, managedResult.h);
                Assert.Equal(nativeBgra, managedResult.Bytes);
            }
        }
        finally
        {
            NativePgfOracle.CloseHandle(nativeHandle);
        }
    }

    /// <summary>Same cross-check, but over synthetic fixtures produced by this port's own encoder
    /// (Stage 8) at several quality levels and edge-case dimensions - not just the one committed
    /// real-world fixture.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(15)]
    public void EveryLevel_SyntheticFixture_ManagedProgressiveDecodeMatchesNativeOracle_ByteExact(byte quality)
    {
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);
            Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));

            PgfProgressiveDecoder? managed = PgfProgressiveDecoder.TryOpen(pgfBytes!);
            Assert.NotNull(managed);

            nint nativeHandle = NativePgfOracle.OpenHandle(pgfBytes!, out int nativeLevels);
            Assert.NotEqual(0, nativeHandle);
            Assert.Equal(managed.Levels, nativeLevels);

            try
            {
                for (int level = managed.Levels - 1; level >= 0; level--)
                {
                    bool managedOk = managed.TryDecodeLevel(level, static (b, lw, lh) => (Bytes: b.ToArray(), lw, lh),
                        out (byte[] Bytes, int lw, int lh) managedResult);
                    Assert.True(managedOk, $"{w}x{h} q={quality}: managed progressive decode failed at level {level}.");

                    bool nativeOk = NativePgfOracle.TryDecodeLevel(nativeHandle, level, out byte[]? nativeBgra, out int nw, out int nh);
                    Assert.True(nativeOk, $"{w}x{h} q={quality}: native progressive decode failed at level {level}.");

                    Assert.Equal(nw, managedResult.lw);
                    Assert.Equal(nh, managedResult.lh);
                    Assert.Equal(nativeBgra, managedResult.Bytes);
                }
            }
            finally
            {
                NativePgfOracle.CloseHandle(nativeHandle);
            }
        }
    }
}
