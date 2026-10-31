<h1 align="center">Pressroom</h1>

<p align="center">
  HTML in, PDF out. An HTML-to-PDF service for Windows, IIS, Linux, and containers.<br>
  An application <code>POST</code>s one complete HTML document; Pressroom returns print-quality PDF bytes,
  or sends the pages straight to an allowlisted Windows or Linux printer.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="docs/images/badge-license.svg"></a>
  <img alt=".NET 10" src="docs/images/badge-dotnet.svg">
  <img alt="Windows and IIS supported" src="docs/images/badge-windows.svg">
  <img alt="Renderer: Chromium headless shell" src="docs/images/badge-renderer.svg">
  <img alt="Output: PDF or printer" src="docs/images/badge-output.svg">
  <img alt="Client: .NET, HTTP-only" src="docs/images/badge-client.svg">
  <a href="docs/container.md"><img alt="Containers: Linux and Windows x64" src="https://img.shields.io/badge/containers-linux%20%7C%20windows--x64-245c78"></a>
  <a href="https://github.com/auroris/Pressroom/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/auroris/Pressroom?label=release&color=2ea44f"></a>
</p>

![A render request on the left, its PDF response headers below it, and the two rendered Letter pages of the bilingual sample document on the right](docs/images/hero.png)

<p align="center">
  <strong><a href="https://github.com/auroris/Pressroom/releases/latest">⬇ Download the latest release</a></strong>
</p>

## What it is

Pressroom is a standalone .NET 10 service that turns a complete HTML document into
a PDF. A release is **one folder**: the server, its private Playwright driver and
Node runtime, the matching Chromium headless shell, and the Noto Sans fonts, all
pinned together with a manifest of file hashes. Copy it into an IIS application and
it runs. The server needs the .NET 10 Hosting Bundle and nothing else: no Node, no
Chrome, no font installation, no downloads at run time.

- **One request, one PDF.** Send `html`, get `application/pdf` with the page count
  and render time in the headers. Choose inline display or attachment per request.
- **Paper-aware output.** Named and custom page sizes, margins, scale, print or
  screen media, running headers and footers with page numbers, tagged PDF, and a
  document outline built from your headings.
- **Predictable typography.** Noto Sans in four faces is packaged and injected into
  every document; embed anything else as a `data:` font.
- **Isolated renders.** Documents are self-contained by rule: only `data:`
  resources load, network and file access are blocked before parsing, scripts are
  off unless the caller's key opts in, and every render gets a fresh sandboxed
  browser context.
- **Print on Windows or Linux.** Add a `printer` identifier and Pressroom hands the
  pages to an allowlisted queue through SumatraPDF or CUPS, returning a submission receipt.
- **Simple access control.** Named API keys with per-key JavaScript permission, or
  explicit anonymous mode for a trusted network. Health stays anonymous for IIS
  warm-up and probes.
- **Built for operations.** Stateless, bounded queue with `Retry-After`, one
  end-to-end deadline per request, planned browser recycling, metadata-only logs,
  an offline `--check` self-test, and stop-copy-start upgrades with folder rollback.
- **A .NET client without the browser.** `Pressroom.Client` is HTTP-only, so
  calling applications never take a Playwright dependency.
- **Opt-in batch jobs.** Submit, poll, download or cancel durable jobs. Per-key
  scheduling reserves capacity for interactive requests; local retention is bounded.
- **Linux and containers.** Sandboxed Linux x64 packages, a non-root container,
  and external JSON configuration. See [Linux acceptance](docs/linux-acceptance.md)
  and optional network isolation in [containers](docs/container.md).

## Choose a deployment

