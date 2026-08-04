namespace PictTag.PgfCodec.Tests;

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
    public void WorkspaceBackedDecode_MatchesTheLosslessSource()
    {
        (byte[] source, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(Oracle.NativePgfOracle.TryEncode(source, width, height, quality: 0, out byte[]? pgf));

        using var workspace = new PgfWorkspace();
        Assert.True(PgfImageDecoder.TryDecode(
            pgf!, static (bgra, _, _) => bgra.ToArray(), out byte[]? decoded, workspace: workspace));

        Assert.Equal(source, decoded);
    }
}
