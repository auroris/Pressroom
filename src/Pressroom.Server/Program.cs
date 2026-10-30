using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.Options;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;
using Pressroom.Server.Printing;
using Serilog;
using Serilog.Events;

namespace Pressroom.Server;

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        var check = args.Contains("--check", StringComparer.Ordinal);
        try
        {
            await using var app = CreateApplication(args.Where(a => a != "--check").ToArray(), check);
            if (check)
            {
                // An offline package check: no listening socket, no installer, no credentials required.
                var renderer = app.Services.GetRequiredService<BrowserRenderer>();
                var settings = app.Services.GetRequiredService<IOptions<RenderSettings>>().Value;
                ServerConfiguration.ValidateRenderSettings(settings);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.MaxTimeoutMs));
                try
                {
                    await renderer.StartAsync(timeout.Token);
                    var snapshot = renderer.GetSnapshot();
                    if (!snapshot.IsReady)
                        throw new InvalidOperationException("The browser did not become ready.");
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        status = "ready", version = ApplicationVersion,
                        chromiumVersion = snapshot.BrowserVersion,
                        playwrightVersion = snapshot.PlaywrightVersion
                    }));
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.ShutdownTimeoutMs));
                    await renderer.StopAsync(cleanup.Token);
                }
                return 0;
            }

            await app.RunAsync();
            return 0;
        }
        catch (Exception exception)
        {
            // Startup has not accepted a document. This console diagnostic is deliberately separate
            // from request logging, which never includes exception messages or document content.
            Console.Error.WriteLine($"Pressroom startup/check failed ({exception.GetType().Name}): {exception.Message}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    public static string ApplicationVersion =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

    internal static WebApplication CreateApplication(string[] args, bool check)
    {
        var configurationFile = ApplicationConfiguration.GetConfigurationFile(args);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        ApplicationConfiguration.Configure(builder.Configuration, AppContext.BaseDirectory, configurationFile);

        builder.Services.AddOptions<RenderSettings>().BindConfiguration("Render");
        builder.Services.AddOptions<PathsSettings>().BindConfiguration("Paths");
        builder.Services.AddOptions<FontsSettings>().BindConfiguration("Fonts");
        builder.Services.AddOptions<AuthSettings>().BindConfiguration("Auth", options => options.ErrorOnUnknownConfiguration = true);
        builder.Services.AddOptions<PrintingSettings>().BindConfiguration("Printing", options => options.ErrorOnUnknownConfiguration = true);
        builder.Services.AddSingleton<ServerConfiguration>();
        if (!check)
            builder.Services.AddHostedService<ConfigurationValidationService>();

        builder.Services.Configure<IISServerOptions>(options =>
        {
            options.AutomaticAuthentication = false;
            options.MaxRequestBodySize = ServerConfiguration.OuterBodyLimit;
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = ServerConfiguration.OuterBodyLimit;
        });
        builder.Services.AddOptions<HostOptions>().Configure<IOptions<RenderSettings>>((options, render) =>
            options.ShutdownTimeout = TimeSpan.FromMilliseconds(render.Value.ShutdownTimeoutMs + 5000));

        builder.Services.AddSerilog((services, logger) =>
        {
            var state = ServerConfiguration.ResolveStateRoot(services.GetRequiredService<IOptions<PathsSettings>>().Value);
            var logDirectory = Path.Combine(state, "logs");
            ServerConfiguration.EnsureWritableDirectory(logDirectory);
            logger.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Filter.ByIncludingOnly(e => e.Properties.TryGetValue("SourceContext", out var source)
                    && source is ScalarValue { Value: string name } && name.StartsWith("Pressroom.", StringComparison.Ordinal))
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}")
                .WriteTo.File(Path.Combine(logDirectory, "pressroom-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true,
                    outputTemplate: "{Timestamp:O} [{Level:u3}] {Message:lj}{NewLine}");
        });

        builder.Services.AddAuthentication(AuthenticationNames.ApiKey)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthenticationNames.ApiKey, null);

        builder.Services.AddAuthorization(options =>
        {
            var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthenticationNames.ApiKey)
                .RequireAssertion(context =>
                {
                    if (context.User.Identity?.IsAuthenticated == true
                        && context.User.HasClaim(AuthenticationNames.BranchClaim, AuthenticationNames.ApiKey))
                        return true;
                    var http = context.Resource as HttpContext;
                    var auth = http?.RequestServices.GetRequiredService<IOptions<AuthSettings>>().Value;
                    return auth?.Mode == AuthMode.Anonymous
                        && !http!.Request.Headers.ContainsKey(AuthenticationNames.ApiKeyHeader);
                }).Build();
            options.DefaultPolicy = policy;
            options.FallbackPolicy = policy;
        });

        builder.Services.AddSingleton<AdmissionGate>();
        builder.Services.AddSingleton<IPrintProcessRunner, PrintProcessRunner>();
        builder.Services.AddSingleton<IPrintService, PrintService>();
        builder.Services.AddSingleton<BrowserRenderer>();
        builder.Services.AddSingleton<IHtmlRenderer>(services => services.GetRequiredService<BrowserRenderer>());
        builder.Services.AddHostedService<BrowserLifecycleService>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                context.Abort();
            }
            catch (Exception exception)
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError("Request {RequestId} failed with {ExceptionType}", context.TraceIdentifier, exception.GetType().Name);
                if (context.Response.HasStarted)
                    context.Abort();
                else
                    await Problems.WriteAsync(context, 500, "Request failed", "The request could not be completed.");
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        RenderEndpoints.Map(app);
        return app;
    }
}
