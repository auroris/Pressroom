# Configuration

Pressroom is configured by JSON files only. The packaged `appsettings.json` holds
nonsecret defaults; one **external settings file** holds everything specific to a
server, including API keys. Settings are read at startup, so restart the
application after changing them. Environment variables are not read, and command
line arguments other than `--config` and `--check` are not application settings.

## The external settings file

| Rule | Behaviour |
|---|---|
| Default location | `C:\ProgramData\Pressroom\pressroom.settings.json` on Windows; `/etc/pressroom/pressroom.settings.json` on Linux |
| Choosing another file | Set `ConfigurationFile` in `appsettings.json`, or pass `--config <path>` on the command line for local runs and package checks |
| Relative paths | Resolve from the application directory |
| Missing file | Allowed when no file is explicitly selected; an explicitly selected file must exist |
| Layering | External values override the packaged defaults key by key |

```json
{
  "ConfigurationFile": "D:\\PressroomConfig\\pressroom.settings.json"
}
```

Keep the real file outside the replaceable application folder and outside source
control, readable by the application pool identity and administrators only. Local
`pressroom.settings.json` files in the repository are ignored by Git. Never place
secrets in `web.config`, `appsettings.json`, or publish profiles.

[`samples/pressroom.settings.example.json`](../samples/pressroom.settings.example.json)
is a complete starting point.

## Access mode and API keys

`Auth:Mode` is `ApiKey` by default. Callers send a configured secret in the
`X-Api-Key` header on every request except `/v1/health`, which is always anonymous.

```json
{
  "Auth": {
    "Mode": "ApiKey",
    "ApiKeys": {
      "reporting-app": { "Secret": "REPLACE_WITH_A_RANDOMLY_GENERATED_SECRET" },
      "charts":        { "Secret": "ANOTHER_RANDOM_SECRET", "AllowJavaScript": true }
    }
  }
}
```

- Each entry maps a **nonsecret identifier** (1–128 characters, shown in logs) to an
  object with a `Secret` and an optional `AllowJavaScript` flag.
- Secrets must be 16–4096 characters, unique across identifiers, and generated from
  at least 32 random bytes. The application ships without a key and fails startup
  in API-key mode until at least one is configured.
- Secrets are compared in constant time and never logged.
- An invalid, empty, oversized, or duplicate `X-Api-Key` header returns 401.

To allow requests without credentials on a trusted network, select anonymous mode
explicitly:

```json
{
  "Auth": { "Mode": "Anonymous", "ApiKeys": {} }
}
```

Anonymous mode admits rendering, service information, and the OpenAPI document
without a key. A supplied invalid key still returns 401. Anonymous renders always
have JavaScript disabled, and anonymous callers cannot print or list printers; a
caller in anonymous mode may still present a valid configured key to use those
permissions. Printer discovery requires a valid key on both Windows and Linux.

Windows authentication and domain group checks are not supported. Under IIS,
enable Anonymous Authentication and disable Windows Authentication; Pressroom
enforces its own access mode.

## JavaScript permission

Every key starts with scripts disabled. Set `"AllowJavaScript": true` on the keys
whose applications need scripts, such as chart producers, and restart. Permission
takes effect automatically: a request needs no flag, and a readiness `function`
works without further setup. The request option `options.javaScript` defaults to
`true` and can only *disable* scripts for one render; setting it to `true` cannot
grant permission to a key that lacks it. A readiness function on a request whose
effective JavaScript is disabled returns 400.

## Rendering limits

| Key | Default | Meaning |
|---|---|---|
| `Render:Concurrency` | `2` | Renders in progress at once. Tune against representative documents and the server's memory budget; CPU count alone is not a capacity estimate |
| `Render:QueueLimit` | `8` | Additional admitted requests waiting for a render slot. Beyond concurrency plus queue, requests receive 429 with `Retry-After` |
| `Render:MaxBodyBytes` | `26214400` | Maximum size of the complete encoded JSON request (25 MiB), including templates and escaped HTML. IIS and Kestrel apply a 32 MiB outer ceiling |
| `Render:DefaultTimeoutMs` | `30000` | Processing deadline when the request omits `options.timeoutMs` |
| `Render:MaxTimeoutMs` | `60000` | Largest `options.timeoutMs` a request may ask for |
| `Render:ShutdownTimeoutMs` | `30000` | Time allowed to drain renders and close the browser on shutdown |
| `Render:RestartAfterRenders` | `500` | Planned browser recycle after this many renders; waiting requests keep their deadlines |
| `Render:DefaultPageSize` | `Letter` | Page size when the request omits `options.pageSize` |
| `Render:ChromiumSandbox` | `true` | Launch Chromium with its sandbox. A launch failure is a startup error; `false` is not a supported production setting |
| `Render:BrowsersPath` | `browsers` | Packaged browser tree, relative to the application folder |
| `Render:ChromiumPath` | unset | Advanced: an explicitly validated executable override, outside the standard package guarantee |

