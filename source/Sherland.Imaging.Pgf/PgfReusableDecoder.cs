namespace Sherland.Imaging.Pgf;

/// <summary>
/// pgf-codec-allocation-and-simd.md Stage 3c: an explicit reusable single-shot decoder for repeated
/// decoding of one immutable PGF payload. It owns both its parsed session graph and a
/// <see cref="PgfWorkspace"/>; callers must dispose it when the repeated-decode lifetime ends.
/// Existing stateless <see cref="PgfImageDecoder"/> APIs remain unchanged.
/// </summary>
public sealed class PgfReusableDecoder : IDisposable
{
    private readonly PgfWorkspace workspace;
    private readonly PgfDecodeSession session;
    private readonly PgfWorkspaceMark persistentMark;
    private bool needsReset;
    private bool disposed;

    private PgfReusableDecoder(PgfWorkspace workspace, PgfDecodeSession session, PgfWorkspaceMark persistentMark)
    {
        this.workspace = workspace;
        this.session = session;
        this.persistentMark = persistentMark;
    }

    /// <summary>Opens one immutable PGF payload for repeated whole-image decode operations.</summary>
    public static PgfReusableDecoder? TryOpen(ReadOnlyMemory<byte> pgfData,
        PgfUserDataPolicy userDataPolicy = PgfUserDataPolicy.CacheAll, uint userDataPrefixSize = 0)
    {
        var workspace = new PgfWorkspace();
        PgfDecodeSession? session = PgfDecodeSession.TryOpen(pgfData, userDataPolicy, userDataPrefixSize, workspace);
        if (session is null)
        {
            workspace.Dispose();
            return null;
        }

        return new PgfReusableDecoder(workspace, session, workspace.MarkPersistent());
    }

    /// <summary>Decodes the immutable input again, recycling the preceding pass's internal buffers
    /// and resetting its bitstream/subband state before the callback receives transient BGRA bytes.</summary>
    public bool TryDecode<TResult>(PgfDecodedCallback<TResult> onDecoded, out TResult? result,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (needsReset)
        {
            session.ResetForDecode();
            workspace.Reset(persistentMark);
        }

        // A cancellation or malformed-stream failure may have consumed only part of the session.
        // Mark it dirty before entering the decode loop so the next caller always gets a full
        // rewind, including when this call throws before producing a result.
        needsReset = true;
        bool success = session.TryDecode(onDecoded, out result, progress, cancellationToken);
        return success;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            workspace.Dispose();
        }
    }
}
