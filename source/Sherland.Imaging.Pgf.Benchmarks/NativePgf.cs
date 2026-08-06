using System.Runtime.InteropServices;

namespace Sherland.Imaging.Pgf.Benchmarks;

/// <summary>
/// Minimal P/Invoke wrapper around the native <c>SherlandImagingPgfNative</c> shim for this benchmark rig's
/// managed-vs-native comparisons (Stage 10, new-features/managed-pgf-codec.md) - re-declares the same
/// exports <c>Sherland.Imaging.Pgf.Tests.Oracle.NativePgfOracle</c> does rather than referencing that test
/// project (a benchmark exe depending on a test project's own dependencies would be backwards) or
/// <c>PictTag.Data</c> (would pull EF Core/SQLite into a benchmark exe for no reason - this rig never
/// touches a digiKam database).
/// </summary>
internal static partial class NativePgf
{
    private const string LibraryName = "SherlandImagingPgfNative";

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
    private static partial nint pgf_open(nint data, nuint dataLen, out uint outWidth, out uint outHeight, out int outLevels);

    [LibraryImport(LibraryName)]
    private static partial void pgf_close(nint handle);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_level_size(nint handle, int level, out uint outWidth, out uint outHeight);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_decode_level_bgra(nint handle, int targetLevel, nint outBuffer, nuint outBufferLen);

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

    public static unsafe bool TryDecode(ReadOnlySpan<byte> pgfData, Span<byte> destination)
    {
        bool decoded;
        fixed (byte* dataPtr = pgfData)
        fixed (byte* bufferPtr = destination)
        {
            decoded = pgf_decode_bgra((nint)dataPtr, (nuint)pgfData.Length, (nint)bufferPtr, (nuint)destination.Length);
        }

        return decoded;
    }

    public static unsafe bool TryEncode(ReadOnlySpan<byte> bgra, int width, int height, byte quality, out byte[]? pgfBytes)
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

    public static bool TryGetLevelSize(nint handle, int level, out int width, out int height)
    {
        bool ok = pgf_level_size(handle, level, out uint w, out uint h);
        width = (int)w;
        height = (int)h;
        return ok;
    }

    public static unsafe bool TryDecodeLevel(nint handle, int level, Span<byte> destination)
    {
        bool decoded;
        fixed (byte* bufferPtr = destination)
        {
            decoded = pgf_decode_level_bgra(handle, level, (nint)bufferPtr, (nuint)destination.Length);
        }

        return decoded;
    }
}
