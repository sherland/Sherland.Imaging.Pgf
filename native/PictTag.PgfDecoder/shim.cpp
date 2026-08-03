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

// __declspec(dllexport) is an MSVC/Windows-DLL-specific extension for the desktop DLL build.
//
// For the Emscripten/WASM build target, EMSCRIPTEN_KEEPALIVE is the equivalent that actually
// matters - confirmed the hard way (real DllNotFoundException("__Internal") at runtime, on a real
// Playwright-driven browser test, not a guess): plain `extern "C"` linkage is enough for these
// functions to compile, link with zero errors, and even show up correctly (by name, with a valid
// function pointer) in the generated `pinvoke-table.h` .NET's WASM SDK builds from scanning
// PictTag.UI.Browser.Interop's LibraryImport("__Internal") declarations - none of that is
// sufficient at runtime. Without EMSCRIPTEN_KEEPALIVE, wasm-ld's own dead-code elimination still
// drops these functions from the module's actual export section (confirmed by inspecting the built
// .wasm's `WebAssembly.Module.exports()` directly - empty of any pgf_* entry either way), and the
// .NET WASM interpreter's own pinvoke resolution (used whenever RunAOTCompilation isn't enabled,
// i.e. every normal Debug `dotnet build`/`dotnet run` inner-loop build) needs the symbol to be a
// real, JS-visible wasm export to resolve it - the embedded C-level pinvoke table alone only
// serves AOT-compiled call sites. EMSCRIPTEN_KEEPALIVE (a real Emscripten macro, equivalent to
// passing -sEXPORTED_FUNCTIONS explicitly) is what actually forces wasm-ld to keep and export the
// symbol.
#if defined(_WIN32)
#define PICTTAG_EXPORT __declspec(dllexport)
#elif defined(__EMSCRIPTEN__)
#include <emscripten.h>
#define PICTTAG_EXPORT EMSCRIPTEN_KEEPALIVE
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

// --- Test-only exports below (new-features/managed-pgf-codec.md) ---
//
// pgf_encode_bgra_alloc/pgf_free_encoded and pgf_debug_decode_channel exist solely to support the
// managed (C#) PGF codec port's correctness test rig - they are never called from PictTag's
// production decode path (PictTag.Data/PictTag.UI.*), only from PictTag.PgfCodec.Tests.
//
// Encoder.cpp has been compiled into this DLL since it was first vendored, but nothing exported it
// until now (the product itself never writes PGF files - see CLAUDE.md's "read-only by design" GUI
// scope boundary). Exposing it here is what makes "encode with the real C++ implementation" a real,
// callable leg of the test rig's 4-way round-trip matrix (C#/C++ encode x C#/C++ decode), rather
// than something only reachable via real digiKam-produced fixtures.

