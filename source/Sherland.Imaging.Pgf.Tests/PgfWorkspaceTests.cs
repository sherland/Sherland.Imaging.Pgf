// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>pgf-codec-allocation-and-simd.md Stage 2 ownership and logical-length tests.</summary>
public class PgfWorkspaceTests
{
    [Fact]
    public void Rents_ExposeRequestedLogicalLength_NotPoolCapacity()
    {
        using var workspace = new PgfWorkspace();

        Memory<int> ints = workspace.RentInt32(3);
        Memory<uint> uints = workspace.RentUInt32(5);
        Memory<bool> booleans = workspace.RentBoolean(7);
        Memory<byte> bytes = workspace.RentByte(9);

        Assert.Equal(3, ints.Length);
        Assert.Equal(5, uints.Length);
        Assert.Equal(7, booleans.Length);
        Assert.Equal(9, bytes.Length);
    }

    [Fact]
    public void Dispose_ReturnsOwnership_AndRejectsFurtherRents()
    {
        var workspace = new PgfWorkspace();
        workspace.RentInt32Backing(32).AsSpan(0, 32).Fill(42);

        workspace.Dispose();
        workspace.Dispose();

        Assert.Throws<ObjectDisposedException>(() => workspace.RentInt32Backing(1));
    }

    [Fact]
    public void Reset_ReusesCompletedOperationBackingArrays()
    {
        using var workspace = new PgfWorkspace();
        int[] first = workspace.RentInt32Backing(32);

        workspace.Reset();

        int[] second = workspace.RentInt32Backing(32);
        Assert.Same(first, second);
    }

    [Fact]
    public void WorkspaceBackedDecode_MatchesTheLosslessSource()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(Oracle.NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        using var workspace = new PgfWorkspace();
        Assert.True(PgfImageDecoder.TryDecode(
            pgf!, static (bgra, _, _) => bgra.ToArray(), out byte[]? decoded, workspace: workspace, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(source, decoded);
    }

    [Fact]
    public void ResetAfterCompletedDecode_ReusesWorkspaceForAnotherDecode()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(Oracle.NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        using var workspace = new PgfWorkspace();
        Assert.True(PgfImageDecoder.TryDecode(pgf!, static (bgra, _, _) => bgra.ToArray(), out byte[]? first, workspace: workspace, cancellationToken: TestContext.Current.CancellationToken));
        workspace.Reset();
        Assert.True(PgfImageDecoder.TryDecode(pgf!, static (bgra, _, _) => bgra.ToArray(), out byte[]? second, workspace: workspace, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(first, second);
    }

    [Fact]
    public void ReusableDecoder_RewindsAndProducesTheSameLosslessPixelsTwice()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(Oracle.NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        using PgfReusableDecoder decoder = Assert.IsType<PgfReusableDecoder>(PgfReusableDecoder.TryOpen(pgf!));
        Assert.True(decoder.TryDecode(static (bgra, _, _) => bgra.ToArray(), out byte[]? first, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(decoder.TryDecode(static (bgra, _, _) => bgra.ToArray(), out byte[]? second, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(source, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ReusableDecoder_AfterWarmup_DoesNotAllocateCodecWorkingMemory()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(Oracle.NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        using PgfReusableDecoder decoder = Assert.IsType<PgfReusableDecoder>(PgfReusableDecoder.TryOpen(pgf!));
        PgfDecodedCallback<int> getLength = static (bgra, _, _) => bgra.Length;
        Assert.True(decoder.TryDecode(getLength, out int warmupLength, cancellationToken: TestContext.Current.CancellationToken));
        // The first rewind sizes the workspace's internal reuse lists. The steady-state contract
        // starts after that setup cycle, not merely after the first decode.
        Assert.True(decoder.TryDecode(getLength, out int recycledLength, cancellationToken: TestContext.Current.CancellationToken));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(decoder.TryDecode(getLength, out int decodedLength, cancellationToken: TestContext.Current.CancellationToken));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(warmupLength, recycledLength);
        Assert.Equal(recycledLength, decodedLength);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ReusableDecoder_CancellationLeavesTheNextDecodeAtTheStreamStart()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(Oracle.NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        using PgfReusableDecoder decoder = Assert.IsType<PgfReusableDecoder>(PgfReusableDecoder.TryOpen(pgf!));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            decoder.TryDecode(static (bgra, _, _) => bgra.Length, out _, cancellationToken: cancellation.Token));
        Assert.True(decoder.TryDecode(static (bgra, _, _) => bgra.ToArray(), out byte[]? decoded, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(source, decoded);
    }
}
