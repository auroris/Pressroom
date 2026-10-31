#requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$ConfigurationDirectory,
    [string]$Image = 'pressroom:windows-local',
    [string]$Name = 'pressroom',
    [ValidateRange(1,65535)][int]$Port = 8080,
    [switch]$BlockOutbound
)
$ErrorActionPreference = 'Stop'
$ConfigurationDirectory = (Resolve-Path -LiteralPath $ConfigurationDirectory).Path
$config = Get-Content -LiteralPath (Join-Path $ConfigurationDirectory pressroom.settings.json) -Raw | ConvertFrom-Json
if ($config.Paths.StateRoot -ine 'C:\state') { throw 'Set Paths:StateRoot to C:\state for the Windows container.' }
if (($config.Printing.Printers -and @($config.Printing.Printers.PSObject.Properties).Count -gt 0) -or $config.Printing.SumatraPdfPath) {
    throw 'Containers are PDF-only. Leave Printing:Printers empty and Printing:SumatraPdfPath unset.'
}
$networkArguments = @('--network','nat')
if ($BlockOutbound) {
    # Explicitly optional host policy; this branch needs administrator rights.
    & (Join-Path $PSScriptRoot Install-WindowsContainerPolicy.ps1)
    $networkArguments = @('--network','pressroom-egress-blocked','--dns','127.0.0.1')
}
$docker = (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (!$docker) { $docker = 'C:\Program Files\Docker\docker.exe' }
# --rm destroys the entire writable layer when the process stops. Start a new
# container with this helper after a crash or host restart; never retain its state.
& $docker run --detach --rm --name $Name @networkArguments `
    --stop-timeout 45 --publish "${Port}:8080" `
    --mount "type=bind,source=$ConfigurationDirectory,target=C:\config,readonly" $Image
if ($LASTEXITCODE -ne 0) { throw 'Windows container startup failed.' }
