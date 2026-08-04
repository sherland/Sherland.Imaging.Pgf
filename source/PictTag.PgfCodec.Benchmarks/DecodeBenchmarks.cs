using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace PictTag.PgfCodec.Benchmarks;

/// <summary>Single-shot decode: managed (<see cref="PgfImageDecoder"/>) vs the existing native
/// P/Invoke path, both measured taking the same real fixture bytes to the same real BGRA output -
/// both legs rent their output buffer from <see cref="ArrayPool{T}"/> rather than allocating a fresh
/// array, matching what <c>PictTag.Data.PgfDecoding.PgfDecoder.TryDecode</c> actually does in
/// production, so <c>NativeDecode</c> here is a fair stand-in for it, not an unrealistically
/// allocation-heavy strawman.</summary>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    [Params(128, 256, 512)]
    public int Size { get; set; }

    [Params((byte)0, (byte)8, (byte)15)]
    public byte Quality { get; set; }

    [ParamsAllValues]
    public FixtureKind Fixture { get; set; }

    private byte[] pgfBytes = null!;
    private int bufferSize;

    [GlobalSetup]
    public void Setup()
    {
        byte[] bgra = Fixtures.Create(Fixture, Size, Size);
        if (!NativePgf.TryEncode(bgra, Size, Size, Quality, out byte[]? bytes))
        {
            throw new InvalidOperationException("Native encode failed during benchmark setup.");
        }

        pgfBytes = bytes!;
        bufferSize = Size * Size * 4;
    }

    [Benchmark(Baseline = true)]
    public bool NativeDecode()
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            return NativePgf.TryDecode(pgfBytes, rented.AsSpan(0, bufferSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark]
    public bool ManagedDecode()
    {
        PgfImageDecoder.TryDecode(pgfBytes, static (bgra, w, h) => true, out bool? ok);
        return ok == true;
    }
}
