// Thin C API around digiKam's vendored libpgf codec (see libpgf/README, LGPL-2.1+). Replicates the
// exact real decode call sequence read from digiKam's own core/libs/pgfutils/pgfutils.cpp wrapper
// (ConfigureDecoder -> Open -> check Channels() == 4 -> Read -> GetBitmap), minus its Qt dependency.
//
// pgf_get_dimensions/pgf_decode_bgra write into a caller-supplied buffer rather than allocating and
// handing back an owned pointer - this is the performance-critical path (thumbnail decode runs per
// grid tile request), so there is no native-side allocation-and-copy plus a paired free/lifetime
// dance across the P/Invoke boundary: the caller (PictTag's P/Invoke wrapper) rents a buffer once
// with ArrayPool<byte> and this shim decodes straight into it.
//
// pgf_open/pgf_decode_level_bgra/pgf_close are a second, stateful API for progressive decoding
// (client-side rendering, not the server's per-request decode): CPGFImage::Read(level) already
// decodes incrementally from wherever it left off down to the requested level (confirmed in
// PGFimage.h - "the current level immediately after Open() is Levels()"), so calling it once per
// level in decreasing order, on the same still-open CPGFImage, renders progressively coarse-to-fine
// without re-decoding earlier levels. This deliberately does NOT attempt incremental *network*
// streaming (a persistent CPGFStream fed by arriving HTTP chunks, mid-decode) - PGF here is only
// ever a thumbnail-sized blob, small enough that buffering the whole response first is simpler and
// safe. A prior draft (new-features/client-side-pgf-and-remove-thumbnail-cache.md) proposed exactly
// that kind of incremental stream with a thrown C++ exception standing in for "not enough bytes
// yet, retry" - real CPGFStream::SetPos's signature doesn't match what that draft assumed, and
// Decoder.cpp's own catch/rethrow usage is for genuine corruption, not a designed resume point, so
// that approach was intentionally not built.
//
// The whole input buffer is copied into the handle at pgf_open time (std::vector<uint8_t>, not a
// borrowed pointer) so its lifetime doesn't depend on the C# caller keeping a GC'd array pinned
// across multiple separate P/Invoke calls between pgf_open and pgf_close.

#include "PGFimage.h"
#include <cstring>

// __declspec(dllexport) is an MSVC/Windows-DLL-specific extension - not meaningful (and not even
// syntactically accepted by clang without extra flags) for the Emscripten/WASM build target, where
// these functions are statically linked into one binary and .NET's own WASM build tooling controls
// which symbols survive linking, driven by which P/Invoke declarations reference them.
#if defined(_WIN32)
#define PICTTAG_EXPORT __declspec(dllexport)
#else
#define PICTTAG_EXPORT
#endif

struct PgfDecoderHandle
{
    // Plain heap buffer, not std::vector<uint8_t> - the .NET WASM SDK's NativeFileReference
    // compilation has no per-file compiler-flag scoping (confirmed: the only extension point,
    // EmccFlags, applies indiscriminately to every native file in the build, including the .NET
    // runtime's own .c files), and this Emscripten-bundled libc++'s <__utility/pair.h> fails to
    // instantiate under whatever C++ standard clang defaults to without an explicit -std= flag -
    // a flag that can't be added globally without breaking those .c files ("-std=c++17 not
    // allowed with 'C'"). Avoiding std::vector here (and the header that pulls it in) sidesteps
    // the problem entirely for the one file that needed it - the vendored libpgf/*.cpp files use
    // no STL and never hit this.
    uint8_t* buffer;
    size_t bufferLen;
    CPGFMemoryStream stream;
    CPGFImage img;

    PgfDecoderHandle(const uint8_t* data, size_t dataLen)
        : buffer(new uint8_t[dataLen]), bufferLen(dataLen), stream(buffer, dataLen)
    {
        memcpy(buffer, data, dataLen);
    }

    ~PgfDecoderHandle()
    {
        delete[] buffer;
    }
};

