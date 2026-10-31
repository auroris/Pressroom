# Verification record

## Packages, Linux, containers and jobs — 2026-10-31

The four roadmap items are implemented in the working tree. **Nothing was
published to nuget.org or a container registry.** The release workflow defaults
`publish_nuget` to false. Linux and container qualification remain pending a real
Linux host; the checklist in [Linux acceptance](../linux-acceptance.md) is unfilled.

### Windows build and automated checks

- Locked solution restore passed, including a prerelease version override.
- Release build with `ContinuousIntegrationBuild=true`: zero warnings or errors.
- Real-browser .NET 10 solution run: **203 passed, zero failed, two skipped**.
  The skips are the Linux process-ownership and Linux HTTP/printing tests.
  Counts are 63 contract/client, 27 renderer and 113 server tests passed.
- The same 63 contract/client tests passed on .NET 8.0.33.
- Toolchain: .NET SDK 10.0.403, .NET/ASP.NET Core 10.0.14,
  Playwright 1.64.1, private Node v24.22.1, bundled Chromium 156.0.8078.4.
  Chromium sandboxing and normal dependency validation remained enabled.
- The IIS folder publish completed its isolated PDF warm-up. ZIP packaging
  validated 505 files, including the private driver and deployment examples.
  Local artifact: `artifacts/roadmap-release-final/Pressroom-0.1.0-win-x64-framework-dependent.zip`.
  SHA-256: `7a3417b980f25a1964d15e78697dc5c2cca9b22fbb50b5a646fca8c9af099b81`.
- PowerShell build helpers parsed, release YAML passed actionlint v1.7.12,
  OpenAPI/seccomp JSON parsed, and `git diff --check` passed.

These checks do not establish a clean Windows Server installation, an IIS
application-pool deployment, a new self-contained release or Visual Studio UI
behavior. Existing acceptance checks still apply.

### NuGet packages

Both packages pack for net8.0 and net10.0, with portable symbols. ZIP/PE/PDB
inspection verified the README, PNG icon, MIT metadata, deterministic PE marker
and Source Link mappings to this repository. Local uncommitted source is not a
published Git commit; remote Source Link resolution requires the eventual
committed CI build. API-baseline validation must start with the first published
stable version rather than a fictional baseline.

Fresh web API consumers installed the local packages, compiled the DI example
and rendered a one-page PDF on each runtime. The reusable helper uses a private
package cache and source mapping: Pressroom packages must come from the local
feed, while missing Microsoft targeting packs may come from nuget.org.
`dotnet nuget verify` reports NU3004 for these unsigned local packages, as
expected. Repository signature verification remains a post-publication check.

### Jobs and scheduling

The ordinary suite covers ownership, idempotency, validation, transitions,
cancellation, result streaming, quotas, retention, Windows storage ACLs,
round-robin scheduling, reserved sync capacity, IIS base-path links and client
batch errors. The real-browser integration test renders 50 documents with eight
client operations in flight while sync requests continue.

The extended run completed **500 batch documents and 501 successful sync
renders**. Successful sync-request p95 was **304.0 ms idle / 428.6 ms busy**
(**1.41×**), with no sync 429. Two planned-recycle 503 responses were counted
separately and handled by the test load generator, preserving the existing API
contract. Public client methods do not silently retry failed renders. Metrics
are saved locally in `artifacts/jobs-acceptance.json`.

A store/scheduler restart test using a renderer substitute rebuilt 100 queued
jobs, completed all 100, and retained one previously rendering job as
`failed/interrupted`. This verifies durable index recovery and scheduling, not
the full process-restart/HTTP-client acceptance scenario. That deployment
scenario remains on the acceptance checklist.

### Linux and container gates

Linux process-tree termination, printing rejection, Unix storage permissions,
tarball executable modes, native library compatibility, sandbox policies,
shutdown, font output and soak tests have **not been executed on Linux in this
pass**. The Docker/Compose/Kubernetes definitions and helpers are prepared, but
no image was built, scanned, sized or published here. The release workflow
requires non-root sandboxed publish/browser checks, final-image HTTP/shutdown
smoke checks, an SPDX SBOM and a critical-CVE scan before pushing an image.
The image uses pinned official .NET 10 Ubuntu 24.04 bases; Debian 13 remains a
separate host-deployment target. Docker builds omit Git history, so Source Link
warnings are expected there; NuGet symbols are built separately from a checkout.

### Windows firewall prompts

Isolation tests deliberately exercise network APIs, including WebRTC probes.
Windows may prompt when a newly located headless-shell executable opens a socket.
The tests passed with the existing executable-scoped inbound block rules. No
inbound allow rule is required. Reuse the same browser path, or optionally run
`build/Block-TestBrowserInbound.ps1` as administrator for that exact executable.
This helper is never called by build, test or publish. No firewall policy was
changed during this work, and no production browser flags were weakened.

## API-key JavaScript permission — 2026-10-24

Each `Auth:ApiKeys` entry is now an object with `Secret` and optional
`AllowJavaScript`, which defaults to `false`. An opted-in key enables scripts
automatically. The request may opt out with `javaScript: false`; it cannot grant
permission. Anonymous requests always have scripts disabled. Effective settings
are applied before validation, so readiness expressions cannot bypass the key's
permission. Duplicate secrets and invalid key objects fail configuration validation.

