# Pressroom: next-phase implementation brief

This brief is written to be handed to an implementer (human or AI) who does not
have the repository open. It describes the system as it is today, the house rules
that every change must respect, and four pieces of work in the order they should
land: NuGet packages for the client, a Linux build, a container image, and an
asynchronous job API. Each piece has acceptance criteria. Treat the existing
documents under `docs/` as the source of truth for current behaviour.

---

## 1. The system today

**What it is.** Pressroom is a .NET 10 ASP.NET Core minimal API that turns one
complete HTML document into a PDF through Playwright and a pinned Chromium headless
shell. Callers `POST /v1/render` with JSON (`html`, `fileName`, `disposition`,
`printer`, `options{...}`) and receive `application/pdf` with `X-Render-Pages` and
`X-Render-Duration-Ms`, or, with a `printer`, a 202 JSON receipt after the PDF is
handed to a Windows printer through SumatraPDF. `GET /v1/health` is anonymous;
`GET /v1/info`, `GET /v1/printers`, and `GET /openapi/v1.json` follow the configured
access mode. Full contract: `docs/api.md`.

**Projects** (solution `Pressroom.slnx`, all `net10.0`, C# 14, nullable, lock files,
`RestorePackagesWithLockFile=true`, `Deterministic=true`, `Version` 0.1.0 in
`Directory.Build.props` and overridden by `-p:Version` at release time):

| Project | Role | Dependencies |
|---|---|---|
| `src/Pressroom.Contracts` | `RenderRequest`, `RenderOptions`, `PageSize`, `PageMargins`, `ReadinessOptions`, `PdfDisposition`, `PrintJobResult`, `IPdfRenderer`, `PdfRenderResult`, `RequestValidator`, `JsonDefaults` | BCL only |
| `src/Pressroom.Client` | `PressroomClient(HttpClient)` implementing `IPdfRenderer`: `RenderAsync` (buffered `RenderedPdf`), `RenderStreamAsync`, `PrintAsync`, `IsReadyAsync`; `PressroomException(StatusCode, Detail, Errors, RetryAfter)` | Contracts |
| `src/Pressroom.Rendering` | `BrowserRenderer : IHtmlRenderer, IHostedService` (browser supervisor, slots via `SemaphoreSlim(Concurrency)`, warm-up, sandbox verification, recycling after `RestartAfterRenders`), `DocumentValidator`, `FontCatalog`, `OwnedDriverProcess`, `WindowsProcessJob`, packaged Noto Sans fonts | Microsoft.Playwright 1.64.1, AngleSharp, PdfPig |
| `src/Pressroom.Server` | Endpoints, `ApiKeyAuthenticationHandler`, `AdmissionGate` (`Concurrency + QueueLimit` permits held until the response completes), `BoundedBodyReader`, `ConfigurationValidationService`, `PrintService`, `Pressroom.Publish.targets`, `web.config` | Rendering, Contracts, Serilog |
| `tests/*.Tests` | Contract and client tests; renderer and sandbox tests (real Chromium); server configuration, printing, and HTTP tests with renderer substitutes | |

**Configuration** is JSON only: packaged `appsettings.json` defaults, then one
external file (default `C:\ProgramData\Pressroom\pressroom.settings.json`, or
`ConfigurationFile` in `appsettings.json`, or `--config <path>`). No environment
variable overrides by design. Sections: `Render` (Concurrency 2, QueueLimit 8,
MaxBodyBytes 25 MiB, DefaultTimeoutMs 30000, MaxTimeoutMs 60000, ShutdownTimeoutMs,
RestartAfterRenders 500, DefaultPageSize, BrowsersPath, ChromiumPath, ChromiumSandbox
true), `Paths:StateRoot`, `Fonts:Manifest`, `Auth` (`Mode` ApiKey|Anonymous,
`ApiKeys:{id:{Secret, AllowJavaScript}}`), `Printing` (`SumatraPdfPath`, `Printers`).
Details: `docs/configuration.md`.

