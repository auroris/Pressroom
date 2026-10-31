#requires -Version 5.1
<#
Invoked automatically by Pressroom.Publish.targets. This is build support, not a
server installation or deployment script. It never installs machine components.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Verify')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$BundleDirectory,
    [string]$NuGetRoot,
    [string]$SelfContained = 'false'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$onWindows = $env:OS -eq 'Windows_NT'
$runtimeIdentifier = if ($onWindows) { 'win-x64' } else { 'linux-x64' }
$pathComparison = if ($onWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (![Environment]::Is64BitProcess) {
    throw 'Pressroom release publishing requires a 64-bit build process.'
}

function Get-FullDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) {
        throw 'A publish working directory must not be a drive root.'
    }
    return $full
}

function Write-Utf8Json([string]$Path, $Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 20
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Remove-ValidationMarkers([string]$Directory) {
    if (Test-Path -LiteralPath $Directory) {
        # Never remove INSTALLATION_COMPLETE or any installer-selected companion.
        Get-ChildItem -LiteralPath $Directory -Recurse -Force -File -Filter DEPENDENCIES_VALIDATED |
            Remove-Item -Force
    }
}

function Get-RelativeFile([string]$Root, [string]$Path) {
    $prefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$Path.StartsWith($prefix, $pathComparison)) {
        throw 'A publish input resolved outside its working directory.'
    }
    return $Path.Substring($prefix.Length).Replace('\', '/')
}

function Require-File([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required publish asset is missing: $Path"
    }
}

function Get-Sha256Hash([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $algorithm.Dispose()
    }
}

$BuildDirectory = Get-FullDirectory $BuildDirectory
$BundleDirectory = Get-FullDirectory $BundleDirectory
$nodeRelative = if ($onWindows) { '.playwright/node/win32_x64/node.exe' } else { '.playwright/node/linux-x64/node' }
$shellName = if ($onWindows) { 'chrome-headless-shell.exe' } else { 'chrome-headless-shell' }
$serverName = if ($onWindows) { 'Pressroom.Server.exe' } else { 'Pressroom.Server' }
$node = Join-Path $BuildDirectory $nodeRelative
$cli = Join-Path $BuildDirectory '.playwright/package/cli.js'
$driverMetadata = Join-Path $BuildDirectory '.playwright/package/package.json'
$depsPath = Join-Path $BuildDirectory 'Pressroom.Server.deps.json'
Require-File $node
Require-File $cli
Require-File $driverMetadata
Require-File $depsPath
$driver = Get-Content -LiteralPath $driverMetadata -Raw | ConvertFrom-Json
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$playwrightLibrary = @($deps.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.Playwright/*' })
if ($playwrightLibrary.Count -ne 1) { throw 'Cannot establish the exact Microsoft.Playwright package version.' }
$playwrightVersion = $playwrightLibrary[0].Name.Split('/')[1]
if ($driver.version -ne $playwrightVersion) {
    throw 'The private driver version does not match the pinned Microsoft.Playwright package.'
}

if ($Mode -eq 'Prepare') {
    [IO.Directory]::CreateDirectory($BundleDirectory) | Out-Null
    $oldManifest = Join-Path $BundleDirectory 'release-manifest.json'
    if (Test-Path -LiteralPath $oldManifest) { Remove-Item -LiteralPath $oldManifest -Force }
    $browserDirectory = Join-Path $BundleDirectory 'browsers'
    [IO.Directory]::CreateDirectory($browserDirectory) | Out-Null
    $previousBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
    try {
        $env:PLAYWRIGHT_BROWSERS_PATH = $browserDirectory
        & $node $cli install chromium --only-shell
        if ($LASTEXITCODE -ne 0) { throw 'The matching Chromium headless-shell download failed.' }
    }
    finally {
        $env:PLAYWRIGHT_BROWSERS_PATH = $previousBrowserPath
    }
    $headless = @(Get-ChildItem -LiteralPath $browserDirectory -Recurse -File -Filter $shellName)
    if ($headless.Count -ne 1) { throw 'The bundled browser tree must contain exactly one matching Chromium headless shell.' }
    Remove-ValidationMarkers $browserDirectory

    # Preserve the notices shipped by each managed package in addition to the
    # browser, private Node/driver, and font notices already copied with their files.
    if ([string]::IsNullOrWhiteSpace($NuGetRoot)) { $NuGetRoot = $env:NUGET_PACKAGES }
    if ([string]::IsNullOrWhiteSpace($NuGetRoot)) {
        $NuGetRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    }
    $NuGetRoot = [IO.Path]::GetFullPath($NuGetRoot)
    $noticeRoot = Join-Path $BundleDirectory 'notices'
    if (Test-Path -LiteralPath $noticeRoot) { Remove-Item -LiteralPath $noticeRoot -Recurse -Force }
    [IO.Directory]::CreateDirectory($noticeRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../THIRD-PARTY-NOTICES.md') -Destination $noticeRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Destination (Join-Path $BundleDirectory 'README.md') -Force
    $releaseDocs = Join-Path $BundleDirectory 'docs'
    [IO.Directory]::CreateDirectory($releaseDocs) | Out-Null
    # Ship the user and integrator guides with the release; the design history stays in the repository.
    Copy-Item -Path (Join-Path $PSScriptRoot '../docs/*.md') -Destination $releaseDocs -Force
    $releaseImages = Join-Path $releaseDocs 'images'
    [IO.Directory]::CreateDirectory($releaseImages) | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot '../docs/images/*') -Destination $releaseImages -Force
    $releaseDeploy = Join-Path $BundleDirectory 'deploy'
    [IO.Directory]::CreateDirectory($releaseDeploy) | Out-Null
    foreach ($example in @('README.md', 'compose.yaml', 'kubernetes.yaml', 'pressroom-seccomp.json', 'check.settings.json', 'LICENSE.moby')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('../deploy/' + $example)) -Destination $releaseDeploy -Force
    }
    $inventory = @()
    foreach ($entry in $deps.libraries.PSObject.Properties | Sort-Object Name) {
        if ($entry.Value.type -ne 'package') { continue }
        $parts = $entry.Name.Split('/')
        $packagePath = Join-Path $NuGetRoot ($parts[0].ToLowerInvariant() + '/' + $parts[1].ToLowerInvariant())
        if (!(Test-Path -LiteralPath $packagePath -PathType Container)) {
            throw "The restored package directory is missing for $($entry.Name)."
        }
        $packageNoticeRoot = Join-Path $noticeRoot ('packages/' + $parts[0] + '/' + $parts[1])
        [IO.Directory]::CreateDirectory($packageNoticeRoot) | Out-Null
        $nuspec = @(Get-ChildItem -LiteralPath $packagePath -File -Filter '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "Package metadata is missing for $($entry.Name)." }
        [xml]$spec = Get-Content -LiteralPath $nuspec[0].FullName -Raw
        $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        $licenseType = $null
        $licenseValue = $null
        if ($null -ne $license) { $licenseType = $license.GetAttribute('type'); $licenseValue = $license.InnerText }
        elseif ($null -ne $licenseUrl) { $licenseType = 'url'; $licenseValue = $licenseUrl.InnerText }
        Copy-Item -LiteralPath $nuspec[0].FullName -Destination $packageNoticeRoot
        $noticeFiles = @(Get-ChildItem -LiteralPath $packagePath -Recurse -File |
            Where-Object { $_.Name -match '^(licen[cs]e|notice|third[-_. ]?party)' })
        if ($licenseType -eq 'file') {
            $declaredLicense = Join-Path $packagePath $licenseValue
            Require-File $declaredLicense
            $noticeFiles += Get-Item -LiteralPath $declaredLicense
        }
        foreach ($notice in $noticeFiles | Sort-Object FullName -Unique) {
            $relative = Get-RelativeFile $packagePath $notice.FullName
            $target = Join-Path $packageNoticeRoot $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $target -Force
        }
        $inventory += [ordered]@{ name = $parts[0]; version = $parts[1]; licenseType = $licenseType; license = $licenseValue }
    }
    Write-Utf8Json (Join-Path $noticeRoot 'packages.json') @($inventory)
    Write-Host "Pressroom: packaged Microsoft.Playwright $playwrightVersion and driver $($driver.version)."
    exit 0
}

# A warm-up failure is a publish failure. Use an isolated settings/state directory,
# and make the child process use only the payload being verified.
$executable = Join-Path $BuildDirectory $serverName
$browserDirectory = Join-Path $BuildDirectory 'browsers'
$fontManifest = Join-Path $BuildDirectory 'fonts/manifest.json'
Require-File $executable
Require-File $fontManifest
Require-File (Join-Path $BuildDirectory 'fonts/OFL.txt')
Require-File (Join-Path $BuildDirectory '.playwright/node/LICENSE')
Require-File (Join-Path $BuildDirectory '.playwright/package/LICENSE')
if (!(Test-Path -LiteralPath $browserDirectory -PathType Container)) { throw 'The staged browser payload is missing.' }
$checkRoot = Join-Path ([IO.Path]::GetTempPath()) ('Pressroom-publish-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($checkRoot) | Out-Null
$checkConfiguration = Join-Path $checkRoot 'pressroom.settings.json'
Write-Utf8Json $checkConfiguration ([ordered]@{
    Paths = [ordered]@{ StateRoot = Join-Path $checkRoot 'state' }
    Render = [ordered]@{
        BrowsersPath = $browserDirectory
        ChromiumPath = $null
        ChromiumSandbox = $true
    }
    Fonts = [ordered]@{ Manifest = $fontManifest }
    Auth = [ordered]@{ Mode = 'Anonymous'; ApiKeys = @{} }
})
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executable
$start.Arguments = '--check --config "' + $checkConfiguration + '"'
$start.WorkingDirectory = $BuildDirectory
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
# Application settings come only from JSON. Remove Playwright process controls
# that could bypass dependency validation or select a different private driver.
$start.EnvironmentVariables.Remove('PLAYWRIGHT_SKIP_VALIDATE_HOST_REQUIREMENTS')
$start.EnvironmentVariables.Remove('PLAYWRIGHT_NODEJS_PATH')
$start.EnvironmentVariables.Remove('PLAYWRIGHT_DRIVER_SEARCH_PATH')
$check = [Diagnostics.Process]::new()
$check.StartInfo = $start
try {
    if (!$check.Start()) { throw 'Could not start the Pressroom package check.' }
    $stdoutTask = $check.StandardOutput.ReadToEndAsync()
    $stderrTask = $check.StandardError.ReadToEndAsync()
    if (!$check.WaitForExit(180000)) {
        # This PID was created by this check and is still running. Terminate only
        # its process tree, never all node/chrome processes on the build machine.
        if ($onWindows) { & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $check.Id /T /F | Out-Null }
        else { $check.Kill($true) }
        throw 'The Pressroom package check did not finish within three minutes.'
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($check.ExitCode -ne 0) {
        Write-Host $stdout
        Write-Host $stderr
        throw "The staged Pressroom renderer failed its package check (exit $($check.ExitCode))."
    }
    $result = $null
    foreach ($line in ($stdout -split "`r?`n")) {
        try {
            $candidate = $line | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $candidate -and $candidate.PSObject.Properties['status'] -and
                $candidate.PSObject.Properties['chromiumVersion']) { $result = $candidate }
        }
        catch { }
    }
    if ($null -eq $result -or $result.status -ne 'ready') { throw 'The package check did not return a ready version result.' }
    Remove-ValidationMarkers $browserDirectory
    Remove-ValidationMarkers (Join-Path $BundleDirectory 'browsers')
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $BuildDirectory -Recurse -Force -File | Sort-Object FullName) {
        $relative = Get-RelativeFile $BuildDirectory $file.FullName
        # The Web SDK transforms web.config later for the selected transport/profile.
        if ($relative -eq 'web.config' -or $relative -eq 'release-manifest.json') { continue }
        $files += [ordered]@{ path = $relative; bytes = $file.Length; sha256 = Get-Sha256Hash $file.FullName }
    }
    $nodeVersion = & $node --version
    if ($LASTEXITCODE -ne 0) { throw 'The packaged Node runtime failed its version check.' }
    $fontInventory = Get-Content -LiteralPath $fontManifest -Raw | ConvertFrom-Json
    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'Pressroom'
        version = $result.version
        createdUtc = [DateTime]::UtcNow.ToString('o')
        runtimeIdentifier = $runtimeIdentifier
        selfContained = ($SelfContained -eq 'true')
        playwrightVersion = $playwrightVersion
        driverVersion = $driver.version
        nodeVersion = [string]$nodeVersion
        chromiumVersion = $result.chromiumVersion
        fonts = $fontInventory.fonts
        buildMachineCheck = [ordered]@{ status = 'passed'; operatingSystem = [Environment]::OSVersion.VersionString; chromiumSandbox = $true; dependencyValidation = 'enabled' }
        cleanIisAcceptance = 'Not established by a build-machine publish check; see docs/windows-acceptance.md.'
        excludedFromHashes = @('web.config', 'release-manifest.json')
        files = @($files)
    }
    Write-Utf8Json (Join-Path $BundleDirectory 'release-manifest.json') $manifest
    Write-Host "Pressroom: PDF warm-up passed with Chromium $($result.chromiumVersion); publish payload is complete."
}
finally {
    $check.Dispose()
    if (Test-Path -LiteralPath $checkRoot) { Remove-Item -LiteralPath $checkRoot -Recurse -Force }
}
