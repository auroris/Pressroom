# Deployment on Windows and IIS

Direct Windows/IIS deployments are intended primarily for intranets. For
internet-facing traffic, prefer the [Linux container](container.md) with its
documented isolation, or [direct Linux](linux.md) under a dedicated unprivileged
user. With jobs enabled, provision `Jobs:MaxStoredBytes` plus one result commit,
logs and temp space. Run one instance per state root and disable IIS overlapped
recycling for the local job store: stop the old worker before its replacement
starts, or the store lock rejects the second instance.

A Pressroom release is one folder. Deploying it means provisioning IIS once, then
copying the folder into place for every update. The release contains the renderer,
its runtime dependencies, and the fonts needed for document output.

## Server prerequisites

| Requirement | Notes |
|---|---|
| Windows Server x64 | A version supported by both .NET 10 and the pinned Playwright release; Windows Server 2019 or later at the time of writing. Record the tested build in the [acceptance record](windows-acceptance.md) |
| IIS with the **.NET 10 Hosting Bundle** | The bundle supplies the ASP.NET Core module. A runtime or SDK alone is not enough, and the module is required even for the self-contained profile |
| .NET 10 runtime | Installed by the Hosting Bundle. A later major runtime alone does not satisfy a `net10.0` application |
| Application Initialization | Optional IIS feature that keeps the renderer warm |

([Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0),
[runtime selection](https://learn.microsoft.com/en-us/dotnet/core/versions/selection),
[Playwright supported systems](https://playwright.dev/dotnet/docs/intro).)

## What is in a release

Each GitHub release contains `Pressroom-<version>-win-x64-framework-dependent.zip`
and a `.zip.sha256` checksum, alongside the Linux tarball and the .NET client
packages for application teams. The ZIP holds:

| Content | Purpose |
|---|---|
| `Pressroom.Server.exe`, `.dll`, `web.config`, dependency assemblies | The application, with IIS in-process hosting |
| `.playwright/` | The private Node runtime and Playwright driver. Hidden folder; **required** |
| `browsers/` | The matching Chromium headless shell and companion files |
| `fonts/` and `fonts/manifest.json` | Noto Sans faces with licence and hashes |
| `appsettings.json` | Nonsecret defaults |
| `notices/`, `THIRD-PARTY-NOTICES.md` | Third-party licences |
| `release-manifest.json` | Versions, font inventory, and SHA-256 hashes of every file |
| `docs/` | These guides, including the acceptance checklist for this release |

Verify the checksum, then extract the complete archive. Only the framework-dependent
package is published; it needs the Hosting Bundle on the server.

## Directory layout and permissions

Keep everything that changes at run time outside the replaceable release folder:

| Path (example) | Application pool identity |
|---|---|
| `C:\Sites\Pressroom` | Read and execute |
| `C:\ProgramData\Pressroom` | Read and traverse |
| `C:\ProgramData\Pressroom\pressroom.settings.json` | Read; administrators manage it |
| `C:\ProgramData\Pressroom\temp` | Modify |
| `C:\ProgramData\Pressroom\logs` | Modify |

Create the state root, `temp`, and `logs` before the first start and grant these
rights to the dedicated pool identity, such as `IIS AppPool\Pressroom`. Restrict
the settings file to that identity and administrators. Apply the permissions in
the table to each directory.

Write the settings file as described in [Configuration](configuration.md): at
minimum `Paths:StateRoot` and either API keys or explicit anonymous mode.

## IIS settings

Create a site or application pointing at the release folder with a dedicated
application pool:

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
| Site binding | HTTPS with your existing certificate |

IIS Anonymous Authentication lets requests reach the application, which then
enforces its own API-key or anonymous access mode.

Request limits: IIS request filtering and the ASP.NET Core server accept bodies up
to 32 MiB; the application enforces its 25 MiB cap before deserialization. Raising
the application cap requires reviewing both outer settings.

Overlapped recycling can briefly run two application instances, each with its own
browser and temporary directory. Budget memory for the overlap, or disable overlap
and accept the restart interruption.

## First start

1. Run the package check as the deploying user to prove the payload on this server:
   ```powershell
   & 'C:\Sites\Pressroom\Pressroom.Server.exe' --check --config 'C:\ProgramData\Pressroom\pressroom.settings.json'
   ```
2. Start the application pool. Watch `C:\ProgramData\Pressroom\logs` for the
   browser warm-up line.
3. Request `/v1/health` and expect 200, then `/v1/info` with a key and confirm the
   expected version.
4. Perform a smoke render with
   [`samples/orders-bilingual.request.json`](../samples/orders-bilingual.request.json)
   and open the result.

The first deployment of a release to a new server should complete the
[Windows / IIS acceptance record](windows-acceptance.md): pool identity, both
access modes through real IIS, limits and cancellation, recycling, update and
rollback, and a soak run.

## Updating

Update during a brief maintenance window: stop the application, replace the release
folder, and start it again.

1. Stage and verify the new release elsewhere on the server (checksum, extract,
   `--check`). Keep the previous release folder for rollback.
2. Stop the application pool, or place `app_offline.htm` at the site root, and wait
   for the browser and driver file locks to clear. The application has a bounded
   shutdown window to drain renders and close Chromium.
3. Replace only the application folder. `robocopy /MIR` is suitable when the
   destination contains nothing but release files; configuration, logs, and
   temporary data live elsewhere. Exit codes 0–7 are success; 8 and above are
   failures. If you use `app_offline.htm`, exclude it from the mirror so it stays
   until copying completes.
4. On a copy failure, leave the application stopped and restore the previous
   complete folder before starting.
5. Start the pool or remove the offline file. Check `/v1/health`, confirm the
   version in `/v1/info`, and run a smoke render.

## Rolling back

Stop the pool, restore the previous complete release folder, start, and repeat the
health, version, and smoke checks. The browser, driver, fonts, and application
always roll back together because they ship as one folder.

<a id="running-without-iis"></a>

## Running with Kestrel

The executable hosts itself on Kestrel for local use and checks:

```powershell
& 'C:\Sites\Pressroom\Pressroom.Server.exe' --config 'C:\ProgramData\Pressroom\pressroom.settings.json' --urls http://localhost:5177
```

IIS in-process hosting is the supported production topology. Hosting the same
executable as a Windows service or behind another reverse proxy is possible but
not packaged or qualified by the release process.

## Operations checklist

- Monitor `/v1/health` for readiness and `/v1/info` for queue depth and renders
  since startup.
- Watch for 429 rates (capacity) separately from 503 (browser replacement).
- Rotate API keys by adding the new key, switching callers, then removing the old
  one; each change needs a restart.
- Review `release-manifest.json` after each deployment to confirm the deployed
  Chromium and Playwright versions.
- Logs roll daily, 14 files of 10 MiB; archive them if you need longer retention.