// Encodes a BGRA buffer (top-down, pitch = width*4) into a newly heap-allocated PGF byte buffer.
// quality: 0 = lossless, up to MaxQuality (31 in this build - __PGF32SUPPORT__ is active by
// PGFplatform.h's own default, since NPGF32 is never defined - see PGFtypes.h; SetHeader's own
// ASSERT(header.quality <= MaxQuality) is the actual enforced bound, not the narrower "0/4/6" preset
// list PGFHeader's doc comment calls out by name).
// The caller owns the returned buffer and must free it with pgf_free_encoded - an owned-pointer
// pattern deliberately chosen here (mirroring pgf_open/pgf_close's handle lifecycle) since this is
// test tooling, not the performance-critical per-request path pgf_decode_bgra's caller-buffer design
// exists for.
PICTTAG_EXPORT bool pgf_encode_bgra_alloc(
    const uint8_t* bgra, uint32_t width, uint32_t height, uint8_t quality,
    uint8_t** outData, size_t* outLen)
{
    if (bgra == nullptr || width == 0 || height == 0 || outData == nullptr || outLen == nullptr)
    {
        return false;
    }

    // CPGFImage::ComputeLevels() (PGFimage.cpp) falls back to nLevels=0 - a completely different,
    // wavelet-transform-free "store raw/uncoded channel data" path (CPGFImage::Open/WriteImage's
    // nLevels==0 branches) - whenever min(width, height) < 2*FilterSize (10 in this build: 5*2,
    // FilterSize=5 per WaveletTransform.h). Real digiKam thumbnails never approach this size, and
    // this codepath was never exercised by this shim's original decode-only exports (all real
    // fixtures decode via the normal nLevels>=1 path) - empirically, exercising it here via this
    // test-only encoder produced a real, repeatable heap corruption (a delayed
    // STATUS_ACCESS_VIOLATION in a later, unrelated call - AddressSanitizer found no report before
    // the crash, so root-causing the exact corrupted write was not pursued further; this guard
    // avoids the whole trigger class instead). Rejecting degenerate sizes here matches this PRD's
    // actual scope: new-features/managed-pgf-codec.md's C# port only needs to handle real
    // thumbnail-sized images, not this near-zero-pixel edge case.
    if (width < 10 || height < 10)
    {
        return false;
    }

    *outData = nullptr;
    *outLen = 0;

    // A pre-sized, non-owning buffer (CPGFMemoryStream(UINT8*, size_t)) rather than
    // CPGFMemoryStream(size_t)'s allocating/growable constructor. That constructor's Write()
    // reallocates via realloc() when the buffer is too small (PGFstream.cpp), but its paired
    // ~CPGFMemoryStream() destructor always frees via delete[] - mixing a C realloc() with a C++
    // delete[] is undefined behavior. This was not a theoretical concern: it produced a real,
    // repeatable STATUS_ACCESS_VIOLATION crash in an unrelated later native call once realloc()
    // actually fired, during this shim's development (see new-features/managed-pgf-codec.md).
    // Sized generously (raw BGRA size, doubled, plus a fixed cushion) since this is test-only
    // tooling, not the performance-critical per-request path - if the real encoded size somehow
    // still exceeds this, CPGFMemoryStream::Write's non-allocated branch throws IOException, which
    // this function fails closed on (returns false) rather than growing/corrupting anything.
    size_t bufferCapacity = static_cast<size_t>(width) * height * 4 * 2 + 65536;
    uint8_t* rawBuffer = new uint8_t[bufferCapacity];

    try
    {
        PGFHeader header;
        header.width = width;
        header.height = height;
        header.nLevels = 0; // 0 = auto: CPGFImage::ComputeLevels() picks a value from the image size
        header.quality = quality;
        header.bpp = 32;
        header.channels = 4;
        header.mode = ImageModeRGBA;
        header.usedBitsPerChannel = 8;

        CPGFImage img;
        img.ConfigureEncoder(false); // no OpenMP - determinism, matches ConfigureDecoder(false) above
        img.SetHeader(header);

        // BGRA in, PGF's RGBA mode expects BGR[A] internally - identity map, matching
        // pgf_decode_bgra's own identity channelMap used for the same reason in reverse.
        int channelMap[] = { 0, 1, 2, 3 };
        img.ImportBitmap(static_cast<int>(width) * 4, const_cast<uint8_t*>(bgra), 32, channelMap);

        CPGFMemoryStream stream(rawBuffer, bufferCapacity);
        img.Write(&stream);

        size_t written = static_cast<size_t>(stream.GetPos());
        uint8_t* result = new uint8_t[written];
        memcpy(result, stream.GetBuffer(), written);
        delete[] rawBuffer;

        *outData = result;
        *outLen = written;
        return true;
    }
    catch (...)
    {
        delete[] rawBuffer;
        return false;
    }
}

PICTTAG_EXPORT void pgf_free_encoded(uint8_t* data)
{
    delete[] data;
}

