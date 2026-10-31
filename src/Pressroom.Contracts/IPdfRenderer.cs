namespace Pressroom.Contracts;

/// <summary>Engine-independent rendering interface for callers that stream the PDF response.</summary>
public interface IPdfRenderer
{
    Task<PdfRenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default);
    Task<PdfRenderResult> RenderAsync(string html, RenderOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Owns its PDF stream and the underlying response. Dispose after copying the stream.</summary>
public sealed class PdfRenderResult(Stream pdfStream, int? pageCount, string fileName, long? durationMs,
    IDisposable? responseOwner = null)
    : IDisposable, IAsyncDisposable
{
    private int _disposed;
    public Stream PdfStream { get; } = pdfStream;
    public int? PageCount { get; } = pageCount;
    public string FileName { get; } = fileName;
    public long? DurationMs { get; } = durationMs;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { PdfStream.Dispose(); }
        finally { responseOwner?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await PdfStream.DisposeAsync().ConfigureAwait(false); }
        finally { responseOwner?.Dispose(); }
    }
}
