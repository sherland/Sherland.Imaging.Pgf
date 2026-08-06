using System.Numerics;

using PictTag.PgfCodec.Tests.Oracle;

namespace PictTag.PgfCodec.Tests;

/// <summary>
/// Verifies that the digest used by the managed-only session benchmark produces the same value
/// for managed and native decode results. The digest is only an anti-elision aid, so this test also
/// asserts the complete decoded pixel buffers directly; a digest collision must not hide a mismatch.
/// </summary>
public sealed class PgfSessionChecksumOracleTests
{
    public static TheoryData<int, int, byte, int> SessionFixtures => new()
    {
        { 10, 10, 0, 0 },
        { 37, 23, 8, 1 },
        { 64, 64, 15, 2 },
    };

    [Theory]
    [MemberData(nameof(SessionFixtures))]
    public void DecodeChecksum_AgreesBetweenManagedAndNative(int width, int height, byte quality, int pattern)
    {
        (byte[] source, _, _) = CreateFixture(width, height, pattern);

        Assert.True(NativePgfOracle.TryEncode(source, width, height, quality, out byte[]? pgfBytes));
        Assert.NotNull(pgfBytes);

        Assert.True(
            PgfImageDecoder.TryDecode(
                pgfBytes,
                static (bgra, decodedWidth, decodedHeight) =>
                    (Bytes: bgra.ToArray(), Width: decodedWidth, Height: decodedHeight),
                out (byte[] Bytes, int Width, int Height) managed));
        Assert.True(NativePgfOracle.TryDecode(
            pgfBytes,
            out byte[]? native,
            out int nativeWidth,
            out int nativeHeight));
        Assert.NotNull(native);

        Assert.Equal((width, height), (managed.Width, managed.Height));
        Assert.Equal((width, height), (nativeWidth, nativeHeight));
        Assert.Equal(native, managed.Bytes);
        Assert.Equal(
            Checksum(managed.Bytes, managed.Width, managed.Height),
            Checksum(native!, nativeWidth, nativeHeight));
    }

    private static (byte[] Bgra, int Width, int Height) CreateFixture(int width, int height, int pattern)
    {
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte checker = (byte)((x / 8 + y / 8 + pattern) % 2 == 0 ? 255 : 24);
                bgra[offset] = (byte)((x * 255 / Math.Max(1, width - 1) + pattern * 17) & 255);
                bgra[offset + 1] = (byte)((y * 255 / Math.Max(1, height - 1) + pattern * 29) & 255);
                bgra[offset + 2] = pattern % 2 == 0 ? checker : (byte)((x + y + pattern * 31) & 255);
                bgra[offset + 3] = 255;
            }
        }

        return (bgra, width, height);
    }

    private static ulong Checksum(ReadOnlySpan<byte> bytes, int width, int height)
    {
        ulong hash = 1469598103934665603UL ^ (uint)width ^ ((ulong)(uint)height << 32);
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
