#requires -Version 7.0
<#
Packages a verified publish directory for a GitHub release. Application settings
and secrets remain the deployment operator's responsibility.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('framework-dependent', 'self-contained')][string]$Variant
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Restrict the version before using it in an asset filename. Numeric prerelease
# identifiers follow SemVer's no-leading-zero rule.
$number = '(0|[1-9][0-9]*)'
$identifier = '(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$semver = "\A$number\.$number\.$number(?:-$identifier(?:\.$identifier)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z"
if ($Version -cnotmatch $semver) {
    throw 'Version must be a SemVer value without a v prefix, such as 1.2.3 or 1.2.3-rc.1.'
}

$publishRoot = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$comparison = if ([OperatingSystem]::IsWindows()) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$publishPrefix = $publishRoot + [IO.Path]::DirectorySeparatorChar
if ($outputRoot.Equals($publishRoot, $comparison) -or $outputRoot.StartsWith($publishPrefix, $comparison)) {
    throw 'OutputDirectory must be outside PublishDirectory.'
}
if (!(Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw 'PublishDirectory does not exist.'
}

$requiredFiles = @(
    'Pressroom.Server.exe', 'Pressroom.Server.dll', 'Pressroom.Server.deps.json',
    'Pressroom.Server.runtimeconfig.json', 'web.config', 'appsettings.json',
    'release-manifest.json', '.playwright/node/win32_x64/node.exe',
    '.playwright/package/cli.js', 'fonts/manifest.json'
)
foreach ($relative in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $publishRoot $relative) -PathType Leaf)) {
        throw "The publish payload is missing $relative."
    }
}
$headless = @(Get-ChildItem -LiteralPath (Join-Path $publishRoot 'browsers') -Recurse -Force -File -Filter 'chrome-headless-shell.exe')
if ($headless.Count -ne 1) {
    throw 'The publish payload must contain exactly one Chromium headless shell.'
}

$manifest = Get-Content -LiteralPath (Join-Path $publishRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$versionSuffix = if ($Version.Contains('+')) { '.' } else { '+' }
if ($manifest.version -cne $Version -and !([string]$manifest.version).StartsWith($Version + $versionSuffix, [StringComparison]::Ordinal)) {
    throw 'The release manifest version does not match the requested package version.'
}
if ($manifest.schemaVersion -ne 1 -or $manifest.product -cne 'Pressroom' -or $manifest.runtimeIdentifier -cne 'win-x64' -or
    $manifest.selfContained -isnot [bool] -or $manifest.selfContained -ne ($Variant -eq 'self-contained') -or
    $manifest.buildMachineCheck.status -cne 'passed' -or
    $manifest.buildMachineCheck.chromiumSandbox -ne $true -or
    $manifest.buildMachineCheck.dependencyValidation -cne 'enabled') {
    throw 'The release manifest does not describe a verified Windows payload for this variant.'
}

$sourceItems = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -Force)
if (@($sourceItems | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) {
    throw 'The publish payload must not contain symbolic links or junctions.'
}
$sourceFiles = @($sourceItems | Where-Object { !$_.PSIsContainer })
if (@($sourceFiles | Where-Object { $_.Name -eq 'pressroom.settings.json' }).Count -ne 0) {
    throw 'An external settings file must not be included in a release.'
}

$assetName = "Pressroom-$Version-win-x64-$Variant.zip"
$archivePath = Join-Path $outputRoot $assetName
$checksumPath = $archivePath + '.sha256'
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) {
    throw 'Release assets already exist; use a fresh output directory instead of overwriting them.'
}
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

# Compress-Archive can omit hidden files. The .NET API preserves the entire
# publish tree, including the private .playwright driver, at the archive root.
[IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (!$entries.TryAdd($entry.FullName, $entry.Length)) {
            throw "The archive contains a duplicate entry: $($entry.FullName)."
        }
    }
    if ($entries.Count -ne $sourceFiles.Count) {
        throw 'The archive does not contain the complete publish file list.'
    }
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
        if (!$entries.ContainsKey($relative) -or $entries[$relative] -ne $file.Length) {
            throw "The archive is missing or has truncated the published file: $relative."
        }
    }
}
finally {
    $archive.Dispose()
}

$algorithm = [Security.Cryptography.SHA256]::Create()
$stream = $null
try {
    $stream = [IO.File]::OpenRead($archivePath)
    $hash = [Convert]::ToHexString($algorithm.ComputeHash($stream)).ToLowerInvariant()
}
finally {
    if ($null -ne $stream) { $stream.Dispose() }
    $algorithm.Dispose()
}
[IO.File]::WriteAllText($checksumPath, "$hash  $assetName`n", [Text.UTF8Encoding]::new($false))
Write-Host "Packaged $assetName ($($sourceFiles.Count) files), including the private Playwright driver."
Write-Host "SHA-256: $hash"