**Packaging today.** Visual Studio publish profiles `IisFolder` and
`IisFolderSelfContained` (win-x64, framework-dependent by default). During publish,
`Pressroom.Publish.targets` runs `build/Publish-Pressroom.ps1` under Windows
PowerShell 5.1 in two modes: `Prepare` installs the matching Chromium headless shell
into a build-owned bundle using the NuGet package's private Node and driver
(`.playwright/node/win32_x64/node.exe`, `.playwright/package/cli.js install --only-shell chromium`),
copies fonts, package notices, README and `docs/*.md`; `Verify` copies the full
payload to an isolated folder and runs `Pressroom.Server.exe --check`, then removes
`DEPENDENCIES_VALIDATED` markers and writes `release-manifest.json` (versions, font
inventory, SHA-256 of every file). The GitHub Actions `release.yml` runs this on a
Windows runner and publishes `Pressroom-<version>-win-x64-framework-dependent.zip`
plus `.sha256`. The targets currently hard-fail unless `OS == Windows_NT` and
`RuntimeIdentifier == win-x64` (a development publish can set
`PressroomBundleBrowser=false`).

**Runtime facts that matter for this work.**

- `BrowserRenderer` sets process-scoped `PLAYWRIGHT_BROWSERS_PATH`,
  `PLAYWRIGHT_NODEJS_PATH`, `TEMP`, `TMP`, `TMPDIR` before `Playwright.CreateAsync()`,
  pointing the temp variables at `<StateRoot>/temp/<pid>-<guid>`. It already picks
  `.playwright/node/linux-x64/node` on Linux and requires x64.
- `OwnedDriverProcess` identifies the private Node driver by snapshotting child
  processes. On Windows it assigns the driver to a Job Object with
  `KILL_ON_JOB_CLOSE` (`WindowsProcessJob`) so every Chromium descendant dies with
  it. On Linux it already reads `/proc/*/stat` to track browser children and kills
  the tree on failed shutdown. Other platforms throw `PlatformNotSupportedException`.
- `PathsSettings.StateRoot` defaults to `%ProgramData%\Pressroom` on Windows and
  `Path.GetTempPath()/Pressroom` elsewhere.
- Startup verifies that the launched browser enforces the Connection-Allowlist
  policy and that the sandbox launch succeeds; both are hard failures.
  `ChromiumSandbox=false` is not a supported production setting.
- `PrintService` throws `PrintUnavailableException` (503) when not on Windows or
  when `SumatraPdfPath` does not exist.
- Logging is Serilog to console and rolling files under `<StateRoot>/logs`,
  metadata only. Logs must never contain document bodies, PDF bytes, secrets,
  blocked URLs, or page console text.
- Historical Linux verification (see `docs/development/verification.md`): the full
  suite including real-Chromium renderer tests passed on Linux with a test-only
  executable override and, because the tests ran as root in a container, with the
  sandbox disabled. Production sandboxing on Linux has not been demonstrated yet.

## 2. House rules for every change

1. **Contract stability.** `POST /v1/render` and the existing JSON shape, headers,
   and status codes do not change. New capabilities are new endpoints, new optional
   fields, or new packages.
2. **JSON-only configuration.** No environment-variable overrides for application
   settings. New settings get a section, defaults in `appsettings.json`, startup
   validation, and a row in `docs/configuration.md`. Container-friendliness comes
   from `--config` and mounted files, not from env vars.
3. **Sandbox stays on.** Never add `--no-sandbox`, never make a sandbox failure
   non-fatal, never skip Playwright host validation in a release artefact.
4. **Metadata-only logs** and no deliberate retention of documents beyond what a
   feature explicitly documents (the async design below is the one place retention
   is introduced, and it is opt-in and documented).
5. **Pinned and locked.** Playwright, Chromium, Node, and NuGet versions are pinned
   and restored with `--locked-mode`. A release artefact must pass a real `--check`
   with the bundled browser before it is published.
