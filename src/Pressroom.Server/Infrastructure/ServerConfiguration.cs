using Microsoft.Extensions.Options;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;

namespace Pressroom.Server.Infrastructure;

public sealed class ServerConfiguration(IOptions<RenderSettings> render, IOptions<AuthSettings> auth, IOptions<PathsSettings> paths)
{
    public const long OuterBodyLimit = 32 * 1024 * 1024;

    public void Validate()
    {
        ValidateRenderSettings(render.Value);
        ValidateAuthSettings(auth.Value);
        _ = ResolveStateRoot(paths.Value);
    }

    public static void ValidateAuthSettings(AuthSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode))
            throw new InvalidOperationException("Auth:Mode must be ApiKey or Anonymous.");
        if (settings.ApiKeys is null || settings.ApiKeys.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128
            || pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.Secret) || pair.Value.Secret.Length is < 16 or > 4096))
            throw new InvalidOperationException("Auth:ApiKeys must contain named key objects with a Secret of 16–4096 characters; identifiers must be 1–128 characters.");
        if (settings.ApiKeys.Values.Select(key => key.Secret).Distinct(StringComparer.Ordinal).Count() != settings.ApiKeys.Count)
            throw new InvalidOperationException("Auth:ApiKeys entries must use distinct secrets so permissions identify exactly one key.");
        if (settings.Mode == AuthMode.ApiKey && settings.ApiKeys.Count == 0)
            throw new InvalidOperationException("Configure at least one Auth:ApiKeys entry or explicitly set Auth:Mode to Anonymous.");
    }

    public static void ValidateRenderSettings(RenderSettings settings)
    {
        settings.Validate();
        if (settings.DefaultTimeoutMs < 100)
            throw new InvalidOperationException("Render:DefaultTimeoutMs must be at least 100 ms.");
        if (settings.MaxBodyBytes is < 1 or >= OuterBodyLimit)
            throw new InvalidOperationException("Render:MaxBodyBytes must be positive and below the 32 MiB outer server limit.");
        if (settings.ShutdownTimeoutMs > 50_000)
            throw new InvalidOperationException("Render:ShutdownTimeoutMs must not exceed 50000 ms under the default ANCM 60-second shutdown budget.");
    }

    public static string ResolveStateRoot(PathsSettings settings)
    {
        var fullPath = RuntimePaths.ResolveStateRoot(settings);
        var releasePath = Path.GetFullPath(AppContext.BaseDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath.Equals(releasePath.TrimEnd(Path.DirectorySeparatorChar), comparison)
            || fullPath.StartsWith(releasePath, comparison))
            throw new InvalidOperationException("Paths:StateRoot must be outside the application release directory.");
        return fullPath;
    }

    public static void EnsureWritableDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".pressroom-probe-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }
}

public sealed class BrowserLifecycleService(BrowserRenderer renderer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => renderer.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => renderer.StopAsync(cancellationToken);
}

public sealed class ConfigurationValidationService(ServerConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        configuration.Validate();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
