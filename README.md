# Pressroom

Pressroom is a standalone .NET 10 service that turns a complete HTML document into
a PDF. It publishes as one IIS application folder, including its matching private
Playwright driver, Node runtime, Chromium headless shell, and Noto Sans fonts.

Applications submit self-contained HTML over HTTP and receive PDF bytes. The
optional `Pressroom.Client` library provides a .NET client without adding a browser
dependency to the calling application.
API-key callers can also submit documents to allowlisted Windows printers and
receive a JSON submission receipt. Printer output requires a configured SumatraPDF
executable on the server.

## Create a GitHub release

The [Release workflow](.github/workflows/release.yml) runs manually. Once it is on
the repository's default branch:

1. Open **Actions → Release → Run workflow** and select the branch to release.
2. Enter an unused version without a `v` prefix, such as `1.2.3` or `1.2.3-rc.1`.
3. Optionally select **draft** or **prerelease**, then click **Run workflow**.
   Versions with a suffix such as `-rc.1` are automatically marked as prereleases.

The workflow restores locked dependencies, builds and tests the solution on
Windows, and publishes the framework-dependent `IisFolder` profile. The publish
must pass the real Chromium PDF warm-up before the workflow creates a release
tagged `v<version>` at the exact commit selected when the run started. Existing tags
and releases are never overwritten. If an interrupted upload leaves a draft or
tag, inspect it before retrying with that version.

Each release contains:

- `Pressroom-<version>-win-x64-framework-dependent.zip`: requires .NET 10 on the server.
- A `.zip.sha256` checksum file for the ZIP.

The ZIP contains the complete application, private Playwright driver, Chromium,
fonts, and release manifest. Extract the entire archive; the `.playwright` folder
is required. The .NET 10 Hosting Bundle is required for IIS.
Configure API keys or anonymous access in the external JSON file described below.
Target-server acceptance remains a separate deployment check.

The workflow caches NuGet packages using the committed dependency locks and creates
the release directly from the verified files on the build runner, avoiding a
second runner and an artifact download. It uses GitHub's built-in token; no personal
token or application secrets are required. The job has repository write permission
to create the tag and release; checkout does not persist credentials. The verified
ZIP and checksum are also retained as workflow artifacts for 14 days. See GitHub's
[manual workflow instructions](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)
for the required repository access and default-branch setup.

## Publish from Visual Studio

Open `Pressroom.slnx` in a Visual Studio installation that supports .NET 10. Select
**Pressroom.Server → Publish**, choose **IisFolder**, and click **Publish**. The
default output is `artifacts\publish\IisFolder` beneath this solution. The profile
uses the server's .NET 10 installation. **IisFolderSelfContained** also includes the
.NET runtime, if you prefer the larger package.

The publish operation automatically:

1. Builds the Windows x64 application using the pinned package versions.
2. Runs the package's private Playwright driver to install its matching Chromium
   headless shell into the publish payload. No separate Node/npm or PowerShell 7
   installation is needed.
3. Adds the complete browser tree, installer-selected companion files, fonts,
   managed package notices, and private driver to the publish file list.
4. Makes an isolated local copy of those files and runs `Pressroom.Server.exe
   --check`. That launches the actual renderer with its sandbox enabled and normal
   dependency validation, performs a PDF warm-up, then stops it. It uses temporary
   local state and does not read the machine's Pressroom secrets.
5. Fails Publish if that check fails. On success, removes the build machine's
   `DEPENDENCIES_VALIDATED` cache markers and writes `release-manifest.json` with
   versions, font inventory, and SHA-256 file hashes before the payload transfers.