The processing deadline covers queue time, document loading, readiness, PDF
generation, and page counting. It does not include sending the finished PDF to a
slow client, but the admission permit is held until the response completes so slow
clients cannot accumulate unbounded buffered PDFs.

## Paths and logging

| Key | Default | Meaning |
|---|---|---|
| `Paths:StateRoot` | Common application data + `Pressroom` on Windows; `/var/lib/pressroom` on Linux | Writable root for `temp`, `logs`, and opt-in `jobs` |
| `Fonts:Manifest` | `fonts/manifest.json` | Packaged font definitions; part of the release |

Use a literal absolute path for `Paths:StateRoot`, such as `C:\ProgramData\Pressroom`.
Placeholders like `%ProgramData%` are not expanded.

Logging is fixed: console output plus daily rolling files `pressroom-<date>.log`
under `<StateRoot>\logs`, at most 14 files of 10 MiB each. Logs contain request
identifiers, key identifiers, sizes, timings, page counts, outcomes, and blocked
resource counts. They never contain document bodies, PDF bytes, secrets, blocked
URLs, or page console text. Arbitrary Serilog sink settings are not loaded.

## Printing

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

`Printers` maps caller-facing identifiers (1–128 ASCII letters, digits, hyphens,
and underscores, matched ignoring case) to exact Windows printer names or Linux
CUPS destinations. No printer is enabled by default. Windows requires
`SumatraPdfPath`; Linux uses `CupsCommandPath`, default `/usr/bin/lp`, which can
also be an absolute path to CUPS `lpr` (for example `/usr/bin/lpr`). The helper
setting for the other OS is ignored. See the [Printing guide](printing.md) for
host setup and a Linux configuration example.

## Asynchronous jobs

Enable with `"Jobs": { "Enabled": true }`. This introduces HTML/PDF storage.
Use one instance per state root. JSON time spans use `hh:mm:ss` or `d.hh:mm:ss`.

| Key | Default | Meaning |
|---|---|---|
| `Jobs:Enabled` | `false` | Enable endpoints, scheduler and store |
| `Jobs:Workers` | `1` | Active jobs; at most Concurrency minus ReservedSyncSlots |
| `Jobs:ReservedSyncSlots` | `1` | At least one sync slot; below Concurrency when jobs are enabled |
| `Jobs:MaxQueued` | `200` | Global waiting-job limit, 1–100000 |
| `Jobs:MaxQueuedPerKey` | `100` | Per-owner waiting limit, at most MaxQueued |
| `Jobs:MaxQueueAge` | `00:10:00` | Queue wait before expiry |
| `Jobs:ResultRetention` | `01:00:00` | Completed metadata/PDF lifetime; also the additional tombstone lifetime |
| `Jobs:RequestRetention` | `01:00:00` | Failed HTML retention, bounded by job expiry |
| `Jobs:MaxStoredBytes` | `2147483648` | 2 GiB, including 16 KiB metadata reserve/job; allowed 1 MiB–1 TiB |
| `Jobs:DeleteResultOnDownload` | `false` | Delete after the first complete successful download |

All durations must be one second to 30 days. Queue limits exclude active jobs.
Sync waiters take priority; background jobs never consume reserved slots.
Job rendering deadlines start at dispatch, excluding queue time.

Admission returns 429/Retry-After on queue/quota pressure. An oversized result
fails with `storage_full`. Serialized atomic writes may temporarily use one extra
result's space. Counters include metadata reserve, not filesystem allocation;
logs and temporary browser files are outside this budget.

Sweeping runs on startup, admission/reads, and every minute. Success removes HTML
immediately. Failed HTML expires at RequestRetention or job expiry, whichever
comes first. Expired content is deleted; tombstones last one additional
ResultRetention window before IDs return 404. Active downloads pin their file
until the stream closes. A job whose files cannot be removed yet, for example
because another process holds one open, is logged as a warning and retried by a
later sweep without affecting other jobs. A storage error while running a job is
logged and marks that job failed where possible; it never stops the service or
synchronous rendering. Disabling jobs stops sweeping old storage; drain/delete
jobs before disabling if retention must continue.

## Startup validation

Pressroom refuses to start, and `--check` fails, on invalid limits, missing packaged
assets, an unusable `StateRoot`, an unknown `Auth:Mode`, invalid key entries,
duplicate secrets, API-key mode without keys, or unknown properties under any
settings section (`Render`, `Paths`, `Fonts`, `Auth`, `Printing`, and `Jobs`), so
a misspelt key such as `Render:Concurency` is an error rather than a silently
ignored setting. Startup also verifies that the launched browser actually enforces
its connection policy and fails if it does not, including for an executable
override.

## Upgrading from earlier configurations

- Replace string key values with objects: `"key": "secret"` becomes
  `"key": { "Secret": "secret" }`. Add `"AllowJavaScript": true` only to keys that
  need scripts.
- Remove `Auth:WindowsEnabled`, `Auth:RequireGroup`, and `Auth:AccessGroup`, and set
  `Auth:Mode` explicitly.
- Move any environment variable overrides into the JSON file.
