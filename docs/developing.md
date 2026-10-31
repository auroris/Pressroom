# Developing

This guide is for people building Pressroom itself: the repository layout, the
build and test commands, how a release is produced, and where the design history
lives. Integrators do not need any of this; start with
[Getting started](getting-started.md) instead.

## Prerequisites

- The .NET 10 SDK (`global.json` pins 10.0.100 with latest-feature roll-forward).
- A Visual Studio release that supports .NET 10, for the publish profiles.
- Windows x64 for publishing and release packaging. Building and running the unit
  and API tests also works on Linux; real-browser tests need the browser payload.

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

## Design history and verification

- [Rendering service design](development/rendering-service-design.md): the design
  baseline, decisions, native dependency investigation, and references.
- [Verification record](development/verification.md): what was tested against real
  Chromium and IIS, and when.
- [Windows / IIS acceptance](windows-acceptance.md): the checklist shipped with each
  release for target-server qualification.
- [Third-party notices](../THIRD-PARTY-NOTICES.md): licences of the redistributed
  components.
