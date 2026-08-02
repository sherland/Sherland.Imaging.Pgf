namespace PictTag.PgfCodec;

/// <summary>
/// Single-shot managed decode: PGF bytes to a BGRA buffer, direct equivalent of
/// <c>CPGFImage::Open</c> + <c>Read(0)</c> + <c>GetBitmap</c> (PGFimage.cpp:141, 402, 1788) chained
/// together, RGBA/32bpp only (managed-pgf-codec.md's Non-goals: indexed color already fails during
/// header parse, and every real digiKam thumbnail is RGBA anyway). <see cref="PgfProgressiveDecoder"/>
/// (Stage 9) is the level-by-level equivalent, mirroring <c>Open</c>+repeated <c>Read(level)</c> -
/// this type always decodes straight through to level 0 in one call, sharing
/// <see cref="PgfDecodeSession"/>'s setup/per-level decode logic with it.
///
/// Fails closed (returns <see langword="false"/>, never throws) on any malformed/truncated input,
/// matching the native shim's own <c>catch (...) { return false; }</c> boundary
/// (managed-pgf-codec.md Tier 5) - every throwing path in <see cref="PgfHeaderIO"/>/
/// <see cref="PgfMemoryReader"/>/<see cref="PgfDecoderCore"/> is a real-but-invalid-input signal, not
/// a programming error, so it's caught inside <see cref="PgfDecodeSession.TryOpen"/> rather than left
/// to propagate.
/// </summary>
internal static class PgfImageDecoder
{
    public static bool TryDecode(ReadOnlyMemory<byte> pgfData, out byte[]? bgra, out int width, out int height)
    {
        bgra = null;
        width = height = 0;

        PgfDecodeSession? session = PgfDecodeSession.TryOpen(pgfData);
        if (session is null)
        {
            return false;
        }

        (short[] Data, int Width, int Height)[] channelData = new (short[], int, int)[4];

        // Direct port of CPGFImage::Read's non-ROI, non-interleaved (Version5+) loop
        // (PGFimage.cpp:428-475): entropy-decode all 4 channels' subbands at the current level
        // before inverse-transforming any of them - the bitstream interleaves channels
        // level-by-level, not channel-by-channel, so this ordering is load-bearing, not cosmetic.
        for (int currentLevel = session.Levels; currentLevel > 0; currentLevel--)
        {
            (short[] Data, int Width, int Height)[]? decoded = session.DecodeOneLevel(currentLevel);
            if (decoded is null)
            {
                return false;
            }

            channelData = decoded;
        }

        byte[] output = new byte[checked(session.FullWidth * session.FullHeight * 4)];
        PgfColorConversion.DecodeYuvaToBgra(
            channelData[0].Data, channelData[1].Data, channelData[2].Data, channelData[3].Data,
            session.FullWidth, session.FullHeight, session.ChromaWidth, session.Downsample, output);

        bgra = output;
        width = session.FullWidth;
        height = session.FullHeight;
        return true;
    }
}
