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
