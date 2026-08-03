namespace PictTag.PgfCodec.Tests;

/// <summary>
/// new-features/pgf-cancellation-and-progress.md test rig: progress reporting and cooperative
/// cancellation for <see cref="PgfImageDecoder.TryDecode{TResult}"/> (Stage 2) and
/// <see cref="PgfProgressiveDecoder.TryDecodeLevel{TResult}"/> (Stage 2). Deliberately uses a
/// synchronous <see cref="IProgress{T}"/> test double rather than <see cref="Progress{T}"/> itself -
/// the real <see cref="Progress{T}"/> posts callbacks via <see cref="SynchronizationContext"/>
/// (asynchronously, off the calling thread, when none is captured), which would make the
/// cancel-after-Nth-report tests below racy against the decode loop's own thread.
/// </summary>
public class PgfProgressAndCancellationTests
{
    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private static void AssertMonotonicNonDecreasing_EndingAtOne(List<double> values)
    {
        Assert.NotEmpty(values);
        for (int i = 1; i < values.Count; i++)
        {
            Assert.True(values[i] >= values[i - 1], $"Progress regressed: {values[i - 1]} -> {values[i]}");
        }

        Assert.Equal(1.0, values[^1], precision: 9);
    }

    public static TheoryData<int, int, byte> FixtureDimensionsAndQualities()
    {
        TheoryData<int, int, byte> data = [];
        foreach ((int width, int height) in TestBitmaps.EdgeCaseDimensions())
        {
            foreach (byte quality in (byte[])[0, 4, 15])
            {
                data.Add(width, height, quality);
            }
        }

        return data;
    }

    // --- PgfImageDecoder.TryDecode ---

