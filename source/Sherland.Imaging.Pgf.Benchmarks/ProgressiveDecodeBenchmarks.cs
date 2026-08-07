// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace Sherland.Imaging.Pgf.Benchmarks;

/// <summary>Full progressive decode sequence (every level, coarsest to finest, in one benchmark
/// iteration) - the actual production shape a dwell-gated thumbnail grid uses
/// (client-side-pgf-and-remove-thumbnail-cache.md), not just the single-shot path.</summary>
[MemoryDiagnoser]
public class ProgressiveDecodeBenchmarks
{
    [Params(256, 512)]
    public int Size { get; set; }

    [Params((byte)0, (byte)8)]
    public byte Quality { get; set; }

    [ParamsAllValues]
    public FixtureKind Fixture { get; set; }

    /// <summary>Runs the same matrix through both the scalar fallback and vectorized lifting.</summary>
    [Params(false, true)]
    public bool ForceScalarVectors { get; set; }

    private byte[] pgfBytes = null!;
    private PgfWorkspace workspace = null!;

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
        workspace = new PgfWorkspace();
    }

    [Benchmark(Baseline = true)]
    public int NativeProgressiveDecodeAllLevels()
    {
        nint handle = NativePgf.OpenHandle(pgfBytes, out int levels);
        int totalBytes = 0;
        try
        {
            for (int level = levels - 1; level >= 0; level--)
            {
                NativePgf.TryGetLevelSize(handle, level, out int w, out int h);
                int bufferSize = w * h * 4;
                byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
                try
                {
                    if (NativePgf.TryDecodeLevel(handle, level, rented.AsSpan(0, bufferSize)))
                    {
                        totalBytes += bufferSize;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        finally
        {
            NativePgf.CloseHandle(handle);
        }

        return totalBytes;
    }

    [Benchmark]
    public int ManagedProgressiveDecodeAllLevels()
    {
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes);
        if (decoder is null)
        {
            return 0;
        }

        int totalBytes = 0;
        for (int level = decoder.Levels - 1; level >= 0; level--)
        {
            decoder.TryDecodeLevel(level, static (bgra, w, h) => bgra.Length, out int? length);
            totalBytes += length ?? 0;
        }

        return totalBytes;
    }

    /// <summary>Measures the progressive API with its caller-owned workspace. The decoder remains
    /// per-operation, while completed buffer rents are deliberately recycled before the next open.</summary>
    [Benchmark]
    public int ManagedProgressiveDecodeWithWorkspace()
    {
        workspace.Reset();
        PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(pgfBytes, workspace: workspace);
        if (decoder is null)
        {
            return 0;
        }

        int totalBytes = 0;
        for (int level = decoder.Levels - 1; level >= 0; level--)
        {
            decoder.TryDecodeLevel(level, static (bgra, w, h) => bgra.Length, out int? length);
            totalBytes += length ?? 0;
        }

        return totalBytes;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        workspace.Dispose();
        PgfWaveletTransform.ForceScalarVectorsForTesting = false;
    }
}
