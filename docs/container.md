# Containers

The image uses a pinned .NET 10 ASP.NET Core **Ubuntu 24.04** runtime. Microsoft's
.NET 10 images [moved from Debian to Ubuntu](https://learn.microsoft.com/dotnet/core/compatibility/containers/10.0/default-images-use-ubuntu);
this corrects the roadmap's Debian assumption. The SDK remains in the builder.

## Run

Choose a published version and create a JSON settings file with API keys:

```sh
docker run -d --name pressroom -p 127.0.0.1:8080:8080 \
  --read-only --user 10001:10001 --cap-drop ALL \
  --security-opt no-new-privileges \
  --security-opt seccomp=deploy/pressroom-seccomp.json \
  --shm-size=1g --tmpfs /tmp --stop-timeout 45 \
  --mount type=volume,source=pressroom-state,target=/var/lib/pressroom \
  --mount type=bind,source="$PWD/pressroom.settings.json",target=/etc/pressroom/pressroom.settings.json,readonly \
  ghcr.io/auroris/pressroom:<version>
```

UID 10001 must be able to read settings. Named volumes inherit the state-directory
ownership; provision bind mounts with UID/GID 10001 ownership. The service is PID 1
and receives SIGTERM directly. Set stop timeout above ShutdownTimeoutMs plus
cleanup margin. Health probes use `/v1/health`; a full jobs queue is not unhealthy.

The host must permit user namespaces and satisfy its AppArmor policy; see
[Linux](linux.md). The seccomp profile permits the required namespace calls.
Never use privileged execution or SYS_ADMIN. Provide 1 GiB shared memory instead
of Docker's 64 MiB default. Application configuration is JSON-only;
ASPNETCORE_URLS is a hosting setting.

## Compose and Kubernetes

Use [Compose](../deploy/compose.yaml) with the profile/settings beside it and
PRESSROOM_VERSION selecting the tag. The [Kubernetes example](../deploy/kubernetes.yaml)
needs a settings Secret, persistent volume, and this seccomp profile installed
under each node's kubelet. It uses a memory-backed `/dev/shm`, read-only root, no
added capabilities, and one replica with Recreate for the local job store.

Start sizing at 1 CPU and 1.5 GiB RAM for concurrency 2, then measure actual
documents. Provision MaxStoredBytes plus one result commit, logs and temp space.
Console logs are primary; rolling files remain on the state volume. A firewall
or NetworkPolicy must provide any required network egress isolation.

## Build and release

On a Linux Docker host with PowerShell 7:

```sh
pwsh ./build/Build-Container.ps1 -Version 0.1.0 -Image pressroom:local
```

Start with a fresh artifacts/container-publish directory. The helper builds the
SDK stage, runs locked restore/tests/full publish as UID 10001 with the seccomp
profile, then builds the runtime from that verified payload. This two-step build
is intentional: Docker build RUN steps cannot use the runtime's seccomp profile.
The final read-only image passes another sandboxed check and
`build/Test-Container.ps1`: loopback HTTP readiness, authenticated bilingual PDF,
invalid key rejection, empty Linux printer list, and SIGTERM during an active job.
Plain docker build
requires the verified payload first.

The release workflow attaches a Linux tarball/checksum and SPDX SBOM, rejects
critical CVEs, and pushes the version tag after these checks and the Windows
release succeed. Stable releases update latest; drafts do not push. Notes record
image/base digests and uncompressed size. Digests and the Playwright 1.64.1 library
list are pinned in the [Dockerfile](../Dockerfile).

## Acceptance and upgrades

Verify health within 10 seconds, a two-page bilingual render, bad-key 401, and a
successful check under the documented restrictions. Stop during active renders
and verify exit within 45 seconds. Record compressed size (target below 600 MB),
scan results, peak memory and digest. These checks remain pending until executed;
see [verification](development/verification.md).

Upgrade by pulling an explicit tag and recreating the container with the same
settings/state volume. Roll back to the previous tag/digest. Drain/back up jobs
before rollback across a storage-schema change.
