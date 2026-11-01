# Pressroom.Client

HTTP client for the Pressroom HTML-to-PDF service, for .NET 8 and .NET 10.
Uses `Pressroom.Contracts` and the framework's HTTP APIs to call a running server.

Download and extract `nuget-packages` from the GitHub **Packages** workflow, or
download both Client and Contracts `.nupkg` files from a GitHub release. Keep them
together in a local feed directory; these builds are not published to NuGet.org.

```sh
dotnet add package Pressroom.Client --version <version> --source ./packages
```

```csharp
using Pressroom.Client;
using Pressroom.Contracts;

using var http = new HttpClient { BaseAddress = new Uri("https://pressroom.example/") };
http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
var pressroom = new PressroomClient(http);
var pdf = await pressroom.RenderAsync(new RenderRequest
{
    Html = "<!doctype html><html><body><h1>Hello</h1></body></html>",
    FileName = "hello.pdf"
});
await File.WriteAllBytesAsync(pdf.FileName, pdf.Bytes);
```

Reuse a caller-owned `HttpClient` or register a typed client with `AddHttpClient`.
`RenderStreamAsync` returns a disposable `PdfRenderResult` for streaming; `PrintAsync`
submits to a configured Windows or Linux printer. Errors include HTTP status, problem
details, and `Retry-After`. Retry policy belongs to the caller.

With jobs enabled on the server, use `SubmitAsync` to create a durable job,
`GetJobAsync` to read its status, and `WaitAsync` to wait for completion.
`DownloadAsync` and `DownloadStreamAsync` collect results; `CancelAsync` cancels work
or removes a result. `RenderManyAsync` bounds batch submission and yields PDFs or
typed failures.

See the [client guide](https://github.com/auroris/Pressroom/blob/main/docs/development/client.md)
for configuration, validation, streaming, and printing.
