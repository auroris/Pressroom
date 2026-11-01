# Developing

Build Pressroom from the solution, publish its renderer and dependencies as a
release folder, and package that output for Windows, Linux or containers. The
administrator guides in [`docs/`](../getting-started.md) cover installation and
operation and ship inside every release.

## Prerequisites

- The .NET 10 SDK (`global.json` pins 10.0.100 with latest-feature roll-forward).
- The .NET 8 runtime, for the second client test target. Without it, `dotnet test`
  reports the net8.0 contracts project as a launch failure; pass `-f net10.0` to
  run the rest.
- A Visual Studio release that supports .NET 10, for the publish profiles.
- Windows x64 for IIS publishing, or Linux x64 with PowerShell 7 and the
  [documented libraries](../linux.md) for Linux publishing. Real-browser tests need
  the browser payload and sandbox support.

## Repository layout

| Path | Contents |
|---|---|
| `src/Pressroom.Server` | ASP.NET Core minimal API host: endpoints, authentication, admission, jobs, printing, publish targets, IIS `web.config` |
| `src/Pressroom.Rendering` | Browser supervisor, document validation, sandbox policy, font catalog, packaged Noto Sans fonts |
| `src/Pressroom.Contracts` | Request and option records, `IPdfRenderer`, `RequestValidator`, JSON defaults |
| `src/Pressroom.Client` | `PressroomClient`, the HTTP implementation of `IPdfRenderer` |
| `tests/` | Contract and client tests, renderer and sandbox tests, server configuration, jobs, printing, and HTTP tests |
| `build/` | PowerShell helpers invoked by MSBuild, the workflows, and container builds |
| `deploy/` | Compose, Kubernetes, seccomp profile, and Windows container helpers, shipped with each release |
| `samples/` | The bilingual sample document, its ready-to-send request, and the example settings file |
| `docs/` | Administrator guides, shipped with each release |
| `docs/development/` | This guide, the API reference, the authoring and client guides, and design history |
| `.github/workflows/` | The manual `Packages` and `Container images` workflows |

## Documentation rules

`docs/*.md` is for system administrators installing or operating Pressroom in the
four supported configurations: Windows/IIS, Linux, and the Linux and Windows
containers. Keep their documentation links within the shipped administrator guides.
The `docs/development` folder stays in the repository. Guides under
`docs/development` may link to each other and up to the administrator guides.
Application-developer material (the [API reference](api.md), the
[authoring guide](documents.md), and the [.NET client guide](client.md)) lives here.

## Build and test

The solution restores NuGet dependencies from the project files without package
lock files. From the solution directory:

```sh
dotnet restore Pressroom.slnx
dotnet build Pressroom.slnx -c Release --no-restore
dotnet test --solution Pressroom.slnx -c Release
```

Unit and API tests use controlled renderer substitutes and run anywhere, including
on a host that has Pressroom installed: the test host selects its own settings file.
The real-browser tests need the browser
payload (publish once, see below) and writable temporary state; enable them with
`PRESSROOM_RUN_BROWSER_TESTS=1`, `PRESSROOM_TEST_SANDBOX=true`, and
`PRESSROOM_TEST_BROWSERS_PATH` pointing at a published `browsers` folder. Publishing
acquires that browser payload.

### Windows firewall prompts during browser tests

Sandbox tests intentionally exercise local TCP, UDP, and WebRTC sockets. Windows
may prompt for a newly copied `chrome-headless-shell.exe` at each executable path.
Choose Cancel or Block to retain inbound blocking. Reuse the same
browser bundle path for repeated tests. To prevent a first-use prompt, run this
optional helper in an administrator PowerShell before testing that browser copy:

```powershell
./build/Block-TestBrowserInbound.ps1 -BrowserExecutable 'D:\path\to\chrome-headless-shell.exe'
```

Run the helper explicitly to add an inbound block rule for that exact executable
on every profile. A new browser path needs a separate rule. See Microsoft's
[firewall rule behavior](https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/rules).

### Longer jobs acceptance run

Enable the browser tests as above and set `PRESSROOM_JOB_ACCEPTANCE_COUNT=500`.
Set `PRESSROOM_JOB_METRICS_PATH` to save successful sync-request p95 latency and
planned-recycle 503 counts. The test requires a p95 ratio of at most 2 and rejects
sync 429s; only its load generator handles the planned-recycle response. The
public client does not retry renders.

