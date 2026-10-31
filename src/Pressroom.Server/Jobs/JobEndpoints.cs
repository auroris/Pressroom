using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;
using Pressroom.Server.Printing;

namespace Pressroom.Server.Jobs;

public static class JobEndpoints
{
    public static void Map(WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<JobsSettings>>().Value.Enabled) return;
        var group = app.MapGroup("/v1/jobs").RequireAuthorization();
        group.MapPost("", SubmitAsync);
        group.MapGet("/{id}", async (HttpContext context, string id, IJobStore store) =>
        {
            var job = JobValidation.IsId(id) ? store.Get(await OwnerAsync(context), id) : null;
            if (job is null) { await NotFound(context); return; }
            if (!job.IsTerminal) context.Response.Headers.RetryAfter = "1";
            await Results.Json(WithLinks(context, job), JsonDefaults.Options).ExecuteAsync(context);
        });
        group.MapGet("", async (HttpContext context, IJobStore store) =>
        {
            JobStatus? status = null;
            if (context.Request.Query.TryGetValue("status", out var text))
            {
                if (!Enum.TryParse<JobStatus>(text.ToString(), true, out var parsed) || !Enum.IsDefined(parsed)
                    || parsed.ToString().ToLowerInvariant() != text.ToString())
                { await Problems.WriteAsync(context, 400, "Invalid status filter"); return; }
                status = parsed;
            }
            var limit = 50;
            if (context.Request.Query.TryGetValue("limit", out var size) && (!int.TryParse(size, out limit) || limit is < 1 or > 100))
            { await Problems.WriteAsync(context, 400, "Limit must be between 1 and 100"); return; }
            var after = context.Request.Query["after"].FirstOrDefault();
            if (after is not null && !JobValidation.IsId(after)) { await Problems.WriteAsync(context, 400, "Invalid cursor"); return; }
            var page = store.List(await OwnerAsync(context), status, limit, after);
            await Results.Json(page with { Items = page.Items.Select(job => WithLinks(context, job)).ToArray() }, JsonDefaults.Options).ExecuteAsync(context);
        });
        group.MapDelete("/{id}", async (HttpContext context, string id, IJobStore store, JobScheduler scheduler) =>
        {
            if (!JobValidation.IsId(id) || !store.Cancel(await OwnerAsync(context), id)) { await NotFound(context); return; }
            scheduler.Cancel(id);
            context.Response.StatusCode = 204;
        });
        group.MapGet("/{id}/result", DownloadAsync);
    }

    private static async Task SubmitAsync(HttpContext context, IJobStore store, JobSubmissionGate gate,
        IOptions<RenderSettings> configured, IPrintService printing, IHostApplicationLifetime lifetime)
    {
        var settings = configured.Value;
        if (lifetime.ApplicationStopping.IsCancellationRequested) { await Problems.WriteAsync(context, 503, "Service stopping"); return; }
        if (!RenderEndpoints.HasSupportedContentType(context.Request))
        { await Problems.WriteAsync(context, 415, "Unsupported request format", "Send an uncompressed UTF-8 application/json body."); return; }
        if (context.Request.ContentLength > settings.MaxBodyBytes)
        { await TooLarge(context); return; }
        string? idempotencyKey = null;
        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var keys))
        {
            if (keys.Count != 1 || !JobValidation.IsIdempotencyKey(keys[0]))
            { await Problems.WriteAsync(context, 400, "Invalid Idempotency-Key", "Use 1–128 visible ASCII characters in one header value."); return; }
            idempotencyKey = keys[0];
        }
        using var permit = gate.TryEnter();
        if (permit is null) { await Full(context); return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        timeout.CancelAfter(settings.MaxTimeoutMs);
        try
        {
            using var body = await BoundedBodyReader.ReadAsync(context.Request.Body, settings.MaxBodyBytes, timeout.Token);
            var request = JsonSerializer.Deserialize<RenderRequest>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), JsonDefaults.Options);
            if (request is null) { await Problems.WriteAsync(context, 400, "A request object is required"); return; }
            var authentication = await context.AuthenticateAsync(AuthenticationNames.ApiKey);
            if (request.Printer is not null && !authentication.Succeeded)
            {
                await context.ChallengeAsync(AuthenticationNames.ApiKey);
                return;
            }
            request = RenderEndpoints.Prepare(request, authentication, settings, printing, out var errors);
            if (errors.Count != 0) { await Problems.WriteAsync(context, 400, "Invalid request", errors: errors); return; }
            timeout.Token.ThrowIfCancellationRequested();
            var job = store.Submit(await OwnerAsync(context), request, idempotencyKey);
            context.Response.Headers.Location = context.Request.PathBase + $"/v1/jobs/{job.Id}";
            context.Response.Headers.RetryAfter = "1";
            await Results.Json(WithLinks(context, job), JsonDefaults.Options, statusCode: 202).ExecuteAsync(context);
        }
        catch (BodyLimitExceededException) { await TooLarge(context); }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413) { await TooLarge(context); }
        catch (JsonException) { await Problems.WriteAsync(context, 400, "Invalid JSON", "Use the documented request properties and types."); }
        catch (JobCapacityException) { await Full(context); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { await Problems.WriteAsync(context, lifetime.ApplicationStopping.IsCancellationRequested ? 503 : 504, "Job submission interrupted"); }
    }

    private static RenderJob WithLinks(HttpContext context, RenderJob job)
    {
        var path = $"{context.Request.PathBase}/v1/jobs/{job.Id}";
        return job with { Links = new(path, path + "/result") };
    }

    private static async Task DownloadAsync(HttpContext context, string id, IJobStore store)
    {
        var owner = await OwnerAsync(context);
        var job = JobValidation.IsId(id) ? store.Get(owner, id) : null;
        if (job is null) { await NotFound(context); return; }
        if (job.Status is JobStatus.Cancelled or JobStatus.Expired)
        { await Problems.WriteAsync(context, 410, "Job result no longer available"); return; }
        if (job.Status != JobStatus.Succeeded || job.Printer is not null)
        {
            if (!job.IsTerminal) context.Response.Headers.RetryAfter = "1";
            await Problems.WriteAsync(context, 409, "No PDF result available", job.Printer is not null ? "Print jobs return a receipt in the job document." : "Poll the job document for its state and error.");
            return;
        }
        using var download = store.OpenResult(owner, id);
        if (download is null)
        {
            var current = store.Get(owner, id);
            if (current?.Status is JobStatus.Expired or JobStatus.Cancelled) { await Problems.WriteAsync(context, 410, "Job result no longer available"); return; }
            context.Response.Headers.RetryAfter = "1";
            await Problems.WriteAsync(context, 409, "Result is already being downloaded");
            return;
        }
        context.Response.Headers["X-Render-Pages"] = job.PageCount?.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Render-Duration-Ms"] = job.DurationMs?.ToString(CultureInfo.InvariantCulture);
        var disposition = new ContentDispositionHeaderValue(job.Disposition == PdfDisposition.Inline ? "inline" : "attachment");
        disposition.SetHttpFileName(job.FileName);
        context.Response.GetTypedHeaders().ContentDisposition = disposition;
        await Results.Stream(download.Stream, "application/pdf", enableRangeProcessing: false).ExecuteAsync(context);
        await context.Response.CompleteAsync();
        context.RequestAborted.ThrowIfCancellationRequested();
        store.CompleteDownload(id);
    }

    private static async Task<string> OwnerAsync(HttpContext context)
    {
        var authentication = await context.AuthenticateAsync(AuthenticationNames.ApiKey);
        return authentication.Succeeded ? "key:" + authentication.Principal!.FindFirstValue(ClaimTypes.NameIdentifier) : "anonymous:";
    }
    private static Task NotFound(HttpContext context) => Problems.WriteAsync(context, 404, "Job not found");
    private static Task TooLarge(HttpContext context) => Problems.WriteAsync(context, 413, "Request too large", "The complete JSON body exceeds the application limit.");
    private static Task Full(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        return Problems.WriteAsync(context, 429, "Job capacity exhausted", "Retry after the indicated delay or delete stored results.");
    }
}

// Submission bodies have a separate bound so batch uploads cannot consume sync admission permits.
public sealed class JobSubmissionGate(IOptions<RenderSettings> settings) : IDisposable
{
    private readonly AdmissionGate _gate = new(settings);
    public IDisposable? TryEnter() => _gate.TryEnter();
    public void Dispose() => _gate.Dispose();
}
