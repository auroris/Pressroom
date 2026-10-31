# Linux deployment

Direct Linux is suitable for internet-facing or intranet use when Pressroom runs
under a dedicated unprivileged user. Use API-key authentication and put an HTTPS
reverse proxy in front of the loopback-bound service. [Linux Docker with iptables
isolation](container.md) remains the preferred container option for internet-facing
traffic. An unprivileged identity does not itself block outbound connections;
host egress restrictions are a separate optional deployment setting.

Pressroom supports Linux x64 with the .NET 10 ASP.NET Core runtime and Chromium's
sandbox enabled. Optional [printing](printing.md) uses CUPS `lp` or `lpr` and an
explicit printer allowlist. Record target-host qualification in
[Linux acceptance](linux-acceptance.md).

## Prerequisites

Use Ubuntu 24.04 LTS or Debian 13, x64, a kernel with pidfd support (5.4 or later),
and an unprivileged service identity. Install the .NET 10 ASP.NET Core runtime.
Ubuntu 26.04.1 was also exercised on a clean VM; the exact scope and measurements
are recorded in [Linux acceptance](linux-acceptance.md).
Chromium also needs shared libraries; the runtime alone and fonts are insufficient.
On Ubuntu 24.04:

```sh
sudo apt-get install libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 \
  libatspi2.0-0t64 libcairo2 libcups2t64 libdbus-1-3 libdrm2 libgbm1 \
  libglib2.0-0t64 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 \
  libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 \
  fontconfig fonts-noto-core
```

This list comes from [Playwright 1.64.1](https://github.com/microsoft/playwright/blob/v1.64.1/packages/playwright-core/src/server/registry/nativeDeps.ts).
Debian 13 uses the same Chromium package names. Keep fontconfig and Noto:
headers/footers use system fallback fonts; main documents use the packaged faces.
The package never installs dependencies at startup or skips host validation.

## Sandbox

The headless shell uses unprivileged user namespaces; it has no SUID helper.
Permit `user.max_user_namespaces` greater than zero and, where present,
`kernel.unprivileged_userns_clone=1`. Ubuntu's AppArmor policy must also permit
the installed Chromium executable to create user namespaces. Have the host
administrator install an executable-specific AppArmor profile granting `userns`
for the release's headless-shell path. Containers need the supplied seccomp
profile too. Never disable Chromium's sandbox to work around host policy.

For the versioned installation layout below, the Ubuntu VM used this
`/etc/apparmor.d/pressroom-browser` profile:

```text
abi <abi/4.0>,
include <tunables/global>
profile pressroom-release /opt/pressroom/releases/**/chrome-headless-shell flags=(unconfined) {
  userns,
}
```

Load it with `sudo apparmor_parser -r /etc/apparmor.d/pressroom-browser`.
The release tree must remain root-owned and unwritable by the service. This
grants user namespaces to that executable path without disabling Ubuntu's
global `kernel.apparmor_restrict_unprivileged_userns` setting. A build outside
`/opt/pressroom/releases` needs a separate policy for its own browser path.

Run `--check` as the service identity. A sandbox/library failure stops startup.
`Render:ChromiumSandbox=false` is rejected by the server. CI relaxes Ubuntu's
AppArmor userns restriction only on its disposable runner and uses a non-root
container; that is not a production host policy recipe.

## Install and run

Verify the `.tar.gz.sha256`, then extract the complete archive preserving modes
into `/opt/pressroom/releases/<version>`. Keep the release read-only to the service.

```sh
sudo useradd --system --home /var/lib/pressroom --shell /usr/sbin/nologin pressroom
sudo install -d -o pressroom -g pressroom -m 700 /var/lib/pressroom
sudo install -d -o root -g pressroom -m 750 /etc/pressroom
```

Create `/etc/pressroom/pressroom.settings.json`, readable only by root and the
service group, with API keys as in [Configuration](configuration.md). The default
state root is `/var/lib/pressroom`; it holds `temp`, `logs`, and opt-in `jobs`.
Subdirectories are created if writable; permission failures are fatal. Use
literal, case-correct absolute paths. Point `current` at the complete release.

```sh
sudo -u pressroom /opt/pressroom/current/Pressroom.Server --check \
  --config /etc/pressroom/pressroom.settings.json
```

Example systemd unit:

```ini
[Unit]
Description=Pressroom HTML-to-PDF service
After=network.target

[Service]
Type=simple
User=pressroom
Group=pressroom
WorkingDirectory=/opt/pressroom/current
ExecStart=/opt/pressroom/current/Pressroom.Server --config /etc/pressroom/pressroom.settings.json --urls http://127.0.0.1:8080
Restart=on-failure
TimeoutStopSec=45
KillMode=control-group
UMask=0077
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/pressroom

[Install]
WantedBy=multi-user.target
```

Use an HTTPS reverse proxy with adequate body limits/timeouts. Avoid systemd
restrictions that prohibit user namespaces. `KillMode=control-group` is the final
boundary on unexpected server death. Normal/failed shutdown tracks descendants,
sends SIGTERM then SIGKILL, and uses pidfds to avoid signalling reused PIDs.

## Updates, storage, and builds

Stop, extract into a new versioned directory, check, update `current`, and start.
Roll back by stopping and restoring the previous complete release. Preserve
settings and state. With jobs enabled, provision MaxStoredBytes plus one result
commit, logs and temporary files. A store lock rejects a second instance using
the same root. Drain/back up jobs before rollback to a version without jobs.

Build as non-root with the .NET 10 SDK, PowerShell 7, and libraries above:

```sh
pwsh ./build/Publish-Linux.ps1 -Version 0.1.0 -OutputDirectory ./artifacts/linux-publish
pwsh ./build/Package-Pressroom.ps1 -PublishDirectory ./artifacts/linux-publish \
  -OutputDirectory ./artifacts/linux-release -Version 0.1.0 \
  -Variant framework-dependent -RuntimeIdentifier linux-x64
```

The shared helper installs the pinned browser, stages the payload, performs a
real sandboxed check, removes dependency-validation cache markers, and writes
the manifest. It does not install machine libraries.
