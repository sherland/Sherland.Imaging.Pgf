// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace Sherland.Imaging.Pgf.Benchmarks;

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

    /// <summary>Runs the same matrix through the forced scalar fallback and the hardware-vector
    /// path, so the SIMD decision never relies on timing from separate benchmark executions.</summary>
    [Params(false, true)]
    public bool ForceScalarVectors { get; set; }

    private byte[] pgfBytes = null!;
    private int bufferSize;
    private PgfReusableDecoder reusableDecoder = null!;

    [GlobalSetup]
    public void Setup()
    {
        PgfWaveletTransform.ForceScalarVectorsForTesting = ForceScalarVectors;
        byte[] bgra = Fixtures.Create(Fixture, Size, Size);
        if (!NativePgf.TryEncode(bgra, Size, Size, Quality, out byte[]? bytes))
        {
            throw new InvalidOperationException("Native encode failed during benchmark setup.");
        }

        pgfBytes = bytes!;
        bufferSize = Size * Size * 4;
        reusableDecoder = PgfReusableDecoder.TryOpen(pgfBytes)
            ?? throw new InvalidOperationException("Managed reusable decode setup failed.");
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

    /// <summary>Measures the opt-in steady-state decode contract: the parsed session and its
    /// workspace remain owned by one explicit disposable decoder across iterations.</summary>
    [Benchmark]
    public bool ManagedReusableDecode()
    {
        return reusableDecoder.TryDecode(static (bgra, w, h) => true, out bool? ok) && ok == true;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        reusableDecoder.Dispose();
        PgfWaveletTransform.ForceScalarVectorsForTesting = false;
    }
}
