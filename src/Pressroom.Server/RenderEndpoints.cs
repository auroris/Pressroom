using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;

namespace Pressroom.Server;

public static class RenderEndpoints
{
    private static readonly string[] NamedPageSizes = ["Letter", "Legal", "Tabloid", "A3", "A4", "A5"];
    private static readonly Lazy<string> OpenApi = new(() =>
    {
        using var stream = typeof(RenderEndpoints).Assembly.GetManifestResourceStream("Pressroom.Server.openapi.json")
            ?? throw new InvalidOperationException("The embedded OpenAPI document is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    private static readonly Lazy<string> AnonymousOpenApi = new(() =>
    {
        var document = JsonNode.Parse(OpenApi.Value)!;
        document["security"] = new JsonArray();
        return document.ToJsonString();
    });

    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/health", (IHtmlRenderer renderer, IHostApplicationLifetime lifetime) =>
        {
            var ready = !lifetime.ApplicationStopping.IsCancellationRequested && renderer.GetSnapshot().IsReady;
            return Results.Json(new { status = ready ? "ready" : "unavailable" }, statusCode: ready ? 200 : 503);
        }).AllowAnonymous();

        app.MapGet("/v1/info", (IHtmlRenderer renderer, AdmissionGate admission, IOptions<RenderSettings> configured) =>
        {
            var state = renderer.GetSnapshot();
            var settings = configured.Value;
            return Results.Json(new
            {
                version = Program.ApplicationVersion,
                playwrightVersion = state.PlaywrightVersion,
                chromiumVersion = state.BrowserVersion,
                active = state.Active,
                queued = state.Queued,
                admitted = admission.Admitted,
                admissionLimit = admission.Limit,
                concurrency = settings.Concurrency,
                queueLimit = settings.QueueLimit,
                maxBodyBytes = settings.MaxBodyBytes,
                maxTimeoutMs = settings.MaxTimeoutMs,
                rendersSinceStartup = state.Completed,
                lastBrowserRestartUtc = state.LastRestartUtc,
                pageSizes = NamedPageSizes,
                fonts = state.Fonts
            });
        }).RequireAuthorization();

        app.MapGet("/openapi/v1.json", (IOptions<AuthSettings> auth) => Results.Content(
            auth.Value.Mode == AuthMode.Anonymous ? AnonymousOpenApi.Value : OpenApi.Value, "application/json"))
            .RequireAuthorization();

        app.MapPost("/v1/render", HandleRenderAsync).RequireAuthorization();
    }

