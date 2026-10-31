using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pressroom.Contracts;

namespace Pressroom.Client;

/// <summary>Uses a caller-owned HttpClient, whose BaseAddress ends with a slash.</summary>
public sealed partial class PressroomClient(HttpClient httpClient) : IPdfRenderer
{
    public async Task<RenderedPdf> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        await using var result = await RenderStreamAsync(request, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await result.PdfStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new(buffer.ToArray(), result.FileName, result.PageCount, result.DurationMs);
    }

    public Task<PdfRenderResult> RenderAsync(string html, RenderOptions options, CancellationToken cancellationToken = default)
        => RenderStreamAsync(new RenderRequest { Html = html, Options = options }, cancellationToken);

    Task<PdfRenderResult> IPdfRenderer.RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        => RenderStreamAsync(request, cancellationToken);

    /// <summary>
    /// Returns an owned response stream without buffering the PDF. The caller supplies cancellation
    /// while copying the stream and disposes the result. Retry and HTML fallback policy stay with the caller.
    /// </summary>
    public async Task<PdfRenderResult> RenderStreamAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Printer is not null)
            throw new ArgumentException("Use PrintAsync for a request with a printer.", nameof(request));
        // The actual server may configure a maximum above the standard 60 seconds.
        var errors = RequestValidator.Validate(request, maxTimeoutMs: 300_000);
        if (errors.Count != 0)
            throw new ArgumentException("Invalid render request: " + string.Join("; ", errors.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}")), nameof(request));

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/render")
        {
            Content = JsonContent.Create(request, options: JsonDefaults.Options)
        };
        message.Headers.Accept.ParseAdd("application/pdf");
        var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType != "application/pdf")
                throw new HttpRequestException("Pressroom returned a successful response that is not a PDF.");
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? request.FileName;
            var pages = ReadNumber(response, "X-Render-Pages");
            return new PdfRenderResult(stream, pages is >= 0 and <= int.MaxValue ? (int?)pages : null,
                RequestValidator.SanitizeFileName(fileName), ReadNumber(response, "X-Render-Duration-Ms"), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Submits to a configured printer using the HttpClient's API key. Never retries automatically.</summary>
    public async Task<PrintJobResult> PrintAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Printer is null)
            throw new ArgumentException("A configured printer identifier is required.", nameof(request));
        var errors = RequestValidator.Validate(request, maxTimeoutMs: 300_000);
        if (errors.Count != 0)
            throw new ArgumentException("Invalid print request: " + string.Join("; ", errors.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}")), nameof(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/render")
        {
            Content = JsonContent.Create(request, options: JsonDefaults.Options)
        };
        message.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new HttpRequestException("Pressroom returned an unexpected print response.");
        var result = JsonSerializer.Deserialize<PrintJobResult>(await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false), JsonDefaults.Options);
        if (result is null || string.IsNullOrWhiteSpace(result.JobId) || result.Status != "submitted"
            || !string.Equals(result.Printer, request.Printer, StringComparison.OrdinalIgnoreCase) || result.PageCount < 1)
            throw new HttpRequestException("Pressroom returned an invalid print receipt.");
        return result;
    }

    private static async Task<PressroomException> ReadFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? title = null, detail = null, code = null;
        bool? retryable = null;
        Dictionary<string, string[]>? validationErrors = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            // Native IIS errors need not be JSON. Do not replace the status with a JSON parsing exception.
            try
            {
                using var problem = JsonDocument.Parse(await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false));
                if (problem.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                if (problem.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) detail = d.GetString();
                if (problem.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (problem.RootElement.TryGetProperty("retryable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) retryable = r.GetBoolean();
                if (problem.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                    validationErrors = e.Deserialize<Dictionary<string, string[]>>();
            }
            catch (JsonException) { }
            catch (HttpRequestException) { }
        }
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = TimeSpan.FromTicks(Math.Max(0, (date - DateTimeOffset.UtcNow).Ticks));
        return new PressroomException(response.StatusCode, title ?? "Pressroom rejected the render request.", detail, validationErrors, retryAfter) { Code = code, Retryable = retryable };
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("v1/health", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        if (content.Headers.ContentLength > limit) throw new HttpRequestException("Pressroom JSON response exceeds 1 MiB.");
        using var buffer = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - buffer.Length)), cancellationToken).ConfigureAwait(false)) != 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > limit) throw new HttpRequestException("Pressroom JSON response exceeds 1 MiB.");
        }
        return buffer.ToArray();
    }

    private static long? ReadNumber(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed record RenderedPdf(byte[] Bytes, string FileName, int? PageCount, long? DurationMs);

public sealed class PressroomException(HttpStatusCode statusCode, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors, TimeSpan? retryAfter)
    : Exception(detail is null ? title : $"{title} {detail}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Detail { get; } = detail;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string? Code { get; init; }
    public bool? Retryable { get; init; }
}
