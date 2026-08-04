using BenchmarkDotNet.Attributes;

namespace PictTag.PgfCodec.Benchmarks;

/// <summary>Single-shot encode: managed (<see cref="PgfImageEncoder"/>) vs the native shim's test-only
/// <c>pgf_encode_bgra_alloc</c> export (there is no production encode call site to compare against -
/// PictTag never writes PGF files, see CLAUDE.md's "read-only by design" GUI scope boundary - this
/// benchmark exists purely to characterize the new managed encoder itself, informative rather than a
/// regression-guard against an existing production path).</summary>
[MemoryDiagnoser]
public class EncodeBenchmarks
{
    [Params(128, 256, 512)]
    public int Size { get; set; }

    [Params((byte)0, (byte)8, (byte)15)]
    public byte Quality { get; set; }

    [ParamsAllValues]
    public FixtureKind Fixture { get; set; }

    private byte[] bgra = null!;

    [GlobalSetup]
    public void Setup() => bgra = Fixtures.Create(Fixture, Size, Size);

    [Benchmark(Baseline = true)]
    public int NativeEncode()
    {
        NativePgf.TryEncode(bgra, Size, Size, Quality, out byte[]? pgfBytes);
        return pgfBytes!.Length;
    }

    [Benchmark]
    public int ManagedEncode()
    {
        PgfImageEncoder.TryEncode(bgra, Size, Size, Quality, out byte[]? pgfBytes);
        return pgfBytes!.Length;
    }
}
