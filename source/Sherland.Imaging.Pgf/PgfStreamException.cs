namespace Sherland.Imaging.Pgf;

/// <summary>
/// Mirrors libpgf's <c>IOException</c> (PGFtypes.h) - thrown by <see cref="PgfMemoryReader"/>/
/// <see cref="PgfByteWriter"/> for the same "invalid stream position" case the original
/// <c>CPGFMemoryStream::SetPos</c> throws for (<c>ReturnWithError(InvalidStreamPos)</c>). Later
/// decode/encode stages catch this the same way the native shim's <c>catch (...)</c> does - fail
/// closed, never let a malformed/truncated PGF blob crash the caller.
/// </summary>
public sealed class PgfStreamException(string message) : Exception(message);
