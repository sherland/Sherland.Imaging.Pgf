using BenchmarkDotNet.Running;
using Sherland.Imaging.Pgf;
using Sherland.Imaging.Pgf.Benchmarks;

// "Output size per quality value" (managed-pgf-codec.md's "Test rig: performance") isn't a
// latency/allocation metric BenchmarkDotNet reports on, so it's printed directly here rather than
// forced into a [Benchmark] method - real numbers from both encoders, not assumed to match just
// because Stage 8's round-trip matrix already proved pixel-identical decoded output.
if (args.Contains("--sizes"))
{
    PrintOutputSizeSweep();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

static void PrintOutputSizeSweep()
{
    Console.WriteLine("Output size per quality value (256x256 gradient fixture):");
    Console.WriteLine($"{"quality",8} {"native",10} {"managed",10}");

    byte[] bgra = Fixtures.Gradient(256, 256);
    for (byte quality = 0; quality <= PgfConstants.MaxQuality; quality++)
    {
        NativePgf.TryEncode(bgra, 256, 256, quality, out byte[]? nativeBytes);
        PgfImageEncoder.TryEncode(bgra, 256, 256, quality, out byte[]? managedBytes);
        Console.WriteLine($"{quality,8} {nativeBytes!.Length,10} {managedBytes!.Length,10}");
    }
}
