using System.Runtime.InteropServices;

namespace PictTag.PgfCodec.Tests.Oracle;

/// <summary>
/// Test-only P/Invoke wrapper around the native <c>PictTagPgfDecoder</c> shim
/// (<c>native/PictTag.PgfDecoder/shim.cpp</c>), covering both the existing production decode
/// exports (re-declared here rather than reused from <c>PictTag.Data.PgfDecoding.PgfDecoder</c>, to
/// keep this test project free of that project's EF Core/SQLite dependency) and the new test-only
/// encode/debug exports (<c>pgf_encode_bgra_alloc</c>/<c>pgf_free_encoded</c>/
/// <c>pgf_debug_decode_channel</c>) added specifically for this correctness test rig
/// (<c>new-features/managed-pgf-codec.md</c>). Never referenced by <see cref="PictTag.PgfCodec"/>
/// itself - this type exists only to serve as the real C++ oracle/round-trip-matrix leg in tests.
/// </summary>
internal static partial class NativePgfOracle
{
    private const string LibraryName = "PictTagPgfDecoder";

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_get_dimensions(nint data, nuint dataLen, out uint outWidth, out uint outHeight);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_decode_bgra(nint data, nuint dataLen, nint outBuffer, nuint outBufferLen);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_encode_bgra_alloc(
        nint bgra, uint width, uint height, byte quality, out nint outData, out nuint outLen);

    [LibraryImport(LibraryName)]
    private static partial void pgf_free_encoded(nint data);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_debug_decode_channel(
        nint data, nuint dataLen, int level, int channel,
        nint outBuffer, nuint outBufferLen, out uint outWidth, out uint outHeight);

    [LibraryImport(LibraryName)]
    private static partial nint pgf_open(nint data, nuint dataLen, out uint outWidth, out uint outHeight, out int outLevels);

    [LibraryImport(LibraryName)]
    private static partial void pgf_close(nint handle);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_level_size(nint handle, int level, out uint outWidth, out uint outHeight);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_decode_level_bgra(nint handle, int targetLevel, nint outBuffer, nuint outBufferLen);

    /// <summary>Opens (and immediately closes) via the existing, already-proven-safe production
    /// progressive-decode entry point (<c>pgf_open</c>/<c>pgf_close</c> - unlike
    /// <see cref="TryDebugDecodeChannel"/>, these are exercised extensively and safely elsewhere in
    /// this repo, e.g. <c>PictTag.Data.Tests.PgfDecoderTests</c>) purely to read back
    /// <c>Levels()</c> - the one header field <see cref="TryGetDimensions"/> doesn't expose, needed
    /// to cross-validate <c>PgfHeaderIO.ComputeLevels</c> against the real codec's own header
    /// parsing, not just this port's self-consistency.</summary>
    public static unsafe bool TryGetLevelCount(ReadOnlySpan<byte> pgfData, out int levels)
    {
        nint handle;
        uint w, h;
        int lvls;
        fixed (byte* dataPtr = pgfData)
        {
            handle = pgf_open((nint)dataPtr, (nuint)pgfData.Length, out w, out h, out lvls);
        }

        if (handle == 0)
        {
            levels = 0;
            return false;
        }

        pgf_close(handle);
        levels = lvls;
        return true;
    }

    /// <summary>Opens a real native progressive-decode handle (<c>pgf_open</c>) - the caller must
    /// pass the returned handle to <see cref="TryDecodeLevel"/> in decreasing level order (matching
    /// <c>CPGFImage::Read</c>'s own contract) and then to <see cref="CloseHandle"/>. Test-only
    /// equivalent of <c>PictTag.Data.PgfDecoding.PgfDecoder.OpenProgressive</c>, re-declared here
    /// (not reused) for the same reason as every other export in this class - see this class's own
    /// doc comment.</summary>
    public static unsafe nint OpenHandle(ReadOnlySpan<byte> pgfData, out int levels)
    {
        nint handle;
        fixed (byte* dataPtr = pgfData)
        {
            handle = pgf_open((nint)dataPtr, (nuint)pgfData.Length, out _, out _, out int lvls);
            levels = lvls;
        }

        return handle;
    }

    public static void CloseHandle(nint handle) => pgf_close(handle);

