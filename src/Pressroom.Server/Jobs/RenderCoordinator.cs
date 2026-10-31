using Microsoft.Extensions.Options;
using Pressroom.Contracts;
using Pressroom.Rendering;

namespace Pressroom.Server.Jobs;

/// <summary>One capacity boundary for HTTP and jobs; waiting synchronous callers take priority.</summary>
public sealed class RenderCoordinator(IHtmlRenderer renderer, IOptions<RenderSettings> render)
{
    private readonly SemaphoreSlim _slots = new(render.Value.Concurrency, render.Value.Concurrency);
    private readonly object _sync = new();
    private int _waitingSync;

    public int WaitingSync { get { lock (_sync) return _waitingSync; } }

    public async Task<RenderOutput> RenderAsync(RenderRequest request, CancellationToken token)
    {
        lock (_sync) _waitingSync++;
        try { await _slots.WaitAsync(token); }
        finally { lock (_sync) _waitingSync--; }
        try { return await renderer.RenderAsync(request, token); }
        finally { _slots.Release(); }
    }

    public IDisposable? TryReserveBackground(int reserved)
    {
        lock (_sync)
        {
            if (_waitingSync != 0 || _slots.CurrentCount <= reserved || !renderer.GetSnapshot().IsReady || !_slots.Wait(0)) return null;
            return new Lease(_slots);
        }
    }

    public Task<RenderOutput> RenderReservedAsync(RenderRequest request, CancellationToken token) => renderer.RenderAsync(request, token);

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Release(); }
    }
}
