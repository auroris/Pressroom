# Pressroom: HTML-to-PDF rendering service

Status: design baseline, revised 2026-10-24. Publishing is integrated into Visual Studio; see [the README](../README.md) for the implemented workflow. Windows/IIS acceptance testing has not yet been performed.

Pressroom provides HTML-to-PDF rendering as a standalone service. Any application that can produce a self-contained HTML document can submit it and receive a PDF. Calling applications remain responsible for their templates and access checks.

## 1. Purpose, scope, and deployment objective

- One rendering API: send complete HTML, receive a PDF. Callers use Razor, mustache, or their existing templating system.
- One centrally maintained rendering engine and font package per environment.
- Paper output: page sizes, margins, page breaks, running headers and footers with page numbers, tagged PDF, and document outlines.
- One deployable ASP.NET Core application. A production release is a complete Windows x64 folder, ready to copy into an already provisioned IIS application.
- Routine upgrades consist of stopping the application, copying the release, and starting it. The production application does not download browsers, restore packages, compile assets, or install machine components.
- Qualify the standard PDF workload without an additional machine-wide Visual C++ Redistributable installation or the Windows Media Foundation feature. Bundle redistributable native dependencies if the selected executables actually require them. This is a release acceptance objective, not yet a demonstrated Windows runtime result.

Non-goals for version 1: arbitrary URL rendering, remote asset fetching, document storage, HTML authoring or templating, Word conversion, asynchronous jobs with callbacks, PDF/A, signatures, or PDF merging. The baseline requires no database, message broker, container runtime, or separately installed worker service.

## 2. Architecture

ASP.NET Core 10 minimal API, stateless, with Serilog and an OpenAPI document at `/openapi/v1.json`. IIS in-process hosting is the primary deployment. Running the same host on Kestrel, including as an explicitly installed Windows service, remains an alternative.

Playwright for .NET owns one headless Chromium browser instance per application instance. Chromium and Playwright have child processes; “one application” does not mean one operating-system process. Each render receives a fresh nonpersistent browser context, disposed when the request ends. No context, cookie jar, or browser storage is reused between callers.

The rendering queue is memory-only and bounded. Start with `Render:Concurrency = 2` and `Render:QueueLimit = 8`; these are conservative design defaults, to be tuned against representative documents and the server's memory budget. CPU count alone is not a capacity estimate. Admission must bound requests being read/validated as well as queued and active renders, so a burst cannot allocate unlimited HTML bodies before reaching the queue.

An application-owned browser supervisor starts Chromium, performs a warm-up render, tracks health, and coordinates restart and shutdown. Planned recycling after 500 completed render attempts drains active contexts before replacing the browser. A crash fails affected requests and starts a bounded recovery attempt; the process must not create competing replacement browsers.

Multiple instances have independent queues and browser owners. Callers can use a conventional load balancer with the same access policy configured on each instance. There is no durable job recovery in v1.

### Project boundaries

| Project | Responsibility |
|---|---|
| `Pressroom.Contracts` | Request/response models and shared validation concepts |
| `Pressroom.Client` | HTTP client and authentication configuration; no Playwright dependency |
| `Pressroom.Rendering` | Playwright adapter, browser lifecycle, font resources, PDF inspection |
| `Pressroom.Server` | API, admission, authentication, logging, health, hosting |

The server publishes as one folder. A developer who explicitly selects local rendering references the rendering component separately; ordinary HTTP clients do not acquire Chromium or its driver transitively.

## 3. API contract

### `POST /v1/render`

Request: UTF-8 `application/json` containing a complete document. Request compression is outside the initial contract. All options below are optional; their shown values are the baseline defaults.

```json
{
  "html": "<!doctype html><html lang=\"en\">...</html>",
  "fileName": "orders-2026-10.pdf",
  "disposition": "attachment",
  "options": {
    "pageSize": "Letter",
    "landscape": false,
    "margins": { "top": "20mm", "right": "15mm", "bottom": "20mm", "left": "15mm" },
    "preferCssPageSize": false,
    "printBackground": true,
    "scale": 1.0,
    "displayHeaderFooter": true,
    "headerTemplate": "<div style=\"font-size:9px;width:100%;text-align:center\"><span class=\"title\"></span></div>",
    "footerTemplate": "<div style=\"font-size:9px;width:100%;text-align:center\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>",
    "tagged": true,
    "outline": true,
    "media": "print",
    "javaScript": true,
    "locale": "en-CA",
    "timeZone": "America/Edmonton",
    "readiness": { "selector": null, "function": null, "delayMs": 0, "timeoutMs": 5000 },
    "timeoutMs": 30000
  }
}
```

`html` is required. Omitted `fileName` becomes `document.pdf`; sanitize supplied names for `Content-Disposition` and use a framework response API rather than assembling the header manually. The sample filename is illustrative, not a fixed default.