6. **Windows and IIS remain first-class.** Nothing in the Linux or container work
   may regress the IIS folder release, the Visual Studio publish check, or the
   Windows acceptance record.
7. **Docs ship with the code.** Each piece updates the relevant guides under
   `docs/` (user and integrator focused), adds a verification entry under
   `docs/development/verification.md`, and extends `docs/windows-acceptance.md` or
   adds a Linux acceptance record where deployment behaviour changes.
8. **Tests first-class.** Unit and API tests use renderer substitutes; renderer
   and sandbox tests use the real browser. New behaviour gets both where applicable.

## 3. Work item A: NuGet packages for the client

**Goal.** Integrators add `Pressroom.Client` from nuget.org instead of referencing
projects from a clone.

**Scope.**

- Make `Pressroom.Contracts` and `Pressroom.Client` packable: `IsPackable=true`,
  `PackageId` equal to the project name, `Authors`, `Description` (already set),
  `PackageLicenseExpression=MIT`, `PackageProjectUrl` and `RepositoryUrl`
  (`https://github.com/auroris/Pressroom`), `PackageReadmeFile` pointing at a
  short package README (copy the relevant parts of `docs/client.md`), a package
  icon, `PublishRepositoryUrl`, `EmbedUntrackedSources`, `IncludeSymbols` with
  `SymbolPackageFormat=snupkg`, `ContinuousIntegrationBuild` in CI, and
  Source Link via `Microsoft.SourceLink.GitHub`. Keep `Pressroom.Server` and
  `Pressroom.Rendering` `IsPackable=false` for now.
- Multi-target the two client projects `net8.0;net10.0` so .NET 8 LTS applications
  can consume them. Audit the code for C# 14 or .NET 10-only API usage: both
  projects use `System.Net.Http.Json`, `System.Text.Json`, generated regexes,
  `Rune`, and collection expressions; all are available on .NET 8 with the pinned
  language version. If any .NET 10 API is used, polyfill or `#if`.
- Enable `EnablePackageValidation` with a baseline once the first version ships, so
  later versions cannot break the public API accidentally.
- Version the packages with the same `-p:Version` the release workflow passes.
  Pre-release suffixes map to NuGet pre-release versions.
- Add a `publish-packages` job to `.github/workflows/release.yml` (or a separate
  manual workflow `packages.yml`) that runs on `ubuntu-latest`: restore locked, build,
  test the contract and client tests, `dotnet pack` the two projects, and push to
  nuget.org. Prefer nuget.org **trusted publishing** (OIDC) if available for the
  account; otherwise a `NUGET_API_KEY` repository secret scoped to the two package
  IDs. Pushing happens only after the Windows release job has succeeded for the same
  version, so a package never references a version that has no server release.
- Lock files: packing with locked restore must keep working; update
  `packages.lock.json` for both target frameworks.

**Documentation.** `docs/client.md` and `README.md` quick start switch from project
references to `dotnet add package Pressroom.Client`. `docs/developing.md` gains a
"Packages" section describing the workflow and the validation baseline.

**Acceptance.**

- `dotnet pack src/Pressroom.Client -c Release` produces `Pressroom.Client.<v>.nupkg`
  and `.snupkg` containing `lib/net8.0` and `lib/net10.0`, README, icon, and licence
  metadata; `dotnet nuget verify` and NuGet Package Explorer show Source Link and
  deterministic build.
- A fresh `dotnet new webapi` on .NET 8 and on .NET 10 can `dotnet add package`
  from a local feed, compile the snippet in `docs/client.md`, and render against a
  running Pressroom.
- The workflow publishes both packages for a pre-release version to nuget.org and
  the versions resolve.

## 4. Work item B: Linux build

**Goal.** The same server runs on Linux x64 with the sandbox on, packaged with its
own pinned Chromium, Node, and fonts, verified by the same `--check` gate. Printing
is explicitly unsupported on Linux.

**Code changes.**

