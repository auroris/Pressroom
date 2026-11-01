# Containers

The Linux container with the iptables isolation below is the recommended
internet-facing deployment, together with API-key authentication and an HTTPS
reverse proxy in front of the loopback-bound API. Windows containers are intended
primarily for internal networks. Firewall
configuration is optional in general, but is part of the recommended
internet-facing setup.

Images are published to GitHub Container Registry as `ghcr.io/auroris/pressroom`.
Linux tags are `<version>-linux`, `<version>`, `latest-linux`, and `latest`;
Windows tags are `<version>-windows-ltsc2022` and `latest-windows-ltsc2022`.
Prereleases never update the `latest` tags. Container images are built against
the newest stable Chromium at build time, whereas the Windows and Linux install
packages of the same version carry a pinned Chromium; check `/v1/info` for the
versions actually running. Pull by explicit tag or digest in production.

The Linux image runs on Microsoft's current .NET 10 ASP.NET Core Linux base as
UID/GID 10001. Chromium sandboxing and dependency validation remain enabled.

## Runtime policy

- One application listener: TCP 8080, for rendering, jobs, and health over the API.
- Outbound isolation is optional. Basic Docker networking permits outgoing
  connections. The rules below block new connections to the host, peers and
  internet while preserving responses to API requests.
- The application filesystem is read-only. `/tmp` and `/var/lib/pressroom` use
  temporary memory-backed storage for the container's lifetime.
- `cap_drop: [ALL]` includes NET_ADMIN and NET_RAW; the application cannot alter
  network policy or open raw sockets.
- JSON configuration and API keys live outside the container and are mounted
  read-only.
- For [Linux printing](printing.md), use a direct host installation with CUPS
  client tools and configured queues. The supplied image omits those dependencies.

Jobs and results disappear when this container stops, restarts, or is replaced.
Download results before replacing it; clients must resubmit lost work. The direct
Linux installation can use persistent job storage instead.

## Optional Linux outbound isolation

To add outbound restrictions to ordinary Docker networking, create a dedicated
bridge and apply these rules before starting its containers. These commands use Docker Engine's
iptables firewall backend (including iptables-nft).

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

Verify the policy after applying it: from inside the running container, a
connection to a listener on the host gateway and to an internet address must both
fail while `/v1/health` on the published port still answers.

## Run

The `deploy` folder of each release holds the Compose files, the Kubernetes
example, the seccomp profile, and the Windows helpers used below. Create
`deploy/pressroom.settings.json` with API keys. Make it readable by UID or GID
10001; for example use root ownership, group 10001, and mode 0640. Then:

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

Keep `init: true` (Compose) or `--init` (`docker run`). Docker's minimal init runs
as PID 1, reaps orphaned Chromium helpers and forwards SIGTERM to the service.
This allows Pressroom's `dotnet` process to shut down gracefully and releases the
process slots held by orphaned helpers.

Health probes use `/v1/health`, which stays healthy when the jobs queue is full.
Keep the stop timeout above `Render:ShutdownTimeoutMs` plus a cleanup margin. Console logs are
available through Docker; files inside the state filesystem are temporary.
Application settings are JSON-only; `ASPNETCORE_URLS` is a hosting setting.

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
cluster. Kubernetes has no `init: true` equivalent, so by default the service is
PID 1 in its Pod. Set `shareProcessNamespace: true` (the pause container then
reaps orphans) or use a derived image whose entry point is a tiny init such as
`tini` if orphaned Chromium processes must be reaped.

## Windows containers

Windows containers are intended primarily for intranet deployments. Use the
Linux container with iptables isolation for the recommended internet-facing setup.

The Windows image uses the latest serviced Windows Server LTSC 2022 base and
the .NET 10 runtime, matching a Windows Server 2022 host. Windows base versions
must match a supported host; an arbitrary newest Windows tag is not interchangeable
with LTSC 2022. The full Windows Server base is required: Server Core does not pass
the sandboxed browser check.

Put `pressroom.settings.json` in a dedicated external directory, with
`Paths:StateRoot` set to `C:\state`. Leave printing settings empty. Start with the
helper from the release's `deploy` folder:

```powershell
.\deploy\Start-WindowsContainer.ps1 -ConfigurationDirectory C:\Pressroom\config `
  -Image ghcr.io/auroris/pressroom:<version>-windows-ltsc2022
```

This publishes only TCP 8080, mounts configuration read-only, runs as
ContainerUser, and uses `--rm` so stopping or crashing removes the complete
writable layer. Start a new container with the helper after a stop or host reboot.
Use stop followed by the start helper when discarding state: `docker restart`
retains the same Windows writable layer, even with `--rm`.
Windows Docker uses the unprivileged Windows identity and disposable writable
layer for this deployment's process permissions and state lifecycle.

Outbound blocking is optional on Windows too. Add `-BlockOutbound` to the start
helper to install and verify the dedicated HNS host and switch ACLs **before**
startup; it runs `deploy\Install-WindowsContainerPolicy.ps1`. Only that option
requires an administrator session beyond normal Docker access. The rules allow
API responses, block new outgoing traffic, and close the Windows base's other
service ports on the container interface. They affect only the dedicated network.

## Upgrade and rollback

Pull an explicit tag or digest, finish and download required jobs, then recreate
the container with the same external configuration. Roll back by recreating it
from the previous image digest. Recreating the container discards its temporary
jobs and results. Keep the host firewall installed throughout the change.
