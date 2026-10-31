# .NET client

`Pressroom.Client` is a small HTTP client for .NET 10 applications. It depends only
on `Pressroom.Contracts` and the base class library, so referencing it adds no
Playwright or Chromium dependency to your application. Any HTTP client in any
language can call the [API](api.md) directly; this library saves you the request
plumbing, validation, and error mapping.

## Referencing the projects

Both projects live under `src/` and target `net10.0`. Add project references, or
build and reference the assemblies:

```xml
<ItemGroup>
  <ProjectReference Include="..\Pressroom\src\Pressroom.Client\Pressroom.Client.csproj" />
</ItemGroup>
```

`Pressroom.Contracts` holds the request and option records, `IPdfRenderer`, the
validator, and the JSON settings, and can be referenced on its own by code that
only builds requests.

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
    Disposition = PdfDisposition.Inline,
    Options = new RenderOptions
    {
        PageSize = PageSize.Named("A4"),
        Margins = new PageMargins { Top = "15mm", Bottom = "15mm" }
    }
}, cancellationToken);

// pdf.Bytes, pdf.FileName, pdf.PageCount, pdf.DurationMs, pdf.Disposition
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

A web application that proxies Pressroom's output must set its own response
headers. Copying the bytes does not carry `Content-Disposition` through, so use the
framework's typed file APIs and the disposition and file name the client returns:

```csharp
// ASP.NET Core MVC
Response.Headers.ContentDisposition = pdf.Disposition == PdfDisposition.Inline
    ? $"inline; filename=\"{pdf.FileName}\""
    : $"attachment; filename=\"{pdf.FileName}\"";
return File(pdf.Bytes, "application/pdf");
```

`FileName` is already sanitized by the client.

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

The client never retries a print job. See [Printing](printing.md) for what
`submitted` does and does not mean.

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
(see [Getting started](getting-started.md#4-run-it)) and keep a separate key or
anonymous mode for development.

Applications that must render without any server can reference
`Pressroom.Rendering` directly and host the browser themselves. That is a separate,
explicit choice with its own browser setup; it is not an automatic fallback in the
HTTP client.