1. **Process lifetime boundary.** Windows uses a Job Object. On Linux, implement
   `LinuxProcessGroup` (or similar) so the driver and all Chromium descendants can be
   terminated reliably: the simplest robust approach is to keep the existing
   `/proc` tree tracking for normal operation and add a kill-by-process-tree on
   failed shutdown that walks `/proc` for all descendants (not only direct browser
   roots), sends `SIGTERM`, waits a bounded time, then `SIGKILL`. If the driver can
   be started in its own session or process group, add `kill(-pgid)` as the first
   step. In a container, PID 1 exit is the final boundary; document that the
   container entrypoint must forward `SIGTERM` to the server (run the server as
   PID 1 or under `tini`). Do not add `PR_SET_PDEATHSIG`-style hacks that only
   cover direct children.
2. **State root default.** Change the non-Windows default of `Paths:StateRoot` from
   the temp directory to `/var/lib/pressroom`, and treat an unwritable or missing
   directory as the same startup failure as on Windows. Keep `temp` and `logs`
   subdirectories.
3. **Default configuration file location.** On Linux, the default external file is
   `/etc/pressroom/pressroom.settings.json`. Keep `--config` and `ConfigurationFile`.
4. **Printing.** When `Printing:Printers` is non-empty or `SumatraPdfPath` is set on
   a non-Windows OS, fail configuration validation with a clear message rather than
   accepting settings that can never work. `GET /v1/printers` returns an empty list;
   `printer` in a request returns 400 with a problem detail stating that printing is
   available on Windows only. Document this in `docs/printing.md`.
5. **Sandbox on Linux.** Chromium's sandbox on Linux needs either unprivileged user
   namespaces or the SUID helper. The headless shell ships without a SUID helper, so
   the environment must allow user namespaces (`kernel.unprivileged_userns_clone=1`
   on older kernels; on container hosts, a seccomp profile that permits
   `clone`/`unshare` with `CLONE_NEWUSER`, like the profile Playwright publishes for
   its Docker images). Keep `ChromiumSandbox=true` as the only supported setting;
   `--check` must fail clearly when the environment cannot sandbox, with a message
   that names the fix (seccomp profile or sysctl), not a suggestion to disable the
   sandbox.
6. **Fonts.** The packaged Noto Sans faces are injected into main documents, but
   header and footer templates fall back to system fonts. The Linux package must
   install a system fallback (`fonts-noto-core` or `fonts-liberation`) and
   `fontconfig`, otherwise footers may render as boxes. Verify with the bilingual
   sample and add the result to the verification record.
7. **Paths and case.** The code already switches string comparison by OS; audit
   `ServerConfiguration`, `FontCatalog`, and `RuntimePaths` for any remaining
   Windows path assumptions (drive letters, backslashes in defaults, `.exe` suffix).

**Packaging changes.**

- Make `build/Publish-Pressroom.ps1` run under PowerShell 7 on Linux: replace
  Windows-only constructs, select `.playwright/node/linux-x64/node`, install
  `chromium --only-shell` into the bundle, run `install-deps`-equivalent only in the
  Dockerfile (not in the script), copy fonts and notices, remove
  `DEPENDENCIES_VALIDATED`, run `Pressroom.Server --check` against the staged copy,
  and write `release-manifest.json` with `runtimeIdentifier: linux-x64`. The .NET
  SDK Docker images include `pwsh`, so the same script can drive both platforms.
- In `Pressroom.Publish.targets`, allow `linux-x64` when `OS != Windows_NT`, and
  resolve the PowerShell executable to `pwsh` there. Keep the Windows rules exactly
  as they are.
- Produce `Pressroom-<version>-linux-x64-framework-dependent.tar.gz` as a release
  asset in addition to the container image, with the same checksum file and
  manifest. Framework-dependent is consistent with Windows; a self-contained
  variant is optional.

**Documentation.** New `docs/linux.md` (prerequisites, directory layout and
permissions, systemd unit example with `--config`, sandbox requirements, update and
rollback) linked from the README table; `docs/deployment.md` stays Windows and IIS
focused and cross-links. New `docs/linux-acceptance.md` mirroring the Windows
record: cold start as a non-root user, sandbox on, both access modes, limits and
cancellation, recycle and shutdown, soak.

