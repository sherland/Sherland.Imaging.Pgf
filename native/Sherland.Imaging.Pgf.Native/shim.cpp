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
#include "Encoder.h"
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

// Test-only writer for genuine pre-Version5 Bitmap fixtures. libpgf's public encoder has emitted
// Version5's tiled HL/LH layout for every obtainable release, so merely clearing Version5 after a
// normal Write() would produce bytes that no decoder can legitimately read. This small derived
// helper instead uses WriteHeader's real transform/header setup, then writes HL/LH in the exact
// paired InterBlockSize order CDecoder::DecodeInterleaved consumes (Decoder.cpp:343-454).
class LegacyBitmapTestImage final : public CPGFImage
{
public:
    void ClearVersionFlags(bool clearVersion5)
    {
        m_preHeader.version &= ~Version7;
        if (clearVersion5)
        {
            m_preHeader.version &= ~Version5;
        }
    }

    void WriteLegacyInterleaved(CPGFStream* stream)
    {
        ASSERT(stream);
        ASSERT(m_header.nLevels > 0);

        // Does the real CPGFImage setup work: transforms channels and creates CEncoder while
        // writing the genuine PGF preheader/header. ClearVersionFlags must run before this call.
        WriteHeader(stream);
        m_encoder->WriteLevelLength(m_levelLength);

        for (m_currentLevel = m_header.nLevels; m_currentLevel > 0; )
        {
            for (int c = 0; c < m_header.channels; c++)
            {
                CWaveletTransform* wt = m_wtChannel[c];
                if (m_currentLevel == m_header.nLevels)
                {
                    wt->GetSubband(m_currentLevel, LL)->ExtractTile(*m_encoder);
                }

                WriteInterleavedHlLh(wt, m_currentLevel);
                wt->GetSubband(m_currentLevel, HH)->ExtractTile(*m_encoder);
            }

            m_encoder->SetEncodedLevel(--m_currentLevel);
        }

        m_encoder->Flush();
        m_encoder->UpdateLevelLength();
        delete m_encoder;
        m_encoder = nullptr;
    }

private:
    void WriteInterleavedHlLh(CWaveletTransform* wt, int level)
    {
        CSubband* hl = wt->GetSubband(level, HL);
        CSubband* lh = wt->GetSubband(level, LH);
        const div_t lhH = div(lh->GetHeight(), InterBlockSize);
        const div_t hlW = div(hl->GetWidth(), InterBlockSize);
        const int hlws = hl->GetWidth() - InterBlockSize;
        const int hlwr = hl->GetWidth() - hlW.rem;
        const int lhws = lh->GetWidth() - InterBlockSize;
        const int lhwr = lh->GetWidth() - hlW.rem;
        int hlBase = 0, lhBase = 0, hlBase2, lhBase2, hlPos, lhPos;

        // Inverse of CDecoder::DecodeInterleaved's four rectangular walks. Keep this intentionally
        // structural rather than clever: the fixture writer exists only to feed that native decoder
        // and the managed port with valid historical byte layout.
        for (int i = 0; i < lhH.quot; i++) {
            hlBase2 = hlBase; lhBase2 = lhBase;
            for (int j = 0; j < hlW.quot; j++) {
                hlPos = hlBase2; lhPos = lhBase2;
                for (int y = 0; y < InterBlockSize; y++) {
                    for (int x = 0; x < InterBlockSize; x++) {
                        m_encoder->WriteValue(hl, hlPos++);
                        m_encoder->WriteValue(lh, lhPos++);
                    }
                    hlPos += hlws; lhPos += lhws;
                }
                hlBase2 += InterBlockSize; lhBase2 += InterBlockSize;
            }
            hlPos = hlBase2; lhPos = lhBase2;
            for (int y = 0; y < InterBlockSize; y++) {
                for (int x = 0; x < hlW.rem; x++) {
                    m_encoder->WriteValue(hl, hlPos++);
                    m_encoder->WriteValue(lh, lhPos++);
                }
                if (lh->GetWidth() > hl->GetWidth()) m_encoder->WriteValue(lh, lhPos);
                hlPos += hlwr; lhPos += lhwr;
                hlBase += hl->GetWidth(); lhBase += lh->GetWidth();
            }
        }
        hlBase2 = hlBase; lhBase2 = lhBase;
        for (int j = 0; j < hlW.quot; j++) {
            hlPos = hlBase2; lhPos = lhBase2;
            for (int y = 0; y < lhH.rem; y++) {
                for (int x = 0; x < InterBlockSize; x++) {
                    m_encoder->WriteValue(hl, hlPos++);
                    m_encoder->WriteValue(lh, lhPos++);
                }
                hlPos += hlws; lhPos += lhws;
            }
            hlBase2 += InterBlockSize; lhBase2 += InterBlockSize;
        }
        hlPos = hlBase2; lhPos = lhBase2;
        for (int y = 0; y < lhH.rem; y++) {
            for (int x = 0; x < hlW.rem; x++) {
                m_encoder->WriteValue(hl, hlPos++);
                m_encoder->WriteValue(lh, lhPos++);
            }
            if (lh->GetWidth() > hl->GetWidth()) m_encoder->WriteValue(lh, lhPos);
            hlPos += hlwr; lhPos += lhwr;
            hlBase += hl->GetWidth();
        }
        if (hl->GetHeight() > lh->GetHeight()) {
            for (int j = 0; j < hl->GetWidth(); j++) m_encoder->WriteValue(hl, hlBase + j);
        }
    }
};

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
// production decode path (PictTag.Data/PictTag.UI.*), only from Sherland.Imaging.Pgf.Tests.
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
    // FilterSize=5 per WaveletTransform.h). This guard used to reject that range unconditionally
    // (managed-pgf-codec.md Stage 1): exercising it here, at the time, produced a real, repeatable
    // heap corruption. pgf-user-data-and-small-images.md's Open Question 1 revisited that finding
    // once this function's own realloc()/delete[] mismatch (the very next paragraph below) was
    // fixed, since both bugs were found in the same investigation and never conclusively
    // disentangled at the time - a deliberately isolated experiment (a scratch copy of this shim
    // with only this guard removed, stress-tested via 5000 encode-then-decode round trips across
    // ten sizes down to 1x1, mixed gradient/solid-color content, quality=0) reproduced no crash and
    // no pixel mismatch. That result is consistent with the original corruption having been the
    // realloc()/delete[] bug (already fixed) rather than anything specific to the nLevels==0 path
    // itself - the guard is removed accordingly. `pgf_debug_decode_channel`'s own, separate,
    // still-unresolved repeated-call crash (this file's later doc comment) is not affected either
    // way: that finding was independently narrowed to its own GetChannel()/memcpy read, a code path
    // this function never calls. (width/height == 0 is already rejected above.)

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

