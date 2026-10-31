#requires -Version 7.0
# -PlaywrightVersion: 'pinned' (default) restores the checked-in lock files unchanged. 'latest' or an
# explicit different version refreshes the lock files in this checkout; do not commit that refresh
# unless the pinned version is being upgraded deliberately.
param([string]$Version = '1.1.0', [string]$PlaywrightVersion = 'pinned')
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsWindows()) { throw 'Run on Windows x64 with the .NET 10 SDK.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root artifacts/windows-container-publish
if (Test-Path $output) { throw 'Use a fresh artifacts/windows-container-publish directory.' }
$pinned = & (Join-Path $PSScriptRoot Resolve-PlaywrightVersion.ps1) -Version pinned
$PlaywrightVersion = & (Join-Path $PSScriptRoot Resolve-PlaywrightVersion.ps1) -Version $PlaywrightVersion
Write-Host "Selected Microsoft.Playwright $PlaywrightVersion and its matching browser."
$properties = @("-p:Version=$Version", '-p:PlaywrightPlatform=win-x64')
Push-Location $root
try {
    if ($PlaywrightVersion -ne $pinned) {
        $properties += "-p:PressroomPlaywrightVersion=$PlaywrightVersion"
        dotnet restore Pressroom.slnx --force-evaluate @properties
        if ($LASTEXITCODE -ne 0) { throw 'Playwright dependency refresh failed.' }
    }
    dotnet restore Pressroom.slnx --locked-mode @properties
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build Pressroom.slnx -c Release --no-restore @properties -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test --solution Pressroom.slnx -f net10.0 -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish src/Pressroom.Server -c Release --no-restore @properties -p:PublishProfile=IisFolder -p:ContinuousIntegrationBuild=true -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Sandboxed Windows publish check failed.' }
    $env:PRESSROOM_RUN_BROWSER_TESTS = '1'
    $env:PRESSROOM_TEST_SANDBOX = 'true'
    $env:PRESSROOM_TEST_BROWSERS_PATH = Join-Path $output browsers
    foreach ($project in @('Pressroom.Rendering.Tests','Pressroom.Server.Tests')) {
        dotnet test --project "tests/$project" -c Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Real-browser tests failed: $project" }
    }
} finally { Pop-Location }
