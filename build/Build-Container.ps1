#requires -Version 7.0
param([string]$Version = '0.1.0', [string]$Image = 'pressroom:local')
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsLinux()) { throw 'Run on a Linux Docker host; Linux permissions are part of the check.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts/container-publish'
if (Test-Path $output) { throw 'Use a fresh artifacts/container-publish directory for each build.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& docker buildx build --load --target builder -t pressroom-builder:local $root
if ($LASTEXITCODE -ne 0) { throw 'Builder image failed.' }
# A new bind mount needs ownership matching the fixed unprivileged build identity.
& docker run --rm --user 0 --entrypoint chown --mount "type=bind,source=$output,target=/out" pressroom-builder:local 10001:10001 /out
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare output permissions.' }
$profile = Join-Path $root 'deploy/pressroom-seccomp.json'
& docker run --rm --security-opt "seccomp=$profile" --shm-size=1g --stop-timeout 45 `
    --mount "type=bind,source=$output,target=/out" pressroom-builder:local -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Linux publish and sandbox verification failed.' }
$manifest = Get-Content (Join-Path $output release-manifest.json) -Raw | ConvertFrom-Json
if ($manifest.buildMachineCheck.status -ne 'passed' -or !$manifest.buildMachineCheck.chromiumSandbox) { throw 'Verified manifest is required.' }
& docker buildx build --load --target runtime -t $Image $root
if ($LASTEXITCODE -ne 0) { throw 'Runtime image failed.' }
$checkConfig = Join-Path $root 'deploy/check.settings.json'
& docker run --rm --read-only --tmpfs /tmp --shm-size=1g --stop-timeout 45 --security-opt "seccomp=$profile" `
    --mount "type=bind,source=$checkConfig,target=/etc/pressroom/pressroom.settings.json,readonly" $Image --check --config /etc/pressroom/pressroom.settings.json
if ($LASTEXITCODE -ne 0) { throw 'Final runtime image failed its sandboxed check.' }
& (Join-Path $PSScriptRoot Test-Container.ps1) -Image $Image
& docker image inspect $Image --format '{{.Size}}'