**Acceptance.**

- `dotnet test` passes on `ubuntu-latest` including real-Chromium renderer and
  sandbox tests **as a non-root user with the sandbox enabled** (the CI job needs the
  seccomp or sysctl setup; document which).
- The Linux tarball passes `--check` on a clean Ubuntu LTS and Debian stable VM with
  only the .NET 10 runtime and the documented font packages installed, running as
  an unprivileged user with `/var/lib/pressroom` writable.
- The bilingual sample renders two pages with correct Noto Sans text and a readable
  footer; output is byte-for-byte comparable (same page count, same text extraction)
  with the Windows render.
- Killing the server with `SIGTERM` during two concurrent renders leaves no
  `chrome-headless-shell` or `node` processes behind within `ShutdownTimeoutMs + 5 s`.

## 5. Work item C: Container image

**Goal.** `docker run ghcr.io/auroris/pressroom:<version>` with a mounted settings
file is a complete, sandboxed deployment that passes the same `--check` as the
folder releases. Builds on work item B.

**Dockerfile (multi-stage).**

1. **build** from `mcr.microsoft.com/dotnet/sdk:10.0`: copy sources, restore
   `--locked-mode`, run unit and API tests (skip real-browser tests here or run them
   with the sandbox requirements satisfied in CI), publish `Pressroom.Server` for
   `linux-x64` framework-dependent with the Linux-capable publish helper, which
   installs the headless shell into `/app/browsers`, copies notices and docs, and
   writes `release-manifest.json`. Pass `-p:Version` and
   `-p:ContinuousIntegrationBuild=true`.
2. **runtime** from `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian): install only
   the Chromium headless shell's shared library dependencies plus `fontconfig` and
   the fallback font package. Derive the apt list from Playwright's
   `install-deps chromium` for the pinned version and pin it in the Dockerfile with
   a comment naming the Playwright version; do not run `install-deps` at container
   start. Create user `pressroom` (fixed UID, e.g. 10001), directories
   `/app` (read-only), `/etc/pressroom` (read-only, settings mount point),
   `/var/lib/pressroom` (volume, owned by `pressroom`). Copy the published payload.
   `USER pressroom`, `EXPOSE 8080`, `ENV ASPNETCORE_URLS=http://+:8080` (this is a
   hosting setting, not an application setting, so it is allowed),
   `HEALTHCHECK CMD curl -fsS http://localhost:8080/v1/health || exit 1`,
   `ENTRYPOINT ["dotnet", "Pressroom.Server.dll"]` with
   `CMD ["--config", "/etc/pressroom/pressroom.settings.json"]`. Running the server
   as PID 1 is acceptable because .NET handles `SIGTERM`; otherwise use `tini`.
3. **check** stage (or a CI step): run the runtime image with
   `--check --config /dev/null`-style minimal config and the documented security
   options, and fail the build if it does not print `"status":"ready"`.

**Runtime requirements to document and to encode in a Compose example and a
Kubernetes manifest snippet:**

- `--security-opt seccomp=<pressroom-seccomp.json>` (ship the JSON in `deploy/`;
  it is Docker's default profile plus `clone`, `clone3`, `unshare`, and `setns` with
  user namespaces) **or** a host with unprivileged user namespaces and no seccomp
  restriction. Never recommend `--privileged` or `--cap-add SYS_ADMIN`.
- `--shm-size=1g` or `--ipc=host` substitute: Chromium needs more than Docker's
  64 MiB `/dev/shm`; prefer `--shm-size`.
- Read-only root filesystem (`--read-only`) with a writable volume at
  `/var/lib/pressroom` and `--tmpfs /tmp`.
- Stop timeout above `Render:ShutdownTimeoutMs` (e.g. `--stop-timeout 45`).
- Settings via a mounted file or Kubernetes Secret at
  `/etc/pressroom/pressroom.settings.json`; secrets never via env.
