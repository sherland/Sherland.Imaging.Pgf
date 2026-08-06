using System.Numerics;

using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;
using PictTag.PgfCodec;

namespace PictTag.PgfCodec.Performance;

/// <summary>
/// End-to-end managed-only workloads that represent how a thumbnail service uses the codec. These
/// are deliberately session-shaped rather than targeted kernel tests: each benchmark opens a fresh
/// workspace, processes a realistic set of images, and returns a checksum so the complete work cannot
/// be optimized away. The native comparison matrix remains in PictTag.PgfCodec.Benchmarks.
/// </summary>
[MemoryDiagnoser]
public class SessionWorkloadBenchmarks
{
    private static readonly (int Width, int Height)[] SessionDimensions =
    [
        (300, 120), (320, 180), (360, 200), (400, 225), (480, 270),
        (500, 300), (300, 200), (350, 240), (420, 280), (500, 400),
        (300, 300), (360, 320), (400, 360), (450, 380), (500, 500),
        (320, 256), (384, 288), (448, 336), (480, 480), (500, 360),
    ];

    private ImageCase[] images = null!;

    /// <summary>Runs the same end-to-end workload with the vector path and its scalar fallback so
    /// the harness can expose whether SIMD matters for a realistic session, not just a kernel.</summary>
    [Params(false, true)]
    public bool ForceScalarVectors { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        PgfWaveletTransform.ForceScalarVectorsForTesting = false;

        // Fixture creation and initial encoding are setup work. The timed methods model a session
        // after the service has received its image payloads, not fixture preparation.
        images = new ImageCase[50];
        for (int i = 0; i < images.Length; i++)
        {
            (int width, int height) = SessionDimensions[i % SessionDimensions.Length];
            byte quality = (byte)(i % 3 switch
            {
                0 => 0,
                1 => 8,
                _ => 15,
            });
            byte pattern = (byte)(i % 4);
            byte[] source = CreateBgra(width, height, pattern);

            if (!PgfImageEncoder.TryEncode(source, width, height, quality, out byte[]? pgfBytes)
                || pgfBytes is null)
            {
                throw new InvalidOperationException($"Managed fixture encode failed for {width}x{height}.");
            }

            images[i] = new ImageCase(width, height, quality, source, pgfBytes, new byte[pgfBytes.Length]);
        }
    }

    [IterationSetup]
    public void IterationSetup() => PgfWaveletTransform.ForceScalarVectorsForTesting = ForceScalarVectors;

    [IterationCleanup]
    public void IterationCleanup() => PgfWaveletTransform.ForceScalarVectorsForTesting = false;

    /// <summary>Actual grid-style use: open a new session and decode 50 thumbnails of mixed sizes.</summary>
    [Benchmark]
    public ulong DecodeFiftyImagesInNewSession()
    {
        using var workspace = new PgfWorkspace();
        ulong checksum = 0;

        foreach (ImageCase image in images)
        {
            if (!PgfImageDecoder.TryDecode(
                    image.PgfBytes,
                    static (bgra, width, height) => Checksum(bgra, width, height),
                    out ulong imageChecksum,
                    workspace: workspace))
            {
                throw new InvalidOperationException($"Managed decode failed for {image.Width}x{image.Height}.");
            }

            checksum = RotateAndMix(checksum, imageChecksum);
            workspace.Reset();
        }

        return checksum;
    }

    /// <summary>Actual progressive-grid use: decode every available level for 50 mixed thumbnails.</summary>
    [Benchmark]
    public ulong ProgressiveDecodeFiftyImagesInNewSession()
    {
        using var workspace = new PgfWorkspace();
        ulong checksum = 0;

        foreach (ImageCase image in images)
        {
            PgfProgressiveDecoder? decoder = PgfProgressiveDecoder.TryOpen(image.PgfBytes, workspace: workspace);
            if (decoder is null)
            {
                throw new InvalidOperationException($"Managed progressive open failed for {image.Width}x{image.Height}.");
            }

            if (decoder.Levels == 0)
            {
                checksum = DecodeProgressiveLevel(decoder, 0, checksum, image);
            }
            else
            {
                for (int level = decoder.Levels - 1; level >= 0; level--)
                {
                    checksum = DecodeProgressiveLevel(decoder, level, checksum, image);
                }
            }

            workspace.Reset();
        }

        return checksum;
    }

