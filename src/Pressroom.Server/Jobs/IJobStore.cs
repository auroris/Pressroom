using Pressroom.Contracts;
using Pressroom.Rendering;

namespace Pressroom.Server.Jobs;

public sealed record StoredJob(RenderJob Job, string Owner, string? IdempotencyHash, long RequestBytes = 0, long ResultBytes = 0,
    DateTimeOffset? RequestExpiresUtc = null, DateTimeOffset? RemoveUtc = null, long Sequence = 0, int SchemaVersion = 1);
public sealed record JobCapacity(bool Enabled, int Queued, int Rendering, int Stored, long StoredBytes, int Workers, int MaxQueued);
public sealed class JobCapacityException : Exception;

public interface IJobStore
{
    void Initialize();
    RenderJob Submit(string owner, RenderRequest request, string? idempotencyKey);
    RenderJob? Get(string owner, string id);
    RenderJobPage List(string owner, JobStatus? status, int limit, string? after);
    IReadOnlyList<StoredJob> Queued();
    RenderRequest? ReadRequest(string id);
    bool Start(string id);
    void Succeed(string id, RenderOutput output, long durationMs, PrintJobResult? printJob);
    void Fail(string id, JobError error);
    bool Cancel(string owner, string id);
    JobDownload? OpenResult(string owner, string id);
    void CompleteDownload(string id);
    void Sweep();
    JobCapacity Capacity { get; }
}

public sealed class JobDownload(RenderJob job, Stream stream, Action released) : IDisposable
{
    public RenderJob Job { get; } = job;
    public Stream Stream { get; } = stream;
    public void Dispose() { try { Stream.Dispose(); } finally { released(); } }
}
