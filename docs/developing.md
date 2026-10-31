# Developing

This guide is for people building Pressroom itself: the repository layout, the
build and test commands, how a release is produced, and where the design history
lives. Integrators do not need any of this; start with
[Getting started](getting-started.md) instead.

## Prerequisites

- The .NET 10 SDK (`global.json` pins 10.0.100 with latest-feature roll-forward).
- A Visual Studio release that supports .NET 10, for the publish profiles.
- The .NET 8 runtime for the second client test target.
- Windows x64 for IIS publishing, or Linux x64 with PowerShell 7 and the
  [documented libraries](linux.md) for Linux publishing. Real-browser tests need
  the browser payload and sandbox support.

## Repository layout

| Path | Contents |
|---|---|
| `src/Pressroom.Server` | ASP.NET Core minimal API host: endpoints, authentication, admission, printing, publish targets, IIS `web.config` |
| `src/Pressroom.Rendering` | Browser supervisor, document validation, sandbox policy, font catalog, packaged Noto Sans fonts |
| `src/Pressroom.Contracts` | Request and option records, `IPdfRenderer`, `RequestValidator`, JSON defaults. No browser dependency |
| `src/Pressroom.Client` | `PressroomClient`, the HTTP implementation of `IPdfRenderer` |
| `tests/` | Contract and client tests, renderer and sandbox tests, server configuration, printing, and HTTP tests |
| `build/` | `Publish-Pressroom.ps1` and `Package-Pressroom.ps1`, invoked by MSBuild and the release workflow |
| `samples/` | The bilingual sample document, its ready-to-send request, and the example settings file |
| `docs/` | The user and integrator guides; `docs/development/` holds the design baseline and verification record |
| `.github/workflows/release.yml` | The manual release workflow |

## Build and test

The solution pins exact NuGet versions in lock files. From the solution directory:

```sh
dotnet restore Pressroom.slnx --locked-mode
dotnet build Pressroom.slnx -c Release --no-restore
dotnet test --solution Pressroom.slnx -c Release
```

Unit and API tests use controlled renderer substitutes and run anywhere. The
real-browser tests need the browser payload (publish once, see below) and writable
temporary state. Ordinary **Build** and tests never download browsers.

## Windows firewall prompts during browser tests

Sandbox tests intentionally exercise local TCP/UDP/WebRTC sockets. Windows may
prompt for a newly copied `chrome-headless-shell.exe` at each executable path.
Choose Cancel/Block; Chromium needs no inbound network exception. Reuse the same
browser bundle path for repeated tests. To prevent a first-use prompt, run this
optional helper in an administrator PowerShell before testing that browser copy:

```powershell
./build/Block-TestBrowserInbound.ps1 -BrowserExecutable 'D:\path\to\chrome-headless-shell.exe'
```

It adds an inbound **block** rule for that exact executable on every profile.
It does not grant access or disable the firewall, and is never run by publish or
tests. A new browser path needs a separate rule. See Microsoft's
[firewall rule behavior](https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/rules).

## Running locally

Publish the `IisFolder` profile once to acquire and verify the matching browser,
then run the published executable with a local settings file:

```powershell
& '.\artifacts\publish\IisFolder\Pressroom.Server.exe' --config 'C:\PressroomConfig\pressroom.settings.json' --urls http://localhost:5177
```

Point `Paths:StateRoot` at a writable folder outside the release and configure
either a key or explicit anonymous mode. `--check` runs the same preflight without
listening on a port. Playwright's internal process settings are supplied by the
application and the publish helper; no environment variables are needed.

For cross-platform implementation work, a development-only publish can skip the
browser bundle with `-p:PressroomBundleBrowser=false`. That output lacks the
Windows browser bundle and check and is not a complete release.

## Publishing from Visual Studio

Open `Pressroom.slnx`, select **Pressroom.Server → Publish**, choose **IisFolder**,
and click **Publish**. The default output is `artifacts\publish\IisFolder`. The
profile is `net10.0`, `win-x64`, framework-dependent, with single-file, trimming,
and AOT disabled. **IisFolderSelfContained** also includes the .NET runtime.

Publish automatically:

1. Builds the Windows x64 application with the pinned package versions.
2. Runs the package's private Playwright driver to install the matching Chromium
   headless shell into the publish payload. No separate Node, npm, or PowerShell 7
   is needed; the helper uses Windows PowerShell 5.1.
3. Adds the complete browser tree, installer-selected companion files, fonts,
   managed package notices, and private driver to the publish file list, using the
   Web SDK's [publish file list](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/visual-studio-publish-profiles?view=aspnetcore-10.0#include-files)
   rather than post-deployment copying.
4. Makes an isolated local copy and runs `Pressroom.Server.exe --check` against it,
   with the sandbox enabled and a real PDF warm-up, using temporary state and no
   machine secrets.
5. Fails Publish if that check fails. On success, removes the build machine's
   `DEPENDENCIES_VALIDATED` cache markers and writes `release-manifest.json` with
   versions, font inventory, and SHA-256 hashes.