- Memory request sized for `Concurrency` Chromium contexts plus the browser
  (measure; start with 1.5 GiB for concurrency 2) and a CPU request of at least 1.
- Logs: console output is already structured by Serilog; file logging under the
  volume remains available but should be considered secondary in containers.

**CI.** Add a `container` job to `release.yml` on `ubuntu-latest`: `docker buildx`
with the pinned base image digests, run the `--check` gate with the seccomp profile,
generate an SBOM (`syft` or `docker sbom`) and attach it to the release, push to
`ghcr.io/auroris/pressroom` with tags `<version>` and, for non-pre-release, `latest`,
and record the image digest in the release notes. Use the workflow's `GITHUB_TOKEN`
with `packages: write`.

**Documentation.** New `docs/container.md` (run, Compose, Kubernetes, seccomp, shm,
volumes, health, upgrade by tag, rollback by previous tag) linked from the README
table; README badges gain a container badge; `docs/security.md` gains a paragraph on
what the container adds (read-only root, non-root user) and still does not add
(network egress policy, which belongs to the NetworkPolicy or firewall).

**Acceptance.**

- `docker run` with the documented flags and a mounted settings file answers
  `/v1/health` 200 within 10 seconds of start, renders the bilingual sample, and
  returns 401 for a bad key.
- The image runs with `--read-only`, as a non-root user, with the sandbox verified
  on (the server logs the sandbox verification line; `--check` reports ready).
- `docker stop` during active renders exits cleanly within the stop timeout with no
  orphaned processes (`docker top` shows none before exit).
- Image size recorded; the headless shell, Node, fonts, and app should fit in well
  under 600 MB compressed. No `apt` cache or SDK remnants in the runtime layer.
- Scanning (Trivy or equivalent) reports no critical CVEs in the base layers at
  release time; the release notes list the base image digest.

## 6. Work item D: Asynchronous jobs

**Problem.** The synchronous API admits `Concurrency + QueueLimit` requests (10 by
default) and answers 429 beyond that. A caller producing hundreds of documents
(statements, nightly batches) must pace itself and hold connections open for every
render. An asynchronous mode lets a caller submit work, go away, and collect results.

**Design principles.**

- The synchronous path is untouched and keeps priority: async work must never
  starve interactive renders.
- Async is **opt-in** (`Jobs:Enabled=false` by default) because it introduces
  storage of rendered PDFs, which the current design deliberately avoids.
- Single instance in v1, same as today. Multi-instance needs a shared store and is
  explicitly out of scope, but the store is behind an interface so it can be added.
- Polling first. Callbacks are a later, opt-in extension because today the server
  makes no outbound connections, and that property is part of the security story.

**API.**

| Endpoint | Access | Behaviour |
|---|---|---|
| `POST /v1/jobs` | Configured access mode (same as render) | Body identical to `POST /v1/render` (printer allowed on Windows). Validates fully, stores the request, enqueues. Returns **202** with `Location: /v1/jobs/{id}` and a JSON job document. 429 with `Retry-After` when the job queue is full (global or per key). Supports `Idempotency-Key` header: same key plus same caller within the retention window returns the existing job |
| `GET /v1/jobs/{id}` | Owner only | Job document: `id`, `status` (`queued`, `rendering`, `succeeded`, `failed`, `cancelled`, `expired`), `createdUtc`, `startedUtc`, `completedUtc`, `expiresUtc`, `queuePosition` while queued, `pageCount` and `durationMs` when succeeded, `error` (problem-details shape) when failed, `printer` and `printJob` for print jobs. Returns `Retry-After` while not terminal |
| `GET /v1/jobs/{id}/result` | Owner only | The PDF with the same headers as the sync response (`Content-Disposition` from the stored request, `X-Render-Pages`, `X-Render-Duration-Ms`, `Cache-Control: no-store`). 409 with `Retry-After` while the job is not finished; 410 after expiry or deletion; 404 for unknown ids |
| `DELETE /v1/jobs/{id}` | Owner only | Cancels a queued or rendering job, or deletes a stored result early. 204. Idempotent |
| `GET /v1/jobs?status=&limit=&after=` | Owner only | The caller's own jobs, newest first, for reconciliation after a client crash. Bounded page size |

