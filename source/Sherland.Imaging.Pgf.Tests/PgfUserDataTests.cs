using System.Buffers.Binary;
using Sherland.Imaging.Pgf.Tests.Oracle;

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>
/// Stage 1 exit tests (new-features/pgf-user-data-and-small-images.md): decode-side user data
/// (Goal 1/2) and the untrusted-length defensive bound (Goal 3). This port's own encoder doesn't
/// write user data yet (Stage 2's scope) - matching the PRD's own Stage 1 exit test wording ("a
/// real-or-synthetic file"), these tests hand-assemble synthetic PGF byte buffers via
/// <see cref="BuildHeaderBytes"/> rather than waiting on the encoder. Stage 2's own tests (further
/// down this file) exercise the real encoder instead, now that it can write user data.
/// </summary>
public class PgfUserDataTests
{
    /// <summary>Hand-assembles the pre-header/header/[colorTable]/userData/levelLengths byte layout
    /// exactly as <see cref="PgfHeaderIO.Write"/> does, plus a user data section that method doesn't
    /// support yet (Stage 2's scope) - the same layout <c>Encoder.cpp:38</c>'s
    /// <c>PGFPostHeader ::= [ColorTable] [UserData]</c> comment documents.</summary>
    private static byte[] BuildHeaderBytes(PgfHeader header, ReadOnlySpan<byte> colorTable, ReadOnlySpan<byte> userData)
    {
        bool hasColorTable = header.Mode == PgfConstants.ImageModeIndexedColor && !colorTable.IsEmpty;
        uint hSize = (uint)(PgfConstants.HeaderSize + (hasColorTable ? PgfConstants.ColorTableSize : 0) + userData.Length);

        PgfByteWriter writer = new();

        Span<byte> preHeaderBytes = stackalloc byte[PgfConstants.PreHeaderSize];
        PgfConstants.Magic.CopyTo(preHeaderBytes);
        preHeaderBytes[3] = (byte)PgfConstants.EncoderVersionFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(preHeaderBytes[4..8], hSize);
        writer.Write(preHeaderBytes);

        Span<byte> headerBytes = stackalloc byte[PgfConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[0..4], header.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[4..8], header.Height);
        headerBytes[8] = header.NLevels;
        headerBytes[9] = header.Quality;
        headerBytes[10] = header.Bpp;
        headerBytes[11] = header.Channels;
        headerBytes[12] = header.Mode;
        headerBytes[13] = header.UsedBitsPerChannel;
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes[14..16], header.VersionNumberRaw);
        writer.Write(headerBytes);

        if (hasColorTable)
        {
            writer.Write(colorTable);
        }

        writer.Write(userData);

        Span<byte> zero = stackalloc byte[4];
        for (int i = 0; i < header.NLevels; i++)
        {
            writer.Write(zero);
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Splices <paramref name="userData"/> into a real, fully-encoded PGF file (produced by
    /// <see cref="PgfImageEncoder.TryEncode"/>) by re-serializing only the pre-header/header/
    /// levelLengths prefix with a patched <c>hSize</c> and the new user data inserted, then
    /// reattaching the original entropy-coded bitstream unchanged - proving user data support
    /// doesn't disturb real decodable image data, not just header-level bookkeeping.</summary>
    private static byte[] InsertUserData(byte[] encodedPgfBytes, PgfHeader header, ReadOnlySpan<byte> userData)
    {
        int prefixLength = PgfConstants.PreHeaderSize + PgfConstants.HeaderSize + (4 * header.NLevels);
        ReadOnlySpan<byte> bitstream = encodedPgfBytes.AsSpan(prefixLength);

        byte[] newPrefix = BuildHeaderBytes(header, colorTable: default, userData);

        byte[] result = new byte[newPrefix.Length + bitstream.Length];
        newPrefix.CopyTo(result, 0);
        bitstream.CopyTo(result.AsSpan(newPrefix.Length));
        return result;
    }

    [Fact]
    public void Read_NoUserData_ReportsNone()
    {
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, userData: default);

        PgfMemoryReader reader = new(pgfBytes);
        (_, _, _, _, PgfUserData userData) = PgfHeaderIO.Read(reader);

        Assert.Empty(userData.CachedBytes);
        Assert.Equal(0u, userData.TotalLength);
    }

    [Fact]
    public void Read_CacheAllPolicy_ReturnsUserDataByteExact()
    {
        byte[] payload = "hello pgf user data, this is arbitrary metadata"u8.ToArray();
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, payload);

        PgfMemoryReader reader = new(pgfBytes);
        (_, _, _, _, PgfUserData userData) = PgfHeaderIO.Read(reader, PgfUserDataPolicy.CacheAll);

        Assert.Equal(payload, userData.CachedBytes);
        Assert.Equal((uint)payload.Length, userData.TotalLength);
    }

