using Pressroom.Contracts;

namespace Pressroom.Rendering;

public interface IHtmlRenderer
{
    Task<RenderOutput> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default);
    RendererSnapshot GetSnapshot();
}

public sealed record RenderOutput(byte[] Pdf, int PageCount, int BlockedResources);

public sealed record PackagedFont(string Family, string Weight, string Style);

public sealed record RendererSnapshot
{
    public bool IsReady { get; init; }
    public string? BrowserVersion { get; init; }
    public string PlaywrightVersion { get; init; } = "unknown";
    public int Active { get; init; }
    public int Queued { get; init; }
    public long Completed { get; init; }
    public DateTimeOffset? LastRestartUtc { get; init; }
    public IReadOnlyList<PackagedFont> Fonts { get; init; } = [];
}

public sealed class RenderUnavailableException(string message) : Exception(message);
public sealed class RenderDocumentException(string message) : Exception(message);

public sealed class RenderSettings
{
    public int Concurrency { get; set; } = 2;
    public int QueueLimit { get; set; } = 8;
    public long MaxBodyBytes { get; set; } = 26_214_400;
    public int DefaultTimeoutMs { get; set; } = 30_000;
    public int MaxTimeoutMs { get; set; } = 60_000;
    public int ShutdownTimeoutMs { get; set; } = 30_000;
    public int RestartAfterRenders { get; set; } = 500;
    public string DefaultPageSize { get; set; } = "Letter";
    public string BrowsersPath { get; set; } = "browsers";
    public string? ChromiumPath { get; set; }
    public bool ChromiumSandbox { get; set; } = true;
    public int CleanupTimeoutMs { get; set; } = 5_000;

    public void Validate()
    {
        if (Concurrency is < 1 or > 64 || QueueLimit is < 0 or > 1_024)
            throw new InvalidOperationException("Render concurrency must be 1–64 and queue limit 0–1024.");
        if (MaxBodyBytes is < 1 or > 268_435_456)
            throw new InvalidOperationException("Render:MaxBodyBytes must be between 1 byte and 256 MiB.");
        if (DefaultTimeoutMs < 1 || MaxTimeoutMs < DefaultTimeoutMs || MaxTimeoutMs > 300_000)
            throw new InvalidOperationException("Render timeouts must be positive; the maximum must cover the default and cannot exceed five minutes.");
        if (ShutdownTimeoutMs is < 1_000 or > 300_000 || CleanupTimeoutMs is < 100 or > 30_000)
            throw new InvalidOperationException("Render shutdown and cleanup timeouts are outside the supported range.");
        if ((long)CleanupTimeoutMs * 2 >= ShutdownTimeoutMs)
            throw new InvalidOperationException("Render:ShutdownTimeoutMs must exceed twice CleanupTimeoutMs to reserve browser and process termination time.");
        if (RestartAfterRenders < 1)
            throw new InvalidOperationException("Render:RestartAfterRenders must be positive.");
        if (!new[] { "Letter", "Legal", "Tabloid", "A3", "A4", "A5" }.Contains(DefaultPageSize, StringComparer.Ordinal))
            throw new InvalidOperationException("Render:DefaultPageSize is not a supported named size.");
        if (string.IsNullOrWhiteSpace(BrowsersPath))
            throw new InvalidOperationException("Render:BrowsersPath cannot be empty.");
    }
}

public sealed class FontsSettings
{
    public string Manifest { get; set; } = "fonts/manifest.json";
}

public sealed class PathsSettings
{
    public string StateRoot { get; set; } = OperatingSystem.IsWindows()
        ? @"%ProgramData%\Pressroom"
        : Path.Combine(Path.GetTempPath(), "Pressroom");
}

public static class RuntimePaths
{
    public static string ResolveStateRoot(PathsSettings settings)
    {
        var value = Environment.ExpandEnvironmentVariables(settings.StateRoot);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException("Paths:StateRoot must expand to an absolute path.");
        return Path.GetFullPath(value);
    }

    public static string ResolveApplicationPath(string path) => Path.GetFullPath(path, AppContext.BaseDirectory);
}