"Owner" is the API key identifier that created the job. In anonymous mode, jobs
created without a key are owned by the anonymous principal and are retrievable by
anyone who knows the 128-bit random id; document this and recommend API-key mode
for async. A key can never read another key's jobs (404, not 403, to avoid
confirming existence).

**Job document example.**

```json
{
  "id": "9f1c2e4d6b7a4c3e8d5f0a1b2c3d4e5f",
  "status": "queued",
  "queuePosition": 12,
  "createdUtc": "2026-11-02T03:10:00Z",
  "expiresUtc": "2026-11-02T04:10:00Z",
  "fileName": "statement-10231.pdf",
  "links": { "self": "/v1/jobs/9f1c…", "result": "/v1/jobs/9f1c…/result" }
}
```

**Scheduling.**

- A `JobScheduler` hosted service owns a set of per-key FIFO queues and dispatches
  in round-robin across keys, so one caller's thousand-document batch does not block
  another caller's five.
- Workers: `Jobs:Workers` (default 1). A worker acquires a render slot from the same
  `BrowserRenderer` as sync requests. To keep sync priority, the worker only takes a
  slot when at least `Jobs:ReservedSyncSlots` (default 1) slots remain free for
  synchronous work; with `Concurrency=2` that means async uses at most one slot. An
  operator who wants more throughput raises `Concurrency` and `Workers` together.
- A job's `options.timeoutMs` bounds rendering only, not queue time (unlike sync).
  `Jobs:MaxQueueAge` (default 10 minutes) expires jobs that waited too long with
  status `expired` and a problem detail; `Jobs:MaxQueued` (default 200) and
  `Jobs:MaxQueuedPerKey` (default 100) bound admission.
- Cancellation: `DELETE` removes a queued job immediately; for a rendering job it
  cancels the render's token, which closes the browser context exactly as a
  disconnect does today.
- Browser recycling and restarts treat an in-flight job like an in-flight sync
  render: it fails with a 503-shaped error and `retryable: true`; the scheduler
  does not auto-retry (clients own retry policy, consistent with today), but the
  job document says it is safe to resubmit.

**Storage.**

- `IJobStore` with one implementation in v1: **local disk under
  `<StateRoot>/jobs/<id>/`** holding `request.json` (the validated request,
  including the HTML, because it must survive until rendering), `job.json`
  (metadata and state), and `result.pdf`. Files are created with permissions that
  restrict them to the service identity. An in-memory index is rebuilt from disk at
  startup.
- Limits: `Jobs:ResultRetention` (default 1 hour after completion),
  `Jobs:RequestRetention` for failed jobs (default same), `Jobs:MaxStoredBytes`
  (default 2 GiB; admission returns 429 with `Retry-After` when exceeded),
  `Jobs:DeleteResultOnDownload` (default `false`; when `true`, the first successful
  `GET result` deletes the file). A sweeper removes expired entries every minute and
  on startup.
- **Restart semantics.** On startup, jobs found in `queued` state are re-enqueued
  (their request is on disk); jobs found in `rendering` are marked `failed` with
  `code: "interrupted"` and `retryable: true`. This is the one place where storing
  the request pays for itself. Document that a crash loses nothing that was
  acknowledged with 202 except the in-flight render.
- **Retention statement** for `docs/security.md`: with jobs enabled, submitted HTML
  and rendered PDFs are stored on local disk under the state root for the configured
  retention, readable by the service identity; operators who need encryption at
  rest should use volume-level encryption. Logs remain metadata only (job ids and
  state transitions, never content).

**Capacity reporting.** `GET /v1/info` gains `jobs: { enabled, queued, rendering,
stored, storedBytes, workers, maxQueued }`. `/v1/health` stays as is; a full job
queue is not unhealthy.

