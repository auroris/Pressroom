using Pressroom.Rendering;

namespace Pressroom.Server.Jobs;

public sealed class JobsSettings
{
    public bool Enabled { get; set; }
    public int Workers { get; set; } = 1;
    public int ReservedSyncSlots { get; set; } = 1;
    public int MaxQueued { get; set; } = 200;
    public int MaxQueuedPerKey { get; set; } = 100;
    public TimeSpan MaxQueueAge { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ResultRetention { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RequestRetention { get; set; } = TimeSpan.FromHours(1);
    public long MaxStoredBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public bool DeleteResultOnDownload { get; set; }

    public void Validate(RenderSettings render)
    {
        if (Workers is < 1 or > 64 || ReservedSyncSlots < 1
            || (Enabled && (ReservedSyncSlots >= render.Concurrency || Workers > render.Concurrency - ReservedSyncSlots)))
            throw new InvalidOperationException("Jobs:Workers must be 1–64 and leave Jobs:ReservedSyncSlots (at least 1) of Render:Concurrency for synchronous requests.");
        if (MaxQueued is < 1 or > 100_000 || MaxQueuedPerKey < 1 || MaxQueuedPerKey > MaxQueued)
            throw new InvalidOperationException("Jobs queue limits must be positive; MaxQueuedPerKey must not exceed MaxQueued (at most 100000).");
        if (MaxStoredBytes < 1024 * 1024 || MaxStoredBytes > 1024L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Jobs:MaxStoredBytes must be between 1 MiB and 1 TiB.");
        foreach (var duration in new[] { MaxQueueAge, ResultRetention, RequestRetention })
            if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromDays(30))
                throw new InvalidOperationException("Jobs queue age and retention must be between one second and 30 days.");
    }
}