    /// <summary>Single-image use: start a session and encode one 500x500 image through the public
    /// owned-result API, as a caller that needs one completed PGF payload.</summary>
    [Benchmark]
    public ulong EncodeOneImageInNewSession()
    {
        ImageCase image = images[14];
        using var workspace = new PgfWorkspace();

        if (!PgfImageEncoder.TryEncode(image.Source, image.Width, image.Height, image.Quality,
                out byte[]? encoded, workspace: workspace)
            || encoded is null)
        {
            throw new InvalidOperationException("Managed single-image encode failed.");
        }

        return Checksum(encoded);
    }

    /// <summary>Bulk service use: start a session and encode 20 mixed-size images into caller-owned
    /// buffers while recycling one workspace between operations.</summary>
    [Benchmark]
    public ulong EncodeTwentyImagesInNewSession()
    {
        using var workspace = new PgfWorkspace();
        ulong checksum = 0;

        for (int i = 0; i < SessionDimensions.Length; i++)
        {
            ImageCase image = images[i];
            workspace.Reset();
            if (!PgfImageEncoder.TryEncodeMode(
                    image.Source, image.Width, image.Height, image.Quality, PgfConstants.ImageModeRGBA,
                    image.Destination, out int bytesWritten, workspace: workspace))
            {
                throw new InvalidOperationException($"Managed bulk encode failed for {image.Width}x{image.Height}.");
            }

            checksum = RotateAndMix(checksum, Checksum(image.Destination.AsSpan(0, bytesWritten)));
        }

        return checksum;
    }

    /// <summary>Bulk service use through the convenience API, retained as an explicit allocation
    /// baseline for future optimization work. It uses the same 20 source images as the caller-buffer
    /// workload above.</summary>
    [Benchmark]
    public ulong EncodeTwentyImagesWithOwnedResults()
    {
        using var workspace = new PgfWorkspace();
        ulong checksum = 0;

        for (int i = 0; i < SessionDimensions.Length; i++)
        {
            ImageCase image = images[i];
            workspace.Reset();
            if (!PgfImageEncoder.TryEncode(image.Source, image.Width, image.Height, image.Quality,
                    out byte[]? encoded, workspace: workspace)
                || encoded is null)
            {
                throw new InvalidOperationException($"Managed bulk encode failed for {image.Width}x{image.Height}.");
            }

            checksum = RotateAndMix(checksum, Checksum(encoded));
        }

        return checksum;
    }

    private static ulong DecodeProgressiveLevel(
        PgfProgressiveDecoder decoder, int level, ulong checksum, ImageCase image)
    {
        if (!decoder.TryDecodeLevel(
                level,
                static (bgra, width, height) => Checksum(bgra, width, height),
                out ulong levelChecksum))
        {
            throw new InvalidOperationException(
                $"Managed progressive decode failed for {image.Width}x{image.Height}, level {level}.");
        }

        return RotateAndMix(checksum, levelChecksum);
    }

    private static byte[] CreateBgra(int width, int height, byte pattern)
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

        return bgra;
    }

    private static ulong Checksum(ReadOnlySpan<byte> bytes, int width = 0, int height = 0)
    {
        ulong hash = 1469598103934665603UL ^ (uint)width ^ ((ulong)(uint)height << 32);
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong RotateAndMix(ulong left, ulong right) =>
        BitOperations.RotateLeft(left, 13) ^ right + 0x9E3779B97F4A7C15UL;

    private sealed class ImageCase(
        int width, int height, byte quality, byte[] source, byte[] pgfBytes, byte[] destination)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte Quality { get; } = quality;
        public byte[] Source { get; } = source;
        public byte[] PgfBytes { get; } = pgfBytes;
        public byte[] Destination { get; } = destination;
    }
}

/// <summary>Opt-in DiagnosticsHub variant. Keep profiler overhead and generated .diagsession files
/// out of ordinary timing runs; select this type explicitly with a BenchmarkDotNet filter.</summary>
[CPUUsageDiagnoser]
public class ProfiledSessionWorkloadBenchmarks : SessionWorkloadBenchmarks
{
}
