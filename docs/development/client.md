# .NET client

`Pressroom.Client` is a small HTTP client for .NET 8 and .NET 10 applications. It depends only
on `Pressroom.Contracts` and the base class library, so referencing it adds no
Playwright or Chromium dependency to your application. Any HTTP client in any
language can call the [API](api.md) directly; this library saves you the request
plumbing, validation, and error mapping.

## Installing the package

Download and extract the `nuget-packages` artifact from a successful
[Packages workflow run](https://github.com/auroris/Pressroom/actions/workflows/release.yml),
or download both `Pressroom.Client` and `Pressroom.Contracts` `.nupkg` files from a
GitHub release. Keep them together in a local feed directory, such as `./packages`,
and install the matching version:

```sh
dotnet add package Pressroom.Client --version <version> --source ./packages
```

`Pressroom.Contracts` holds the request and option records, `IPdfRenderer`, the
validator, and the JSON settings, and can be referenced on its own by code that
only builds requests. The workflows distribute these packages only through GitHub;
they do not publish them to NuGet.org or another NuGet feed. Symbol packages and
SHA-256 files are included in the download too.

## Creating a client

`PressroomClient` wraps an `HttpClient` you own. The base address must end with a
slash, and the API key goes in a default header:

```csharp
using Pressroom.Client;
using Pressroom.Contracts;

builder.Services.AddHttpClient<PressroomClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Pressroom:Url"]!);   // https://pressroom.example/
    http.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["Pressroom:ApiKey"]!);
    http.Timeout = TimeSpan.FromSeconds(65);                               // above the server's maximum deadline
});
```

Omit the header when the server runs in anonymous mode. Store the URL and key in
your application's configuration, not in code.

## Rendering a PDF

The buffered overload returns the whole PDF in memory together with the response
metadata:

```csharp
RenderedPdf pdf = await pressroom.RenderAsync(new RenderRequest
{
    Html = completeHtml,
    FileName = "orders-preview.pdf",
    Options = new RenderOptions
    {
        PageSize = PageSize.Named("A4"),
        Margins = new PageMargins { Top = "15mm", Bottom = "15mm" }
    }
}, cancellationToken);

// pdf.Bytes, pdf.FileName, pdf.PageCount, pdf.DurationMs
```

For large documents, stream instead. `RenderStreamAsync` returns a
`PdfRenderResult` that owns the response; copy its `PdfStream` and dispose it:

```csharp
await using PdfRenderResult result = await pressroom.RenderStreamAsync(request, cancellationToken);
await result.PdfStream.CopyToAsync(destination, cancellationToken);
```

`PressroomClient` also implements `IPdfRenderer`, whose
`RenderAsync(string html, RenderOptions options, CancellationToken)` is the
streaming form for code that should not depend on the HTTP client type.

The client validates the request locally with the same rules as the server and
throws `ArgumentException` before sending anything invalid, so a typo in a unit or
an impossible margin is caught in your tests rather than on the wire.

## Returning the PDF to a browser

A web application chooses how to deliver the PDF using its own response headers.
Pressroom returns bytes and a sanitized filename; it has no browser delivery
setting. For a download, use the framework's file API:

```csharp
// ASP.NET Core MVC
return File(pdf.Bytes, "application/pdf", pdf.FileName);
```

For a preview, set a typed header in the application instead:

```csharp
// ASP.NET Core MVC
var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
disposition.SetHttpFileName(pdf.FileName);
Response.GetTypedHeaders().ContentDisposition = disposition;
return File(pdf.Bytes, "application/pdf");
```

The typed header handles Unicode filenames. Browser settings and PDF support
determine whether the preview is displayed. If upgrading an existing caller,
remove its Pressroom `Disposition` assignments and use an application-owned
choice between these responses; see [API migration notes](api.md#upgrading-callers-that-use-disposition).

## Printing

`PrintAsync` sends the same request with a `Printer` identifier and returns the
submission receipt. The PDF-returning methods reject requests that carry a
printer, and `PrintAsync` rejects requests without one:

```csharp
PrintJobResult job = await pressroom.PrintAsync(new RenderRequest
{
    Html = completeHtml,
    Printer = "shipping"
}, cancellationToken);

// job.JobId, job.Printer, job.Status == "submitted", job.PageCount
```

The client never retries a print job. See [Printing](../printing.md) for what
`submitted` does and does not mean.

## Batch rendering

Enable jobs on the server first. `SubmitAsync` accepts the same RenderRequest and
an optional 1–128 character visible-ASCII idempotency key. The same key under the
same caller returns the existing job for its retention window, even if a later
request's body differs; choose a new key for new work.

```csharp
RenderJob job = await pressroom.SubmitAsync(request, "statement-10231", cancellationToken);
RenderJob completed = await pressroom.WaitAsync(job.Id, cancellationToken, TimeSpan.FromMinutes(5));
if (completed.Status == JobStatus.Succeeded && completed.Printer is null)
{
    RenderedPdf pdf = await pressroom.DownloadAsync(job.Id, cancellationToken);
    await pressroom.CancelAsync(job.Id, cancellationToken); // delete the stored result early
}
```

`WaitAsync` polls at the interval the server suggests in `Retry-After` (one second
if absent, never more than ten), returns as soon as the job reaches any terminal
state, and throws `TimeoutException` when `maxWait` runs out. Cancelling the wait
stops the polling, not the job on the server. `GetJobAsync` reads a job's current
state, and `ListJobsAsync` pages through the caller's jobs by status so an
application can reconcile after a crash. `DownloadStreamAsync` owns its HTTP
response in the same way as `RenderStreamAsync`; dispose it when done.

For PDF batches, bounded submission, polling, download, and deletion are built in:

```csharp
await foreach (RenderManyResult item in pressroom.RenderManyAsync(requests, 8, cancellationToken))
{
    if (item.Error is { } error)
    {
        // Inspect StatusCode, Code and Retryable. Choose your own retry policy.
        continue;
    }
    await File.WriteAllBytesAsync(item.Pdf!.FileName, item.Pdf.Bytes, cancellationToken);
}
```

Results arrive in completion order and carry the original request. Exactly one
of `Pdf` and `Error` is populated. The client never retries a failed submission or
render. An HTTP transport failure is reported as a `transport` error whose
retryability is unknown, because the server may have accepted the job; list your
jobs before resubmitting. Disposing the enumeration early, or cancelling, stops the
local work and leaves any acknowledged jobs on the server. Use `SubmitAsync` and
`WaitAsync` for printer jobs, whose result is a receipt rather than a PDF.

## Errors, retries, and fallback

Server failures throw `PressroomException` with the HTTP status, the problem
`Detail`, any validation `Errors`, and the parsed `RetryAfter` delay:

```csharp
try
{
    return await pressroom.RenderAsync(request, cancellationToken);
}
catch (PressroomException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
{
    // Capacity or restart: wait ex.RetryAfter ?? TimeSpan.FromSeconds(1) and retry once, or fall back.
}
catch (PressroomException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
{
    // The document's readiness condition failed. Retrying the same document will not help.
}
```

The client does not retry automatically; the policy belongs to you because only you
know whether a late PDF is still useful. Sensible defaults:

- Retry 429 and 503 once after `RetryAfter`, with a bounded total wait.
- Do not retry 400, 401, 413, 422, or 504.
- Treat a connection failure or your own `HttpClient` timeout as unavailable.

Many applications keep an **HTML fallback**: when no Pressroom URL is configured,
or a render fails, deliver the same HTML to the browser and let the user print it.
A health probe before every render is unnecessary; `IsReadyAsync` exists for
dashboards and startup checks, but the render call itself is the most reliable
signal.

## Local development

Pressroom runs happily on `localhost`. Point `Pressroom:Url` at a local console run
(see [Getting started](../getting-started.md#4-run-it)) and keep a separate key or
anonymous mode for development.

Applications that must render without any server can reference
`Pressroom.Rendering` directly and host the browser themselves. That is a separate,
explicit choice with its own browser setup; it is not an automatic fallback in the
HTTP client.
