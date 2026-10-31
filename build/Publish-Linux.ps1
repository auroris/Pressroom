#requires -Version 7.0
param([string]$Version = '0.1.0', [string]$OutputDirectory = '/out')
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsLinux()) { throw 'Run this helper on Linux x64.' }
dotnet restore Pressroom.slnx --locked-mode "-p:Version=$Version" -p:PlaywrightPlatform=linux-x64
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
dotnet build Pressroom.slnx -c Release --no-restore "-p:Version=$Version" -p:ContinuousIntegrationBuild=true -p:PlaywrightPlatform=linux-x64
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The net8.0 suite also runs in the dedicated package job.
dotnet test --solution Pressroom.slnx -f net10.0 -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
dotnet publish src/Pressroom.Server -r linux-x64 --self-contained false -c Release --no-restore `
    "-p:Version=$Version" -p:ContinuousIntegrationBuild=true -p:PlaywrightPlatform=linux-x64 -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Sandboxed Linux publish check failed.' }
$env:PRESSROOM_RUN_BROWSER_TESTS = '1'
$env:PRESSROOM_TEST_SANDBOX = 'true'
$env:PRESSROOM_TEST_BROWSERS_PATH = Join-Path $OutputDirectory browsers
dotnet test --project tests/Pressroom.Rendering.Tests -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Sandboxed browser tests failed.' }
dotnet test --project tests/Pressroom.Server.Tests -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Real-browser jobs tests failed.' }
