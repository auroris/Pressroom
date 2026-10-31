#requires -Version 7.0
# -PlaywrightVersion defaults to the repository's pinned version and its lock files, so the payload is
# reproducible from the commit. Pass 'latest' (or a version) explicitly to float inside the builder.
param([string]$Version = '1.1.0', [string]$Image = 'pressroom:local', [string]$PlaywrightVersion = 'pinned', [switch]$TestNetworkIsolation)
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsLinux()) { throw 'Run on a Linux Docker host; Linux permissions are part of the check.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts/container-publish'
if (Test-Path $output) { throw 'Use a fresh artifacts/container-publish directory for each build.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:BUILDX_METADATA_PROVENANCE = 'max'
& docker buildx build --pull --load --target builder --metadata-file (Join-Path $root 'artifacts/container-builder.json') -t pressroom-builder:local $root
if ($LASTEXITCODE -ne 0) { throw 'Builder image failed.' }
# A new bind mount needs ownership matching the fixed unprivileged build identity.
& docker run --rm --user 0 --entrypoint chown --mount "type=bind,source=$output,target=/out" pressroom-builder:local 10001:10001 /out
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare output permissions.' }
$profile = Join-Path $root 'deploy/pressroom-seccomp.json'
& docker run --rm --security-opt "seccomp=$profile" --shm-size=1g --stop-timeout 45 `
    --mount "type=bind,source=$output,target=/out" pressroom-builder:local -Version $Version -PlaywrightVersion $PlaywrightVersion
if ($LASTEXITCODE -ne 0) { throw 'Linux publish and sandbox verification failed.' }
$manifest = Get-Content (Join-Path $output release-manifest.json) -Raw | ConvertFrom-Json
if ($manifest.buildMachineCheck.status -ne 'passed' -or !$manifest.buildMachineCheck.chromiumSandbox) { throw 'Verified manifest is required.' }
& docker buildx build --pull --load --target runtime --metadata-file (Join-Path $root 'artifacts/container-image.json') -t $Image $root
if ($LASTEXITCODE -ne 0) { throw 'Runtime image failed.' }
$checkConfig = Join-Path $root 'deploy/check.settings.json'
# --init matches the documented runtime: a reaping init is PID 1, not the service.
& docker run --rm --init --network none --read-only --cap-drop ALL --security-opt no-new-privileges `
    --tmpfs /var/lib/pressroom:rw,nosuid,noexec,size=512m,uid=10001,gid=10001,mode=0700 `
    --tmpfs /tmp:rw,nosuid,noexec,size=256m --shm-size=1g --stop-timeout 45 --security-opt "seccomp=$profile" `
    --mount "type=bind,source=$checkConfig,target=/etc/pressroom/pressroom.settings.json,readonly" $Image --check --config /etc/pressroom/pressroom.settings.json
if ($LASTEXITCODE -ne 0) { throw 'Final runtime image failed its sandboxed check.' }
& (Join-Path $PSScriptRoot Test-Container.ps1) -Image $Image -TestNetworkIsolation:$TestNetworkIsolation
$inspection = (& docker image inspect $Image | ConvertFrom-Json)[0]
if ($LASTEXITCODE -ne 0) { throw 'Image inspection failed.' }
[ordered]@{
    image = $Image; imageId = $inspection.Id; dockerReportedSizeBytes = $inspection.Size
    builtAtUtc = [DateTime]::UtcNow.ToString('o'); playwrightVersion = $manifest.playwrightVersion
    chromiumVersion = $manifest.chromiumVersion; sdkBase = 'mcr.microsoft.com/dotnet/sdk:10.0'
    runtimeBase = 'mcr.microsoft.com/dotnet/aspnet:10.0'
} | ConvertTo-Json | Set-Content (Join-Path $root 'artifacts/container-build.json')