`disposition` accepts exactly `"inline"` or `"attachment"`; omission defaults to `"attachment"`. Inline requests browser display of the PDF, while attachment requests a download. Both modes return `application/pdf` and a sanitized suggested filename; the option selects the response disposition. Reject other values with 400. Browser settings and PDF support still determine the final inline behavior. [HTTP disposition semantics](https://www.rfc-editor.org/rfc/rfc6266.html#section-4.2).

`pageSize` accepts `Letter`, `Legal`, `Tabloid`, `A3`, `A4`, or `A5`, or an object such as `{ "width": "8.5in", "height": "11in" }`. `preferCssPageSize` allows CSS `@page` dimensions to take precedence. Validate dimensions, margins, scale, locale, time zone, timeout bounds, and unknown properties before opening a browser context.

Headers and footers use Chromium's `date`, `title`, `url`, `pageNumber`, and `totalPages` substitutions. They require their own styles and font handling; page CSS is not inherited and their scripts are not executed. `tagged` requests structural tagging; it is not a declaration of PDF/UA conformance. See the [Playwright PDF API][pw-pdf].

Readiness defaults to no caller-supplied condition. A chart document can opt into `function: "window.readyToPrint === true"`. If several conditions are supplied, all must complete within one readiness budget; a delay is not an additional unbounded wait. A JavaScript readiness expression with `javaScript: false` is invalid. Wait for required fonts before printing, within the remaining overall deadline.

The overall `timeoutMs` covers admitted request processing, queue waiting, document loading, readiness, PDF generation, and page counting. It does not reset between stages and does not include sending the completed PDF over a slow client connection. Until options are parsed, use the server maximum as the admission deadline, then shorten it using the original admission timestamp. HTTP body-read and connection protections remain host responsibilities.

`Render:MaxBodyBytes` means the entire encoded UTF-8 JSON body, including templates and escaped HTML, not just the decoded `html` string. Default: 26,214,400 bytes (25 MiB).

| Status | Meaning | Body / headers |
|---|---|---|
| 200 | Rendered | `application/pdf`; requested inline/attachment `Content-Disposition` with sanitized filename; `X-Render-Pages`; `X-Render-Duration-Ms` |
| 400 | Invalid JSON, required field, option, unit, or unsupported combination | `application/problem+json` with field information |
| 401 / 403 | Authentication or authorization failure | Application problem details |
| 413 | Application request-body limit exceeded | Problem details when Pressroom handles the rejection |
| 415 | Unsupported media type or request content encoding | Problem details |
| 422 | Caller readiness failed or uncaught page execution error | Bounded diagnostics for the authenticated caller |
| 429 | Admission/queue capacity exhausted | Problem details and `Retry-After` |
| 503 | Browser unavailable, restarting, or host draining | Problem details and `Retry-After` when a response can still be sent |
| 504 | Overall processing deadline expired | Problem details |

Native IIS rejection, a lost connection, or forced process termination cannot be guaranteed to produce application problem details. `X-Render-Duration-Ms` measures processing from admission through PDF/page-count completion, including queue time.

### `GET /v1/health`

Readiness: 200 only after a successful warm-up and while the browser is available; 503 during startup, replacement, failure, or shutdown. Queue saturation alone is not unhealthy; the render endpoint uses 429. Return a minimal status suitable for an anonymous IIS warm-up/health request. Put detailed versions and operational counters in `/v1/info`, which follows the configured access mode.

### `GET /v1/info`

Endpoint reporting application and Playwright versions, Chromium version, active/queued counts, limits, renders since startup, last browser restart, named page sizes, and guaranteed packaged fonts. It requires a valid API key in API-key mode and allows requests without a key in anonymous mode. Any discovered system fonts must be identified separately as machine-dependent. Never return keys, secrets, document content, or internal filesystem paths.

The OpenAPI document follows the same configured access mode as rendering and service information.

## 4. Input, fonts, and retention

### Self-contained documents

Styles are inline or in `<style>`. Images and caller-supplied fonts use `data:` URLs. The renderer accepts inline resources and the blank document context; resource fetches to network and local-file locations are rejected. V1 does not expose a URL-rendering endpoint.

Install context-wide request interception and WebSocket rejection before creating pages, and set `ServiceWorkers = Block`. Cover navigations, subframes, popups, scripts, CSS imports, images, and fetch/XHR in integration tests. Record blocked-resource counts and reasons, not raw URLs. A blocked resource remains missing and does not by itself fail the render. [Playwright documents separate service-worker and WebSocket considerations][pw-context].

These are browser/application controls, not proof of OS-wide network isolation. A deployment requiring a strict no-egress guarantee needs an independently enforced and tested network policy. JavaScript may use private state within its request context; it has no state shared with another render. Disabling JavaScript does not replace the other controls.

Reject `<base>` elements during HTML validation rather than assuming Chromium ignores them. Relative URLs must not create an alternative asset-fetching path. This does not turn the renderer into a general HTML sanitizer.

### Fonts

The standard package includes a small font set licensed for redistribution, with a manifest of family names, weights, styles, and files. Load those resources from the release and supply data-backed `@font-face` rules to documents that use the packaged families. An arbitrary `fonts` directory does not automatically register fonts with Windows or Chromium. [CSS defines how font-face resources are selected][css-fonts].

Callers can embed additional fonts themselves. Host-installed fonts remain optional compatibility support, not a prerequisite for the standard acceptance documents. The initial implementation bundles Noto Sans Regular, Bold, Italic, and Bold Italic with the SIL OFL license and file hashes. Real Chromium tests verify those faces in main-document PDF text; Windows/IIS qualification is still required.

Header/footer templates require separate styling and acceptance tests. Custom embedded header/footer fonts are unsupported in the initial build: an independent data-backed Noto Sans rule still fell back to a system font in the real Chromium 156 Linux footer test. Use generic footer typography and verify its appearance on Windows; do not assume main-document embedding covers it. Wait for `document.fonts.ready` for the main document within the render deadline. [Font readiness is specified by the CSS Font Loading API][css-font-loading].

### Retention and logging

Pressroom does not deliberately persist input HTML or generated PDF documents. Use nonpersistent contexts, set `AcceptDownloads = false`, leave `PdfAsync`'s output path unset, and disable tracing, HAR, video, and document screenshots in normal production operation. Chromium's temporary profile/artifact files and operating-system paging mean this is not a blanket zero-disk-write guarantee. [Browser options][pw-browser] and [browser launch implementation][pw-launch-source] explain those separate mechanisms.

Serilog records request identifiers, caller names/key identifiers, sizes, timings, page counts, outcomes, and bounded resource-block counts. Raw HTML, PDF bytes, API keys, full blocked URLs, and page console/error text must not enter server logs. Any page diagnostics returned to the caller are bounded and redacted separately. Diagnostic modes that retain documents are outside the normal v1 configuration.

## 5. Authentication and process isolation

Support two explicit settings for `Auth:Mode`: `ApiKey` (the default) and `Anonymous`. API-key mode requires at least one configured key and a valid `X-Api-Key` on rendering, service-information, and OpenAPI requests. Anonymous mode permits requests without that header. Health remains anonymous in both modes. Windows authentication and domain-group authorization are not supported.

Provision named keys in `Auth:ApiKeys` in the external JSON configuration file. Compare secrets in constant time and identify them in logs without exposing the secret. Key values must be 16–4096 characters and identifiers 1–128 characters; generate secrets from at least 32 random bytes. A supplied invalid, empty, oversized, or duplicate key fails with 401 on policy-protected endpoints, even in anonymous mode. There is no fallback to a host-supplied identity or a Negotiate challenge.

Under IIS, enable Anonymous Authentication and disable Windows Authentication. ASP.NET Core performs endpoint authorization; disabling anonymous access at IIS would prevent callers from reaching the application. For in-process hosting, set `IISServerOptions.AutomaticAuthentication = false` so IIS does not supply an authenticated Windows identity. Verify the configured access mode and response headers through real IIS. See [AutomaticAuthentication][iis-auto-auth].

Run under a dedicated application-pool identity with access only to the required release and runtime locations. Set `ChromiumSandbox = true` explicitly and verify launch under that identity; Playwright's default is false. A launch failure must produce a clear startup error, not silently disable sandboxing. An explicitly documented exception changes the deployment's isolation assumptions. [Launch options][pw-launch].

Use HTTPS to protect API keys and document content in transit.

## 6. Rendering and lifecycle

1. Authenticate and perform header/length checks. Acquire bounded admission before reading or allocating an entire request body; return 429 if capacity is unavailable.
2. Count body bytes while reading, parse and validate the request, and establish its remaining deadline. Release admission immediately on rejection.
3. Wait for a render slot within the same deadline. Cancel queued entries when the caller disconnects or the host stops.
4. Create a nonpersistent context with locale, time zone, JavaScript policy, downloads disabled, and service workers blocked. Install request/WebSocket controls before creating the page. Emulate the selected media on the page.
5. Compose approved packaged font rules into the HTML passed to `SetContent`, or inject them after `SetContent` completes. Use the remaining load timeout, then complete caller readiness and font readiness; a style node created before replacing the document would be lost.
6. Generate PDF bytes with mapped options and no filesystem output path. Inspect the in-memory PDF with a packaged library such as PdfPig to obtain the page count; do not depend on an undocumented Chromium count.
7. Close the context and release the render slot, then return the PDF. Keep the total admission permit until the response completes or aborts so slow clients cannot leave an unbounded number of buffered PDFs while new requests are admitted. Record metadata and update the recycling counter when rendering completes. Apply host response/connection limits separately from the processing deadline.

### Deadlines and cancellation

The request owns a single deadline and observes `RequestAborted`. A timeout wrapper alone does not stop browser work. On timeout or disconnect, remove queued work or close the active context, observe outstanding operations, and perform bounded cleanup. Release render capacity only when the context is gone, or when its failed browser has been terminated. A stuck cleanup marks the browser unhealthy and triggers supervised replacement. Other affected requests may then fail with 503.

This needs explicit implementation because the PDF API has no per-call cancellation-token parameter. `Task.WaitAsync` can bound the caller's wait; it is not cancellation of the underlying rendering operation. [PDF API][pw-pdf]; [Task.WaitAsync][task-wait].

### Recycling and shutdown

At the planned render budget, stop assigning new work, drain active contexts within their existing deadlines, close the browser, relaunch, warm up, and resume. Waiting requests keep their original deadlines. Browser unavailability is visible as 503; full admission while waiting is 429.

On host shutdown, stop admission, cancel waiting entries, and drain/cancel active contexts within `Render:ShutdownTimeoutMs`. Then dispose browser and driver processes. Coordinate .NET host, ANCM, and IIS shutdown limits; the outer limits must leave time for the intended cleanup. Forced shutdown can terminate connections. Clean only this instance's processes and temporary files.

## 7. Configuration

Published `appsettings.json` contains nonsecret defaults. Load the external JSON settings file afterward. Its default location is the operating system's common application-data directory under `Pressroom/pressroom.settings.json`, normally `C:\ProgramData\Pressroom\pressroom.settings.json` on Windows. A blank or omitted `ConfigurationFile` uses that optional default; set `ConfigurationFile` in `appsettings.json` to select a required file at another location. Resolve relative paths against the application directory. `--config <path>` can also select a file for local runs and package checks. Application settings are read at startup from JSON, without environment-variable or arbitrary command-line overrides; restart after changing them. Real settings files and keys remain outside the release and source control.

| Key | Default | Meaning |
|---|---|---|
| `Render:Concurrency` | `2` | Active contexts; tune after memory/throughput measurements |
| `Render:QueueLimit` | `8` | Additional admitted/waiting requests |
| `Render:MaxBodyBytes` | `26214400` | Complete encoded JSON request, 25 MiB |
| `Render:DefaultTimeoutMs`, `Render:MaxTimeoutMs` | `30000`, `60000` | End-to-end processing deadlines |
| `Render:ShutdownTimeoutMs` | `30000` | Graceful application cleanup budget |
| `Render:RestartAfterRenders` | `500` | Planned browser recycling threshold |
| `Render:DefaultPageSize` | `Letter` | Default page size |
| `Render:BrowsersPath` | `browsers` relative to application base | Packaged Playwright browser tree |
| `Render:ChromiumPath` | unset | Advanced, explicitly validated executable override |
| `Render:ChromiumSandbox` | `true` | Explicit launch policy |
| `ConfigurationFile` | Common application data + `Pressroom/pressroom.settings.json` | External JSON settings; explicitly selected files must exist |
| `Paths:StateRoot` | Common application data + `Pressroom` on Windows | External runtime location |
| `Fonts:Manifest` | `fonts/manifest.json` relative to application base | Guaranteed packaged font definitions |
| `Auth:Mode` | `ApiKey` | Require a valid key, or explicitly select `Anonymous` to allow requests without a key |
| `Auth:ApiKeys` | empty | Dictionary of nonsecret identifier to valid API-key secret |
| Logging policy (fixed in v1) | Console plus up to 14 rolling files, 10 MiB each | Metadata-only logs under the external runtime root; arbitrary Serilog sink settings are not loaded |

Fail startup on invalid limits, missing packaged assets, unusable runtime directories, an unsupported authentication mode, invalid key entries, or API-key mode without configured keys. Normal releases use their matching bundled browser; a system executable override is outside the standard package compatibility guarantee.

## 8. Windows and IIS deployment

### 8.1 Server prerequisites

The supported baseline is Windows x64 on a Windows Server version supported by both the selected Playwright release and .NET 10. Current Playwright documentation lists Windows Server 2019 or later; record the actual tested edition/build in each release's acceptance record. [.NET Playwright system requirements][pw-requirements].

Provision IIS and the .NET 10 Hosting Bundle once. The bundle supplies ASP.NET Core's IIS module; installing only a .NET runtime/SDK is insufficient. Self-contained .NET publishing does not remove the IIS-module requirement. Keep .NET 10 installed for the framework-dependent profile; a newer major runtime alone does not satisfy it by default. [Hosting Bundle][hosting-bundle]; [runtime selection][runtime-selection].

Native requirements must be established for the selected PDF renderer, rather than inferred from the complete browser automation dependency list. The packaging policy is:

| Component | Standard-package approach |
|---|---|
| Chromium headless shell and Playwright driver | Ship the matching private binaries and resources |
| Visual C++ runtime DLLs, if imported by a shipped executable | Bundle the permitted release redistributables beside that consuming executable, matching architecture/toolset; update them with Pressroom |
| Universal CRT | Use the component supplied by the supported modern Windows Server installation |
| Media Foundation | First qualify the PDF workload with this optional feature absent; do not require it just because a broad dependency check mentions it |
| IIS module / Hosting Bundle | Remains a one-time server prerequisite |

Microsoft supports app-local Visual C++ deployment. Use the official Redistributable files for the applicable toolset, preserve their notices, and record versions; arbitrary copies from another machine's System32 are not a packaging source. The Universal CRT is a separate OS component included with Windows Server 2016 and later. [VC++ redistribution][vc-redist]; [Universal CRT][ucrt].

Chromium's Windows media code delay-loads Media Foundation libraries and has a failure path that disables media functionality when they are absent. This supports testing PDF-only operation without Media Foundation; it does not prove every browser path is independent of it. Camera/microphone capture, media playback, and browser video recording are outside Pressroom v1's supported rendering workload. [Chromium media build][chromium-media-build]; [media initialization][chromium-mf-init].

Keep normal Playwright dependency validation enabled. The inspected Windows checker examines normal PE imports; the headless shell's Media Foundation references are delayed imports. The broad Chromium `install-deps` group alone is therefore not evidence that PDF startup needs Media Foundation. [Windows checker][pw-winldd].

If future qualification encounters a demonstrable preflight false positive, `PLAYWRIGHT_SKIP_VALIDATE_HOST_REQUIREMENTS=1` is a diagnostic mechanism for separating validation failure from real launch/render failure. It is not needed by the present static findings and is not a standard Pressroom setting. Any later release that needs it must prove the exact PDF workload and retain real startup/warm-up checks. It does not change Windows DLL loading or make mandatory imports optional. [Maintainer explanation][pw-validation-override]; [Windows DLL loading][windows-dll-loading].

If the selected stock renderer genuinely cannot pass without Media Foundation, record the exact failure before choosing a fallback. One-time feature provisioning is a separate deployment alternative outside the intended standard package, not a silent relaxation of its acceptance objective. Copying individual Media Foundation DLLs into the application directory is not an equivalent deployment. A custom Chromium build is a last resort because it creates an ongoing browser build/patch workload. Do not use unrelated sandbox/GPU switches as a substitute for proving the dependency behavior.

Enable IIS Anonymous Authentication, disable Windows Authentication, and enable Application Initialization for the warm-service configuration. Provision the site, certificate/binding, dedicated app pool, external JSON configuration, and directory permissions. Routine application upgrades do not repeat this setup.

### 8.2 Release contents

Default profile: `net10.0`, `win-x64`, framework-dependent, ordinary folder publish, IIS in-process. Offer a separate self-contained folder profile if needed. Keep single-file publishing disabled because IIS in-process hosting cannot load it. Leave trimming/AOT outside the baseline until dependency compatibility is demonstrated. [Publishing][dotnet-publishing]; [IIS in-process hosting][iis-inprocess].

| Release-relative content | Purpose |
|---|---|
| `Pressroom.Server.exe`, `.dll`, `.deps.json`, `.runtimeconfig.json`, dependency assemblies | Application and managed dependencies |
| `web.config` | Generated IIS entry point and application settings |
| `.playwright/node/win32_x64/` | Private Node executable supplied by Playwright |
| `.playwright/package/` | Matching automation driver and browser metadata |
| `browsers/` | Matching Chromium executables and installer-selected companion files |
| `fonts/` and manifest | Redistributable, tested font resources |
| `appsettings.json` | Nonsecret application defaults |
| License/notice files and release manifest | Version inventory and distributed notices |

Preserve the published directory structure, including dot-prefixed `.playwright`. The package's private Node driver makes a separate server Node/npm installation unnecessary. [Driver implementation][pw-driver] and [publish targets][pw-targets].

Pin Playwright and restore dependencies from committed lock information. Download the matching Chromium headless shell during release packaging using that package's CLI with `install --only-shell chromium`. Launch with `Headless = true` and no channel override; this selects Playwright's default headless shell. Use a Windows x64 build environment and clean staging. The publish helper supplies the installer's process-scoped `PLAYWRIGHT_BROWSERS_PATH` automatically. Preserve installer-selected companion files; do not hand-prune them just because video recording is unused. [Browser packaging][pw-browsers].

After build smoke tests, remove only Playwright's `DEPENDENCIES_VALIDATED` cache markers before archiving; preserve `INSTALLATION_COMPLETE`. The validation cache is not proof about the destination machine. A read/execute-only browser directory is the intended production layout; marker writes are best-effort in the current implementation. Test the marker-free artifact under that permission model. [Registry implementation][pw-registry].

### Visual Studio publish integration

The standalone implementation supplies `IisFolder` and `IisFolderSelfContained` Visual Studio profiles. Pressroom.Server imports `Pressroom.Publish.targets`: during Publish it uses the matching private Node/driver to install `chromium --only-shell` into a build-owned bundle, adds the complete bundle to `ResolvedFileToPublish`, assembles an isolated local copy, and runs that copy's `--check` before deployment transfer. A failed warm-up fails Publish. Normal Build and unit tests do not acquire browsers.

The default framework-dependent profile targets `net10.0`, `win-x64`, with single-file/trimming/AOT disabled. The project targets also apply to a Visual Studio IIS/Web Deploy profile configured for Windows x64. The automatic helper uses Windows PowerShell 5.1 already supplied with the Windows build environment; no separate PowerShell 7 or Node installation is required. Build support is invoked by Visual Studio, rather than by a separate operator deployment script.

The publish helper copies supplied managed package notices, records versions and SHA-256 hashes, and removes only `DEPENDENCIES_VALIDATED` markers. It leaves `web.config` out of file hashes because the Web SDK transforms that file for the chosen profile after payload computation. Preserve `INSTALLATION_COMPLETE` and every installer-selected companion file.

The production startup path never calls the installer. A successful publishing-machine check does not replace clean Windows/IIS qualification with the actual pool identity. See `../README.md` for the current workflow and `windows-acceptance.md` for the release gate.

### 8.3 Paths and startup

Example deployment layout:

| Location | Access and lifecycle |
|---|---|
| `C:\Sites\Pressroom` | Application release; app identity reads/executes; deployment identity replaces it |
| `C:\ProgramData\Pressroom\pressroom.settings.json` | External settings/secrets; app identity reads; restricted administration writes |
| `C:\ProgramData\Pressroom\temp\<instance-id>` | Instance-specific working area; app identity can create/modify/delete |
| `C:\ProgramData\Pressroom\logs` | Bounded metadata logs; survives code replacement |

Grant ACLs per directory, not blanket modification of the entire release or configuration tree. Resolve application resources using `AppContext.BaseDirectory`. Resolve and validate the runtime root explicitly; do not assume the current working directory or an interactive user's profile.

Before `Playwright.CreateAsync()`, the application supplies process-scoped Playwright and temporary-directory settings from its file-based configuration. These internal environment variables are required by the driver and are not operator configuration. The Node driver creates temporary directories before launching the browser, so setting only browser launch environment options is too late. [Launch implementation][pw-launch-source].

Startup checks the driver/browser/font payload, effective paths and permissions, configured native prerequisites, and an actual render before reporting ready. Implement a documented `--check` mode that performs the same preflight and exits with a useful code without installing anything. Stale-folder cleanup must distinguish previous dead instances from another still-running IIS worker.

### 8.4 IIS settings

| Setting | Baseline |
|---|---|
| Application pool | Dedicated to Pressroom |
| .NET CLR version | No Managed Code |
| Enable 32-bit Applications | False |
| Maximum Worker Processes | 1 |
| Start Mode | AlwaysRunning |
| Application Preload | Enabled |
| Application Initialization | Installed/enabled; warm-up can request `/v1/health` |
| Idle Time-out | 0 for the warm renderer |
| Load User Profile | True, alongside explicit writable paths |
| Authentication | Anonymous enabled, Windows disabled; application enforces the configured access mode |

These settings complement application startup/warm-up. [IIS advanced configuration][iis-advanced]; [process model][iis-process].

Default overlapped recycling can temporarily run old and new workers, each with its own browser and queue, even with one normal worker process. Budget memory for that overlap and use unique temporary directories. A deployment that requires strictly one instance can disable overlap and accept the restart interruption. [IIS recycling][iis-recycling].

Configure IIS request filtering and ASP.NET Core server body ceilings above the application cap; initial outer ceiling: 33,554,432 bytes (32 MiB), with the 25 MiB application cap enforced before deserialization. Apply the server limit to IISServerOptions for in-process hosting and Kestrel for standalone hosting. Raising the application cap requires reviewing both outer settings. Requests exceeding an outer ceiling may receive an IIS-generated response rather than Pressroom's 413 problem details. [IIS request limits][iis-request-limits].

### 8.5 Updating and rolling back

1. Stage and verify the complete new release before touching the active directory. Retain the previous release for rollback.
2. Stop the dedicated pool, or place `app_offline.htm` at the site root. Wait for shutdown and browser/driver file locks to clear.
3. Copy only into the replaceable application directory. `robocopy /MIR` is suitable when the destination contains only release files; keep configuration, logs, and temporary data outside it.
4. If using the offline file, explicitly exclude/preserve it during the mirror so it remains until copying completes. Interpret Robocopy exit codes correctly: 0–7 are nonfailure statuses; 8 or above is failure.
5. On copy failure, leave the application stopped/offline. Repair the copy or restore the previous complete release before restarting.
6. Start the pool or remove the offline file. Request readiness, verify the expected release version, and perform an authenticated smoke render.
7. Roll back by stopping, restoring the previous complete release, and rerunning those checks. Browser, driver, and application versions roll back together.

This is a brief maintenance deployment, not zero-downtime live overwrite. No database migration is involved in the stateless baseline. Sources: [IIS deployment][iis-hosting], [ANCM offline handling][ancm], and [Robocopy][robocopy].

## 9. Client library and caller integration

`IPdfRenderer.RenderAsync(string html, RenderOptions options, CancellationToken)` returns a disposable `PdfRenderResult` with the PDF stream, page count, filename, disposition, and render duration. `PressroomClient` implements this HTTP interface and also offers a `RenderRequest` convenience overload returning buffered bytes. Use a request-object overload to select `Disposition = PdfDisposition.Inline` or `PdfDisposition.Attachment` per request. The caller supplies a reusable HTTP client with a Pressroom base URL and API key from its configuration file; omit the key when using anonymous mode. The HTTP client project has no Playwright dependency.

For a server-to-server call, the calling application must set its own browser response's `Content-Disposition` from the selected/returned disposition and sanitized filename. Returning only the PDF bytes does not forward Pressroom's HTTP headers. Use typed framework header/file APIs; preserve the per-request display/download choice through the caller's response.

The caller owns its total deadline and retry policy. This implementation does not automatically retry; 429/503 responses retain `Retry-After` metadata so an explicit caller policy can choose bounded retries. A caller may instead deliver its HTML when a Pressroom URL is absent or Pressroom is unavailable. A render response/connection failure can drive that decision without a health probe before every call. Do not automatically retry validation/readiness failures or an overall render timeout.

Pressroom itself remains usable on localhost for development and exposes only its rendering API/readiness behavior. Local rendering is a separate explicit use of `Pressroom.Rendering`, with its own browser setup; it is not an automatic fallback in the HTTP client or server.

The calling application remains responsible for its templates, access checks, localization, and HTML fallback. Letter is the default page size; A4 and other sizes remain available per request.

## 10. Tests and acceptance

### Contract and renderer

- Unit coverage: validation, option mapping, body/deadline enforcement, key comparison and policy selection, admission release, and lifecycle state transitions.
- Real Chromium: page count, headers/footers, page sizes, breaks, tags and outlines, embedded images/fonts, packaged fonts, and both print/screen media.
- Plain static HTML succeeds without a readiness flag. Explicit readiness failure returns 422 within its budget; overall timeout returns 504 and actually releases browser resources.
- Exercise HTTP(S), local-file, WebSocket, service-worker, frame/popup, and CSS fetch attempts; confirm no document-controlled URL or console text reaches logs.
- Verify caller disconnect cleanup, planned recycling during concurrent renders, browser crash/recovery, and queue-full 429 without marking a healthy browser unavailable.

### Deployment acceptance gate

Unpack the actual release into a new location on a clean supported Windows Server with IIS and the .NET 10 Hosting Bundle, using the intended IIS identity. There must be no developer browser cache, separately installed Node/npm, .NET SDK, PowerShell 7 runtime dependency, or external download access needed by the application. Start qualification with Media Foundation absent and without relying on a machine-wide VC++ runtime beyond components already supplied by the stated baseline. Include any required app-local redistributables in the artifact. Record the exact machine inventory so a preconfigured build server cannot hide a prerequisite.

Run with normal Playwright validation enabled. If it alone rejects optional multimedia dependencies, investigate direct browser startup and the real PDF workload; a validation bypass is diagnostic evidence only, and any change to the standard release policy must be explicit. Do not count either a suppressed warning or a static dependency scan as a successful render. Repeat this qualification when the pinned browser changes.

Verify cold start and warm-up, file permissions, an authenticated PDF render with embedded and packaged assets, header/footer fonts, app-pool recycling, child-process cleanup, a stop/copy/start update, and rollback. Run from the published files; never substitute `dotnet run` for this gate. Repeat against the self-contained profile if that profile is released.

Exercise both configured access modes through real IIS: API-key mode accepts valid keys and rejects missing, invalid, empty, and duplicate keys; anonymous mode accepts requests without a key and rejects supplied invalid keys. Confirm that host credentials cannot bypass API-key mode and that no Negotiate/NTLM challenges occur. Check complete JSON sizes near both application and IIS ceilings.

Soak with at least a thousand representative renders at configured concurrency, including recycling, and establish bounded memory/process growth and peak memory. Record environment, package/browser versions, font inventory, limits, and results. Acceptance is not claimed until this gate runs successfully.

Functional acceptance: the supplied bilingual sample renders as two Letter portrait pages with page numbers and correctly displayed English and French text.

## 11. Later

Asynchronous batches/callbacks, PDF/A, signatures, merging, and a controlled asset-host allow-list remain future work. Word output through LibreOffice would introduce a separate packaging and operational decision; it is not part of v1's deployment promise.

## 12. Decisions and implementation checkpoints

- Pressroom is the product name. Chromium via Playwright remains the engine behind an implementation-independent contract.
- IIS folder deployment is the primary operational target. The installed .NET 10 runtime is used by default; a separate self-contained profile is optional.
- The release owns its matching driver, browser, and approved fonts. Server font installation and system Chrome/Edge are not baseline dependencies.
- Prefer the stock headless shell, bundle any required redistributable DLLs, and test without Media Foundation before adding a Windows media-feature prerequisite. Keep Playwright's normal validation enabled; the current static findings do not establish a need for an override.
- The service remains synchronous and stateless. Browser/application restarts do not create a need for a durable queue.
- External resource fetching stays outside v1. Strict OS egress isolation, if required by the environment, is separately provisioned and verified.
- Before the first release, fix the supported Windows baseline, pin dependencies, select redistributable fonts, prove header/footer font behavior, and run the clean-IIS acceptance gate.

### Changes from the original design

This revision adds the release/package layout, server prerequisites, runtime paths, IIS settings, deployment/rollback procedure, and clean-server acceptance gate. It also changes default readiness to `null`, uses an explicit initial concurrency of 2, defines the byte limit over the complete JSON body, adds overall-timeout behavior, separates saturation from readiness, coordinates browser recycling, and narrows the original no-network/no-disk claims to guarantees the implementation can substantiate.

## 13. Native dependency investigation, 2026-10-23

Static inspection covered the Windows browser and companion native tools corresponding to a candidate Microsoft.Playwright 1.64.1 release. Its [version properties][pw-version-props] identify driver 1.64.1 and Node 24.22.1; the driver [browser metadata][pw-browser-metadata] selects Chromium/Chrome Headless Shell 156.0.8078.4 (revision 1248), FFmpeg 1013, and Winldd 1007. The initial project pins Microsoft.Playwright 1.64.1. This static evidence is not a completed Windows/IIS test.

The Playwright Chromium CDN download was unavailable in the research environment, so the corresponding exact-version headless archive from Google's official Chrome for Testing distribution was inspected. This is not a claim that a downloaded Playwright CDN archive was independently compared byte for byte. Node came from its official versioned Windows archive; FFmpeg and Winldd came from Microsoft's Playwright distribution.

| Downloaded archive | Bytes | SHA-256 |
|---|---:|---|
| [Headless Shell 156.0.8078.4][inspected-headless-archive] | 123826519 | `5af4c82cf4b351ec4bad4c71dcf938af14fccea8a0eb0d5c2a7b49ccc922698c` |
| [Node 24.22.1 x64][inspected-node-archive] | 37748064 | `d2046d28c2c3dcd211ac5145c0bf30e2047ef9dfb306693c8c6e2eccd8b95fa5` |
| [FFmpeg 1013][inspected-ffmpeg-archive] | 1663577 | `0c4ee6c694e9bffea27128ab543ae573466c5245d7d16c054e40115d7d743874` |
| [Winldd 1007][inspected-winldd-archive] | 128684 | `0069f0d11d4ad6df068a068c003d22fe7dbec192a47bba64b2e115e9c8ce41d8` |

- Method: inspect PE normal and delay-import tables for all eight EXE/DLL files; cross-check normal imports with `objdump -p`.
- `chrome-headless-shell.exe` startup imports: `VERSION.dll`, `WINMM.dll`, `WS2_32.dll`, `KERNEL32.dll`, `CRYPT32.dll`, `ntdll.dll`, and `DWrite.dll`.
- `MF.dll`, `MFPlat.dll`, and `MFReadWrite.dll` appear only in that executable's delay-import table.
- No modern `vcruntime*`, `msvcp*`, or UCRT imports were present in the normal/delay tables of these eight files. FFmpeg imports Windows' `msvcrt.dll`, which is a different runtime.

| Inspected file | Relevant native dependency finding |
|---|---|
| `chrome-headless-shell.exe` | Media Foundation references are delayed; no modern VC runtime imports |
| `libEGL.dll` | No Media Foundation or modern VC runtime imports |
| `libGLESv2.dll` | No Media Foundation or modern VC runtime imports |
| `vk_swiftshader.dll` | No Media Foundation or modern VC runtime imports |
| `vulkan-1.dll` | No Media Foundation or modern VC runtime imports |
| `node.exe` | No Media Foundation or modern VC runtime imports |
| `ffmpeg-win64.exe` | No Media Foundation or modern VC runtime imports; retain this companion file |
| `PrintDeps.exe` | No Media Foundation or modern VC runtime imports |

Dynamic library loads and transitive OS dependencies are not exhaustively established by these tables. The finding is scoped to these eight native files, not every .NET runtime component or future dependency. Actual cold launch, PDF output, and repeated operation without Media Foundation still require the clean-Windows gate in section 10. No Windows executable was run in this investigation.

## References

Primary documentation and implementation sources checked on 2026-10-23. Version-sensitive behavior must be rechecked against the versions pinned for release.

- [Playwright browser installation and packaging][pw-browsers]
- [Playwright supported systems][pw-requirements]
- [Playwright .NET driver][pw-driver] and [publish targets][pw-targets]
- [Playwright native dependency checks][pw-native-deps] and [browser launch implementation][pw-launch-source]
- [Browser launch options][pw-launch], [browser contexts/routing][pw-context], [browser options][pw-browser], [PDF API][pw-pdf]
- [CSS Fonts][css-fonts], [CSS Font Loading][css-font-loading], [HTML base element][html-base]
- [.NET publishing][dotnet-publishing], [runtime selection][runtime-selection], [Task.WaitAsync][task-wait]
- [IIS hosting][iis-hosting], [Hosting Bundle][hosting-bundle], [in-process hosting][iis-inprocess], [ANCM][ancm]
- [Automatic authentication][iis-auto-auth], [request limits][iis-request-limits]
- [IIS advanced configuration][iis-advanced], [process model][iis-process], [recycling][iis-recycling], [Robocopy][robocopy]
- [VC++ redistribution][vc-redist], [Universal CRT][ucrt], [Windows DLL loading][windows-dll-loading]
- [Chromium media build][chromium-media-build], [media initialization][chromium-mf-init], [Playwright registry][pw-registry], [validation override][pw-validation-override]
- [Pinned Playwright version mapping][pw-version-props], [browser metadata][pw-browser-metadata], [Windows dependency scanner][pw-winldd]

[pw-browsers]: https://playwright.dev/dotnet/docs/browsers
[pw-requirements]: https://playwright.dev/dotnet/docs/intro
[pw-driver]: https://github.com/microsoft/playwright-dotnet/blob/main/src/Playwright/Helpers/Driver.cs
[pw-targets]: https://github.com/microsoft/playwright-dotnet/blob/main/src/Playwright/build/Microsoft.Playwright.targets
[pw-native-deps]: https://github.com/microsoft/playwright/blob/main/packages/playwright-core/src/server/registry/dependencies.ts
[pw-launch-source]: https://github.com/microsoft/playwright/blob/main/packages/playwright-core/src/server/browserType.ts
[pw-launch]: https://playwright.dev/dotnet/docs/api/class-browsertype
[pw-context]: https://playwright.dev/dotnet/docs/api/class-browsercontext
[pw-browser]: https://playwright.dev/dotnet/docs/api/class-browser
[pw-pdf]: https://playwright.dev/dotnet/docs/api/class-page#page-pdf
[css-fonts]: https://www.w3.org/TR/css-fonts-4/#font-face-rule
[css-font-loading]: https://www.w3.org/TR/css-font-loading/#font-face-set-ready
[html-base]: https://html.spec.whatwg.org/multipage/semantics.html#the-base-element
[dotnet-publishing]: https://learn.microsoft.com/en-us/dotnet/core/deploying/
[runtime-selection]: https://learn.microsoft.com/en-us/dotnet/core/versions/selection
[task-wait]: https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0
[iis-hosting]: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0
[hosting-bundle]: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0
[iis-inprocess]: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/in-process-hosting?view=aspnetcore-10.0
[ancm]: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/aspnet-core-module?view=aspnetcore-10.0
[iis-auto-auth]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.builder.iisserveroptions.automaticauthentication?view=aspnetcore-10.0
[iis-request-limits]: https://learn.microsoft.com/en-us/iis/configuration/system.webServer/security/requestFiltering/requestLimits/
[iis-advanced]: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/advanced?view=aspnetcore-10.0
[iis-process]: https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/processmodel/
[iis-recycling]: https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/recycling/
[robocopy]: https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy
[vc-redist]: https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170
[ucrt]: https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment?view=msvc-170
[windows-dll-loading]: https://learn.microsoft.com/en-us/windows/win32/dlls/load-time-dynamic-linking
[chromium-media-build]: https://chromium.googlesource.com/chromium/src/media/+/0574dcca29a22648921b44935d7e6c3ed3a397e3/base/win/BUILD.gn
[chromium-mf-init]: https://chromium.googlesource.com/chromium/src/media/+/0574dcca29a22648921b44935d7e6c3ed3a397e3/base/win/mf_initializer.cc
[pw-registry]: https://github.com/microsoft/playwright/blob/main/packages/playwright-core/src/server/registry/index.ts
[pw-validation-override]: https://github.com/microsoft/playwright/issues/38334
[inspected-headless-archive]: https://storage.googleapis.com/chrome-for-testing-public/156.0.8078.4/win64/chrome-headless-shell-win64.zip
[inspected-node-archive]: https://nodejs.org/dist/v24.22.1/node-v24.22.1-win-x64.zip
[inspected-ffmpeg-archive]: https://playwright.download.prss.microsoft.com/dbazure/download/playwright/builds/ffmpeg/1013/ffmpeg-win64.zip
[inspected-winldd-archive]: https://playwright.download.prss.microsoft.com/dbazure/download/playwright/builds/winldd/1007/winldd-win64.zip
[pw-version-props]: https://github.com/microsoft/playwright-dotnet/blob/v1.64.1/src/Common/Version.props
[pw-browser-metadata]: https://github.com/microsoft/playwright/blob/v1.64.1/packages/playwright-core/browsers.json
[pw-winldd]: https://github.com/microsoft/playwright/blob/v1.64.1/browser_patches/winldd/PrintDeps.cpp
