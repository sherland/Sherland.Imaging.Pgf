using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace Sherland.Imaging.Pgf.Benchmarks;

/// <summary>Batched native-vs-managed decode comparison. <see cref="DecodeBenchmarks"/> measures one
/// image per <c>[Benchmark]</c> invocation; this class instead processes a realistic multi-image
/// batch per invocation - the same mixed-size/quality shape <c>Sherland.Imaging.Pgf.Performance</c>'s
/// managed-only <c>SessionWorkloadBenchmarks</c> models. That project's own doc comment explicitly
/// defers "the native comparison matrix" to this project - this class is that deferred comparison.
/// <c>[Params(1, 10, 50)]</c> spans a single thumbnail load up to a full grid page, so
/// <see cref="DecodeBenchmarks"/>' single-image ratio can be checked against whether it narrows,
/// widens, or holds at realistic batch sizes. See docs/benchmarks/pgfcodec/README.md's "Native vs.
/// managed" table for the interpreted results and its interop-fairness caveat.</summary>
[MemoryDiagnoser]
public class BatchDecodeBenchmarks
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
    public int NativeDecodeBatch()
    {
        int totalBytes = 0;
        foreach (BatchImage image in images)
        {
            int bufferSize = image.Width * image.Height * 4;
            byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                if (NativePgf.TryDecode(image.PgfBytes, rented.AsSpan(0, bufferSize)))
                {
                    totalBytes += bufferSize;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return totalBytes;
    }

    [Benchmark]
    public int ManagedDecodeBatch()
    {
        int totalBytes = 0;
        foreach (BatchImage image in images)
        {
            PgfImageDecoder.TryDecode(image.PgfBytes, static (bgra, w, h) => bgra.Length, out int length);
            totalBytes += length;
        }

        return totalBytes;
    }

    /// <summary>Same batch through the reusable-session/workspace contract a real grid-loading
    /// service would actually use (matching
    /// <c>SessionWorkloadBenchmarks.DecodeFiftyImagesInNewSession</c>), not the always-allocating
    /// convenience API above.</summary>
    [Benchmark]
    public int ManagedReusableDecodeBatch()
    {
        workspace.Reset();
        int totalBytes = 0;
        foreach (BatchImage image in images)
        {
            PgfImageDecoder.TryDecode(
                image.PgfBytes, static (bgra, w, h) => bgra.Length, out int length, workspace: workspace);
            totalBytes += length;
            workspace.Reset();
        }

        return totalBytes;
    }

    [GlobalCleanup]
    public void Cleanup() => workspace.Dispose();
}