The first publish needs internet access to restore packages and acquire the
browser; later publishes reuse the cache. `build/Publish-Pressroom.ps1` is invoked
by MSBuild; there is no separate deployment script to run.

### Web Deploy profiles

Your usual Visual Studio **IIS / Web Deploy** profile works for `Pressroom.Server`:
select **Release**, **net10.0**, **win-x64**, framework-dependent, with single-file,
trimming, and AOT disabled. The project's publish targets add the browser assets to
that profile's payload. Keep `RestoreLockedMode=true` to enforce the lock files, and
keep credentials in Visual Studio's user settings rather than a committed `.pubxml`.

`EnableMsDeployAppOffline` defaults to `true`, allowing the ASP.NET Core module to
stop the application before Web Deploy replaces locked files. For a Folder profile
pointed directly at an IIS directory, stop the pool before copying and start it
afterwards; a local staging folder is the default. Keep a complete previous
publish folder for rollback.

The build-machine check proves the payload launches and renders on the publishing
machine. It does not replace the target-server
[acceptance record](windows-acceptance.md).

## Creating a GitHub release

The [Release workflow](../.github/workflows/release.yml) runs manually from the
repository's default branch:

1. Open **Actions → Release → Run workflow** and select the branch to release.
2. Enter an unused version without a `v` prefix, such as `1.2.3` or `1.2.3-rc.1`.
3. Optionally select **draft** or **prerelease**, then **Run workflow**. Versions
   with a suffix are marked prerelease automatically.

The workflow restores locked dependencies, builds and tests on Windows, publishes
the `IisFolder` profile, and requires the real Chromium PDF warm-up to pass before
creating a release tagged `v<version>` at the exact commit selected when the run
started. Existing tags and releases are never overwritten; if an interrupted upload
leaves a draft or tag, inspect it before retrying with that version.

Each release contains `Pressroom-<version>-win-x64-framework-dependent.zip` and a
`.zip.sha256` checksum. The workflow caches NuGet packages by lock file, creates the
release directly from the verified files on the build runner, uses GitHub's
built-in token with repository write permission for the tag and release, and keeps
the ZIP and checksum as workflow artifacts for 14 days. See GitHub's
[manual workflow instructions](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)
for the required repository access.

## Packages

Contracts and Client target `net8.0;net10.0`. The server and renderer remain .NET 10
and are not packable. The client tests run on both frameworks (install both runtimes).

```sh
dotnet restore Pressroom.slnx --locked-mode
dotnet pack src/Pressroom.Contracts -c Release -o artifacts/nuget --no-restore
dotnet pack src/Pressroom.Client -c Release -o artifacts/nuget --no-restore
```

Pass the same `-p:Version=1.2.3-rc.1` to restore, build and pack for a prerelease.
Packages contain both target assemblies, a README, icon, MIT metadata, and portable
PDBs in `.snupkg`. Source Link points at the build commit; CI normalizes build paths.
Package validation checks framework compatibility. After the first stable release,
set `PackageValidationBaselineVersion` in `build/ClientPackage.props` to that
published version and refresh the lock files. Subsequent releases must pass that
[API baseline](https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/baseline-version-validator).
There is deliberately no fictional baseline before the first publication.

The release workflow packs on Ubuntu only after the Windows release succeeds.
Publication is opt-in with `publish_nuget` (default false); draft runs always
preserve packages as artifacts without publishing them. Configure
`NUGET_USER` and a nuget.org [trusted publishing policy](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
for owner `auroris`, repository `Pressroom`, workflow `release.yml`, scoped to
`Pressroom.Client` and `Pressroom.Contracts`. No permanent API key is needed.
The workflow pushes symbols with the packages. `dotnet nuget verify` verifies
signatures, not Source Link; local unsigned packages are expected to lack a
repository signature until nuget.org signs them. Inspect Source Link in the PDB
or NuGet Package Explorer; verify downloaded, repository-signed packages after release.

`build/Test-ClientPackages.ps1 -ServerUrl http://127.0.0.1:5177/` creates fresh
web API consumers for both targets, installs packages from `artifacts/nuget`,
compiles the client example and renders against an anonymous loopback test server.
Use `-Net8Dotnet` if the .NET 8 runtime is installed separately. Each run uses a
private package cache so an older local package with the same version cannot mask
a change.

For the longer jobs acceptance run, enable the browser tests as above and set
`PRESSROOM_JOB_ACCEPTANCE_COUNT=500`. Set `PRESSROOM_JOB_METRICS_PATH` to save
successful sync-request p95 latency and planned-recycle 503 counts. The test
requires a p95 ratio at most 2 and rejects sync 429s; only its load generator
handles the existing recycle response. The public client does not retry renders.

## Design history and verification

- [Rendering service design](development/rendering-service-design.md): the design
  baseline, decisions, native dependency investigation, and references.
- [Verification record](development/verification.md): what was tested against real
  Chromium and IIS, and when.
- [Windows / IIS acceptance](windows-acceptance.md): the checklist shipped with each
  release for target-server qualification.
- [Third-party notices](../THIRD-PARTY-NOTICES.md): licences of the redistributed
  components.
