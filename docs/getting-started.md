# Getting started

This guide takes you from a release ZIP to your first PDF in about ten minutes.
It uses a local console run so you can see everything happen; the
[Deployment guide](deployment.md) covers the production IIS setup.

## 1. Download and extract a release

Download `Pressroom-<version>-win-x64-framework-dependent.zip` and its `.zip.sha256`
checksum from the [GitHub releases page](https://github.com/auroris/Pressroom/releases).
Verify the checksum, then extract the **entire** archive into an empty folder such as
`C:\Sites\Pressroom`:

```powershell
(Get-FileHash .\Pressroom-1.0.1-win-x64-framework-dependent.zip -Algorithm SHA256).Hash
Expand-Archive .\Pressroom-1.0.1-win-x64-framework-dependent.zip -DestinationPath C:\Sites\Pressroom
```

The archive is the complete application: the server, its private Playwright driver
and Node runtime under the hidden `.playwright` folder, the matching Chromium
headless shell under `browsers`, the Noto Sans fonts, third-party notices, and a
`release-manifest.json` listing every version and file hash. Nothing downloads at
run time, so do not prune the folder.

The only machine prerequisite is the **.NET 10 runtime**. For IIS, install the
[.NET 10 Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0),
which includes the runtime and the IIS module. No Node, npm, Chrome, Edge, Visual
C++ redistributable, or font installation is needed.

## 2. Create the settings file

Pressroom reads its packaged `appsettings.json` defaults and then one external JSON
file that holds everything specific to your server, including API keys. The
default location is `C:\ProgramData\Pressroom\pressroom.settings.json`.

Create the state directories, then copy
[`samples/pressroom.settings.example.json`](../samples/pressroom.settings.example.json)
into place and add a key:

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\Pressroom\temp, C:\ProgramData\Pressroom\logs | Out-Null
Copy-Item .\samples\pressroom.settings.example.json C:\ProgramData\Pressroom\pressroom.settings.json
```

```json
{
  "Paths": { "StateRoot": "C:\\ProgramData\\Pressroom" },
  "Auth": {
    "Mode": "ApiKey",
    "ApiKeys": {
      "reporting-app": { "Secret": "REPLACE_WITH_A_RANDOMLY_GENERATED_SECRET" }
    }
  }
}
```

Generate each secret from at least 32 random bytes, for example:

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

Pressroom ships without a key and refuses to start in API-key mode until one is
configured. The key identifier (`reporting-app`) is not secret; it appears in logs so
you can tell callers apart. The [Configuration guide](configuration.md) describes
every setting, including anonymous mode for trusted networks and JavaScript
permission for chart-producing applications.

## 3. Check the package

The server has an offline self-test that launches the bundled Chromium with the
sandbox enabled, renders a warm-up document, and exits. It listens on no port and
installs nothing:

```powershell
& 'C:\Sites\Pressroom\Pressroom.Server.exe' --check --config 'C:\ProgramData\Pressroom\pressroom.settings.json'
```

A healthy package prints one JSON line with `"status": "ready"` and the Chromium and
Playwright versions. A failure prints a one-line reason to standard error and exits
with code 1.

## 4. Run it

For a local run, start the executable directly. It hosts itself on Kestrel:

```powershell
& 'C:\Sites\Pressroom\Pressroom.Server.exe' --config 'C:\ProgramData\Pressroom\pressroom.settings.json' --urls http://localhost:5177
```

Startup logs show the browser warm-up and the configured concurrency. The
`/v1/health` endpoint answers `200` once the renderer is ready:

```powershell
Invoke-RestMethod http://localhost:5177/v1/health
```

For production, host the same folder as an IIS application with a dedicated
application pool. The [Deployment guide](deployment.md) has the IIS settings table,
directory permissions, and the update and rollback procedure.

## 5. Render your first PDF

The repository includes a ready-to-send request,
[`samples/orders-bilingual.request.json`](../samples/orders-bilingual.request.json),
whose document exercises two Letter pages, English and French text, all four
packaged font styles, inline vector graphics, and a page break.

With curl:

```sh
curl -sS -X POST http://localhost:5177/v1/render \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: REPLACE_WITH_YOUR_SECRET" \
  --data-binary @samples/orders-bilingual.request.json \
  -D - -o orders.pdf
```

With PowerShell:

```powershell
Invoke-WebRequest -Method Post -Uri http://localhost:5177/v1/render `
  -ContentType 'application/json' -Headers @{ 'X-Api-Key' = 'REPLACE_WITH_YOUR_SECRET' } `
  -InFile .\samples\orders-bilingual.request.json -OutFile .\orders.pdf
```

The response is `application/pdf` with a `Content-Disposition` header carrying the
sanitized file name, plus `X-Render-Pages` and `X-Render-Duration-Ms`. Open
`orders.pdf` and you should see the two-page bilingual document with
"Page 1 / 2" and "Page 2 / 2" footers at the bottom right and no running header.

A minimal request is just the HTML:

```json
{
  "html": "<!doctype html><html><head><title>Hello</title></head><body><h1>Hello</h1></body></html>",
  "fileName": "hello.pdf",
  "disposition": "inline"
}
```

## 6. Call it from .NET

Reference the `Pressroom.Client` project (it pulls in `Pressroom.Contracts`), give it
an `HttpClient` whose base address ends with a slash, and render:

```csharp
using Pressroom.Client;
using Pressroom.Contracts;

var http = new HttpClient { BaseAddress = new Uri("https://pressroom.example/") };
http.DefaultRequestHeaders.Add("X-Api-Key", configuration["Pressroom:ApiKey"]!);
var pressroom = new PressroomClient(http);

var pdf = await pressroom.RenderAsync(new RenderRequest
{
    Html = completeHtml,
    FileName = "orders.pdf",
    Disposition = PdfDisposition.Inline
}, cancellationToken);

return File(pdf.Bytes, "application/pdf");
```

The client is HTTP-only and adds no browser dependency to your application. See the
[.NET client guide](client.md) for streaming, printing, error handling, and
fallback strategies.

## Where next

| You want to… | Read |
|---|---|
| Write HTML that paginates well and uses the packaged fonts | [Authoring documents](documents.md) |
| See every request option, response header, and status code | [API reference](api.md) |
| Configure keys, JavaScript permission, limits, and paths | [Configuration](configuration.md) |
| Send output to a Windows printer | [Printing](printing.md) |
| Put it on an IIS server and keep it updated | [Deployment](deployment.md) |
| Understand what the sandbox does and does not guarantee | [Security model](security.md) |
