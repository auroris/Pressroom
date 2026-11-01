# AurorisNet.Pressroom.Client

HTTP client for the Pressroom HTML-to-PDF service, for .NET 8 and .NET 10.
Includes both the `Pressroom.Client` and `Pressroom.Contracts` assemblies and uses
the framework's HTTP APIs to call a running server. No browser dependencies.

For versions published to NuGet.org:

```sh
dotnet add package AurorisNet.Pressroom.Client --version <version>
```

For builds distributed through GitHub, download and extract `nuget-packages` from
the **Packages** workflow, or download `AurorisNet.Pressroom.Client.<version>.nupkg`
from a release into a local feed directory. Add `--source ./packages` to the
command above. A separate Contracts package is not needed. When migrating from
the old packages, remove the `Pressroom.Client` and `Pressroom.Contracts` package
references; the C# namespaces stay the same.

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
