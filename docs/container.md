# Containers

For internet-facing use, the recommended deployment is **Linux Docker with the
iptables isolation below**, API-key authentication, and an HTTPS reverse proxy
in front of the loopback-bound API. A [direct Linux installation](linux.md) under
a dedicated unprivileged user is also suitable for internet-facing use.
Windows/IIS and Windows containers are intended primarily for intranets where
Pressroom receives no internet traffic. Both Linux options also serve intranets.
Firewall configuration is optional in general, but is part of the recommended
internet-facing Docker setup.

The Linux image runs on Microsoft's current .NET 10 ASP.NET Core Linux base as
UID/GID 10001. Builds pull the rolling `10.0` tags each time; the verified base
currently uses Ubuntu 24.04. The SDK stays in the builder. Chromium sandboxing and dependency
validation remain enabled. See [the VM acceptance record](linux-acceptance.md).

## Runtime policy

- One application listener: TCP 8080, for rendering, jobs, and health over the API.
- Outbound isolation is optional. Basic Docker networking permits outgoing
  connections. The rules below block new connections to the host, peers and
  internet while preserving responses to API requests.
- The application filesystem is read-only. `/tmp` and `/var/lib/pressroom` are
  temporary memory-backed filesystems; there is no persistent application volume.
- `cap_drop: [ALL]` includes NET_ADMIN and NET_RAW; the application cannot alter
  network policy or open raw sockets.
- JSON configuration and API keys live outside the container and are mounted
  read-only. The Docker daemon is never mounted into the container.
- The Linux image does not include CUPS client tools or printer configuration.
  [Linux printing](printing.md) is intended for a direct host installation;
  Pressroom does not detect or block printing in containers.

Jobs and results disappear when this container stops, restarts, or is replaced.
Download results before replacing it; clients must resubmit lost work. The direct
Linux installation can use persistent job storage instead.

## Optional Linux outbound isolation

No host firewall changes are made by the image or ordinary Compose startup.
To enable the requested API-only network policy, create a dedicated bridge and
apply these rules **before** starting its containers. These commands use Docker
Engine's iptables firewall backend (including iptables-nft).

```sh
docker network create --driver bridge \
  --opt com.docker.network.bridge.name=pressroom0 \
  --opt com.docker.network.bridge.enable_icc=false pressroom-egress-blocked

sudo iptables -I DOCKER-USER 1 -i pressroom0 -m conntrack ! --ctstate ESTABLISHED,RELATED -j DROP
sudo iptables -I INPUT 1 -i pressroom0 -m conntrack ! --ctstate ESTABLISHED,RELATED -j DROP
sudo ip6tables -I FORWARD 1 -i pressroom0 -m conntrack ! --ctstate ESTABLISHED,RELATED -j DROP
sudo ip6tables -I INPUT 1 -i pressroom0 -m conntrack ! --ctstate ESTABLISHED,RELATED -j DROP
```

The INPUT rules cover host services; forwarded-traffic rules alone are insufficient.
The IPv6 rules also cover hosts where Docker has no IPv6 DOCKER-USER chain.
Rules match only `pressroom0`. Persist them using your host's firewall management
and apply them before container startup after reboot. Run the commands once;
replace `-I <chain> 1` with `-D <chain>` to remove these exact rules after stopping
the isolated containers. Do not flush unrelated rules. Use this network exclusively
for Pressroom and do not attach a second network.

An ordinary Docker `internal` network is insufficient here: it permits access to
host gateway services, and Docker 29 does not publish its ports. The supplied
policy keeps normal API port publishing and drops new outbound flows. DNS is
directed to container loopback so Docker's resolver cannot forward external queries.

The host must permit user namespaces and satisfy its AppArmor policy; see
[Linux](linux.md). The seccomp profile permits Chromium's namespace calls and its
`chroot` inside that namespace even when host capabilities are all dropped.
Never add `SYS_ADMIN` or disable Chromium sandboxing to bypass a startup failure.

## Run

Create `deploy/pressroom.settings.json` with API keys. Make it readable by UID or
GID 10001; for example use root ownership, group 10001, and mode 0640. Then:

