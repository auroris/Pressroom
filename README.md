# Pressroom

Pressroom is a standalone .NET 10 service that turns a complete HTML document into
a PDF. It publishes as one IIS application folder, including its matching private
Playwright driver, Node runtime, Chromium headless shell, and Noto Sans fonts.

This is a separate solution. A suitable location on your machine is
`C:\Users\steph\source\repos\Pressroom`. Nothing in this solution modifies
`C:\Users\steph\source\repos\UmbracoAppHost`.

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
application-pool identity, mixed authentication, and operation without Media
Foundation. This source delivery does not claim that clean-Windows gate has run.

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
| Authentication, mixed mode | Enable **Anonymous** and **Windows Authentication** |
| Site binding | HTTPS with your existing certificate/binding |

Anonymous access lets an API-key request reach the application; endpoint
authorization then chooses the key or Windows branch. An empty or invalid
`X-Api-Key` never falls back to Windows authentication. Optional Windows group
restrictions apply only to the Windows branch. See
[ASP.NET Core Windows authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0).

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

Copy `samples/pressroom.settings.example.json` to the external configuration path
and adjust it there. API keys are a dictionary of nonsecret identifier to randomly
generated secret under `Auth:ApiKeys`; use at least 32 random bytes for a new key.
The application enforces a minimum length of 16 characters. The sample contains
no shared default key. To use keys alone, set `Auth:WindowsEnabled` to `false` and
configure at least one key.

Set the application-pool environment variable `PRESSROOM_CONFIG_FILE` if you want
a different configuration file path. Environment overrides such as
`Render__Concurrency` take precedence over the external file. Keep secrets out of
`web.config`, published `appsettings.json`, and publish profiles. A missing
explicitly selected external file is a startup error.

The standard package aims to run without an additional machine-wide Visual C++
redistributable or Media Foundation installation. It includes no arbitrary copies
of Windows DLLs and does not suppress dependency validation. Static inspection
supports that goal for the selected headless renderer; the target server's real
PDF workload remains the qualification test. See the native-dependency evidence
in `docs/rendering-service-design.md`.

## API

| Endpoint | Authentication | Result |
|---|---|---|
| `POST /v1/render` | Windows or `X-Api-Key` | PDF with requested inline/attachment disposition; `X-Render-Pages` and `X-Render-Duration-Ms` |
| `GET /v1/health` | Anonymous | Minimal readiness: 200 or 503 |
| `GET /v1/info` | Windows or key | Versions, limits, active/queued counts, and packaged font inventory |
| `GET /openapi/v1.json` | Windows or key | OpenAPI contract |

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
readiness conditions, and a bounded processing deadline. Static HTML needs no
readiness flag. A chart may opt into
`"readiness": { "function": "window.readyToPrint === true" }`.

Documents must contain their own CSS and assets. Network and local-file resource
requests, WebSockets, and service workers are blocked at the browser boundary.
Inline/data-backed resources are supported. Use
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

Input HTML and generated PDFs are not deliberately stored by the server. Logs
contain metadata, never document bodies, API-key secrets, raw blocked URLs, or
page error/console text. Chromium may create temporary files in its private
working directory; this is not a promise of zero disk writes or OS-wide network
isolation.

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
Both result types retain the returned `Disposition`. If an application such as
Umbraco calls Pressroom from its server, it must apply the chosen
`Content-Disposition` to its own PDF response to the browser. Copying the bytes
alone does not carry Pressroom's HTTP headers through that extra response. Use
the web framework's typed header/file APIs for the mode and filename.
The caller controls the overall deadline and retry policy. The client does not
retry automatically; `PressroomException` exposes the status and `Retry-After`
information so the caller can select HTML fallback or an explicit bounded retry.

For Windows credentials, construct the reusable client with an
`HttpClientHandler { UseDefaultCredentials = true }` instead of adding a key. Keep
the base address's trailing slash, for example `https://pressroom.example/`.

The calling application owns its HTML and configured URL. Its agreed behavior is
to deliver HTML when running under localhost, when no Pressroom URL is configured,
or when Pressroom is unavailable. That decision belongs in the caller; Pressroom
continues to expose a rendering API and permits localhost development. A health
probe before every render is unnecessary: use the render response/connection
outcome within the caller's timeout policy. This solution makes no changes to
UmbracoAppHost.

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

For a local Windows development run outside Publish, first build the server and
use the private driver from its output directory to provision the developer's
browser folder once:

```powershell
$output = (Resolve-Path '.\src\Pressroom.Server\bin\Debug\net10.0').Path
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $output 'browsers'
& "$output\.playwright\node\win32_x64\node.exe" `
  "$output\.playwright\package\cli.js" install chromium --only-shell
```

Configure writable external state and either Windows authentication or your own
development API key before running. The published executable's `--check` mode is
also available for local diagnostics: it validates the real packaged renderer
without listening on a port or installing anything.

Cross-platform implementation verification can explicitly set
`PressroomBundleBrowser=false` for a development-only publish. That output lacks
the automatic Windows browser bundle/check and is not a complete IIS release.

See `docs/verification.md` for the checks performed on this source delivery,
`docs/rendering-service-design.md` for the full design, and
`docs/windows-acceptance.md` for the target-server qualification record. The
application has no database, durable job queue, or separate worker service.
