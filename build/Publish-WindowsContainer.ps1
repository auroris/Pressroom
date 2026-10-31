#requires -Version 7.0
param([string]$Version = '0.1.0', [string]$PlaywrightVersion = 'latest')
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsWindows()) { throw 'Run on Windows x64 with the .NET 10 SDK.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root artifacts/windows-container-publish
if (Test-Path $output) { throw 'Use a fresh artifacts/windows-container-publish directory.' }
$PlaywrightVersion = & (Join-Path $PSScriptRoot Resolve-PlaywrightVersion.ps1) -Version $PlaywrightVersion
Write-Host "Selected Microsoft.Playwright $PlaywrightVersion and its matching browser."
$properties = @("-p:Version=$Version", "-p:PressroomPlaywrightVersion=$PlaywrightVersion", '-p:PlaywrightPlatform=win-x64')
Push-Location $root
try {
    dotnet restore Pressroom.slnx --force-evaluate @properties
    if ($LASTEXITCODE -ne 0) { throw 'Playwright dependency refresh failed.' }
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