    private static async Task HandleRenderAsync(HttpContext context, IHtmlRenderer renderer,
        AdmissionGate admission, IOptions<RenderSettings> configured,
        IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        var settings = configured.Value;
        if (!HasSupportedContentType(context.Request))
        {
            await Problems.WriteAsync(context, 415, "Unsupported request format", "Send an uncompressed UTF-8 application/json body.");
            return;
        }
        if (context.Request.ContentLength > settings.MaxBodyBytes)
        {
            await Problems.WriteAsync(context, 413, "Request too large", "The complete JSON body exceeds the application limit.");
            return;
        }
        if (lifetime.ApplicationStopping.IsCancellationRequested || !renderer.GetSnapshot().IsReady)
        {
            await UnavailableAsync(context);
            return;
        }

        using var permit = admission.TryEnter();
        if (permit is null)
        {
            context.Response.Headers.RetryAfter = "1";
            await Problems.WriteAsync(context, 429, "Render capacity exhausted", "Retry this request after the indicated delay.");
            return;
        }

        // This permit is intentionally kept through ExecuteAsync below, including slow PDF readers.
        var admittedAt = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        deadline.CancelAfter(settings.MaxTimeoutMs);
        var logger = loggerFactory.CreateLogger("Pressroom.Server.Render");
        long bodyBytes = 0;
        var pages = 0;
        var blocked = 0;
        long processingMilliseconds = 0;
        try
        {
            RenderRequest? request;
            using (var body = await BoundedBodyReader.ReadAsync(context.Request.Body, settings.MaxBodyBytes, deadline.Token))
            {
                bodyBytes = body.Length;
                deadline.Token.ThrowIfCancellationRequested();
                request = JsonSerializer.Deserialize<RenderRequest>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), JsonDefaults.Options);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (request is null)
            {
                await Problems.WriteAsync(context, 400, "Invalid request", errors: new Dictionary<string, string[]> { ["body"] = ["A request object is required."] });
                return;
            }

            if (request.Options is { } suppliedOptions)
                request = request with
                {
                    Options = suppliedOptions with
                    {
                        PageSize = suppliedOptions.PageSize ?? PageSize.Named(settings.DefaultPageSize),
                        TimeoutMs = suppliedOptions.TimeoutMs ?? settings.DefaultTimeoutMs
                    }
                };
            var errors = RequestValidator.Validate(request, settings.MaxTimeoutMs);
            if (errors.Count == 0)
            {
                var effectiveOptions = request.Options!;
                var remaining = TimeSpan.FromMilliseconds(effectiveOptions.TimeoutMs!.Value) - Stopwatch.GetElapsedTime(admittedAt);
                if (remaining <= TimeSpan.Zero)
                    deadline.Cancel();
                else
                    deadline.CancelAfter(remaining);
                deadline.Token.ThrowIfCancellationRequested();

                RejectBase(request.Html, "html", errors);
                RejectBase(request.Options!.HeaderTemplate, "options.headerTemplate", errors);
                RejectBase(request.Options.FooterTemplate, "options.footerTemplate", errors);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (errors.Count != 0)
            {
                await Problems.WriteAsync(context, 400, "Invalid request", errors: errors);
                return;
            }

            var output = await renderer.RenderAsync(request, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            pages = output.PageCount;
            blocked = output.BlockedResources;
            processingMilliseconds = (long)Stopwatch.GetElapsedTime(admittedAt).TotalMilliseconds;

            // The processing deadline excludes transfer time. RequestAborted still governs the response.
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            context.Response.Headers["X-Render-Pages"] = output.PageCount.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-Render-Duration-Ms"] = processingMilliseconds.ToString(CultureInfo.InvariantCulture);
            var disposition = new ContentDispositionHeaderValue(request.Disposition == PdfDisposition.Inline ? "inline" : "attachment");
            // The framework emits an ASCII filename fallback plus RFC 5987 filename* for Unicode.
            disposition.SetHttpFileName(RequestValidator.SanitizeFileName(request.FileName));
            context.Response.GetTypedHeaders().ContentDisposition = disposition;
            // A download filename here would make Results.File overwrite an inline disposition.
            await Results.File(output.Pdf, "application/pdf", fileDownloadName: null,
                enableRangeProcessing: false).ExecuteAsync(context);
        }
        catch (BodyLimitExceededException)
        {
            await Problems.WriteAsync(context, 413, "Request too large", "The complete JSON body exceeds the application limit.");
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            await Problems.WriteAsync(context, 413, "Request too large", "The request exceeds the server body limit.");
        }
        catch (JsonException)
        {
            await Problems.WriteAsync(context, 400, "Invalid JSON", errors: new Dictionary<string, string[]>
            {
                ["body"] = ["Supply a valid request using the documented property names and types. Unknown properties are rejected."]
            });
        }
        catch (RenderDocumentException)
        {
            await Problems.WriteAsync(context, 422, "Document could not be rendered", "A document readiness condition failed or page execution reported an error.");
        }
        catch (RenderUnavailableException)
        {
            await UnavailableAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await UnavailableAsync(context);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await Problems.WriteAsync(context, 504, "Render deadline expired", "The request exceeded its overall processing timeout.");
        }
        catch
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 500;
            throw;
        }
        finally
        {
            if (processingMilliseconds == 0)
                processingMilliseconds = (long)Stopwatch.GetElapsedTime(admittedAt).TotalMilliseconds;
            logger.LogInformation("Render {RequestId} caller {Caller} status {Status} bodyBytes {BodyBytes} pages {Pages} durationMs {DurationMs} blockedResources {BlockedResources}",
                context.TraceIdentifier, SafeCaller(context.User.Identity?.Name),
                context.RequestAborted.IsCancellationRequested ? 499 : context.Response.StatusCode,
                bodyBytes, pages, processingMilliseconds, blocked);
        }
    }

    private static string SafeCaller(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown"
        : new string(value.Where(c => !char.IsControl(c)).Take(128).ToArray());

    private static bool HasSupportedContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (media.Charset.HasValue && !string.Equals(media.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase))
            return false;
        var encoding = request.Headers.ContentEncoding.ToString();
        return string.IsNullOrWhiteSpace(encoding) || string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectBase(string? html, string field, IDictionary<string, string[]> errors)
    {
        if (!string.IsNullOrEmpty(html) && DocumentValidator.HasBaseElement(html))
            errors[field] = ["The base element is not supported. Embed all document resources."];
    }

    private static Task UnavailableAsync(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        return Problems.WriteAsync(context, 503, "Renderer unavailable", "The renderer is starting, restarting, or shutting down.");
    }
}
