using Microsoft.Extensions.Options;
using Pressroom.Rendering;

namespace Pressroom.Server.Infrastructure;

/// <summary>Bounds bodies, queued/active work, and PDF responses still being transferred.</summary>
public sealed class AdmissionGate : IDisposable
{
    private readonly SemaphoreSlim _permits;
    private int _admitted;

    public AdmissionGate(IOptions<RenderSettings> options)
    {
        ServerConfiguration.ValidateRenderSettings(options.Value);
        Limit = checked(options.Value.Concurrency + options.Value.QueueLimit);
        _permits = new SemaphoreSlim(Limit, Limit);
    }

    public int Limit { get; }
    public int Admitted => Volatile.Read(ref _admitted);

    public IDisposable? TryEnter()
    {
        if (!_permits.Wait(0))
            return null;
        Interlocked.Increment(ref _admitted);
        return new Permit(this);
    }

    private sealed class Permit(AdmissionGate owner) : IDisposable
    {
        private AdmissionGate? _owner = owner;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _owner, null);
            if (gate is null)
                return;
            Interlocked.Decrement(ref gate._admitted);
            gate._permits.Release();
        }
    }

    public void Dispose() => _permits.Dispose();
}
