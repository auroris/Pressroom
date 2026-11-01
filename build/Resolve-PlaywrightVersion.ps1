#requires -Version 7.0
# Prints a concrete Microsoft.Playwright version. 'pinned' (the default) is the repository's
# PressroomPlaywrightVersion property.
# 'latest' asks NuGet for the newest stable release and must be requested explicitly.
param([string]$Version = 'pinned')
$ErrorActionPreference = 'Stop'
if (!$Version -or $Version -eq 'pinned') {
    $project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Pressroom.Rendering/Pressroom.Rendering.csproj') -Raw)
    $Version = $project.SelectSingleNode('/Project/PropertyGroup/PressroomPlaywrightVersion').InnerText
}
elseif ($Version -eq 'latest') {
    $curl = (Get-Command curl -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $response = & $curl --fail --silent --show-error --retry 2 --connect-timeout 10 --max-time 60 `
        https://api.nuget.org/v3-flatcontainer/microsoft.playwright/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the latest stable Microsoft.Playwright package.' }
    $Version = (($response -join "`n" | ConvertFrom-Json).versions |
        Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_ } -Descending | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Select pinned, latest, or a stable Playwright version.' }
$Version