The Release solution suite passed **147 tests, zero failed, zero skipped** on
Windows, with real-browser tests enabled and Chromium's sandbox explicitly on.
HTTP tests covered automatic key permission, request opt-out, other keys,
anonymous callers, invalid/duplicate headers and host-principal claims.

A real-browser test rendered the same payload with scripts disabled and enabled.
Inline scripts, data-backed scripts, modules and load handlers were blocked in
the main document, data frames and srcdoc frames when disabled; the enabled
control executed them. Static data-backed CSS, PNG images, packaged fonts and
selector readiness remained functional. Existing network/file isolation probes
continued to execute with scripts enabled and passed.

Font loading itself uses CSS. Fixed, trusted Playwright automation installs the
packaged font rules and waits for font readiness while caller scripts remain
disabled. Font injection now runs synchronously: the previous style-tag helper
waited indefinitely for a load handler suppressed by the script-disabled policy.
Direct local renderer calls also reject readiness expressions when scripts are
disabled.

## Windows configuration and authentication update — 2026-10-24

With .NET SDK 10.0.204 on Windows, the Release build completed with zero warnings
or errors. The solution tests reported **102 passed, zero failed, and eight
skipped**. The eight optional real-browser tests were not enabled in that run.

Coverage includes configured API keys, explicit anonymous access, rejection of
invalid supplied keys in either mode, and host identities being unable to bypass
API-key authentication. Configuration tests cover JSON precedence, relative file
selection, missing explicitly selected files, invalid settings, and exclusion of
environment-variable and command-line setting overrides.

The framework-dependent `IisFolder` profile also passed a complete Windows publish
with locked dependency restore. Its isolated JSON-configured `--check` launched
the bundled Chromium 156.0.8078.4, completed the PDF warm-up with sandboxing and
dependency validation enabled, and produced the release manifest. The publish
helper's Windows path quoting and SHA-256 hashing were corrected during this check.
The self-contained profile and a clean IIS deployment were not exercised in this
run. Use [`windows-acceptance.md`](../windows-acceptance.md) for target-server qualification.

## Historical Linux verification

The initial implementation was verified in a Linux workspace with a locally
installed .NET 10 SDK. The results below describe that historical run, before the
file-only configuration and API-key/anonymous authentication update.

### Checks performed

- Release compilation succeeded with zero warnings or errors. The consolidated
  suite passed **83 tests, zero failed and zero skipped**: 45 contract/client tests,
  24 HTTP API tests, and 14 renderer tests (including real Chromium). The API tests use controlled renderer substitutes for authentication,
  admission, body limits, and error handling; real renderer tests exercise Chromium.
- Both Windows x64 publish profiles, framework-dependent and self-contained,
  have passed cross-compilation with locked dependency restore. The generated IIS
  entry point uses `Pressroom.Server.exe` with empty arguments; the private Windows
  driver, fonts, and selected runtime payload were verified. This verifies compilation and payload selection, not Windows
  executable behavior or IIS operation.
- The automatic publish helper was parsed with Microsoft's PowerShell parser with
  zero syntax errors. Both publish profiles, the MSBuild target file, `web.config`,
  and the sample JSON files were parsed successfully.
- A scratch MSBuild harness imported the actual publish targets and simulated only
  the external helper. It confirmed that application, private `.playwright` driver,
  browser, and generated manifest enter the publish file list. It also confirmed
  that a failed package verification prevents destination transfer. This is a
  publish-ordering test, not a Windows browser test.
- The packaged Noto Sans faces were exercised in real main-document PDFs. A
  separate footer experiment fell back to a system font despite a data-backed
  Noto rule; custom embedded header/footer fonts are therefore unsupported in this
  initial implementation.

### Response disposition checks

The consolidated passing suite verified explicit inline and attachment responses,
the attachment default, sanitized Unicode filenames, PDF content type, and 400
responses for invalid disposition values/types. The HTTP client serializes the
choice and preserves the response disposition in buffered and streaming results.

An additional real HTTP smoke render returned a two-page bilingual PDF through the
compiled service, with readiness, authenticated info, and OpenAPI returning 200.
Invalid and missing API keys returned 401 without a Negotiate challenge in API-key
mode. Actual browser navigation and IIS deployment behavior remain Windows checks.

### Toolchain

- .NET SDK: 10.0.402; target framework: net10.0.
- Application version: 0.1.0.
- Microsoft.Playwright: 1.64.1; Chromium headless shell: 156.0.8078.4.
- Self-contained cross-publish includes .NET and ASP.NET Core 10.0.13.
- Locked restore was verified for normal solution development and both publish profiles.

### Browser provenance and limits

The Playwright browser CDN download failed in this environment. Real Linux browser
tests used Google's official Chrome for Testing headless shell at the exact selected
version through a test-only executable override. The root-container Linux tests
explicitly disabled the Chromium sandbox. Production defaults and the Windows
publish check retain sandboxing and normal host validation.

Visual Studio Publish must acquire the pinned Playwright package's browser payload
and pass its real Windows `--check`; there is no silent download-source or system-
browser fallback. Cross-publishing here explicitly used `PressroomBundleBrowser=false`
to inspect the Windows managed/native host output. Those temporary outputs are not
the qualified, complete IIS release.

No Windows executable, IIS app-pool identity, Windows Job Object/nested-job
execution, or Visual Studio UI was run in that verification. The
clean-server Media Foundation/VC-runtime objective and full publish/IIS workflow
remain the checks in [`windows-acceptance.md`](../windows-acceptance.md).
