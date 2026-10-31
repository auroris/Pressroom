using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Pressroom.Contracts;

namespace Pressroom.Client;

public sealed partial class PressroomClient
{
    public async Task<RenderJob> SubmitAsync(RenderRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var errors = RequestValidator.Validate(request, maxTimeoutMs: 300_000);
        if (errors.Count != 0) throw new ArgumentException("Invalid render request: " + string.Join("; ", errors.Keys), nameof(request));
        if (idempotencyKey is not null && !JobValidation.IsIdempotencyKey(idempotencyKey))
            throw new ArgumentException("Use 1–128 visible ASCII characters for an idempotency key.", nameof(idempotencyKey));
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/jobs") { Content = JsonContent.Create(request, options: JsonDefaults.Options) };
        if (idempotencyKey is not null) message.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted) throw new HttpRequestException("Unexpected job submission response.");
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RenderJob> GetJobAsync(string id, CancellationToken cancellationToken = default)
        => (await PollAsync(id, cancellationToken).ConfigureAwait(false)).Job;

    public async Task<RenderJobPage> ListJobsAsync(JobStatus? status = null, int limit = 50, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (status is { } value && !Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(status));
        if (after is not null) ValidateId(after);
        var path = $"v1/jobs?limit={limit}" + (status is null ? "" : "&status=" + status.ToString()!.ToLowerInvariant())
            + (after is null ? "" : "&after=" + after);
        using var response = await httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RenderJobPage>(await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false), JsonDefaults.Options)
            ?? throw new HttpRequestException("Missing job list.");
    }

    public async Task<RenderJob> WaitAsync(string id, CancellationToken cancellationToken = default, TimeSpan? maxWait = null)
    {
        ValidateId(id);
        if (maxWait is { } maximum && (maximum <= TimeSpan.Zero || maximum.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(maxWait));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (maxWait is { } duration) timeout.CancelAfter(duration);
        try
        {
            while (true)
            {
                var (job, retryAfter) = await PollAsync(id, timeout.Token).ConfigureAwait(false);
                if (job.IsTerminal) return job;
                var delay = TimeSpan.FromSeconds(Math.Clamp((retryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds, 0.1, 10));
                await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("The job did not finish within maxWait. The server job remains available for reconciliation."); }
    }

    public async Task<RenderedPdf> DownloadAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var result = await DownloadStreamAsync(id, cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        await result.PdfStream.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new(bytes.ToArray(), result.FileName, result.PageCount, result.DurationMs, result.Disposition);
    }

    public async Task<PdfRenderResult> DownloadStreamAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var response = await httpClient.GetAsync($"v1/jobs/{id}/result", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode) throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType != "application/pdf") throw new HttpRequestException("The job result is not a PDF.");
            var disposition = response.Content.Headers.ContentDisposition;
            var pages = ReadNumber(response, "X-Render-Pages");
            return new(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                pages is >= 0 and <= int.MaxValue ? (int?)pages : null,
                RequestValidator.SanitizeFileName(disposition?.FileNameStar ?? disposition?.FileName?.Trim('"')),
                ReadNumber(response, "X-Render-Duration-Ms"), response,
                string.Equals(disposition?.DispositionType, "inline", StringComparison.OrdinalIgnoreCase) ? PdfDisposition.Inline : PdfDisposition.Attachment);
        }
        catch { response.Dispose(); throw; }
    }

    public async Task CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        using var response = await httpClient.DeleteAsync($"v1/jobs/{id}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bounded submission, polling, download and deletion. Yields completion order; never retries a failed operation.</summary>
    public async IAsyncEnumerable<RenderManyResult> RenderManyAsync(IEnumerable<RenderRequest> requests, int maxInFlight,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (maxInFlight is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxInFlight));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var source = requests.GetEnumerator();
        var pending = new List<Task<RenderManyResult>>();
        var exhausted = false;
        try
        {
            while (!exhausted || pending.Count > 0)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                while (!exhausted && pending.Count < maxInFlight)
                {
                    if (!source.MoveNext()) { exhausted = true; break; }
                    pending.Add(RenderOneAsync(source.Current, cancellation.Token));
                }
                if (pending.Count == 0) break;
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                yield return await completed.ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<RenderManyResult> RenderOneAsync(RenderRequest request, CancellationToken token)
    {
        try
        {
            if (request.Printer is not null) throw new ArgumentException("RenderManyAsync returns PDFs. Use SubmitAsync and WaitAsync for print jobs.");
            var job = await SubmitAsync(request, cancellationToken: token).ConfigureAwait(false);
            job = await WaitAsync(job.Id, token).ConfigureAwait(false);
            if (job.Status != JobStatus.Succeeded)
            {
                var error = job.Error ?? new JobError(job.Status == JobStatus.Expired ? 410 : 409, "Job did not succeed", Code: job.Status.ToString().ToLowerInvariant(), Retryable: false);
                return new(request, null, new((HttpStatusCode)error.Status, error.Title, error.Detail, error.Errors, null) { Code = error.Code, Retryable = error.Retryable });
            }
            var pdf = await DownloadAsync(job.Id, token).ConfigureAwait(false);
            await CancelAsync(job.Id, token).ConfigureAwait(false);
            return new(request, pdf, null);
        }
        catch (PressroomException exception) { return new(request, null, exception); }
        catch (ArgumentException exception) { return new(request, null, new(HttpStatusCode.BadRequest, "Invalid batch request", exception.Message, null, null) { Code = "invalid_request", Retryable = false }); }
        catch (HttpRequestException) { return new(request, null, new(HttpStatusCode.BadGateway, "HTTP transport failed", "Reconcile submitted jobs before retrying.", null, null) { Code = "transport" }); }
    }

    private async Task<(RenderJob Job, TimeSpan? RetryAfter)> PollAsync(string id, CancellationToken token)
    {
        ValidateId(id);
        using var response = await httpClient.GetAsync($"v1/jobs/{id}", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ReadFailureAsync(response, token).ConfigureAwait(false);
        var job = await ReadJobAsync(response, token).ConfigureAwait(false);
        if (job.Id != id) throw new HttpRequestException("The server returned a different job identifier.");
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is { } date) delay = date - DateTimeOffset.UtcNow;
        return (job, delay);
    }

    private static async Task<RenderJob> ReadJobAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new HttpRequestException("The job response is not JSON.");
        var job = JsonSerializer.Deserialize<RenderJob>(await ReadBoundedContentAsync(response.Content, token).ConfigureAwait(false), JsonDefaults.Options);
        if (job is null || !JobValidation.IsId(job.Id)) throw new HttpRequestException("Invalid job document.");
        return job;
    }
    private static void ValidateId(string id)
    {
        if (!JobValidation.IsId(id)) throw new ArgumentException("Use a 32-character lowercase hexadecimal job identifier.", nameof(id));
    }
}

/// <summary>Exactly one of Pdf and Error is populated for each completed batch item.</summary>
public sealed record RenderManyResult(RenderRequest Request, RenderedPdf? Pdf, PressroomException? Error);
