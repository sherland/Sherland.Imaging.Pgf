namespace PictTag.PgfCodec.Benchmarks;

/// <summary>Synthetic BGRA fixtures at realistic digiKam thumbnail dimensions (docs/GUI.md: the
/// thumbnail API is requested as <c>GET /thumbnails/{id}?size=256</c>, and PGF here is always a
/// thumbnail-sized blob, never a full original) - 128/256/512 span the small/typical/detail-view
/// range without needing a real digiKam library on hand.</summary>
internal static class Fixtures
{
    public static byte[] Create(FixtureKind kind, int width, int height) => kind switch
    {
        FixtureKind.Gradient => Gradient(width, height),
        FixtureKind.Checkerboard => Checkerboard(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static byte[] Gradient(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                bgra[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                bgra[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                bgra[i + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 1));
                bgra[i + 3] = 255;
            }
        }

        return bgra;
    }

    private static byte[] Checkerboard(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                byte value = ((x / 4) + (y / 4)) % 2 == 0 ? (byte)255 : (byte)0;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = value;
                bgra[i + 3] = 255;
            }
        }

        return bgra;
    }
}

/// <summary>Deterministic thumbnail-content classes used by the allocation/throughput matrix.</summary>
public enum FixtureKind
{
    Gradient,
    Checkerboard,
}