## Running locally

Publish the `IisFolder` profile once to acquire and verify the matching browser,
then run the published executable with a local settings file:

```powershell
& '.\artifacts\publish\IisFolder\Pressroom.Server.exe' --config 'C:\PressroomConfig\pressroom.settings.json' --urls http://localhost:5177
```

Point `Paths:StateRoot` at a writable folder outside the release and configure
either a key or explicit anonymous mode. `--check` runs the preflight and exits.
The application and publish helper supply Playwright's internal process settings.

For cross-platform implementation work, a development-only publish can skip the
browser bundle with `-p:PressroomBundleBrowser=false`. That output lacks the
browser bundle and check and is not a complete release.

## Publishing from Visual Studio

Open `Pressroom.slnx`, select **Pressroom.Server → Publish**, choose **IisFolder**,
and click **Publish**. The default output is `artifacts\publish\IisFolder`. The
profile is `net10.0`, `win-x64`, framework-dependent, with single-file, trimming,
and AOT disabled. **IisFolderSelfContained** also includes the .NET runtime; it is
not part of the published releases.

Publish automatically:

1. Builds the Windows x64 application with the pinned package versions.
2. Runs the package's private Playwright driver to install the matching Chromium
   headless shell into the publish payload. The helper uses Windows PowerShell 5.1
   and the package's private Node runtime.