```sh
PRESSROOM_VERSION=<version> docker compose -f deploy/compose.yaml up -d
```

For outbound blocking, first apply the optional rules above, then use:

```sh
PRESSROOM_VERSION=<version> docker compose -f deploy/compose.yaml -f deploy/compose.host-isolated.yaml up -d
```

Compose publishes only `127.0.0.1:8080`. Place an HTTPS reverse proxy on the host
for remote clients. The equivalent isolated single-container command is:

```sh
docker run -d --name pressroom --init -p 127.0.0.1:8080:8080 \
  --network pressroom-egress-blocked --dns 127.0.0.1 \
  --read-only --user 10001:10001 --cap-drop ALL \
  --security-opt no-new-privileges \
  --security-opt seccomp=deploy/pressroom-seccomp.json \
  --shm-size=1g --stop-timeout 45 \
  --tmpfs /tmp:rw,nosuid,noexec,size=256m \
  --tmpfs /var/lib/pressroom:rw,nosuid,noexec,size=512m,uid=10001,gid=10001,mode=0700 \
  --mount type=bind,source="$PWD/deploy/pressroom.settings.json",target=/etc/pressroom/pressroom.settings.json,readonly \
  ghcr.io/auroris/pressroom:<version>
```

Keep `init: true` (Compose) or `--init` (`docker run`). The image's entry point is
`dotnet` itself, and a process running as PID 1 inherits every orphaned descendant
in the container. If a Chromium helper outlives its parent, .NET does not reap it
and it would remain a zombie holding a process slot. Docker's minimal init runs as
PID 1 instead: it reaps those orphans and forwards SIGTERM to the service, so
graceful shutdown is unchanged. The build and test helpers run the image the same
way. Health probes use `/v1/health`;
a full jobs queue is not unhealthy. Keep the stop timeout above ShutdownTimeoutMs
plus cleanup margin. Console logs are available through Docker; files inside the
state filesystem are temporary. Application settings are JSON-only;
ASPNETCORE_URLS is a hosting setting.

Compose limits the container to 1 CPU and 1.5 GiB RAM. Temporary files consume that
memory budget too. If jobs are enabled, set `Jobs:MaxStoredBytes` below the 512 MiB
state limit, leaving room for logs, browser temporary files, and a result commit;
256 MiB is a reasonable starting budget. Measure actual documents before resizing.

## Kubernetes

The [Kubernetes example](../deploy/kubernetes.yaml) uses a settings Secret,
memory-backed temporary storage, one replica, and a NetworkPolicy allowing only
TCP 8080 ingress with no egress. The CNI must enforce NetworkPolicy. Install the
seccomp profile under each node's kubelet. `emptyDir` lasts for the Pod lifetime;
replacing a Pod removes its state. This manifest has not been exercised on a
Kubernetes cluster by the VM tests. Kubernetes has no `init: true` equivalent, so
by default the service is PID 1 in its Pod. Set `shareProcessNamespace: true` (the
pause container then reaps orphans) or use a derived image whose entry point is a
tiny init such as `tini` if orphaned Chromium processes must be reaped.

## Build and verify

On a prepared Linux Docker host with PowerShell 7:

```sh
pwsh ./build/Build-Container.ps1 -Version 0.1.0 -Image pressroom:local
```

Use a fresh `artifacts/container-publish` directory. The builder performs locked
restore, tests, and a complete publish as UID 10001 with the seccomp profile. By
default it uses the repository's pinned `Microsoft.Playwright` (the
`PressroomPlaywrightVersion` property) and the checked-in lock files unchanged, so
the payload matches the Windows package and can be reproduced from the commit.
`-PlaywrightVersion latest` is an explicit opt-in that resolves the newest stable
release, and `-PlaywrightVersion <version>` selects or reproduces a recorded one.
Either refreshes the transitive lock files together in the disposable builder
before bundling the matching driver and Chromium. `build/Build-WindowsContainer.ps1`
accepts the same values. On Windows that refresh rewrites the lock files in the
checkout, so do not commit it unless you are deliberately upgrading the pin. The
runtime is built from that verified payload. This two-step build is intentional:
Docker build RUN steps cannot use the runtime's seccomp profile. The builder
needs network access to acquire dependencies; the running service does not.

