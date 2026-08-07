// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using System.Runtime.InteropServices;

namespace Sherland.Imaging.Pgf.Tests.Oracle;

/// <summary>
/// Test-only P/Invoke wrapper around the native <c>SherlandImagingPgfNative</c> shim
/// (<c>native/Sherland.Imaging.Pgf.Native/shim.cpp</c>), covering both the existing production decode
/// exports (re-declared here rather than reused from <c>PictTag.Data.PgfDecoding.PgfDecoder</c>, to
/// keep this test project free of that project's EF Core/SQLite dependency) and the new test-only
/// encode/debug exports (<c>pgf_encode_bgra_alloc</c>/<c>pgf_free_encoded</c>/
/// <c>pgf_debug_decode_channel</c>) added specifically for this correctness test rig
/// (<c>new-features/managed-pgf-codec.md</c>). Never referenced by <see cref="Sherland.Imaging.Pgf"/>
/// itself - this type exists only to serve as the real C++ oracle/round-trip-matrix leg in tests.
/// </summary>
internal static partial class NativePgfOracle
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
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_encode_bgra_alloc_roi(
        nint bgra, uint width, uint height, byte quality, out nint outData, out nuint outLen);

    [LibraryImport(LibraryName)]
    private static partial void pgf_free_encoded(nint data);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_encode_raw_alloc(
        nint source, uint width, uint height, byte quality, byte mode, byte bpp, byte channels,
        nint colorTable, uint colorTableLen, out nint outData, out nuint outLen);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_encode_bitmap_legacy_alloc(
        nint packedBits, uint width, uint height, [MarshalAs(UnmanagedType.U1)] bool clearVersion5,
        out nint outData, out nuint outLen);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_encode_legacy_interleaved_raw_alloc(
        nint source, uint width, uint height, byte quality, byte mode, byte bpp, byte channels,
        nint colorTable, uint colorTableLen, out nint outData, out nuint outLen);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_debug_decode_channel(
        nint data, nuint dataLen, int level, int channel,
        nint outBuffer, nuint outBufferLen, out uint outWidth, out uint outHeight);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_debug_get_header_info(
        nint data, nuint dataLen, out uint outWidth, out uint outHeight, out int outLevels,
        out byte outMode, out byte outBpp, out byte outChannels, out byte outUsedBitsPerChannel);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_debug_get_level_lengths(
        nint data, nuint dataLen, nint outLevelLengths, int levelLengthsCapacity, out int outLevelCount);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool pgf_debug_decode_raw(
        nint data, nuint dataLen, byte bpp, nint channelMap, int channelMapLen,
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

    /// <summary>pgf-all-image-modes.md Stage 1's header-round-trip oracle: opens via real
    /// <c>CPGFImage::Open()</c> (so <c>CompleteHeader</c>'s real validation genuinely runs) and
    /// reports back every raw header field, for any mode - unlike <see cref="TryGetDimensions"/>/
    /// <see cref="OpenHandle"/>, which both hardcode an RGBA-only (<c>Channels()==4</c>)
    /// restriction the base PRD's test rig never needed to lift.</summary>
    public static unsafe bool TryGetHeaderInfo(
        ReadOnlySpan<byte> pgfData, out int width, out int height, out int levels,
        out byte mode, out byte bpp, out byte channels, out byte usedBitsPerChannel)
    {
        bool ok;
        uint w, h;
        fixed (byte* dataPtr = pgfData)
        {
            ok = pgf_debug_get_header_info(
                (nint)dataPtr, (nuint)pgfData.Length, out w, out h, out levels,
                out mode, out bpp, out channels, out usedBitsPerChannel);
        }

        width = (int)w;
        height = (int)h;
        return ok;
    }

    /// <summary>pgf-real-level-lengths.md Stage 4: real per-level byte lengths, reported via
    /// <c>CPGFImage::GetEncodedLevelLength</c> (shim.cpp's <c>pgf_debug_get_level_lengths</c> doc
    /// comment) - the native oracle leg for <see cref="Sherland.Imaging.Pgf.PgfProgressiveDecoder.
    /// TryGetLevelLength"/>'s cross-implementation verification (Stage 5). Learns <c>Levels()</c>
    /// via <see cref="TryGetHeaderInfo"/> first so the caller-supplied buffer is sized correctly -
    /// the shim itself fails closed on an undersized one rather than truncating. Index order matches
    /// the public level-0-is-full-resolution convention both this port's and the native's own public
    /// accessor use, not the raw on-wire array order.</summary>
    public static unsafe bool TryGetLevelLengths(ReadOnlySpan<byte> pgfData, out uint[]? levelLengths)
    {
        levelLengths = null;

        if (!TryGetHeaderInfo(pgfData, out _, out _, out int levels, out _, out _, out _, out _))
        {
            return false;
        }

        uint[] buffer = new uint[Math.Max(levels, 1)];
        bool ok;
        int reportedLevelCount;
        fixed (byte* dataPtr = pgfData)
        fixed (uint* bufferPtr = buffer)
        {
            ok = pgf_debug_get_level_lengths(
                (nint)dataPtr, (nuint)pgfData.Length, (nint)bufferPtr, buffer.Length, out reportedLevelCount);
        }

        if (!ok)
        {
            return false;
        }

        levelLengths = buffer[..reportedLevelCount];
        return true;
    }

    /// <summary>General-purpose decode oracle for any mode (pgf-all-image-modes.md): asks
    /// <c>GetBitmap</c> for exactly <paramref name="bpp"/>/<paramref name="channelMap"/>'s worth of
    /// interleaving - e.g. <c>bpp:8, channelMap:[0]</c> for a single grayscale/indexed channel,
    /// <c>bpp:24, channelMap:[0,1,2]</c> for a 3-channel HSL/HSB/Lab-family mode - rather than
    /// <see cref="TryDecode"/>'s hardcoded RGBA/32bpp shape. Bytes GetBitmap never touches (a wider
    /// caller-requested stride than the mode's own real channel count) come back zeroed (the native
    /// side's own <c>memset</c>), not uninitialized, so a caller can tell "real reconstructed byte"
    /// from "not part of this mode's own output" - see <c>pgf_debug_decode_raw</c>'s doc comment.</summary>
    public static unsafe bool TryDecodeRaw(
        ReadOnlySpan<byte> pgfData, byte bpp, ReadOnlySpan<int> channelMap, out byte[]? raw, out int width, out int height)
    {
        raw = null;
        width = height = 0;

        if (!TryGetDimensions(pgfData, out int fullWidth, out int fullHeight))
        {
            // TryGetDimensions itself hardcodes Channels()==4, so it can't be reused for non-RGBA
            // modes - fall back to the header-info oracle purely for dimensions here.
            if (!TryGetHeaderInfo(pgfData, out fullWidth, out fullHeight, out _, out _, out _, out _, out _))
            {
                return false;
            }
        }

        // Ceiling bits-to-bytes-per-row, matching the native shim's own pitch formula exactly
        // (pgf_debug_decode_raw's doc comment) - width*(bpp/8) truncates to 0 for bpp==1 (Bitmap).
        int bufferSize = checked((int)(((long)fullWidth * bpp + 7) / 8) * fullHeight);
        byte[] buffer = new byte[bufferSize];
        bool ok;
        uint w, h;
        fixed (byte* dataPtr = pgfData)
        fixed (int* channelMapPtr = channelMap)
        fixed (byte* bufferPtr = buffer)
        {
            ok = pgf_debug_decode_raw(
                (nint)dataPtr, (nuint)pgfData.Length, bpp, (nint)channelMapPtr, channelMap.Length,
                (nint)bufferPtr, (nuint)buffer.Length, out w, out h);
        }

        if (!ok)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        raw = buffer;
        return true;
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
    /// <c>MaxQuality</c> (31 in this build - pgf-all-image-modes.md's DataT correction; the real
    /// oracle build genuinely has <c>__PGF32SUPPORT__</c> active, not 15 as earlier docs assumed).</summary>
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

    /// <summary>pgf-roi-support.md Stage 5: real-native-encoder leg of the cross-implementation ROI
    /// round-trip matrix - identical to <see cref="TryEncode"/> except the produced file is
    /// ROI-flagged/tile-structured (<c>pgf_encode_bgra_alloc_roi</c>, shim.cpp's own doc comment: the
    /// one real difference is <c>SetHeader(header, PGFROI)</c>).</summary>
    public static unsafe bool TryEncodeRoi(
        ReadOnlySpan<byte> bgra, int width, int height, byte quality, out byte[]? pgfBytes)
    {
        pgfBytes = null;

        nint dataPtr;
        nuint dataLen;
        bool encoded;
        fixed (byte* bgraPtr = bgra)
        {
            encoded = pgf_encode_bgra_alloc_roi((nint)bgraPtr, (uint)width, (uint)height, quality, out dataPtr, out dataLen);
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

    /// <summary>pgf-all-image-modes.md Stage 9: general mode-parameterized encode with the real
    /// native encoder - generalizes <see cref="TryEncode"/> to every mode this PRD covers, closing
    /// the "native encode" leg of the round-trip matrix for non-RGBA modes (see
    /// <c>pgf_encode_raw_alloc</c>'s doc comment in shim.cpp). <paramref name="source"/> must be
    /// shaped exactly like <see cref="Sherland.Imaging.Pgf.PgfImageEncoder.TryEncodeMode"/>'s own input
    /// contract for <paramref name="mode"/> (tightly packed, the mode's own real bpp).
    /// <paramref name="colorTable"/> is only meaningful for
    /// <see cref="Sherland.Imaging.Pgf.PgfConstants.ImageModeIndexedColor"/>.</summary>
    public static unsafe bool TryEncodeMode(
        ReadOnlySpan<byte> source, int width, int height, byte quality, byte mode, byte bpp, byte channels,
        ReadOnlySpan<byte> colorTable, out byte[]? pgfBytes)
    {
        pgfBytes = null;

        nint dataPtr;
        nuint dataLen;
        bool encoded;
        fixed (byte* sourcePtr = source)
        fixed (byte* colorTablePtr = colorTable)
        {
            encoded = pgf_encode_raw_alloc(
                (nint)sourcePtr, (uint)width, (uint)height, quality, mode, bpp, channels,
                (nint)colorTablePtr, (uint)colorTable.Length, out dataPtr, out dataLen);
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

    /// <summary>Creates a valid pre-Version7 Bitmap fixture using the native test-only writer in
    /// <c>shim.cpp</c>. When <paramref name="clearVersion5"/> is true it additionally emits the
    /// pre-Version5 interleaved entropy layout; this is deliberately not a header-only mutation,
    /// because <c>CPGFImage::Read</c> dispatches HL/LH entropy decoding on that same flag. The
    /// helper therefore lets the legacy Bitmap PRD verify the rare <c>yw=w2</c> stride with the
    /// independent native decoder rather than a self-consistent malformed payload.</summary>
    public static unsafe bool TryEncodeLegacyBitmap(
        ReadOnlySpan<byte> packedBits, int width, int height, bool clearVersion5, out byte[]? pgfBytes)
    {
        pgfBytes = null;
        nint dataPtr;
        nuint dataLen;
        bool encoded;
        fixed (byte* sourcePtr = packedBits)
        {
            encoded = pgf_encode_bitmap_legacy_alloc(
                (nint)sourcePtr, (uint)width, (uint)height, clearVersion5, out dataPtr, out dataLen);
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

    /// <summary>Creates a synthetic pre-Version5 fixture for any supported non-Bitmap mode using
    /// the native shim's real import, color-conversion, wavelet, and macroblock machinery. Only the
    /// historical HL/LH interleaved write order is supplied by the test-only shim. Validate the
    /// result with <see cref="TryDecodeRaw"/>/<c>GetBitmap</c>; do not use the repeated-call-unsafe
    /// <see cref="TryDebugDecodeChannel"/> in an automated matrix.</summary>
    public static unsafe bool TryEncodeLegacyInterleavedMode(
        ReadOnlySpan<byte> source, int width, int height, byte quality, byte mode, byte bpp, byte channels,
        ReadOnlySpan<byte> colorTable, out byte[]? pgfBytes)
    {
        pgfBytes = null;
        fixed (byte* sourcePtr = source)
        fixed (byte* colorTablePtr = colorTable)
        {
            bool encoded = pgf_encode_legacy_interleaved_raw_alloc(
                (nint)sourcePtr, (uint)width, (uint)height, quality, mode, bpp, channels,
                (nint)colorTablePtr, (uint)colorTable.Length, out nint dataPtr, out nuint dataLen);
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
    }

    /// <summary>Dumps one channel's raw post-decode/pre-colorconversion <c>DataT</c> (INT32 - see
    /// <c>Sherland.Imaging.Pgf.PgfConstants</c>'s doc comment: the real oracle build genuinely has
    /// <c>__PGF32SUPPORT__</c> active, not INT16 as earlier docs in this repo assumed) buffer after
    /// decoding down to <paramref name="level"/> - lets the C# port's own intermediate YUV channel
    /// data be compared stage-by-stage against this real oracle, isolating entropy-decode/
    /// inverse-transform correctness from color conversion (Tier 3,
    /// <c>new-features/managed-pgf-codec.md</c>).
    ///
    /// pgf-all-image-modes.md correction: the native shim's <c>outBuffer</c> parameter and this
    /// wrapper's probe buffer were previously <c>int16_t</c>/<see cref="short"/>, while the real
    /// <c>memcpy(outBuffer, channelData, required * sizeof(DataT))</c> on the native side always
    /// copies <c>required * 4</c> bytes (real <c>sizeof(DataT) == 4</c>) - a native heap-buffer
    /// overflow of up to <c>required * 2</c> bytes past the old, too-small buffer, latent because
    /// this method was never actually called from any test. Both sides now agree on
    /// <c>int32_t</c>/<see cref="int"/>.</summary>
    public static unsafe bool TryDebugDecodeChannel(
        ReadOnlySpan<byte> pgfData, int level, int channel, out int[]? channelData, out int width, out int height)
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

        int[] buffer = new int[checked(fullWidth * fullHeight)];
        bool ok;
        uint w, h;
        fixed (byte* dataPtr = pgfData)
        fixed (int* bufferPtr = buffer)
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
