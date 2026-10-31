#requires -Version 7.0
# -PlaywrightVersion defaults to the repository's pinned version; pass 'latest' (or a version) explicitly to float.
param([string]$Version='0.1.0',[string]$Image='pressroom:windows-local',[string]$PlaywrightVersion='pinned',[switch]$UseExistingPublish,[switch]$TestNetworkIsolation)
$ErrorActionPreference='Stop'
if (![OperatingSystem]::IsWindows()) { throw 'Run on a Windows Docker host.' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$UseExistingPublish) { & (Join-Path $PSScriptRoot Publish-WindowsContainer.ps1) -Version $Version -PlaywrightVersion $PlaywrightVersion }
$manifest=Get-Content (Join-Path $root artifacts/windows-container-publish/release-manifest.json) -Raw | ConvertFrom-Json
if ($manifest.buildMachineCheck.status -ne 'passed' -or !$manifest.buildMachineCheck.chromiumSandbox -or $manifest.runtimeIdentifier -ne 'win-x64') { throw 'A verified Windows payload is required.' }
$docker=(Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (!$docker) { $docker='C:\Program Files\Docker\docker.exe' }
& $docker build --pull -f (Join-Path $root Dockerfile.windows) -t $Image $root
if ($LASTEXITCODE -ne 0) { throw 'Windows image build failed.' }
& (Join-Path $PSScriptRoot Test-WindowsContainer.ps1) -Image $Image -TestNetworkIsolation:$TestNetworkIsolation
$inspection=(& $docker image inspect $Image | ConvertFrom-Json)[0]
$bases=@('mcr.microsoft.com/windows/server:ltsc2022','mcr.microsoft.com/dotnet/aspnet:10.0-windowsservercore-ltsc2022') |
    ForEach-Object { $base=(& $docker image inspect $_ | ConvertFrom-Json)[0]; @{tag=$_;id=$base.Id;digests=$base.RepoDigests} }
@{image=$Image;imageId=$inspection.Id;osVersion=$inspection.OsVersion;dockerReportedSizeBytes=$inspection.Size;playwrightVersion=$manifest.playwrightVersion;chromiumVersion=$manifest.chromiumVersion;bases=$bases} |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root artifacts/windows-container-build.json)
