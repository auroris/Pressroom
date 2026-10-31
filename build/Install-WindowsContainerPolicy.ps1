#requires -Version 5.1
#requires -RunAsAdministrator
param([string]$Subnet = '172.30.77.0/24', [string]$Gateway = '172.30.77.1')
$ErrorActionPreference = 'Stop'
$docker = (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (!$docker) { $docker = 'C:\Program Files\Docker\docker.exe' }
$info = & $docker info --format '{{json .}}' | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $info.OSType -ne 'windows' -or $info.Name.Split('.')[0] -ine $env:COMPUTERNAME) {
    throw 'Use the local Windows Docker daemon from an administrator PowerShell session.'
}
$name = 'pressroom-egress-blocked'
$exists = & $docker network ls --filter "name=^$name`$" --format '{{.Name}}'
if (!$exists) {
    & $docker network create --driver nat --subnet $Subnet --gateway $Gateway `
        --opt "com.docker.network.windowsshim.networkname=$name" $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the dedicated NAT network. Select a non-overlapping subnet.' }
}
$network = (& $docker network inspect $name | ConvertFrom-Json)[0]
if ($LASTEXITCODE -ne 0 -or $network.Driver -ne 'nat') { throw 'The existing network is not a Windows NAT network.' }
if (!('Pressroom.ContainerNetwork' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
namespace Pressroom {
 public static class ContainerNetwork {
  [DllImport("computenetwork.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  public static extern int HcnOpenNetwork(ref Guid id, out IntPtr handle, out IntPtr error);
  [DllImport("computenetwork.dll", CharSet=CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  public static extern int HcnModifyNetwork(IntPtr handle, string settings, out IntPtr error);
  [DllImport("computenetwork.dll", CharSet=CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  public static extern int HcnQueryNetworkProperties(IntPtr handle, string query, out IntPtr properties, out IntPtr error);
  [DllImport("computenetwork.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  public static extern int HcnCloseNetwork(IntPtr handle);
  public static void Check(int hr, IntPtr error) {
   string message = error == IntPtr.Zero ? "" : Marshal.PtrToStringUni(error);
   if(error != IntPtr.Zero) Marshal.FreeCoTaskMem(error);
   if(hr < 0) throw new InvalidOperationException("HCN 0x" + hr.ToString("X8") + ": " + message);
  }
 }
}
'@
}
# Host ACLs are stateful. The explicit inbound host rule preserves API replies
# while the outbound rule blocks new connections. Host priority must be zero.
# Switch rules close the other Windows services' ports on the container NIC.
$policies = @(
    @{ Type='NetworkACL'; Settings=@{ Action='Allow'; Direction='In'; RuleType='Host'; Priority=0; Protocols='6'; LocalPorts='8080' } },
    @{ Type='NetworkACL'; Settings=@{ Action='Allow'; Direction='In'; RuleType='Switch'; Priority=1000; Protocols='6'; LocalPorts='8080' } },
    @{ Type='NetworkACL'; Settings=@{ Action='Block'; Direction='Out'; RuleType='Host'; Priority=0; Protocols='256' } },
    @{ Type='NetworkACL'; Settings=@{ Action='Block'; Direction='In'; RuleType='Switch'; Priority=2000; Protocols='256' } }
)
function Get-PolicyKey($Settings) {
    $rule = if ($Settings.RuleType) { $Settings.RuleType } else { 'Host' }
    @($Settings.Action,$Settings.Direction,$rule,[int]$Settings.Priority,$Settings.Protocols,
        $Settings.LocalPorts,$Settings.RemotePorts,$Settings.LocalAddresses,$Settings.RemoteAddresses) -join '|'
}
function Read-Network([IntPtr]$Handle) {
    $properties = [IntPtr]::Zero; $errorRecord = [IntPtr]::Zero
    [Pressroom.ContainerNetwork]::Check([Pressroom.ContainerNetwork]::HcnQueryNetworkProperties(
        $Handle,'{"SchemaVersion":{"Major":2,"Minor":0}}',[ref]$properties,[ref]$errorRecord),$errorRecord)
    try { [Runtime.InteropServices.Marshal]::PtrToStringUni($properties) | ConvertFrom-Json }
    finally { if ($properties -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::FreeCoTaskMem($properties) } }
}
$hns = @(Get-HnsNetwork | Where-Object Name -eq $name)
if ($hns.Count -ne 1) { throw 'Could not identify the dedicated HNS network.' }
$id = [Guid]$hns[0].ID; $handle = [IntPtr]::Zero; $errorRecord = [IntPtr]::Zero
[Pressroom.ContainerNetwork]::Check([Pressroom.ContainerNetwork]::HcnOpenNetwork([ref]$id,[ref]$handle,[ref]$errorRecord),$errorRecord)
try {
    $current = Read-Network $handle
    $acls = @($current.Policies | Where-Object Type -eq 'NetworkACL')
    if ($acls.Count -eq 0) {
        if (@($network.Containers.PSObject.Properties).Count -gt 0) { throw 'Stop containers on this network before installing its initial policy.' }
        $request = @{ ResourceType='Policy'; RequestType='Add'; Settings=@{ Policies=$policies } } | ConvertTo-Json -Depth 8 -Compress
        [Pressroom.ContainerNetwork]::Check([Pressroom.ContainerNetwork]::HcnModifyNetwork($handle,$request,[ref]$errorRecord),$errorRecord)
    }
    $actual = @((Read-Network $handle).Policies | Where-Object Type -eq 'NetworkACL')
    $expectedKeys = @($policies | ForEach-Object { Get-PolicyKey $_.Settings } | Sort-Object)
    $actualKeys = @($actual | ForEach-Object { Get-PolicyKey $_.Settings } | Sort-Object)
    if ($actual.Count -ne 4 -or ($actualKeys -join "`n") -cne ($expectedKeys -join "`n")) {
        throw 'Unexpected network ACLs. Stop its containers, remove only pressroom-egress-blocked, and reinstall the policy.'
    }
} finally { [Pressroom.ContainerNetwork]::HcnCloseNetwork($handle) | Out-Null }
Write-Host 'Windows API-only network policy verified before container startup.'
