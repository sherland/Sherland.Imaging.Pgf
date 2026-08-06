using BenchmarkDotNet.Attributes;

namespace PictTag.PgfCodec.Benchmarks;

/// <summary>Batched native-vs-managed encode comparison - the encode-side sibling of
/// <see cref="BatchDecodeBenchmarks"/>; see that class's doc comment for why this exists alongside
/// the existing single-image <see cref="EncodeBenchmarks"/>.</summary>
[MemoryDiagnoser]
public class BatchEncodeBenchmarks
{
    [Params(1, 10, 50)]
    public int BatchSize { get; set; }

    private BatchImage[] images = null!;
    private PgfWorkspace workspace = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        images = BatchFixtures.Create(BatchSize);
        workspace = new PgfWorkspace();
    }

    [Benchmark(Baseline = true)]
    public int NativeEncodeBatch()
    {
        int totalBytes = 0;
        foreach (BatchImage image in images)
        {
            NativePgf.TryEncode(image.Source, image.Width, image.Height, image.Quality, out byte[]? pgfBytes);
            totalBytes += pgfBytes!.Length;
        }

        return totalBytes;
    }

    [Benchmark]
    public int ManagedEncodeBatch()
    {
        int totalBytes = 0;
        foreach (BatchImage image in images)
        {
            PgfImageEncoder.TryEncode(image.Source, image.Width, image.Height, image.Quality, out byte[]? pgfBytes);
            totalBytes += pgfBytes!.Length;
        }

        return totalBytes;
    }

    /// <summary>Same batch through the workspace/caller-owned-output contract a real bulk-encode
    /// service would use (matching
    /// <c>SessionWorkloadBenchmarks.EncodeTwentyImagesInNewSession</c>).</summary>
    [Benchmark]
    public int ManagedWorkspaceEncodeBatch()
    {
        int totalBytes = 0;
        foreach (BatchImage image in images)
        {
            workspace.Reset();
            if (!PgfImageEncoder.TryEncodeMode(
                    image.Source, image.Width, image.Height, image.Quality, PgfConstants.ImageModeRGBA,
                    image.Destination, out int bytesWritten, workspace: workspace))
            {
                throw new InvalidOperationException($"Managed workspace encode failed for {image.Width}x{image.Height}.");
            }

            totalBytes += bytesWritten;
        }

        return totalBytes;
    }

    [GlobalCleanup]
    public void Cleanup() => workspace.Dispose();
}
