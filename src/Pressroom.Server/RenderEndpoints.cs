using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;
using Pressroom.Server.Printing;
using Pressroom.Server.Jobs;

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

        app.MapGet("/v1/info", (IHtmlRenderer renderer, AdmissionGate admission, IOptions<RenderSettings> configured, IJobStore jobs, RenderCoordinator coordinator) =>
        {
            var state = renderer.GetSnapshot();
            var settings = configured.Value;
            return Results.Json(new
            {
                version = Program.ApplicationVersion,
                playwrightVersion = state.PlaywrightVersion,
                chromiumVersion = state.BrowserVersion,
                active = state.Active,
                queued = state.Queued + coordinator.WaitingSync,
                admitted = admission.Admitted,
                admissionLimit = admission.Limit,
                concurrency = settings.Concurrency,
                queueLimit = settings.QueueLimit,
                maxBodyBytes = settings.MaxBodyBytes,
                maxTimeoutMs = settings.MaxTimeoutMs,
                rendersSinceStartup = state.Completed,
                lastBrowserRestartUtc = state.LastRestartUtc,
                pageSizes = NamedPageSizes,
                fonts = state.Fonts,
                jobs = jobs.Capacity
            });
        }).RequireAuthorization();

        app.MapGet("/openapi/v1.json", (IOptions<AuthSettings> auth) => Results.Content(
            auth.Value.Mode == AuthMode.Anonymous ? AnonymousOpenApi.Value : OpenApi.Value, "application/json"))
            .RequireAuthorization();

        app.MapPost("/v1/render", HandleRenderAsync).RequireAuthorization();

        app.MapGet("/v1/printers", async (HttpContext context, IPrintService printing) =>
        {
            if (!(await context.AuthenticateAsync(AuthenticationNames.ApiKey)).Succeeded)
            {
                await context.ChallengeAsync(AuthenticationNames.ApiKey);
                return;
            }
            await Results.Json(new { printers = printing.Printers }).ExecuteAsync(context);
        }).RequireAuthorization();
    }

    private static async Task HandleRenderAsync(HttpContext context, IHtmlRenderer renderer,
        AdmissionGate admission, IOptions<RenderSettings> configured,
        IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory, IPrintService printing, RenderCoordinator coordinator)
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
        var printingStarted = false;
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

            var authentication = await context.AuthenticateAsync(AuthenticationNames.ApiKey);
            if (request.Printer is not null && !authentication.Succeeded)
            {
                await context.ChallengeAsync(AuthenticationNames.ApiKey);
                return;
            }

            request = Prepare(request, authentication, settings, printing, out var errors);
            if (errors.Count == 0)
            {
                // The parsed deadline includes time already spent reading and checking the body.
                var remaining = TimeSpan.FromMilliseconds(request.Options!.TimeoutMs!.Value) - Stopwatch.GetElapsedTime(admittedAt);
                if (remaining <= TimeSpan.Zero)
                    deadline.Cancel();
                else
                    deadline.CancelAfter(remaining);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (errors.Count != 0)
            {
                await Problems.WriteAsync(context, 400, "Invalid request", errors: errors);
                return;
            }

            var output = await coordinator.RenderAsync(request, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            pages = output.PageCount;
            blocked = output.BlockedResources;

            if (request.Printer is { } printer)
            {
                printingStarted = true;
                var result = await printing.PrintAsync(printer, output, deadline.Token);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                await Results.Json(result, statusCode: StatusCodes.Status202Accepted).ExecuteAsync(context);
                return;
            }

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
        catch (PrintUnavailableException)
        {
            await Problems.WriteAsync(context, 503, "Printing unavailable",
                "Check the print helper configuration (Printing:SumatraPdfPath on Windows or Printing:CupsCommandPath on Linux) and the service identity's access to the executable and temporary directory.");
        }
        catch (PrintSubmissionException)
        {
            await Problems.WriteAsync(context, 502, "Print submission failed",
                "The print helper reported a failure. Check the configured printer and its permissions. The job may have partially printed; check the print queue before retrying.");
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
            await Problems.WriteAsync(context, 504, printingStarted ? "Print deadline expired" : "Render deadline expired",
                printingStarted ? "Print submission exceeded the processing timeout. A job may already be in the print queue; check it before retrying."
                    : "The request exceeded its overall processing timeout.");
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

    internal static bool HasSupportedContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (media.Charset.HasValue && !string.Equals(media.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase))
            return false;
        var encoding = request.Headers.ContentEncoding.ToString();
        return string.IsNullOrWhiteSpace(encoding) || string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies caller permissions and service defaults to a parsed request, then validates it. Synchronous
    /// renders and job submissions both use this so a stored job is held to exactly the same rules.
    /// </summary>
    internal static RenderRequest Prepare(RenderRequest request, AuthenticateResult authentication, RenderSettings settings,
        IPrintService printing, out Dictionary<string, string[]> errors)
    {
        if (request.Options is { } suppliedOptions)
        {
            // Permissions come only from the API-key handler, never an unrelated
            // host identity. A request can disable scripts but cannot grant them.
            var javaScriptPermitted = authentication.Succeeded
                && authentication.Principal?.HasClaim(AuthenticationNames.JavaScriptClaim, "true") == true;
            request = request with
            {
                Options = suppliedOptions with
                {
                    JavaScript = suppliedOptions.JavaScript && javaScriptPermitted,
                    PageSize = suppliedOptions.PageSize ?? PageSize.Named(settings.DefaultPageSize),
                    TimeoutMs = suppliedOptions.TimeoutMs ?? settings.DefaultTimeoutMs
                }
            };
        }
        errors = RequestValidator.Validate(request, settings.MaxTimeoutMs);
        // A malformed identifier keeps its specific validation message.
        if (request.Printer is not null && !errors.ContainsKey("printer")
            && !printing.Printers.Contains(request.Printer, StringComparer.OrdinalIgnoreCase))
            errors["printer"] = ["Select a printer identifier configured in Printing:Printers."];
        if (errors.Count == 0)
        {
            RejectBase(request.Html, "html", errors);
            RejectBase(request.Options!.HeaderTemplate, "options.headerTemplate", errors);
            RejectBase(request.Options.FooterTemplate, "options.footerTemplate", errors);
        }
        return request;
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