    [Fact]
    public void Read_SkipPolicy_CachesNothing_ButStillReportsTotalLength()
    {
        byte[] payload = "some metadata nobody asked to cache"u8.ToArray();
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, payload);

        PgfMemoryReader reader = new(pgfBytes);
        (_, _, _, _, PgfUserData userData) = PgfHeaderIO.Read(reader, PgfUserDataPolicy.Skip);

        Assert.Empty(userData.CachedBytes);
        Assert.Equal((uint)payload.Length, userData.TotalLength);
    }

    [Fact]
    public void Read_SkipPolicy_LandsAtCorrectLevelLengthOffset()
    {
        // Regression for the skip path specifically: reading the level-length array afterward must
        // still succeed (i.e. the skip advanced exactly userDataLen bytes, not more or less).
        byte[] payload = "metadata of an odd, non-word-aligned length: 37 bytes!"u8.ToArray()[..37];
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, payload);

        PgfMemoryReader reader = new(pgfBytes);
        (_, PgfHeader roundTrippedHeader, uint[] levelLengths, _, _) = PgfHeaderIO.Read(reader, PgfUserDataPolicy.Skip);

        Assert.Equal(header.NLevels, levelLengths.Length);
        Assert.Equal(header, roundTrippedHeader);
        Assert.Equal(pgfBytes.Length, reader.Position);
    }

    [Fact]
    public void Read_CachePrefixPolicy_CachesOnlyPrefix_ButReportsRealTotalLength()
    {
        byte[] payload = new byte[100];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, payload);

        PgfMemoryReader reader = new(pgfBytes);
        (_, _, uint[] levelLengths, _, PgfUserData userData) =
            PgfHeaderIO.Read(reader, PgfUserDataPolicy.CachePrefix, prefixSize: 10);

        Assert.Equal(payload[..10], userData.CachedBytes);
        Assert.Equal(100u, userData.TotalLength);
        Assert.Equal(header.NLevels, levelLengths.Length); // proves the un-cached remainder was skipped correctly
    }

    [Fact]
    public void Read_CachePrefixPolicy_PrefixLargerThanActualData_CachesEverythingAvailable()
    {
        byte[] payload = "short"u8.ToArray();
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, payload);

        PgfMemoryReader reader = new(pgfBytes);
        (_, _, _, _, PgfUserData userData) = PgfHeaderIO.Read(reader, PgfUserDataPolicy.CachePrefix, prefixSize: 1000);

        Assert.Equal(payload, userData.CachedBytes);
        Assert.Equal((uint)payload.Length, userData.TotalLength);
    }

    [Fact]
    public void Read_UserDataAlongsideColorTable_BothRoundTrip()
    {
        byte[] colorTable = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < colorTable.Length; i++)
        {
            colorTable[i] = (byte)(i * 3);
        }

        byte[] payload = "indexed-color metadata"u8.ToArray();
        PgfHeader header = PgfHeaderIO.CreateForMode(width: 32, height: 32, quality: 0, PgfConstants.ImageModeIndexedColor);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable, payload);

        PgfMemoryReader reader = new(pgfBytes);
        (_, _, _, byte[]? roundTrippedColorTable, PgfUserData userData) = PgfHeaderIO.Read(reader);

        Assert.Equal(colorTable, roundTrippedColorTable);
        Assert.Equal(payload, userData.CachedBytes);
        Assert.Equal((uint)payload.Length, userData.TotalLength);
    }

    /// <summary>Goal 3: a corrupted/malicious <c>hSize</c> claiming far more user data than the
    /// stream actually has left must fail closed, not attempt an oversized allocation or silently
    /// under-read. Exercised for every policy - the length check happens before the policy branch,
    /// so all three must reject equally.</summary>
    [Theory]
    [InlineData(PgfUserDataPolicy.CacheAll)]
    [InlineData(PgfUserDataPolicy.Skip)]
    [InlineData(PgfUserDataPolicy.CachePrefix)]
    public void Read_CorruptedHSizeClaimsMoreUserDataThanStreamHas_FailsClosed(PgfUserDataPolicy policy)
    {
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, "tiny"u8);

        // Corrupt hSize (4 bytes right after the 4-byte magic/version) to claim a huge post-header,
        // far beyond what the actual buffer contains.
        BinaryPrimitives.WriteUInt32LittleEndian(pgfBytes.AsSpan(4, 4), 0x7FFFFFFF);

        PgfMemoryReader reader = new(pgfBytes);
        Assert.Throws<PgfFormatException>(() => PgfHeaderIO.Read(reader, policy, prefixSize: 1));
    }

    [Fact]
    public void Read_CorruptedHSizeClaimsMoreUserDataThanStreamHas_DoesNotAllocateAbsurdBuffer()
    {
        // Same corrupted-length scenario as above, but with a claimed length so large that
        // allocating it directly (as CacheAll would, absent the bound) would itself be a real
        // resource-exhaustion risk - this must fail closed before ever reaching `new byte[...]`.
        PgfHeader header = PgfHeaderIO.CreateForEncode(width: 32, height: 32, quality: 0);
        byte[] pgfBytes = BuildHeaderBytes(header, colorTable: default, "tiny"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(pgfBytes.AsSpan(4, 4), uint.MaxValue - 1);

        PgfMemoryReader reader = new(pgfBytes);
        Assert.Throws<PgfFormatException>(() => PgfHeaderIO.Read(reader, PgfUserDataPolicy.CacheAll));
    }

    // --- Full-pipeline tests: real decodable image data, plus user data spliced in. ---

    [Fact]
    public void TryDecode_RealImageWithUserData_DecodesPixelsCorrectly_AndReturnsUserData()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? encoded));

        PgfHeader header = PgfHeaderIO.CreateForEncode((uint)width, (uint)height, quality: 0);
        byte[] payload = "produced by some other real PGF encoder"u8.ToArray();
        byte[] withUserData = InsertUserData(encoded!, header, payload);

        bool decoded = PgfImageDecoder.TryDecode(
            withUserData, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, out PgfUserData userData);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
        Assert.Equal(payload, userData.CachedBytes);
        Assert.Equal((uint)payload.Length, userData.TotalLength);
    }

    [Fact]
    public void TryDecode_SimplerOverload_StillWorks_UnaffectedByUserData()
    {
        // Every existing call site (this codebase's own facade, every other test) must keep
        // compiling and behaving unchanged - the simpler overload delegates to the new one.
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(16, 16, 1, 2, 3, 255);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? encoded));

        bool decoded = PgfImageDecoder.TryDecode(encoded!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void ProgressiveDecoder_TryOpen_ExposesUserDataAsProperty()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? encoded));

        PgfHeader header = PgfHeaderIO.CreateForEncode((uint)width, (uint)height, quality: 0);
        byte[] payload = "progressive decoder user data"u8.ToArray();
        byte[] withUserData = InsertUserData(encoded!, header, payload);

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(withUserData);

        Assert.NotNull(progressive);
        Assert.Equal(payload, progressive.UserData.CachedBytes);
        Assert.Equal((uint)payload.Length, progressive.UserData.TotalLength);

        bool decoded = progressive.TryDecodeLevel(0, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);
        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void ProgressiveDecoder_TryOpen_SkipPolicy_StillDecodesCorrectly()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);
        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? encoded));

        PgfHeader header = PgfHeaderIO.CreateForEncode((uint)width, (uint)height, quality: 0);
        byte[] payload = "this metadata is intentionally not cached"u8.ToArray();
        byte[] withUserData = InsertUserData(encoded!, header, payload);

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(withUserData, PgfUserDataPolicy.Skip);

        Assert.NotNull(progressive);
        Assert.Empty(progressive.UserData.CachedBytes);
        Assert.Equal((uint)payload.Length, progressive.UserData.TotalLength);

        bool decoded = progressive.TryDecodeLevel(0, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra);
        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
    }

    // --- Stage 2: encode-side user data. ---

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(256)]
    public void TryEncode_WithUserData_RoundTripsByteExact(int userDataLength)
    {
        byte[] userData = new byte[userDataLength];
        for (int i = 0; i < userData.Length; i++)
        {
            userData[i] = (byte)(i * 31);
        }

        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);

        bool encoded = PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, userData: userData);
        Assert.True(encoded);

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, out PgfUserData decodedUserData);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
        Assert.Equal(userData, decodedUserData.CachedBytes);
        Assert.Equal((uint)userData.Length, decodedUserData.TotalLength);
    }

    [Fact]
    public void TryEncode_NoUserData_DecodesAsNone()
    {
        (byte[] bgra, int width, int height) = TestBitmaps.SolidColor(32, 32, 10, 20, 30, 255);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes));

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => span.ToArray(), out byte[]? decodedBgra, out PgfUserData decodedUserData);

        Assert.True(decoded);
        Assert.Equal(bgra, decodedBgra);
        Assert.Empty(decodedUserData.CachedBytes);
        Assert.Equal(0u, decodedUserData.TotalLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void TryEncode_WithUserData_LossyQuality_UserDataStillByteExact(byte quality)
    {
        // User data is plain bytes, never quantized - it must round-trip exactly regardless of the
        // image's own lossy quality level, unlike the pixel data.
        byte[] userData = "metadata survives lossy encoding"u8.ToArray();
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality, out byte[]? pgfBytes, userData: userData));

        bool decoded = PgfImageDecoder.TryDecode(
            pgfBytes!, static (span, w, h) => span.ToArray(), out _, out PgfUserData decodedUserData);

        Assert.True(decoded);
        Assert.Equal(userData, decodedUserData.CachedBytes);
        Assert.Equal((uint)userData.Length, decodedUserData.TotalLength);
    }

    [Fact]
    public void TryEncodeMode_IndexedColorWithUserData_ColorTableAndUserDataBothRoundTrip()
    {
        byte[] colorTable = new byte[PgfConstants.ColorTableSize];
        for (int i = 0; i < colorTable.Length; i++)
        {
            colorTable[i] = (byte)(i * 5);
        }

        byte[] userData = "indexed-color image metadata"u8.ToArray();
        (byte[] indices, int width, int height) = TestBitmaps.Gradient(32, 32);
        byte[] indexSource = new byte[width * height];
        for (int i = 0; i < indexSource.Length; i++)
        {
            indexSource[i] = indices[i * 4]; // arbitrary single-channel projection, any byte value is a valid index
        }

        bool encoded = PgfImageEncoder.TryEncodeMode(
            indexSource, width, height, 0, PgfConstants.ImageModeIndexedColor, out byte[]? pgfBytes,
            colorTable, userData: userData);
        Assert.True(encoded);

        PgfMemoryReader reader = new(pgfBytes!);
        (_, _, _, byte[]? decodedColorTable, PgfUserData decodedUserData) = PgfHeaderIO.Read(reader);

        Assert.Equal(colorTable, decodedColorTable);
        Assert.Equal(userData, decodedUserData.CachedBytes);
        Assert.Equal((uint)userData.Length, decodedUserData.TotalLength);
    }

    /// <summary>Cross-checks against the real native decoder (not just this port's own reader) that
    /// writing user data doesn't corrupt <c>hSize</c>/post-header accounting in a way only this
    /// port's own (possibly self-consistently-wrong) reader would tolerate - the real C++ parser
    /// must still open and decode the file correctly.</summary>
    [Fact]
    public void TryEncode_WithUserData_NativeOracleStillOpensAndDecodesIt()
    {
        byte[] userData = "a real third-party PGF consumer wouldn't care about this port's internals"u8.ToArray();
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(48, 48);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, userData: userData));

        bool decoded = NativePgfOracle.TryDecode(pgfBytes!, out byte[]? decodedBgra, out int decodedWidth, out int decodedHeight);

        Assert.True(decoded);
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal(bgra, decodedBgra);
    }

    [Fact]
    public void ProgressiveDecoder_TryOpen_UserDataWrittenByRealEncoder_ExposesItCorrectly()
    {
        byte[] userData = "progressive + real encoder-written user data"u8.ToArray();
        (byte[] bgra, int width, int height) = TestBitmaps.Gradient(64, 64);

        Assert.True(PgfImageEncoder.TryEncode(bgra, width, height, quality: 0, out byte[]? pgfBytes, userData: userData));

        PgfProgressiveDecoder? progressive = PgfProgressiveDecoder.TryOpen(pgfBytes!);

        Assert.NotNull(progressive);
        Assert.Equal(userData, progressive.UserData.CachedBytes);
        Assert.Equal((uint)userData.Length, progressive.UserData.TotalLength);
    }
}
