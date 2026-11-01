#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Render')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Render')][uri]$ServerUrl,
    [Parameter(Mandatory, ParameterSetName = 'Compile')][switch]$BuildOnly,
    [string]$Feed = 'artifacts/nuget',
    [string]$Version = '1.1.0',
    [string]$Net8Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$feedPath = (Resolve-Path $Feed).Path
$smokeRoot = Join-Path $PSScriptRoot ('../artifacts/package-smoke/' + [Guid]::NewGuid().ToString('N'))
# The destination must be an anonymous local test server; no keys are placed on command lines.
if (!$BuildOnly -and !$ServerUrl.IsLoopback) { throw 'Use a loopback test server for this package smoke check.' }
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
$nugetConfig = Join-Path $smokeRoot NuGet.Config
$escapedFeed = [Security.SecurityElement]::Escape($feedPath)
@"
<configuration>
  <packageSources><clear /><add key="local" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Pressroom.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="Microsoft.*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content $nugetConfig
foreach ($framework in @('net8.0', 'net10.0')) {
    $directory = Join-Path $smokeRoot $framework
    dotnet new webapi --no-openapi --no-restore -f net10.0 -o $directory
    if ($LASTEXITCODE -ne 0) { throw 'Could not create consumer project.' }
    $project = (Get-ChildItem $directory -Filter '*.csproj').FullName
    $projectText = (Get-Content $project -Raw).Replace('net10.0', $framework)
    $projectText = $projectText.Replace('</PropertyGroup>', "<RestorePackagesPath>$smokeRoot/.packages</RestorePackagesPath></PropertyGroup>")
    $projectText | Set-Content $project
    dotnet add $project package Pressroom.Client --version $Version --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not install local client package.' }
    # Source mapping keeps Pressroom local while allowing missing SDK targeting packs.
    dotnet restore $project --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw 'Consumer package restore failed.' }
    @'
using Pressroom.Client;
using Pressroom.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<PressroomClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Pressroom:Url"]!);
    http.Timeout = TimeSpan.FromSeconds(65);
});
using var app = builder.Build();
var pressroom = app.Services.GetRequiredService<PressroomClient>();
var pdf = await pressroom.RenderAsync(new RenderRequest
{
    Html = "<!doctype html><html><body><h1>Package smoke — prêt</h1></body></html>",
    FileName = "smoke.pdf",
    Options = new RenderOptions { PageSize = PageSize.Named("A4"), Margins = new PageMargins { Top = "15mm", Bottom = "15mm" } }
});
if (pdf.PageCount != 1 || !pdf.Bytes.AsSpan().StartsWith("%PDF"u8)) throw new Exception("Invalid PDF.");
Console.WriteLine($"Consumer {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}: {pdf.PageCount} page, {pdf.Bytes.Length} bytes, {pdf.FileName}.");
'@ | Set-Content (Join-Path $directory Program.cs)
    dotnet build $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Consumer build failed.' }
    if ($BuildOnly) {
        Write-Host "Consumer $framework compiled against the locally packed client and contracts."
        continue
    }
    $dll = Join-Path $directory "bin/Release/$framework/$([IO.Path]::GetFileNameWithoutExtension($project)).dll"
    $runtime = if ($framework -eq 'net8.0') { $Net8Dotnet } else { 'dotnet' }
    & $runtime $dll --Pressroom:Url $ServerUrl.AbsoluteUri
    if ($LASTEXITCODE -ne 0) { throw 'Consumer PDF render failed.' }
}
