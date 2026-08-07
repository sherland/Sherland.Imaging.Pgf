// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using BenchmarkDotNet.Attributes;

namespace Sherland.Imaging.Pgf.Benchmarks;

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

    /// <summary>Runs the same matrix through both the scalar fallback and vectorized lifting.</summary>
    [Params(false, true)]
    public bool ForceScalarVectors { get; set; }

    private byte[] bgra = null!;
    private byte[] destination = null!;
    private PgfWorkspace workspace = null!;

    [GlobalSetup]
    public void Setup()
    {
        PgfWaveletTransform.ForceScalarVectorsForTesting = ForceScalarVectors;
        bgra = Fixtures.Create(Fixture, Size, Size);
        PgfImageEncoder.TryEncode(bgra, Size, Size, Quality, out byte[]? expected);
        destination = new byte[expected!.Length];
        workspace = new PgfWorkspace();
    }

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

    /// <summary>Measures the explicit workspace plus caller-owned-output contract. Reset makes
    /// the completed operation's rents available to the next benchmark iteration.</summary>
    [Benchmark]
    public int ManagedWorkspaceEncode()
    {
        workspace.Reset();
        if (!PgfImageEncoder.TryEncodeMode(bgra, Size, Size, Quality, PgfConstants.ImageModeRGBA,
                destination, out int bytesWritten, workspace: workspace))
        {
            throw new InvalidOperationException("Managed workspace encode exceeded its prepared destination.");
        }

        return bytesWritten;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        workspace.Dispose();
        PgfWaveletTransform.ForceScalarVectorsForTesting = false;
    }
}