The first publish needs internet access to restore packages and acquire the
matching browser. Later publishes reuse the build cache. The deployed application
never downloads browsers, restores packages, or installs Windows features.
`build/Publish-Pressroom.ps1` is called by MSBuild automatically; there is no
separate deployment script to run. Ordinary **Build** and unit tests do not
download browsers. The approach uses the Web SDK's
[publish file list](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/visual-studio-publish-profiles?view=aspnetcore-10.0#include-files)
rather than copying extra files after deployment has finished.

### Your usual IIS / Web Deploy profile

You can create or import your usual Visual Studio **IIS / Web Deploy** profile for
`Pressroom.Server`. Select **Release**, **net10.0**, **win-x64**, and framework
dependent deployment, with single-file, trimming, and AOT disabled. The project's
publish targets also add the browser assets to that profile's payload. Keep
`RestoreLockedMode=true` in the profile to enforce the committed dependency locks.
Credentials belong in Visual Studio's user settings, not in a committed `.pubxml`.

`EnableMsDeployAppOffline` defaults to `true`, allowing ANCM to shut the application
down before Web Deploy replaces locked files. The app receives a bounded shutdown
window to close its browser and driver. For a Folder profile pointed directly at
an IIS directory or share, stop the dedicated pool before replacing its files and
start it afterward; a local staging folder is the default. Keep a complete previous
publish folder for rollback, including its browser and driver. See Microsoft's
[IIS deployment guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0)
for the host's shutdown/copy behavior.

The build check runs on your publishing machine. The first Windows/IIS deployment
still needs the acceptance checks in `docs/windows-acceptance.md`, particularly the
application-pool identity, configured access mode, and operation without Media
Foundation. The clean-Windows gate has not yet been run.

## One-time IIS setup

Use a Windows Server x64 version supported by both .NET 10 and the pinned
Playwright release. Provision **IIS and the .NET 10 Hosting Bundle**. A .NET runtime
or SDK alone does not supply the ASP.NET Core IIS module; the Hosting Bundle is
required even for the self-contained profile. A framework-dependent `net10.0`
application also needs .NET 10, rather than only a later major runtime.
([Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0),
[runtime selection](https://learn.microsoft.com/en-us/dotnet/core/versions/selection),
[Playwright supported systems](https://playwright.dev/dotnet/docs/intro).)

Configure the application in IIS Manager:

| Setting | Value |
|---|---|
| Application pool | Dedicated to Pressroom; **No Managed Code** |
| Enable 32-Bit Applications | **False** |
| Maximum Worker Processes | **1** |
| Start Mode | **AlwaysRunning** |
| Load User Profile | **True** |
| Idle Time-out | **0** for an always-warm renderer |
| Application Preload | **True**, with Application Initialization installed |
| Warm-up URL | `/v1/health` |
| Authentication | Enable **Anonymous Authentication**; disable **Windows Authentication** |
| Site binding | HTTPS with your existing certificate/binding |

IIS Anonymous Authentication lets requests reach Pressroom. The application then
enforces its configured API-key or anonymous access mode. Pressroom does not use
Windows authentication or domain-group restrictions.

Keep runtime data outside the replaceable site folder:

| Example path | Application-pool permissions |
|---|---|
| `C:\Sites\Pressroom` | Read and execute |
| `C:\ProgramData\Pressroom` | Read/traverse |
| `C:\ProgramData\Pressroom\pressroom.settings.json` | Read; administrators manage it |
| `C:\ProgramData\Pressroom\temp` | Modify |
| `C:\ProgramData\Pressroom\logs` | Modify |

Create the state root, `temp`, and `logs` directories before the first start and
grant these rights to the dedicated pool identity, such as `IIS AppPool\Pressroom`.
Restrict the configuration file to that identity and administrators. Overlapped
IIS recycling can temporarily run two application instances; each has its own
browser and temporary directory, so budget memory for the overlap.

### Application configuration and access

Copy [`samples/pressroom.settings.example.json`](samples/pressroom.settings.example.json)
to `C:\ProgramData\Pressroom\pressroom.settings.json` and adjust it there. The
application reads packaged `appsettings.json` defaults, then the external JSON
file. Settings are read at startup; restart the application after changes.

To select a different external file, set `ConfigurationFile` in `appsettings.json`:

```json
{
  "ConfigurationFile": "D:\\PressroomConfig\\pressroom.settings.json"
}
```

A blank or omitted `ConfigurationFile` uses the operating system's common
application-data folder, normally `C:\ProgramData\Pressroom\pressroom.settings.json`
on Windows. Relative paths resolve from the application directory. An explicitly
selected file must exist. For a local run or package check, `--config <path>` can
select the file. Application settings do not use environment-variable overrides.

The default `Auth:Mode` is `ApiKey`. Configure one or more valid keys in the external
file under `Auth:ApiKeys`, mapping a nonsecret identifier to each key's settings:

```json
{
  "Auth": {
    "Mode": "ApiKey",
    "ApiKeys": {
      "reporting-app": {
        "Secret": "REPLACE_WITH_A_RANDOMLY_GENERATED_SECRET",
        "AllowJavaScript": false
      }
    }
  }
}
```

Replace the illustrative value before use; the application ships without a key.
Generate each secret from at least 32 random bytes. Key values must contain
16–4096 characters and identifiers 1–128 characters. Secrets must be unique across
key IDs. API-key mode fails startup when no keys are configured. Callers send a
valid secret in `X-Api-Key`.

JavaScript permission is off for every key by default. To allow a chart-producing
application to execute scripts, set `"AllowJavaScript": true` on its key and
restart the service. The key enables scripts automatically; no request flag is
needed. A caller can disable scripts for an individual render with
`"options": { "javaScript": false }`. Setting the request option to `true`
cannot grant permission to a key that lacks it.

When upgrading an existing configuration, replace each string key value with an
object containing `Secret`. Add `AllowJavaScript: true` only for keys that need
scripts; omitting it leaves scripts disabled.

To allow requests without credentials, explicitly select anonymous mode:

```json
{
  "Auth": {
    "Mode": "Anonymous",
    "ApiKeys": {}
  }
}
```

Anonymous mode permits access to rendering, service information, and OpenAPI
without a key. In either mode, a supplied invalid, empty, or duplicate `X-Api-Key`
returns 401 on those endpoints. `/v1/health` always allows anonymous access.
Anonymous renders have JavaScript disabled. A caller in anonymous mode may supply
a valid opted-in key to enable scripts for its request.

Keep real keys in the restricted external file, outside the published release and
source control. Local `pressroom.settings.json` files are ignored by Git. Keep
secrets out of `web.config`, packaged `appsettings.json`, and publish profiles.

When upgrading an earlier configuration, remove `Auth:WindowsEnabled`,
`Auth:RequireGroup`, and `Auth:AccessGroup`, then set `Auth:Mode` explicitly. Move
environment overrides into the JSON file. If setting `Paths:StateRoot`, use a
literal absolute path such as `C:\ProgramData\Pressroom`; `%ProgramData%` and other
environment-variable placeholders are not expanded.

The standard package aims to run without an additional machine-wide Visual C++
redistributable or Media Foundation installation. It includes no arbitrary copies
of Windows DLLs and does not suppress dependency validation. Static inspection
supports that goal for the selected headless renderer; the target server's real
PDF workload remains the qualification test. See the native-dependency evidence
in `docs/rendering-service-design.md`.

## API

| Endpoint | Authentication | Result |
|---|---|---|
| `POST /v1/render` | Configured access mode; API key required when `printer` is supplied | PDF with requested disposition and render headers, or 202 JSON print submission receipt |
| `GET /v1/printers` | API key required in every access mode | Configured printer identifiers |
| `GET /v1/health` | Anonymous | Minimal readiness: 200 or 503 |
| `GET /v1/info` | Configured access mode | Versions, limits, active/queued counts, and packaged font inventory |
| `GET /openapi/v1.json` | Configured access mode | OpenAPI contract |

Minimal request:

```json
{
  "html": "<!doctype html><html><head><title>Example</title></head><body><h1>Hello</h1></body></html>",
  "fileName": "example.pdf",
  "disposition": "inline"
}
```

Choose `"disposition": "inline"` to request browser PDF display, or
`"disposition": "attachment"` to request a download. The default is
`"attachment"` when omitted. These are the only accepted values; invalid values
return 400. Both modes return `application/pdf` and a sanitized suggested
filename, with the selected `Content-Disposition` header. Browser
settings and PDF support determine how an inline response is ultimately handled
([HTTP disposition semantics](https://www.rfc-editor.org/rfc/rfc6266.html#section-4.2)).

Defaults include Letter portrait, print media, background graphics, tagged PDF,
outlines, and page-number headers/footers. Options allow named or custom paper
sizes, margins, scale, media, locale, time zone, header/footer HTML, JavaScript,
readiness conditions, and a bounded processing deadline. The API key determines
whether JavaScript runs; static HTML needs no readiness flag. With an opted-in
key, a chart can use
`"readiness": { "function": "window.readyToPrint === true" }` without a
JavaScript flag. The request's `javaScript` option defaults to `true` but can only
disable the key's permission, never grant it. A readiness function returns 400
when JavaScript is effectively disabled. Existing script-dependent callers need
`AllowJavaScript: true` on their key; their requests need no new flag.

### Print to a Windows printer

Install [SumatraPDF](https://www.sumatrapdfreader.org/free-pdf-reader) on the Windows
server (the portable executable is also supported). This optional dependency is
needed only for printer output and is not bundled with Pressroom. Configure its
absolute path and an allowlist of printer identifiers in `appsettings.json` or
the external settings file, then restart Pressroom:

```json
{
  "Printing": {
    "SumatraPdfPath": "C:\\Tools\\SumatraPDF\\SumatraPDF.exe",
    "Printers": {
      "shipping": "Warehouse Laser Printer",
      "pdf-test": "Microsoft Print to PDF"
    }
  }
}
```

Each value is the exact name of a Windows printer installed and accessible to the
service's identity. Identifiers accept 1–128 ASCII letters, digits, underscores,
and hyphens and are matched ignoring case. No printer is enabled by default.
Grant the IIS application-pool identity execute access to SumatraPDF, print access
to the selected queues, and Modify access to the `temp` directory under
`Paths:StateRoot`. Printers installed only for an interactive user may not be
available to IIS. Configure
paper, tray, duplex, and other device defaults on the Windows printer; the PDF's
render options control its content, and SumatraPDF uses the printer's settings to
place it on paper. Qualify the output on the intended printer.

Send a valid `X-Api-Key` and add `printer` to the normal render request:

```json
{
  "html": "<!doctype html><html><body><h1>Shipping document</h1></body></html>",
  "printer": "shipping"
}
```

Every valid configured API key can print. Anonymous callers cannot print or list
printers, even with `Auth:Mode` set to `Anonymous`. `GET /v1/printers` returns
`{"printers":["pdf-test","shipping"]}` to authenticated callers; it lists the
allowlist, not live queue health. Unknown identifiers return 400 before rendering.
Missing or invalid credentials return 401. Omitting `printer` or using `null`
preserves the existing PDF response. `fileName` and `disposition` have no effect
on printer output.

Pressroom renders through Chromium, temporarily writes the PDF in a private job
directory under `Paths:StateRoot\temp`, and invokes SumatraPDF's
[`-print-to` command](https://www.sumatrapdfreader.org/docs/Command-line-arguments).
After the helper reports success, the response is **202 application/json**:

```json
{
  "jobId": "64e5b7e07c8e4bdb86521bed5cd54d4c",
  "printer": "shipping",
  "status": "submitted",
  "pageCount": 1
}
```

`submitted` means the helper accepted submission to Windows, not that paper has
finished printing. `jobId` is a Pressroom correlation ID and the temporary PDF's
basename, not a Windows spooler job ID. There is no status polling endpoint.
Offline devices or later spooler errors are managed through the Windows queue.
The normal admission limit and processing timeout cover rendering and submission.
The helper is stopped on cancellation or timeout, and temporary files are cleaned
up after it exits. The Windows spooler can retain document data independently.

Printer errors return `application/problem+json`: 502 for a helper-reported
submission failure, 503 when the helper or its local resources are unavailable,
and 504 for a processing timeout. A timeout, disconnect, or failure can occur
after some or all pages reached the spooler. **Check the queue before retrying**;
Pressroom does not deduplicate or automatically retry print requests.

For a local interactive test, select `pdf-test` and complete Microsoft Print to
PDF's **Save Print Output As** dialog before the request timeout. That virtual
printer normally prompts for a destination file, so it is a manual test target,
not an unattended IIS destination. See `docs/windows-acceptance.md` for the test.

### Rendering behavior and limits

Documents must contain their own CSS and assets. Resource URLs are restricted to
`data:`; network, local-file, and `blob:` resources are blocked. Inline CSS remains
supported; inline and data-backed scripts require JavaScript permission and an
omitted or true `javaScript` option. Browser-enforced Content Security Policy and an empty
Connection Allowlist apply before document parsing, including nested documents;
the latter also blocks WebRTC, WebTransport, and speculative network connections.
Offline contexts and request/WebSocket interception provide additional barriers.
Workers, popups, forms, downloads, and origin-backed storage are disabled. Startup
checks the browser's actual connection-policy enforcement and fails if it is
unsupported, including for an executable override. Use
`font-family: 'Noto Sans', sans-serif` for the bundled regular, bold, italic, and
bold-italic faces; no Windows font installation is needed for those main-document
fonts. Header/footer templates have separate styling and Chromium limitations;
custom embedded header/footer fonts are not supported in this initial build. A
real Chromium check used the packaged face in the main document but fell back to
a system face in the footer, even with an independent data-backed font rule.
The supplied footer therefore uses a generic font; qualify its appearance on
Windows separately. Tagging does not by
itself establish PDF/UA compliance.

The complete encoded JSON request is limited to 25 MiB by default; IIS/Kestrel have
a 32 MiB outer limit. Two renders can be active, with eight additional admitted
requests. A permit remains held through response completion so slow clients do
not accumulate unlimited buffered PDFs. Saturation returns 429 while readiness
can remain healthy. Readiness/page execution failure is 422, renderer replacement
is 503, and the overall processing timeout is 504.

PDF-only requests do not deliberately store input HTML or generated PDFs. Printer
requests temporarily store the generated PDF as described above. Logs
contain metadata, never document bodies, API-key secrets, raw blocked URLs, or
page error/console text. Chromium may create temporary files in its private
working directory; this is not a promise of zero disk writes or OS-wide network
isolation.

The pinned Chromium build can reject printing of header/footer templates that
attempt external loads. WebRTC attempts inside `srcdoc` or blank child frames can
also crash the individual render under the connection policy. These cases return
a content-free document error; the render context is closed and later renders
remain usable. See the sandbox checks in `docs/verification.md`.

## Calling Pressroom

`Pressroom.Client` is an HTTP-only project: referencing it does not bring Chromium
into the caller. Supply a reusable `HttpClient` and the configured Pressroom URL:

```csharp
using Pressroom.Client;
using Pressroom.Contracts;

// Use your application's HttpClient lifetime/DI setup in production.
var http = new HttpClient
{
    BaseAddress = new Uri(configuration["Pressroom:Url"]!),
    Timeout = TimeSpan.FromSeconds(65)
};
http.DefaultRequestHeaders.Add("X-Api-Key", configuration["Pressroom:ApiKey"]!);
var pressroom = new PressroomClient(http);
var pdf = await pressroom.RenderAsync(new RenderRequest
{
    Html = completeHtml,
    FileName = "orders-preview.pdf",
    Disposition = PdfDisposition.Inline
}, cancellationToken);
// Return pdf.Bytes with application/pdf, pdf.FileName, and pdf.Disposition.
```

For streaming, use `IPdfRenderer.RenderAsync(string html, RenderOptions options,
CancellationToken)` and dispose the returned `PdfRenderResult` after copying its
`PdfStream`. The request-object overload above is a buffered convenience API.
Both result types retain the returned `Disposition`. A calling web application
must apply the chosen
`Content-Disposition` to its own PDF response to the browser. Copying the bytes
alone does not carry Pressroom's HTTP headers through that extra response. Use
the web framework's typed header/file APIs for the mode and filename.
The caller controls the overall deadline and retry policy. The client does not
retry automatically; `PressroomException` exposes the status and `Retry-After`
information so the caller can select HTML fallback or an explicit bounded retry.

For printer output, call `PrintAsync` with the same API-key-configured client:

```csharp
var job = await pressroom.PrintAsync(new RenderRequest
{
    Html = completeHtml,
    Printer = "shipping"
}, cancellationToken);
// job.Status == "submitted"; job.JobId identifies this submission.
```

The PDF-returning client methods reject requests containing a printer before
sending them. `PrintAsync` returns `PrintJobResult` or throws `PressroomException`
for a server error; it never retries a print job automatically.

For anonymous mode, omit the `X-Api-Key` header. Keep the base address's trailing
slash, for example `https://pressroom.example/`. Store the caller's URL and key in
its application configuration file.

The calling application owns its HTML, configured URL, and fallback behavior. It
may deliver HTML when no Pressroom URL is configured or when Pressroom is
unavailable. Pressroom also permits localhost development. A health probe before
every render is unnecessary: use the render response or connection outcome within
the caller's timeout policy.

## Build, tests, and development

The solution pins the SDK compatibility policy in `global.json` and exact NuGet
versions in package references/lock files. From the solution directory:

```text
dotnet restore Pressroom.slnx --locked-mode
dotnet build Pressroom.slnx -c Release --no-restore
dotnet test --solution Pressroom.slnx -c Release
```

Real-browser tests require the separate browser payload and writable temporary
state. Unit/API tests use controlled renderer substitutes where appropriate.
The sample `samples/orders-bilingual.html` exercises two Letter pages, English
and French text, all four packaged font styles, inline vector graphics, headings,
and page breaks. `samples/orders-bilingual.request.json` is ready to submit and
requests inline preview; change its `disposition` to `attachment` for a download.

For a local Windows run, publish the `IisFolder` profile to acquire and verify the
matching browser, then run its executable with your local settings file:

```powershell
& '.\artifacts\publish\IisFolder\Pressroom.Server.exe' --config 'C:\PressroomConfig\pressroom.settings.json'
```

Configure a writable `Paths:StateRoot` outside the release folder and either your
own API key or explicit anonymous mode. The published executable's `--check` mode
is also available for local diagnostics: it validates the real packaged renderer
without listening on a port or installing anything. Add `--config <path>` to use
an explicit diagnostic configuration file. Playwright's internal process settings
are supplied by the application and publish helper; no environment variables need
to be configured for normal deployment or local use.

Cross-platform implementation verification can explicitly set
`PressroomBundleBrowser=false` for a development-only publish. That output lacks
the automatic Windows browser bundle/check and is not a complete IIS release.

See [`docs/verification.md`](docs/verification.md) for the verification record,
[`docs/rendering-service-design.md`](docs/rendering-service-design.md) for the design,
and [`docs/windows-acceptance.md`](docs/windows-acceptance.md) for the target-server
qualification record. The
application has no database, durable job queue, or separate worker service.