    [Theory]
    [MemberData(nameof(FixtureDimensionsAndQualities))]
    public void TryDecode_ProgressReports_OncePerLevel_MonotonicNonDecreasing_EndsAtOne(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);
        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));

        PgfProgressiveDecoder? probe = PgfProgressiveDecoder.TryOpen(pgfBytes!);
        Assert.NotNull(probe);
        int expectedLevels = probe.Levels;

        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);

        bool ok = PgfImageDecoder.TryDecode(pgfBytes!, static (b, dw, dh) => true, out _, progress);

        Assert.True(ok);
        Assert.Equal(expectedLevels, reports.Count);
        AssertMonotonicNonDecreasing_EndingAtOne(reports);
    }

    [Fact]
    public void TryDecode_PreCancelledToken_ThrowsBeforeAnyLevelWork()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);

        Assert.Throws<OperationCanceledException>(() =>
            PgfImageDecoder.TryDecode(pgfBytes, static (b, w, h) => true, out _, progress, new CancellationToken(canceled: true)));

        Assert.Empty(reports);
    }

    [Fact]
    public void TryDecode_CancelDuringOperation_ThrowsAfterCompletingExactlyNLevels()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? probe = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(probe);
        Assert.True(probe.Levels > 1, "Fixture should have more than one level for this test to be meaningful.");

        foreach (int cancelAfterReports in new[] { 1, probe.Levels - 1 }.Distinct())
        {
            using CancellationTokenSource cts = new();
            List<double> reports = [];
            SynchronousProgress<double> progress = new(p =>
            {
                reports.Add(p);
                if (reports.Count == cancelAfterReports)
                {
                    cts.Cancel();
                }
            });

            Assert.Throws<OperationCanceledException>(() =>
                PgfImageDecoder.TryDecode(pgfBytes, static (b, w, h) => true, out _, progress, cts.Token));

            Assert.Equal(cancelAfterReports, reports.Count);
        }
    }

    [Fact]
    public void TryDecode_CancellationDoesNotCorruptStateForSubsequentUnrelatedCall()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PgfImageDecoder.TryDecode(pgfBytes, static (b, w, h) => true, out _, cancellationToken: cts.Token));

        bool ok = PgfImageDecoder.TryDecode(pgfBytes, static (bgra, w, h) => (Bytes: bgra.ToArray(), w, h),
            out (byte[] Bytes, int w, int h) result);

        Assert.True(ok);
        Assert.NotEmpty(result.Bytes);
    }

    // --- PgfProgressiveDecoder.TryDecodeLevel ---

    [Theory]
    [MemberData(nameof(FixtureDimensionsAndQualities))]
    public void TryDecodeLevel_FullRangeInOneCall_ProgressReports_OncePerLevel_MonotonicNonDecreasing_EndsAtOne(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);
        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes));

        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes!);
        Assert.NotNull(decoder);
        int expectedLevels = decoder.Levels;

        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);

        bool ok = decoder.TryDecodeLevel(0, static (b, dw, dh) => true, out _, progress);

        Assert.True(ok);
        Assert.Equal(expectedLevels, reports.Count);
        AssertMonotonicNonDecreasing_EndingAtOne(reports);
    }

    [Fact]
    public void TryDecodeLevel_PerLevelCalls_ReportsExactlyOncePerCall()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);
        Assert.True(decoder.Levels > 1, "Fixture should have more than one level.");

        for (int level = decoder.Levels - 1; level >= 0; level--)
        {
            List<double> reports = [];
            SynchronousProgress<double> progress = new(reports.Add);

            bool ok = decoder.TryDecodeLevel(level, static (b, w, h) => true, out _, progress);

            Assert.True(ok);
            Assert.Single(reports);
            Assert.Equal(1.0, reports[0], precision: 9);
        }
    }

    [Fact]
    public void TryDecodeLevel_RerequestingSameLevel_ReportsZeroTimes()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);

        Assert.True(decoder.TryDecodeLevel(1, static (b, w, h) => true, out _));

        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);
        bool ok = decoder.TryDecodeLevel(1, static (b, w, h) => true, out _, progress);

        Assert.True(ok);
        Assert.Empty(reports);
    }

    [Fact]
    public void TryDecodeLevel_PreCancelledToken_ThrowsBeforeAnyLevelWork()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(decoder);

        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);

        Assert.Throws<OperationCanceledException>(() =>
            decoder.TryDecodeLevel(0, static (b, w, h) => true, out _, progress, new CancellationToken(canceled: true)));

        Assert.Empty(reports);
    }

    [Fact]
    public void TryDecodeLevel_CancelDuringOperation_ThrowsAfterCompletingExactlyNLevels_AndCanStillResume()
    {
        byte[] pgfBytes = File.ReadAllBytes(TestFixtures.SampleThumbnailPath);
        PgfProgressiveDecoder? probe = PgfProgressiveDecoder.TryOpen(pgfBytes);
        Assert.NotNull(probe);
        Assert.True(probe.Levels > 1, "Fixture should have more than one level for this test to be meaningful.");

        foreach (int cancelAfterReports in new[] { 1, probe.Levels - 1 }.Distinct())
        {
            PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
            Assert.NotNull(decoder);

            using CancellationTokenSource cts = new();
            List<double> reports = [];
            SynchronousProgress<double> progress = new(p =>
            {
                reports.Add(p);
                if (reports.Count == cancelAfterReports)
                {
                    cts.Cancel();
                }
            });

            Assert.Throws<OperationCanceledException>(() =>
                decoder.TryDecodeLevel(0, static (b, w, h) => true, out _, progress, cts.Token));

            Assert.Equal(cancelAfterReports, reports.Count);

            // Not corrupted: this decoder instance is stateful across calls (unlike TryDecode's
            // call-scoped session) - a fresh, uncancelled call resumes from exactly where the
            // cancelled one left off rather than re-decoding or losing state.
            bool ok = decoder.TryDecodeLevel(0, static (bgra, w, h) => (Bytes: bgra.ToArray(), w, h),
                out (byte[] Bytes, int w, int h) result);
            Assert.True(ok);
            Assert.NotEmpty(result.Bytes);
        }
    }

    // --- PgfImageEncoder.TryEncode (Stage 3) ---

    [Theory]
    [MemberData(nameof(FixtureDimensionsAndQualities))]
    public void TryEncode_ProgressReports_OncePerLevel_MonotonicNonDecreasing_EndsAtOne(int width, int height, byte quality)
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(width, height);

        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);

        bool ok = PgfImageEncoder.TryEncode(bgra, w, h, quality, out byte[]? pgfBytes, progress);
        Assert.True(ok);

        PgfProgressiveDecoder? probe = PgfProgressiveDecoder.TryOpen(pgfBytes!);
        Assert.NotNull(probe);

        Assert.Equal(probe.Levels, reports.Count);
        AssertMonotonicNonDecreasing_EndingAtOne(reports);
    }

    [Fact]
    public void TryEncode_PreCancelledToken_ThrowsBeforeAnyLevelWork()
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(64, 64);
        List<double> reports = [];
        SynchronousProgress<double> progress = new(reports.Add);

        Assert.Throws<OperationCanceledException>(() =>
            PgfImageEncoder.TryEncode(bgra, w, h, quality: 4, out _, progress, new CancellationToken(canceled: true)));

        Assert.Empty(reports);
    }

    [Fact]
    public void TryEncode_CancelDuringOperation_ThrowsAfterCompletingExactlyNLevels()
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(200, 150);

        Assert.True(PgfImageEncoder.TryEncode(bgra, w, h, quality: 4, out byte[]? probeBytes));
        PgfProgressiveDecoder? probe = PgfProgressiveDecoder.TryOpen(probeBytes!);
        Assert.NotNull(probe);
        Assert.True(probe.Levels > 1, "Fixture should have more than one level for this test to be meaningful.");

        foreach (int cancelAfterReports in new[] { 1, probe.Levels - 1 }.Distinct())
        {
            using CancellationTokenSource cts = new();
            List<double> reports = [];
            SynchronousProgress<double> progress = new(p =>
            {
                reports.Add(p);
                if (reports.Count == cancelAfterReports)
                {
                    cts.Cancel();
                }
            });

            Assert.Throws<OperationCanceledException>(() =>
                PgfImageEncoder.TryEncode(bgra, w, h, quality: 4, out _, progress, cts.Token));

            Assert.Equal(cancelAfterReports, reports.Count);
        }
    }

    [Fact]
    public void TryEncode_CancellationDoesNotCorruptStateForSubsequentUnrelatedCall()
    {
        (byte[] bgra, int w, int h) = TestBitmaps.Gradient(64, 64);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PgfImageEncoder.TryEncode(bgra, w, h, quality: 4, out _, cancellationToken: cts.Token));

        bool ok = PgfImageEncoder.TryEncode(bgra, w, h, quality: 4, out byte[]? pgfBytes);

        Assert.True(ok);
        Assert.NotEmpty(pgfBytes!);
    }
}