**Client.**

- `PressroomClient.SubmitAsync(RenderRequest, idempotencyKey?) → RenderJob`
- `GetJobAsync(id) → RenderJob`
- `WaitAsync(id, CancellationToken, maxWait?)` polling with the server's
  `Retry-After` (fallback 1 s, capped backoff to 10 s), returning the terminal job
- `DownloadAsync(id) → RenderedPdf` and `DownloadStreamAsync(id) → PdfRenderResult`
- `CancelAsync(id)`
- `RenderManyAsync(IEnumerable<RenderRequest>, int maxInFlight, CancellationToken)
  → IAsyncEnumerable<(RenderRequest, RenderedPdf | PressroomException)>` that
  submits with bounded concurrency, polls, downloads, and deletes on success. This
  is the method batch callers actually want.
- `PressroomException` gains `Code` (string) and `Retryable` (bool?) from the job
  error shape. The client never retries automatically.

**Out of scope for v1, written down so nobody improvises them:** webhooks and
callbacks (would require per-key outbound allowlists and HMAC signing); shared or
database-backed stores and multi-instance affinity; server-side ZIP of a batch;
priorities beyond sync-over-async; automatic retry on the server.

**Documentation.** `docs/api.md` gains a "Jobs" section; `docs/client.md` gains
"Batch rendering"; `docs/configuration.md` gains the `Jobs` section and its
defaults; `docs/security.md` updates the retention section; `docs/deployment.md`,
`docs/linux.md`, and `docs/container.md` note the disk sizing for `MaxStoredBytes`;
`docs/windows-acceptance.md` gains a jobs checklist (restart with queued jobs,
expiry, per-key isolation, disk limit).

**Tests.**

- Contract: job document serialization, idempotency key handling, status
  transitions, owner isolation (404 for another key), anonymous ownership rules.
- Scheduler (renderer substitute): round-robin fairness across keys, reserved sync
  slot respected under load, `MaxQueueAge` expiry, cancellation of queued and
  rendering jobs, restart recovery for `queued` and `rendering`.
- Store: retention sweeper, `MaxStoredBytes` admission, delete-on-download,
  permissions on created files, index rebuild.
- HTTP: full lifecycle through the API, 409 and `Retry-After` on early result
  fetch, 410 after expiry, `/v1/info` counters.
- Real browser: a batch of 50 small documents through `RenderManyAsync` with
  `maxInFlight 8` completes with correct page counts while a concurrent sync
  render keeps succeeding within its deadline.

**Acceptance.**

- With defaults plus `Jobs:Enabled=true`, 500 submitted documents complete without
  any 429 from the sync endpoint during the run, and sync p95 latency stays within
  two times its idle value.
- Restarting the service with 100 queued and 1 rendering job yields 100 completed
  jobs and 1 `failed/interrupted` job after restart, with the client's
  `RenderManyAsync` surfacing exactly that one error.
- Disk usage never exceeds `MaxStoredBytes` plus one in-flight result; expired
  results are gone within two sweeper intervals.
- A key cannot list, read, download, or delete another key's job.

## 7. Suggested order and deliverables

1. **A: NuGet packages.** One PR. Independent of everything else; immediate value.
2. **B: Linux build.** One PR for code changes with tests green on Linux CI, a
   second PR for packaging and the tarball release asset.
3. **C: Container image.** One PR after B, including the seccomp profile, Compose
   and Kubernetes examples, SBOM, and the CI gate.
4. **D: Asynchronous jobs.** Split into: contracts and client surface (with the
   server endpoints behind `Jobs:Enabled`), scheduler and local store, then
   `RenderManyAsync` and documentation. Each PR keeps the sync path's tests green.

Each PR updates `docs/` and `docs/development/verification.md`, and the final PR of
each work item updates `README.md` where features or badges change. Keep the
existing writing style: short sections, tables for reference material, explicit
statements of what is and is not guaranteed.
