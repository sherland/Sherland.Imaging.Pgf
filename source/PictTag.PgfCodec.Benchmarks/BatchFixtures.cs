namespace PictTag.PgfCodec.Benchmarks;

/// <summary>Shared multi-image batch construction for <see cref="BatchDecodeBenchmarks"/>/
/// <see cref="BatchEncodeBenchmarks"/> - the same mixed-size/quality/pattern shape
/// <c>PictTag.PgfCodec.Performance</c>'s managed-only <c>SessionWorkloadBenchmarks</c> uses, so the
/// native comparison here models the identical realistic thumbnail-grid session rather than a
/// different workload shape that would make the two projects' numbers hard to compare.</summary>
internal static class BatchFixtures
{
    public static readonly (int Width, int Height)[] Dimensions =
    [
        (300, 120), (320, 180), (360, 200), (400, 225), (480, 270),
        (500, 300), (300, 200), (350, 240), (420, 280), (500, 400),
        (300, 300), (360, 320), (400, 360), (450, 380), (500, 500),
        (320, 256), (384, 288), (448, 336), (480, 480), (500, 360),
    ];

    /// <summary>Encodes every fixture through the native encoder (not the managed one), so a decode
    /// benchmark's input bytes never depend on the managed encoder under test - matching
    /// DecodeBenchmarks/ProgressiveDecodeBenchmarks' own [GlobalSetup] convention.</summary>
    public static BatchImage[] Create(int batchSize)
    {
        BatchImage[] images = new BatchImage[batchSize];
        for (int i = 0; i < batchSize; i++)
        {
            (int width, int height) = Dimensions[i % Dimensions.Length];
            byte quality = (byte)(i % 3 switch
            {
                0 => 0,
                1 => 8,
                _ => 15,
            });
            FixtureKind kind = i % 2 == 0 ? FixtureKind.Gradient : FixtureKind.Checkerboard;
            byte[] source = Fixtures.Create(kind, width, height);

            if (!NativePgf.TryEncode(source, width, height, quality, out byte[]? pgfBytes) || pgfBytes is null)
            {
                throw new InvalidOperationException($"Native fixture encode failed for {width}x{height}.");
            }

            images[i] = new BatchImage(width, height, quality, source, pgfBytes, new byte[source.Length]);
        }

        return images;
    }
}

internal sealed class BatchImage(
    int width, int height, byte quality, byte[] source, byte[] pgfBytes, byte[] destination)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte Quality { get; } = quality;
    public byte[] Source { get; } = source;
    public byte[] PgfBytes { get; } = pgfBytes;
    public byte[] Destination { get; } = destination;
}
