namespace Sherland.Imaging.Pgf;

/// <summary>Thrown for malformed/unsupported PGF data (bad magic, truncated header, an image mode
/// this port doesn't support) - distinct from <see cref="PgfStreamException"/>, which is
/// specifically the "seek out of range" case. Both are caught the same way by callers (fail closed,
/// matching the native shim's <c>catch (...) { return false; }</c> boundary) - the split exists for
/// this port's own test/debugging clarity, not because callers need to distinguish them.</summary>
public sealed class PgfFormatException(string message) : Exception(message);