    /// <summary>Decodes one level of an already-open native progressive handle - the oracle leg for
    /// Tier 2's progressive-decode cross-check (managed-pgf-codec.md: "Cover both the single-shot
    /// TryDecode path and every progressive level via OpenProgressive/TryDecodeLevel").</summary>
    public static unsafe bool TryDecodeLevel(nint handle, int level, out byte[]? bgra, out int width, out int height)
    {
        bgra = null;
        width = height = 0;

        if (!pgf_level_size(handle, level, out uint w, out uint h))
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        byte[] buffer = new byte[checked(width * height * 4)];
        bool decoded;
        fixed (byte* bufferPtr = buffer)
        {
            decoded = pgf_decode_level_bgra(handle, level, (nint)bufferPtr, (nuint)buffer.Length);
        }

        if (!decoded)
        {
            return false;
        }

        bgra = buffer;
        return true;
    }

    public static unsafe bool TryGetDimensions(ReadOnlySpan<byte> pgfData, out int width, out int height)
    {
        bool ok;
        uint w, h;
        fixed (byte* dataPtr = pgfData)
        {
            ok = pgf_get_dimensions((nint)dataPtr, (nuint)pgfData.Length, out w, out h);
        }

        width = (int)w;
        height = (int)h;
        return ok;
    }

    /// <summary>Decodes with the real native decoder (unmodified production code path) - the decode
    /// oracle throughout this test rig.</summary>
    public static unsafe bool TryDecode(ReadOnlySpan<byte> pgfData, out byte[]? bgra, out int width, out int height)
    {
        bgra = null;

        if (!TryGetDimensions(pgfData, out width, out height))
        {
            return false;
        }

        int bufferSize = checked(width * height * 4);
        byte[] buffer = new byte[bufferSize];
        bool decoded;
        fixed (byte* dataPtr = pgfData)
        fixed (byte* bufferPtr = buffer)
        {
            decoded = pgf_decode_bgra((nint)dataPtr, (nuint)pgfData.Length, (nint)bufferPtr, (nuint)bufferSize);
        }

        if (!decoded)
        {
            return false;
        }

        bgra = buffer;
        return true;
    }

    /// <summary>Encodes a BGRA (top-down, pitch = width*4) bitmap with the real native encoder
    /// (<c>Encoder.cpp</c>, exported test-only via <c>pgf_encode_bgra_alloc</c> - see that
    /// function's doc comment in shim.cpp). One leg of the 4-way round-trip matrix
    /// (<c>new-features/managed-pgf-codec.md</c>). <paramref name="quality"/>: 0 = lossless, up to
    /// <c>MaxQuality</c> (15 in this build).</summary>
    public static unsafe bool TryEncode(
        ReadOnlySpan<byte> bgra, int width, int height, byte quality, out byte[]? pgfBytes)
    {
        pgfBytes = null;

        nint dataPtr;
        nuint dataLen;
        bool encoded;
        fixed (byte* bgraPtr = bgra)
        {
            encoded = pgf_encode_bgra_alloc((nint)bgraPtr, (uint)width, (uint)height, quality, out dataPtr, out dataLen);
        }

        if (!encoded)
        {
            return false;
        }

        try
        {
            byte[] result = new byte[dataLen];
            fixed (byte* resultPtr = result)
            {
                Buffer.MemoryCopy((void*)dataPtr, resultPtr, dataLen, dataLen);
            }

            pgfBytes = result;
            return true;
        }
        finally
        {
            pgf_free_encoded(dataPtr);
        }
    }

    /// <summary>Dumps one channel's raw post-decode/pre-colorconversion <c>DataT</c> (INT16) buffer
    /// after decoding down to <paramref name="level"/> - lets the C# port's own intermediate YUV
    /// channel data be compared stage-by-stage against this real oracle, isolating entropy-decode/
    /// inverse-transform correctness from color conversion (Tier 3,
    /// <c>new-features/managed-pgf-codec.md</c>).</summary>
    public static unsafe bool TryDebugDecodeChannel(
        ReadOnlySpan<byte> pgfData, int level, int channel, out short[]? channelData, out int width, out int height)
    {
        channelData = null;
        width = height = 0;

        // Full-resolution (level 0) dimensions are always >= any other level's pixel count - a safe
        // upper bound for the probe buffer without needing to know the target level's exact size
        // upfront (which the native side only computes once decoding has actually started).
        if (!TryGetDimensions(pgfData, out int fullWidth, out int fullHeight))
        {
            return false;
        }

        short[] buffer = new short[checked(fullWidth * fullHeight)];
        bool ok;
        uint w, h;
        fixed (byte* dataPtr = pgfData)
        fixed (short* bufferPtr = buffer)
        {
            ok = pgf_debug_decode_channel(
                (nint)dataPtr, (nuint)pgfData.Length, level, channel,
                (nint)bufferPtr, (nuint)buffer.Length, out w, out h);
        }

        if (!ok)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        channelData = buffer[..(width * height)];
        return true;
    }
}