// Dumps one channel's raw post-decode/pre-colorconversion DataT buffer (DataT = INT32 in this
// build - see PGFtypes.h; __PGF32SUPPORT__ is active by PGFplatform.h's own default, since NPGF32
// is never defined anywhere - pgf-all-image-modes.md's DataT correction) after decoding down to
// the given level. Lets the C# port's own intermediate YUV channel data be compared
// stage-by-stage against this real oracle (entropy decode -> inverse transform, before color
// conversion even exists in the port) instead of only diffing final BGRA end to end.
//
// KNOWN LIMITATION - NOT currently called from any automated test (PictTag.PgfCodec.Tests
// deliberately excludes it, see that project's notes). Repeated calls to this specific function -
// even just a couple of dozen, and even mixed in among many more calls to pgf_encode_bgra_alloc/
// pgf_decode_bgra that never showed any problem on their own (200+ consecutive calls each, clean) -
// produced a real, repeatable STATUS_ACCESS_VIOLATION during this shim's development
// (new-features/managed-pgf-codec.md). The crash reproduces even calling this function alone,
// repeatedly, on a single already-known-good real fixture (no encoding involved at all), and
// disappears entirely if the final GetChannel()/memcpy read is skipped - narrowing it to that read
// specifically, not Open()/Read() or this shim's other exports. AddressSanitizer (a real
// /fsanitize=address rebuild, confirmed genuinely active via its own startup diagnostics) found no
// violation report before the crash, so the exact corrupted access was not root-caused. Believed
// safe for a single, occasional, manual-debugging call (the pattern this function is actually
// designed for - inspecting one specific case while developing the C# port's entropy-decode/
// inverse-transform stages) - do not add automated test coverage that calls it more than once or
// twice per test process until this is properly root-caused.
PICTTAG_EXPORT bool pgf_debug_decode_channel(
    const uint8_t* data, size_t dataLen, int level, int channel,
    int32_t* outBuffer, size_t outBufferLen, uint32_t* outWidth, uint32_t* outHeight)
{
    if (data == nullptr || dataLen == 0 || outBuffer == nullptr || outWidth == nullptr ||
        outHeight == nullptr || level < 0 || channel < 0)
    {
        return false;
    }

    try
    {
        CPGFMemoryStream stream(const_cast<UINT8*>(data), dataLen);
        CPGFImage img;
        img.ConfigureDecoder(false);
        img.Open(&stream);

        if (channel >= img.Channels())
        {
            return false;
        }

        // See pgf_encode_bgra_alloc's doc comment: nLevels==0 means the file used the wavelet-
        // transform-free "raw/uncoded channel" path, which isn't what this debug function exists
        // to inspect (there is no post-decode/pre-colorconversion wavelet coefficient data at all
        // in that case) - and exercising it here was empirically implicated in a real heap
        // corruption. Reject rather than risk it, matching pgf_encode_bgra_alloc's guard.
        if (img.Levels() == 0)
        {
            return false;
        }

        img.Read(level);

        uint32_t width = img.ChannelWidth(channel);
        uint32_t height = img.ChannelHeight(channel);
        size_t required = static_cast<size_t>(width) * height;
        if (outBufferLen < required)
        {
            return false;
        }

        const DataT* channelData = img.GetChannel(channel);
        memcpy(outBuffer, channelData, required * sizeof(DataT));

        *outWidth = width;
        *outHeight = height;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

// pgf-all-image-modes.md Stage 1: reports back every raw header field after a real Open() - unlike
// pgf_get_dimensions/pgf_open, deliberately WITHOUT their hardcoded Channels()==4 (RGBA-only)
// restriction, since this port now needs to prove CompleteHeader()'s real validation accepts a
// C#-written header for every mode it covers, not just RGBA. Only touches Open() plus plain header
// accessors (Mode()/Channels()/Width()/Height()/Levels(), GetHeader()->bpp/usedBitsPerChannel) -
// none of pgf_debug_decode_channel's GetChannel()/Read(level) path, so this doesn't share that
// function's unresolved repeated-call crash risk (see that function's doc comment above).
PICTTAG_EXPORT bool pgf_debug_get_header_info(
    const uint8_t* data, size_t dataLen,
    uint32_t* outWidth, uint32_t* outHeight, int32_t* outLevels,
    uint8_t* outMode, uint8_t* outBpp, uint8_t* outChannels, uint8_t* outUsedBitsPerChannel)
{
    if (data == nullptr || dataLen == 0 || outWidth == nullptr || outHeight == nullptr ||
        outLevels == nullptr || outMode == nullptr || outBpp == nullptr || outChannels == nullptr ||
        outUsedBitsPerChannel == nullptr)
    {
        return false;
    }

    try
    {
        CPGFMemoryStream stream(const_cast<UINT8*>(data), dataLen);
        CPGFImage img;
        img.ConfigureDecoder(false);
        img.Open(&stream);

        *outWidth = img.Width();
        *outHeight = img.Height();
        *outLevels = img.Levels();
        *outMode = img.Mode();
        *outBpp = img.GetHeader()->bpp;
        *outChannels = img.Channels();
        *outUsedBitsPerChannel = img.UsedBitsPerChannel();
        return true;
    }
    catch (...)
    {
        return false;
    }
}

// pgf-all-image-modes.md: general-purpose oracle decode for any mode, with a caller-supplied bpp/
// channelMap - unlike pgf_decode_bgra (hardcoded bpp=32/RGBA channelMap, Channels()==4-only), this
// lets the test rig ask for exactly the interleaving a given mode's own real shape needs (e.g.
// bpp=8/channelMap={0} for a single grayscale/indexed channel, bpp=24/channelMap={0,1,2} for a
// 3-channel HSL/HSB/Lab-family mode) - the same real, caller-configurable GetBitmap contract
// PGFimage.cpp:1772-1787's own doc comment describes, just not hardcoded to one shape. Built on
// GetBitmap, the same call this shim's other exports already make many times without incident - NOT
// GetChannel(), so this doesn't share pgf_debug_decode_channel's unresolved repeated-call crash risk.
// channelMap must have exactly `channelCount` entries (validated by the real Channels() below, not
// trusted blindly from the caller).
PICTTAG_EXPORT bool pgf_debug_decode_raw(
    const uint8_t* data, size_t dataLen, uint8_t bpp, const int32_t* channelMap, int32_t channelMapLen,
    uint8_t* outBuffer, size_t outBufferLen, uint32_t* outWidth, uint32_t* outHeight)
{
    if (data == nullptr || dataLen == 0 || channelMap == nullptr || outBuffer == nullptr ||
        outWidth == nullptr || outHeight == nullptr || bpp == 0 || bpp % 8 != 0)
    {
        return false;
    }

    try
    {
        CPGFMemoryStream stream(const_cast<UINT8*>(data), dataLen);
        CPGFImage img;
        img.ConfigureDecoder(false);
        img.Open(&stream);

        if (channelMapLen != img.Channels())
        {
            return false;
        }

        uint32_t width = img.Width();
        uint32_t height = img.Height();
        int pitch = static_cast<int>(width) * (bpp / 8);
        size_t requiredSize = static_cast<size_t>(pitch) * height;
        if (outBufferLen < requiredSize)
        {
            return false;
        }

        // GetBitmap only ever writes the source mode's own real channel count worth of bytes per
        // pixel (PGFimage.cpp:1830 onward) - any remaining bytes in a wider caller-requested stride
        // (e.g. a 4-byte stride for a genuinely 1- or 3-channel mode) are left exactly as this
        // memset leaves them, so the C# caller can distinguish "real reconstructed byte" from
        // "GetBitmap never touched this position" instead of reading uninitialized memory.
        memset(outBuffer, 0, requiredSize);

        img.Read();

        // Fixed-size local array, not std::vector - matches this shim's/libpgf's own established
        // "no STL" convention (see PgfDecoderHandle's doc comment above for why that matters for the
        // WASM build specifically). MaxChannels (PGFtypes.h) is 8; channelMapLen was already
        // validated above to equal the real Channels(), which can never exceed that.
        int channelMapArr[MaxChannels];
        for (int32_t i = 0; i < channelMapLen; i++)
        {
            channelMapArr[i] = channelMap[i];
        }

        img.GetBitmap(pitch, outBuffer, bpp, channelMapArr);

        *outWidth = width;
        *outHeight = height;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

} // extern "C"
