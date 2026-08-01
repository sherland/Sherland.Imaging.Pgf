// Thin C API around digiKam's vendored libpgf codec (see libpgf/README, LGPL-2.1+). Replicates the
// exact real decode call sequence read from digiKam's own core/libs/pgfutils/pgfutils.cpp wrapper
// (ConfigureDecoder -> Open -> check Channels() == 4 -> Read -> GetBitmap), minus its Qt dependency.
//
// Both exported functions write into a caller-supplied buffer rather than allocating and handing
// back an owned pointer - this is the performance-critical path (thumbnail decode runs per grid
// tile request), so there is no native-side allocation-and-copy plus a paired free/lifetime dance
// across the P/Invoke boundary: the caller (PictTag's P/Invoke wrapper) rents a buffer once with
// ArrayPool<byte> and this shim decodes straight into it.

#include "PGFimage.h"

extern "C" {

__declspec(dllexport) bool pgf_get_dimensions(
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

__declspec(dllexport) bool pgf_decode_bgra(
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

} // extern "C"