3. Adds the complete browser tree, installer-selected companion files, fonts,
   managed package notices, the administrator guides, the `deploy` folder, and the
   private driver to the publish file list, using the Web SDK's
   [publish file list](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/visual-studio-publish-profiles?view=aspnetcore-10.0#include-files).
4. Makes an isolated local copy and runs `Pressroom.Server.exe --check` against it,
   with the sandbox enabled and a real PDF warm-up, using temporary state and no
   machine secrets.
5. Fails Publish if that check fails. On success, removes the build machine's
   `DEPENDENCIES_VALIDATED` cache markers and writes `release-manifest.json` with
   versions, font inventory, and SHA-256 hashes.

The first publish needs internet access to restore packages and acquire the
browser; later publishes reuse the cache. MSBuild invokes `build/Publish-Pressroom.ps1`
as part of publishing.

### Web Deploy profiles

Your usual Visual Studio **IIS / Web Deploy** profile works for `Pressroom.Server`:
select **Release**, **net10.0**, **win-x64**, framework-dependent, with single-file,
trimming, and AOT disabled. The project's publish targets add the browser assets to
that profile's payload. Keep credentials in Visual Studio's user settings rather
than a committed `.pubxml`.

`EnableMsDeployAppOffline` defaults to `true`, allowing the ASP.NET Core module to
stop the application before Web Deploy replaces locked files. For a Folder profile
pointed directly at an IIS directory, stop the pool before copying and start it
afterwards; a local staging folder is the default. Keep a complete previous
publish folder for rollback.

The build-machine check verifies launch and rendering on the publishing machine.
Qualify the deployed release on its target server with the
[acceptance checklist](../windows-acceptance.md).

## Building the Linux package

Build as non-root on Linux x64 with the .NET 10 SDK, PowerShell 7, and the
libraries listed in the [Linux guide](../linux.md):

```sh
pwsh ./build/Publish-Linux.ps1 -Version 1.1.0 -OutputDirectory ./artifacts/linux-publish
pwsh ./build/Package-Pressroom.ps1 -PublishDirectory ./artifacts/linux-publish \
  -OutputDirectory ./artifacts/linux-release -Version 1.1.0 \
  -Variant framework-dependent -RuntimeIdentifier linux-x64
```

The shared helper installs the pinned browser, stages the payload, performs a
real sandboxed check, removes dependency-validation cache markers, and writes
the manifest. Install the host libraries before running it. The tarball preserves the
executable modes that the Linux guide relies on.

## Building and verifying container images

On a prepared Linux Docker host with PowerShell 7:

```sh
pwsh ./build/Build-Container.ps1 -Version 1.1.0 -Image pressroom:local
```

Use a fresh `artifacts/container-publish` directory. The builder performs locked
restore, tests, and a complete publish as UID 10001 with the seccomp profile. The
runtime image is then built from that verified payload. Publishing in a separate
container applies the seccomp profile needed for the sandboxed check; Docker build
`RUN` steps cannot use that runtime profile. Give the builder network access to
acquire dependencies.

The final image passes an offline sandboxed check with networking disabled and
`build/Test-Container.ps1`: API readiness, authenticated bilingual output,
invalid-key rejection, no printers, active-job SIGTERM, and loss of temporary
state on restart. Add `-TestNetworkIsolation` to the build or test helper after
preparing the iptables rules from the [container guide](../container.md) to test
outbound blocking with a reachable host canary. The build helpers do not install
Linux firewall rules; CI prepares its disposable host and runs that extra check.

On a Windows Docker host with PowerShell 7 and the .NET 10 SDK:

```powershell
./build/Build-WindowsContainer.ps1 -Version 1.1.0 -Image pressroom:windows-local
```

This publishes the Windows payload, builds `Dockerfile.windows` on the full
`windows/server:ltsc2022` base (Server Core does not pass the sandboxed browser
check), and runs `build/Test-WindowsContainer.ps1`. `-TestNetworkIsolation`
installs and checks the optional HNS policy and needs an administrator session.

### Playwright version selection

Every build helper takes `-PlaywrightVersion`. The default, `pinned`, is the
repository's `PressroomPlaywrightVersion` property. `latest` is an explicit opt-in
that resolves the newest stable release from NuGet, and an explicit `<version>`
selects a recorded one. These overrides apply during normal NuGet restore without
generating or enforcing package lock files. The Dockerfile's apt package list
follows the pinned Playwright's Chromium dependency list; review it when changing
the pin.

## Manual package builds and GitHub releases

The [Packages workflow](../../.github/workflows/release.yml) has only a
`workflow_dispatch` trigger. Open **Actions → Packages → Run workflow**, select
the branch and enter a version without `v`, such as `1.2.3` or `1.2.3-rc.1`.
Three jobs run in parallel after input validation:

| GitHub artifact | Contents |
|---|---|
| `nuget-packages` | `AurorisNet.Pressroom.Client`, bundling the client and contracts assemblies, as `.nupkg` and `.snupkg` with SHA-256 files |
| `windows-install-packages` | `Pressroom-<version>-win-x64-framework-dependent.zip` and its SHA-256 file, for IIS or local Windows hosting |
| `linux-install-packages` | `Pressroom-<version>-linux-x64-framework-dependent.tar.gz`, its SHA-256 file, and the verification image's SBOM |

The Windows and Linux packages differ: they contain native apphosts, private
Node/Playwright drivers and Chromium for their respective platforms. Both include
fonts, the administrator guides, the `deploy` folder, and a file-hash manifest, and
require the .NET 10 ASP.NET Core runtime. IIS also needs the Hosting Bundle. Linux
needs the documented native libraries and sandbox policy, and runs as an
unprivileged user. Docker builds and verifies the Linux payload; operators install
the resulting tarball directly on the host. Both installer builds use the pinned Playwright
version, so one release carries one Chromium build and can be rebuilt from its
commit; the workflow fails if either package manifest records a different version.

The workflow tests both client target frameworks and compiles fresh consumers
against the actual packages. Installer jobs require the sandboxed browser checks;
the Linux job also verifies network isolation and passes the critical-CVE gate.
Artifacts remain downloadable from the GitHub run for 14 days.

Optionally enable **create_release** to attach all verified files to a GitHub
release after every build passes. **draft** and **prerelease** apply to that release;
versions with a suffix are marked prerelease automatically. Only this optional job
has repository write permission. It verifies all package checksums and creates
`v<version>` at the selected commit; existing tags and releases are never overwritten.
Use an unused version when creating a release. If an interrupted upload leaves a
tag or draft, inspect it before retrying. A run that only builds artifacts can reuse a version.

Optionally enable **publish_nuget** to publish `AurorisNet.Pressroom.Client` and its
symbols to NuGet.org after all three build jobs pass. This checkbox defaults to off
and is independent of **create_release**; **draft** does not defer a selected NuGet
publication. Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
the publishing job downloads the verified artifact, checks its hashes, then uses
GitHub OIDC through the pinned `NuGet/login` action to obtain a temporary API key
immediately before pushing the package and symbols. Only that job has
`id-token: write`; no stored NuGet API-key secret is used and no rebuild occurs.

Set up the account and repository once before selecting **publish_nuget**:

1. Sign in to NuGet.org as `AurorisNet`, open **Trusted Publishing** from the
   profile menu, and add a GitHub policy for the intended package owner. Use
   `AurorisNet` as the policy owner when publishing under that personal profile.
2. Enter the policy values below. The workflow field takes only the filename.
3. In GitHub **Settings → Secrets and variables → Actions → Variables**, set the
   repository variable `NUGET_USER` to `AurorisNet` (the NuGet profile username).
   A missing variable fails workflow input validation; the login step validates
   the NuGet policy when publishing.

| NuGet policy field | Value |
|---|---|
| Repository owner | `auroris` |
| Repository | `Pressroom` |
| Workflow file | `release.yml` |
| Environment | Leave blank; this job does not use a GitHub environment |
| Package glob | `AurorisNet.Pressroom.Client` |
| Scopes | Push new packages and new package versions |

Use a version not already published to NuGet.org; duplicates fail. If a push is
interrupted, inspect the package and symbols on NuGet.org before retrying. A
version suffix controls NuGet prerelease status.

Bump `Version` in `Directory.Build.props` and the `-Version` defaults of the build
helpers when starting a new release line, so local builds and workflow runs agree.

## Container images

For container-only builds, use **Actions → Container images → Run workflow**.
It uses a `workflow_dispatch` trigger, resolves the latest stable
Playwright once, pulls current compatible Linux and Windows bases, and tests before
optionally publishing to GHCR with GitHub's built-in token. A newly created GHCR
package may need its visibility set to public for
anonymous pulls. Uncheck `publish` for verification only. Tags are listed in the
[container guide](../container.md).

## NuGet packages

Contracts and Client target `net8.0;net10.0`. The server and renderer remain .NET 10
and are not packable. Contracts remains a shared source project and is not packable
on its own. Only Client produces a NuGet package, named `AurorisNet.Pressroom.Client`,
containing both assemblies. The client tests run on both frameworks (install both runtimes).

```sh
dotnet restore Pressroom.slnx
dotnet pack src/Pressroom.Client -c Release -o artifacts/nuget --no-restore
```

Pass the same `-p:Version=1.2.3-rc.1` to restore, build and pack for a prerelease.
The package contains `Pressroom.Client.dll` and `Pressroom.Contracts.dll` for each
target, a README, icon, and MIT metadata. Its `.snupkg` contains portable PDBs for
both assemblies and frameworks. Source Link points at the build commit; CI normalizes build paths.
Package validation checks framework compatibility. After the first stable release,
set `PackageValidationBaselineVersion` in `build/ClientPackage.props` to that
published version and restore dependencies. Subsequent releases must pass that
[API baseline](https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/baseline-version-validator).

The Packages workflow always keeps the client package and symbols on GitHub and
optionally publishes them to NuGet.org through **publish_nuget**. Download and extract
`nuget-packages` from a successful run, or download the single `.nupkg` file from a
GitHub release into a local feed directory. See [client installation](client.md#installing-the-package).
Locally built packages are unsigned; `dotnet nuget verify` checks signatures, not Source Link.
Inspect Source Link in the PDB or NuGet Package Explorer.

`build/Test-ClientPackages.ps1 -ServerUrl http://127.0.0.1:5177/` checks the bundled
assemblies, symbols, and absence of package dependencies, creates fresh web API
consumers for both targets, installs the client package from `artifacts/nuget`,
compiles the client example and renders against an anonymous loopback test server.
Use `-Net8Dotnet` if the .NET 8 runtime is installed separately. Each run uses a
private package cache so an older local package with the same version cannot mask
a change. Use `-BuildOnly` instead of `-ServerUrl` to verify package restore and
consumer compilation without a running server, as the packaging workflow does.

## Design history and verification

- [API reference](api.md), [authoring documents](documents.md), and the
  [.NET client](client.md): the integrator guides.
- [Rendering service design](rendering-service-design.md): the design
  baseline, decisions, and references.
- [Roadmap brief](roadmap-brief.md): the brief for the packages, Linux, container,
  and jobs work, kept as history.
- [Linux acceptance checklist](../linux.md#acceptance-checklist): the checks for
  qualifying a Linux deployment.
- [Windows / IIS acceptance](../windows-acceptance.md): the checklist shipped with
  each release for target-server qualification.
- [Third-party notices](../../THIRD-PARTY-NOTICES.md): licences of the redistributed
  components.
