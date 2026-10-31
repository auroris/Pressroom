#requires -Version 5.1
#requires -RunAsAdministrator
<#
Optional Windows developer setup. Suppress first-use firewall prompts by explicitly
blocking inbound traffic to one browser executable. Never grants network access.
This helper is not invoked by build, tests, or publish.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$BrowserExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathRooted($BrowserExecutable)) { throw 'Specify an absolute browser executable path.' }
# Publish staging is removed after verification; firewall rules can still name that exact path.
$browserPath = [IO.Path]::GetFullPath($BrowserExecutable)
if ([IO.Path]::GetFileName($browserPath) -cne 'chrome-headless-shell.exe') {
    throw 'Specify the complete path to the bundled chrome-headless-shell.exe.'
}
$algorithm = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($browserPath.ToLowerInvariant()))).Replace('-', '') }
finally { $algorithm.Dispose() }
$name = 'Pressroom-TestBrowser-Block-' + $hash.Substring(0, 24)
if ($PSCmdlet.ShouldProcess($browserPath, 'Block inbound traffic on all network profiles')) {
    if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) {
        Set-NetFirewallRule -Name $name -Enabled True -Direction Inbound -Action Block -Profile Any | Out-Null
    } else {
        New-NetFirewallRule -Name $name -DisplayName 'Pressroom test browser (block inbound)' `
            -Program $browserPath -Direction Inbound -Action Block -Profile Any -Enabled True | Out-Null
    }
    Write-Host "Inbound traffic blocked for $browserPath. Outbound policy and other applications are unchanged."
}