The final image passes an offline sandboxed check with networking disabled and
`build/Test-Container.ps1`: API readiness, authenticated bilingual output,
invalid-key rejection, no printers, active-job SIGTERM, and loss of temporary
state on restart. Add `-TestNetworkIsolation` to the build or test helper after
preparing the optional rules to test outbound blocking with a reachable host canary.
The build helpers do not install Linux firewall rules. CI explicitly prepares
its disposable host and runs that additional check.

Published artifacts use GitHub Container Registry (`ghcr.io`); a Docker Hub
account is unnecessary. Builds and publishing are manually initiated in GitHub
Actions. Open **Actions → Container images → Run workflow**, enter the application
version, and choose whether to publish and update `latest`. There are no push,
pull-request, tag, or scheduled triggers. Uncheck `publish` for verification only.
GitHub's built-in token publishes to GHCR; no Docker Hub login or registry secret
is required. A newly created GHCR package may need its visibility set to public
in GitHub's package settings for anonymous pulls.

Linux tags are `<version>-linux`, `<version>`, `latest-linux`, and `latest`.
Windows tags are `<version>-windows-ltsc2022` and `latest-windows-ltsc2022`.
Prereleases never update the latest tags. Each run of this workflow deliberately
resolves the latest stable Playwright once and passes that version to both
platform builds, so images track current Chromium; the Packages workflow instead
builds installers from the pinned version. Each run also pulls current compatible
bases and retains verification/provenance
artifacts. Linux publication also requires the SBOM and critical-CVE gate.

## Windows containers

Windows containers are intended primarily for intranet deployments. Use the
Linux container with iptables isolation for the recommended internet-facing setup.

The Windows image uses the latest serviced Windows Server LTSC 2022 base and
the .NET 10 runtime. This matches the tested Windows Server 2022 host. Server Core
failed the sandboxed browser check; the full Windows Server image passed. Windows
base versions must match a supported host; an arbitrary newest Windows tag is
not interchangeable with LTSC 2022.

Build on a Windows Docker host with PowerShell 7 and the .NET 10 SDK:

```powershell
./build/Build-WindowsContainer.ps1 -Version 0.1.0 -Image pressroom:windows-local
```

Put `pressroom.settings.json` in a dedicated external directory, with
`Paths:StateRoot` set to `C:\state`. Leave printing settings empty. Start with:

```powershell
./build/Start-WindowsContainer.ps1 -ConfigurationDirectory C:\Pressroom\config `
  -Image ghcr.io/auroris/pressroom:0.1.0-windows-ltsc2022
```

This publishes only TCP 8080, mounts configuration read-only, runs as
ContainerUser, and uses `--rm` so stopping or crashing removes the complete
writable layer. Start a new container with the helper after a stop or host reboot.
Use stop followed by the start helper when discarding state: `docker restart`
retains the same Windows writable layer, even with `--rm`.
Windows Docker does not implement Linux capabilities or the Linux read-only/tmpfs
options; it uses the unprivileged Windows identity and a disposable writable layer.

Outbound blocking is optional on Windows too. Add `-BlockOutbound` to the start
helper to install/verify the dedicated HNS host and switch ACLs **before** startup.
Only that option requires an administrator session beyond normal Docker access.
The rules allow API responses, block new outgoing traffic, and close the Windows
base's other service ports on the container interface. They affect only the
dedicated network. `-TestNetworkIsolation` enables their acceptance check during
a Windows build. An in-container firewall helper was investigated, but the Windows
firewall services could not run inside the tested container; the shipped option
uses the host's supported HNS API.

## Upgrade and rollback

Pull an explicit tag/digest, finish and download required jobs, then recreate the
container with the same external configuration. Roll back by recreating it from
the previous image digest. Temporary jobs and results are not carried between
containers. Keep the host firewall installed throughout the change.
