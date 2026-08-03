namespace PictTag.PgfCodec;

/// <summary>
/// Direct port of the decode/encode-relevant parts of <c>CSubband</c> (Subband.h/Subband.cpp) - one
/// quadrant of one wavelet transform level's coefficients, plus the sequential read/write cursor
/// (<see cref="ReadBuffer"/>/<see cref="WriteBuffer"/>) <see cref="PgfWaveletTransform"/>'s row-based
/// lifting steps use, distinct from the indexed <see cref="GetData"/>/<see cref="SetData"/> access
/// <see cref="PlaceTile"/>/<see cref="ExtractTile"/> use via <see cref="PgfDecoderCore.Partition"/>/
/// <see cref="PgfEncoderCore.Partition"/>.
///
/// <c>CSubband::Dequantize</c> is deliberately not ported: grepping every call site found it's only
/// ever used from <c>CPGFImage::Reconstruct</c> (PGFimage.cpp:348), an encode-time "verify what I
/// just wrote" helper this codebase never calls (only <c>Open()</c>+<c>Read()</c> for decode,
/// <c>SetHeader()</c>+<c>ImportBitmap()</c>+<c>Write()</c> for encode) - genuinely dead code for
/// this port's scope, not an oversight.
///
/// ROI is not ported (see <see cref="PgfMacroBlock"/>'s doc comment for why that's justified, not
/// assumed) - <c>AllocMemory</c> simplifies accordingly: the real version's <c>oldSize &gt;=
/// newSize</c> reuse-check only matters when ROI can shrink/grow <c>m_size</c> after
/// <c>Initialize</c>; without ROI, size is fixed for this subband's whole lifetime, so allocation is
/// just "allocate once if not already allocated."
/// </summary>
internal sealed class PgfSubband
{
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Level { get; private set; }

    public PgfSubbandOrientation Orientation { get; private set; }

    private int size;
    private int[]? data;
    private int dataPos;

    public void Initialize(int width, int height, int level, PgfSubbandOrientation orientation)
    {
        Width = width;
        Height = height;
        size = width * height;
        Level = level;
        Orientation = orientation;
        data = null;
        dataPos = 0;
    }

    /// <summary>Allocates <see cref="size"/> coefficients if not already allocated. Simplified from
    /// the original's resize-aware version - see class doc comment.</summary>
    public bool AllocMemory()
    {
        if (data is not null)
        {
            return true;
        }

        data = new int[size];
        return true;
    }

    public void FreeMemory() => data = null;

    /// <summary>Direct port of <c>CSubband::SetBuffer</c> (Subband.h:148) - used only for level-0's
    /// LL subband, which shares the channel's own raw pixel array rather than owning its own
    /// allocation (<c>CWaveletTransform::InitSubbands</c>'s <c>data</c> parameter).</summary>
    public void SetBuffer(int[] buffer) => data = buffer;

    public int[] GetBuffer()
    {
        System.Diagnostics.Debug.Assert(data is not null, "Subband buffer accessed before AllocMemory/SetBuffer.");
        return data!;
    }

    public void SetData(int pos, int value) => GetBuffer()[pos] = value;

    public int GetData(int pos) => GetBuffer()[pos];

    public void InitBuffPos() => dataPos = 0;

    public void WriteBuffer(int value) => GetBuffer()[dataPos++] = value;

    public int ReadBuffer() => GetBuffer()[dataPos++];

    /// <summary>Direct port of <c>CSubband::Quantize</c> (Subband.cpp:112) - scalar
    /// quantization-with-deadzone, called per-subband from <c>CWaveletTransform::ForwardTransform</c>
    /// (encode only; decode's equivalent adjustment lives in <see cref="PlaceTile"/>, mirroring
    /// <c>CSubband::PlaceTile</c>'s own inline adjustment rather than a separate <c>Dequantize</c>
    /// call - see this class's doc comment for why <c>Dequantize</c> itself isn't ported).</summary>
    public void Quantize(int quantParam)
    {
        int[] buffer = GetBuffer();

        if (Orientation == PgfSubbandOrientation.Ll)
        {
            quantParam -= Level + 1;
            if (quantParam > 0)
            {
                quantParam--;
                for (int i = 0; i < size; i++)
                {
                    buffer[i] = buffer[i] < 0
                        ? unchecked((int)-(((-buffer[i] >> quantParam) + 1) >> 1))
                        : unchecked((int)(((buffer[i] >> quantParam) + 1) >> 1));
                }
            }
        }
        else
        {
            quantParam -= Orientation == PgfSubbandOrientation.Hh ? Level - 1 : Level;
            if (quantParam > 0)
            {
                int threshold = ((1 << quantParam) * 7) / 5; // good value, per the original's own comment
                quantParam--;
                for (int i = 0; i < size; i++)
                {
                    if (buffer[i] < -threshold)
                    {
                        buffer[i] = unchecked((int)-(((-buffer[i] >> quantParam) + 1) >> 1));
                    }
                    else if (buffer[i] > threshold)
                    {
                        buffer[i] = unchecked((int)(((buffer[i] >> quantParam) + 1) >> 1));
                    }
                    else
                    {
                        buffer[i] = 0;
                    }
                }
            }
        }
    }

    /// <summary>Direct port of <c>CSubband::PlaceTile</c>'s non-ROI branch (Subband.cpp:203) -
    /// allocates this subband, computes the orientation/level-adjusted dequantization parameter
    /// (the decode-side mirror of <see cref="Quantize"/>'s own adjustment), and drives
    /// <see cref="PgfDecoderCore.Partition"/> to fill it from the entropy decoder.</summary>
    public void PlaceTile(PgfDecoderCore decoder, int quantParam)
    {
        if (!AllocMemory())
        {
            throw new PgfFormatException("Failed to allocate subband memory.");
        }

        quantParam -= Orientation switch
        {
            PgfSubbandOrientation.Ll => Level + 1,
            PgfSubbandOrientation.Hh => Level - 1,
            _ => Level,
        };
        if (quantParam < 0)
        {
            quantParam = 0;
        }

        decoder.Partition(GetBuffer(), quantParam, Width, Height, startPos: 0, pitch: Width);
    }

    /// <summary>Direct port of <c>CSubband::ExtractTile</c>'s non-ROI branch (Subband.cpp:177) -
    /// drives <see cref="PgfEncoderCore.Partition"/> to feed this subband's (already-quantized, via
    /// <see cref="Quantize"/>) coefficients into the entropy encoder.</summary>
    public void ExtractTile(PgfEncoderCore encoder)
    {
        encoder.Partition(GetBuffer(), Width, Height, startPos: 0, pitch: Width);
    }
}