- **Internet-facing use:** use the [Linux Docker image with the documented iptables
  isolation](docs/container.md#optional-linux-outbound-isolation), API-key authentication,
  and an HTTPS reverse proxy in front of the loopback-bound API. A
  [direct Linux installation](docs/linux.md) is also suitable when it runs under
  a dedicated unprivileged user; use API keys and HTTPS there too.
- **Intranet use:** Windows/IIS installations and Windows containers are intended
  primarily for networks where Pressroom is not exposed to internet traffic.
  Both Linux deployment options also work on intranets.

Network isolation remains an optional deployment setting; enable it for the
recommended internet-facing Docker setup. See the [security model](docs/security.md).

## Quick start

This example uses a direct Windows installation for an intranet.

Download and verify the latest release ZIP, then extract the whole archive into a
folder such as `C:\Sites\Pressroom`. Install the
[.NET 10 Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0)
if the server does not already have it.

Create `C:\ProgramData\Pressroom\pressroom.settings.json` with at least one API key
(start from [`samples/pressroom.settings.example.json`](samples/pressroom.settings.example.json)):

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

Check the package, then run it:

```powershell
& 'C:\Sites\Pressroom\Pressroom.Server.exe' --check
& 'C:\Sites\Pressroom\Pressroom.Server.exe' --urls http://localhost:5177
```

Render the bundled sample:

```sh
curl -sS -X POST http://localhost:5177/v1/render \
  -H "Content-Type: application/json" -H "X-Api-Key: REPLACE_WITH_YOUR_SECRET" \
  --data-binary @samples/orders-bilingual.request.json -o orders.pdf
```

Or from .NET 8 or .NET 10:

```sh
dotnet add package Pressroom.Client
```

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
```

The [Getting started guide](docs/getting-started.md) walks through each step, and
the [Deployment guide](docs/deployment.md) covers the production IIS setup.

## Documentation

| Guide | Read it for |
|---|---|
| [Getting started](docs/getting-started.md) | From release ZIP to first PDF: extract, configure a key, check, run, render with curl, PowerShell, or .NET. |
| [Authoring documents](docs/documents.md) | Self-contained HTML, packaged fonts, page sizes and breaks, headers and footers, accessibility, JavaScript and readiness, troubleshooting. |
| [API reference](docs/api.md) | Every endpoint, request option with its rules and defaults, response headers, status codes, deadlines, and capacity. |
| [.NET client](docs/client.md) | `PressroomClient`: buffered and streaming renders, returning PDFs to browsers, printing, errors, retries, and fallback. |
| [Configuration](docs/configuration.md) | The external settings file, access modes and API keys, JavaScript permission, limits, paths, logging, and upgrade notes. |
| [Printing](docs/printing.md) | Windows/SumatraPDF and Linux/CUPS setup, the printer allowlist, receipts, and local testing. |
| [Deployment](docs/deployment.md) | Server prerequisites, release contents, directories and permissions, IIS settings, first start, updating, and rollback. |
| [Linux](docs/linux.md) | Shared libraries, fonts, user namespaces, systemd, tarball packaging and rollback. |
| [Containers](docs/container.md) | Sandboxed non-root image, Compose, Kubernetes, volumes, health and image verification. |
| [Linux acceptance](docs/linux-acceptance.md) | Ubuntu VM, container, load, recovery, and network-policy verification. |
| [Security model](docs/security.md) | Trust boundaries, document isolation controls, authentication, retention, resource protection, and what is not guaranteed. |
| [Windows / IIS acceptance](docs/windows-acceptance.md) | The checklist shipped with each release for qualifying a target server. |
| [Developing](docs/developing.md) | Repository layout, build and test, publishing from Visual Studio, the GitHub release workflow, and the design history. |

## Developing

```sh
dotnet restore Pressroom.slnx --locked-mode
dotnet build Pressroom.slnx -c Release --no-restore
dotnet test --solution Pressroom.slnx -c Release
```

The prerequisites are the .NET 10 SDK and, for publishing, a Visual Studio release
that supports .NET 10 on Windows x64. Publishing the `IisFolder` profile acquires
the matching Chromium, runs the real renderer through a PDF warm-up, and writes the
release manifest; ordinary builds and tests never download browsers. Releases are
cut by a manual GitHub Actions workflow. See [Developing](docs/developing.md).

## License

[MIT](LICENSE). Redistributed components and their licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