// Produces a valid historical Bitmap file for the codec's test suite. clearVersion5 selects the
// rare pre-Version5/pre-Version7 color-conversion stride; unlike a header-byte patch, it also uses
// the matching interleaved entropy layout through LegacyBitmapTestImage above.
PICTTAG_EXPORT bool pgf_encode_bitmap_legacy_alloc(
    const uint8_t* packedBits, uint32_t width, uint32_t height, bool clearVersion5,
    uint8_t** outData, size_t* outLen)
{
    if (packedBits == nullptr || width < 10 || height < 10 || outData == nullptr || outLen == nullptr)
    {
        return false;
    }

    *outData = nullptr;
    *outLen = 0;
    const size_t rowBytes = (static_cast<size_t>(width) + 7) / 8;
    const size_t bufferCapacity = static_cast<size_t>(width) * height * 8 + 65536;
    uint8_t* rawBuffer = new uint8_t[bufferCapacity];

    try
    {
        PGFHeader header;
        header.width = width;
        header.height = height;
        header.nLevels = 0;
        header.quality = 0;
        header.bpp = 1;
        header.channels = 1;
        header.mode = ImageModeBitmap;
        header.usedBitsPerChannel = 1;

        LegacyBitmapTestImage img;
        img.ConfigureEncoder(false);
        img.SetHeader(header);
        img.ClearVersionFlags(clearVersion5);

        // Historical RgbToYuv's real layout (libpgf 6.14.12 PGFimage.cpp:1340-1374): each
        // row begins with packed-byte DataT values minus YUVoffset8, then pads to pixel width.
        DataT* channel = new DataT[static_cast<size_t>(width) * height];
        for (uint32_t y = 0; y < height; y++) {
            DataT* row = channel + static_cast<size_t>(y) * width;
            for (size_t x = 0; x < rowBytes; x++) row[x] = static_cast<DataT>(packedBits[y * rowBytes + x]) - 128;
            for (uint32_t x = static_cast<uint32_t>(rowBytes); x < width; x++) row[x] = 128;
        }
        img.SetChannel(channel);

        CPGFMemoryStream stream(rawBuffer, bufferCapacity);
        if (clearVersion5) {
            img.WriteLegacyInterleaved(&stream);
        } else {
            // Version5's real tiled entropy layout is still available through the public writer;
            // only Version7 has been cleared so native GetBitmap takes its historical packed path.
            img.Write(&stream);
        }

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

// Generic sibling of pgf_encode_bitmap_legacy_alloc for the all-mode pre-Version5 verification
// matrix. ImportBitmap remains the native implementation for source-mode conversion; only the
// unavailable historical interleaved entropy write is supplied by LegacyBitmapTestImage. The
// resulting fixture is decoded through pgf_debug_decode_raw/GetBitmap, never the separately unsafe
// repeated-call pgf_debug_decode_channel path.
PICTTAG_EXPORT bool pgf_encode_legacy_interleaved_raw_alloc(
    const uint8_t* source, uint32_t width, uint32_t height, uint8_t quality, uint8_t mode, uint8_t bpp, uint8_t channels,
    const uint8_t* colorTable, uint32_t colorTableLen, uint8_t** outData, size_t* outLen)
{
    if (source == nullptr || width < 10 || height < 10 || outData == nullptr || outLen == nullptr ||
        channels == 0 || channels > MaxChannels || bpp == 0) return false;
    *outData = nullptr; *outLen = 0;
    size_t pitch = (static_cast<uint64_t>(width) * bpp + 7) / 8;
    size_t bufferCapacity = pitch * height * 2 + 65536;
    uint8_t* rawBuffer = new uint8_t[bufferCapacity];
    try {
        PGFHeader header;
        header.width = width; header.height = height; header.nLevels = 0; header.quality = quality;
        header.bpp = bpp; header.channels = channels; header.mode = mode; header.usedBitsPerChannel = 0;
        LegacyBitmapTestImage img;
        img.ConfigureEncoder(false);
        img.SetHeader(header);
        if (mode == ImageModeIndexedColor && colorTable != nullptr && colorTableLen == ColorTableSize) {
            img.SetColorTable(0, ColorTableLen, reinterpret_cast<const RGBQUAD*>(colorTable));
        }
        img.ClearVersionFlags(true);
        int channelMap[] = { 0, 1, 2, 3, 4, 5, 6, 7 };
        img.ImportBitmap(static_cast<int>(pitch), const_cast<uint8_t*>(source), bpp, channelMap);
        CPGFMemoryStream stream(rawBuffer, bufferCapacity);
        img.WriteLegacyInterleaved(&stream);
        size_t written = static_cast<size_t>(stream.GetPos());
        uint8_t* result = new uint8_t[written];
        memcpy(result, stream.GetBuffer(), written);
        delete[] rawBuffer; *outData = result; *outLen = written; return true;
    } catch (...) { delete[] rawBuffer; return false; }
}

// pgf-roi-support.md Stage 5: identical to pgf_encode_bgra_alloc except for one line -
// img.SetHeader(header, PGFROI) instead of img.SetHeader(header) - enabling the real, tile-structured
// ROI encoding scheme (CPGFImage::ROIisSupported() becomes true, so CPGFImage::WriteHeader/WriteLevel's
// own #ifdef __PGFROISUPPORT__ branches activate: WriteHeader's own unconditional per-channel
// SetROI(fullRect) call, WriteLevel's per-tile ExtractTile/EncodeTileBuffer sequencing - see PGFimage.h's
// own doc comment on SetHeader's flags parameter: "In case you use level-wise encoding then set flag =
// PGFROI"). This is the cross-implementation encode leg the managed port's Stage 5 needs: a genuinely
// independent (non-C#) encoder producing real PGFROI-flagged bytes to decode-test the managed ROI
// decoder against, closing the loop the same way pgf_encode_bgra_alloc already does for the base
// (non-ROI) round-trip matrix. See that function's own doc comment for every other design choice this
// one shares unchanged (allocation/exception-safety shape, the nLevels==0 guard history).
PICTTAG_EXPORT bool pgf_encode_bgra_alloc_roi(
    const uint8_t* bgra, uint32_t width, uint32_t height, uint8_t quality,
    uint8_t** outData, size_t* outLen)
{
    if (bgra == nullptr || width == 0 || height == 0 || outData == nullptr || outLen == nullptr)
    {
        return false;
    }

    *outData = nullptr;
    *outLen = 0;

    size_t bufferCapacity = static_cast<size_t>(width) * height * 4 * 2 + 65536;
    uint8_t* rawBuffer = new uint8_t[bufferCapacity];

    try
    {
        PGFHeader header;
        header.width = width;
        header.height = height;
        header.nLevels = 0; // 0 = auto, same as pgf_encode_bgra_alloc
        header.quality = quality;
        header.bpp = 32;
        header.channels = 4;
        header.mode = ImageModeRGBA;
        header.usedBitsPerChannel = 8;

        CPGFImage img;
        img.ConfigureEncoder(false); // no OpenMP - determinism, matches pgf_encode_bgra_alloc
        img.SetHeader(header, PGFROI); // the one real difference from pgf_encode_bgra_alloc

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

// pgf-all-image-modes.md Stage 9: general mode-parameterized encode, generalizing
// pgf_encode_bgra_alloc (RGBA-only) to every mode this PRD covers - the missing "real native
// encoder" leg of the round-trip matrix for non-RGBA modes. PgfImageEncoder.TryEncodeMode was
// already verified against this shim's *decode* side (pgf_debug_decode_raw); this closes the loop
// the other direction - does the real encoder, given this port's own source pixel data, produce a
// file this port's own managed decoder reads back correctly. Same allocation/exception-safety shape
// as pgf_encode_bgra_alloc (owned-pointer + pgf_free_encoded, oversized non-growable
// CPGFMemoryStream, width/height >= 10 guard - see that function's own doc comment for why each of
// those choices exists).
//
// bpp/channels are caller-supplied rather than derived from mode here (unlike this port's own
// PgfHeaderIO.CreateForMode / PgfModeInfo.TryGetBppAndChannels) so a test can also exercise
// CompleteHeader's own real validation/rejection paths if it ever needs to - real callers always
// pass the same canonical values PgfModeInfo.TryGetBppAndChannels returns.
PICTTAG_EXPORT bool pgf_encode_raw_alloc(
    const uint8_t* source, uint32_t width, uint32_t height, uint8_t quality, uint8_t mode, uint8_t bpp, uint8_t channels,
    const uint8_t* colorTable, uint32_t colorTableLen,
    uint8_t** outData, size_t* outLen)
{
    if (source == nullptr || width == 0 || height == 0 || outData == nullptr || outLen == nullptr ||
        channels == 0 || channels > MaxChannels || bpp == 0)
    {
        return false;
    }

    // Guard removed for the same reason as pgf_encode_bgra_alloc's own doc comment
    // (pgf-user-data-and-small-images.md Open Question 1): the original heap corruption attributed
    // to this size range didn't reproduce once isolated from the (separately fixed)
    // realloc()/delete[] bug. (width/height == 0 already rejected above.)

    *outData = nullptr;
    *outLen = 0;

    // Ceiling bits-to-bytes-per-row - the same ImportBitmap/RgbToYuv contract as GetBitmap's own
    // pitch (pgf_debug_decode_raw's doc comment), needed here too since bpp==1 (Bitmap) and bpp==12
    // (RGB12) are both real, valid requests.
    size_t pitch = (static_cast<uint64_t>(width) * bpp + 7) / 8;
    size_t bufferCapacity = pitch * height * 2 + 65536;
    uint8_t* rawBuffer = new uint8_t[bufferCapacity];

    try
    {
        PGFHeader header;
        header.width = width;
        header.height = height;
        header.nLevels = 0; // 0 = auto, same as pgf_encode_bgra_alloc
        header.quality = quality;
        header.bpp = bpp;
        header.channels = channels;
        header.mode = mode;
        header.usedBitsPerChannel = 0; // let CompleteHeader (inside SetHeader) derive it

        CPGFImage img;
        img.ConfigureEncoder(false);
        img.SetHeader(header);

        // colorTableLen is a BYTE count from the C# caller (matching ColorTableSize = ColorTableLen *
        // sizeof(RGBQUAD) = 1024), not an entry count (ColorTableLen = 256) - comparing against the
        // wrong constant here silently skipped SetColorTable entirely for every real call, caught by
        // PgfNativeEncodeRoundTripTests.IndexedColor_NativeEncodeThenManagedDecode_RoundTripsExactly
        // (decoded colors came back as the color table's zero-initialized default, not the real
        // palette).
        if (mode == ImageModeIndexedColor && colorTable != nullptr && colorTableLen == ColorTableSize)
        {
            img.SetColorTable(0, ColorTableLen, reinterpret_cast<const RGBQUAD*>(colorTable));
        }

        int channelMap[] = { 0, 1, 2, 3, 4, 5, 6, 7 };
        img.ImportBitmap(static_cast<int>(pitch), const_cast<uint8_t*>(source), bpp, channelMap);

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

// Dumps one channel's raw post-decode/pre-colorconversion DataT buffer (DataT = INT32 in this
// build - see PGFtypes.h; __PGF32SUPPORT__ is active by PGFplatform.h's own default, since NPGF32
// is never defined anywhere - pgf-all-image-modes.md's DataT correction) after decoding down to
// the given level. Lets the C# port's own intermediate YUV channel data be compared
// stage-by-stage against this real oracle (entropy decode -> inverse transform, before color
// conversion even exists in the port) instead of only diffing final BGRA end to end.
//
// KNOWN LIMITATION - NOT currently called from any automated test (Sherland.Imaging.Pgf.Tests
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

// pgf-real-level-lengths.md Stage 4: reports the real per-level byte lengths
// CPGFImage::GetEncodedLevelLength (PGFimage.h:367) already computes correctly today - m_levelLength
// is populated unconditionally on every real Open() (CDecoder's constructor, Decoder.cpp:188-207,
// whenever preHeader.version > 0), not lazily, so this is a pure "report back" extension: no new
// parsing or decode-path change, same Open()-plus-plain-accessors shape as
// pgf_debug_get_header_info just above (not GetChannel()/Read(level), so this doesn't share
// pgf_debug_decode_channel's unresolved repeated-call crash risk either).
//
// outLevelLengths/levelLengthsCapacity follow this shim's own established caller-supplied-buffer
// convention (pgf_debug_decode_raw's outBuffer): the caller learns Levels() from
// pgf_debug_get_header_info first, then passes a buffer sized to at least that many entries. Fails
// closed (returns false, writes nothing) if the buffer is too small, rather than truncating
// silently - GetEncodedLevelLength(level) itself only ASSERTs level is in range, so this shim's own
// bound check is the only thing making an undersized buffer safe.
//
// outLevelLengths[i] is GetEncodedLevelLength(i) directly - i.e. indexed in the *public* API's
// level-0-is-full-resolution order (GetEncodedLevelLength's own formula flips that against the
// on-wire/m_levelLength array order internally), matching
// Sherland.Imaging.Pgf.PgfProgressiveDecoder.TryGetLevelLength's own level parameter one-to-one, not
// PgfHeaderIO.Read's raw on-wire uint[] order.
PICTTAG_EXPORT bool pgf_debug_get_level_lengths(
    const uint8_t* data, size_t dataLen,
    uint32_t* outLevelLengths, int32_t levelLengthsCapacity, int32_t* outLevelCount)
{
    if (data == nullptr || dataLen == 0 || outLevelLengths == nullptr || levelLengthsCapacity < 0 ||
        outLevelCount == nullptr)
    {
        return false;
    }

    try
    {
        CPGFMemoryStream stream(const_cast<UINT8*>(data), dataLen);
        CPGFImage img;
        img.ConfigureDecoder(false);
        img.Open(&stream);

        const int levels = img.Levels();
        *outLevelCount = levels;

        if (levels > levelLengthsCapacity)
        {
            return false;
        }

        for (int i = 0; i < levels; i++)
        {
            outLevelLengths[i] = img.GetEncodedLevelLength(i);
        }

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
    // bpp==1 (Bitmap, PGFimage.cpp:1402) and bpp==12 (RGB12, PGFimage.cpp:1687) are both real,
    // unconditional (ASSERT(bpp == <mode's own native bpp>), no caller choice) requests neither
    // %8==0 nor %16==0 - bpp%4==0 covers every real bpp this shim is ever asked for (1/4/8/12/16/24/
    // 32/40/48/64), rejecting only genuinely nonsensical requests.
    if (data == nullptr || dataLen == 0 || channelMap == nullptr || outBuffer == nullptr ||
        outWidth == nullptr || outHeight == nullptr || bpp == 0 || (bpp != 1 && bpp % 4 != 0))
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
        // Ceiling bits-to-bytes-per-row - matches RgbToYuv/GetBitmap's own w2 (PGFimage.cpp:1405) for
        // bpp==1, and is equivalent to the plain width*(bpp/8) every other (byte-aligned bpp) mode
        // uses, so one formula covers both without a special case.
        int pitch = static_cast<int>((static_cast<uint64_t>(width) * bpp + 7) / 8);
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
