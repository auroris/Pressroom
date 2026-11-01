# Linux deployment

Direct Linux is suitable for internet-facing or intranet use when Pressroom runs
under a dedicated unprivileged user. Use API-key authentication and put an HTTPS
reverse proxy in front of the loopback-bound service. Configure host network
policy separately when the deployment needs outbound restrictions. The
[Linux container](container.md) documents a tested recipe.

Pressroom supports Linux x64 with the .NET 10 ASP.NET Core runtime and Chromium's
sandbox enabled. Optional [printing](printing.md) uses CUPS `lp` or `lpr` and an
explicit printer allowlist. Record target-host qualification with the
[acceptance checklist](#acceptance-checklist) at the end of this guide.

## Prerequisites

Use Ubuntu 24.04 LTS or Debian 13, x64, a kernel with pidfd support (5.4 or later),
and an unprivileged service identity. Install
the .NET 10 ASP.NET Core runtime and Chromium's shared libraries. On Ubuntu 24.04:

```sh
sudo apt-get install libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 \
  libatspi2.0-0t64 libcairo2 libcups2t64 libdbus-1-3 libdrm2 libgbm1 \
  libglib2.0-0t64 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 \
  libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 \
  fontconfig fonts-noto-core
```

This is the Chromium dependency list published by the bundled Playwright release.
Debian 13 uses the same package names. Keep fontconfig and Noto: headers and
footers use system fallback fonts; main documents use the packaged faces. The
service validates the installed dependencies at startup.

## Sandbox

The headless shell uses unprivileged user namespaces for its sandbox.
Permit `user.max_user_namespaces` greater than zero and, where present,
`kernel.unprivileged_userns_clone=1`. Ubuntu's AppArmor policy must also permit
the installed Chromium executable to create user namespaces. Install an
executable-specific AppArmor profile granting `userns` for the release's
headless-shell path. Containers need the supplied seccomp profile too. Never
disable Chromium's sandbox to work around host policy.

For the versioned installation layout below, this `/etc/apparmor.d/pressroom-browser`
profile grants user namespaces to that executable path while retaining Ubuntu's
global `kernel.apparmor_restrict_unprivileged_userns` setting:

```text
abi <abi/4.0>,
include <tunables/global>
profile pressroom-release /opt/pressroom/releases/**/chrome-headless-shell flags=(unconfined) {
  userns,
}
```

Load it with `sudo apparmor_parser -r /etc/apparmor.d/pressroom-browser`.
The release tree must remain root-owned and unwritable by the service. An
installation outside `/opt/pressroom/releases` needs a profile for its own browser
path.

Run `--check` as the service identity. A sandbox or library failure stops startup,
and `Render:ChromiumSandbox=false` is rejected by the server.

## Install and run

Download `Pressroom-<version>-linux-x64-framework-dependent.tar.gz` and its
`.sha256` file from the [GitHub releases page](https://github.com/auroris/Pressroom/releases).
Verify the checksum, then extract the complete archive preserving modes into
`/opt/pressroom/releases/<version>`. Keep the release read-only to the service.

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

Use an HTTPS reverse proxy with adequate body limits and timeouts. Avoid systemd
restrictions that prohibit user namespaces. `KillMode=control-group` is the final
boundary on unexpected server death. Normal and failed shutdown track descendants,
send SIGTERM then SIGKILL, and use pidfds to avoid signalling reused PIDs.

## Updates and storage

Stop, extract into a new versioned directory, check, update `current`, and start.
Roll back by stopping and restoring the previous complete release. Preserve
settings and state. With jobs enabled, provision `Jobs:MaxStoredBytes` plus one
result commit, logs, and temporary files. A store lock rejects a second instance
using the same root. Drain or back up jobs before rolling back to a version
without jobs.

## Acceptance checklist

Repeat these checks on each target host before go-live and after changing the
release, the kernel, or the sandbox policy. Record passed, failed, or not run for
each, with the release manifest and host inventory.

- **Package and sandbox.** `--check` passes as the service identity with the
  release read-only, the AppArmor profile loaded, and `Render:ChromiumSandbox`
  true. `/v1/health` returns 200 after startup and `/v1/info` reports the expected
  version, Chromium, and fonts.
- **Access.** Valid key succeeds; missing, invalid, empty, and duplicate keys
  return 401; anonymous mode, if used, renders without a key and rejects invalid
  keys.
- **Output.** `samples/orders-bilingual.request.json` renders two Letter pages
  with page numbers, French accents, bold and italic faces, and the inline graphic.
- **Limits and cancellation.** Oversized bodies return 413, saturation returns 429
  with `Retry-After` while health stays 200, and cancelled or timed-out requests
  release capacity.
- **Shutdown and recovery.** `systemctl stop` finishes within `TimeoutStopSec`
  with no live browser or driver processes left behind; the service becomes ready
  again after restart and after a browser crash.
- **Printing**, when configured: the CUPS helper submits as the service user, an
  unknown identifier returns 400, anonymous print requests return 401, and
  `temp/print-*` directories are removed after submission.
- **Jobs**, when enabled: restart resumes queued jobs and marks the interrupted
  one failed; another key cannot read or delete a job; expired results return 410.
- **Egress**, if required: outbound connections from the service identity are
  blocked by host policy, verified with a reachable control target.