extern "C" {

PICTTAG_EXPORT bool pgf_get_dimensions(
    const uint8_t* data, size_t dataLen, uint32_t* outWidth, uint32_t* outHeight)
{
    if (data == nullptr || dataLen == 0 || outWidth == nullptr || outHeight == nullptr)
    {
        return false;
    }

    try
    {
        CPGFMemoryStream stream(const_cast<UINT8*>(data), dataLen);
        CPGFImage img;
        img.ConfigureDecoder(false);
        img.Open(&stream);

        if (img.Channels() != 4)
        {
            return false;
        }

        *outWidth = img.Width();
        *outHeight = img.Height();
        return true;
    }
    catch (...)
    {
        return false;
    }
}

PICTTAG_EXPORT bool pgf_decode_bgra(
    const uint8_t* data, size_t dataLen, uint8_t* outBuffer, size_t outBufferLen)
{
    if (data == nullptr || dataLen == 0 || outBuffer == nullptr)
    {
        return false;
    }

    try
    {
        CPGFMemoryStream stream(const_cast<UINT8*>(data), dataLen);
        CPGFImage img;
        img.ConfigureDecoder(false);
        img.Open(&stream);

        if (img.Channels() != 4)
        {
            return false;
        }

        uint32_t width = img.Width();
        uint32_t height = img.Height();
        int pitch = static_cast<int>(width) * 4;
        size_t requiredSize = static_cast<size_t>(pitch) * height;
        if (outBufferLen < requiredSize)
        {
            return false;
        }

        img.Read();

        // BGRA byte order (little-endian) - matches digiKam's own wrapper's channel map for
        // QImage::Format_ARGB32 on a little-endian host.
        int channelMap[] = { 0, 1, 2, 3 };
        img.GetBitmap(pitch, outBuffer, 32, channelMap);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

PICTTAG_EXPORT PgfDecoderHandle* pgf_open(
    const uint8_t* data, size_t dataLen, uint32_t* outWidth, uint32_t* outHeight, int* outLevels)
{
    if (data == nullptr || dataLen == 0)
    {
        return nullptr;
    }

    try
    {
        PgfDecoderHandle* handle = new PgfDecoderHandle(data, dataLen);
        handle->img.ConfigureDecoder(false);
        handle->img.Open(&handle->stream);

        if (handle->img.Channels() != 4)
        {
            delete handle;
            return nullptr;
        }

        if (outWidth) *outWidth = handle->img.Width();
        if (outHeight) *outHeight = handle->img.Height();
        if (outLevels) *outLevels = handle->img.Levels();
        return handle;
    }
    catch (...)
    {
        return nullptr;
    }
}

// Real per-level dimensions (CPGFImage::Width(level)/Height(level)) - call before
// pgf_decode_level_bgra for a given level to size the output buffer, same two-step pattern as
// pgf_get_dimensions -> pgf_decode_bgra above.
PICTTAG_EXPORT bool pgf_level_size(
    PgfDecoderHandle* handle, int level, uint32_t* outWidth, uint32_t* outHeight)
{
    if (handle == nullptr || outWidth == nullptr || outHeight == nullptr || level < 0)
    {
        return false;
    }

    try
    {
        *outWidth = handle->img.Width(level);
        *outHeight = handle->img.Height(level);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

// Decodes progressively down to targetLevel (0 = full resolution, Levels()-1 = coarsest) on a
// handle from pgf_open. Levels must be requested in decreasing order (Levels()-1, Levels()-2, ...,
// 0) - matching CPGFImage::Read's own semantics of continuing from wherever it left off - but a
// caller that only wants the final image can also call this once with targetLevel 0 directly.
PICTTAG_EXPORT bool pgf_decode_level_bgra(
    PgfDecoderHandle* handle, int targetLevel, uint8_t* outBuffer, size_t outBufferLen)
{
    if (handle == nullptr || outBuffer == nullptr || targetLevel < 0)
    {
        return false;
    }

    try
    {
        uint32_t width = handle->img.Width(targetLevel);
        uint32_t height = handle->img.Height(targetLevel);
        int pitch = static_cast<int>(width) * 4;
        size_t requiredSize = static_cast<size_t>(pitch) * height;
        if (outBufferLen < requiredSize)
        {
            return false;
        }

        handle->img.Read(targetLevel);

        int channelMap[] = { 0, 1, 2, 3 };
        handle->img.GetBitmap(pitch, outBuffer, 32, channelMap);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

PICTTAG_EXPORT void pgf_close(PgfDecoderHandle* handle)
{
    delete handle;
}

} // extern "C"
